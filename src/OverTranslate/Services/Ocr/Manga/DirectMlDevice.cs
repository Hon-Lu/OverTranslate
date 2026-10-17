using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Which graphics adapter the manga models may run on, or why none; and loading the DirectML
/// runtime they run with.
/// </summary>
/// <remarks>
/// <para>ONNX Runtime's DirectML device id is the adapter's position in DXGI's
/// <c>EnumAdapters1</c> order, so that is the order asked here. Adapter 0 is the system's default
/// and is what is used when it is real hardware. Listing adapters needs dxgi.dll only, not
/// DirectML, so it is answered before anything is downloaded.</para>
///
/// <para>A software adapter — WARP, "Microsoft Basic Render Driver", the only adapter a VM or a
/// driverless machine has — is refused even though DirectML would accept it: it runs the models on
/// the CPU through a rasteriser and is slower than the column pipeline it would replace.</para>
///
/// <para>A hardware adapter without Direct3D 12 — a VM's display adapter, VMware SVGA 3D among
/// them — is found out just before loading (<see cref="Direct3D12Problem"/>), not here: asking
/// loads the graphics driver, 150ms on an RTX 3070 against 12 for the listing, and this is asked on
/// the UI thread by every model hint. Just before loading, the driver is being loaded anyway.</para>
///
/// <para>DirectML.dll is not shipped with the app; it is downloaded with the models
/// (<see cref="MangaModelStore.RuntimePath"/>). onnxruntime.dll imports it delay-loaded, by module
/// name, on the first DirectML session. So it is loaded here first, by full path, after checking
/// it is the file the manifest pins and carries Microsoft's signature: the delay-load then finds
/// that module already in the process and uses it. Never the copy in System32, which can be older
/// than the version onnxruntime was built against, nor one in the app folder. And never no copy at
/// all: a delay-load that finds nothing fails inside the loader's helper, where it cannot be caught,
/// so a session is not created unless <see cref="Load"/> has succeeded.</para>
/// </remarks>
internal static class DirectMlDevice
{
    // DXGI_ADAPTER_FLAG_SOFTWARE
    private const uint SoftwareFlag = 2;
    // Microsoft Basic Render Driver: the WARP adapter, whatever its flags say.
    private const uint MicrosoftVendor = 0x1414;
    private const uint BasicRenderDevice = 0x8C;
    private const string LibraryName = "DirectML.dll";

    private static readonly object LoadSync = new();
    // The runtime this process has loaded, once it has; a module cannot be swapped once loaded.
    private static string? _loaded;

    internal readonly record struct Adapter(
        int Index, string Name, uint VendorId, uint DeviceId, bool Software, ulong DedicatedMemory);

    /// <summary>The adapter to run on, or null with the reason there is none.</summary>
    internal static (Adapter? Adapter, string? Reason) Choose()
    {
        IReadOnlyList<Adapter> adapters;
        try
        {
            adapters = Enumerate();
        }
        catch (Exception ex)
        {
            return (null, $"DXGI adapters could not be listed: {ex.Message}");
        }

        return Choose(adapters);
    }

    /// <summary>The first hardware adapter in enumeration order.</summary>
    internal static (Adapter? Adapter, string? Reason) Choose(IReadOnlyList<Adapter> adapters)
    {
        if (adapters.Count == 0)
            return (null, "no graphics adapter");

        foreach (var adapter in adapters)
        {
            if (!IsSoftware(adapter))
                return (adapter, null);
        }

        return (null, $"only a software adapter ({adapters[0].Name})");
    }

