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
    private static readonly TimeSpan ThreadCallTimeout = TimeSpan.FromSeconds(5);

    // How long the first usable frame is waited for. A monitor is always being composed, so unlike
    // a window this is about the capture chain coming up rather than about the source having
    // content — but the wait also has to cover the exclusion taking effect, which needs a frame
    // composed after the set call.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<IntPtr> _resolveMonitor;
    private readonly Func<IReadOnlyList<IntPtr>> _resolveOverlays;
    private readonly IDirect3DDevice _device;
    private readonly WgcSurfaceReader _reader;

    // The capture chain below lives on this thread and is touched nowhere else. See WgcFrameThread.
    private readonly WgcFrameThread _thread;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IntPtr _monitor;
    private SizeInt32 _poolSize;
    private int _disposed;

    // Which windows the session was last told to leave out, and the iteration that answer holds
    // from. Set on the frame thread, where the frames are checked against them.
    private IntPtr[] _excluded = [];
    private long _isolatedFrom;

    // The list last asked for — by a poll, or by Attach — and the one the frames wait for. Under
    // _latestLock, with _isolatedFrom, so a list queued behind another is never overtaken by it.
    private IntPtr[] _requested = [];

    private readonly object _latestLock = new();
    private Bitmap? _latest;
    private long _latestAt;
    private long _lastGrabAt;

    // Raised whenever a frame has been read back; only the start-up wait listens.
    private readonly ManualResetEventSlim _frameSignal = new(false);

    private int _framesReceived;
    private int _framesRead;
    private int _framesBeforeExclusion;
    private int _rebuilds;

    // Where a poll and the frame handler are right now, for the line a stuck session leaves behind —
    // see StepMarker. Reported by DescribeActivity.
    private readonly StepMarker _grabStep = new("idle");
    private readonly StepMarker _arrivalStep = new("idle");
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
        _thread = new WgcFrameThread(ReadHeldFrame);
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
            var attaching = backend;
            if (!backend._thread.Invoke(() => attaching.Attach(monitor), ThreadCallTimeout))
                throw new InvalidOperationException("the monitor refused capture");
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
        try
        {
            return GrabRegionCore(screenBounds);
        }
        finally
        {
            _grabStep.Set("idle");
        }
    }

    private Bitmap? GrabRegionCore(Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return null;
        if (IsDisposed) return null;

        Volatile.Write(ref _lastGrabAt, Stopwatch.GetTimestamp());

        // Before any pixels are handed out, because this is where an overlay that appeared since the
        // last poll gets excluded. Until the frames catch up with the new list there is nothing safe
        // to return, and the loop simply skips a poll.
        _grabStep.Set("exclusions");
        SyncExclusions();

        _grabStep.Set("origin");
        if (!TryGetFrameOrigin(out var origin)) return null;

        _grabStep.Set("crop");
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

    public string DescribeSteps() =>
        $"grab=[{_grabStep.Describe()}] arrival=[{_arrivalStep.Describe()}]";

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
    ///
    /// Setting the list is a call on the session, so it is handed to the frame thread rather than
    /// made here. Until that thread has made it, nothing is handed out: the picture in hand is
    /// dropped and no frame is read, because any frame composed before the call may show the
    /// overlay that has just appeared.
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

        IntPtr[] windows;
        lock (_latestLock)
        {
            if (Matches(_requested, wanted)) return;

            windows = [.. wanted];
            _requested = windows;
            Volatile.Write(ref _isolatedFrom, long.MaxValue);
            _latest?.Dispose();
            _latest = null;
        }

        _thread.Post(() =>
        {
            if (IsDisposed || _session is null) return;
            if (!ApplyExclusions(windows))
            {
                // The list was accepted once, at construction, and has now been refused. Frames from
                // here on may contain the overlays, so none is read: the session stops producing
                // rather than starting to feed on itself.
                Log.Error(
                    "Realtime monitor capture could not update its window exclusion list; " +
                    "no further frames will be read");
            }
        });
    }

    /// <summary>
    /// Sets the exclusion list and records the iteration it holds from. Frame thread only.
    /// </summary>
    private bool ApplyExclusions(IntPtr[] windows)
    {
        if (_session is not { } session) return false;

        var iteration = WgcWindowExclusion.TrySet(session, windows, out var detail);
        if (iteration is not { } applied) return false;

        _excluded = windows;
        Interlocked.Increment(ref _exclusionUpdates);

        // The frame in hand was composed under the previous list, which did not have whatever
        // window has just been added to this one — so it may show that overlay, and handing it to
        // the next poll would be the self-feed arriving by the back door. Dropped rather than kept
        // as a stale-but-probably-fine picture: polls return nothing until a frame composed under
        // the new list arrives, which is one screen refresh away.
        //
        // The iteration is only taken up if no poll has asked for a newer list since: a list queued
        // behind this one is the one the frames have to wait for.
        lock (_latestLock)
        {
            if (ReferenceEquals(windows, _requested)) Volatile.Write(ref _isolatedFrom, (long)applied);
            _latest?.Dispose();
            _latest = null;
        }
        // The kept frame was composed under the old list too.
        _thread.ReleaseHeld();
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

    /// <summary>
    /// Points the capture at a monitor, replacing whatever it was pointed at before. Frame thread
    /// only, like the pool it creates — see the same method in <see cref="WgcWindowCaptureBackend"/>.
    /// </summary>
    private bool Attach(IntPtr monitor)
    {
        if (IsDisposed) return false;

        Detach();

        var item = WgcInterop.CreateItemForMonitor(monitor);
        if (item is null) return false;

        _item = item;
        _monitor = monitor;
        item.Closed += OnItemClosed;

        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.Create(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(item);

        // Nothing is read until this succeeds — set before StartCapture so the very first frame
        // the system composes for this session is already composed without the overlays.
        IntPtr[] windows = [.. _resolveOverlays()];
        lock (_latestLock) _requested = windows;
        if (!ApplyExclusions(windows))
        {
            Log.Warn("Realtime monitor capture refused: the window exclusion list was not accepted");
            Detach();
            return false;
        }

        // The pointer is not part of the screen's content, and it lands in the middle of the
        // text being read as often as anywhere else.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _session.IsCursorCaptureEnabled = false;

        WgcFrameThread.LimitFrameRate(_session, MaxFrameAge);
        _session.StartCapture();

        Log.Info(
            "Realtime capture backend WgcMonitor attached: hmonitor={Monitor:X} " +
            "itemSize={Width}x{Height} excluded={Excluded}",
            monitor, item.Size.Width, item.Size.Height, _excluded.Length);
        return true;
    }

    /// <summary>
    /// Called on the frame thread at the monitor's refresh rate. Takes every frame to keep the pool
    /// turning over, reads back only what a poll has asked for — and only what was composed with the
    /// exclusion list in force.
    /// </summary>
    /// <remarks>
    /// With the timer that reads a kept frame, the only code that touches a frame or the device. See
    /// the same handler in <see cref="WgcWindowCaptureBackend"/> and <see cref="WgcFrameThread"/>.
    /// </remarks>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        SizeInt32? resizeTo = null;
        Direct3D11CaptureFrame? frame = null;
        var held = false;
        try
        {
            _arrivalStep.Set("frame");
            frame = sender.TryGetNextFrame();
            if (frame is null) return;

            Interlocked.Increment(ref _framesReceived);

            // Noted here and done at the end of the handler, once the frame has been released: a
            // pool asked to recreate itself while one of its own frames is still held waits for a
            // buffer that only this thread can give back, and this thread is inside the wait. See
            // the same deferral in WgcWindowCaptureBackend, where the deadlock was found.
            var content = frame.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                resizeTo = content;

            // A newer frame makes the kept one stale, whatever becomes of this one.
            _thread.ReleaseHeld();

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

            var sinceRead = Stopwatch.GetElapsedTime(Volatile.Read(ref _latestAt));
            var arrival = WgcFrameThrottle.OnArrival(
                HasFrame(),
                sinceRead,
                Stopwatch.GetElapsedTime(Volatile.Read(ref _lastGrabAt)),
                MaxFrameAge,
                IdleAfter);
            if (arrival == WgcFrameThrottle.Arrival.Skip) return;
            // A frame that resizes the pool cannot be kept past the end of this handler, so it is
            // read now rather than risk being the one change on a still screen that goes missing.
            if (arrival == WgcFrameThrottle.Arrival.Hold && resizeTo is null)
            {
                _thread.Hold(frame, WgcFrameThrottle.HeldFrameDueIn(sinceRead, MaxFrameAge));
                held = true;
                return;
            }

            _arrivalStep.Set("readback");
            ReadBack(frame.Surface, content, (long)iteration.Value);
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
            if (!held) frame?.Dispose();
            // After the frame above has been released, so the pool gets every buffer back — and so
            // must the one kept from an earlier call, which the early returns above never touch.
            // Recreating while it is still out waits for a buffer nobody is going to give back.
            if (resizeTo is { } size)
            {
                _thread.ReleaseHeld();
                _arrivalStep.Set("recreate");
                Recreate(size);
            }

            _arrivalStep.Set("idle");
        }
    }

    /// <summary>
    /// Copies a frame composed under exclusion iteration <paramref name="iteration"/> into memory as
    /// the picture polls are handed. Frame thread only.
    /// </summary>
    private void ReadBack(IDirect3DSurface surface, SizeInt32 content, long iteration)
    {
        var started = Stopwatch.GetTimestamp();
        var bitmap = _reader.Read(surface, content.Width, content.Height);
        Interlocked.Add(ref _readbackTicks, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref _framesRead);

        Bitmap? previous;
        lock (_latestLock)
        {
            // A poll asked for a new exclusion list while this was being read, so it was composed
            // under the old one and may show the overlay the new one leaves out.
            if (iteration < Volatile.Read(ref _isolatedFrom))
            {
                previous = bitmap;
            }
            else
            {
                previous = _latest;
                _latest = bitmap;
                Volatile.Write(ref _latestAt, Stopwatch.GetTimestamp());
            }
        }
        previous?.Dispose();
        _frameSignal.Set();
    }

    /// <summary>
    /// Reads back the frame the handler kept, once the readback limit allows it — on a still screen
    /// no later frame is coming to do it. Run by the frame thread's timer; owns the frame.
    /// </summary>
    private void ReadHeldFrame(Direct3D11CaptureFrame frame)
    {
        try
        {
            // Nobody has polled for a while: the session is ending.
            if (IsDisposed || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastGrabAt)) > IdleAfter) return;

            // Checked again: the exclusion list may have changed while the frame was kept.
            var iteration = WgcWindowExclusion.TryGetIteration(frame);
            if (iteration is null || (long)iteration.Value < Volatile.Read(ref _isolatedFrom)) return;

            _arrivalStep.Set("held readback");
            ReadBack(frame.Surface, frame.ContentSize, (long)iteration.Value);
        }
        catch (ObjectDisposedException)
        {
            // The pool went while the frame was waiting; the next frame recovers.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Realtime monitor capture dropped a held frame");
        }
        finally
        {
            frame.Dispose();
            _arrivalStep.Set("idle");
        }
    }

    /// <summary>Frame thread only, and only once the frame that asked for it has been released.</summary>
    private void Recreate(SizeInt32 size)
    {
        if (IsDisposed || _pool is null) return;

        try
        {
            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, size);
            _poolSize = size;
            Interlocked.Increment(ref _rebuilds);
            Log.Debug("Realtime monitor capture resized to {Width}x{Height}", size.Width, size.Height);
        }
        catch (Exception ex)
        {
            // The next frame of the wrong size asks again.
            Log.Debug(ex, "Realtime monitor capture could not resize to {Width}x{Height}", size.Width, size.Height);
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

            // A wait the frame ends rather than a sleep; frames arrive on the frame thread.
            _frameSignal.Wait(TimeSpan.FromMilliseconds(Math.Min(50, left.TotalMilliseconds)));
            _frameSignal.Reset();
        }

        Log.Warn(
            "Realtime monitor capture of hmonitor={Monitor:X} produced no frame at or after " +
            "configuration iteration {Iteration} ({Discarded} discarded)",
            _monitor, Volatile.Read(ref _isolatedFrom), Volatile.Read(ref _framesBeforeExclusion));
        return false;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
        // On the frame thread, with everything else that touches the chain.
        if (!IsDisposed) _thread.Post(ReattachAfterClose);
    }

    private void ReattachAfterClose()
    {
        if (IsDisposed) return;

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

    /// <summary>Frame thread only.</summary>
    private void Detach()
    {
        if (_item is { } item) item.Closed -= OnItemClosed;
        if (_pool is { } pool) pool.FrameArrived -= OnFrameArrived;

        _thread.ReleaseHeld();
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _item = null;
        _excluded = [];
    }

    /// <summary>
    /// Closes the capture on the frame thread and ends that thread — see the same method in
    /// <see cref="WgcWindowCaptureBackend"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (!_thread.Shutdown(Detach, ThreadCallTimeout))
        {
            Log.Warn(
                "Realtime capture thread for hmonitor={Monitor:X} did not close within {Seconds}s. {Steps} | {Activity}",
                _monitor, ThreadCallTimeout.TotalSeconds, DescribeSteps(), DescribeActivity());
            return;
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
