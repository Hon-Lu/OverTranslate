using System.IO;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Which graphics adapter the manga models may run on, or why none.
/// </summary>
/// <remarks>
/// <para>ONNX Runtime's DirectML device id is the adapter's position in DXGI's
/// <c>EnumAdapters1</c> order, so that is the order asked here. Adapter 0 is the system's default
/// and is what is used when it is real hardware.</para>
///
/// <para>A software adapter — WARP, "Microsoft Basic Render Driver", the only adapter a VM or a
/// driverless machine has — is refused even though DirectML would accept it: it runs the models on
/// the CPU through a rasteriser and is slower than the column pipeline it would replace. So is a
/// machine where DirectML.dll cannot be loaded at all. On Windows 10 1809 there is no system copy;
/// the app ships its own, but a missing one there would fail inside a delay-loaded import, where it
/// cannot be caught.</para>
/// </remarks>
internal static class DirectMlDevice
{
    // DXGI_ADAPTER_FLAG_SOFTWARE
    private const uint SoftwareFlag = 2;
    // Microsoft Basic Render Driver: the WARP adapter, whatever its flags say.
    private const uint MicrosoftVendor = 0x1414;
    private const uint BasicRenderDevice = 0x8C;
    // Windows 10 1903, the first build with DirectML in System32.
    private const int SystemDirectMlBuild = 18362;

    internal const string NoLibrary = "DirectML.dll is not available on this system";

    internal readonly record struct Adapter(
        int Index, string Name, uint VendorId, uint DeviceId, bool Software, ulong DedicatedMemory);

    /// <summary>The adapter to run on, or null with the reason there is none.</summary>
    internal static (Adapter? Adapter, string? Reason) Choose()
    {
        if (!DirectMlLibraryAvailable(AppContext.BaseDirectory, Environment.OSVersion.Version.Build, File.Exists))
            return (null, NoLibrary);

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

    internal static bool IsSoftware(Adapter adapter) =>
        adapter.Software || (adapter.VendorId == MicrosoftVendor && adapter.DeviceId == BasicRenderDevice);

    internal static bool DirectMlLibraryAvailable(string appDirectory, int osBuild, Func<string, bool> fileExists) =>
        fileExists(Path.Combine(appDirectory, "DirectML.dll")) || osBuild >= SystemDirectMlBuild;

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

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

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
