using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The COM plumbing between WPF and Windows.Graphics.Capture: the two things the projection cannot
/// give on its own — a capture item for an HWND, and a Direct3D device to receive frames on. Turning
/// the frames themselves into bitmaps is <see cref="WgcSurfaceReader"/>'s.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled in with a D3D wrapper library. What is needed here is narrow —
/// create a device, hold it, hand it to the frame pool, never draw anything — and the surface a
/// wrapper would add is much wider than that, on a project whose whole dependency list is five
/// packages.
///
/// Everything here is 1903 or older. The types are attributed accordingly so the callers are forced
/// to say out loud which Windows they need, because the application itself still runs on 1809.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class WgcInterop
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidDxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    private const string GraphicsCaptureItemClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";

    /// <summary>
    /// Whether this system can capture at all. Asked before anything else is built, because every
    /// call below is newer than the Windows this application supports.
    /// </summary>
    public static bool IsCaptureSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return false;

        try
        {
            // Not the same question as the build number: capture is also refused on systems where
            // the graphics stack cannot serve it, and this is the only way to hear that.
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception)
        {
            // A missing or broken projection reads as unsupported rather than as a crash on startup.
            return false;
        }
    }

    /// <summary>
    /// A capture item for one top-level window, or null when the window cannot be captured — it has
    /// closed, it is a window this process may not read, or the system refused.
    /// </summary>
    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var interopFactory = GetCaptureItemInterop();
        var iid = IidGraphicsCaptureItem;
        var abi = IntPtr.Zero;
        try
        {
            interopFactory.CreateForWindow(hwnd, ref iid, out abi);
            return abi == IntPtr.Zero ? null : FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
        }
    }

    /// <summary>A capture item for one monitor. Same rules as <see cref="CreateItemForWindow"/>.</summary>
    public static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmonitor)
    {
        var interopFactory = GetCaptureItemInterop();
        var iid = IidGraphicsCaptureItem;
        var abi = IntPtr.Zero;
        try
        {
            interopFactory.CreateForMonitor(hmonitor, ref iid, out abi);
            return abi == IntPtr.Zero ? null : FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
        }
    }

    /// <summary>
    /// A Direct3D device for the frame pool to hand frames back on, plus the raw D3D11 device and
    /// its immediate context behind it, which the caller must release when it is done — usually by
    /// handing both to a <see cref="WgcSurfaceReader"/>, which owns them from then on.
    /// </summary>
    /// <remarks>
    /// BGRA support is required, not optional: the frame pool is created for
    /// <c>B8G8R8A8UIntNormalized</c>, which is also the one format that maps onto a 32bpp GDI bitmap
    /// without a channel shuffle. The device is created without a debug layer and without a swap
    /// chain — nothing here presents anything.
    ///
    /// The immediate context used to be released here, on the grounds that frames arrive already
    /// copied into the pool's textures and nothing needed it. Reading one of those textures back is
    /// what needs it, and that is now done through D3D rather than through an async WinRT call —
    /// see <see cref="WgcSurfaceReader"/> for why that had to change.
    /// </remarks>
    /// <param name="preferredMonitor">
    /// The monitor the capture source is on, or <see cref="IntPtr.Zero"/> for no preference. Used to
    /// pick the adapter the device is created on.
    /// </param>
    /// <param name="forceWarp">
    /// Skip the hardware adapters entirely and render on the software one. The retry after a
    /// hardware device produced no frames at all.
    /// </param>
    /// <param name="description">Which adapter was used, for the log.</param>
    /// <remarks>
    /// The adapter is not a free choice, and getting it wrong is silent. Windows.Graphics.Capture
    /// delivers frames onto the device it is given, and on Windows 10 a device on an adapter other
    /// than the one composing that monitor is not an error — <c>StartCapture</c> succeeds, the item
    /// reports the right size, and <c>FrameArrived</c> then never fires. That is exactly the shape
    /// of the report this was written for: capture attached, no frame at all, in every display mode
    /// the game offered.
    ///
    /// So the adapter is chosen rather than defaulted. The one that owns the monitor the source is
    /// on is asked for by name, which on a hybrid machine — a laptop with two GPUs, a desktop with
    /// the integrated display output still enabled — is not necessarily adapter zero, which is what
    /// passing null gets.
    /// </remarks>
    public static IDirect3DDevice CreateDirect3DDevice(
        IntPtr preferredMonitor, bool forceWarp, out IntPtr rawDevice, out IntPtr immediateContext,
        out string description)
    {
        const int DriverTypeUnknown = 0;
        const int DriverTypeHardware = 1;
        const int DriverTypeWarp = 5;
        const uint FlagBgraSupport = 0x20;
        const uint SdkVersion = 7;

        var hr = unchecked((int)0x80004005);
        var device = IntPtr.Zero;
        var context = IntPtr.Zero;
        description = "none";

        // The adapter that drives the monitor being captured, when one can be named.
        var adapter = IntPtr.Zero;
        if (!forceWarp && preferredMonitor != IntPtr.Zero)
            adapter = FindAdapterForMonitor(preferredMonitor, out description);

        try
        {
            if (adapter != IntPtr.Zero)
            {
                // Driver type must be unknown when an adapter is named; anything else is E_INVALIDARG.
                hr = D3D11CreateDevice(
                    adapter, DriverTypeUnknown, IntPtr.Zero, FlagBgraSupport,
                    IntPtr.Zero, 0, SdkVersion, out device, out _, out context);

                if (hr < 0) description = $"{description} (refused a device: 0x{hr:X8})";
            }
        }
        finally
        {
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
        }

        if (hr < 0 && !forceWarp)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, FlagBgraSupport,
                IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
            if (hr >= 0) description = "default hardware adapter";
        }

        // A machine with no usable hardware device is rare but real — a remote session, a stripped
        // VM. WARP is slower and entirely good enough to read a window at a handful of frames a second.
        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, FlagBgraSupport,
                IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
            if (hr >= 0) description = "WARP (software) adapter";
        }

        if (hr < 0)
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.ThrowExceptionForHR(hr);
        }

        var dxgi = IntPtr.Zero;
        var abi = IntPtr.Zero;
        try
        {
            var dxgiIid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref dxgiIid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out abi));

            rawDevice = device;
            immediateContext = context;
            device = IntPtr.Zero;
            context = IntPtr.Zero;
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (context != IntPtr.Zero) Marshal.Release(context);
        }
    }

    /// <summary>
    /// The DXGI adapter driving <paramref name="monitor"/>, as a raw pointer the caller must
    /// release, or <see cref="IntPtr.Zero"/> when it cannot be named.
    /// </summary>
    /// <remarks>
    /// Asked by walking the adapters and their outputs, because an HMONITOR is the only handle both
    /// sides of this question speak: capture names its source by monitor or by window, and DXGI
    /// names its outputs by HMONITOR. Nothing here throws — an unnameable adapter means the caller
    /// falls back to the default one, which is what it used to do always.
    /// </remarks>
    private static IntPtr FindAdapterForMonitor(IntPtr monitor, out string description)
    {
        description = "no adapter matched the source monitor";

        var factoryPtr = IntPtr.Zero;
        try
        {
            var iid = IidDxgiFactory1;
            if (CreateDXGIFactory1(ref iid, out factoryPtr) < 0 || factoryPtr == IntPtr.Zero) return IntPtr.Zero;

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);

            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapterPtr) < 0 || adapterPtr == IntPtr.Zero)
                    return IntPtr.Zero;

                var keep = false;
                try
                {
                    var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                    if (!AdapterDrivesMonitor(adapter, monitor)) continue;

                    description = adapter.GetDesc1(out var desc) >= 0
                        ? $"adapter \"{desc.Description}\" (vendor={desc.VendorId:X4} device={desc.DeviceId:X4}) driving the source monitor"
                        : "the adapter driving the source monitor";

                    keep = true;
                    return adapterPtr;
                }
                finally
                {
                    if (!keep) Marshal.Release(adapterPtr);
                }
            }
        }
        catch (Exception)
        {
            // Choosing the adapter is an improvement on defaulting to adapter zero, never a
            // requirement: every failure here simply means the default is used.
            return IntPtr.Zero;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
        }
    }

    private static bool AdapterDrivesMonitor(IDXGIAdapter1 adapter, IntPtr monitor)
    {
        for (uint index = 0; ; index++)
        {
            if (adapter.EnumOutputs(index, out var outputPtr) < 0 || outputPtr == IntPtr.Zero) return false;

            try
            {
                var output = (IDXGIOutput)Marshal.GetObjectForIUnknown(outputPtr);
                if (output.GetDesc(out var desc) >= 0 && desc.Monitor == monitor) return true;
            }
            finally
            {
                Marshal.Release(outputPtr);
            }
        }
    }

    private static GraphicsCaptureItem FromAbi(IntPtr abi) =>
        WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);

    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        // RoGetActivationFactory by hand, because .NET dropped the built-in WinRT marshalling that
        // used to make this one line and the projection does not surface the interop factory.
        var classId = IntPtr.Zero;
        var factory = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(
                GraphicsCaptureItemClassId, GraphicsCaptureItemClassId.Length, out classId));

            var iid = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref iid, out factory));

            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        void CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    // ── DXGI, only far enough to name the adapter behind a monitor ───────────────────────────────
    //
    // Declared by hand and by slot. The methods that are never called are still declared, because a
    // COM interface is a vtable and leaving one out would shift every method after it onto the wrong
    // slot; their signatures are deliberately empty, which is safe precisely because they are never
    // called.

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint index, out IntPtr adapter);
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        [PreserveSig]
        int EnumOutputs(uint index, out IntPtr output);

        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport]
    [Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        [PreserveSig]
        int GetDesc(out DXGI_OUTPUT_DESC desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        [MarshalAs(UnmanagedType.Bool)]
        public bool AttachedToDesktop;

        public uint Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
