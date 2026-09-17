using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(2.6);

    private readonly System.Drawing.Rectangle _screenBounds;
    private readonly DispatcherTimer _messageTimer;

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
    private bool _hasLanguages;
    private bool _isPaused;

    // Whether the edit layer is still taking the mouse. Owned here rather than by the layer: the
    // button that changes it lives on this bar, and the bar has to say so in its own hint text.
    private bool _crosshairEnabled = true;

    // Whether a message is standing in for the bar's own text. Kept explicitly rather than read off
    // the timer, which stopped being the same question once a message could be sticky: a sticky one
    // is showing with no timer running, and the timer check would have quietly restored the bar
    // underneath it.
    private bool _messageShowing;

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
    private bool _isDragging;
    private System.Drawing.Point _dragStartMouse;
    private System.Drawing.Point _dragStartPosition;

    public RealtimeControlWindow(System.Drawing.Rectangle screenBounds)
    {
        InitializeComponent();

        _screenBounds = screenBounds;
        ApplyPauseButton();
        ApplyCrosshairButton();
        RestoreText();

        _messageTimer = new DispatcherTimer { Interval = MessageDuration };
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); RestoreText(); };

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

        // The bar is sized to its content, so every change of text changes its width — a status
        // message replacing another, the language pair giving way to 已暫停, a block count going from
        // one digit to two. The right edge is what stays put through all of it.
        //
        // Not the middle, which is what this did first, and not the left edge either. Everything on
        // this bar that changes width — the status text, the language pair, the block count — sits to
        // the left of the buttons, and the buttons are what the pointer is on. Pinning the left edge
        // hands the whole change to the buttons; pinning the middle hands them half of it. Pausing
        // swaps a language pair for one short word, so either way the button under the pointer walks
        // out from under it, and the user cannot press the same spot again to resume — which is the
        // one thing somebody who just pressed pause is most likely to do next.
        //
        // Pinning the right edge costs the left edge instead, where the dot and the status text live.
        // Those are read, not aimed at.
        //
        // Centring is still how the bar is first placed — see PlaceInitially. That is a starting
        // position, not a rule about how it grows.
        SizeChanged += (_, e) =>
        {
            // Not while the scale is being re-applied: that changes the width in DIP without the text
            // having changed at all, and treating it as growth would walk the bar sideways.
            if (!IsLoaded || !e.WidthChanged || _isDragging || _adjustingForScale) return;

            var grown = (int)Math.Round((e.NewSize.Width - e.PreviousSize.Width) * _windowScale);
            if (grown == 0) return;

            _position = _position with { X = _position.X - grown };
            ClampIntoScreen();
        };
    }

    public event EventHandler? StartRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? PauseToggleRequested;

    /// <summary>
    /// Raised with the state framing should be in from now on: true to take the mouse back for
    /// drawing blocks, false to hand it to whatever is underneath.
    /// </summary>
    public event EventHandler<bool>? CrosshairToggleRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // No WDA_EXCLUDEFROMCAPTURE here any more: this bar stays out of what the loop reads because
        // the capture backend leaves it out, not because the bar asked to be invisible (#105).
        WindowStyles.ApplyNoActivate(this);
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
        QueueScaleFixup();
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
        // its timer would restore the old text on top of the new state a moment later.
        _messageTimer.Stop();
        RestoreText();
        // Nothing is in flight once paused, and a pulse left running would go on saying there is.
        SetBusy(false);
    }

    public void SetMode(RealtimeControlMode mode)
    {
        _mode = mode;
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
        RestoreText();
        SetBusy(false);

        // The capsule is far narrower than the bar, so the window's size changes under a position
        // that was valid for the other mode.
        Dispatcher.BeginInvoke(new Action(ClampIntoScreen), DispatcherPriority.Loaded);
    }

    // Spaced either side of the slash: at 12px the bare 1/1 reads as one glyph cluster rather than
    // as a count out of a limit, which is the one thing this line is here to say.
    public void SetBlockCount(int count, int max) => BlockCountText.Text = $"· {count} / {max}";

    /// <summary>Pulses the status dot while a recognition or translation pass is in flight.</summary>
    public void SetBusy(bool busy)
    {
        // A pass cancelled by 暫停 can report itself busy on its way out; the dot is saying
        // something else now.
        if (busy && !_isPaused)
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
        }

        _messageShowing = true;
        _messageTimer.Stop();
        if (!sticky) _messageTimer.Start();
    }

    /// <summary>Re-asserts the bar above a window created after it — the edit layer, on re-entry.</summary>
    public void BringToFront()
    {
        Topmost = false;
        Topmost = true;
    }

    private void RestoreText()
    {
        _messageShowing = false;
        EditHintText.Text = _crosshairEnabled ? EditHint : CrosshairOffHint;

        RunStatusText.Text = _isPaused ? PausedStatus : RunStatus;

        var showPair = _hasLanguages && !_isPaused;
        LangPairPanel.Visibility = showPair ? Visibility.Visible : Visibility.Collapsed;
        RunStatusText.Visibility = showPair ? Visibility.Collapsed : Visibility.Visible;

        SetDotState(failed: false);
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
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void EditBtn_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private void PauseBtn_Click(object sender, RoutedEventArgs e) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);

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

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
