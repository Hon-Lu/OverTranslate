using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services.LocalAi;

/// <summary>A dedicated graphics card as Vulkan names it.</summary>
/// <param name="MemoryMiB">Its largest device-local heap: the figure a model's VRAM estimate is held against.</param>
internal sealed record LocalAiGpuDevice(string Name, long MemoryMiB);

/// <summary>Which dedicated graphics cards this machine has.</summary>
/// <remarks>
/// <para>Asked of Vulkan, because Vulkan is what the models run on and it answers the question
/// outright: each device says whether it is discrete, integrated, virtual or a CPU. DXGI cannot —
/// an integrated GPU reports "dedicated" memory of its own, and no size separates the two — and
/// <c>llama-server --list-devices</c> gives no type, and is not here before the first download.</para>
///
/// <para>Only <c>VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU</c> counts. An integrated GPU, a VM's virtual
/// one and a software rasteriser all count as nothing, which is the point: the feature is refused
/// rather than left to run where it would slow the whole computer down.</para>
///
/// <para>This creates a Vulkan instance in this process, which loads the driver and any implicit
/// layers installed on the machine. The layers are switched off for the call: overlays that hook
/// every Vulkan application are the usual reason that fails, and the question needs none of them.
/// It is slow enough to be kept off the UI thread, and answered once per run.</para>
/// </remarks>
internal static class LocalAiGpu
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Lazy<Task<IReadOnlyList<LocalAiGpuDevice>>> Cached =
        new(() => Task.Run(Detect));

    private const int DiscreteGpu = 2;      // VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
    private const uint DeviceLocal = 1;     // VK_MEMORY_HEAP_DEVICE_LOCAL_BIT
    private const string LayersDisable = "VK_LOADER_LAYERS_DISABLE";

    /// <summary>The dedicated cards, asked once and remembered for this run.</summary>
    internal static Task<IReadOnlyList<LocalAiGpuDevice>> DetectAsync() => Cached.Value;

    /// <summary>The cards, or none when Vulkan is missing or fails: the same as having none.</summary>
    private static IReadOnlyList<LocalAiGpuDevice> Detect()
    {
        var before = Environment.GetEnvironmentVariable(LayersDisable);
        Environment.SetEnvironmentVariable(LayersDisable, "~implicit~");
        try
        {
            var devices = Enumerate();
            Log.Info("Vulkan dedicated GPUs: {Devices}",
                devices.Count == 0 ? "none" : string.Join("; ", devices.Select(d => $"{d.Name} ({d.MemoryMiB} MiB)")));
            return devices;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            Log.Info("Vulkan is not usable here: {Message}", ex.Message);
            return [];
        }
        finally
        {
            Environment.SetEnvironmentVariable(LayersDisable, before);
        }
    }

    private static unsafe List<LocalAiGpuDevice> Enumerate()
    {
        const int applicationInfoType = 0;      // VK_STRUCTURE_TYPE_APPLICATION_INFO
        const int instanceCreateInfoType = 1;   // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
        const uint apiVersion11 = (1u << 22) | (1u << 12);

        var application = new ApplicationInfo { Type = applicationInfoType, ApiVersion = apiVersion11 };
        var create = new InstanceCreateInfo { Type = instanceCreateInfoType, ApplicationInfo = &application };
        var result = vkCreateInstance(&create, IntPtr.Zero, out var instance);
        if (result != 0) throw new InvalidOperationException($"vkCreateInstance returned {result}");

        try
        {
            uint count = 0;
            result = vkEnumeratePhysicalDevices(instance, ref count, null);
            if (result != 0) throw new InvalidOperationException($"vkEnumeratePhysicalDevices returned {result}");
            if (count == 0) return [];

            var handles = new IntPtr[count];
            fixed (IntPtr* first = handles)
            {
                result = vkEnumeratePhysicalDevices(instance, ref count, first);
                if (result != 0) throw new InvalidOperationException($"vkEnumeratePhysicalDevices returned {result}");
            }

            var found = new List<LocalAiGpuDevice>();
            foreach (var handle in handles.Take((int)count))
            {
                // Bigger than the structure's 824 bytes; the fields wanted are at fixed offsets.
                var properties = stackalloc byte[2048];
                vkGetPhysicalDeviceProperties(handle, properties);
                if (*(int*)(properties + 16) != DiscreteGpu) continue;

                var name = Marshal.PtrToStringUTF8((IntPtr)(properties + 20)) ?? "";

                var memory = stackalloc byte[1024];
                vkGetPhysicalDeviceMemoryProperties(handle, memory);
                var heapCount = *(uint*)(memory + 260);
                ulong largest = 0;
                for (var heap = 0; heap < Math.Min(heapCount, 16u); heap++)
                {
                    var entry = memory + 264 + heap * 16;
                    if ((*(uint*)(entry + 8) & DeviceLocal) != 0 && *(ulong*)entry > largest)
                        largest = *(ulong*)entry;
                }

                found.Add(new LocalAiGpuDevice(name, (long)(largest / (1024 * 1024))));
            }

            return found;
        }
        finally
        {
            vkDestroyInstance(instance, IntPtr.Zero);
        }
    }

    /// <summary>
    /// The <c>--device</c> value of the first of <paramref name="cards"/> in the output of
    /// <c>llama-server --list-devices</c>, or null when none of them is listed.
    /// </summary>
    /// <remarks>
    /// <para>By name, both being the driver's own <c>deviceName</c>. The check is what keeps the model
    /// off anything else: without a dedicated card llama.cpp falls back to the first GPU it can use,
    /// and with none at all to the CPU, and says nothing about either.</para>
    ///
    /// <para>Lines look like <c>  Vulkan0: NVIDIA GeForce RTX 3070 (8232 MiB, 7250 MiB free)</c>.</para>
    /// </remarks>
    internal static string? ChooseDevice(string listDevicesOutput, IReadOnlyList<LocalAiGpuDevice> cards)
    {
        foreach (var raw in listDevicesOutput.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line.StartsWith("Vulkan", StringComparison.Ordinal)) continue;
            var open = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= colon) continue;
            var name = line[(colon + 1)..open].Trim();
            if (cards.Any(card => card.Name == name)) return line[..colon];
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ApplicationInfo
    {
        public int Type;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct InstanceCreateInfo
    {
        public int Type;
        public IntPtr Next;
        public uint Flags;
        public ApplicationInfo* ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    private static extern unsafe int vkCreateInstance(InstanceCreateInfo* createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport("vulkan-1.dll")]
    private static extern unsafe int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr* devices);

    [DllImport("vulkan-1.dll")]
    private static extern unsafe void vkGetPhysicalDeviceProperties(IntPtr device, byte* properties);

    [DllImport("vulkan-1.dll")]
    private static extern unsafe void vkGetPhysicalDeviceMemoryProperties(IntPtr device, byte* properties);
}
