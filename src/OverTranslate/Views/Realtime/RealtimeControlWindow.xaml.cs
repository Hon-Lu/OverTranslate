using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OverTranslate.Views.Realtime;

public enum RealtimeControlMode
{
    Edit,
    Running
}

/// <summary>
/// Whether a bar message is answering the user or reporting that something went wrong. Only the
/// second colours the status dot, so the distinction has to be made by the caller — a message that
/// merely reads badly is not a failure.
/// </summary>
public enum RealtimeMessageKind
{
    Info,
    Failure
}

/// <summary>
/// The one piece of chrome that stays on screen for the whole realtime session: a bar while the user
/// is framing blocks, a capsule once translation is running. Draggable, so it can always be moved
/// off whatever it happens to be covering.
/// </summary>
/// <remarks>
/// It is one window across both modes rather than two, so its position survives the switch — a user
/// who moved it out of the way of a subtitle should not find it back in the middle after pressing
/// 編輯. Like the other realtime windows it never takes activation, so it cannot pull focus from a
/// full-screen game; and the capture backend leaves it out of its frames, so it never ends up inside
/// a watched block's own reading.
/// </remarks>
public partial class RealtimeControlWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(2.6);

    // The session's screen, as it is now: replaced when the display changes shape under a running
    // session — see UpdateScreenBounds.
    private System.Drawing.Rectangle _screenBounds;
    private readonly DispatcherTimer _messageTimer;

    // A display mode change arrives as a run of WM_DPICHANGED, and every fix-up waits on WPF's render
    // thread while that thread is rebuilding its device — 0.3 to 1.4s each, measured. One fix-up
    // after the run has settled is all of them that ever mattered.
    private static readonly TimeSpan ScaleFixupSettle = TimeSpan.FromMilliseconds(250);
    private readonly DispatcherTimer _scaleFixupSettle;

    /// <summary>
    /// The marquee the framing button shows while framing is off, and the pointer it shows while it
    /// is on. Coordinates are in a 16×16 box drawn at its own size — see the Path in XAML.
    /// </summary>
    private const string MarqueeIcon =
        "M4.5,2.5 H11.5 A2,2 0 0 1 13.5,4.5 V11.5 A2,2 0 0 1 11.5,13.5 "
        + "H4.5 A2,2 0 0 1 2.5,11.5 V4.5 A2,2 0 0 1 4.5,2.5 Z";

    private const string PointerIcon = "M4.2,2.2 L4.2,12.9 L7,10.2 L8.9,14.4 L10.7,13.5 L8.9,9.6 L12.6,9.4 Z";

    /// <summary>
    /// Dash pattern for the marquee, in multiples of the stroke width. The same broken outline the
    /// drag preview draws on screen, which is what makes the icon read as that gesture rather than
    /// as a plain box.
    /// </summary>
    private static readonly DoubleCollection MarqueeDashes = Freeze([2.2, 1.8]);

    private static DoubleCollection Freeze(double[] values)
    {
        var collection = new DoubleCollection(values);
        collection.Freeze();
        return collection;
    }

    private RealtimeControlMode _mode = RealtimeControlMode.Edit;
    private static string EditHint => LocalizationService.Get("S.Realtime.EditHint");
    // Stands in for the framing instruction while framing is off, because that instruction is then
    // wrong: dragging does nothing, and a bar still telling the user to drag reads as the layer
    // having broken rather than as a state they asked for.
    private static string CrosshairOffHint => LocalizationService.Get("S.Realtime.CrosshairOffHint");
    // What the capsule says before SetLanguages has named the pair. A transient message borrows the
    // same slot and RestoreText brings whichever of the two applies back.
    private static string RunStatus => LocalizationService.Get("S.Realtime.Running");
    // Not transient, unlike everything else that lands in that slot: a paused session looks exactly
    // like a session with nothing to translate — no words, no scrims, a still screen — so the one
    // thing that separates them has to stay on screen for as long as the state lasts.
    private static string PausedStatus => LocalizationService.Get("S.Realtime.Paused");
    // Lasts exactly as long as the load it is about; a message borrowing the slot meanwhile hands it
    // back to this. 已暫停 wins over it — see SetLoadingModels.
    private static string LoadingModelsStatus => LocalizationService.Get("S.Realtime.LoadingModels");
    private bool _hasLanguages;
    // The codes behind the two names, for the lists to mark and for a pick to be told apart from
    // the language already in use.
    private string _sourceCode = "";
    private string _targetCode = "";

    // The language list dropped from one of the two names, while it is open (#345).
    private RealtimeLanguageMenu? _menu;
    // Which list a press on the capsule just closed, so the click that press ends in does not open
    // the same list again: pressing an open list's own name closes it, as a menu button does.
    private RealtimeLanguageMenuKind? _menuClosedByPress;
    // 顯示外觀, while it is open (#355).
    private RealtimeAppearancePanel? _appearance;
    private bool _isPaused;
    private bool _isLoadingModels;
    // What the session last said through SetBusy; the dot also pulses while the models load.
    private bool _busy;

    // Whether the capsule is folded down to its dot, toggle and ✕. Edit mode ignores it: the bar
    // there is the framing controls, every one of which is in use while framing.
    private bool _collapsed;

    // Whether the edit layer is still taking the mouse. Owned here rather than by the layer: the
    // button that changes it lives on this bar, and the bar has to say so in its own hint text.
    private bool _crosshairEnabled = true;

    // Whether a message is standing in for the bar's own text. Kept explicitly rather than read off
    // the timer, which stopped being the same question once a message could be sticky: a sticky one
    // is showing with no timer running, and the timer check would have quietly restored the bar
    // underneath it.
    private bool _messageShowing;

    // A failure the session is still retrying, and whether it is what the bar shows right now. Kept
    // apart from the transient messages so one of those passing over it puts it back when it times
    // out, and so clearing it never takes down a message that is not it.
    private readonly RealtimeFailureNotice _failure = new();
    private bool _failureShowing;

    // Whether the press under way began on a message in the pair's place: released without a drag,
    // it clicks the message away — see DismissMessage.
    private bool _pressOnMessage;

    // The scale WPF renders this window at, which is the DPI of whatever monitor it currently sits
    // on — not necessarily the one the session runs on. Everything that converts between the window's
    // DIP and screen pixels has to use this one, and it has to be re-read whenever Windows rescales
    // the window: unlike the edit and block layers, this bar moves, so it does not get to freeze its
    // scale by refusing WM_DPICHANGED. See OnDpiChanged.
    private double _windowScale = 1.0;

    // True while the scale is being re-applied, so the width change that causes is not mistaken for
    // the bar's text having grown.
    private bool _adjustingForScale;

    // One fix-up pass is enough however many times the scale changed before it runs.
    private bool _scaleFixupQueued;

    private System.Drawing.Point _position;

    // False until PlaceInitially has run. The bar is shown before it is placed, so the sizing passes
    // before that have no resting position to hold the right edge of.
    private bool _placed;

    private bool _isDragging;
    private System.Drawing.Point _dragStartMouse;
    private System.Drawing.Point _dragStartPosition;

    public RealtimeControlWindow(System.Drawing.Rectangle screenBounds)
    {
        InitializeComponent();

        _screenBounds = screenBounds;
        ApplyPauseButton();
        ApplyCrosshairButton();
        ApplyCollapsed();
        RestoreText();
        InitializeLabels();

        _messageTimer = new DispatcherTimer { Interval = MessageDuration };
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); RestoreText(); };

        _scaleFixupSettle = new DispatcherTimer { Interval = ScaleFixupSettle };
        _scaleFixupSettle.Tick += (_, _) => { _scaleFixupSettle.Stop(); QueueScaleFixup(); };
        Closed += (_, _) =>
        {
            _scaleFixupSettle.Stop();
            CloseMenu("bar closed");
            CloseAppearance();
        };

        // A list left open over a capsule that has gone away — a capture standing the session down —
        // would be a menu for a bar that is not there. The same for 顯示外觀.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            CloseMenu("bar hidden");
            CloseAppearance();
        };

        // 顯示外觀 is set against the capsule, so it goes wherever the capsule goes: pushed along by
        // its own width changing, or held back into the screen. A drag closes it instead — see
        // Chrome_MouseLeftButtonDown.
        LocationChanged += (_, _) => FollowCapsule();
        SizeChanged += (_, _) => FollowCapsule();

        // The capsule never activates, so pressing it does not take activation from an open list and
        // the list would never hear about the press. It is told here instead: any press on the
        // capsule is a press outside the list.
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            _menuClosedByPress = _menu?.Kind;
            CloseMenu("press on the bar");
        };

        Loaded += (_, _) =>
        {
            ReadWindowScale();
            ApplyScreenScale();
            PlaceInitially();

            // The bar is shown before it is placed, so it opens on whichever monitor WPF picked and
            // is then moved onto the session's. Crossing to a monitor at another scale makes Windows
            // rescale it, which invalidates everything measured above — so re-apply once it has
            // landed. Same inputs, so it is a no-op on a uniform desktop. The move usually raises
            // WM_DPICHANGED and OnDpiChanged queues this anyway; queueing it here as well costs one
            // no-op pass and covers the case where the scale changed without the message arriving.
            QueueScaleFixup();
        };

    }

    public event EventHandler? StartRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? PauseToggleRequested;

    /// <summary>
    /// Raised when the user asks for 截圖翻譯 from the running capsule. The session is not this
    /// window's to stand down, so the request goes out like every other one here.
    /// </summary>
    public event EventHandler? CaptureRequested;

    /// <summary>
    /// Raised with the state framing should be in from now on: true to take the mouse back for
    /// drawing blocks, false to hand it to whatever is underneath.
    /// </summary>
    public event EventHandler<bool>? CrosshairToggleRequested;

    /// <summary>
    /// Raised when either 對照顯示 button is pressed. The new state is the session's to decide and
    /// report back through <see cref="SetCompareDisplay"/>: it is kept in the settings file and has
    /// to reach whichever layers are up, none of which this window owns.
    /// </summary>
    public event EventHandler? CompareDisplayToggleRequested;

    /// <summary>
    /// Raised with the capsule's new state when the user collapses or expands it, for the session
    /// to keep between sittings. Not raised by <see cref="SetCollapsed"/>.
    /// </summary>
    public event EventHandler<bool>? CollapsedChanged;

    /// <summary>
    /// Raised with the pair the user picked from one of the two language lists — the half they did
    /// not touch unchanged. The session decides what follows and reports the pair back through
    /// <see cref="SetLanguages"/>.
    /// </summary>
    public event EventHandler<(string Source, string Target)>? LanguagesChangeRequested;

    /// <summary>
    /// Raised when the 顯示外觀 panel has written a change to the settings file (#355), for the
    /// session to draw its overlays with.
    /// </summary>
    public event EventHandler? AppearanceChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // No WDA_EXCLUDEFROMCAPTURE here any more: this bar stays out of what the loop reads because
        // the capture backend leaves it out, not because the bar asked to be invisible (#105).
        WindowStyles.ApplyNoActivate(this);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(OnWindowPosChanging);
    }

    /// <summary>
    /// Holds the right edge of the bar still while its width changes.
    /// </summary>
    /// <remarks>
    /// <para>The bar is sized to its content, so every change of text changes its width — a status
    /// message replacing another, the language pair giving way to 已暫停, a block count going from one
    /// digit to two. Everything that changes width sits to the left of the buttons, and the buttons
    /// are what the pointer is on: pinning the left edge hands the whole change to them, pinning the
    /// middle hands them half of it. Pausing swaps a language pair for one short word, so either way
    /// the button under the pointer walks out from under it and the user cannot press the same spot
    /// again to resume — which is the one thing somebody who has just pressed pause is most likely to
    /// want. Pinning the right edge spends the movement on the left end instead, where the dot and
    /// the status text live. Those are read, not aimed at.</para>
    ///
    /// <para>Caught here rather than in <c>SizeChanged</c>, which is where this started and is what
    /// made it flicker. WPF resizes the HWND first and raises SizeChanged after, so a correction from
    /// there is a second SetWindowPos behind the first: for one frame the bar is drawn already grown
    /// and not yet moved, and the wider the change the further it visibly jumps before snapping back.
    /// Editing the rectangle Windows is about to use makes the move and the resize one operation, so
    /// there is no such frame. Same reason and same shape as QuickLookupWindow, which pins its top
    /// edge this way.</para>
    ///
    /// <para>Centring is still how the bar is first placed — see <see cref="PlaceInitially"/>. That
    /// is a starting position, not a rule about how it grows.</para>
    /// </remarks>
    private IntPtr OnWindowPosChanging(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging) return IntPtr.Zero;

        // A drag is the user placing the bar, and a scale fix-up changes the width in DIP without the
        // text having changed at all — holding an edge through either would walk the bar sideways.
        if (!_placed || _isDragging || _adjustingForScale) return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        // Pure moves come through here too — our own clamps and placements are exactly that — and
        // answering them would be a loop.
        if ((pos.flags & SwpNoSize) != 0) return IntPtr.Zero;

        var bounds = ScreenGeometry.PhysicalBounds(this);
        if (bounds.IsEmpty || pos.cx == bounds.Width) return IntPtr.Zero;

        // x and y are undefined when the caller said not to move, so where the window is now is what
        // the new width has to be measured against.
        var top = (pos.flags & SwpNoMove) == 0 ? pos.y : bounds.Top;

        // The same clamp ClampIntoScreen applies, done here because doing it afterwards would be the
        // second SetWindowPos this exists to avoid.
        var left = Math.Clamp(
            bounds.Right - pos.cx,
            _screenBounds.Left,
            Math.Max(_screenBounds.Left, _screenBounds.Right - pos.cx));

        pos.x = left;
        pos.y = top;
        pos.flags &= ~SwpNoMove;
        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);

        // Dragging reads this back as where the bar is, so it has to follow what was just applied.
        _position = new System.Drawing.Point(left, top);
        return IntPtr.Zero;
    }

    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    /// <summary>
    /// Windows has rescaled the bar — it was moved onto a monitor at another scale, or the user
    /// changed that monitor's scaling while a session was running.
    /// </summary>
    /// <remarks>
    /// Everything this window measures is relative to <see cref="_windowScale"/>: the compensating
    /// transform in <see cref="ApplyScreenScale"/>, and the DIP → pixel conversions behind
    /// <see cref="PhysicalWidth"/>. Left at the value read when the bar opened, the transform would
    /// multiply a scale WPF has already applied — a bar 1.5× too big on a 150% monitor opened from a
    /// 100% one — and every physical measurement taken of it would be wrong by the same factor.
    ///
    /// Deferred rather than done here: this arrives inside the SetWindowPos that moved the window,
    /// and re-placing it from within that call would be undone by the rest of the move as the stack
    /// unwinds. Running at Loaded priority puts it after the window has settled, which is the same
    /// answer the capture toolbar reached for the same crossing.
    /// </remarks>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Restarted on every change, so a run of them ends in one fix-up — see ScaleFixupSettle.
        _scaleFixupSettle.Stop();
        _scaleFixupSettle.Start();
    }

    /// <summary>
    /// Puts the bar back on the session's screen after that screen changed shape.
    /// </summary>
    /// <remarks>
    /// A game switching to fullscreen at a lower resolution takes the monitor with it — 2560x1440 to
    /// 1280x720, measured — and the bar, placed against the old rectangle, was left at a position
    /// that no longer exists: off the screen, with 結束 on it. Clamped into the new rectangle it
    /// stays where the user can reach it, and goes no further than that: the position is not reset,
    /// so a bar the user dragged somewhere stays as close to there as the screen allows.
    /// </remarks>
    public void UpdateScreenBounds(System.Drawing.Rectangle screenBounds)
    {
        if (screenBounds == _screenBounds || screenBounds.IsEmpty) return;

        _screenBounds = screenBounds;
        if (_placed) ClampIntoScreen();
    }

    private void QueueScaleFixup()
    {
        if (_scaleFixupQueued) return;

        _scaleFixupQueued = true;
        Dispatcher.BeginInvoke(new Action(ApplyCurrentScale), DispatcherPriority.Loaded);
    }

    private void ApplyCurrentScale()
    {
        _scaleFixupQueued = false;
        if (!IsLoaded) return;

        _adjustingForScale = true;
        try
        {
            ReadWindowScale();
            ApplyScreenScale();

            // Before the width is measured in ClampIntoScreen: the transform just set is what decides
            // it, and an un-run layout pass would place the bar by its previous size.
            UpdateLayout();
            ClampIntoScreen();
        }
        finally
        {
            _adjustingForScale = false;
        }
    }

    private void ReadWindowScale()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;
    }

    /// <summary>
    /// Puts the session's language pair where the running capsule's status text was.
    /// </summary>
    /// <remarks>
    /// The pulsing dot beside it already says the session is running, so "即時翻譯中" was a second
    /// way of saying the same thing, occupying the one line the user can see while the translation
    /// window is hidden. The pair is the thing that cannot be checked anywhere else and the one
    /// setting that fails silently when it is wrong: recognition told to read the wrong script does
    /// not raise an error, it returns plausible nonsense, and this is where that is caught at a glance.
    /// </remarks>
    public void SetLanguages(string sourceCode, string targetCode)
    {
        _sourceCode = sourceCode;
        _targetCode = targetCode;
        SourceLangText.Text = Models.LanguageData.GetSourceDisplayName(sourceCode);
        TargetLangText.Text = Models.LanguageData.GetTargetDisplayName(targetCode);
        _hasLanguages = SourceLangText.Text.Length > 0 && TargetLangText.Text.Length > 0;

        if (!_messageShowing) RestoreText();
    }

    /// <summary>
    /// Shows the session as paused or running: the button's action, the status text and the dot.
    /// </summary>
    /// <remarks>
    /// All three, because each answers a different question at a different distance. The dot is the
    /// only part readable at a glance from across a full-screen window and says something is or is
    /// not happening; the text says which, in a word, without the user having to remember what grey
    /// means; the button says what pressing it would do. A pause that only greyed the dot was read
    /// as the session having died.
    /// </remarks>
    public void SetPaused(bool paused)
    {
        _isPaused = paused;

        ApplyPauseButton();
        // A message from before the pause has nothing to say about the state that replaced it, and
        // its timer would restore the old text on top of the new state a moment later. Nor does a
        // failure: nothing is retried while paused, and a resumed session reports its own.
        _messageTimer.Stop();
        _failure.Set(null);
        RestoreText();
        // Nothing is in flight once paused, and a pulse left running would go on saying there is.
        SetBusy(false);
    }

    /// <summary>
    /// Says 載入模型中… in the pair's place, with the dot pulsing, until called with false. Paused,
    /// the bar says 已暫停 instead; in edit mode the running capsule is not on screen at all.
    /// </summary>
    public void SetLoadingModels(bool loading)
    {
        if (_isLoadingModels == loading) return;
        _isLoadingModels = loading;

        ApplyPulse();
        if (!_messageShowing) RestoreText();
    }

    public void SetMode(RealtimeControlMode mode)
    {
        _mode = mode;
        CloseMenu($"mode {mode}");
        CloseAppearance();
        Labels.Hide();
        EditPanel.Visibility = mode == RealtimeControlMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        RunPanel.Visibility = mode == RealtimeControlMode.Running ? Visibility.Visible : Visibility.Collapsed;

        // Both modes are entered by the session doing something — framing or starting — and neither
        // is a paused one. Going back to edit mode from a paused session is the case this covers.
        _isPaused = false;
        ApplyPauseButton();

        // Every trip into edit mode starts with the crosshair, whatever the last one ended on: the
        // layer is rebuilt each time, so it opens taking the mouse, and the user pressed 編輯
        // because they came to draw. Off is the temporary state, not the one to inherit.
        _crosshairEnabled = true;
        ApplyCrosshairButton();

        _messageTimer.Stop();
        _failure.Set(null);
        RestoreText();
        SetBusy(false);

        // The capsule is far narrower than the bar, so the window's size changes under a position
        // that was valid for the other mode.
        Dispatcher.BeginInvoke(new Action(ClampIntoScreen), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Shows 對照顯示 as on or off on both of its buttons — the edit bar's and the capsule's, which
    /// are one switch seen from two modes.
    /// </summary>
    /// <remarks>
    /// On is the accent on a filled square, the way a toggle on a toolbar says it is held down; off
    /// is the bar's ordinary icon. Colour alone would do on the capsule, but the edit bar already has
    /// an accent-coloured glyph — the start triangle — and a second one would read as a second way
    /// to start. The square is what makes this one a state rather than an action. The tooltip leaves
    /// the state to the square and says what a press will do: it used to open with the state word,
    /// and "對照顯示：關閉" on a switch that was off read as what clicking it would do.
    /// </remarks>
    public void SetCompareDisplay(bool enabled)
    {
        var tooltip = LocalizationService.Get(
            enabled ? "S.Realtime.CompareDisplayOnHint" : "S.Realtime.CompareDisplayOffHint");

        foreach (var button in new[] { CompareEditBtn, CompareRunBtn })
        {
            if (enabled)
            {
                button.SetResourceReference(ForegroundProperty, "AppAccent");
                button.SetResourceReference(BackgroundProperty, "ToolbarGroupBg");
            }
            else
            {
                button.ClearValue(ForegroundProperty);
                button.ClearValue(BackgroundProperty);
            }

            button.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(
                button, LocalizationService.Get("S.Realtime.CompareDisplay"));
            System.Windows.Automation.AutomationProperties.SetItemStatus(
                button, LocalizationService.Get(enabled ? "S.Realtime.CompareDisplayOn" : "S.Realtime.CompareDisplayOff"));
        }
    }

    /// <summary>
    /// Folds the running capsule down to its dot, the expand button and ✕, or opens it again.
    /// </summary>
    /// <remarks>
    /// <para>For a session left running over a game or a video, where even the capsule is in the
    /// way. What stays is chosen so collapsed is a state the user can live in rather than pass
    /// through: the dot still pulses and still turns red on a failure, which is the one thing worth
    /// noticing from across the screen, and ✕ is there so ending the session does not mean opening
    /// it first. Everything else — the pair, 截圖翻譯, 暫停, 編輯 — is one press away. 暫停 is
    /// left out on purpose: it has its own shortcut, and a fourth button would undo most of what
    /// collapsing saves.</para>
    ///
    /// <para>The words the capsule would have shown move to its tooltip, so a red dot can still be
    /// read without expanding. No size animation: this is a layered window, and stepping its size
    /// frame by frame costs more than the motion is worth. The right edge is pinned as the width
    /// changes, so the toggle and ✕ stay under the pointer either way.</para>
    /// </remarks>
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        ApplyCollapsed();
    }

    // In the glyph's 14×14 box: 4.5 wide and 10 tall, centred on (7, 7) so the ink sits on the
    // button's centre in both directions.
    private static readonly Geometry CollapseChevron = FreezeGeometry("M4.75,2 L9.25,7 L4.75,12");
    private static readonly Geometry ExpandChevron = FreezeGeometry("M9.25,2 L4.75,7 L9.25,12");

    private static Geometry FreezeGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void ApplyCollapsed()
    {
        RunDetails.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;

        // › to fold towards the right edge, ‹ to grow back out to the left.
        CollapseGlyph.Data = _collapsed ? ExpandChevron : CollapseChevron;
        var label = LocalizationService.Get(_collapsed ? "S.Realtime.ExpandBar" : "S.Realtime.CollapseBar");
        CollapseBtn.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CollapseBtn, label);

        UpdateCollapsedToolTip();
        CloseMenuIfPairHidden();

        // Its button has gone with the rest of the capsule.
        if (_collapsed) CloseAppearance();
    }

    /// <summary>
    /// Puts whatever the capsule's text slot is saying on the capsule's tooltip while that slot is
    /// hidden. The buttons keep their own tooltips; this one answers for the dot and the space
    /// around it.
    /// </summary>
    private void UpdateCollapsedToolTip()
    {
        if (!_collapsed)
        {
            RunPanel.ToolTip = null;
            return;
        }

        var pair = $"{SourceLangText.Text} → {TargetLangText.Text}";
        RunPanel.ToolTip = LangPairPanel.Visibility != Visibility.Visible
            ? RunStatusText.Text
            : RunStatusText.Visibility == Visibility.Visible ? $"{RunStatusText.Text} · {pair}" : pair;
    }

    // Spaced either side of the slash: at 12px the bare 1/1 reads as one glyph cluster rather than
    // as a count out of a limit, which is the one thing this line is here to say. Named, too: the
    // limit is no longer one the user set on the page, so a bare 1 / 3 could as well be a step or a
    // page number.
    public void SetBlockCount(int count, int max) =>
        BlockCountText.Text = "· " + LocalizationService.Format("S.Realtime.BlockCountProgress", count, max);

    /// <summary>Pulses the status dot while a recognition or translation pass is in flight.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyPulse();
    }

    private void ApplyPulse()
    {
        // A pass cancelled by 暫停 can report itself busy on its way out; the dot is saying
        // something else now.
        if ((_busy || _isLoadingModels) && !_isPaused)
        {
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 1,
                To = 0.3,
                Duration = new Duration(TimeSpan.FromMilliseconds(620)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            return;
        }

        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    /// <summary>
    /// Replaces the bar's own text. Transient by default: these are answers to something the user
    /// just did (a refused drag, an engine that failed), not state worth keeping on screen.
    /// </summary>
    /// <param name="sticky">
    /// Keeps the message up until something else replaces it, for the one kind that is not an answer
    /// but an instruction: a session that could not start and names what to do instead. That
    /// instruction is 結束即時翻譯 and then change 擷取來源 — a page this session has hidden behind
    /// the shell window — so the user cannot act on it without first leaving the screen the message
    /// is on. Timed out after a couple of seconds it would be gone before they got back, with
    /// nothing left on screen to say why nothing happened.
    /// </param>
    public void ShowMessage(
        string message, RealtimeMessageKind kind = RealtimeMessageKind.Info, bool sticky = false)
    {
        if (_mode == RealtimeControlMode.Edit)
        {
            EditHintText.Text = message;
        }
        else
        {
            // A message is a sentence, not a pair, so it takes the whole slot back for its duration.
            LangPairPanel.Visibility = Visibility.Collapsed;
            RunStatusText.Visibility = Visibility.Visible;
            RunStatusText.Text = message;

            // Same frame as the text it belongs to. The dot is the one thing on this bar visible at
            // a glance from across a full-screen window, so a failure is worth spending its colour
            // on; it goes back to accent when the message does.
            SetDotState(kind == RealtimeMessageKind.Failure);
            UpdateCollapsedToolTip();
            CloseMenuIfPairHidden();

            RunStatusText.Cursor = System.Windows.Input.Cursors.Hand;
            RunStatusText.SetResourceReference(ToolTipProperty, "S.Realtime.DismissMessage");
        }

        _messageShowing = true;
        _failureShowing = false;
        _messageTimer.Stop();
        if (!sticky) _messageTimer.Start();
    }

    /// <summary>
    /// Shows a failure the session is still retrying until it is cleared with null, rather than for
    /// the couple of seconds a message lasts: the retry can be sixteen seconds away, and a bar that
    /// had gone back to the language pair in the meantime read as the trouble being over.
    /// </summary>
    /// <remarks>Only while running and not paused; both of those leaving clear it.</remarks>
    public void SetFailure(string? message)
    {
        if (!_failure.Set(message)) return;
        if (_mode != RealtimeControlMode.Running || _isPaused) return;

        if (message is not null) ShowFailure();
        // A transient message showing puts the bar back itself when it times out; otherwise the bar
        // may be the pair under a dot still red for a failure that was clicked away.
        else if (_failureShowing || !_messageShowing) RestoreText();
    }

    private void ShowFailure()
    {
        ShowMessage(_failure.Message!, RealtimeMessageKind.Failure, sticky: true);
        _failureShowing = true;
    }

    /// <summary>
    /// Re-asserts the bar above a window created after it — the edit layer, on re-entry, or overlays
    /// rebuilt mid-session — and 顯示外觀 with it, which would otherwise sit under them until the
    /// session's next re-assert.
    /// </summary>
    public void BringToFront()
    {
        Topmost = false;
        Topmost = true;
        if (_appearance is { } panel) AlwaysOnTop.Reassert(panel);
    }

    private void RestoreText()
    {
        // The bar's own text, unless a failure is still being retried: then that is its own text.
        var failing = _failure.Message is not null && _mode == RealtimeControlMode.Running && !_isPaused;
        if (failing && _failure.StandsInForPair)
        {
            ShowFailure();
            return;
        }

        _messageShowing = false;
        _failureShowing = false;
        EditHintText.Text = _crosshairEnabled ? EditHint : CrosshairOffHint;

        RunStatusText.Text = _isPaused ? PausedStatus : _isLoadingModels ? LoadingModelsStatus : RunStatus;

        // Paused, the pair stays beside 已暫停 rather than giving way to it: the pair is the one place
        // the languages can be changed from (#345), and a paused session is where someone who noticed
        // the wrong one is most likely to stop and fix it before carrying on.
        var showPair = _hasLanguages && !_isLoadingModels;
        LangPairPanel.Visibility = showPair ? Visibility.Visible : Visibility.Collapsed;
        RunStatusText.Visibility = showPair && !_isPaused ? Visibility.Collapsed : Visibility.Visible;

        // Red still, for a failure that was clicked away: it is set aside, not over.
        SetDotState(failed: failing);
        UpdateCollapsedToolTip();
        CloseMenuIfPairHidden();

        RunStatusText.Cursor = null;
        RunStatusText.ClearValue(ToolTipProperty);
    }

    /// <summary>
    /// Takes a message off the pair's place on a click. The one that matters is the failure being
    /// retried, which otherwise stays until it clears — and when it is a service refusing the
    /// language pair, it never does, while covering the pair the user would change to fix it.
    /// </summary>
    private void DismissMessage()
    {
        if (!_messageShowing) return;
        if (_failureShowing) _failure.Dismiss();
        _messageTimer.Stop();
        RestoreText();
    }

    // ── Language lists (#345) ────────────────────────────────────────────────────────────────────

    private void SourceLangBtn_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu(RealtimeLanguageMenuKind.Source, SourceLangBtn);

    private void TargetLangBtn_Click(object sender, RoutedEventArgs e) =>
        ToggleMenu(RealtimeLanguageMenuKind.Target, TargetLangBtn);

    private void ToggleMenu(RealtimeLanguageMenuKind kind, System.Windows.Controls.Button button)
    {
        // The press that started this click has already closed whatever list was open; when it was
        // this one, closing it was the whole answer.
        var closedByPress = _menuClosedByPress;
        _menuClosedByPress = null;
        if (closedByPress == kind)
        {
            Log.Info("Realtime language list {Kind}: button pressed, the press closed this list so it stays closed", kind);
            return;
        }

        Log.Info(
            "Realtime language list {Kind}: button pressed, mode={Mode} paused={Paused} collapsed={Collapsed} pairVisible={PairVisible} open={Open}",
            kind, _mode, _isPaused, _collapsed, LangPairPanel.Visibility == Visibility.Visible, _menu?.Kind);

        CloseMenu("another list opening");
        // One thing open over the capsule at a time: the two would open into the same space.
        CloseAppearance();

        var menu = new RealtimeLanguageMenu(
            kind,
            kind == RealtimeLanguageMenuKind.Source ? _sourceCode : _targetCode,
            PhysicalRect(button),
            PhysicalRect(Chrome),
            _screenBounds);
        menu.LanguagePicked += (_, code) => OnLanguagePicked(kind, code);
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_menu, menu)) return;
            _menu = null;
            button.Tag = null;
        };

        _menu = menu;
        button.Tag = "Open";
        menu.Show();
    }

    private void OnLanguagePicked(RealtimeLanguageMenuKind kind, string code)
    {
        var source = kind == RealtimeLanguageMenuKind.Source ? code : _sourceCode;
        var target = kind == RealtimeLanguageMenuKind.Target ? code : _targetCode;

        // Picking the language already in use is a list closed, not a change.
        if (string.Equals(source, _sourceCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target, _targetCode, StringComparison.OrdinalIgnoreCase)) return;

        LanguagesChangeRequested?.Invoke(this, (source, target));
    }

    // A list belongs to the name it dropped from; once that name is off the capsule — a message in
    // its place, 載入模型中…, the capsule collapsed — the list is pointing at nothing.
    private void CloseMenuIfPairHidden()
    {
        if (_menu is not null && (LangPairPanel.Visibility != Visibility.Visible || _collapsed))
            CloseMenu(_collapsed ? "bar collapsed" : "pair hidden");
    }

    /// <param name="reason">What closed it, for the log (#358).</param>
    private void CloseMenu(string reason)
    {
        var menu = _menu;
        if (menu is null) return;
        _menu = null;
        SourceLangBtn.Tag = null;
        TargetLangBtn.Tag = null;
        menu.Dismiss(giveBackForeground: true, reason);
    }

    // ── 顯示外觀 (#355) ─────────────────────────────────────────────────────────────────────────

    private void AppearanceBtn_Click(object sender, RoutedEventArgs e)
    {
        // Pressing it again is how the panel is put away: it stays open otherwise, so one value after
        // another can be tried against the translation.
        if (_appearance is not null)
        {
            CloseAppearance();
            return;
        }

        CloseMenu("appearance panel opening");

        var panel = new RealtimeAppearancePanel(PhysicalRect(Chrome), _screenBounds);
        panel.AppearanceChanged += (_, _) => AppearanceChanged?.Invoke(this, EventArgs.Empty);
        panel.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_appearance, panel)) return;
            _appearance = null;
            SetAppearanceOpen(false);
        };

        _appearance = panel;
        SetAppearanceOpen(true);
        panel.Show();
    }

    private void CloseAppearance()
    {
        var panel = _appearance;
        if (panel is null) return;
        _appearance = null;
        SetAppearanceOpen(false);
        panel.Dismiss();
    }

    private void FollowCapsule()
    {
        if (_appearance is { } panel && IsLoaded) panel.UpdateAnchor(PhysicalRect(Chrome));
    }

    /// <summary>The button held down while its panel is open, in 原文對照's way of showing a state.</summary>
    private void SetAppearanceOpen(bool open)
    {
        if (open)
        {
            AppearanceBtn.SetResourceReference(ForegroundProperty, "AppAccent");
            AppearanceBtn.SetResourceReference(BackgroundProperty, "ToolbarGroupBg");
        }
        else
        {
            AppearanceBtn.ClearValue(ForegroundProperty);
            AppearanceBtn.ClearValue(BackgroundProperty);
        }
    }

    // Screen pixels, through WPF's own transform to the device — which already carries this
    // window's LayoutTransform and render scale.
    private static System.Drawing.Rectangle PhysicalRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
        return System.Drawing.Rectangle.FromLTRB(
            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
    }

    // SetResourceReference rather than an assigned brush: the theme can still be switched from the
    // shell while a session runs, and a snapshotted brush would keep the old theme's red.
    //
    // Paused is the secondary text brush rather than a colour of its own: this bar has no amber, and
    // inventing one for a state the user asked for would read as a warning. Grey is the absence of
    // the accent — which is exactly what a paused session is — and it stays legible in both themes.
    private void SetDotState(bool failed) =>
        StatusDot.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty,
            failed
                ? "AppError"
                : _isPaused ? "AppTextSecondary" : "AppAccent");

    private void ApplyPauseButton()
    {
        // Segoe Fluent Icons: play to resume, pause to stop. The glyph is what the press does.
        PauseBtn.Content = _isPaused ? "\uE768" : "\uE769";
        PauseBtn.ToolTip = RealtimePauseHint.ForControlTooltip(RealtimePauseHint.CurrentHotkey, _isPaused);
        System.Windows.Automation.AutomationProperties.SetName(
            PauseBtn, LocalizationService.Get(_isPaused ? "S.Realtime.Resume" : "S.Realtime.Pause"));
    }

    private void ApplyCrosshairButton()
    {
        // The icon is the action, not the state — see the button's comment in XAML. The dashes go
        // with the marquee and have to come off the pointer, which is one continuous outline.
        CrosshairGlyph.Data = Geometry.Parse(_crosshairEnabled ? PointerIcon : MarqueeIcon);
        CrosshairGlyph.StrokeDashArray = _crosshairEnabled ? null : MarqueeDashes;

        var label = LocalizationService.Get(
            _crosshairEnabled ? "S.Realtime.CrosshairOff" : "S.Realtime.CrosshairOn");
        CrosshairBtn.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CrosshairBtn, label);
    }

    // ── Placement and dragging ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the bar for the monitor it will sit on rather than the one WPF measured it against.
    /// The two are the same on a uniform desktop, so this is a no-op there; on a mixed-DPI one it is
    /// the difference between a bar at the right physical size and one at another monitor's scale.
    /// </summary>
    private void ApplyScreenScale()
    {
        double targetScale = ScreenGeometry.ScaleAt(
            _screenBounds.Left + _screenBounds.Width / 2,
            _screenBounds.Top + _screenBounds.Height / 2);

        double relative = targetScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);
    }

    private void PlaceInitially()
    {
        UpdateLayout();

        double targetScale = ScreenGeometry.ScaleAt(
            _screenBounds.Left + _screenBounds.Width / 2,
            _screenBounds.Top + _screenBounds.Height / 2);

        // Bottom centre, which does sit where subtitles do — the top edge was tried and read as
        // out of the way to the point of being out of mind, and a control the user forgets is there
        // is worse than one they move once. Dragging is the answer to the overlap, and the position
        // survives switching modes so it only has to be done once a session. The 48px gap is scaled
        // by the target monitor so it reads the same distance up on every display.
        _position = new System.Drawing.Point(
            _screenBounds.Left + (_screenBounds.Width - PhysicalWidth) / 2,
            _screenBounds.Bottom - PhysicalHeight - (int)Math.Round(48 * targetScale));

        ClampIntoScreen();
        _placed = true;
    }

    private void ClampIntoScreen()
    {
        int x = Math.Clamp(
            _position.X, _screenBounds.Left, Math.Max(_screenBounds.Left, _screenBounds.Right - PhysicalWidth));
        int y = Math.Clamp(
            _position.Y, _screenBounds.Top, Math.Max(_screenBounds.Top, _screenBounds.Bottom - PhysicalHeight));

        _position = new System.Drawing.Point(x, y);
        ScreenGeometry.MoveToPhysical(this, x, y);
    }

    // ActualWidth already includes the LayoutTransform above, so the window's own render scale is
    // all that stands between it and screen pixels.
    private int PhysicalWidth => (int)Math.Round(ActualWidth * _windowScale);
    private int PhysicalHeight => (int)Math.Round(ActualHeight * _windowScale);

    // Physical pixels throughout, taken from the cursor rather than from WPF: the window carries its
    // own position in screen pixels, and mixing in a DIP delta would drift on a scaled display.
    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOnMessage = _messageShowing && _mode == RealtimeControlMode.Running
            && ReferenceEquals(e.OriginalSource, RunStatusText);

        // A press on the bar itself, not one of its buttons, is a press outside 顯示外觀, as it is
        // for the lists: put away rather than dragged along, where the bar could end up under it.
        CloseAppearance();

        _isDragging = true;
        _dragStartMouse = System.Windows.Forms.Control.MousePosition;
        _dragStartPosition = _position;
        ((UIElement)sender).CaptureMouse();
    }

    private void Chrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var mouse = System.Windows.Forms.Control.MousePosition;
        _position = new System.Drawing.Point(
            _dragStartPosition.X + (mouse.X - _dragStartMouse.X),
            _dragStartPosition.Y + (mouse.Y - _dragStartMouse.Y));
        ClampIntoScreen();
    }

    private void Chrome_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();

        // A press that went nowhere was a click; one that moved the bar was a drag, whatever it began on.
        var mouse = System.Windows.Forms.Control.MousePosition;
        var slop = SystemParameters.MinimumHorizontalDragDistance * _windowScale;
        var clicked = Math.Abs(mouse.X - _dragStartMouse.X) <= slop && Math.Abs(mouse.Y - _dragStartMouse.Y) <= slop;
        if (_pressOnMessage && clicked) DismissMessage();
        _pressOnMessage = false;
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void EditBtn_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private void PauseBtn_Click(object sender, RoutedEventArgs e) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);

    private void CaptureBtn_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(this, EventArgs.Empty);

    private void CompareBtn_Click(object sender, RoutedEventArgs e) =>
        CompareDisplayToggleRequested?.Invoke(this, EventArgs.Empty);

    private void CrosshairBtn_Click(object sender, RoutedEventArgs e)
    {
        _crosshairEnabled = !_crosshairEnabled;
        ApplyCrosshairButton();

        // A message from before the toggle was answering something else, and the hint underneath it
        // has just changed — the same reason SetPaused drops one.
        _messageTimer.Stop();
        RestoreText();

        CrosshairToggleRequested?.Invoke(this, _crosshairEnabled);
    }

    private void CollapseBtn_Click(object sender, RoutedEventArgs e)
    {
        SetCollapsed(!_collapsed);
        CollapsedChanged?.Invoke(this, _collapsed);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
