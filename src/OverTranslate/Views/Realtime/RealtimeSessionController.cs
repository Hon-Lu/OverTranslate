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
/// read from the settings file rather than chosen per session — see AppSettings — and arrive on the
/// request. The running bar's 顯示外觀 panel changes them halfway (#355) by writing the file and
/// handing the session a request re-read from it — see RealtimeSessionController.ApplyAppearance.
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
/// <param name="FixedFontSize">
/// The size every horizontal line is set at — 顯示外觀 → 文字大小 → 固定 — or null for 自動. Travels
/// with the colours, under the same rule.
/// </param>
public sealed record RealtimeStartRequest(
    System.Drawing.Rectangle ScreenBounds,
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
    string? FixedBorderColor = null,
    double? FixedFontSize = null);

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
    // For the whole session: see OnForegroundChanged and UpdateBlockVisibility.
    private ForegroundChangeWatch? _foregroundWatch;
    private Window? _hiddenShell;

    private readonly Dictionary<int, RealtimeBlockWindow> _blockWindows = [];
    // Every window this session draws, as handles, for a capture backend that has to leave them out
    // of its frames — see RefreshOverlayHandles. Written on the UI thread, read on the polling one.
    private IReadOnlyList<IntPtr> _overlayHandles = [];
    private List<RealtimeBlockPlacement> _blocks = [];
    // What each block window is showing, by region id, so 對照顯示 can be switched mid-session by
    // building the windows again in their new places and putting the same words back — see
    // ToggleCompareDisplay. Cleared with the screen on 暫停, for the same reason the windows are.
    private readonly Dictionary<int, IReadOnlyList<TranslatedBlock>> _shownLines = [];

    // 對照顯示, read from the settings file when the session starts and written back on every press.
    // Held here as well because the press has to reach whichever layers are up at the time.
    private bool _compareDisplay;

    // UI updates are dispatched asynchronously. Carrying the session's generation here prevents an
    // update queued before a pause from restoring text after the screen was cleared.
    private int _visibleGeneration;

    // What the capture currently on screen took from this session, so ResumeAfterCapture gives back
    // exactly that and nothing more. Default — every flag false — means no capture is standing on it.
    private RealtimeCaptureInterlude _interlude;

    /// <summary>
    /// The layout the last sitting ended with, offered back to the next one.
    /// </summary>
    /// <remarks>
    /// <para>Ending a session is not usually the user finishing — it is them going back to change the
    /// language, the engine or a colour, all of which live on a page a running session has hidden.
    /// Redrawing three blocks by hand every time they touch one of those is the tax that made this
    /// worth keeping, and the modes go with the rectangles because re-answering 字幕 / 對話 vs 遊戲 / UI for
    /// each block is the same tax again. The writing direction rides along for the same reason.</para>
    ///
    /// <para>Held here and not written to the settings file, which is the line RealtimePage draws for
    /// everything else about a sitting: this survives 結束即時翻譯 and nothing more. What the settings
    /// file keeps instead is what the user asked it to keep — a 翻譯區域預設, see
    /// <see cref="Models.RealtimeFramingPreset"/>. The target — the screen, and the window when there
    /// is one — is kept alongside the blocks because these are physical pixels on one monitor and one
    /// window: offered back for a different screen or another application's window, they would be
    /// blocks the user never drew, in places nothing is.</para>
    /// </remarks>
    private RememberedBlocks? _remembered;

    /// <param name="PresetId">The kept framing the blocks were last put on the screen from, so the
    /// next sitting's list still says which one they are — see <see cref="_appliedFramingId"/>.</param>
    private sealed record RememberedBlocks(
        RealtimeFramingStore.Target Target, IReadOnlyList<RealtimeBlockPlacement> Blocks,
        string? PresetId = null);

    /// <summary>
    /// The kept framing the blocks on the screen were last put there from — applied at the start,
    /// picked from the list, saved, updated or reverted — or null when the user drew them.
    /// </summary>
    /// <remarks>
    /// What lets the bar's list tell "the framing you applied, changed since" (已修改, with 還原 and
    /// 更新) from blocks drawn by hand: the blocks alone only say whether they match a framing now.
    /// A framing deleted on the page since leaves an id nothing goes by, which the list reads as
    /// blocks drawn by hand — the blocks themselves stay where they are.
    /// </remarks>
    private string? _appliedFramingId;

    /// <summary>
    /// What this sitting's blocks belong to, worked out once as the session starts: the screen, and
    /// the window when the source is one. Used to save the framing, to seed the next one and to decide
    /// whether last sitting's blocks may be offered back — see <see cref="Target"/>.
    /// </summary>
    private RealtimeFramingStore.Target _target;

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

    // Diagnostics for an edit layer seen stuck under the capture source and never reproduced: when
    // the timer last ran (cleared wherever it is stopped, so a pause is not read as a stall), and
    // what was last found above the layer — logged only when that changes.
    private long _lastStayOnTopTick;
    private IntPtr _editCoveredBy;

    // The session's screen as it is now, and the name it is found again by after the display
    // changes — see OnDisplaySettled. A display mode change arrives as several notifications; the
    // settle timer turns them into one look once they have stopped.
    private string _screenDeviceName = "";
    private System.Drawing.Rectangle _screenNow;
    private readonly DispatcherTimer _displaySettle = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // 載入模型中… on the bar while a start's models load; null outside a sitting.
    private RealtimeModelLoadingHint? _loadingHint;

    /// <summary>
    /// How long the modes must rest before a running session reads again on them.
    /// </summary>
    /// <remarks>
    /// Each change that reaches the session restarts the changed blocks' loops, and a recognition
    /// already inside the recogniser's gate cannot be called back — so a few changes in quick
    /// succession (switches flicked one after another on the 模式 panel, a slider stepped with the
    /// keys) would stack abandoned recognitions behind the gate. The editors already write a slider
    /// only once it is let go; this is the second line, for everything else. Long enough to take in
    /// a quick run of presses, short enough to read as "it changed when I let go".
    /// <para>A property, not a static field: <see cref="Instance"/> is built by a static initializer
    /// above this line, and a field here would still be zero when its constructor read it — the
    /// timer then fired at once and nothing was ever folded together (measured live: two presses
    /// 21 ms apart restarted the blocks twice).</para>
    /// </remarks>
    internal static TimeSpan ProfilesSettle => TimeSpan.FromMilliseconds(400);
    private readonly DispatcherTimer _profilesSettle = new() { Interval = ProfilesSettle };

    // Which block the 模式 panel is tuning, while the panel has the blocks marked — see MarkBlocks.
    // Kept here so overlays built again (a language change, 原文對照) are marked again.
    private int? _markedBlock;
    private bool _marking;

    private RealtimeSessionController()
    {
        _profilesSettle.Tick += (_, _) => FlushProfiles();

        _displaySettle.Tick += (_, _) =>
        {
            _displaySettle.Stop();
            OnDisplaySettled();
        };

        _stayOnTop.Tick += (_, _) =>
        {
            LogStayOnTopStall();
            KeepLayersOnTop();
        };
    }

    /// <summary>
    /// What the stay-on-top timer does each second, and what a switch of the foreground does at once:
    /// settles which blocks belong on the screen, then puts the layers back at the front of the
    /// topmost band.
    /// </summary>
    private void KeepLayersOnTop()
    {
        UpdateBlockVisibility();

        // Anything of ours opened over the session goes up first and the layers in under it —
        // see RaiseCompanions for why they are not raised and then covered again.
        var below = RaiseCompanions();
        void Raise(Window window)
        {
            if (below == IntPtr.Zero || !AlwaysOnTop.PlaceBehind(window, below))
                AlwaysOnTop.Reassert(window);
        }

        // The control bar last, so it ends up above the block layers it may overlap. A block put
        // away by UpdateBlockVisibility is left where it is: raising it is what used to keep it
        // over the window that had covered its source.
        foreach (var block in _blockWindows.Values)
            if (block.IsVisible) Raise(block);
        if (_edit is { } edit) Raise(edit);
        if (_control is { } control) Raise(control);

        // Every tooltip after every window: the control bar is owned by the edit layer, and
        // raising it brings the layer up too — over any tooltip of the layer's raised before it.
        if (_edit is { } editTips) AlwaysOnTop.ReassertToolTips(editTips);
        if (_control is { } controlTips) AlwaysOnTop.ReassertToolTips(controlTips);

        LogEditLayerCovered();
    }

    /// <summary>
    /// Another application's window has come to the foreground: the layers go back on top now
    /// rather than on the next tick.
    /// </summary>
    /// <remarks>
    /// A topmost window that comes to the foreground — a game or player kept on top, clicked back
    /// into — is put at the front of the topmost band, over the control bar. Left to the timer, the
    /// bar stayed under it for up to a second, and a press on the bar in that time went to the
    /// window over it: the bar's first press after clicking back into such a window did nothing,
    /// about one time in three when the press came within a second (measured live: 13 of 36 with a
    /// topmost full-screen window, each lost press with that window found over the button). Clicking
    /// it again while it already has the foreground does not raise it again, which is why only the
    /// first press was lost. The bar never takes the foreground (#358), so being raised is the only
    /// way back over such a window, and this is the moment it is covered.
    ///
    /// Not while the layers are away: the timer is stopped then — for a capture standing on the
    /// session, a pause of the stay-on-top work — and raising them would put them over the window
    /// that asked them to leave.
    /// </remarks>
    private void OnForegroundChanged()
    {
        if (!_stayOnTop.IsEnabled) return;
        KeepLayersOnTop();
    }

    /// <summary>
    /// Puts every other window of ours that is on top at the front: 取詞翻譯's popup, 替換翻譯's
    /// card, a list one of them has dropped down (#326). Returns the backmost of them, for the
    /// session's layers to go in behind — or zero when there are none.
    /// </summary>
    /// <remarks>
    /// The user summoned these over a running session to use them now, so the layers must not come
    /// up over them. They used to — raised to the front and the popup raised back over them in the
    /// same tick — and any frame the compositor presented in between showed the subtitles on top: a
    /// flicker that came and went. Raising these first and placing the layers behind them never puts
    /// the layers in front at all. Back to front, so they keep their order among themselves — a
    /// dropped-down list stays over its popup.
    ///
    /// Windows owned by one of the layers are left out. Windows keeps an owned window above its owner
    /// by itself, and raising it brings the owner up too, to just beneath it — so raising one owned
    /// by a block layer put that layer back over the popup until the layers were placed behind it
    /// again, the same flicker by another route. Visual Studio's in-app toolbar, on every window of a
    /// debugged build, is one.
    /// </remarks>
    private IntPtr RaiseCompanions()
    {
        var layers = Volatile.Read(ref _overlayHandles);
        var visible = OwnTopLevelWindows.Visible();
        var backmost = IntPtr.Zero;

        for (var i = visible.Count - 1; i >= 0; i--)
        {
            if (layers.Contains(visible[i])
                || !OwnTopLevelWindows.IsTopmost(visible[i])
                || OwnTopLevelWindows.IsOwnedByAny(visible[i], layers)) continue;

            AlwaysOnTop.Reassert(visible[i]);
            if (backmost == IntPtr.Zero) backmost = visible[i];
        }

        return backmost;
    }

    /// <summary>
    /// What a monitor capture is told to leave out: the session's own layers, then every other window
    /// of ours on the screen.
    /// </summary>
    /// <remarks>
    /// The others are 取詞翻譯's popup, 替換翻譯's card and anything they drop down, which may open
    /// over a running session (#326) — usually inside a block, since the text they act on is the text
    /// being watched. Left in the frames, the session would read their words and lay a translation of
    /// them over the very popup showing it. Asked of the system on every poll rather than recorded as
    /// they open, so no window of ours can be missing from the list for even one poll. In handle order
    /// after the layers, so the backend sees the same list for the same windows whatever their z-order.
    /// </remarks>
    internal static IReadOnlyList<IntPtr> ExclusionList(IReadOnlyList<IntPtr> layers, IEnumerable<IntPtr> ownVisible)
    {
        var others = ownVisible.Where(hwnd => !layers.Contains(hwnd)).Distinct().Order().ToList();
        return others.Count == 0 ? layers : [.. layers, .. others];
    }

    /// <summary>
    /// Warns when a tick arrives long after the one before: nothing was holding the layers on top
    /// meanwhile.
    /// </summary>
    /// <remarks>
    /// A late tick is not a blocked UI thread. The timer runs at Background priority, so any run of
    /// higher-priority work starves it while the thread keeps answering — measured over a display
    /// mode change: ticks 3–8s late while the thread never took more than 1.4s to answer a message,
    /// most of the gap spent in WPF waiting on its render thread. The message says what was measured
    /// and no more.
    /// </remarks>
    private void LogStayOnTopStall()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastStayOnTopTick != 0)
        {
            var gap = System.Diagnostics.Stopwatch.GetElapsedTime(_lastStayOnTopTick, now);
            if (gap > TimeSpan.FromSeconds(3))
                Log.Warn("Realtime stay-on-top timer was delayed: ticked {Gap:F1}s after the previous tick, the layers were not re-asserted meanwhile",
                    gap.TotalSeconds);
        }
        _lastStayOnTopTick = now;
    }

    /// <summary>
    /// Checks, right after the layer was put back on top, whether another application's window is
    /// still above it — which would mean the re-assert did not take, or that window is topmost too.
    /// </summary>
    private void LogEditLayerCovered()
    {
        var edit = _edit is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        var cover = edit == IntPtr.Zero ? null : WindowZOrderDiagnostics.FindForeignWindowAbove(edit);
        var coveredBy = cover?.Hwnd ?? IntPtr.Zero;
        if (coveredBy == _editCoveredBy) return;

        if (cover is { } c)
            Log.Warn(
                "Realtime edit layer is under hwnd={Hwnd:X} class={Class} process={Process} exstyle={ExStyle:X8} topmost={Topmost}; edit exstyle={EditExStyle:X8}",
                c.Hwnd, c.ClassName, c.ProcessName, c.ExStyle, c.IsTopmost, WindowZOrderDiagnostics.ExStyle(edit));
        else if (edit != IntPtr.Zero)
            Log.Info("Realtime edit layer is no longer under hwnd={Hwnd:X}", _editCoveredBy);

        _editCoveredBy = coveredBy;
    }

    /// <summary>
    /// In 視窗擷取, takes the overlays off the screen while their source is not there to be seen —
    /// minimised, hidden, cloaked — and, while it is, hides each translation box another
    /// application's window is touching. Everything comes back when that ends.
    /// </summary>
    /// <remarks>
    /// The capture reads the window itself, so a covered game goes on being translated, and the
    /// overlays drawn from it went on being re-asserted on top of whatever covered it: switching to
    /// a second game left the first one's subtitles and repaired background floating over it, which
    /// reads as the screen having frozen. See SourceWindowCoverage for what counts as covering.
    ///
    /// By box, not by block: a window over one line takes that line away and leaves the others, and
    /// one over an empty part of a block takes nothing — see RealtimeBoxCoverage. A 對照顯示 copy is
    /// a block window of its own where it is drawn, so it is judged where it is, not where its source
    /// line is.
    ///
    /// On the stay-on-top timer rather than a timer of its own: it is the same question the timer is
    /// already asking — which layers belong on top. And at once whenever another application comes
    /// to the foreground (ForegroundChangeWatch), which is when a source is usually covered, so the
    /// tick is only the backstop for what no switch announces. Not in 螢幕擷取, which reads the
    /// screen as it is: there is no source window to be covered, and whatever is on top is what the
    /// user meant to translate.
    /// </remarks>
    private void UpdateBlockVisibility()
    {
        if (!IsTranslating || _interlude.HideLayers || _blockWindows.Count == 0) return;
        if (_request is not { CaptureMode: Models.RealtimeCaptureMode.Window } request) return;

        var view = SourceWindowCoverage.Look(request.SourceWindow);

        foreach (var (id, window) in _blockWindows)
        {
            var show = !view.Hidden;
            if (show != window.IsVisible)
            {
                window.Visibility = show ? Visibility.Visible : Visibility.Hidden;
                Log.Debug("Realtime block {Id} {State}: source {Source}", id, show ? "shown" : "hidden",
                    show ? "back on screen" : "not on screen");
            }

            // Left as they were while the whole overlay is away: the next look with the source on
            // the screen settles them against whatever is over it then.
            if (!show) continue;
            if (window.ApplyCovers(view.Covers) > 0)
                Log.Debug("Realtime block {Id}: {Covered} translation box(es) covered", id, window.CoveredBoxes);
        }
    }

    // Raised on a thread of the system's choosing, so it only starts the settle timer, on the
    // dispatcher.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        OnDispatcher(() =>
        {
            if (!IsActive) return;
            _displaySettle.Stop();
            _displaySettle.Start();
        });

    /// <summary>
    /// The display has stopped changing: finds the session's screen again and puts the bar back on
    /// it — see RealtimeSessionScreen — then settles the blocks at once rather than on the next tick.
    /// </summary>
    private void OnDisplaySettled()
    {
        if (!IsActive || _control is not { } control) return;

        var screens = System.Windows.Forms.Screen.AllScreens
            .Select(screen => new RealtimeSessionScreen.Screen(screen.DeviceName, screen.Bounds))
            .ToList();
        var now = RealtimeSessionScreen.Resolve(_screenDeviceName, _screenNow, screens);
        if (now != _screenNow)
            Log.Info("Realtime session screen changed from {From} to {To}", _screenNow, now);

        _screenNow = now;
        control.UpdateScreenBounds(now);
        UpdateBlockVisibility();
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
            "Realtime session starting on screen {Screen}, {Src}->{Tgt}",
            request.ScreenBounds, request.SourceLanguage, request.TargetLanguage);

        _request = request;
        _hiddenShell = shellToHide;
        _hiddenShell?.Hide();
        _compareDisplay = SettingsService.Instance.Current.Realtime.CompareDisplay;

        // The same two engines the capture side uses — see AppServices. There is no fallback to get
        // wrong any more: nothing here has to find a window first, so there is no path on which a
        // second inference runtime could be built by accident.
        _session = new RealtimeTranslationSession(AppServices.Ocr, AppServices.Translation);
        Volatile.Write(ref _visibleGeneration, 0);
        _session.RegionUpdated += OnRegionUpdated;
        _session.Failed += OnSessionFailed;
        _session.FailureChanged += OnFailureChanged;
        _session.BusyChanged += OnBusyChanged;

        var control = new RealtimeControlWindow(request.ScreenBounds);
        control.StartRequested += (_, _) => StartTranslating();
        control.EditRequested += (_, _) => EnterEditMode();
        control.CloseRequested += (_, _) => Stop();
        control.PauseToggleRequested += (_, _) => TogglePause();
        // Handed on rather than acted on here: standing this session down is only half of a capture,
        // and the half that owns the screen afterwards belongs to MainWindow. The button is on the
        // running capsule only, which is the one state a capture is allowed in anyway.
        control.CaptureRequested += (_, _) => StartCaptureFromBar();
        // Wired here rather than in EnterEditMode, which runs again on every trip back into framing
        // and would stack a fresh handler each time. The layer it talks to is whichever one is up:
        // there is none while translating, and the button that raises this is not on screen then.
        control.CrosshairToggleRequested += (_, enabled) => _edit?.SetCrosshairEnabled(enabled);
        // Wired once for the same reason, and unlike the crosshair it is live in both modes — the
        // button is on the bar and on the capsule, and both are this one state.
        control.CompareDisplayToggleRequested += (_, _) => ToggleCompareDisplay();
        // 翻譯區域預設's 儲存目前框選: the blocks on screen now are kept for this screen or window, so
        // the next sitting opens on them instead of on empty space — see RealtimeFramingPreset.
        control.SaveFramingRequested += (_, _) => SaveFraming();
        // …its rows, which put one of those kept framings back on the screen: the layer swaps its
        // blocks in place, so the same sitting can be framed for subtitles and then for a menu…
        control.FramingChosen += (_, id) => ApplyFraming(id);
        // …and a changed framing's 更新 and 還原.
        control.FramingUpdateRequested += (_, id) => UpdateFraming(id);
        control.FramingRestoreRequested += (_, id) => ApplyFraming(id, restore: true);
        control.SetCompareDisplay(_compareDisplay);
        control.SetCollapsed(SettingsService.Instance.Current.Realtime.ControlBarCollapsed);
        control.CollapsedChanged += (_, collapsed) => SaveControlBarCollapsed(collapsed);
        control.LanguagesChangeRequested += (_, pair) => ChangeLanguages(pair.Source, pair.Target);
        control.AppearanceChanged += (_, _) => ApplyAppearance();
        // A static source again: unsubscribed in Stop, or it would keep this session alive.
        SettingsService.Instance.RealtimeProfilesChanged += OnRealtimeProfilesChanged;
        _control = control;
        control.Show();

        _loadingHint = new RealtimeModelLoadingHint(shown =>
        {
            Log.Info("Realtime bar: model loading hint {State}", shown ? "shown" : "hidden");
            _control?.SetLoadingModels(shown);
        }, OnDispatcher);
        RefreshOverlayHandles();

        _screenNow = request.ScreenBounds;
        _screenDeviceName = System.Windows.Forms.Screen.FromRectangle(request.ScreenBounds).DeviceName;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _foregroundWatch = ForegroundChangeWatch.Install(OnForegroundChanged);

        // The framing this sitting opens on: the blocks it remembers from an earlier sitting on this
        // screen or window, or else the preset kept for this screen or window — see InitialBlocks.
        // Before the layer, which draws them.
        _target = Target(request);
        _blocks = InitialBlocks(out var applied, out var missed);

        EnterEditMode();

        // Said on the bar rather than left to be noticed: blocks already on screen when the session
        // opens are the one part of this feature that happens without the user doing anything — and
        // a preset kept for this screen or window but not placed says why here too, in the same slot.
        if (applied is { } preset)
        {
            control.ShowMessage(LocalizationService.Format(
                "S.Realtime.FramingApplied", RealtimeFramingDisplay.MessageName(preset)));
        }
        else if (missed is { } near)
        {
            control.ShowMessage(FramingMissMessage(near.Why, near.Preset));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The blocks a session opens with, which kept framing they came from, and — when one was kept
    /// but could not be placed — that preset, so the bar can say why.
    /// </summary>
    /// <remarks>
    /// The remembered blocks win over a preset: they are the user's own last answer, including the
    /// nudges they made to it after saving, and they were drawn on this same screen and window. A
    /// preset is what is left when nothing is remembered, which is every launch after the one it was
    /// saved in — the one the user picked for that target, or the newest it has — see
    /// <see cref="RealtimeFramingStore.Match"/>.
    /// </remarks>
    private List<RealtimeBlockPlacement> InitialBlocks(
        out Models.RealtimeFramingPreset? applied,
        out (RealtimeFramingStore.Miss Why, Models.RealtimeFramingPreset Preset)? missed)
    {
        applied = null;
        missed = null;
        _appliedFramingId = null;

        // Remembered blocks that followed their window partly off this session's screen are not
        // offered back: the framing layer covers this screen only, so they could not be reached.
        if (_remembered is { } kept && RealtimeFramingStore.SameTarget(kept.Target, _target))
        {
            var followed = RealtimeFramingStore.FollowWindow(kept.Target, _target, kept.Blocks);
            if (RealtimeFramingStore.OnScreen(followed, _target.Screen))
            {
                _appliedFramingId = kept.PresetId;
                return [.. followed];
            }
        }

        if (RealtimeFramingStore.Match(_target) is { } preset)
        {
            var blocks = RealtimeFramingStore.Placements(preset, _target);
            if (blocks.Count > 0)
            {
                applied = preset;
                _appliedFramingId = preset.Id;
                return [.. blocks];
            }
        }

        // A preset is kept for this screen or window, but not for its size, or its window reaches
        // past this screen: the layer opens empty, and the bar says why rather than opening on
        // nothing with no explanation.
        var why = RealtimeFramingStore.Explain(_target, out var nearest);
        if (why != RealtimeFramingStore.Miss.None && nearest is not null) missed = (why, nearest);

        return [];
    }

    /// <summary>
    /// What the bar says about a preset that is kept for this target but could not be placed: the
    /// size it was drawn for, against the size the screen or window has now — or that its window
    /// reaches past the screen being framed.
    /// </summary>
    private static string FramingMissMessage(RealtimeFramingStore.Miss why, Models.RealtimeFramingPreset preset) =>
        why switch
        {
            RealtimeFramingStore.Miss.OffScreen => LocalizationService.Get("S.Realtime.FramingOffScreen"),
            _ when preset.IsForWindow => LocalizationService.Format(
                "S.Realtime.FramingWindowChanged", preset.WindowSize.Width, preset.WindowSize.Height),
            _ => LocalizationService.Format(
                "S.Realtime.FramingScreenChanged", preset.Screen.Width, preset.Screen.Height),
        };

    /// <summary>
    /// What a framing belongs to: this session's screen, and its window when it is reading one — see
    /// <see cref="RealtimeFramingStore.Target"/>.
    /// </summary>
    /// <remarks>
    /// The window is named from its live handle rather than from the picker's stored pair, because
    /// the two can differ by the time a session runs — the page lets the title be refreshed, and a
    /// window retitles itself as it plays. Its rectangle is measured now too, in the same form window
    /// capture reads it at, because a window preset's blocks are kept relative to that rectangle: a
    /// window that has moved since the session started must be saved and placed at where it is now.
    /// The stored pair is the fallback for a handle that has stopped describing a window the list
    /// would offer, which is the case a title is being saved for anyway.
    /// </remarks>
    private RealtimeFramingStore.Target Target(RealtimeStartRequest request)
    {
        bool window = request.CaptureMode == Models.RealtimeCaptureMode.Window;
        var named = window ? CaptureWindowList.Of(request.SourceWindow) : null;
        var stored = SettingsService.Instance.Current.Realtime;

        // A handle that has stopped describing a window the list would offer names nothing here, and
        // neither does a description that could not read the process; the stored pair is what the
        // session was started from, and is the better half to save under.
        string Process(string live) =>
            live.Length > 0 || !window ? live : stored.CaptureWindowProcess;
        string Title(string live) =>
            live.Length > 0 || !window ? live : stored.CaptureWindowTitle;

        var bounds = window
            ? RealtimeFramingStore.WindowBoundsOf(request.SourceWindow)
            : System.Drawing.Rectangle.Empty;
        if (window && bounds.Width <= 0 && _target.IsWindow) bounds = _target.WindowBounds;

        // The executable tells apart two games of one process name; a handle that has gone keeps the
        // one read at start, as the rectangle does.
        var path = window ? CaptureWindowList.ExecutablePathOf(request.SourceWindow) : "";
        if (window && path.Length == 0 && _target.IsWindow) path = _target.WindowPath;

        return new RealtimeFramingStore.Target(
            request.CaptureMode,
            request.ScreenBounds,
            _screenDeviceName,
            Process(named?.ProcessName ?? ""),
            Title(named?.Title ?? ""),
            bounds,
            path);
    }

    /// <summary>
    /// Saves the blocks on screen as a new framing for this screen or window, from 翻譯區域預設's
    /// 儲存目前框選: a target may keep several, and this one is selected by being saved — unless the
    /// same arrangement is already kept, which is selected instead of saved twice.
    /// </summary>
    /// <remarks>
    /// <para>Works the same while translating: the blocks on the screen are the ones being read, so
    /// keeping them only writes the settings file and the session goes on as it was.</para>
    ///
    /// <para>A press that cannot save costs the press, not the session: the settings write already
    /// logs and swallows a disk error, and anything else here is a fault that must not reach the
    /// dispatcher, where it would end the application over a button.</para>
    /// </remarks>
    public void SaveFraming()
    {
        if (_request is not { } request || _control is not { } control) return;

        if (_blocks.Count == 0)
        {
            control.ShowMessage(LocalizationService.Get("S.Realtime.NeedOneBlock"));
            return;
        }

        try
        {
            var target = Target(request);
            if (!target.HasWindow)
            {
                control.ShowMessage(LocalizationService.Get("S.Realtime.FramingSaveFailed"), RealtimeMessageKind.Failure);
                return;
            }

            var preset = RealtimeFramingStore.Save(target, _blocks);
            if (preset is null)
            {
                control.ShowMessage(LocalizationService.Format(
                    "S.Realtime.FramingLimitReached", Models.RealtimeSettings.MaxFramingPresets));
                return;
            }

            // The window may have moved while this was being framed: what follows (the bar's list,
            // the next save) is about where it is now.
            _target = target;
            _appliedFramingId = preset.Id;
            control.ShowMessage(LocalizationService.Format(
                "S.Realtime.FramingSaved", RealtimeFramingDisplay.MessageName(preset)));
            Log.Info("Realtime framing saved for {Target} ({Count} blocks, translating={Translating})",
                preset.TargetKey, preset.Blocks.Count, IsTranslating);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save the realtime framing");
            control.ShowMessage(LocalizationService.Get("S.Realtime.FramingSaveFailed"), RealtimeMessageKind.Failure);
        }
    }

    /// <summary>
    /// Writes the blocks on screen into the framing <paramref name="id"/> — 更新 on the row of the
    /// framing that was applied and changed since. Its name and selection stay; like a save, it only
    /// writes the settings file, translating or not.
    /// </summary>
    public void UpdateFraming(string id)
    {
        if (_request is not { } request || _control is not { } control) return;

        if (_blocks.Count == 0)
        {
            control.ShowMessage(LocalizationService.Get("S.Realtime.NeedOneBlock"));
            return;
        }

        try
        {
            var target = Target(request);
            if (!target.HasWindow)
            {
                control.ShowMessage(LocalizationService.Get("S.Realtime.FramingSaveFailed"), RealtimeMessageKind.Failure);
                return;
            }

            if (RealtimeFramingStore.Update(id, target, _blocks) is not { } preset) return;

            _target = target;
            _appliedFramingId = preset.Id;
            control.ShowMessage(LocalizationService.Format(
                "S.Realtime.FramingUpdated", RealtimeFramingDisplay.MessageName(preset)));
            Log.Info("Realtime framing {Preset} updated ({Count} blocks, translating={Translating})",
                preset.Id, preset.Blocks.Count, IsTranslating);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not update the realtime framing");
            control.ShowMessage(LocalizationService.Get("S.Realtime.FramingSaveFailed"), RealtimeMessageKind.Failure);
        }
    }

    /// <summary>
    /// What 翻譯區域預設 shows now: every framing of this monitor or application with what each one
    /// is, and what 儲存目前框選 can do — see <see cref="RealtimeFramingStore.ListFor"/> — with the
    /// target it was worked out for, which a greyed row's reason names the size of.
    /// </summary>
    public (RealtimeFramingStore.FramingList List, RealtimeFramingStore.Target Target) FramingList()
    {
        var target = SessionTarget;
        var list = IsActive
            ? RealtimeFramingStore.ListFor(
                SettingsService.Instance.Current.Realtime.FramingPresets, target, _blocks, _appliedFramingId)
            : new RealtimeFramingStore.FramingList([], RealtimeFramingStore.SaveState.NoBlocks, null);
        return (list, target);
    }

    /// <summary>
    /// The target as it is now, for work done while framing rather than at start: the window may have
    /// been moved — or resized — since the session opened, and its rectangle decides where a window
    /// preset's blocks go.
    /// </summary>
    private RealtimeFramingStore.Target SessionTarget =>
        _request is { } request ? Target(request) : _target;

    /// <summary>
    /// Puts the framing <paramref name="id"/> names on the screen — a row pressed in
    /// 翻譯區域預設, or <paramref name="restore"/>: 還原 on the row of the framing that was applied
    /// and changed since — and marks it as this target's.
    /// </summary>
    /// <remarks>
    /// <para>While framing, the layer swaps its blocks in place and the next 開始翻譯 reads the new
    /// ones.</para>
    ///
    /// <para>While translating, the overlays and the capture backend are built around the regions the
    /// session started with, so a new framing is a new session on it: the sitting is taken back into
    /// framing and started again straight away, which is the same two steps the bar's 編輯 and 開始翻譯
    /// are, run together. A session that was paused comes back paused — 暫停 is the user's own switch,
    /// and a layout change is not a reason to start reading the screen again. A framing that is
    /// already the one on the screen is not a change at all, so nothing happens. If the restart
    /// cannot start at all, its own refusal stands (the bar is left saying why, in framing) rather
    /// than being replaced by "applied".</para>
    ///
    /// <para>Switching away from blocks no framing keeps — a framing changed after it was applied, or
    /// blocks drawn by hand — loses them, and the bar says so in the same sentence.</para>
    ///
    /// <para>A press that cannot switch costs the press, not the session: anything that goes wrong is
    /// logged rather than raised to the dispatcher, where it would end the application over a menu
    /// row.</para>
    /// </remarks>
    public void ApplyFraming(string id, bool restore = false)
    {
        if (_control is not { } control) return;
        if (RealtimeFramingStore.ForId(id) is not { } preset) return;

        var target = SessionTarget;
        if (preset.IsForWindow && !target.HasWindow)
        {
            control.ShowMessage(LocalizationService.Get("S.Realtime.WindowCaptureGone"), RealtimeMessageKind.Failure);
            return;
        }

        var blocks = RealtimeFramingStore.Placements(preset, target);
        if (blocks.Count == 0)
        {
            // Kept for this screen or window but drawn for another size, or its window reaches past
            // this screen: say which, in the place a successful switch says "applied".
            var why = RealtimeFramingStore.FitOf(preset, target);
            control.ShowMessage(FramingMissMessage(
                why == RealtimeFramingStore.Miss.None ? RealtimeFramingStore.Miss.ScreenSize : why, preset));
            return;
        }

        // This framing is the one already on the screen: nothing to rebuild, and while translating
        // nothing worth taking the session down for.
        if (_blocks.SequenceEqual(blocks))
        {
            _appliedFramingId = preset.Id;
            return;
        }

        bool discarding = !restore && RealtimeFramingStore.HasUnsavedChanges(
            SettingsService.Instance.Current.Realtime.FramingPresets, target, _blocks);

        try
        {
            // Where the window is now is what the blocks were placed against; keep it, so the next
            // save or list is about the same rectangle.
            _target = target;

            if (_edit is { } edit)
            {
                edit.SetBlocks(blocks);
            }
            else if (IsTranslating)
            {
                bool wasPaused = _session?.IsPaused == true;

                _blocks = [.. blocks];
                EnterEditMode();
                StartTranslating();

                // A restart that could not start has put the bar back into framing and said why; that
                // message is the one worth keeping.
                if (!IsTranslating) return;
                if (wasPaused) TogglePause();
            }
            else
            {
                return;
            }

            _appliedFramingId = preset.Id;
            RealtimeFramingStore.Select(id);
            var key = restore ? "S.Realtime.FramingRestored"
                : discarding ? "S.Realtime.FramingAppliedDiscarded"
                : "S.Realtime.FramingApplied";
            control.ShowMessage(LocalizationService.Format(key, RealtimeFramingDisplay.MessageName(preset)));
            Log.Info("Realtime framing switched to {Preset} ({Count} blocks, restore={Restore}, discarded={Discarded})",
                preset.Id, blocks.Count, restore, discarding);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not switch the realtime framing");
        }
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
    /// caller (the pause shortcut) is deliberately silent in that mode rather than explaining a
    /// rule about a feature the user has not started.
    ///
    /// Nothing either while a capture has the session stood down. The shortcut is still live then —
    /// the bar it belongs to is off the screen, so the key is the only way to reach this at all —
    /// and pressing it would start the loops reading the capture's own dim layer, then hand
    /// <see cref="ResumeAfterCapture"/> a session in the opposite state from the one it recorded.
    /// The session comes back in the state the capture found it, and that is the whole promise.
    /// </remarks>
    /// <returns>Whether there was a running session to toggle.</returns>
    public bool TogglePause()
    {
        if (_interlude.HideLayers) return false;
        if (!IsTranslating || _session is null || _control is not { } control) return false;

        // Modes changed a moment ago go to the session first: 繼續 starts on what it holds.
        FlushProfiles();

        if (_session.IsPaused)
        {
            // Before the loops start, so the first result cannot arrive to a bar still saying 已暫停.
            control.SetPaused(false);
            _session.Resume();
            _loadingHint?.Wait(_session.ModelsLoading);
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
        _shownLines.Clear();

        control.SetPaused(true);
        return true;
    }

    /// <summary>
    /// Sends the running capsule's 截圖翻譯 press on to the one place that owns a capture.
    /// </summary>
    /// <remarks>
    /// Nothing is decided here on purpose. <see cref="TryYieldToCapture"/> is called from that side
    /// as well, so the bar, the shortcut and the nav rail all reach the same answer down the same
    /// path rather than down three that can drift apart.
    /// </remarks>
    private static void StartCaptureFromBar()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartCaptureFromRealtimeBar();
    }

    /// <summary>
    /// Stands the session down so a screenshot capture can have the screen, and reports whether the
    /// capture may start at all.
    /// </summary>
    /// <remarks>
    /// <see cref="RealtimeCaptureInterlude"/> holds the rule and why it is the way it is; this does
    /// the three things it asks for, in an order the screen depends on.
    ///
    /// The layers have to genuinely leave the screen, not merely be emptied: a capture grabs the
    /// desktop with CopyFromScreen, and these windows stopped carrying WDA_EXCLUDEFROMCAPTURE when
    /// keeping them out of frame became the realtime backend's job alone (#105). Left up, the
    /// control bar and whatever the blocks were drawing would be baked into the very image the user
    /// is about to select from — and could then be recognised and translated back to them.
    ///
    /// Idempotent, because the capture shortcut is also what closes a capture: the second press
    /// arrives here with the session already stood down and must not hide anything twice or take a
    /// second reading of a state this call itself changed.
    /// </remarks>
    /// <returns>False only while the user is framing blocks, which is where a capture is refused.</returns>
    public bool TryYieldToCapture()
    {
        if (_interlude.HideLayers) return true;

        var interlude = RealtimeCaptureInterlude.For(IsActive, IsTranslating, _session is { IsPaused: true });
        if (!interlude.Allowed) return false;
        if (!interlude.HideLayers || _control is not { } control) return true;

        // First, and before this session is marked as stood down — TogglePause refuses once it is.
        // This is what frees the OCR engine: the loops stop before the capture asks for a model of
        // its own, so the two never read at the same time, and the swap AcquireRuntime may have to
        // make waits only for a pass already in flight.
        //
        // What it reports is kept rather than what was intended, so the resume is the exact inverse
        // of what happened here: a pause that did not take must not come back as a resume that does.
        if (interlude.PauseWatching && !TogglePause())
            interlude = interlude with { PauseWatching = false };

        _interlude = interlude;

        // Nothing to keep on top while the layers are away, and a tick that fired mid-capture would
        // be re-asserting them over the window that asked them to leave.
        _stayOnTop.Stop();
        _lastStayOnTopTick = 0;

        // One wait for the composition rather than one per window: they go together, and the user is
        // holding a shortcut waiting for the screen to freeze.
        List<Window> layers = [.. _blockWindows.Values];
        layers.Add(control);
        WindowScreenPresence.HideAndWaitForScreen(layers);

        Log.Info("Realtime session yielded to a capture (paused by the capture: {Paused})",
            interlude.PauseWatching);
        return true;
    }

    /// <summary>
    /// Puts the session back the way the capture found it, once that capture is off the screen.
    /// </summary>
    /// <remarks>
    /// Gives back only what <see cref="TryYieldToCapture"/> took. A session the user had paused
    /// themselves comes back paused: they pressed 暫停 for a reason of their own, and a capture that
    /// resumed it on the way out would be moving a switch nobody touched.
    ///
    /// Safe to call when no capture ever stood on the session, which is what lets the capture
    /// teardown call it unconditionally.
    /// </remarks>
    public void ResumeAfterCapture()
    {
        if (!_interlude.HideLayers) return;

        var interlude = _interlude;
        _interlude = default;

        // The session can have ended while the capture was up — 結束即時翻譯 is on the tray menu, and
        // a failing capture source ends one on its own. There is nothing left to put back then.
        if (!IsActive || _control is not { } control) return;

        // Re-asserted as they come back rather than left to the timer's next tick: the capture was
        // topmost too, and a second of the bar sitting under whatever it left behind reads as the
        // session having been lost.
        foreach (var block in _blockWindows.Values)
        {
            block.Show();
            AlwaysOnTop.Reassert(block);
        }

        control.Show();
        AlwaysOnTop.Reassert(control);
        _stayOnTop.Start();

        if (interlude.PauseWatching) TogglePause();

        Log.Info("Realtime session restored after a capture (resumed by the capture: {Resumed})",
            interlude.PauseWatching);
    }

    public void Stop()
    {
        if (!IsActive) return;

        Log.Info("Realtime session ending");

        _stayOnTop.Stop();
        _lastStayOnTopTick = 0;
        DisposeEscapeHook();
        // Nothing is left to read on the modes, and a tick after this would find no blocks.
        _profilesSettle.Stop();
        _marking = false;
        _markedBlock = null;

        // A static event: left subscribed, it would keep this session's handler alive and running.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SettingsService.Instance.RealtimeProfilesChanged -= OnRealtimeProfilesChanged;
        _displaySettle.Stop();
        _foregroundWatch?.Dispose();
        _foregroundWatch = null;

        // Before the bar goes. A load still under way finishes on its own and finds no hint.
        _loadingHint?.Dispose();
        _loadingHint = null;

        if (_session != null)
        {
            _session.RegionUpdated -= OnRegionUpdated;
            _session.Failed -= OnSessionFailed;
            _session.FailureChanged -= OnFailureChanged;
            _session.BusyChanged -= OnBusyChanged;

            // The one press that means "done with this": the recogniser's memory goes back here
            // rather than on 暫停, which means "not now" and is followed by 繼續 wanting it again.
            _session.Stop(releaseRecogniser: true);
            _session = null;
        }

        // Taken out of reach now, released last and only after this has returned: everything the
        // user can see — the overlays, the bar, the shell window — goes back first.
        var capture = DetachCapture();
        CloseBlockWindows();
        CloseEditWindow();

        CloseWindow(_control, nameof(RealtimeControlWindow));
        _control = null;

        // Kept before the working copy is dropped — see RememberedBlocks for why this one thing
        // outlives the session while everything else here is torn down.
        _remembered = _blocks.Count > 0 ? new RememberedBlocks(_target, _blocks, _appliedFramingId) : null;
        _appliedFramingId = null;

        _blocks = [];
        _request = null;

        // A session can be ended from the tray while a capture stands on it. The windows this was
        // about are gone with it, so the capture's teardown has nothing left to give back.
        _interlude = default;

        RestoreShell();
        ReleaseInBackground(capture);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Modes ────────────────────────────────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (_request is not { } request || _control is not { } control) return;

        // 開始翻譯 reads every block on its mode as it is then; nothing is waiting to be handed over.
        _profilesSettle.Stop();
        _marking = false;
        _markedBlock = null;

        // Before the block windows go: a backend built around a set of overlays is only valid while
        // those overlays are the ones on screen, and edit mode is where the user changes them.
        _session?.Stop();
        // Released once this has returned — the framing layer is about to go up and must not wait on it.
        // StartTranslating builds a new backend of its own, so the old one going at its own pace
        // shares nothing with what comes next.
        ReleaseInBackground(DetachCapture());
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
            request.ScreenBounds, _blocks, Models.RealtimeSettings.MaxBlockCount,
            settings.Current.Realtime.NewBlockProfileOrStandard(),
            settings.Current.Realtime.TextOrientation,
            RealtimeProfileEditing.Choices(settings.Current.Realtime.Profiles),
            _compareDisplay);
        edit.BlocksChanged += (_, _) =>
        {
            var before = _blocks;
            _blocks = [.. edit.GetPhysicalBlocks()];
            control.SetBlockCount(_blocks.Count, Models.RealtimeSettings.MaxBlockCount);

            // The 模式 panel shows which blocks there are and what each is set to, not where they
            // are: a block being dragged reports itself many times a second, and redrawing the
            // panel for each of those would be all cost.
            if (!SameBlocksForPanel(before, _blocks)) BlocksChanged?.Invoke(this, EventArgs.Empty);

            // Every block removed is the framing put away, not changed: what is drawn next is the
            // user's own, and the list no longer offers 還原 or 更新 for the one applied before.
            if (_blocks.Count == 0) _appliedFramingId = null;

            // 翻譯區域預設, when it is open, says whether these blocks are a kept framing.
            control.RefreshFramings();
        };
        edit.BlockFocused += (_, index) => BlockFocused?.Invoke(this, index);
        edit.BlockRemoved += (_, index) => BlockRemoved?.Invoke(this, index);
        // Written on the press rather than at the end of the session: a session over a full-screen
        // game is as likely to end with the machine being shut down as with 結束即時翻譯. The pair
        // together because they arrive together: one tray being pressed is the user telling this
        // sitting what they are watching.
        edit.BlockDefaultsChanged += (_, defaults) =>
        {
            var realtime = settings.Current.Realtime;
            if (realtime.NewBlockProfile == defaults.Profile &&
                realtime.TextOrientation == defaults.Orientation) return;
            realtime.NewBlockProfile = defaults.Profile;
            realtime.TextOrientation = defaults.Orientation;
            settings.Save();
        };
        edit.LimitReached += (_, _) =>
            control.ShowMessage(LocalizationService.Format("S.Realtime.TooManyBlocks", Models.RealtimeSettings.MaxBlockCount));
        // 模式設定… opens the 模式 panel on that block, over this layer, and nothing ends: it used to
        // end the sitting and open the page, because the page's window would open underneath a layer
        // that covers the whole screen. The panel is owned by the bar, which this layer owns, so it
        // is above the layer for as long as both are up. Deferred, because the press arrives inside
        // the layer's own input handling, which closes the list it came from.
        edit.ProfilePanelRequested += (_, index) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _control?.OpenProfiles(index));
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
        control.SetBlockCount(_blocks.Count, Models.RealtimeSettings.MaxBlockCount);
        // Belt and braces alongside the ownership above: cheap, and the failure it guards against is
        // a control bar the user cannot reach at all.
        control.BringToFront();

        _stayOnTop.Start();

        // Only while editing. The hook swallows Esc process-wide, and a translating session can run
        // for hours next to an application that needs its own Esc key.
        DisposeEscapeHook();
        _escapeHook = GlobalEscapeHook.Install(OnEscapeWhileFraming);
    }

    /// <summary>
    /// Esc while framing puts away one thing at a time: a block's open list of modes, then what is
    /// open on the bar's 翻譯區域預設 or 模式 panel, then the panel — and only otherwise ends the
    /// sitting. A press meant for any of those must not cost the user every block.
    /// </summary>
    private void OnEscapeWhileFraming()
    {
        if (_edit?.TryDismissMenu() == true) return;
        if (_control?.TryDismissFramings() == true) return;
        if (_control?.TryDismissProfiles() == true) return;
        Stop();
    }

    /// <summary>
    /// Whether the 模式 panel would show <paramref name="b"/> just as it shows <paramref name="a"/>:
    /// the same number of blocks, each on the same mode and direction. Where they are is not shown.
    /// </summary>
    internal static bool SameBlocksForPanel(
        IReadOnlyList<RealtimeBlockPlacement> a, IReadOnlyList<RealtimeBlockPlacement> b) =>
        a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.ProfileId == pair.Second.ProfileId && pair.First.Orientation == pair.Second.Orientation);

    /// <summary>
    /// The 即時翻譯 shortcut pressed while blocks are being framed: the edit bar's 開始翻譯 (#324).
    /// </summary>
    /// <remarks>
    /// So one key walks the whole way — framing, translating, paused and back — each press the next
    /// step from wherever the session is. Refused the same way the button is when nothing is framed.
    /// </remarks>
    /// <returns>Whether there was a framing session to start.</returns>
    public bool StartTranslatingFromShortcut()
    {
        if (!IsActive || IsTranslating) return false;

        StartTranslating();
        return true;
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

        var regions = Regions();

        ShowBlockWindows(request, regions);

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
        _loadingHint?.Wait(_session.ModelsLoading);
    }

    // Ids are positions in _blocks: the session and the overlays are both built from this list, so a
    // result arriving for region 1 is for whatever block is second in it.
    private List<RealtimeRegion> Regions() => _blocks
        .Select((block, index) => new RealtimeRegion(
            index, block.Bounds, SettingsService.Instance.Current.Realtime.ResolveProfile(block.ProfileId),
            block.Orientation))
        .ToList();

    /// <summary>
    /// Opens one overlay per region, over its block or — with 對照顯示 on, for a block whose copy the
    /// user has not turned off — over the block's copy.
    /// </summary>
    /// <remarks>
    /// The two 進階選項 looks are turned off for the copy, whatever the page says. Both exist to make
    /// the translation sit in the picture where the source was — one erases the source and repairs
    /// the background, the other reads the source's colour — and under 對照顯示 the source is meant
    /// to stay visible and the translation is somewhere else. A patch repaired from the block and
    /// pasted beside it would be a piece of the wrong place; the band is the honest look for a
    /// translation drawn next to its source, and it costs no screen grabs. A block with its copy
    /// turned off is drawn over itself, so it gets them back exactly as with the switch off.
    /// </remarks>
    private void ShowBlockWindows(RealtimeStartRequest request, IReadOnlyList<RealtimeRegion> regions)
    {
        var copies = _compareDisplay ? CompareLocations(request) : null;

        // Resolved before the overlays are shown. They are click-through and belong to this process,
        foreach (var region in regions)
        {
            var block = _blocks[region.Id];
            bool compared = copies is not null && !block.CompareHidden;
            var look = RealtimeBlockAppearance.For(request, compared);
            var window = new RealtimeBlockWindow(
                region.Id, region.Bounds, GrabUnderlying, request.SourceLanguage, request.TargetLanguage,
                look.TextColor, look.ScrimColor, look.ScrimOpacity,
                look.NaturalBackground,
                look.SampleTextColor,
                region.Profile.Layout, look.Border, look.BorderColor, region.Orientation,
                compared ? copies![region.Id] : null, compared ? block.CompareScale : 1.0,
                look.FixedFontSize, region.Profile.Align);
            _blockWindows[region.Id] = window;
            window.Show();
        }
        // Built again under an open 模式 panel (a language change, 原文對照): named again.
        ShowMarkers();
    }

    /// <summary>
    /// Where each block's copy goes on screen, indexed like <see cref="_blocks"/> — which is what a
    /// region's id is. The entry for a block whose copy is turned off means nothing.
    /// </summary>
    private System.Drawing.Point[] CompareLocations(RealtimeStartRequest request)
    {
        var offsets = RealtimeComparePlacement.Place(
            [.. _blocks.Select(block =>
                new RealtimeCompareSlot(
                    block.Bounds, block.Orientation, block.CompareOffset, block.CompareScale, block.CompareHidden))],
            request.ScreenBounds);

        return [.. _blocks.Select((block, index) => new System.Drawing.Point(
            block.Bounds.X + offsets[index].X, block.Bounds.Y + offsets[index].Y))];
    }

    private static void SaveControlBarCollapsed(bool collapsed)
    {
        var settings = SettingsService.Instance;
        if (settings.Current.Realtime.ControlBarCollapsed == collapsed) return;
        settings.Current.Realtime.ControlBarCollapsed = collapsed;
        settings.Save();
    }

    /// <summary>
    /// The 原文對照 shortcut (#398): the bar's button, pressed from wherever the user is.
    /// </summary>
    /// <remarks>
    /// The same toggle as the button, so the two cannot drift apart. Nothing here takes the
    /// foreground: the windows it rebuilds are shown without activation and the bar is only
    /// re-stacked, so the game the key was pressed in keeps the keyboard.
    /// </remarks>
    public void ToggleCompareFromShortcut()
    {
        if (!IsActive) return;
        ToggleCompareDisplay();
    }

    /// <summary>Says on the bar why a shortcut that belongs to the session did nothing.</summary>
    public void ShowShortcutProblem(string message) =>
        _control?.ShowMessage(message, RealtimeMessageKind.Failure);

    /// <summary>
    /// Switches 對照顯示, from the bar or the capsule, without ending anything.
    /// </summary>
    /// <remarks>
    /// <para>Written to the settings file on the press, like the edit layer's own states: a session
    /// over a full-screen game is as likely to end with the machine shutting down as with
    /// 結束即時翻譯.</para>
    ///
    /// <para>While framing, the edit layer shows or hides the copies' outlines and that is all. While
    /// translating, the overlays are built again in their new places rather than moved: a window
    /// pinned beside its block also has to stop repairing the picture under itself (see
    /// <see cref="ShowBlockWindows"/>), and those choices are made once, when a window is built. The
    /// session is not touched — no pause, no new reading — and each window is handed back the words
    /// it was showing, so the switch moves the translation rather than blanking it until the next
    /// line arrives. A paused session has nothing to hand back, which is the right answer: it is
    /// paused precisely so that nothing shows.</para>
    ///
    /// <para>The capture backend is told about the new handles straight away, because it keeps the
    /// overlays out of its frames by handle — a window it has not heard of would be read back as
    /// source text. It drops frames until its list matches, so for that moment nothing is read,
    /// which is the safe side to be wrong on.</para>
    /// </remarks>
    private void ToggleCompareDisplay()
    {
        if (_request is not { } request || _control is not { } control) return;

        // The bar is off the screen while a capture stands on the session, so this is unreachable
        // then — and windows rebuilt now would appear over the capture that hid their predecessors.
        if (_interlude.HideLayers) return;

        _compareDisplay = !_compareDisplay;
        control.SetCompareDisplay(_compareDisplay);

        var settings = SettingsService.Instance;
        if (settings.Current.Realtime.CompareDisplay != _compareDisplay)
        {
            settings.Current.Realtime.CompareDisplay = _compareDisplay;
            settings.Save();
        }

        _edit?.SetCompareDisplay(_compareDisplay);

        if (!IsTranslating || _blockWindows.Count == 0) return;

        var shown = new Dictionary<int, IReadOnlyList<TranslatedBlock>>(_shownLines);

        CloseBlockWindows();
        ShowBlockWindows(request, Regions());
        RefreshOverlayHandles();

        foreach (var (id, lines) in shown)
        {
            if (!_blockWindows.TryGetValue(id, out var window)) continue;
            window.SetLines(lines);
            _shownLines[id] = lines;
        }

        // The new windows opened above the bar; it goes back on top of them.
        control.BringToFront();

        Log.Info("Realtime compare display switched {State} mid-session", _compareDisplay ? "on" : "off");
    }

    /// <summary>
    /// Raised after the floating bar changed the session's language pair (#345), once the new pair
    /// is in the settings file — for the page whose pickers show it.
    /// </summary>
    public event EventHandler? LanguagesChanged;

    /// <summary>
    /// Switches the language pair from the running capsule, without ending anything (#345).
    /// </summary>
    /// <remarks>
    /// <para>Written to the settings file on the press, like 對照顯示, so 結束 lands on a page whose
    /// pickers already say what the session ended on — and the next session starts on it.</para>
    ///
    /// <para>The overlays are built again rather than reused, and built empty. Both languages decide
    /// how a window draws — the target its font, its punctuation and whether a vertical block is set
    /// down or across (#315), the pair together whether Latin is being set as CJK — and those are
    /// fixed when a window is built. Unlike <see cref="ToggleCompareDisplay"/>, nothing is handed
    /// back: what the windows were showing is the old pair's translation, and drawn in the new
    /// pair's layout it would be the wrong words in the wrong shape. The session fences what was in
    /// flight, so nothing read for the old pair reaches the new windows either; they stay empty
    /// until the first reading in the new pair arrives.</para>
    ///
    /// <para>Paused, the session stays paused and the windows stay empty; 繼續 starts on the new
    /// pair. The blocks — their modes and the direction the user drew them in — are left exactly as
    /// they were: those describe the picture, and the picture has not changed.</para>
    /// </remarks>
    private void ChangeLanguages(string source, string target)
    {
        if (_request is not { } request || _control is not { } control || _session is not { } session) return;

        // The bar is off the screen while a capture stands on the session, for the reason
        // ToggleCompareDisplay gives.
        if (_interlude.HideLayers || !IsTranslating) return;

        source = Models.LanguageData.GetValidRealtimeSourceCode(source);
        target = Models.LanguageData.GetValidTargetCode(target);
        if (string.Equals(source, request.SourceLanguage, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target, request.TargetLanguage, StringComparison.OrdinalIgnoreCase)) return;

        // The new pair starts the loops on what the session holds, so modes changed a moment ago
        // go to it first.
        FlushProfiles();

        var paused = session.IsPaused;
        var updated = request with { SourceLanguage = source, TargetLanguage = target };
        _request = updated;

        var settings = SettingsService.Instance;
        settings.Current.Realtime.SourceLanguage = source;
        settings.Current.Realtime.TargetLanguage = target;
        settings.Save();

        // First, so nothing the old pair still had in flight can be drawn: updates carrying an
        // older generation are refused by OnRegionUpdated from here on.
        Volatile.Write(ref _visibleGeneration, session.ChangeLanguages(source, target));

        var rebuilt = _blockWindows.Count > 0;
        if (rebuilt)
        {
            CloseBlockWindows();
            ShowBlockWindows(updated, Regions());
            RefreshOverlayHandles();
        }

        control.SetLanguages(source, target);
        // The new windows opened above the bar; it goes back on top of them.
        control.BringToFront();
        if (!paused) _loadingHint?.Wait(session.ModelsLoading);

        Log.Info(
            "Realtime languages switched mid-session: {OldSrc}->{OldTgt} to {Src}->{Tgt} (overlays rebuilt: {Rebuilt}, paused: {Paused})",
            request.SourceLanguage, request.TargetLanguage, source, target, rebuilt, paused);

        LanguagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts every block on <paramref name="fromId"/> — this sitting's, while framing or translating,
    /// and the blocks kept for the next — on <paramref name="toId"/>, a mode just split off that
    /// preset on the page (the user's call: what is changed there is meant for what is on screen).
    /// </summary>
    /// <remarks>
    /// Called on the dispatcher straight after the new mode is stored, so the blocks move onto it in
    /// the same pass that reads them again; the deferred <see cref="ApplyProfiles"/> the store raised
    /// then finds nothing left to change.
    /// </remarks>
    public void FollowProfile(string fromId, string toId)
    {
        _blocks = RealtimeProfileEditing.Follow(_blocks, fromId, toId);
        if (_remembered is { } kept)
            _remembered = kept with { Blocks = RealtimeProfileEditing.Follow(kept.Blocks, fromId, toId) };
        _edit?.SetProfileChoices(RealtimeProfileEditing.Choices(SettingsService.Instance.Current.Realtime.Profiles), (fromId, toId));
        ApplyProfiles();
        // At once: the press made a mode and moved the blocks onto it, and the user is watching for
        // the change it carried.
        FlushProfiles();
    }

    // Raised by whoever saved the modes, on whatever thread that was; the work is the dispatcher's.
    private void OnRealtimeProfilesChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyProfiles);

    /// <summary>
    /// Raised on the dispatcher when which blocks there are, or which mode one is on, may have
    /// changed — chosen on the 模式 panel or a block's own list, moved onto a mode just split off its
    /// preset, put back on 標準 when its mode was deleted, or a block drawn or removed while framing —
    /// for the panel to show it.
    /// </summary>
    public event EventHandler? BlocksChanged;

    /// <summary>
    /// Raised with a block's position when the user presses it on the edit layer, or has just drawn
    /// it: the 模式 panel turns to that block.
    /// </summary>
    public event EventHandler<int>? BlockFocused;

    /// <summary>
    /// Raised with the position a block had on the edit layer, before <see cref="BlocksChanged"/>
    /// reports it gone: the 模式 panel keeps its place among the blocks left.
    /// </summary>
    public event EventHandler<int>? BlockRemoved;

    /// <summary>Whether blocks are being framed on the edit layer.</summary>
    public bool IsFraming => _edit is not null;

    /// <summary>
    /// The blocks the 模式 panel tunes, in order — being translated, or being framed; 區塊 1 is the
    /// first. Empty otherwise.
    /// </summary>
    public IReadOnlyList<RealtimeBlockPlacement> PanelBlocks => IsTranslating || IsFraming ? _blocks : [];

    /// <summary>
    /// Puts block <paramref name="index"/> on <paramref name="profileId"/> from the 模式 panel,
    /// without ending anything.
    /// </summary>
    /// <remarks>
    /// The answer new blocks start on goes with it, as choosing a block's mode on the edit layer
    /// does: either way it is the user telling this sitting what they are watching. While framing,
    /// the edit layer makes the change itself, as a pick from the block's own list — that is where
    /// the blocks live until translation starts, and the one path keeps the two from disagreeing.
    /// </remarks>
    public void SetBlockProfile(int index, string profileId)
    {
        if (_edit is { } edit)
        {
            Log.Info("Realtime block {Block} switched mode from the mode panel while framing", index);
            edit.SetBlockProfile(index, profileId);
            return;
        }

        if (!IsTranslating || index < 0 || index >= _blocks.Count) return;

        var settings = SettingsService.Instance;
        var realtime = settings.Current.Realtime;
        profileId = realtime.KnownProfileId(profileId);
        if (_blocks[index].ProfileId == profileId) return;

        Log.Info("Realtime block {Block} switched mode mid-session to {Profile:l}", index,
            Models.RealtimePresets.IsPreset(profileId) ? profileId : "custom");
        _blocks[index] = _blocks[index] with { ProfileId = profileId };
        if (realtime.NewBlockProfile != profileId)
        {
            realtime.NewBlockProfile = profileId;
            settings.Save();
        }
        ApplyProfiles();
    }

    /// <summary>
    /// Names every block on screen and outlines <paramref name="selected"/> — while the 模式 panel is
    /// open, so 區塊 2 on the panel is a block the user can find. One block is marked as well, so the
    /// panel and the screen read the same however many blocks there are.
    /// </summary>
    public void MarkBlocks(int? selected)
    {
        _marking = true;
        _markedBlock = selected;
        ShowMarkers();
    }

    /// <summary>Takes the names off the blocks again.</summary>
    public void UnmarkBlocks()
    {
        _marking = false;
        _markedBlock = null;
        ShowMarkers();
    }

    /// <summary>Puts away a block's open list of modes on the edit layer, if there is one.</summary>
    public void DismissFramingMenu() => _edit?.TryDismissMenu();

    private void ShowMarkers()
    {
        // While framing the blocks are the edit layer's, and so are their names.
        if (_edit is { } edit)
        {
            edit.SetMarkers(_marking && _blocks.Count > 0, _markedBlock);
            return;
        }

        bool shown = _marking && _blockWindows.Count > 0;
        foreach (var (id, window) in _blockWindows)
            window.SetMarker(
                shown ? LocalizationService.Format("S.Realtime.BlockNumber", id + 1) : null,
                id == _markedBlock);
    }

    /// <summary>
    /// Hands the modes as they are now to the session — at once, rather than when they have rested
    /// (<see cref="ProfilesSettle"/>). Called before anything that starts the loops again on what
    /// the session holds (繼續, a language change), so they do not start on the modes from before.
    /// </summary>
    private void FlushProfiles()
    {
        _profilesSettle.Stop();
        if (_session is not { } session || !IsActive) return;

        if (session.ChangeProfiles(Regions()) && IsTranslating && !session.IsPaused)
            _loadingHint?.Wait(session.ModelsLoading);
    }

    /// <summary>
    /// Puts every block on what its mode says now, after the modes were changed on the page, without
    /// ending anything.
    /// </summary>
    /// <remarks>
    /// <para>A block whose mode was deleted goes back to 標準 — on the block itself, so the edit layer
    /// and the next sitting show it there too. Each overlay is redrawn in its mode's layout at once,
    /// and the session reads every block again on the new values — see
    /// <see cref="RealtimeTranslationSession.ChangeProfiles"/>, which does nothing when no block's
    /// values really changed.</para>
    ///
    /// <para>The direction is not touched: it is not part of a mode.</para>
    /// </remarks>
    private void ApplyProfiles()
    {
        if (_session is not { } session) return;

        var realtime = SettingsService.Instance.Current.Realtime;
        _blocks = [.. _blocks.Select(block => block with { ProfileId = realtime.KnownProfileId(block.ProfileId) })];

        // While framing, the layer's lists and buttons follow — a mode added or renamed on the page
        // is offered at once, one deleted takes its blocks to 標準 there too. It reports its blocks
        // back through BlocksChanged, which brings _blocks into line with what it shows.
        _edit?.SetProfileChoices(RealtimeProfileEditing.Choices(realtime.Profiles));

        var regions = Regions();
        foreach (var region in regions)
            if (_blockWindows.TryGetValue(region.Id, out var window))
                window.SetLayout(region.Profile.Layout, region.Profile.Align);

        BlocksChanged?.Invoke(this, EventArgs.Empty);

        // A running session reads again once the modes have rested — see ProfilesSettle. Paused or
        // framing, nothing is read, so the session only has to remember them, and does at once.
        if (IsTranslating && !session.IsPaused)
        {
            _profilesSettle.Stop();
            _profilesSettle.Start();
        }
        else
        {
            FlushProfiles();
        }
    }

    /// <summary>
    /// Raised after the running bar's 顯示外觀 panel changed the overlays' look (#355), once the
    /// change is in the settings file — for the page whose card shows it.
    /// </summary>
    public event EventHandler? AppearanceChanged;

    /// <summary>
    /// Redraws the overlays with what 顯示外觀 says now, from the running bar's panel (#355), without
    /// ending anything.
    /// </summary>
    /// <remarks>
    /// <para>The panel has already written the change to the settings file; the request is re-read
    /// from it through the same mapping a start uses (RealtimeQuickStart.WithAppearance), so a
    /// session changed here draws exactly what one started now would.</para>
    ///
    /// <para>Each overlay is restyled where it is, keeping its words — see
    /// RealtimeBlockWindow.ApplyAppearance for why not built again, as 對照顯示 does. The session is not
    /// touched: the words, the languages and the reading are the same, only how they are drawn has
    /// changed. A paused session's overlays are empty and stay so.</para>
    /// </remarks>
    private void ApplyAppearance()
    {
        if (_request is not { } request || _control is null) return;

        // The bar is off the screen while a capture stands on the session, for the reason
        // ToggleCompareDisplay gives.
        if (_interlude.HideLayers) return;

        _request = RealtimeQuickStart.WithAppearance(request, SettingsService.Instance.Current.Realtime);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);

        if (!IsTranslating || _blockWindows.Count == 0) return;

        // Which blocks are drawn as copies is the same answer ShowBlockWindows gave: the switch and
        // each block's own say have not changed.
        var compareOn = _compareDisplay;
        foreach (var (id, window) in _blockWindows)
        {
            bool compared = compareOn && id < _blocks.Count && !_blocks[id].CompareHidden;
            window.ApplyAppearance(RealtimeBlockAppearance.For(_request, compared));
        }

        Log.Info("Realtime appearance changed mid-session; overlays restyled in place");
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
            () => ExclusionList(Volatile.Read(ref _overlayHandles), OwnTopLevelWindows.Visible()));

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
            // Pressed again after this run found the component missing: the same answer as the
            // first time, not the older one about the Windows version.
            refusal = WgcCapability.IsComponentMissing
                ? "S.Realtime.CaptureComponentMissing"
                : "S.Realtime.RealtimeUnsupported";
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
                // Not the display-mode advice: a report came from someone who pressed 開始翻譯 for
                // twenty minutes, restarting their game in between, on a Windows that could not
                // capture any window at all.
                WindowCaptureOutcome.Unavailable => "S.Realtime.CaptureComponentMissing",
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

    // Ahead of the dispatcher's background work, unlike the other callbacks here. Background items
    // wait for the interface to have nothing else to do, and while a session runs it is animating
    // something at every frame. Measured over a page being scrolled, the update that takes a
    // translation off the screen was handed over within a poll and then waited 0.5–1.5 seconds to
    // run, which is the whole of the scroll it was for.
    private void OnRegionUpdated(object? sender, RealtimeRegionUpdate update) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            // Refused from a pass before a pause or a language change (the generation), and from a
            // block's loop that a change of mode has since replaced — see RealtimeRegionUpdate.IsCurrent.
            if (!update.IsCurrent(Volatile.Read(ref _visibleGeneration))) return;

            // The user may have gone back to edit mode while this pass was in flight; its window is
            // gone and the result belongs to a layout that no longer exists.
            if (_blockWindows.TryGetValue(update.RegionId, out var window))
            {
                window.SetLines(update.Lines);
                _shownLines[update.RegionId] = update.Lines;
            }
        });

    private void OnSessionFailed(object? sender, string message) =>
        OnDispatcher(() => _control?.ShowMessage(message, RealtimeMessageKind.Failure));

    // Read when it lands rather than carried with the event: these arrive late and can cross a pause
    // or a restart on the way, and only what the session says now is worth keeping on the bar.
    private void OnFailureChanged(object? sender, EventArgs e) =>
        OnDispatcher(() =>
        {
            if (sender is not RealtimeTranslationSession session || session != _session) return;
            _control?.SetFailure(session.IsPaused ? null : session.CurrentFailure);
        });

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
        // The 模式 panel first, while the layer whose blocks it names is still here to take the
        // names off: it was opened for these blocks, and the bar is about to change its mode anyway.
        if (_edit is not null) _control?.CloseProfiles();

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
        _shownLines.Clear();
        RefreshOverlayHandles();
    }

    /// <summary>
    /// Takes the capture backend out of reach — <see cref="GrabUnderlying"/> sees nothing from here on —
    /// and hands it to the caller to release.
    /// </summary>
    private IRealtimeCaptureBackend? DetachCapture()
    {
        var capture = _capture;
        _capture = null;
        return capture;
    }

    /// <summary>
    /// Releases a capture backend on a pool thread, never waited for here — the backend closes its
    /// capture on its own frame thread; see <see cref="RealtimeCaptureRelease"/>.
    /// </summary>
    private static void ReleaseInBackground(IRealtimeCaptureBackend? capture) =>
        _ = RealtimeCaptureRelease.Release(capture);

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
