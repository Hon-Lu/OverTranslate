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
/// Captures the watched application's window directly, through Windows.Graphics.Capture, and cuts
/// each region out of that.
/// </summary>
/// <remarks>
/// The point of this backend is what it does <i>not</i> see. Its source is one window's composited
/// surface, so OverTranslate's own subtitle layers — which are separate top-level windows drawn over
/// that one — are not in the frame at all. There is nothing to exclude, nothing to hide, and no
/// window style the overlays have to adopt to stay out of the way: the isolation that #94 asked
/// three transparent WPF windows to provide is a property of the source instead. That also means it
/// keeps working where <c>WDA_EXCLUDEFROMCAPTURE</c> does not, which is every Windows before 11 24H2.
///
/// It buys two other things on the way. A window covered by something else still captures, because
/// DWM holds the window's own surface rather than the screen's. And one readback serves every region
/// instead of one desktop copy per region per poll.
///
/// What it costs is the source question — which window — and the limits of window capture: content
/// the user wants that lives in a <i>separate</i> top-level window (a popup, a tooltip, an overlay
/// from another program) is not in this frame either. That is the same property working for and
/// against us, and it is why this is chosen per session rather than always.
///
/// The source question is now put to the user, in <see cref="CaptureWindowList"/> and the picker on
/// the page, rather than inferred from the framed blocks by <see cref="SourceWindowResolver"/> —
/// which survives as the probe tool's way of answering it without a page.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
public sealed class WgcWindowCaptureBackend : IRealtimeCaptureBackend
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // How often a frame is read back off the GPU. This is the one expensive step in the path — a
    // full copy of the captured window into main memory — so it is throttled here, well away from
    // the rate frames arrive at, which over a game is 144 a second.
    //
    // Set under the poll interval so a region always finds a frame it has not seen before, and no
    // tighter: at this window size a readback measured 20ms, which is 16% of a core spent whether
    // anything changed or not. The first thing to try if that ever matters is reading back only the
    // rectangles being watched rather than the whole window — which needs a crop on the GPU, and so
    // the D3D interop this deliberately does without for now.
    //
    // It used to be half the poll, which the poll dropping to 150ms gave up: a frame can now be up
    // to 120ms old when a poll finds it, where it used to be 125ms at worst out of a 250ms poll.
    // That is the same staleness in absolute terms and a bigger share of a shorter poll, and it is
    // deliberate — this is the expensive half of the path and the poll is the cheap half, so the
    // cheap half is what was spent. RealtimeRegionState.PollingIsNeverFasterThanFramesAreReadBack
    // is what stops the poll going under this number, where the settle wait would break silently.
    // Internal rather than private so the realtime poll interval can be checked against it:
    // polling faster than frames are read back means two polls in a row see the same pixels,
    // which reads as "the picture has settled" and disables the settle wait without saying so.
    internal static readonly TimeSpan MaxFrameAge = TimeSpan.FromMilliseconds(120);

    // Reading stops this long after the last region asked for anything. Nothing in a running session
    // goes quiet for that long, so in practice this only covers a session being torn down: the
    // capture chain outlives the last poll by a moment, and there is no reason to keep copying a
    // window nobody is watching any more.
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(1);

    // Two is what the samples use and what the model here wants: frames are consumed as they arrive
    // and never held, so a deeper pool would only bank staler frames.
    private const int FrameBuffers = 2;

    // How long a new backend is given to produce one frame with something in it before it is judged
    // unusable. Generous: this is paid once, on a path whose alternative is a session that appears
    // to work and never translates anything — and only in full when the capture is genuinely not
    // producing anything, since the wait now ends on the frame rather than on a timer.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(3000);

    private readonly Func<IntPtr> _resolveSource;
    private readonly IDirect3DDevice _device;
    private readonly WgcSurfaceReader _reader;

    // Guards the whole capture chain below, which is torn down and rebuilt on a resize and on the
    // source window being replaced.
    private readonly object _sync = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IntPtr _hwnd;
    private bool _disposed;

    // The most recent readback and when it was taken. Written by the frame handler, read by every
    // region loop.
    private readonly object _latestLock = new();
    private Bitmap? _latest;
    private long _latestAt;

    // When a region last asked for pixels. What makes the readback demand-driven: capture runs at
    // the window's frame rate, this says whether anyone is still watching.
    private long _lastGrabAt;

    // Raised whenever a frame has been read back. Only the start-up wait listens: it turns waiting
    // for the first frame from a sleep into a wait the frame itself ends — and, on the UI thread
    // that starts a session, into a wait that keeps pumping COM while it runs. A thread sleeping in
    // Thread.Sleep pumps nothing, which is one way a capture that is working fine can look like a
    // capture that never produced a frame.
    private readonly ManualResetEventSlim _frameSignal = new(false);

    private int _framesReceived;
    private int _framesRead;
    private int _rebuilds;
    private long _readbackTicks;
    private int _outsideReported;

    // Which adapter the frames are being asked for on, and the first thing a frame handler failed
    // with, if it failed. Both exist for the log line that a capture producing nothing leaves
    // behind: without them "no frame at all" is the whole of what a report can say.
    private string _adapter = "unknown";
    private string? _firstFault;

    private WgcWindowCaptureBackend(
        Func<IntPtr> resolveSource, IDirect3DDevice device, WgcSurfaceReader reader)
    {
        _resolveSource = resolveSource;
        _device = device;
        _reader = reader;
    }

    /// <summary>
    /// Raised when the source window is gone and could not be found again. The session must end
    /// rather than carry on producing nothing — and must not quietly fall back to grabbing the
    /// desktop, which is where the self-feed came from.
    /// </summary>
    public event EventHandler<string>? SourceLost;

    public string Name => "WgcWindow";

    /// <summary>
    /// Builds a backend around the window <paramref name="resolveSource"/> names, or returns null
    /// when this system cannot capture, when there is no source window, or when the window refuses
    /// to be captured.
    /// </summary>
    /// <param name="resolveSource">
    /// Asked again whenever the source has to be found afresh — the window was closed and reopened,
    /// or a game replaced its window on a mode change. Returns <see cref="IntPtr.Zero"/> for none.
    /// </param>
    public static WgcWindowCaptureBackend? TryCreate(Func<IntPtr> resolveSource) =>
        TryCreate(resolveSource, out _);

    /// <inheritdoc cref="TryCreate(Func{IntPtr})"/>
    /// <param name="outcome">
    /// Why there is no backend, when this returns null. The interface says something different for
    /// each: a window presenting past the compositor is a setting in that application the user can
    /// change, while a capture chain that produced nothing is not.
    /// </param>
    public static WgcWindowCaptureBackend? TryCreate(
        Func<IntPtr> resolveSource, out WindowCaptureOutcome outcome)
    {
        outcome = WindowCaptureOutcome.Refused;

        if (!WgcInterop.IsCaptureSupported())
        {
            Log.Info(
                "Windows.Graphics.Capture is unavailable on this system (Windows {Version}); " +
                "realtime translation has no capture backend at all here",
                Environment.OSVersion.Version);
            return null;
        }

        var hwnd = resolveSource();
        if (hwnd == IntPtr.Zero) return null;

        WgcWindowCaptureBackend? backend;
        (backend, outcome) = TryStart(resolveSource, hwnd, forceWarp: false);
        if (backend is not null) return backend;

        // No frame at all is the one failure a different graphics device can answer. It is what a
        // capture on the wrong adapter looks like — nothing arrives, nothing is refused — so the
        // software adapter, which every machine has and which no display arrangement can put on the
        // wrong side of, is given one try before the mode is declared unavailable.
        //
        // Blank frames are not retried: those say the window is being presented past the compositor,
        // and no device this program creates changes that.
        if (outcome is not WindowCaptureOutcome.NoFrame) return null;

        // Half the wait for the retry. A capture that works at all hands over its first frame at
        // once, and this one is on top of a wait the user has already spent.
        Log.Info("Retrying window capture for hwnd={Hwnd:X} on the software adapter", hwnd);
        (backend, outcome) = TryStart(resolveSource, hwnd, forceWarp: true);
        return backend;
    }

    private static (WgcWindowCaptureBackend? Backend, WindowCaptureOutcome Outcome) TryStart(
        Func<IntPtr> resolveSource, IntPtr hwnd, bool forceWarp)
    {
        var timeout = forceWarp ? FirstFrameTimeout / 2 : FirstFrameTimeout;

        IDirect3DDevice? device = null;
        WgcSurfaceReader? reader = null;
        WgcWindowCaptureBackend? backend = null;
        var outcome = WindowCaptureOutcome.Refused;
        try
        {
            device = WgcInterop.CreateDirect3DDevice(
                MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), forceWarp,
                out var rawDevice, out var context, out var adapter);

            reader = new WgcSurfaceReader(rawDevice, context);
            backend = new WgcWindowCaptureBackend(resolveSource, device, reader) { _adapter = adapter };
            if (!backend.Attach(hwnd)) throw new InvalidOperationException("the window refused capture");

            outcome = backend.WaitForUsableFrame(timeout);
            if (outcome is not WindowCaptureOutcome.Started)
                throw new InvalidOperationException(
                    outcome is WindowCaptureOutcome.NoFrame
                        ? "the window produced no frame at all — see the counters logged above"
                        : "the window produced only blank frames — a game in exclusive fullscreen is " +
                          "the usual cause");

            return (backend, outcome);
        }
        catch (Exception ex)
        {
            // Every reason this can fail is a reason to use another backend, not to end the session:
            // a system with no usable graphics device, a window that closed between resolving and
            // attaching, a policy that forbids capturing it.
            Log.Warn(ex, "Could not start window capture for hwnd={Hwnd:X}", hwnd);
            backend?.Dispose();
            if (backend is null)
            {
                (device as IDisposable)?.Dispose();
                reader?.Dispose();
            }
            return (null, outcome);
        }
    }


    public Bitmap? GrabRegion(Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return null;
        if (Volatile.Read(ref _disposed)) return null;

        // Nothing here waits for a frame. Capture only emits one when the window's content changes,
        // so waiting would block every poll for its full timeout precisely when the window is still
        // — and a still window's last frame is a perfectly true picture of it. The frames keep
        // themselves fresh in the background instead; this says someone is still watching.
        Volatile.Write(ref _lastGrabAt, Stopwatch.GetTimestamp());

        if (!TryGetFrameOrigin(out var origin)) return null;

        lock (_latestLock)
        {
            if (_latest is not { } latest) return null;

            var crop = screenBounds with { X = screenBounds.X - origin.X, Y = screenBounds.Y - origin.Y };
            var visible = Rectangle.Intersect(crop, new Rectangle(0, 0, latest.Width, latest.Height));
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                // The region is no longer over the window at all — it was dragged away, or the
                // window moved out from under it. Nothing to read, and nothing to say four times a
                // second about it.
                if (Interlocked.Exchange(ref _outsideReported, 1) == 0)
                    Log.Warn(
                        "Realtime region {Bounds} lies outside the captured window at {Origin}; " +
                        "further occurrences logged at Debug", screenBounds, origin);
                else
                    Log.Debug("Realtime region {Bounds} lies outside the captured window", screenBounds);
                return null;
            }

            // Always the size that was asked for, with whatever part of the window is available drawn
            // in its own place. A smaller bitmap would move every recognised line relative to the
            // block that displays it.
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

    /// <summary>
    /// Asks Windows to stop drawing its capture indicator around the source window, and says what
    /// happened.
    /// </summary>
    /// <remarks>
    /// The indicator is a coloured frame Windows puts around anything being captured, and it is
    /// there for a good reason — a user should be able to see that something is reading their
    /// screen. For this application it is also a real cost: a realtime session runs for hours over a
    /// game, and that frame is around the game for all of it.
    ///
    /// Removing it is gated twice. The property is Windows 11 21H2 and later, and setting it needs
    /// consent granted against the <c>graphicsCaptureWithoutBorder</c> package capability — which
    /// this application, shipped unpackaged through Velopack, has no identity to declare. So this is
    /// expected to be refused, is written to be refused safely, and returns the refusal in words so
    /// the log and the probe can record what a given system actually said.
    /// </remarks>
    public string TryHideCaptureBorder()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            return "unavailable (needs Windows 11 21H2)";

        try
        {
            var access = GraphicsCaptureAccess
                .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask().GetAwaiter().GetResult();

            if (access != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                return $"denied ({access})";

            lock (_sync)
            {
                if (_session is null) return "no session";
                _session.IsBorderRequired = false;
            }

            return "allowed";
        }
        catch (Exception ex)
        {
            // Refusal arrives as an exception on some systems rather than a status, and the caller
            // is asking a question, not performing an operation that may fail.
            return $"failed ({ex.GetType().Name}: {ex.Message.Trim()})";
        }
    }

    public string DescribeActivity()
    {
        var reads = Volatile.Read(ref _framesRead);
        var averageMs = reads == 0
            ? 0
            : Stopwatch.GetElapsedTime(0, Volatile.Read(ref _readbackTicks)).TotalMilliseconds / reads;

        return $"hwnd={_hwnd:X} received={Volatile.Read(ref _framesReceived)} read={reads} " +
               $"avgReadback={averageMs:F1}ms rebuilds={Volatile.Read(ref _rebuilds)}";
    }

    // ── Capture chain ────────────────────────────────────────────────────────────────────────────

    /// <summary>Points the capture at a window, replacing whatever it was pointed at before.</summary>
    private bool Attach(IntPtr hwnd)
    {
        lock (_sync)
        {
            if (_disposed) return false;

            DetachLocked();

            var item = WgcInterop.CreateItemForWindow(hwnd);
            if (item is null) return false;

            _item = item;
            _hwnd = hwnd;
            item.Closed += OnItemClosed;

            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, _poolSize);
            _pool.FrameArrived += OnFrameArrived;

            _session = _pool.CreateCaptureSession(item);
            _session.StartCapture();

            Log.Info(
                "Realtime capture backend WgcWindow attached: hwnd={Hwnd:X} itemSize={Width}x{Height} " +
                "on {Adapter}",
                hwnd, item.Size.Width, item.Size.Height, _adapter);
            return true;
        }
    }

    /// <summary>
    /// Free-threaded, called by the capture stack at the window's own frame rate — which may be 144
    /// times a second over a game. So it does as little as possible: it takes the frame to keep the
    /// pool turning over, and only reads one back when a poll has asked for one.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        SizeInt32? resizeTo = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            Interlocked.Increment(ref _framesReceived);

            // The frame says how much of its texture is the window, and that is not the size the
            // pool was created for: a window's capture item reports the whole window rectangle,
            // while the content inside it is the visible frame — 2578x1458 against 2560x1440 on a
            // maximised window here. It also changes for real when the window is resized or moved
            // to a monitor at another scale.
            //
            // The pool is resized to match, and the frame in hand is still read. Dropping it looked
            // harmless and was not: capture only produces frames when the window's content changes,
            // so on a still window the frame dropped this way was the only one that would ever
            // arrive, and the capture appeared to produce nothing at all.
            //
            // The resize itself is deferred to the end of this handler, after the frame has been
            // read and released. Recreating a pool while still holding one of its frames is asking
            // it to reclaim a buffer this thread has not given back, and the wait for that can only
            // end on this thread — which is inside the wait. That deadlock takes the capture with
            // it, silently and on the very first frame, since a window's content size and its item
            // size routinely differ by the invisible resize border.
            var content = frame.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                resizeTo = content;

            // The first frame is always read, and that is not an optimisation detail: capture emits
            // one frame when the session starts and one whenever the content changes, so over a
            // paused video or a menu that is not animating those are the same frame. Letting it go
            // means the capture appears to produce nothing at all.
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
            // One bad frame must not take the capture down — the next one is milliseconds away, and
            // a poll that finds no fresh frame simply reads the one before it.
            //
            // The first one is kept, though. A capture that produces nothing because every frame
            // throws is indistinguishable, from the outside, from one where no frame ever arrives,
            // and the difference is the whole of what a report about it would need to say.
            Interlocked.CompareExchange(ref _firstFault, $"{ex.GetType().Name}: {ex.Message.Trim()}", null);
            Log.Debug(ex, "Realtime capture dropped a frame");
        }
        finally
        {
            // Outside the try above, so the frame it was decided for has been disposed by now.
            if (resizeTo is { } size) Recreate(size);
        }
    }

    private SizeInt32 _poolSize;

    private void Recreate(SizeInt32 size)
    {
        lock (_sync)
        {
            if (_disposed || _pool is null) return;

            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, size);
            _poolSize = size;
            Interlocked.Increment(ref _rebuilds);
            Log.Debug("Realtime capture resized to {Width}x{Height}", size.Width, size.Height);
        }
    }

    /// <summary>
    /// The source window closed. A window that is merely being replaced — a game changing display
    /// mode, an application restarting — can be found again, so the source is re-resolved once
    /// before the session is told it has lost its source.
    /// </summary>
    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (Volatile.Read(ref _disposed)) return;

        Log.Warn("Realtime capture source window {Hwnd:X} closed", _hwnd);

        try
        {
            var replacement = _resolveSource();
            if (replacement != IntPtr.Zero && Attach(replacement))
            {
                Log.Info("Realtime capture re-attached to hwnd={Hwnd:X}", replacement);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not re-attach realtime capture after the source window closed");
        }

        // Deliberately no fallback to grabbing the desktop. That path can only be safe where the
        // overlays are excluded from capture, and arriving at it silently is how a session ends up
        // recognising its own subtitles.
        SourceLost?.Invoke(this, LocalizationService.Get("S.Realtime.CaptureSourceLost"));
    }

    /// <summary>
    /// Waits for this backend to produce one frame with something in it, and says whether it did.
    /// </summary>
    /// <remarks>
    /// A game in exclusive fullscreen is why this exists, and the way it fails is the reason it has
    /// to be checked rather than assumed. The window still has a handle, capture still attaches,
    /// frames still arrive at around 37 a second — measured — and every one of them is a single flat
    /// colour, because the game is presenting to the display through its own swap chain and there is
    /// no composited window surface for DWM to hand over. Nothing in the counters looks wrong:
    /// <c>received=296 read=36 rebuilds=0</c> is what a healthy capture looks like too.
    ///
    /// Left unchecked that becomes a session that starts, reports nothing amiss, and never produces
    /// a subtitle, with no line in the log a user could be pointed at. Caught here it is one failed
    /// construction and a refusal that names 螢幕擷取 as the answer — which captures the same
    /// fullscreen game perfectly well, because the screen is exactly where that content does exist.
    ///
    /// The cost of being wrong is a game whose opening frame is genuinely one flat colour being
    /// refused here. That is a frame with nothing to translate in it either, and the user is told
    /// which mode to try instead rather than left watching a session produce nothing.
    /// </remarks>
    private WindowCaptureOutcome WaitForUsableFrame(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var sawFrame = false;

        while (true)
        {
            // The readback is demand-driven, and nothing has asked yet.
            Volatile.Write(ref _lastGrabAt, Stopwatch.GetTimestamp());

            lock (_latestLock)
            {
                if (_latest is { } latest)
                {
                    sawFrame = true;
                    if (!IsBlank(latest)) return WindowCaptureOutcome.Started;
                }
            }

            var left = timeout - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) break;

            // On the handle rather than through ManualResetEventSlim.Wait, and not Thread.Sleep.
            // This is called from the thread that starts a session, which on a WPF application is
            // the UI thread — an STA thread, whose blocking waits must keep pumping COM for anything
            // marshalled through it to arrive. A sleep here would be a capture stack held shut for
            // the whole of its own start-up timeout.
            _frameSignal.Wait(TimeSpan.FromMilliseconds(Math.Min(50, left.TotalMilliseconds)));
            _frameSignal.Reset();
        }

        Log.Warn(
            sawFrame
                ? "Realtime window capture of hwnd={Hwnd:X} produced only blank frames; the window is " +
                  "not being composited — a game in exclusive fullscreen is the usual cause " +
                  "({Activity}, adapter: {Adapter})"
                : "Realtime window capture of hwnd={Hwnd:X} produced no frame at all " +
                  "({Activity}, adapter: {Adapter})",
            _hwnd, DescribeActivity(), _adapter);

        if (_firstFault is { } fault)
            Log.Warn("Realtime window capture of hwnd={Hwnd:X} first failed with {Fault}", _hwnd, fault);

        return sawFrame ? WindowCaptureOutcome.Blank : WindowCaptureOutcome.NoFrame;
    }

    /// <summary>
    /// Whether a frame is one flat colour, judged from a grid of samples rather than every pixel —
    /// the answer is either immediate or not worth the whole image.
    /// </summary>
    private static bool IsBlank(Bitmap frame)
    {
        var first = frame.GetPixel(0, 0).ToArgb();

        for (var y = 1; y < 8; y++)
        {
            for (var x = 1; x < 8; x++)
            {
                var sample = frame.GetPixel(frame.Width * x / 8, frame.Height * y / 8);
                if (sample.ToArgb() != first) return false;
            }
        }

        return true;
    }

    private bool HasFrame()
    {
        lock (_latestLock) return _latest is not null;
    }

    /// <summary>
    /// Where the captured frame's top-left corner sits on the screen, in physical pixels.
    /// </summary>
    /// <remarks>
    /// The one measurement the crop depends on, and the one Windows will not simply state. A
    /// window's capture surface is one of two rectangles: the window rect, which since Vista
    /// includes an invisible resize border several pixels wide, or the extended frame bounds, which
    /// do not. Guessing wrong shifts every region by that border — a few pixels, always in the same
    /// direction, and quite enough to cut the top off a line of subtitles.
    ///
    /// So neither is assumed. The frame's own size says which rectangle it was taken from, and that
    /// rectangle's corner is the origin. Re-read every poll because both of them move: the window
    /// can be dragged, and dragged to a monitor at another scale.
    /// </remarks>
    private bool TryGetFrameOrigin(out Point origin)
    {
        origin = Point.Empty;
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero) return false;

        int width, height;
        lock (_latestLock)
        {
            if (_latest is not { } latest) return false;
            width = latest.Width;
            height = latest.Height;
        }

        if (!GetWindowRect(hwnd, out var window)) return false;

        var hasExtended = DwmGetWindowAttribute(
            hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var extended, Marshal.SizeOf<RECT>()) == 0;

        if (hasExtended
            && extended.Right - extended.Left == width
            && extended.Bottom - extended.Top == height)
        {
            origin = new Point(extended.Left, extended.Top);
            return true;
        }

        origin = new Point(window.Left, window.Top);
        return true;
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
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DetachLocked();
        }

        // After the chain is down, so a frame in flight cannot read a bitmap being disposed.
        lock (_latestLock)
        {
            _latest?.Dispose();
            _latest = null;
        }

        _frameSignal.Dispose();
        (_device as IDisposable)?.Dispose();
        _reader.Dispose();
    }

    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out RECT value, int size);
}
