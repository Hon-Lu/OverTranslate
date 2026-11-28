using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Writes one line of the process's memory figures to the shipped log every half hour, so a report
/// of high memory can be read as in use or idle, and steady or climbing, from the log alone.
/// </summary>
/// <remarks>
/// Only numbers go in the line. Nothing here may change what it measures: no forced collection
/// (GC.Collect, GetTotalMemory(true)), no Process object whose handle could itself leak, and the
/// timer runs on the thread pool so it never touches the UI thread or keeps the process alive.
/// </remarks>
internal sealed class MemoryUsageLog : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;
    private int _failures;

    public MemoryUsageLog(TimeSpan interval)
    {
        _interval = interval;
        // One-shot, re-armed at the end of each tick: two ticks can never overlap, and a tick that
        // was due while the machine slept fires once, not once per missed period.
        _timer = new System.Threading.Timer(_ => Tick(), null, interval, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        try
        {
            var s = Sample.Take();
            Log.Info("Memory: private {PrivateMB}MB, working set {WorkingSetMB}MB, GC heap {GcHeapMB}MB, LOH {LohMB}MB, handles {Handles}, GDI {Gdi}, USER {User}",
                s.PrivateBytes >> 20, s.WorkingSetBytes >> 20, s.GcHeapBytes >> 20, s.LohBytes >> 20,
                s.Handles, s.GdiObjects, s.UserObjects);
        }
        catch (Exception ex)
        {
            // A broken sample is never worth the app; say so once, not every half hour.
            try { if (Interlocked.Increment(ref _failures) == 1) Log.Warn(ex, "Memory usage sample failed"); }
            catch { /* the log itself is gone, e.g. during shutdown */ }
        }

        try { _timer.Change(_interval, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed while this tick ran: the app is closing */ }
    }

    internal readonly record struct Sample(
        long PrivateBytes, long WorkingSetBytes, long GcHeapBytes, long LohBytes,
        int Handles, int GdiObjects, int UserObjects)
    {
        /// <summary>Reads the figures without collecting, allocating little, from any thread.</summary>
        public static Sample Take()
        {
            // The pseudo-handle needs no closing; Process.GetCurrentProcess() would open a real one.
            nint self = GetCurrentProcess();

            var counters = new ProcessMemoryCountersEx { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
            if (!GetProcessMemoryInfo(self, ref counters, counters.cb))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            GetProcessHandleCount(self, out uint handles);

            // As of the last collection; reading it never starts one. Index 3 is the large object heap.
            var gc = GC.GetGCMemoryInfo();
            long loh = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : 0;

            return new Sample(
                (long)counters.PrivateUsage,
                (long)counters.WorkingSetSize,
                GC.GetTotalMemory(forceFullCollection: false),
                loh,
                (int)handles,
                (int)GetGuiResources(self, GrGdiObjects),
                (int)GetGuiResources(self, GrUserObjects));
        }
    }

    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(nint process, out uint count);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
}
