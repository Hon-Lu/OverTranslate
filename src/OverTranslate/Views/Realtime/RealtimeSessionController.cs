using System.Windows;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;

namespace OverTranslate.Views.Realtime;

/// <param name="ScreenBounds">
/// The one screen the session may use, in physical pixels. Realtime translation is deliberately
/// single-screen: every block then shares one DPI and one capture path, and the loop never has to
/// reason about a window that straddles two monitors at different scales.
/// </param>
/// <param name="Provider">
/// The engine this session translates with. Carried on the request rather than read from settings
/// because 即時翻譯 keeps its choices to itself — see RealtimePage.
/// </param>
/// <param name="TextColor">
/// Subtitle colours as "#RRGGBB", carried here like everything else this sitting runs with. They are
/// read from the settings file rather than chosen per session — see AppSettings — but they still
/// arrive on the request, because a session cannot be asked to change them halfway: reaching the
/// page that sets them means the shell window, and a running session has hidden it.
/// </param>
/// <param name="ScrimOpacity">
/// How opaque the band behind the text is, 0–100. Travels with the colours for the same reason and
/// under the same rule.
/// </param>
/// <param name="NaturalBackground">
/// Whether to repair the picture under the source line instead of drawing the band — 顯示外觀 →
/// 進階選項. Off unless asked for; see AppSettings for why that is the honest default. Travels here
/// under the same rule as the colours: the page that sets it is behind the shell window a running
/// session has hidden.
/// </param>
/// <param name="SampleSourceTextColor">
/// Whether to draw the translation in a colour read off the source line rather than in
/// <paramref name="TextColor"/>. Independent of <paramref name="NaturalBackground"/> — the two fail
/// in different places, so either can be had without the other.
/// </param>
/// <param name="CaptureMode">Which of the two sources this session reads.</param>
/// <param name="SourceWindow">
/// The window to read in <see cref="RealtimeCaptureMode.Window"/>, and
/// <see cref="IntPtr.Zero"/> otherwise. A handle, so it dies with the window — which is the point:
/// the session ends with it rather than quietly reading something else.
/// </param>
/// <param name="Border">Whether a border is drawn around the band — 顯示外觀 → 邊框.</param>
/// <param name="FixedBorderColor">
/// The border's colour as "#RRGGBB", or null for a colour per group chosen from its source text.
/// </param>
public sealed record RealtimeStartRequest(
    System.Drawing.Rectangle ScreenBounds,
    int MaxBlocks,
    string SourceLanguage,
    string TargetLanguage,
    Models.TranslationProvider Provider,
    string TextColor,
    string ScrimColor,
    int ScrimOpacity,
    bool NaturalBackground,
    bool SampleSourceTextColor,
    Models.RealtimeCaptureMode CaptureMode = Models.RealtimeCaptureMode.Screen,
    IntPtr SourceWindow = default,
    bool Border = false,
    string? FixedBorderColor = null);