    /// <summary>
    /// Loads the downloaded DirectML.dll at <paramref name="path"/> into the process, for the next
    /// DirectML session to use. Null once it is loaded (at once, if it already was); otherwise why
    /// it was not, and then no DirectML session may be created.
    /// </summary>
    /// <param name="file">What the manifest says the file is: its size and SHA-256.</param>
    internal static string? Load(string? path, MangaModelFile? file)
    {
        if (path is null || file is null)
            return "this build does not download DirectML";

        lock (LoadSync)
        {
            if (_loaded is not null)
                return string.Equals(_loaded, path, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"another DirectML.dll was loaded already ({_loaded})";

            if (Problem(path, file) is { } problem)
                return problem;

            // A DirectML.dll something else loaded first would be the one the delay-load binds to.
            if (GetModuleHandleW(LibraryName) is var existing && existing != IntPtr.Zero)
                return $"a DirectML.dll from {ModulePath(existing)} is already loaded";
            if (!NativeLibrary.TryLoad(path, out var handle))
                return $"{path} could not be loaded (error {Marshal.GetLastPInvokeError()})";
            if (GetModuleHandleW(LibraryName) != handle)
                return $"{path} was loaded, but a module by its name resolves elsewhere";

            _loaded = path;
            return null;
        }
    }

    /// <summary>Why the file at <paramref name="path"/> is not the runtime the manifest names, or null.</summary>
    internal static string? Problem(string path, MangaModelFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return "DirectML.dll has not been downloaded";
            if (info.Length != file.Size)
                return $"DirectML.dll is {info.Length} bytes, not {file.Size}";
            using (var stream = File.OpenRead(path))
                if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != file.Sha256.ToLowerInvariant())
                    return "DirectML.dll does not hash to what the manifest says";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"DirectML.dll could not be read: {ex.Message}";
        }

        return Authenticode.Problem(path, "Microsoft Corporation") is { } signature
            ? $"DirectML.dll: {signature}"
            : null;
    }

    private static string ModulePath(IntPtr module)
    {
        var buffer = new char[1024];
        var length = GetModuleFileNameW(module, buffer, buffer.Length);
        return length == 0 ? "an unknown place" : new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] fileName, int size);

    internal static bool IsSoftware(Adapter adapter) =>
        adapter.Software || (adapter.VendorId == MicrosoftVendor && adapter.DeviceId == BasicRenderDevice);

    private static unsafe IReadOnlyList<Adapter> Enumerate()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        var adapters = new List<Adapter>();
        try
        {
            // IDXGIFactory1::EnumAdapters1 is slot 12: IUnknown 3, IDXGIObject 4, IDXGIFactory 5.
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)
                (*(IntPtr**)factory)[12];
            for (uint index = 0; ; index++)
            {
                const int notFound = unchecked((int)0x887A0002);   // DXGI_ERROR_NOT_FOUND
                int hr = enumAdapters(factory, index, out var adapter);
                if (hr == notFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    // IDXGIAdapter1::GetDesc1 is slot 10: IUnknown 3, IDXGIObject 4, IDXGIAdapter 3.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out AdapterDescription, int>)
                        (*(IntPtr**)adapter)[10];
                    Marshal.ThrowExceptionForHR(getDesc1(adapter, out var description));
                    adapters.Add(new Adapter(
                        (int)index,
                        new string(description.Description, 0, 128).TrimEnd('\0'),
                        description.VendorId,
                        description.DeviceId,
                        (description.Flags & SoftwareFlag) != 0,
                        (ulong)description.DedicatedVideoMemory));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return adapters;
    }

    /// <summary>
    /// Why ONNX Runtime could not make a Direct3D 12 device on <paramref name="adapter"/>, or null
    /// when it could, or when that cannot be told — then it is left to ONNX Runtime to find out.
    /// </summary>
    /// <remarks>
    /// <para>ONNX Runtime's DirectML provider makes its device with <c>D3D12CreateDevice</c> at
    /// feature level 11_0; this asks the same, without making one. On VMware SVGA 3D that call fails
    /// with DXGI_ERROR_UNSUPPORTED, and finding it out through ONNX Runtime took a second — loading
    /// DirectML.dll and starting a session — every run of the app.</para>
    ///
    /// <para>Turns away exactly the adapters ONNX Runtime would fail on, and no other: no list of
    /// vendors or names, which would sooner or later meet a real GPU it did not know.</para>
    /// </remarks>
    internal static unsafe string? Direct3D12Problem(Adapter adapter)
    {
        const int featureLevel11 = 0xB000;   // D3D_FEATURE_LEVEL_11_0
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out var factory) < 0) return null;
            try
            {
                var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)
                    (*(IntPtr**)factory)[12];
                if (enumAdapters(factory, (uint)adapter.Index, out var found) < 0) return null;
                try
                {
                    var device = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");   // ID3D12Device
                    int hr = D3D12CreateDevice(found, featureLevel11, ref device, IntPtr.Zero);
                    return hr >= 0 ? null : $"{adapter.Name} cannot run Direct3D 12 (0x{hr:X8})";
                }
                finally
                {
                    Marshal.Release(found);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"Direct3D 12 is not on this system: {ex.Message}";
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

    // DXGI_ADAPTER_DESC1
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDescription
    {
        public fixed char Description[128];
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
}
