using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Captures one whole monitor through Windows.Graphics.Capture, with OverTranslate's own overlays
/// left out of the frames by the capture session itself, and cuts each region out of that.
/// </summary>
/// <remarks>
/// This is the full-screen source done the way the window source already works: the isolation is a
/// property of what is being captured, not a favour asked of the windows drawing on top of it. The
/// session is told which windows to leave out, the system composes the monitor without them, and the
/// excluded rectangles come back showing what is <i>underneath</i> — measured, because everything
/// here depended on that and nothing said it: with a stand-in subtitle layer over a source window,
/// the layer went from 98.5% of the region to 0.0%, and 98.1% of it came back as the source window
/// rather than as black (<c>WgcProbe exclusion</c>).
///
/// What it replaces on that path is <c>WDA_EXCLUDEFROMCAPTURE</c>, which asks each overlay to hide
/// itself from anything reading the screen and fails silently on every Windows before 11 24H2 —
/// #94, where the loop spent nine seconds translating its own output. The difference is not that
/// this API is newer. It is that the request and the promise are separate here: the set call returns
/// the configuration iteration from which the exclusion holds, every frame says which iteration it
/// was composed under, and this backend simply does not read a frame from before that number. There
/// is no state in which it believes an exclusion that did not happen.
///
/// The cost is who can use it. The exclusion list needs a Windows much newer than 24H2, so a system
/// without it has no 螢幕擷取 at all — this is the only backend for that mode, and
/// <c>RealtimeSessionController.CreateScreenCapture</c> refuses rather than offering a second one.
/// There used to be a second one, grabbing the composited desktop with the overlays asked to hide
/// themselves; it was dropped in #105 precisely because that arrangement cannot be checked from
/// inside the program, which is the difference this backend exists to make.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
public sealed class WgcMonitorCaptureBackend : IRealtimeCaptureBackend
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // The same throttles as the window backend, set for the same reasons: a readback is the one
    // expensive step and frames arrive far faster than a session polls. See
    // WgcWindowCaptureBackend, where each number is argued.
    // Internal rather than private so the realtime poll interval can be checked against it:
    // polling faster than frames are read back means two polls in a row see the same pixels,
    // which reads as "the picture has settled" and disables the settle wait without saying so.
    internal static readonly TimeSpan MaxFrameAge = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(1);
    private const int FrameBuffers = 2;

    // How long the first usable frame is waited for. A monitor is always being composed, so unlike
    // a window this is about the capture chain coming up rather than about the source having
    // content — but the wait also has to cover the exclusion taking effect, which needs a frame
    // composed after the set call.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<IntPtr> _resolveMonitor;
    private readonly Func<IReadOnlyList<IntPtr>> _resolveOverlays;
    private readonly IDirect3DDevice _device;
    private readonly WgcSurfaceReader _reader;

    private readonly object _sync = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IntPtr _monitor;
    private SizeInt32 _poolSize;
    private bool _disposed;

    // Which windows the session was last told to leave out, and the iteration that answer holds
    // from. Both are read by the frame handler on the capture stack's own thread.
    private IntPtr[] _excluded = [];
    private long _isolatedFrom;

    private readonly object _latestLock = new();
    private Bitmap? _latest;
    private long _latestAt;
    private long _lastGrabAt;

    // Raised whenever a frame has been read back; only the start-up wait listens. See
    // WgcWindowCaptureBackend for why that wait must not be a sleep.
    private readonly ManualResetEventSlim _frameSignal = new(false);

    private int _framesReceived;
    private int _framesRead;
    private int _framesBeforeExclusion;
    private int _rebuilds;
    private int _exclusionUpdates;
    private long _readbackTicks;
    private int _outsideReported;

    private WgcMonitorCaptureBackend(
        Func<IntPtr> resolveMonitor,
        Func<IReadOnlyList<IntPtr>> resolveOverlays,
        IDirect3DDevice device,
        WgcSurfaceReader reader)
    {
        _resolveMonitor = resolveMonitor;
        _resolveOverlays = resolveOverlays;
        _device = device;
        _reader = reader;
    }

    /// <summary>
    /// Raised when the monitor is gone — unplugged, or replaced by a mode change — and could not be
    /// found again. Same contract as the window backend: the session ends rather than falling back
    /// to a source that would contain this application's own overlays.
    /// </summary>
    public event EventHandler<string>? SourceLost;

    public string Name => "WgcMonitor";

    /// <summary>
    /// Builds a backend over the monitor <paramref name="resolveMonitor"/> names, or returns null
    /// when this system cannot capture, when it has no window exclusion list, when the monitor
    /// refuses capture, or when the exclusion was not accepted.
    /// </summary>
    /// <param name="resolveOverlays">
    /// Every window of this application's that must stay out of the frames, asked again whenever a
    /// poll finds the answer may have changed — overlays are created and destroyed while a session
    /// runs, and an exclusion list set once at the start would be a list of the windows that
    /// happened to exist then. Must be safe to call from the polling thread, so the caller hands
    /// over a snapshot rather than reaching into windows.
    /// </param>
    public static WgcMonitorCaptureBackend? TryCreate(
        Func<IntPtr> resolveMonitor, Func<IReadOnlyList<IntPtr>> resolveOverlays)
    {
        if (!WgcInterop.IsCaptureSupported()) return null;

        if (!WgcCapability.SupportsWindowExclusion)
        {
            // Not a failure worth a warning: it is the ordinary state of every Windows older than
            // the one that shipped the API, and the caller has another backend to offer.
            Log.Info(
                "Realtime monitor capture unavailable: this system (Windows {Version}) has no window " +
                "exclusion list, so a monitor capture could not keep this application's overlays out " +
                "of its frames", Environment.OSVersion.Version);
            return null;
        }

        var monitor = resolveMonitor();
        if (monitor == IntPtr.Zero) return null;

        IDirect3DDevice? device = null;
        WgcSurfaceReader? reader = null;
        WgcMonitorCaptureBackend? backend = null;
        try
        {
            // On the adapter driving this monitor rather than on whichever one is listed first: a
            // capture served by another adapter is not refused, it simply never delivers a frame.
            device = WgcInterop.CreateDirect3DDevice(
                monitor, false, out var rawDevice, out var context, out var adapter);
            Log.Debug("Realtime monitor capture using {Adapter}", adapter);

            reader = new WgcSurfaceReader(rawDevice, context);
            backend = new WgcMonitorCaptureBackend(resolveMonitor, resolveOverlays, device, reader);
            if (!backend.Attach(monitor)) throw new InvalidOperationException("the monitor refused capture");
            if (!backend.WaitForIsolatedFrame(FirstFrameTimeout))
                throw new InvalidOperationException("no frame arrived with the exclusion list in effect");

            return backend;
        }
        catch (Exception ex)
        {
            // Every reason this fails is a reason to offer another backend rather than to end the
            // session — and refusing here is the whole point, because the alternative is a screen
            // capture that quietly contains the subtitles it is about to read.
            Log.Warn(ex, "Could not start monitor capture for hmonitor={Monitor:X}", monitor);
            backend?.Dispose();
            if (backend is null)
            {
                (device as IDisposable)?.Dispose();
                reader?.Dispose();
            }
            return null;
        }
    }

    /// <summary>
    /// The monitor a screen rectangle belongs to, or <see cref="IntPtr.Zero"/> if there is none.
    /// </summary>
    /// <remarks>
    /// Asked of Windows rather than worked out from <c>Screen.AllScreens</c>, which would have to
    /// match rectangles that a display arrangement can make ambiguous. Nearest rather than exact so
    /// a session whose screen was remembered from a layout that has since changed still lands
    /// somewhere real instead of refusing to start.
    /// </remarks>
    public static IntPtr MonitorFor(Rectangle screenBounds)
    {
        var rect = new RECT
        {
            Left = screenBounds.Left,
            Top = screenBounds.Top,
            Right = screenBounds.Right,
            Bottom = screenBounds.Bottom
        };
        return MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
    }

    public Bitmap? GrabRegion(Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return null;
        if (Volatile.Read(ref _disposed)) return null;

        Volatile.Write(ref _lastGrabAt, Stopwatch.GetTimestamp());

        // Before any pixels are handed out, because this is where an overlay that appeared since the
        // last poll gets excluded. Until the frames catch up with the new list there is nothing safe
        // to return, and the loop simply skips a poll.
        SyncExclusions();

        if (!TryGetFrameOrigin(out var origin)) return null;

        lock (_latestLock)
        {
            if (_latest is not { } latest) return null;

            var crop = screenBounds with { X = screenBounds.X - origin.X, Y = screenBounds.Y - origin.Y };
            var visible = Rectangle.Intersect(crop, new Rectangle(0, 0, latest.Width, latest.Height));
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                // The region is not on this monitor any more — the screen changed resolution under
                // it, or the layout moved. Nothing to read, and nothing to say on every poll.
                if (Interlocked.Exchange(ref _outsideReported, 1) == 0)
                    Log.Warn(
                        "Realtime region {Bounds} lies outside the captured monitor at {Origin}; " +
                        "further occurrences logged at Debug", screenBounds, origin);
                else
                    Log.Debug("Realtime region {Bounds} lies outside the captured monitor", screenBounds);
                return null;
            }

            // Always the size that was asked for, so a partly-visible region does not move every
            // recognised line relative to the block that displays it.
            var frame = new Bitmap(
                screenBounds.Width, screenBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(frame);
                graphics.DrawImage(
                    latest,
                    new Rectangle(visible.X - crop.X, visible.Y - crop.Y, visible.Width, visible.Height),
                    visible,
                    GraphicsUnit.Pixel);
                return frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }

    public string DescribeActivity()
    {
        var reads = Volatile.Read(ref _framesRead);
        var averageMs = reads == 0
            ? 0
            : Stopwatch.GetElapsedTime(0, Volatile.Read(ref _readbackTicks)).TotalMilliseconds / reads;

        return $"hmonitor={_monitor:X} received={Volatile.Read(ref _framesReceived)} read={reads} " +
               $"avgReadback={averageMs:F1}ms discardedBeforeExclusion={Volatile.Read(ref _framesBeforeExclusion)} " +
               $"exclusionUpdates={Volatile.Read(ref _exclusionUpdates)} excluded={_excluded.Length} " +
               $"rebuilds={Volatile.Read(ref _rebuilds)}";
    }

    // ── Exclusion ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the session's exclusion list match the overlays that exist now, and says whether the
    /// list is currently in force.
    /// </summary>
    /// <remarks>
    /// Called on every poll rather than pushed by whatever creates an overlay, because the property
    /// this backend promises is about frames, not about bookkeeping: asking here means an overlay
    /// that appeared by any route — a block added, the control bar rebuilt, something a future
    /// change introduces — is excluded before the next region is read, and nobody has to remember to
    /// announce it. The work when nothing changed is comparing a handful of handles.
    /// </remarks>
    private void SyncExclusions()
    {
        IReadOnlyList<IntPtr> wanted;
        try
        {
            wanted = _resolveOverlays();
        }
        catch (Exception ex)
        {
            // The caller's snapshot is unavailable — a session being torn down is the usual cause.
            // Keeping the list as it is means keeping the exclusion that is already in force.
            Log.Debug(ex, "Realtime monitor capture could not read its overlay list");
            return;
        }

        if (Matches(_excluded, wanted)) return;

        lock (_sync)
        {
            if (_disposed || _session is null) return;
            if (!ApplyExclusionsLocked(wanted))
            {
                // The list was accepted once, at construction, and has now been refused. Frames from
                // here on may contain the overlays, so none is read: the session stops producing
                // rather than starting to feed on itself.
                Volatile.Write(ref _isolatedFrom, long.MaxValue);
                Log.Error(
                    "Realtime monitor capture could not update its window exclusion list; " +
                    "no further frames will be read");
            }
        }
    }

    /// <summary>
    /// Sets the exclusion list and records the iteration it holds from. Callers hold <see cref="_sync"/>.
    /// </summary>
    private bool ApplyExclusionsLocked(IReadOnlyList<IntPtr> windows)
    {
        if (_session is not { } session) return false;

        var iteration = WgcWindowExclusion.TrySet(session, windows, out var detail);
        if (iteration is not { } applied) return false;

        _excluded = [.. windows];
        Volatile.Write(ref _isolatedFrom, (long)applied);
        Interlocked.Increment(ref _exclusionUpdates);

        // The frame in hand was composed under the previous list, which did not have whatever
        // window has just been added to this one — so it may show that overlay, and handing it to
        // the next poll would be the self-feed arriving by the back door. Dropped rather than kept
        // as a stale-but-probably-fine picture: polls return nothing until a frame composed under
        // the new list arrives, which is one screen refresh away.
        lock (_latestLock)
        {
            _latest?.Dispose();
            _latest = null;
        }
        Log.Debug("Realtime monitor capture exclusion list: {Detail}", detail);
        return true;
    }

    private static bool Matches(IReadOnlyList<IntPtr> current, IReadOnlyList<IntPtr> wanted)
    {
        if (current.Count != wanted.Count) return false;
        for (var i = 0; i < current.Count; i++)
            if (current[i] != wanted[i])
                return false;
        return true;
    }

    // ── Capture chain ────────────────────────────────────────────────────────────────────────────

    private bool Attach(IntPtr monitor)
    {
        lock (_sync)
        {
            if (_disposed) return false;

            DetachLocked();

            var item = WgcInterop.CreateItemForMonitor(monitor);
            if (item is null) return false;

            _item = item;
            _monitor = monitor;
            item.Closed += OnItemClosed;

            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, _poolSize);
            _pool.FrameArrived += OnFrameArrived;

            _session = _pool.CreateCaptureSession(item);

            // Nothing is read until this succeeds — set before StartCapture so the very first frame
            // the system composes for this session is already composed without the overlays.
            if (!ApplyExclusionsLocked(_resolveOverlays()))
            {
                Log.Warn("Realtime monitor capture refused: the window exclusion list was not accepted");
                DetachLocked();
                return false;
            }

            // The pointer is not part of the screen's content, and it lands in the middle of the
            // text being read as often as anywhere else.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _session.IsCursorCaptureEnabled = false;

            _session.StartCapture();

            Log.Info(
                "Realtime capture backend WgcMonitor attached: hmonitor={Monitor:X} " +
                "itemSize={Width}x{Height} excluded={Excluded}",
                monitor, item.Size.Width, item.Size.Height, _excluded.Length);
            return true;
        }
    }

    /// <summary>
    /// Free-threaded, called at the monitor's refresh rate. Takes every frame to keep the pool
    /// turning over, reads back only what a poll has asked for — and only what was composed with the
    /// exclusion list in force.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        SizeInt32? resizeTo = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            Interlocked.Increment(ref _framesReceived);

            // Noted here and done at the end of the handler, once the frame has been released: a
            // pool asked to recreate itself while one of its own frames is still held waits for a
            // buffer that only this thread can give back, and this thread is inside the wait. See
            // the same deferral in WgcWindowCaptureBackend, where the deadlock was found.
            var content = frame.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                resizeTo = content;

            // The one check this whole backend rests on. A frame numbered below the iteration the
            // exclusion was accepted at was composed before it took effect and may still hold the
            // overlays; reading it would put this application's own subtitles in front of
            // recognition, which is #94 with a newer API. A frame that cannot say which iteration it
            // belongs to is treated the same way — the promise is the frame's, not the API's.
            var iteration = WgcWindowExclusion.TryGetIteration(frame);
            if (iteration is null || (long)iteration.Value < Volatile.Read(ref _isolatedFrom))
            {
                Interlocked.Increment(ref _framesBeforeExclusion);
                return;
            }

            if (HasFrame())
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _latestAt)) < MaxFrameAge) return;
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastGrabAt)) > IdleAfter) return;
            }

            var started = Stopwatch.GetTimestamp();
            var bitmap = _reader.Read(frame.Surface, content.Width, content.Height);
            Interlocked.Add(ref _readbackTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _framesRead);

            Bitmap? previous;
            lock (_latestLock)
            {
                previous = _latest;
                _latest = bitmap;
                Volatile.Write(ref _latestAt, Stopwatch.GetTimestamp());
            }
            previous?.Dispose();
            _frameSignal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The session is being torn down under us; the frame belongs to a pool that is going.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Realtime monitor capture dropped a frame");
        }
        finally
        {
            // Outside the try above, so the frame it was decided for has been disposed by now.
            if (resizeTo is { } size) Recreate(size);
        }
    }

    private void Recreate(SizeInt32 size)
    {
        lock (_sync)
        {
            if (_disposed || _pool is null) return;

            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, size);
            _poolSize = size;
            Interlocked.Increment(ref _rebuilds);
            Log.Debug("Realtime monitor capture resized to {Width}x{Height}", size.Width, size.Height);
        }
    }

    /// <summary>
    /// Waits for one frame that was composed with the exclusion list in force, which is what makes
    /// this backend's isolation a fact rather than a request.
    /// </summary>
    /// <remarks>
    /// Unlike a window, a monitor is always being composed, so this is not asking whether the source
    /// has content — it is the last gate before the session is allowed to read the screen. A monitor
    /// showing something perfectly still produces no new frames, which is why the iteration is what
    /// is waited for and why the wait is generous: it is paid once.
    /// </remarks>
    private bool WaitForIsolatedFrame(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            // The readback is demand-driven and nothing has polled yet.
            Volatile.Write(ref _lastGrabAt, Stopwatch.GetTimestamp());

            lock (_latestLock)
            {
                if (_latest is not null) return true;
            }

            var left = timeout - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) break;

            // A wait rather than a sleep, for the reason spelled out in
            // WgcWindowCaptureBackend.WaitForUsableFrame: this runs on the thread that started the
            // session, and on an STA thread only a wait keeps pumping COM.
            _frameSignal.Wait(TimeSpan.FromMilliseconds(Math.Min(50, left.TotalMilliseconds)));
            _frameSignal.Reset();
        }

        Log.Warn(
            "Realtime monitor capture of hmonitor={Monitor:X} produced no frame at or after " +
            "configuration iteration {Iteration} ({Discarded} discarded)",
            _monitor, Volatile.Read(ref _isolatedFrom), Volatile.Read(ref _framesBeforeExclusion));
        return false;
    }

    private bool HasFrame()
    {
        lock (_latestLock) return _latest is not null;
    }

    /// <summary>
    /// The monitor's top-left corner in virtual-screen coordinates, which is where the frame's
    /// (0,0) sits. Re-read every poll because it moves: a second monitor's origin changes when the
    /// primary changes resolution, and negative coordinates are ordinary on a display to the left of
    /// the primary.
    /// </summary>
    private bool TryGetFrameOrigin(out Point origin)
    {
        origin = Point.Empty;
        var monitor = _monitor;
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        origin = new Point(info.rcMonitor.Left, info.rcMonitor.Top);
        return true;
    }

    /// <summary>
    /// The monitor is gone — unplugged, or replaced when the display mode changed. One attempt to
    /// find it again, then the session is told it has lost its source.
    /// </summary>
    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (Volatile.Read(ref _disposed)) return;

        Log.Warn("Realtime capture source monitor {Monitor:X} closed", _monitor);

        try
        {
            var replacement = _resolveMonitor();
            if (replacement != IntPtr.Zero && Attach(replacement))
            {
                Log.Info("Realtime capture re-attached to hmonitor={Monitor:X}", replacement);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not re-attach realtime capture after the source monitor closed");
        }

        // Deliberately no fallback to grabbing the desktop: that path is only safe where the
        // overlays can hide themselves, and arriving at it silently is where the self-feed came from.
        SourceLost?.Invoke(this, LocalizationService.Get("S.Realtime.CaptureSourceLost"));
    }

    // ── Teardown ─────────────────────────────────────────────────────────────────────────────────

    private void DetachLocked()
    {
        if (_item is { } item) item.Closed -= OnItemClosed;
        if (_pool is { } pool) pool.FrameArrived -= OnFrameArrived;

        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _item = null;
        _excluded = [];
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DetachLocked();
        }

        lock (_latestLock)
        {
            _latest?.Dispose();
            _latest = null;
        }

        _frameSignal.Dispose();
        (_device as IDisposable)?.Dispose();
        _reader.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
}