/// <summary>
/// Owns a realtime session end to end: the edit layer, the per-block overlays, the floating control
/// and the polling loop, plus the shell window it hid to get out of the way.
/// </summary>
/// <remarks>
/// A single instance, like the capture session in <see cref="MainWindow"/> and for the same reason:
/// these windows are Topmost and cover the screen, so a second set would be unclosable furniture on
/// top of the first. It runs on the shared engines in <see cref="AppServices"/> rather than creating
/// its own — a second <see cref="OcrService"/> would load a second copy of the ONNX runtime, and the
/// two features would then fight over the CPU instead of queueing.
/// </remarks>
internal sealed class RealtimeSessionController
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static RealtimeSessionController Instance { get; } = new();

    private RealtimeStartRequest? _request;
    private RealtimeControlWindow? _control;
    private RealtimeEditWindow? _edit;
    private RealtimeTranslationSession? _session;
    // Owned here rather than by the session, because it outlives a pause and is torn down by the
    // same two things that tear down the overlays it was built around: going back to edit mode, and
    // ending the session.
    private IRealtimeCaptureBackend? _capture;
    private GlobalEscapeHook? _escapeHook;
    private Window? _hiddenShell;

    private readonly Dictionary<int, RealtimeBlockWindow> _blockWindows = [];
    // Every window this session draws, as handles, for a capture backend that has to leave them out
    // of its frames — see RefreshOverlayHandles. Written on the UI thread, read on the polling one.
    private IReadOnlyList<IntPtr> _overlayHandles = [];
    private List<RealtimeBlockPlacement> _blocks = [];
    // UI updates are dispatched asynchronously. Carrying the session's generation here prevents an
    // update queued before a pause from restoring text after the screen was cleared.
    private int _visibleGeneration;

    /// <summary>
    /// The layout the last sitting ended with, offered back to the next one.
    /// </summary>
    /// <remarks>
    /// Ending a session is not usually the user finishing — it is them going back to change the
    /// language, the engine or a colour, all of which live on a page a running session has hidden.
    /// Redrawing three blocks by hand every time they touch one of those is the tax that made this
    /// worth keeping, and the modes go with the rectangles because re-answering 字幕 / 對話 vs 遊戲 / UI for
    /// each block is the same tax again. The writing direction rides along for the same reason.
    ///
    /// Held here and not written to the settings file, which is the line RealtimePage draws for
    /// everything else about a sitting: this survives 結束即時翻譯 and nothing more. The screen it
    /// was drawn on is kept alongside it because these are physical pixels on one monitor — offered
    /// back for a different screen, or the same one at a different resolution, they would be blocks
    /// the user never drew, in places nothing is.
    /// </remarks>
    private RememberedBlocks? _remembered;

    private sealed record RememberedBlocks(
        System.Drawing.Rectangle Screen, IReadOnlyList<RealtimeBlockPlacement> Blocks);

    /// <summary>
    /// Keeps the layers at the front of the topmost band — see <see cref="AlwaysOnTop"/> for why
    /// they slide behind other topmost windows without it, and why they cannot simply activate
    /// themselves instead.
    /// </summary>
    /// <remarks>
    /// A second is short enough that being covered is a blink rather than a state the user has to
    /// do something about, and long enough that the cost is a handful of SetWindowPos calls — no
    /// redraw, no allocation, nothing that touches the session.
    /// </remarks>
    private readonly DispatcherTimer _stayOnTop = new() { Interval = TimeSpan.FromSeconds(1) };

    private RealtimeSessionController()
    {
        _stayOnTop.Tick += (_, _) =>
        {
            // The control bar last, so it ends up above the block layers it may overlap.
            foreach (var block in _blockWindows.Values) AlwaysOnTop.Reassert(block);
            if (_edit is { } edit) AlwaysOnTop.Reassert(edit);
            if (_control is { } control) AlwaysOnTop.Reassert(control);
        };
    }

    /// <summary>Raised when the session starts or ends, so the page can re-render its controls.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised when a session ended on its own rather than because the user said so, carrying the
    /// message to show them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StateChanged"/> because the two have different audiences. That one
    /// is for anything drawing this feature's controls, and fires whoever ended the session. This
    /// one exists only for the case where nobody asked — the user has no reason to be looking at
    /// this application, so it needs a listener that can reach them outside it.
    /// </remarks>
    public event EventHandler<string>? SessionEnded;

    public bool IsActive => _control != null;

    /// <summary>
    /// True while the polling loop is running, as opposed to the user framing blocks. The edit layer
    /// is the whole difference: <see cref="EnterEditMode"/> creates it and <see cref="StartTranslating"/>
    /// closes it, so its absence during a session is what "translating" means here.
    /// </summary>
    public bool IsTranslating => IsActive && _edit is null;

    /// <summary>
    /// Starts a session and drops straight into edit mode. <paramref name="shellToHide"/> is put
    /// away for the duration and brought back by <see cref="Stop"/> — the user is about to frame
    /// something on the screen the shell is sitting on.
    /// </summary>
    public void Start(RealtimeStartRequest request, Window? shellToHide)
    {
        if (IsActive) return;

        Log.Info(
            "Realtime session starting on screen {Screen}, max {Max} block(s), {Src}->{Tgt}",
            request.ScreenBounds, request.MaxBlocks, request.SourceLanguage, request.TargetLanguage);

        _request = request;
        _blocks = _remembered is { } kept && kept.Screen == request.ScreenBounds
            ? [.. kept.Blocks]
            : [];
        _hiddenShell = shellToHide;
        _hiddenShell?.Hide();

        // The same two engines the capture side uses — see AppServices. There is no fallback to get
        // wrong any more: nothing here has to find a window first, so there is no path on which a
        // second inference runtime could be built by accident.
        _session = new RealtimeTranslationSession(AppServices.Ocr, AppServices.Translation);
        Volatile.Write(ref _visibleGeneration, 0);
        _session.RegionUpdated += OnRegionUpdated;
        _session.Failed += OnSessionFailed;
        _session.BusyChanged += OnBusyChanged;

        var control = new RealtimeControlWindow(request.ScreenBounds);
        control.StartRequested += (_, _) => StartTranslating();
        control.EditRequested += (_, _) => EnterEditMode();
        control.CloseRequested += (_, _) => Stop();
        control.PauseToggleRequested += (_, _) => TogglePause();
        // Wired here rather than in EnterEditMode, which runs again on every trip back into framing
        // and would stack a fresh handler each time. The layer it talks to is whichever one is up:
        // there is none while translating, and the button that raises this is not on screen then.
        control.CrosshairToggleRequested += (_, enabled) => _edit?.SetCrosshairEnabled(enabled);
        _control = control;
        control.Show();
        RefreshOverlayHandles();

        EnterEditMode();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts every layer back at the front of the topmost band, on demand.
    /// </summary>
    /// <remarks>
    /// The way out of the one case the timer cannot win on its own: another topmost window that
    /// re-asserts itself just as persistently, leaving the control bar unreachable and the session
    /// unstoppable — the running session deliberately has no global Esc, because it would swallow
    /// the key from the game underneath. Reached by clicking the tray icon, which does nothing
    /// useful during a session anyway: it opens the translation window, and that window cannot be
    /// used while the layers own the screen.
    /// </remarks>
    public void BringToFront()
    {
        if (!IsActive) return;

        foreach (var block in _blockWindows.Values) AlwaysOnTop.Reassert(block);
        if (_edit is { } edit) AlwaysOnTop.Reassert(edit);
        if (_control is { } control)
        {
            AlwaysOnTop.Reassert(control);
            // Says the click did something even when nothing was covering the bar, which is the
            // more common case: a user who tries this is looking for a sign of life.
            control.ShowMessage(LocalizationService.Get("S.Realtime.MovedToFront"));
        }

        Log.Info("Realtime layers re-asserted on top by request");
    }

    /// <summary>
    /// Stops or restarts the watching, without ending the session — see
    /// <see cref="RealtimeTranslationSession.Pause"/> for what a pause gives back.
    /// </summary>
    /// <remarks>
    /// One entry point for both directions, because both callers are toggles: one button and one
    /// shortcut, each pressed by a user who can see on the bar which of the two states they are in.
    ///
    /// Does nothing while the user is framing blocks: there is nothing running to pause, and the
    /// caller (the capture shortcut) is deliberately silent in that mode rather than explaining a
    /// rule about a feature the user has not started.
    /// </remarks>
    /// <returns>Whether there was a running session to toggle.</returns>
    public bool TogglePause()
    {
        if (!IsTranslating || _session is null || _control is not { } control) return false;

        if (_session.IsPaused)
        {
            // Before the loops start, so the first result cannot arrive to a bar still saying 已暫停.
            control.SetPaused(false);
            _session.Resume();
            return true;
        }

        var pauseGeneration = _session.Pause();
        Volatile.Write(ref _visibleGeneration, pauseGeneration);

        // The whole point of the feature: 暫停 takes the words and their scrims off the screen
        // rather than freezing them there, because a frozen translation over a scene that has moved
        // on is worse than none. Updates queued before this carry an older generation and
        // OnRegionUpdated refuses to put them back.
        foreach (var window in _blockWindows.Values)
            window.SetLines([]);

        control.SetPaused(true);
        return true;
    }

    public void Stop()
    {
        if (!IsActive) return;

        Log.Info("Realtime session ending");

        _stayOnTop.Stop();
        DisposeEscapeHook();

        if (_session != null)
        {
            _session.RegionUpdated -= OnRegionUpdated;
            _session.Failed -= OnSessionFailed;
            _session.BusyChanged -= OnBusyChanged;

            // The one press that means "done with this": the recogniser's memory goes back here
            // rather than on 暫停, which means "not now" and is followed by 繼續 wanting it again.
            _session.Stop(releaseRecogniser: true);
            _session = null;
        }

        DisposeCapture();
        CloseBlockWindows();
        CloseEditWindow();

        CloseWindow(_control, nameof(RealtimeControlWindow));
        _control = null;

        // Kept before the working copy is dropped — see RememberedBlocks for why this one thing
        // outlives the session while everything else here is torn down.
        _remembered = _request is { } ended && _blocks.Count > 0
            ? new RememberedBlocks(ended.ScreenBounds, _blocks)
            : null;

        _blocks = [];
        _request = null;

        RestoreShell();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops the layout kept from the last sitting, so the next one starts on an empty screen.
    /// </summary>
    /// <remarks>
    /// Called when the block count changes, and when the translation window closes. Both are the
    /// same statement: what was drawn was drawn for a set-up that no longer exists. The count is
    /// not checked when the blocks are offered back, deliberately — the user asked for them to go
    /// the moment the count moved, and 2 → 3 → 2 leaving old blocks behind would be a set-up they
    /// had told the program to forget reappearing because they changed their mind twice.
    ///
    /// Only the memory. A session that happens to be running keeps its own blocks and carries on:
    /// closing the shell window has never been a request to end a session, and this does not make
    /// it one.
    /// </remarks>
    public void ForgetBlocks() => _remembered = null;

    // ── Modes ────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (_request is not { } request || _control is not { } control) return;

        // Before the block windows go: a backend built around a set of overlays is only valid while
        // those overlays are the ones on screen, and edit mode is where the user changes them.
        _session?.Stop();
        DisposeCapture();
        CloseBlockWindows();
        CloseEditWindow();

        // Before the layer that covers the screen goes up. The user named this window in a list,
        // which they can do with it buried behind everything else — and then framing over it would
        // start with them putting away the layer they just asked for to go and find it.
        if (request.CaptureMode is Models.RealtimeCaptureMode.Window)
            SourceWindowFocus.Raise(request.SourceWindow);

        // Read here rather than carried on the request: the user changes it from inside this layer,
        // and every later trip through edit mode should open on the answer they gave last.
        var settings = SettingsService.Instance;

        var edit = new RealtimeEditWindow(
            request.ScreenBounds, _blocks, request.MaxBlocks, settings.Current.Realtime.GuidanceExpanded);
        edit.BlocksChanged += (_, _) =>
        {
            _blocks = [.. edit.GetPhysicalBlocks()];
            control.SetBlockCount(_blocks.Count, request.MaxBlocks);
        };
        // Written on the press rather than at the end of the session: a session over a full-screen
        // game is as likely to end with the machine being shut down as with 結束即時翻譯.
        edit.GuidanceExpandedChanged += (_, expanded) =>
        {
            if (settings.Current.Realtime.GuidanceExpanded == expanded) return;
            settings.Current.Realtime.GuidanceExpanded = expanded;
            settings.Save();
        };
        edit.LimitReached += (_, _) =>
            control.ShowMessage(LocalizationService.Format("S.Realtime.TooManyBlocks", request.MaxBlocks));
        _edit = edit;
        edit.Show();
        RefreshOverlayHandles();

        // Ownership, not a Topmost nudge: the edit layer covers the whole screen, so any moment it
        // is above the control the user's clicks on the bar land on the drawing canvas instead —
        // which reads as the buttons having stopped working. Re-asserting Topmost puts the control
        // back on top once; an owned window is guaranteed to stay above its owner no matter what is
        // clicked afterwards. CloseEditWindow clears this again before closing the owner, or the
        // control would be closed along with it.
        control.Owner = edit;

        control.SetMode(RealtimeControlMode.Edit);
        control.SetBlockCount(_blocks.Count, request.MaxBlocks);
        // Belt and braces alongside the ownership above: cheap, and the failure it guards against is
        // a control bar the user cannot reach at all.
        control.BringToFront();

        _stayOnTop.Start();

        // Only while editing. The hook swallows Esc process-wide, and a translating session can run
        // for hours next to an application that needs its own Esc key.
        DisposeEscapeHook();
        _escapeHook = GlobalEscapeHook.Install(Stop);
    }

    private void StartTranslating()
    {
        if (_request is not { } request || _control is not { } control || _session is null) return;

        if (_blocks.Count == 0)
        {
            control.ShowMessage(LocalizationService.Get("S.Realtime.NeedOneBlock"));
            return;
        }

        DisposeEscapeHook();
        CloseEditWindow();

        var regions = _blocks
            .Select((block, index) =>
                new RealtimeRegion(index, block.Bounds, block.Mode, block.Orientation))
            .ToList();

        // Resolved before the overlays are shown. They are click-through and belong to this process,
        foreach (var region in regions)
        {
            var window = new RealtimeBlockWindow(
                region.Id, region.Bounds, GrabUnderlying, request.SourceLanguage, request.TargetLanguage,
                request.TextColor, request.ScrimColor, request.ScrimOpacity,
                request.NaturalBackground, request.SampleSourceTextColor, region.Mode,
                request.Border, request.FixedBorderColor, region.Orientation);
            _blockWindows[region.Id] = window;
            window.Show();
        }

        // Before the backend, which asks for this list as it starts: the overlays are up, and the
        // first frame it accepts must already be composed without them.
        RefreshOverlayHandles();

        // Built here and not in Start: the layers exist and have handles now, which is the earliest
        // a backend can be asked whether it can keep them out of frame — and the last moment before
        // the loop would begin reading the screen they are drawn on.
        var capture = CreateCaptureBackend(request, out var refusal);
        if (capture is null)
        {
            EnterEditMode();

            // Sticky: this one names an action the user has to leave this screen to take, so it has
            // to still be there when they come back to it. See RealtimeControlWindow.ShowMessage.
            control.ShowMessage(
                LocalizationService.Get(refusal), RealtimeMessageKind.Failure, sticky: true);
            return;
        }

        _capture = capture;

        // Set before the mode switch renders, so the capsule appears with the pair already in it
        // rather than showing the placeholder for a frame.
        control.SetLanguages(request.SourceLanguage, request.TargetLanguage);
        control.SetMode(RealtimeControlMode.Running);
        control.BringToFront();
        _stayOnTop.Start();

        _session.Start(regions, request.SourceLanguage, request.TargetLanguage, request.Provider, capture);
    }

    /// <summary>
    /// Builds the backend the user asked for, or names why it cannot be had.
    /// </summary>
    /// <remarks>
    /// One mode, one backend, and no path between them. The policy this replaces tried window
    /// capture first and fell back to the desktop grab when the source could not be inferred, which
    /// was the right shape while the source was a guess — but it meant the answer to "what is being
    /// read" depended on what happened to be under the blocks at the moment 開始翻譯 was pressed,
    /// and the user could neither see that answer nor choose it.
    ///
    /// Now they choose, which makes falling back worse than refusing: someone who picked a window
    /// and silently got the whole screen has been handed the other feature, carrying the other
    /// feature's requirement — a system new enough to compose a monitor without this application's
    /// overlays — attached to a choice they never made. So each mode either produces its own backend
    /// or says what to do instead.
    /// </remarks>
    /// <param name="refusal">The string key to show the user, when this returns null.</param>
    private IRealtimeCaptureBackend? CreateCaptureBackend(
        RealtimeStartRequest request, out string refusal)
    {
        // Once per run of translating, and the first thing to look for in any report about this
        // feature: which capture path the user was actually on, and what their system offered.
        Log.Info("Realtime capture capability: {Capability}", WgcCapability.Describe());

        refusal = "S.Realtime.ScreenCaptureUnavailable";

        if (request.CaptureMode is Models.RealtimeCaptureMode.Window)
            return CreateWindowCapture(request.SourceWindow, out refusal);

        return CreateScreenCapture(request, out refusal);
    }

    /// <summary>
    /// The whole screen as a monitor capture with this session's overlays excluded, or null when
    /// this system cannot do that — in which case the user is sent to 視窗擷取.
    /// </summary>
    /// <remarks>
    /// One backend, one requirement, and no second choice. This used to fall back to grabbing the
    /// composited desktop on systems with no window exclusion list, with the overlays asked to hide
    /// themselves via <c>WDA_EXCLUDEFROMCAPTURE</c>. That path is gone (#105), and dropping it was
    /// the point rather than a side effect: it was the one capture path whose isolation could not be
    /// checked from inside the program — display affinity fails silently on a layered WPF window, on
    /// every Windows before 11 24H2, which is how #94 went unnoticed for as long as it did.
    ///
    /// What that costs is the band of systems between 24H2 and the exclusion list, who lose 螢幕擷取.
    /// They are not left without the feature: 視窗擷取 needs neither API — its isolation is that the
    /// source is somebody else's window — so it works on every system with WGC at all, and the
    /// refusal below is written to send them there rather than to explain a mechanism they cannot
    /// act on.
    /// </remarks>
    private IRealtimeCaptureBackend? CreateScreenCapture(RealtimeStartRequest request, out string refusal)
    {
        refusal = "S.Realtime.ScreenCaptureUnavailable";

        // Normally unreachable: 擷取來源 does not offer this mode on a machine that answers false,
        // so arriving here means a settings file that named it, or a capability that changed under a
        // page left open. Kept because the interface offering a mode is not what makes it safe.
        if (!WgcCapability.SupportsScreenMode)
        {
            Log.Info(
                "Realtime screen capture unavailable: {Capability}", WgcCapability.Describe());
            return null;
        }

        return CreateMonitorCapture(request.ScreenBounds);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
    private WgcMonitorCaptureBackend? CreateMonitorCapture(System.Drawing.Rectangle screenBounds)
    {
        // The monitor is resolved again on every re-attach rather than captured once: a display that
        // was unplugged and plugged back in, or that changed mode, is a different HMONITOR for the
        // same rectangle.
        var backend = WgcMonitorCaptureBackend.TryCreate(
            () => WgcMonitorCaptureBackend.MonitorFor(screenBounds),
            () => Volatile.Read(ref _overlayHandles));

        if (backend is null) return null;

        backend.SourceLost += OnCaptureSourceLost;
        return backend;
    }

    /// <summary>
    /// Records the handles of every window this session draws, for the capture backend to keep out
    /// of its frames.
    /// </summary>
    /// <remarks>
    /// Taken here and handed over as a snapshot because these are WPF windows: their handles may
    /// only be read on the thread that owns them, and the backend asks from the polling thread. Kept
    /// in a stable order — blocks by id, then the bar, then the edit layer — so the backend can tell
    /// "the same windows as last time" from "a window appeared" by comparing the two lists.
    ///
    /// Called wherever one of those windows is created or closed, which is every time the answer can
    /// change. Missing a call is not a silent hazard: the backend compares this list on every poll,
    /// so a window that appeared without one is still excluded within a poll — and until the frames
    /// catch up with the new list, nothing is read at all.
    /// </remarks>
    private void RefreshOverlayHandles()
    {
        var handles = new List<IntPtr>();

        foreach (var id in _blockWindows.Keys.Order())
            AddHandle(handles, _blockWindows[id]);

        AddHandle(handles, _control);
        AddHandle(handles, _edit);

        Volatile.Write(ref _overlayHandles, handles);

        static void AddHandle(List<IntPtr> handles, Window? window)
        {
            if (window is null) return;

            // Zero until the window has been shown, which is not worth guarding elsewhere: an
            // unrealised window is drawing nothing for a capture to pick up.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) handles.Add(hwnd);
        }
    }

    /// <summary>
    /// The picture under a rectangle of the screen, taken from the session's capture backend.
    /// </summary>
    /// <remarks>
    /// The single door through which anything in a running session may see the screen. It is handed
    /// to every block overlay so the natural-background repair reads what is under it rather than
    /// photographing itself (#99) — and the point of routing it through here is that it can no
    /// longer be correct only on some Windows versions. A backend either has an isolated frame or
    /// has none; there is no third answer it could give that quietly includes our own subtitles.
    ///
    /// Resolved on each call rather than captured, because a block overlay is created before the
    /// backend is — the overlays have to exist and have handles before a monitor capture can be told
    /// to leave them out. Null before then, and again from the moment the backend is disposed, which
    /// is a whole tick before the overlays close.
    /// </remarks>
    private System.Drawing.Bitmap? GrabUnderlying(System.Drawing.Rectangle screenBounds) =>
        _capture?.GrabRegion(screenBounds);

    /// <summary>
    /// Window capture over the handle the user picked, or the reason it is not available.
    /// </summary>
    /// <remarks>
    /// A refusal per reason, because each has a different answer. A system without window capture
    /// has no realtime capture at all — 螢幕擷取 is built on the same API and needs more of it, not
    /// less — so that one states the fact and stops. A window that closed between the picker and
    /// this call needs the list refreshed. A window presenting past the compositor — a
    /// game in exclusive fullscreen, see <c>WgcWindowCaptureBackend.WaitForUsableFrame</c> — has a
    /// display setting the user can change. And a capture that produced nothing at all is none of
    /// those: it is this machine's capture chain, which the user cannot act on, so that one names
    /// the log instead of an action.
    ///
    /// None of them sends the user to 螢幕擷取. Someone who chose 視窗擷取 either has a reason to
    /// avoid the other mode or is on a machine that does not offer it — this report came from the
    /// second kind — so the way forward that is always theirs to take is the display mode of the
    /// application they are translating, and that is what every one of these says.
    ///
    /// The last two used to share the fullscreen message, which is how a report of it arrived —
    /// a Windows 10 user told to leave exclusive fullscreen by a game that was already windowed.
    /// </remarks>
    private WgcWindowCaptureBackend? CreateWindowCapture(IntPtr hwnd, out string refusal)
    {
        refusal = "S.Realtime.WindowCaptureFailed";

        if (!WgcCapability.IsCaptureSupported)
        {
            refusal = "S.Realtime.WindowCaptureUnsupported";
            Log.Info("Realtime window capture unavailable: this system does not support it");
            return null;
        }

        if (!CaptureWindowList.StillAvailable(hwnd))
        {
            refusal = "S.Realtime.WindowCaptureGone";
            Log.Info("Realtime window capture unavailable: hwnd={Hwnd:X} is gone or minimised", hwnd);
            return null;
        }

        return CreateWindowBackend(hwnd, ref refusal);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
    private WgcWindowCaptureBackend? CreateWindowBackend(IntPtr hwnd, ref string refusal)
    {
        // The same handle every time it is asked, which is what turns the backend's re-attach into a
        // no-op and lets the closing of that window end the session. Searching for a replacement is
        // the alternative, and it would have to guess which new window is the same application —
        // guessing is what this mode exists to stop doing.
        var backend = WgcWindowCaptureBackend.TryCreate(() => hwnd, out var outcome);
        if (backend is null)
        {
            refusal = outcome switch
            {
                WindowCaptureOutcome.Blank => "S.Realtime.WindowCaptureFullscreen",
                WindowCaptureOutcome.NoFrame => "S.Realtime.WindowCaptureNoFrame",
                _ => "S.Realtime.WindowCaptureFailed"
            };
            return null;
        }

        backend.SourceLost += OnCaptureSourceLost;
        return backend;
    }

    /// <summary>
    /// The captured window is gone. The session goes with it.
    /// </summary>
    /// <remarks>
    /// It used to drop back to edit mode with the blocks intact, which was the right answer while
    /// the source was inferred from those blocks — pointing them at something else was a real thing
    /// to do next. With the window named up front there is nothing left to re-aim: the one thing
    /// this session was for is closed, and leaving the user in a full-screen editing layer over a
    /// window that no longer exists is furniture, not a choice.
    ///
    /// So it ends, which also puts the shell back. The notice goes out through
    /// <see cref="SessionEnded"/> rather than the control bar, because the bar is one of the things
    /// being torn down — and the user is looking at the application they just closed, not at this
    /// one, so it has to reach them somewhere they will see it.
    /// </remarks>
    private void OnCaptureSourceLost(object? sender, string message) =>
        OnDispatcher(() =>
        {
            if (!IsTranslating) return;

            Log.Info("Realtime session ending: the captured window is gone");
            Stop();
            SessionEnded?.Invoke(this, message);
        });

    // ── Session callbacks (raised on the polling thread) ─────────────────────────────────────────

    private void OnRegionUpdated(object? sender, RealtimeRegionUpdate update) =>
        OnDispatcher(() =>
        {
            if (update.Generation != Volatile.Read(ref _visibleGeneration)) return;

            // The user may have gone back to edit mode while this pass was in flight; its window is
            // gone and the result belongs to a layout that no longer exists.
            if (_blockWindows.TryGetValue(update.RegionId, out var window))
                window.SetLines(update.Lines);
        });

    private void OnSessionFailed(object? sender, string message) =>
        OnDispatcher(() => _control?.ShowMessage(message, RealtimeMessageKind.Failure));

    private void OnBusyChanged(object? sender, bool busy) =>
        OnDispatcher(() => _control?.SetBusy(busy));

    private static void OnDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    // ── Teardown helpers ─────────────────────────────────────────────────────────────────────────

    private void CloseEditWindow()
    {
        // Before the close, always: WPF closes owned windows with their owner, so leaving this set
        // would take the control bar down with the edit layer.
        if (_control is { } control && ReferenceEquals(control.Owner, _edit))
            control.Owner = null;

        CloseWindow(_edit, nameof(RealtimeEditWindow));
        _edit = null;
        RefreshOverlayHandles();
    }

    private void CloseBlockWindows()
    {
        foreach (var window in _blockWindows.Values)
            CloseWindow(window, nameof(RealtimeBlockWindow));
        _blockWindows.Clear();
        RefreshOverlayHandles();
    }

    private void DisposeCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;

        try
        {
            capture.Dispose();
        }
        catch (Exception ex)
        {
            // A capture source can hold graphics resources whose release can fail on its own; the
            // rest of the teardown is what puts the user's screen back and must not be stranded.
            Log.Error(ex, "Failed to dispose the {Backend} capture backend", capture.Name);
        }
    }

    private void DisposeEscapeHook()
    {
        _escapeHook?.Dispose();
        _escapeHook = null;
    }

    private void RestoreShell()
    {
        var shell = _hiddenShell;
        _hiddenShell = null;
        if (shell is null || shell.IsVisible) return;

        try
        {
            shell.Show();
            shell.Activate();
        }
        catch (Exception ex)
        {
            // Racing an app shutdown that already destroyed the window — nothing left to restore,
            // and it must not take the rest of the teardown down with it.
            Log.Warn(ex, "Could not restore the shell window after a realtime session");
        }
    }

    // Each window is closed independently: these are Topmost layers over the user's screen, so a
    // throwing Close() must not strand the ones after it.
    private static void CloseWindow(Window? window, string name)
    {
        if (window is null) return;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to close {Window} — forcing teardown of the remaining windows", name);
        }
    }
}
