using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.DirectX.Direct3D11;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Copies a captured frame's surface down to a <see cref="System.Drawing.Bitmap"/>, synchronously,
/// through Direct3D 11 itself.
/// </summary>
/// <remarks>
/// This used to be one line — <c>SoftwareBitmap.CreateCopyFromSurfaceAsync(...).GetAwaiter()
/// .GetResult()</c> — and that line is what #116 turned out to be. It is an asynchronous WinRT
/// operation, and it was being waited on from inside <c>FrameArrived</c>, which the capture stack
/// raises on a thread of its own choosing. On Windows 11 that thread's apartment lets the completion
/// arrive elsewhere and the wait ends. On Windows 10 it does not: the completion is queued back to
/// the very thread that is blocked waiting for it, and the readback never returns.
///
/// From the outside that deadlock is indistinguishable from a capture that produces nothing. The
/// frame handler is serialised, so the first frame going in and never coming out means no second
/// frame is delivered either, and the counters read <c>received=1 read=0</c> — which is exactly what
/// the Windows 10 reports show, on the hardware adapter and on WARP alike, which is why neither
/// #117's adapter selection nor its software retry moved them.
///
/// So the async call is gone and the copy is done by hand: a staging texture, a
/// <c>CopySubresourceRegion</c>, a <c>Map</c>. Every step is synchronous, none of it touches the
/// WinRT threading model, and it cannot be deadlocked by the thread it is called on. It is also the
/// cheaper path — one GPU-to-CPU copy of just the content region into a staging texture that is
/// reused between frames, rather than a fresh <c>SoftwareBitmap</c> allocated per frame.
///
/// The interop is by vtable slot rather than by declaring these interfaces method for method.
/// D3D11's are large, and three of the four calls needed here sit late in them; a slot index that is
/// wrong is caught by the first call that uses it, while a mis-declared method thirty slots earlier
/// is not caught at all.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal sealed unsafe class WgcSurfaceReader : IDisposable
{
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // IDirect3DDxgiInterfaceAccess::GetInterface, the first method after IUnknown.
    private const int AccessGetInterface = 3;

    // ID3D11Device::CreateTexture2D.
    private const int DeviceCreateTexture2D = 5;

    // ID3D11Texture2D::GetDesc — after IUnknown, ID3D11DeviceChild and ID3D11Resource.
    private const int TextureGetDesc = 10;

    // ID3D11DeviceContext::Map / Unmap / CopySubresourceRegion.
    private const int ContextMap = 14;
    private const int ContextUnmap = 15;
    private const int ContextCopySubresourceRegion = 46;

    private const int UsageStaging = 3;
    private const uint CpuAccessRead = 0x20000;
    private const int MapRead = 1;

    private readonly IntPtr _device;
    private readonly IntPtr _context;

    // Kept between frames: every frame of a session is the same size until the window is resized,
    // and allocating a full-frame staging texture several times a second is the one cost this path does
    // not have to pay. Also what makes the whole read one lock: the staging texture is shared state.
    private readonly object _sync = new();
    private IntPtr _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private int _stagingFormat;
    private bool _disposed;

    /// <summary>
    /// Takes ownership of both pointers — the D3D11 device frames are delivered on, and its
    /// immediate context. Both are released by <see cref="Dispose"/>.
    /// </summary>
    public WgcSurfaceReader(IntPtr device, IntPtr context)
    {
        _device = device;
        _context = context;
    }

    /// <summary>
    /// Reads a captured surface back into a bitmap on the CPU, which is what recognition needs.
    /// </summary>
    /// <param name="width">
    /// How much of the surface is actually the content. A capture texture is allocated to the size
    /// the pool was created for and is routinely larger than the content in it — the extra columns
    /// and rows are whatever was last in that memory — so the content size travels with every frame
    /// and is what gets copied out.
    /// </param>
    public System.Drawing.Bitmap Read(IDirect3DSurface surface, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var source = GetTexture(surface);
        try
        {
            D3D11_TEXTURE2D_DESC desc;
            ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)
                Slot(source, TextureGetDesc))(source, &desc);

            var copyWidth = (uint)Math.Clamp(width, 1, (int)Math.Max(desc.Width, 1));
            var copyHeight = (uint)Math.Clamp(height, 1, (int)Math.Max(desc.Height, 1));

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureStaging(copyWidth, copyHeight, desc.Format);

                var box = new D3D11_BOX
                {
                    Left = 0,
                    Top = 0,
                    Front = 0,
                    Right = copyWidth,
                    Bottom = copyHeight,
                    Back = 1,
                };

                ((delegate* unmanaged[Stdcall]<
                        IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)
                    Slot(_context, ContextCopySubresourceRegion))(
                    _context, _staging, 0, 0, 0, 0, source, 0, &box);

                D3D11_MAPPED_SUBRESOURCE mapped;
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<
                            IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)
                        Slot(_context, ContextMap))(_context, _staging, 0, MapRead, 0, &mapped));

                try
                {
                    return CopyOut(mapped, (int)copyWidth, (int)copyHeight);
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                        Slot(_context, ContextUnmap))(_context, _staging, 0);
                }
            }
        }
        finally
        {
            Marshal.Release(source);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
            _staging = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero) Marshal.Release(_context);
        if (_device != IntPtr.Zero) Marshal.Release(_device);
    }

    private static System.Drawing.Bitmap CopyOut(
        D3D11_MAPPED_SUBRESOURCE mapped, int width, int height)
    {
        // B8G8R8A8 is what the frame pool is created for, and it is also the byte order a 32bpp GDI
        // bitmap uses, so this is a memcpy per row rather than a channel shuffle.
        var bitmap = new System.Drawing.Bitmap(
            width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var locked = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                // Row by row: the two strides agree in practice and must not be assumed to, and a
                // row is a memcpy either way.
                var rowBytes = width * 4;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        (byte*)mapped.Data + ((long)mapped.RowPitch * y),
                        (byte*)locked.Scan0 + ((long)locked.Stride * y),
                        rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The ID3D11Texture2D behind a projected capture surface, as a pointer the caller releases.
    /// </summary>
    private static IntPtr GetTexture(IDirect3DSurface surface)
    {
        // Not a plain cast. A projected WinRT object is not a classic COM callable wrapper, so the
        // D3D texture inside it has to be reached by going out to the ABI pointer and asking the
        // interop interface there for it.
        var inspectable = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        var access = IntPtr.Zero;
        try
        {
            var accessIid = IidDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, ref accessIid, out access));

            var textureIid = IidTexture2D;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                    Slot(access, AccessGetInterface))(access, &textureIid, &texture));

            return texture;
        }
        finally
        {
            if (access != IntPtr.Zero) Marshal.Release(access);
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
        }
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void EnsureStaging(uint width, uint height, int format)
    {
        if (_staging != IntPtr.Zero
            && _stagingWidth == width
            && _stagingHeight == height
            && _stagingFormat == format)
            return;

        if (_staging != IntPtr.Zero)
        {
            Marshal.Release(_staging);
            _staging = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = CpuAccessRead,
            MiscFlags = 0,
        };

        IntPtr staging;
        Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void*, IntPtr*, int>)
                Slot(_device, DeviceCreateTexture2D))(_device, &desc, null, &staging));

        _staging = staging;
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
    }

    /// <summary>The function at <paramref name="slot"/> of a COM pointer's vtable.</summary>
    private static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
