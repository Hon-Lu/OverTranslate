using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Shell;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace OverTranslate.Views.QuickLookup;

/// <summary>
/// 取詞翻譯: one line of text translated in place, over whatever the user was reading.
/// </summary>
/// <remarks>
/// The lightest of the three translation surfaces, and the only one with no way in but a shortcut.
/// It exists for the case the other two are too heavy for — a word in a sentence someone is halfway
/// through — so everything here is arranged around not making them leave what they were doing: the
/// selection is carried in for them, the popup lands where their pointer already is, and it goes
/// away the moment they turn back to what they were doing.
///
/// One at a time, deliberately. Several of these would each be waiting to dismiss themselves on top
/// of somebody's work, and only one window at a time can be the one the user is attending to —
/// which is the whole of what decides whether this one is still wanted.
///
/// It reads the same source language, target language and translation service as 截圖翻譯 and
/// 文字翻譯, so a change made here is a change everywhere. See <see cref="QuickLookupSettings"/> for
/// what it does keep to itself.
/// </remarks>
public partial class QuickLookupWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <inheritdoc cref="QuickLookupWindow"/>
    private static QuickLookupWindow? _current;

    /// <inheritdoc cref="Views.Translation.TranslationPage"/>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private const int EnterMs = 220;
    private const int ExitMs  = 110;
    private const int BodyMs  = 150;

    /// <summary>
    /// The transparent band below the card, in DIP.
    /// </summary>
    /// <remarks>
    /// This is the bottom of the shadow margin on the root Grid — <c>Margin="26,0,26,30"</c> in the
    /// XAML, which says why it is there. It has to be kept in step with it: it is what
    /// <see cref="KeepBodyOnScreen"/> subtracts to find the card inside the window, and a stale value
    /// would put the card that far off the edge it was told to sit against. There is deliberately no
    /// top margin, so Windows can place the visible card itself against the screen edge.
    /// </remarks>
    private const double ShadowMarginBottom = 30;

    /// <summary>
    /// How often Windows is asked which window is actually in front.
    /// </summary>
    /// <remarks>
    /// Short enough that a dismissal this catches rather than <see cref="OnDeactivated"/> still
    /// reads as immediate, and cheap enough to leave running: the check is one call returning a
    /// handle, on a window that lives for seconds.
    /// </remarks>
    private static readonly TimeSpan ForegroundWatchInterval = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan ManualCopyHold = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan AutoCopyHold = TimeSpan.FromMilliseconds(1500);

    private readonly TtsService _tts = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _copiedHold;
    private readonly DispatcherTimer _foregroundWatch;

    /// <summary>This window's handle, for comparing against whatever Windows says is in front.</summary>
    private IntPtr _hwnd;

    /// <summary>
    /// True once this window has actually been the foreground window at least once.
    /// </summary>
    /// <remarks>
    /// The latch is what makes <see cref="CheckForeground"/> mean "it was in front and now it is
    /// not" rather than "it is not in front", which at the moment of opening is briefly true of
    /// every window and would close this one before it had been seen.
    ///
    /// It also carries the one case that cannot be fixed from here: if Windows never hands over the
    /// foreground at all, this stays false and the popup simply waits. Clicking it sets the latch
    /// the ordinary way, and from then on it behaves like any other summon.
    /// </remarks>
    private bool _hadForeground;

    /// <inheritdoc cref="TakeForeground"/>
    private int _foregroundAttempts;

    /// <summary>True while the popup is pinned, which is what keeps it through a deactivation.</summary>
    /// <remarks>
    /// Pinning answers "keep this one on screen while I work". Everything it holds against is the
    /// popup acting on its own — closing itself when attention moves, moving itself to the pointer
    /// on the next summon — and nothing it holds against is the user: a pinned popup still drags,
    /// and the close button still closes it.
    ///
    /// Per window and stored nowhere, because it is a statement about the popup in front of the
    /// user right now. What sets it on the way in is which door was used, not what was set last
    /// time — see <see cref="SummonAsync"/>.
    /// </remarks>
    private bool _pinned;

    /// <summary>True while the gear panel is showing instead of the result.</summary>
    private bool _settingsOpen;

    /// <summary>Whether results use the compact status-and-preview presentation.</summary>
    private bool _resultsCollapsed;

    /// <summary>True while one of the pickers has its list down — see <see cref="OnDeactivated"/>.</summary>
    private bool _dropDownOpen;

    /// <summary>True while the window is writing to its own controls, so that does not auto-translate.</summary>
    private bool _suppressAuto;

    /// <summary>True once the closing animation has started, so nothing restarts it.</summary>
    private bool _closing;

    /// <summary>Monotonic id, so a slow translation cannot overwrite the result of a newer one.</summary>
    private int _seq;

    /// <summary>
    /// The language the engine said the original was in, or empty.
    /// </summary>
    /// <remarks>
    /// This is what makes 朗讀原文 usable at all. The shared source language is 自動 by default and
    /// most people never change it, and there is no such thing as an automatic voice — 文字翻譯
    /// answers that by switching its source speaker off. Here the engine has already been asked, and
    /// its answer is a better one than the picker can give.
    /// </remarks>
    private string _detectedLang = "";

    /// <summary>The two languages 雙語互譯 moves between, as this window currently holds them.</summary>
    /// <remarks>
    /// Kept here rather than read back off a picker because the pair is shown in two pickers and one
    /// line of text at once, and the instant one of them is changed the others are still holding
    /// what it used to be. Which language just left the slot is exactly what
    /// <see cref="ApplyBilingualPick"/> needs to turn the pair around.
    /// </remarks>
    private string _bilingualFirst = QuickLookupSettings.DefaultFirstLanguage;

    /// <inheritdoc cref="_bilingualFirst"/>
    private string _bilingualSecond = QuickLookupSettings.DefaultSecondLanguage;

    /// <summary>
    /// The language the current text turned out to be in under 雙語互譯, or null when there is
    /// nothing to go on yet.
    /// </summary>
    /// <remarks>
    /// This is what the header reads out. Null is the honest answer for an empty box and for a
    /// selection that has just arrived, and it is also all this window can say until the language of
    /// the text is worked out before it is sent — see <see cref="RenderBilingualStatus"/>.
    /// </remarks>
    private string? _bilingualSource;

    /// <summary>The button currently driving playback, so a second click stops rather than replays.</summary>
    private Button? _ttsActiveBtn;

    /// <summary>
    /// Where the popup belongs when nothing is holding it up, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Physical pixels, because that is what <see cref="KeepBodyOnScreen"/> works in and what the
    /// window was placed with. Null is the ordinary state and means "where it is now is where it
    /// belongs"; anything that means the user has chosen a new place — a drag, a re-placement at the
    /// pointer — sets it back to null rather than sending the popup somewhere they left.
    /// </remarks>
    private int? _restingTop;

    /// <summary>
    /// Brings the popup up over the foreground application, carrying whatever is selected there.
    /// </summary>
    /// <remarks>
    /// The selection is read before anything is shown: putting a window on the screen takes the
    /// foreground away from the application holding it, and the copy would then be sent here.
    ///
    /// An already-open popup is refilled rather than replaced, so pressing the shortcut twice does
    /// not throw away a pin or a position the user has set.
    /// </remarks>
    /// <param name="pinned">
    /// Whether to open the popup already pinned, which is how the two doors differ.
    /// <para>
    /// The shortcut is used mid-sentence, on a word, and the popup that answers it is meant to be
    /// glanced at and gone — dismissing itself is the whole of why it costs nothing to press. The
    /// entry on the main window is the opposite errand: the user went looking for the feature and
    /// clicked it, and a window that disappeared while they were still turning back to their text
    /// would look broken to someone who has not yet met the shortcut.
    /// </para>
    /// <para>
    /// It only ever turns the pin on. Off is the user's word, and a shortcut press landing on a
    /// popup they pinned must not quietly take that back.
    /// </para>
    /// </param>
    public static async Task SummonAsync(bool pinned = false)
    {
        var selection = await SelectedTextReader.ReadAsync();

        if (_current is { _closing: false } open)
        {
            open.Refill(selection);
            if (pinned) open.SetPinned(true);
            open.ReacquireForeground();
            return;
        }

        var window = new QuickLookupWindow();
        _current = window;

        // Before Show: showing a window can hand activation straight back, and OnDeactivated closes
        // an unpinned popup without asking how old it is.
        if (pinned) window.SetPinned(true);

        window.Show();
        window.ReacquireForeground();
        window.Refill(selection);
    }

    /// <summary>Takes the foreground, starting the retry budget over.</summary>
    /// <remarks>
    /// Reset per summon rather than per window. A popup that is already open has the latch set from
    /// last time, so a re-summon that failed to take the foreground back would look to
    /// <see cref="CheckForeground"/> exactly like the user clicking away — and close the popup they
    /// had just asked for.
    /// </remarks>
    private void ReacquireForeground()
    {
        _hadForeground = false;
        _foregroundAttempts = 0;
        TakeForeground();
    }

    /// <summary>
    /// Makes this window the one the keyboard is talking to.
    /// </summary>
    /// <remarks>
    /// <c>Show</c> and <c>Activate</c> are not enough, and the failure is silent: Windows refuses to
    /// hand the foreground to a process the user was not already working in, so the popup appears on
    /// top — it is topmost — while every keystroke goes on reaching the application underneath. The
    /// box then draws a focus ring it does not have and typing does nothing, which is the whole
    /// feature broken for anyone who summoned it with nothing selected.
    ///
    /// Attaching this thread's input queue to the foreground one for the length of the call is what
    /// lifts the refusal: while two threads share an input queue, either of them may set the
    /// foreground. Detached again immediately — a permanent attachment couples this application's
    /// message pump to a stranger's, so an application that hangs would take this one with it.
    ///
    /// Even that is not certain to work, which is why it can be asked again. The popup is summoned
    /// moments after this application synthesised a Ctrl+C into the window the user was reading —
    /// see <see cref="SelectedTextReader"/> — and one of the things Windows weighs when deciding
    /// whether to refuse is which process received the last input event. <see cref="CheckForeground"/>
    /// is what notices the refusal and asks again; the attempt count is what stops it asking forever,
    /// because every attempt flashes the taskbar button of whoever is holding the foreground.
    /// </remarks>
    private void TakeForeground()
    {
        var hwnd = Hwnd();
        if (hwnd == IntPtr.Zero) return;

        _foregroundAttempts++;

        var foreground = GetForegroundWindow();
        var owner = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);

        try
        {
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(self, owner, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    /// <summary>Takes the popup off the screen, if one is up.</summary>
    /// <remarks>
    /// Ignores the pin: this is called when the popup cannot go on existing — a realtime session
    /// taking the screen — rather than when something merely thinks it has outstayed its welcome.
    /// </remarks>
    public static void Dismiss() => _current?.BeginClose();

    private QuickLookupWindow()
    {
        InitializeComponent();
        _resultsCollapsed = SettingsService.Instance.Current.QuickLookup.ResultsCollapsed;

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateNowAsync(); };

        _copiedHold = new DispatcherTimer { Interval = ManualCopyHold };
        _copiedHold.Tick += (_, _) =>
        {
            _copiedHold.Stop();
            RenderCopyLabel(copied: false);
            HideAutoCopyConfirmation(immediate: false);
        };

        _foregroundWatch = new DispatcherTimer { Interval = ForegroundWatchInterval };
        _foregroundWatch.Tick += (_, _) => CheckForeground();

        _tts.StateChanged += OnTtsStateChanged;

        _suppressAuto = true;
        LocalizationService.BindLocalizedItems(SrcLangBox, LanguageData.SourceLanguages);
        LocalizationService.BindLocalizedItems(TgtLangBox, LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

        // Off the target languages, both of them: each end of the bilingual pair has to be somewhere
        // a translation can land, so 自動 is not on offer the way it is for a source picker.
        LocalizationService.BindLocalizedItems(BilingualFirstBox, LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(BilingualSecondBox, LanguageData.TargetLanguages);

        LoadSharedPreferences();
        _suppressAuto = false;

        // Attached after the initial values are in, so setting them up neither saves nor translates.
        SrcLangBox.SelectionChanged  += LangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += LangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
        AutoCopyToggle.Checked       += AutoCopyToggle_Toggled;
        AutoCopyToggle.Unchecked     += AutoCopyToggle_Toggled;
        BilingualToggle.Checked      += BilingualToggle_Toggled;
        BilingualToggle.Unchecked    += BilingualToggle_Toggled;

        BilingualFirstBox.SelectionChanged  += BilingualFirstBox_SelectionChanged;
        BilingualSecondBox.SelectionChanged += BilingualSecondBox_SelectionChanged;

        foreach (var picker in new[]
                 {
                     SrcLangBox, TgtLangBox, ProviderBox,
                     BilingualFirstBox, BilingualSecondBox
                 })
        {
            picker.DropDownOpened += (_, _) => _dropDownOpen = true;
            picker.DropDownClosed += (_, _) =>
            {
                _dropDownOpen = false;

                // The deactivation a click outside the list produced arrived while the list was
                // still down, and OnDeactivated skipped it for exactly that reason. Nothing else
                // would ever ask again, so the popup would outlive the click that dismissed it.
                if (!IsActive && !_pinned) BeginClose();
            };
        }

        // The mop-up half of the lift. OnWindowPosChanging does the work, and does it atomically,
        // but the height it is offered is the one Windows is about to apply and that is occasionally
        // a pixel or two short of where the resize settles — enough to leave the bottom row of the
        // card over the edge. By here the resize has landed and the window can be asked how tall it
        // really is. A correction this small is not a movement anybody sees; the lift it is
        // correcting already happened in the same frame as the resize.
        SizeChanged += (_, e) => { if (e.HeightChanged) KeepBodyOnScreen(); };

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        PreviewKeyDown += OnPreviewKeyDown;
        Surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;

        // Composed in code from the shortcut and the interface language, so DynamicResource cannot
        // reach them — see LocalizationService.LanguageChanged.
        LocalizationService.LanguageChanged += OnLanguageChanged;

        Loaded += (_, _) =>
        {
            PositionAtPointer();
            AnimateIn();
            _foregroundWatch.Start();
        };

        RenderChrome();
        RenderCopyLabel(copied: false);
        RenderCollapseButton();
        RenderResultPresentation();
        RenderSettingsButton();
        RenderSettingsHint();
        RenderBilingualMode();
    }

    /// <summary>
    /// Puts a new selection into an open popup.
    /// </summary>
    /// <remarks>
    /// A pinned popup keeps its place: the user put it there, and the point of pinning is that it
    /// stops behaving like a thing that follows the pointer around.
    ///
    /// The box takes keyboard focus either way, with the caret after the last character rather than
    /// the text selected: a selection is one keystroke away from being replaced wholesale, and the
    /// text it would destroy is the text the user asked about.
    /// </remarks>
    private void Refill(string selection)
    {
        if (!_pinned && IsLoaded) PositionAtPointer();

        ShowSettings(false);

        _suppressAuto = true;
        SourceTextBox.Text = selection;
        SourceTextBox.CaretIndex = selection.Length;
        _suppressAuto = false;

        _detectedLang = "";
        _seq++;

        // New text, so the direction the header is reading out belongs to the old text.
        ForgetBilingualDirection();

        if (selection.Length == 0) ShowBody(false);
        else RequestTranslate();

        // After Focus, which selects the whole box on its own when focus arrives programmatically.
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;

        RenderChrome();
    }

    // ══════════════════════════ Placement and motion ══════════════════════════

    /// <summary>
    /// Drops the popup around the pointer, inside the monitor the pointer is on.
    /// </summary>
    /// <remarks>
    /// All physical pixels and the scale of the monitor being placed on, exactly as the toast does:
    /// reading the scale off this window reports the monitor it currently sits on, which before the
    /// first placement is whichever one WPF happened to open it on.
    ///
    /// The pointer ends up just inside the popup's top-left rather than beside it, so that reaching
    /// any of the controls is a small movement from where the hand already is. The offsets put it
    /// over the corner the brand mark occupies, which is the one part of the header nobody clicks.
    ///
    /// Nothing about the placement is remembered between summons. This window goes where the pointer
    /// already is, so a stored position would be a worse answer to the same question every time.
    /// Dragging one pins it, and a pinned popup is not re-placed — see <see cref="Refill"/>.
    /// </remarks>
    private void PositionAtPointer()
    {
        // A fresh placement is a new resting place, and the old one is not somewhere to go back to.
        _restingTop = null;

        var pointer = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(pointer).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(pointer.X, pointer.Y);

        var w = ActualWidth * scale;
        var h = ActualHeight * scale;
        var edge = 4 * scale;

        // Math.Clamp throws when the popup is larger than the monitor it has to fit on.
        var minX = area.Left + edge;
        var maxX = Math.Max(minX, area.Right - w - edge);
        var minY = area.Top + edge;
        var maxY = Math.Max(minY, area.Bottom - h - edge);

        // Measured from the window, which is wider and taller than the card by the shadow margin —
        // so these are the card's own inset plus that margin, and they move together with it.
        var x = Math.Clamp(pointer.X - 48 * scale, minX, maxX);
        var y = Math.Clamp(pointer.Y - 30 * scale, minY, maxY);

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(x), (int)Math.Round(y));

        // Anchored to the pointer rather than to a corner: the popup should look like it came out of
        // the place it was asked for, and go back into it.
        Surface.RenderTransformOrigin = new Point(
            Math.Clamp((pointer.X - x) / Math.Max(w, 1), 0, 1),
            Math.Clamp((pointer.Y - y) / Math.Max(h, 1), 0, 1));
    }

    /// <remarks>
    /// The handle is what the message hook needs, and this is the first moment there is one.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(OnWindowPosChanging);
    }

    /// <summary>
    /// Lifts the popup off the bottom edge while the result is open, and puts it back after.
    /// </summary>
    /// <remarks>
    /// The header is the whole window until a translation arrives, and the result grows out of the
    /// bottom of it — <c>SizeToContent="Height"</c>, so the top stays put and the window gets taller.
    /// A popup summoned or dragged near the foot of the screen therefore grows the answer straight
    /// off the desktop: the user typed a word, something happened, and there is nothing to read.
    ///
    /// Caught on the way in, in <c>WM_WINDOWPOSCHANGING</c>, rather than answered afterwards from
    /// <c>SizeChanged</c>. Two reasons, and the first is that afterwards does not work: WPF resizes
    /// the HWND after the layout pass, so a handler that asked Windows how tall the window was would
    /// be told the height it had a moment ago, wave the grow through, and only catch it on some
    /// later pass — the window visibly drops off the bottom of the screen and is then yanked back.
    /// The second is that this is one operation instead of two: Windows is already about to move and
    /// size this window, and editing the rectangle it is going to use means there is never a frame
    /// where the popup has grown but not yet moved.
    ///
    /// The arithmetic is <see cref="QuickLookupLift"/>'s, which is where it can be tested; this is
    /// the half that has to ask Windows where the window and the monitor actually are.
    ///
    /// Physical pixels throughout — which is what <c>WINDOWPOS</c> is already in, for the reason
    /// <see cref="PositionAtPointer"/> gives.
    /// </remarks>
    private IntPtr OnWindowPosChanging(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || _closing) return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        // Nothing to catch when the height is not changing: our own corrections come through here
        // as pure moves, and answering them would be a loop.
        if ((pos.flags & SwpNoSize) != 0) return IntPtr.Zero;

        var bounds = ScreenGeometry.PhysicalBounds(this);
        if (bounds.IsEmpty) return IntPtr.Zero;

        // x and y are undefined when the caller said not to move, so the window's own position is
        // what the new height has to be judged against.
        var moving = (pos.flags & SwpNoMove) == 0;
        var left = moving ? pos.x : bounds.Left;
        var top  = moving ? pos.y : bounds.Top;

        // A drag arrives here as a move that carries the size along with it, one message per step,
        // and pos.y is where the drag wants the window rather than where it has been allowed to sit.
        // That is the definition of the resting place, so each step replaces it: without this, the
        // first step that overflowed would be remembered as home and the rest of the drag ignored.
        if (moving) _restingTop = null;

        var area = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(left, top);

        // The card may go all the way to the edge; the window may go further, because the last
        // ShadowMarginBottom pixels of it are the transparent border the shadow fades out into. Holding
        // the window itself inside the work area would keep the card that much clear of the bottom
        // of every screen, which is a band of empty desktop under a popup for no stated reason.
        var (wanted, resting) = QuickLookupLift.Place(
            top,
            _restingTop,
            pos.cy,
            area.Top,
            area.Bottom + (int)Math.Round(ShadowMarginBottom * scale));

        _restingTop = resting;

        if (wanted == top) return IntPtr.Zero;

        pos.x = left;
        pos.y = wanted;
        pos.flags &= ~SwpNoMove;
        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);

        return IntPtr.Zero;
    }

    /// <summary>Re-checks the fit after something moved the window without resizing it.</summary>
    /// <remarks>
    /// <see cref="OnWindowPosChanging"/> only hears about resizes, and a drag is not one. Reading
    /// the height back from the window is safe here for the same reason it is wrong there: nothing
    /// is in flight, so what Windows reports is what is on the screen.
    /// </remarks>
    private void KeepBodyOnScreen()
    {
        if (_closing) return;

        var bounds = ScreenGeometry.PhysicalBounds(this);
        if (bounds.IsEmpty) return;

        var area = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(bounds.Left, bounds.Top);

        var (top, resting) = QuickLookupLift.Place(
            bounds.Top,
            _restingTop,
            bounds.Height,
            area.Top,
            area.Bottom + (int)Math.Round(ShadowMarginBottom * scale));

        _restingTop = resting;

        if (top != bounds.Top) ScreenGeometry.MoveToPhysical(this, bounds.Left, top);
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

    /// <remarks>
    /// Blur and scale together rather than a plain fade, so the surface reads as arriving rather
    /// than as being turned up — the shadow and the border are already drawn, and only the geometry
    /// is short of its resting value.
    ///
    /// No overshoot. Nothing threw this window: it appeared because a key was pressed, and a bounce
    /// belongs to motion that inherited momentum from a gesture.
    /// </remarks>
    private void AnimateIn()
    {
        // Windows' "animation effects" setting is this platform's reduced-motion preference.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(EnterMs - 80)),
        });

        var grow = new DoubleAnimation
        {
            From = 0.94, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(EnterMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <remarks>
    /// The same path in reverse, ending where it started. Something that leaves along a different
    /// route than it arrived by reads as a second, unrelated thing happening.
    /// </remarks>
    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;

        _debounce.Stop();
        _foregroundWatch.Stop();
        _tts.Stop();

        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var shrink = new DoubleAnimation
        {
            To = 0.96,
            Duration = new Duration(TimeSpan.FromMilliseconds(ExitMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ExitMs)),
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Slides whichever panel is now current up into place.</summary>
    private void ShowBody(bool visible)
    {
        if (!visible)
        {
            BodyHost.Visibility = Visibility.Collapsed;
            return;
        }

        var wasVisible = BodyHost.Visibility == Visibility.Visible;
        BodyHost.Visibility = Visibility.Visible;
        if (wasVisible || !SystemParameters.ClientAreaAnimation) return;

        BodyHost.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(BodyMs)),
        });
        BodyTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 6, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(BodyMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ══════════════════════════ Staying and going ══════════════════════════

    // The pointer only decides how the header draws itself; what decides whether this window still
    // exists is OnDeactivated.
    private void OnPointerEnter(object sender, MouseEventArgs e) => RenderChrome();

    private void OnPointerLeave(object sender, MouseEventArgs e) => RenderChrome();

    /// <summary>
    /// Closes the popup as soon as the user's attention goes back to something else.
    /// </summary>
    /// <remarks>
    /// Losing activation is the whole dismissal rule, and it is a better one than a timer watching
    /// the pointer: a pointer that has wandered off the window says nothing about whether the person
    /// is still reading it, while clicking into another window is them saying they are finished, at
    /// the moment they finish. Nothing has to be guessed and nothing has to be waited out.
    ///
    /// It also means the popup never closes while it is the window being used — typing in it, waiting
    /// for a translation, listening to one — so none of those needs a rule of its own.
    ///
    /// Three exceptions. A popup that has not yet been confirmed as the foreground window is still
    /// in the activation handoff started by <see cref="ReacquireForeground"/>; treating that transient
    /// deactivation as departure closes it before the retry can run. A pinned popup is one the user
    /// has said to keep regardless, which is what pinning is for. And a picker with its list down has
    /// not lost anybody's attention: the list is its own window, and answering that deactivation
    /// would close the popup out from under the language someone is in the middle of choosing.
    /// </remarks>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_hadForeground || _pinned || _dropDownOpen) return;
        BeginClose();
    }

    /// <summary>
    /// Asks Windows which window is in front, because <see cref="OnDeactivated"/> cannot always say.
    /// </summary>
    /// <remarks>
    /// WPF raises Deactivated for a window it believes was activated, and the popup is sometimes
    /// never activated at all: it is summoned right after a synthesised Ctrl+C has gone to the
    /// window the user was reading, and Windows can refuse to hand the foreground over on that
    /// basis. The popup then sits on top — it is topmost either way — attached to nothing, and no
    /// amount of clicking elsewhere produces the deactivation that would close it. Clicking the
    /// popup itself was the only way out, which is a window the user has to know a trick to dismiss.
    ///
    /// The foreground handle is the fact underneath the state WPF is inferring, so this asks for it
    /// directly. It answers both halves: a refusal is retried while it is still worth retrying, and
    /// a foreground that has moved on closes the popup whether or not WPF noticed.
    ///
    /// The guards are <see cref="OnDeactivated"/>'s, for the same reasons.
    /// </remarks>
    private void CheckForeground()
    {
        if (_closing) return;

        if (GetForegroundWindow() == Hwnd())
        {
            _hadForeground = true;
            return;
        }

        if (!_hadForeground)
        {
            // Two, not one: the first is the one Show made, and by the next tick whatever was
            // holding the foreground has usually finished handling the keystrokes we sent it.
            if (_foregroundAttempts < 3) TakeForeground();
            return;
        }

        if (_pinned || _dropDownOpen) return;
        BeginClose();
    }

    private IntPtr Hwnd() => _hwnd != IntPtr.Zero
        ? _hwnd
        : _hwnd = new WindowInteropHelper(this).Handle;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // An open language list owns these two keys while it is down. This handler tunnels from the
        // window, so it sees them before the list does, and both of its answers are wrong there:
        // Escape would close the whole popup instead of the list the user just opened, and Enter
        // would re-translate while they are typing into the list's search box. The list's own
        // handling of them is in ComboBoxSearch.
        if (_dropDownOpen) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            if (_pinned)
            {
                // Pinning promises the popup survives ordinary dismissal gestures. Escape still
                // gives the reader a quick reset, and clearing through the same path as the button
                // also cancels any translation that belongs to the previous text.
                ClearSourceText();
                return;
            }

            BeginClose();
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return)) return;

        // Enter translates what is in the box now, rather than waiting out the debounce.
        e.Handled = true;
        _debounce.Stop();
        _ = TranslateNowAsync();
    }

    // ══════════════════════════ Moving and pinning ══════════════════════════

    /// <summary>Lets the popup be dragged by any part of it that is not a control.</summary>
    /// <remarks>
    /// Bubbling rather than tunnelling, and that is the whole of it: a press that landed on the box
    /// or on a button has been handled by that control and never arrives here, so dragging cannot
    /// steal a click meant for something. Reached from a tunnelling handler instead, this used to
    /// run for every press on the window — and <c>DragMove</c> then inherited the press's own mouse
    /// capture and never gave it back. A window holding the capture swallows every click on the
    /// desktop after it, which cost the user both the click they aimed at another window and the
    /// deactivation that is the only thing that closes this one.
    ///
    /// Dragging does not pin. It used to, on the reasoning that placing a window somewhere says you
    /// want it to stay there — but the pin is on the header at all times now, so the guess buys
    /// nothing, and a window that pins itself is one the user has to notice and undo.
    ///
    /// Pinning does not stop dragging either, which it briefly did. A pinned popup is the one the
    /// user is going to keep for a while, so it is the one they most need to move out of the way of
    /// what they are reading — locking it in place took that away from exactly the case that wanted
    /// it. What the pin holds against is the popup moving on its own, which is a different thing
    /// from the user moving it: see <see cref="Refill"/>.
    /// </remarks>
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws when the button is already up by the time it runs, which a fast click
            // can manage. There is nothing to move and nothing to report.
        }

        // Nothing to do afterwards. Windows sends the drag through OnWindowPosChanging step by step,
        // so the popup has been kept on screen the whole way down and the place they let go of it is
        // already recorded. Resetting anything here would throw that away.
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    /// <summary>Sets the pin and redraws the header, which is the only thing that reports it.</summary>
    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        RenderChrome();
    }

    /// <summary>
    /// Settles the header against where the pointer is, whether it is pinned, and whether the box
    /// has anything in it.
    /// </summary>
    /// <remarks>
    /// The pin is always on the header. It was faded in on hover while dragging was the main way to
    /// pin, so the button only had to be there for whoever went looking; now that it is the only way
    /// to pin, a control that is not on screen is a feature nobody finds.
    ///
    /// Which glyph shows is the action rather than the state, the way every other toggle in this
    /// application draws itself; the accent is what says which state it is in.
    /// </remarks>
    private void RenderChrome()
    {
        // Which glyph shows is the action, not the state — the way every other toggle in this
        // application draws itself. Segoe MDL2 codepoints so they survive Windows 10, where Segoe
        // Fluent Icons is not installed; see Views/Controls/TtsGlyphs.
        PinBtn.Content = _pinned ? "\uE77A" : "\uE718";
        PinBtn.ToolTip = LocalizationService.Get(
            _pinned ? "S.QuickLookup.Unpin" : "S.QuickLookup.Pin");
        PinBtn.SetResourceReference(
            ForegroundProperty, _pinned ? "AppAccent" : "AppTextSecondary");

        var showField = IsMouseOver || SourceTextBox.IsKeyboardFocusWithin;
        SourceTextBox.SetResourceReference(
            BackgroundProperty, showField ? "AppInputBg" : "AppSurfaceBg");
        SourceTextBox.SetResourceReference(
            BorderBrushProperty, showField ? "AppInputBorder" : "AppSurfaceBg");

        var empty = SourceTextBox.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // On the text alone, not on the pointer, unlike everything else this method settles. The
        // controls above are chrome the user goes looking for; this one is the answer to a box that
        // already has the wrong words in it, and it has to be there when they look down at it.
        ClearBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <remarks>
    /// Cleared through the selection rather than by assigning Text, so it lands on the TextBox's own
    /// undo stack and Ctrl+Z puts the text back — the same reasoning as 文字翻譯's clear button, and
    /// it applies harder here: the text this destroys is usually a selection the user carried in
    /// from somewhere else, and retyping it means going back for it.
    /// </remarks>
    private void ClearBtn_Click(object sender, RoutedEventArgs e)
        => ClearSourceText();

    private void ClearSourceText()
    {
        SourceTextBox.SelectAll();
        SourceTextBox.SelectedText = "";

        // They cleared it in order to type something else, and the button they clicked has just
        // gone — without this they would have to click into the box before typing.
        SourceTextBox.Focus();
    }

    // ══════════════════════════ Translating ══════════════════════════

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderChrome();
        RequestTranslate();
    }

    private void RequestTranslate()
    {
        if (_suppressAuto) return;

        _debounce.Stop();
        SetTranslationLoading(false);
        ClearDictionaryResult();
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _seq++;
            _detectedLang = "";

            // An empty box has no language, so the header goes back to saying it will work one out.
            ForgetBilingualDirection();

            ShowBody(_settingsOpen);
            return;
        }

        _debounce.Start();
    }

    /// <remarks>
    /// Hedged and with fallbacks, unlike 文字翻譯, which sends to the chosen engine alone. The two
    /// windows are answering different questions: there, a failure is worth reporting because the
    /// user is sitting in a window they opened to translate in and can retry. Here the popup has
    /// about a second of the user's attention and no retry button worth the room, so a free endpoint
    /// having a bad minute should cost a moment rather than the answer.
    /// </remarks>
    private async Task TranslateNowAsync()
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        HideAutoCopyConfirmation(immediate: true);

        var settings = SettingsService.Instance.Current;
        var bilingual = BilingualToggle.IsChecked == true;

        // 自動 on the source side in this mode, always. What the pair decides is which of the two
        // languages the answer comes back in; what the text was written in is still the engine's
        // question to answer, and leaving it 自動 is what keeps a wrong guess cheap — the worst it
        // can produce is the other of the user's own two languages, never a translation out of a
        // language the text is not in.
        var srcLang = bilingual
            ? LanguageData.AutomaticSourceLanguage
            : LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);

        var tgtLang = bilingual
            ? ResolveBilingualTarget(text)
            : LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);

        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowStatus(LocalizationService.Get("S.Translation.MissingApiKey"), isError: true);
            return;
        }

        var seq = ++_seq;
        SetTranslationLoading(true);
        ShowStatus(LocalizationService.Get("S.Translation.Translating"), isError: false);

        try
        {
            var (results, detected) = await AppServices.Translation.TranslateAsync(
                [new OcrTextBlock(text, new Rect())], srcLang, tgtLang, settings.ApiKey);

            if (seq != _seq) return;

            // The characters were only ever a guess, and the engine has now said what the text
            // actually was. Where the two disagree about which way this should have gone, the round
            // trip is spent again rather than showing the answer to the wrong question — the result
            // has not been put on screen yet, so nothing flickers; the spinner simply runs longer.
            if (bilingual)
            {
                var corrected = CorrectedBilingualTarget(detected, tgtLang);
                if (corrected is not null)
                {
                    tgtLang = corrected;
                    (results, detected) = await AppServices.Translation.TranslateAsync(
                        [new OcrTextBlock(text, new Rect())], srcLang, tgtLang, settings.ApiKey);

                    if (seq != _seq) return;
                }
            }

            SetTranslationLoading(false);
            _detectedLang = detected ?? "";
            TranslatedText.Text = results.FirstOrDefault()?.TranslatedText ?? "";
            if (settings.QuickLookup.AutoCopyTranslation)
                CopyTranslation(showConfirmation: false);
            StatusText.Visibility = Visibility.Collapsed;
            RenderCompactCompletion();
            ShowResult();

            var dictionarySource = LanguageData.IsAutomaticSource(srcLang) ? _detectedLang : srcLang;
            await LoadDictionaryAsync(seq, text, dictionarySource, tgtLang, settings.Provider);
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;

            SetTranslationLoading(false);
            Log.Warn(ex, "取詞翻譯 could not translate");
            TranslatedText.Text = "";
            ShowStatus(
                LocalizationService.Format(
                    "S.Translation.ProviderUnavailable",
                    LanguageData.GetProviderDisplay(settings.Provider), ex.Message),
                isError: true);
        }
    }

    private async Task LoadDictionaryAsync(
        int seq, string text, string sourceLang, string targetLang, TranslationProvider provider)
    {
        if (sourceLang.Length == 0 || !DictionaryLookupEligibility.IsEligible(text)) return;

        DictionaryView.Clear();
        DictionaryLoadingBar.Visibility = Visibility.Visible;
        DictionaryHost.Visibility = Visibility.Visible;
        KeepBodyOnScreen();

        try
        {
            var result = await AppServices.Translation.LookupDictionaryAsync(
                text, sourceLang, targetLang, engine: provider);
            if (seq != _seq) return;

            DictionaryLoadingBar.Visibility = Visibility.Collapsed;
            DictionaryView.Show(result);
            if (result?.HasContent != true)
                DictionaryView.ShowMessage("S.Dictionary.NoResult");
            DictionaryHost.Visibility = Visibility.Visible;
            KeepBodyOnScreen();
        }
        catch (Exception ex)
        {
            // Keep the fast primary answer even when the optional rich endpoint fails.
            Log.Debug(ex, "取詞翻譯 dictionary lookup did not complete");
            if (seq == _seq)
            {
                DictionaryLoadingBar.Visibility = Visibility.Collapsed;
                DictionaryView.ShowMessage("S.Dictionary.Unavailable");
                DictionaryHost.Visibility = Visibility.Visible;
                KeepBodyOnScreen();
            }
        }
    }

    private void ClearDictionaryResult()
    {
        DictionaryLoadingBar.Visibility = Visibility.Collapsed;
        DictionaryView.Clear();
        DictionaryHost.Visibility = Visibility.Collapsed;
    }

    private void SetTranslationLoading(bool loading)
    {
        var visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        TranslationLoadingBar.Visibility = visibility;
        CompactTranslationLoadingBar.Visibility = visibility;
    }

    /// <remarks>
    /// Silent while the gear panel is up. A translation finishing is not a reason to take the user
    /// out of the settings they opened; <see cref="ShowSettings"/> brings the result back when they
    /// are done, by which point it is already there.
    /// </remarks>
    private void ShowResult()
    {
        if (_settingsOpen) return;

        SettingsPanel.Visibility = Visibility.Collapsed;
        RenderResultPresentation();
        // Both belong to a translation rather than to the panel, so neither is on screen while the
        // status line is still saying 翻譯中.
        var hasResult = TranslatedText.Text.Length > 0;
        ActionRow.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
        TgtTtsBtn.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;

        // After ActionRow, which the note's own visibility is measured against.
        RenderSourceTtsAvailability();
        ShowBody(true);
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(
            ForegroundProperty, isError ? "AppError" : "AppAccent");
        StatusText.Visibility = Visibility.Visible;

        CompactStatusText.Text = text;
        CompactStatusText.SetResourceReference(
            ForegroundProperty, isError ? "AppError" : "AppAccent");
        CompactStatusText.Visibility = Visibility.Visible;
        CompactTranslatedText.Text = "";
        CompactTranslatedText.Visibility = Visibility.Collapsed;
        ShowResult();
    }

    private void RenderCompactCompletion()
    {
        CompactStatusText.Text = "";
        CompactStatusText.Visibility = Visibility.Collapsed;
        CompactTranslatedText.Text = TranslatedText.Text;
        CompactTranslationRow.Margin = new Thickness(0);
        CompactTranslatedText.Visibility = TranslatedText.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// What language the text in the box is actually in, or empty when nobody knows yet.
    /// </summary>
    /// <remarks>
    /// The picker's own value answers this except when it says 自動, which is its default and the
    /// setting most people never touch — so on its own it is unknown most of the time. The engine's
    /// answer is what fills that in, and the two callers are the two places where 自動 is not an
    /// answer they can use: 朗讀原文 has to pick a voice, and <see cref="SwapBtn_Click"/> has to put
    /// something on the target side.
    /// </remarks>
    /// <inheritdoc cref="_detectedLang"/>
    private string EffectiveSourceLanguage()
    {
        // 雙語互譯 has no source picker to read: the text decides, and what it decided is already
        // recorded — see SetBilingualSource.
        if (BilingualToggle.IsChecked == true) return _bilingualSource ?? _detectedLang;

        var chosen = SrcLangBox.SelectedValue as string;
        if (!LanguageData.IsAutomaticSource(chosen)) return LanguageData.GetValidSourceCode(chosen);
        return _detectedLang;
    }

    // ══════════════════════════ Reading aloud ══════════════════════════

    /// <summary>
    /// Settles 朗讀原文 against whether there is a language to read the original in.
    /// </summary>
    /// <remarks>
    /// The OpenAI case is called out on its own because it is the one that never resolves. Every
    /// other engine answers "what language was that" as a by-product of translating, so 自動 is a
    /// state the next result gets the popup out of; an OpenAI-compatible server is never asked, and
    /// cannot be — the prompt it is sent is the user's to write, so the reply cannot be made to
    /// carry a second field. Waiting there is not going to help, and telling someone to wait for
    /// something that will not arrive is worse than telling them nothing.
    ///
    /// So that state gets the note under the actions and a matching tooltip, both of which name the
    /// thing that does fix it: choose a source language. The other unresolved state — an engine that
    /// simply has not answered yet — keeps the message that says to wait, which there is true.
    /// </remarks>
    private void RenderSourceTtsAvailability()
    {
        // Under 雙語互譯 the unresolved state is narrower: the characters answer for any pair
        // written in two scripts, so only a pair that shares one can still be waiting on an engine
        // that is never going to say.
        var openAiAutomatic =
            SettingsService.Instance.Current.Provider == TranslationProvider.OpenAI &&
            (BilingualToggle.IsChecked == true
                ? _bilingualSource is null
                : LanguageData.IsAutomaticSource(SrcLangBox.SelectedValue as string));

        var available = !openAiAutomatic &&
                        EffectiveSourceLanguage().Length > 0 &&
                        SourceTextBox.Text.Length > 0;

        SrcTtsBtn.IsEnabled = available;
        SrcTtsBtn.ToolTip = LocalizationService.Get(
            available ? "S.QuickLookup.SpeakSourceTip"
            : openAiAutomatic ? "S.QuickLookup.SpeakSourceOpenAi"
            : "S.QuickLookup.SpeakSourceUnknown");

        // Beside the actions it explains, so it is absent while they are.
        SourceTtsNote.Visibility = openAiAutomatic && ActionRow.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(SrcTtsBtn, SourceTextBox.Text, EffectiveSourceLanguage());

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(
            TgtTtsBtn,
            TranslatedText.Text,
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string));

    /// <inheritdoc cref="Views.Translation.TranslationPage"/>
    private async Task ToggleTtsAsync(Button button, string text, string language)
    {
        if (_tts.IsActive && ReferenceEquals(_ttsActiveBtn, button)) { _tts.Stop(); return; }
        if (string.IsNullOrWhiteSpace(text) || language.Length == 0) return;

        _ttsActiveBtn = button;
        RenderTtsGlyphs();
        try
        {
            await _tts.SpeakAsync(text, language);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "取詞翻譯 could not read the text aloud");
            ShowStatus(LocalizationService.Format("S.Translation.SpeakFailed", ex.Message), isError: true);
        }
    }

    private void OnTtsStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tts.IsActive) return;
            _ttsActiveBtn = null;
            RenderTtsGlyphs();
        }));

    private void RenderTtsGlyphs()
    {
        TgtTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, TgtTtsBtn)
            ? Controls.TtsGlyphs.Stop
            : Controls.TtsGlyphs.Speak;

        SrcTtsGlyph.Text = ReferenceEquals(_ttsActiveBtn, SrcTtsBtn)
            ? Controls.TtsGlyphs.Stop
            : Controls.TtsGlyphs.Speak;
    }

    // ══════════════════════════ Copying ══════════════════════════

    /// <remarks>
    /// Manual copies confirm on their button. Automatic copies use an in-window overlay because
    /// neither the expanded nor compact result should gain a status line for transient feedback.
    /// </remarks>
    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        CopyTranslation(showConfirmation: true);
    }

    private bool CopyTranslation(bool showConfirmation)
    {
        if (TranslatedText.Text.Length == 0) return false;

        try
        {
            Clipboard.SetText(TranslatedText.Text);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not copy the translation");
            return false;
        }

        if (showConfirmation)
        {
            HideAutoCopyConfirmation(immediate: true);
            RenderCopyLabel(copied: true);
        }
        else
        {
            RenderCopyLabel(copied: false);
            ShowAutoCopyConfirmation();
        }

        _copiedHold.Stop();
        _copiedHold.Interval = showConfirmation ? ManualCopyHold : AutoCopyHold;
        _copiedHold.Start();
        return true;
    }

    private void RenderCopyLabel(bool copied) =>
        CopyLabel.Text = LocalizationService.Get(
            copied ? "S.QuickLookup.Copied" : "S.QuickLookup.Copy");

    private void ShowAutoCopyConfirmation()
    {
        AutoCopyConfirmation.BeginAnimation(OpacityProperty, null);
        AutoCopyConfirmation.Opacity = 1;
        AutoCopyConfirmation.Visibility = Visibility.Visible;
    }

    private void HideAutoCopyConfirmation(bool immediate)
    {
        AutoCopyConfirmation.BeginAnimation(OpacityProperty, null);
        if (AutoCopyConfirmation.Visibility != Visibility.Visible) return;

        if (immediate)
        {
            AutoCopyConfirmation.Visibility = Visibility.Collapsed;
            AutoCopyConfirmation.Opacity = 1;
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            AutoCopyConfirmation.Visibility = Visibility.Collapsed;
            AutoCopyConfirmation.Opacity = 1;
            AutoCopyConfirmation.BeginAnimation(OpacityProperty, null);
        };
        AutoCopyConfirmation.BeginAnimation(OpacityProperty, fade);
    }

    // ══════════════════════════ Settings panel ══════════════════════════

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettings(!_settingsOpen);

    private void CollapseBtn_Click(object sender, RoutedEventArgs e)
    {
        _resultsCollapsed = !_resultsCollapsed;
        SettingsService.Instance.Current.QuickLookup.ResultsCollapsed = _resultsCollapsed;
        SettingsService.Instance.Save();

        RenderCollapseButton();
        if (_settingsOpen) return;

        RenderResultPresentation();
        ShowBody(HasBodyContent());
    }

    /// <summary>
    /// Shows the action rather than the state: an up chevron removes detail, a down chevron restores
    /// it. There is no transition — this can sit in a copy-and-paste loop and must answer instantly.
    /// </summary>
    private void RenderCollapseButton()
    {
        CollapseBtn.Content = _resultsCollapsed ? "\uE70D" : "\uE70E";
        var label = LocalizationService.Get(
            _resultsCollapsed ? "S.QuickLookup.Expand" : "S.QuickLookup.Collapse");
        CollapseBtn.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CollapseBtn, label);
    }

    private void RenderResultPresentation()
    {
        ResultPanel.Visibility = _resultsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CompactResultPanel.Visibility = _resultsCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasBodyContent() =>
        !string.IsNullOrWhiteSpace(SourceTextBox.Text)
        && (TranslatedText.Text.Length > 0
            || StatusText.Visibility == Visibility.Visible
            || TranslationLoadingBar.Visibility == Visibility.Visible);

    /// <summary>
    /// Points the one button in the header that means two things at whichever one it means now.
    /// </summary>
    /// <remarks>
    /// The settings replace the result in place, in the same small window, so this button is the
    /// only way back — and a gear that stayed a gear left that unmarked: the panel read as somewhere
    /// the popup had gone rather than somewhere it could come back from.
    ///
    /// Which glyph shows is the action rather than the state, the way the pin beside it and every
    /// speak button in the application already draw themselves. Segoe MDL2 codepoints, so they
    /// survive Windows 10; see Views/Controls/TtsGlyphs.
    /// </remarks>
    private void RenderSettingsButton()
    {
        SettingsBtn.Content = _settingsOpen ? "\uE72B" : "\uE713";
        SettingsBtn.ToolTip = LocalizationService.Get(
            _settingsOpen ? "S.QuickLookup.BackToResult" : "S.QuickLookup.Settings");
    }

    /// <remarks>
    /// In place rather than in a window of its own: a settings window over a popup that disappears
    /// when the pointer leaves it would be a window whose owner can vanish underneath it.
    /// </remarks>
    private void ShowSettings(bool open)
    {
        _settingsOpen = open;
        RenderSettingsButton();

        if (open)
        {
            _suppressAuto = true;
            LoadSharedPreferences();
            _suppressAuto = false;

            RenderSettingsHint();
            SettingsPanel.Visibility = Visibility.Visible;
            ResultPanel.Visibility = Visibility.Collapsed;
            CompactResultPanel.Visibility = Visibility.Collapsed;
            ShowBody(true);
            return;
        }

        SettingsPanel.Visibility = Visibility.Collapsed;
        RenderResultPresentation();
        ShowBody(HasBodyContent());
    }

    private void RenderSettingsHint() =>
        SettingsHint.Text = LocalizationService.Format(
            "S.QuickLookup.SettingsHint",
            SettingsService.Instance.Current.QuickLookupHotkeyDisplay);

    private void OpenFullSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        // The shell takes the foreground, which would close this window a moment later anyway —
        // doing it first means the popup does not flash behind the window that replaced it.
        BeginClose();
        ShellWindow.ShowOrActivate(ShellPage.Settings);
    }

    // ══════════════════════════ Shared preferences ══════════════════════════

    private void LoadSharedPreferences()
    {
        var settings = SettingsService.Instance.Current;

        SrcLangBox.SelectedValue = LanguageData.GetValidSourceCode(settings.SourceLanguage);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        ProviderBox.SelectedValue = settings.Provider;
        if (ProviderBox.SelectedValue is null) ProviderBox.SelectedIndex = 0;
        AutoCopyToggle.IsChecked = settings.QuickLookup.AutoCopyTranslation;

        BilingualToggle.IsChecked = settings.QuickLookup.BilingualEnabled;
        LoadBilingualPair();
        RenderBilingualMode();
    }

    private void AutoCopyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAuto) return;

        SettingsService.Instance.Current.QuickLookup.AutoCopyTranslation =
            AutoCopyToggle.IsChecked == true;
        SettingsService.Instance.Save();
    }

    // ══════════════════════════ 雙語互譯 ══════════════════════════

    private void BilingualToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAuto) return;

        SettingsService.Instance.Current.QuickLookup.BilingualEnabled =
            BilingualToggle.IsChecked == true;
        SettingsService.Instance.Save();

        RenderBilingualMode();
        RetranslateUnderNewPair();
    }

    /// <summary>
    /// Sends the text off again, because which languages it is going between has just changed.
    /// </summary>
    /// <remarks>
    /// The same thing <see cref="ApplyLanguagePair"/> does for the fixed pair, and for the same
    /// reason: the answer on screen belongs to the pair it was asked under, and leaving it there
    /// would have the popup showing one translation while the header names a different pair.
    /// </remarks>
    private void RetranslateUnderNewPair()
    {
        _detectedLang = "";
        ForgetBilingualDirection();
        RenderSourceTtsAvailability();
        RequestTranslate();
    }

    /// <summary>Puts the header and the settings panel into the shape the switch calls for.</summary>
    /// <remarks>
    /// The header's two shapes trade places rather than one of them changing what it holds: the
    /// pickers do not mean the same thing on the two sides of this switch, and the fixed pair has to
    /// still be the fixed pair when the switch goes back off.
    ///
    /// The settings row grows the panel, which grows the window. Nothing has to be done about that
    /// here — the popup is <c>SizeToContent</c> and <see cref="KeepBodyOnScreen"/> already runs on
    /// the resize, which is what keeps the card on the screen when it opens downwards near an edge.
    /// </remarks>
    private void RenderBilingualMode()
    {
        var on = BilingualToggle.IsChecked == true;

        FixedPairPanel.Visibility      = on ? Visibility.Collapsed : Visibility.Visible;
        BilingualStatusBtn.Visibility  = on ? Visibility.Visible   : Visibility.Collapsed;
        BilingualPairRow.Visibility    = on ? Visibility.Visible   : Visibility.Collapsed;

        RenderBilingualStatus();
    }

    /// <summary>Writes the header's one line: what the popup is translating, and which way.</summary>
    /// <remarks>
    /// Three readings, and the rule is that the line only ever claims what is known. With no text,
    /// or with text that has not been placed yet, it says 自動偵測 — the direction has not been
    /// decided, and naming one before it has would be the header telling the user something the
    /// popup has not done. Once the language of the text is known it reads 原文 → 譯文, which is the
    /// answer to the only question this mode raises: 「which way did it just go?」
    ///
    /// The pair itself is not in the line. It is a setting rather than a state, it is two more names
    /// than this cell has room for beside the ones already there, and it is one click away in the
    /// tooltip and in the panel this button opens.
    ///
    /// <see cref="_bilingualSource"/> is null everywhere today, because deciding which of the two
    /// languages the text is in has to happen before the translation is sent and nothing does that
    /// yet. The line reads 自動偵測 until it does, which is true rather than merely unimplemented.
    /// </remarks>
    private void RenderBilingualStatus()
    {
        var first  = LanguageData.GetTargetDisplayName(_bilingualFirst);
        var second = LanguageData.GetTargetDisplayName(_bilingualSecond);

        BilingualStatusText.Text = _bilingualSource is null
            ? LocalizationService.Get("S.QuickLookup.BilingualStatusIdle")
            : LocalizationService.Format(
                "S.QuickLookup.BilingualStatusPair",
                LanguageData.GetTargetDisplayName(_bilingualSource),
                LanguageData.GetTargetDisplayName(TargetOppositeOf(_bilingualSource)));

        BilingualStatusBtn.ToolTip =
            LocalizationService.Format("S.QuickLookup.BilingualStatusTip", first, second);
    }

    /// <summary>Which of the pair a text in <paramref name="source"/> is translated into.</summary>
    /// <remarks>
    /// The whole of what 雙語互譯 decides, in one line. The second language is the answer only for
    /// text already in the first; everything else — including the languages the pair does not
    /// mention at all — goes to the first, which is what makes it the first.
    /// </remarks>
    private string TargetOppositeOf(string source) =>
        string.Equals(source, _bilingualFirst, StringComparison.OrdinalIgnoreCase)
            ? _bilingualSecond
            : _bilingualFirst;

    /// <summary>Records which of the two languages the text turned out to be in.</summary>
    /// <remarks>
    /// 朗讀原文 is settled from here too, because this is the answer it has been waiting for. It is
    /// also the one place this mode is better off than the fixed pair: the characters name the
    /// language without asking anybody, so the speaker works even under an OpenAI-compatible server,
    /// which never reports one — see <see cref="RenderSourceTtsAvailability"/>.
    /// </remarks>
    private void SetBilingualSource(string? source)
    {
        _bilingualSource = source;
        RenderBilingualStatus();
        RenderSourceTtsAvailability();
    }

    /// <summary>Takes the header back to 自動偵測, for when there is nothing left to read.</summary>
    private void ForgetBilingualDirection()
    {
        if (_bilingualSource is null) return;
        SetBilingualSource(null);
    }

    /// <summary>Reads the text, names the direction, and answers with the language to ask for.</summary>
    private string ResolveBilingualTarget(string text)
    {
        SetBilingualSource(
            BilingualDirection.ResolveSource(text, _bilingualFirst, _bilingualSecond));

        return _bilingualSource is null
            ? _bilingualFirst
            : TargetOppositeOf(_bilingualSource);
    }

    /// <summary>
    /// What the translation should have been asked for, once the engine has said what the text was.
    /// </summary>
    /// <returns>
    /// The language to ask again in, or null when the guess already stands — which is the ordinary
    /// case, and the whole reason the characters are consulted first.
    /// </returns>
    /// <remarks>
    /// An OpenAI-compatible server reports nothing here and this does nothing, leaving the guess in
    /// place. For a pair written in two scripts that guess was never in doubt; for one written in
    /// the same script it means the mode stays on the first language, which is what the settings
    /// panel says happens to anything it cannot place.
    /// </remarks>
    private string? CorrectedBilingualTarget(string? detected, string sentTarget)
    {
        if (string.IsNullOrEmpty(detected)) return null;

        var key = LanguageKey(detected);
        var isFirst = LanguageKey(_bilingualFirst) == key;
        var isSecond = LanguageKey(_bilingualSecond) == key;

        // 繁體 and 簡體 are both 「ZH」 to every engine that reports one, so on that pair this
        // answer cannot separate what OpenCC already separated from the characters. It stands aside
        // rather than overwriting a better answer with a coarser one.
        if (isFirst && isSecond) return null;

        var source = isFirst ? _bilingualFirst
                   : isSecond ? _bilingualSecond
                   : LanguageData.MapSourceToTargetCode(detected);

        if (source is not null) SetBilingualSource(source);

        var wanted = isFirst ? _bilingualSecond : _bilingualFirst;
        return wanted.Equals(sentTarget, StringComparison.OrdinalIgnoreCase) ? null : wanted;
    }

    /// <summary>
    /// A language code reduced to the language it names, so the pair and the engine can be compared.
    /// </summary>
    /// <remarks>
    /// The pair is stored in target codes (EN-US, PT-BR) and an engine reports source codes (EN,
    /// PT), so neither side can be matched against the other as written. 繁體 and 簡體 both fold to
    /// ZH deliberately: an engine saying 「Chinese」 has not said which, and pretending otherwise is
    /// how this would come to re-translate 中文 into 中文.
    /// </remarks>
    private static string LanguageKey(string code)
    {
        var upper = code.ToUpperInvariant();
        return upper.StartsWith("ZH", StringComparison.Ordinal) ? "ZH" : upper.Split('-')[0];
    }

    /// <remarks>
    /// The pickers left the header when this mode took it over, so this is the way back to them. The
    /// gear does the same thing, but the pair is what the user was looking at when they decided they
    /// wanted it changed, and that is the thing that should be clickable.
    /// </remarks>
    private void BilingualStatusBtn_Click(object sender, RoutedEventArgs e) => ShowSettings(true);

    /// <summary>Reads the stored pair into the pickers and the header line that show it.</summary>
    private void LoadBilingualPair()
    {
        var quickLookup = SettingsService.Instance.Current.QuickLookup;
        var first  = LanguageData.GetValidTargetCode(quickLookup.BilingualFirstLanguage);
        var second = LanguageData.GetValidTargetCode(quickLookup.BilingualSecondLanguage);

        // One language twice is a pair that translates nothing into itself. The pickers cannot
        // produce it — see ApplyBilingualPick — so it can only come from a hand-edited settings
        // file, and the second slot is the one to move: the first is also the fallback, and
        // redirecting that would change what every lookup does rather than just this pair.
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            second = string.Equals(first, QuickLookupSettings.DefaultSecondLanguage,
                                   StringComparison.OrdinalIgnoreCase)
                ? QuickLookupSettings.DefaultFirstLanguage
                : QuickLookupSettings.DefaultSecondLanguage;
        }

        SetBilingualPair(first, second);
    }

    private void BilingualFirstBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto) return;
        ApplyBilingualPick(((ComboBox)sender).SelectedValue as string, firstSlot: true);
    }

    private void BilingualSecondBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto) return;
        ApplyBilingualPick(((ComboBox)sender).SelectedValue as string, firstSlot: false);
    }

    /// <summary>Takes a pick in one slot, settles the other, and stores the pair.</summary>
    /// <param name="firstSlot">Which slot was picked in — the first is also the fallback.</param>
    private void ApplyBilingualPick(string? picked, bool firstSlot)
    {
        if (string.IsNullOrEmpty(picked)) return;

        var first  = firstSlot ? picked : _bilingualFirst;
        var second = firstSlot ? _bilingualSecond : picked;

        // Picking the language the other slot is already holding is how the pair gets turned around.
        // There is no reading of 「英文 ⇄ 英文」 that translates anything, and the one thing the user
        // can have meant is that the language they just displaced goes to the other side — which is
        // also the only way to change which of the two is the fallback.
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            if (firstSlot) second = _bilingualFirst;
            else           first  = _bilingualSecond;
        }

        SetBilingualPair(first, second);

        var quickLookup = SettingsService.Instance.Current.QuickLookup;
        quickLookup.BilingualFirstLanguage  = first;
        quickLookup.BilingualSecondLanguage = second;
        SettingsService.Instance.Save();

        RetranslateUnderNewPair();
    }

    /// <summary>Writes one pair into the pickers, the line under them, and the header.</summary>
    private void SetBilingualPair(string first, string second)
    {
        _bilingualFirst  = first;
        _bilingualSecond = second;

        // Restored rather than set to false: this also runs from LoadSharedPreferences, which is
        // called from inside a suppressed stretch of its own.
        var wasSuppressed = _suppressAuto;
        _suppressAuto = true;

        BilingualFirstBox.SelectedValue  = first;
        BilingualSecondBox.SelectedValue = second;

        _suppressAuto = wasSuppressed;

        RenderBilingualFallbackHint();
        RenderBilingualStatus();
    }

    /// <remarks>
    /// Composed here rather than in the XAML because it names whichever language is in the first
    /// slot, and that is also why it has to be rebuilt when the interface language changes — see
    /// <see cref="OnLanguageChanged"/>.
    /// </remarks>
    private void RenderBilingualFallbackHint() =>
        BilingualFallbackHint.Text = LocalizationService.Format(
            "S.QuickLookup.BilingualFallback",
            LanguageData.GetTargetDisplayName(_bilingualFirst));

    private void LangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto) return;
        ApplyLanguagePair();
    }

    /// <summary>Saves the pair as the shared preference and re-translates under it.</summary>
    /// <remarks>
    /// Its own method because <see cref="SwapBtn_Click"/> moves both pickers and must not do this
    /// twice: reading either half while the other has already moved would save a pair the user never
    /// chose, and send a translation off under it.
    /// </remarks>
    private void ApplyLanguagePair()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        settings.TargetLanguage = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        SettingsService.Instance.Save();

        // The engine's answer belongs to the language pair it was asked about.
        _detectedLang = "";
        RenderSourceTtsAvailability();
        RequestTranslate();
    }

    /// <summary>
    /// Turns the pair around, carrying the translation back into the box as the new original.
    /// </summary>
    /// <remarks>
    /// The languages alone would not be a swap here, which is where this parts company with
    /// 截圖翻譯's toolbar button. There the source text is the screen and cannot move; here it is a
    /// box with a word in it, and turning the pair around without turning the text around leaves the
    /// popup translating 「hello」 out of 繁體中文 — a round trip through the wrong direction, which
    /// is not what anybody presses this for. 文字翻譯 swaps both for the same reason.
    ///
    /// 自動 is the source language most of the time, and it cannot move to the target side: there is
    /// no such thing as translating into "whatever". <see cref="EffectiveSourceLanguage"/> is what
    /// makes the button work from there anyway — the engine has already said what the original was,
    /// and that answer is what goes across.
    /// </remarks>
    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        // Both read before anything moves: the detected language describes the text that is about to
        // stop being the original, and ApplyLanguagePair clears it.
        var srcVal = EffectiveSourceLanguage();
        var tgtVal = TgtLangBox.SelectedValue as string;

        _suppressAuto = true;

        if (TranslatedText.Text.Length > 0)
        {
            var wasOriginal = SourceTextBox.Text;

            // Through the selection, so Ctrl+Z puts the original back — the same reason 清除 does it
            // that way, and the same text at stake: usually something carried in from another window.
            SourceTextBox.SelectAll();
            SourceTextBox.SelectedText = TranslatedText.Text;

            // Shown flipped now rather than left saying the old thing until the round trip lands.
            // Translating a translation back gives the text it came from, so this already is the
            // answer; waiting for the engine to agree would just read as lag.
            TranslatedText.Text = wasOriginal;
        }

        // Anything already in flight was asked under the old pair and would overwrite both.
        _seq++;

        // Target → source. ZH-HANT has to stay traditional rather than collapse to ZH, which is what
        // the mapping is for.
        if (tgtVal != null)
        {
            SrcLangBox.SelectedValue = LanguageData.MapTargetToSourceCode(tgtVal);
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Source → target. With 自動 and nothing detected — an empty box, or an engine that has not
        // answered — there is nothing to carry across, and English is the fallback: it is what most
        // of what people point this at is written in, and it is one click to correct.
        TgtLangBox.SelectedValue = LanguageData.MapSourceToTargetCode(
            srcVal.Length > 0 ? srcVal : "EN");
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;

        _suppressAuto = false;

        ApplyLanguagePair();

        // The box holds text the user may well want to edit now, and the click left the caret on a
        // button. Caret after Focus, which selects the whole box on its own when focus arrives
        // programmatically — see Refill.
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto || ProviderBox.SelectedValue is not TranslationProvider provider) return;

        SettingsService.Instance.Current.Provider = provider;
        SettingsService.Instance.Save();

        // The engine's answer belongs to the engine that gave it, and the new one may not answer at
        // all — see RenderSourceTtsAvailability.
        _detectedLang = "";
        RenderSourceTtsAvailability();
        RequestTranslate();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => BeginClose();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RenderChrome();
        RenderCopyLabel(copied: false);
        RenderCollapseButton();
        RenderSettingsButton();
        RenderSettingsHint();
        RenderSourceTtsAvailability();
        RenderBilingualFallbackHint();
        RenderBilingualStatus();
        if (TranslatedText.Text.Length > 0 && StatusText.Visibility != Visibility.Visible)
            RenderCompactCompletion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
        _copiedHold.Stop();
        _foregroundWatch.Stop();
        _tts.Dispose();

        // Static and outliving every window, so a handler left attached keeps this one alive for as
        // long as the application runs.
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }
}
