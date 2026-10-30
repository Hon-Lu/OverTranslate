using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NLog;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The list that drops from one half of the running capsule's language pair (#345): pick a
/// language and it is in use, there and then.
/// </summary>
/// <remarks>
/// <para>A window of its own rather than a Popup in the capsule. The capsule never takes
/// activation, so that it cannot pull focus from a full-screen game, and a Popup inside a window
/// that is never active gets no keyboard — the target list's search box would show a caret and type
/// nothing. This one does activate, for as long as it is open, because the user has just clicked to
/// use it; it gives the foreground back to whatever had it when it closes on a choice or on Esc.
/// Clicking anywhere else is the user moving on, and whatever they clicked keeps the focus.</para>
///
/// <para>Nothing about the session's rules for windows of its own is special-cased here. It is a
/// visible top-level window of this process, so the capture leaves it out of every frame
/// (OwnTopLevelWindows), and a topmost one with no owner, so the session's once-a-second re-assert
/// raises it first and puts the layers in under it (RealtimeSessionController.RaiseCompanions). An
/// owner would cost both: a window owned by the capsule is skipped by that re-assert, and raising the
/// capsule would drag it along.</para>
/// </remarks>
internal partial class RealtimeLanguageMenu : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // The two widths the design settled on: the source list is five short names, the target list
    // also carries the search box and thirty names, some of them long in Russian.
    private const double SourceWidth = 184;
    private const double TargetWidth = 220;
    private const double TargetListMaxHeight = 236;

    // Between the capsule's edge and the menu's.
    private const double Gap = 6;

    // What stands between each surface's edge and its text, so the menu can be set where the names
    // line up rather than the boxes: the menu's border and padding (Chrome), a row's padding
    // (LanguageRow), and the padding of the capsule's language button (RealtimeLangButton). Each
    // mirrors its template's value.
    private const double MenuBorder = 1;
    private const double MenuPadding = 6;
    private const double RowPadding = 10;
    private const double AnchorPadding = 6;

    /// <summary>
    /// How far left of the pressed button the menu's edge goes, in DIPs, so the names in the list
    /// start where the name on the button does.
    /// </summary>
    internal const double TextAlignmentOffset = MenuBorder + MenuPadding + RowPadding - AnchorPadding;

    private readonly IReadOnlyList<RealtimeLanguageMenuEntry> _entries;
    private readonly List<Button> _rows = [];
    private readonly System.Drawing.Rectangle _anchor;
    private readonly System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private IntPtr _previousForeground;
    private double _windowScale = 1.0;
    private bool _closing;

    // For the log only (#358): a list that was pressed open and never seen left nothing behind to
    // say how far it got, so each step says so, and the close says why and after how long.
    private readonly System.Diagnostics.Stopwatch _age = System.Diagnostics.Stopwatch.StartNew();
    private string _closeReason = "closed without Dismiss";

    /// <param name="kind">Which half of the pair this menu changes.</param>
    /// <param name="selectedCode">The language in use, marked in the list.</param>
    /// <param name="anchor">The pressed button, in physical pixels: the names in the menu line up with the one on it.</param>
    /// <param name="bar">The capsule's visible surface, in physical pixels: the menu opens clear of it.</param>
    /// <param name="screen">The session's screen, which the menu has to stay on.</param>
    public RealtimeLanguageMenu(
        RealtimeLanguageMenuKind kind,
        string? selectedCode,
        System.Drawing.Rectangle anchor,
        System.Drawing.Rectangle bar,
        System.Drawing.Rectangle screen)
    {
        InitializeComponent();

        Kind = kind;
        _anchor = anchor;
        _bar = bar;
        _screen = screen;
        _entries = RealtimeLanguageMenuItems.For(kind, selectedCode);

        var searchable = kind == RealtimeLanguageMenuKind.Target;
        Chrome.Width = searchable ? TargetWidth : SourceWidth;
        SearchBox.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.Common.SearchLanguage");
        if (searchable) ItemsScroll.MaxHeight = TargetListMaxHeight;

        // Five names need no search box, and without one the list needs a word to say what it is a
        // list of: the capsule shows the pair without labels.
        HeaderText.Visibility = searchable ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Text = LocalizationService.Get("S.Realtime.SourceLanguage");

        foreach (var entry in _entries)
        {
            var row = BuildRow(entry);
            _rows.Add(row);
            ItemsPanel.Children.Add(row);
        }

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);

        Log.Info(
            "Realtime language list {Kind}: created, selected={Selected} entries={Count} anchor={Anchor} bar={Bar} screen={Screen}",
            kind, selectedCode, _entries.Count, anchor, bar, screen);
    }

    public RealtimeLanguageMenuKind Kind { get; }

    /// <summary>Raised with the code of the language the user picked, just before the menu closes.</summary>
    public event EventHandler<string>? LanguagePicked;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show hands this window the foreground: what had it is where the keyboard goes back.
        _previousForeground = GetForegroundWindow();

        // Opened at the anchor, so the window starts on the monitor it will be placed on and the scale
        // read in Place is that monitor's.
        ScreenGeometry.MoveToPhysical(this, _anchor.Left, _anchor.Bottom);

        Log.Info(
            "Realtime language list {Kind}: window created after {Ms}ms, hwnd={Hwnd:X} foreground={Foreground:X}",
            Kind, _age.ElapsedMilliseconds, new WindowInteropHelper(this).Handle.ToInt64(), _previousForeground.ToInt64());
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sizes the menu for the session's screen, drops it below the capsule — or above it, when the
    /// capsule sits too low for the list to fit underneath — and only then shows it.
    /// </summary>
    private void Place()
    {
        if (_closing)
        {
            Log.Info("Realtime language list {Kind}: placement skipped, already closing ({Reason})", Kind, _closeReason);
            return;
        }

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;
        else
            Log.Info("Realtime language list {Kind}: no composition target at placement", Kind);

        // The capsule's own correction — see RealtimeControlWindow.ApplyScreenScale — so the menu
        // comes out the same physical size as the bar it drops from.
        double screenScale = ScreenGeometry.ScaleAt(_anchor.Left, _anchor.Top);
        double relative = screenScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);
        UpdateLayout();

        int Physical(double dip) => (int)Math.Round(dip * screenScale);

        var margin = RootChrome.Margin;
        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(Chrome.ActualHeight);
        int gap = Physical(Gap);

        int below = _bar.Bottom + gap;
        int above = _bar.Top - gap - chromeHeight;
        bool opensUp = below + chromeHeight > _screen.Bottom && above >= _screen.Top;
        int chromeTop = opensUp ? above : below;

        int chromeLeft = ChromeLeft(_anchor.Left, Physical(TextAlignmentOffset), chromeWidth, _screen);

        // Cast upwards when the menu opens upwards, so the shadow falls on the side away from the bar.
        Shadow.Direction = opensUp ? 90 : 270;

        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));

        var first = Opacity < 1;
        Log.Info(
            "Realtime language list {Kind}: placed after {Ms}ms, first={First} windowScale={WindowScale} screenScale={ScreenScale} " +
            "chrome={ChromeLeft},{ChromeTop} {ChromeWidth}x{ChromeHeight} opensUp={OpensUp} window={Bounds}",
            Kind, _age.ElapsedMilliseconds, first, _windowScale, screenScale,
            chromeLeft, chromeTop, chromeWidth, chromeHeight, opensUp, ScreenGeometry.PhysicalBounds(this));

        if (first)
        {
            Opacity = 1;
            TakeForeground();
            FocusFirst();
        }
    }

    /// <summary>
    /// The menu's left edge, in physical pixels: the text-aligned spot left of the button, held on
    /// the screen — flush with its left edge when the offset would push past it, and pulled in from
    /// the right edge when the menu is wider than the room there.
    /// </summary>
    internal static int ChromeLeft(int anchorLeft, int offset, int chromeWidth, System.Drawing.Rectangle screen) =>
        Math.Clamp(anchorLeft - offset, screen.Left, Math.Max(screen.Left, screen.Right - chromeWidth));

    private Button BuildRow(RealtimeLanguageMenuEntry entry)
    {
        var label = new TextBlock
        {
            Text = entry.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var row = new Button
        {
            Style = (Style)FindResource("LanguageRow"),
            Tag = entry,
            ToolTip = entry.Language.Display == entry.Label ? null : entry.Language.Display
        };
        System.Windows.Automation.AutomationProperties.SetName(row, entry.Language.Display);

        var content = new DockPanel { LastChildFill = true };
        if (entry.IsSelected)
        {
            row.SetResourceReference(ForegroundProperty, "AppAccent");
            System.Windows.Automation.AutomationProperties.SetItemStatus(row, "✓");

            var tick = new Path
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(8, 0, 0, 0),
                Stretch = Stretch.None,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M2.5,7.4 L5.6,10.3 L11.5,3.9")
            };
            tick.SetResourceReference(Shape.StrokeProperty, "AppAccent");
            DockPanel.SetDock(tick, Dock.Right);
            content.Children.Add(tick);
        }
        content.Children.Add(label);
        row.Content = content;

        row.Click += (_, _) => Pick(entry);
        return row;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        var matches = 0;
        foreach (var row in _rows)
        {
            var visible = RealtimeLanguageMenuItems.Matches((RealtimeLanguageMenuEntry)row.Tag, query);
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) matches++;
        }

        NoResultsText.Text = LocalizationService.Format("S.Realtime.LanguageNoMatch", query.Trim());
        NoResultsText.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsScroll.ScrollToTop();
    }

    /// <summary>
    /// The caret in the search box the moment the list is up, so the user can type straight away;
    /// on the source list, which has none, the language in use.
    /// </summary>
    private void FocusFirst()
    {
        if (SearchBox.Visibility == Visibility.Visible)
        {
            FocusWhenReady(SearchBox, attemptsLeft: 8);
            return;
        }

        var selected = _rows.FirstOrDefault(row => ((RealtimeLanguageMenuEntry)row.Tag).IsSelected) ?? _rows.FirstOrDefault();
        if (selected is not null) FocusWhenReady(selected, attemptsLeft: 8);
    }

    // The window has only just been handed the foreground, and focus set in the same pass is
    // sometimes lost to that handoff — the same story ComboBoxSearch tells of its own box.
    private void FocusWhenReady(UIElement element, int attemptsLeft)
    {
        if (_closing || element.IsKeyboardFocused || element.Focus() || attemptsLeft <= 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusWhenReady(element, attemptsLeft - 1));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Dismiss(giveBackForeground: true, "Esc");
                break;

            case Key.Enter:
                // A row picks itself on Enter; the search box picks nothing, the way the capture
                // toolbar's does — there is no highlighted match until the user arrows onto one.
                e.Handled = true;
                if (Keyboard.FocusedElement is Button { Tag: RealtimeLanguageMenuEntry entry }) Pick(entry);
                break;

            case Key.Down:
                e.Handled = MoveFocus(+1);
                break;

            case Key.Up:
                e.Handled = MoveFocus(-1) || (SearchBox.Visibility == Visibility.Visible && SearchBox.Focus());
                break;
        }
    }

    /// <summary>Steps the keyboard through the rows the search has left visible.</summary>
    private bool MoveFocus(int step)
    {
        var visible = _rows.Where(row => row.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return false;

        var index = Keyboard.FocusedElement is Button focused ? visible.IndexOf(focused) : -1;
        var next = index < 0 ? (step > 0 ? 0 : -1) : index + step;
        if (next < 0 || next >= visible.Count) return false;

        visible[next].BringIntoView();
        return visible[next].Focus();
    }

    private void Pick(RealtimeLanguageMenuEntry entry)
    {
        if (_closing) return;

        // Closed first: what the pick sets off rebuilds the overlays, and the menu has nothing to
        // show over them.
        Dismiss(giveBackForeground: true, $"picked {entry.Code}");
        LanguagePicked?.Invoke(this, entry.Code);
    }

    /// <summary>Closes the menu, leaving the session as it was unless something was picked.</summary>
    /// <param name="giveBackForeground">
    /// Whether to hand the keyboard back to the window that had it before the menu opened — true when
    /// the user finished here, false when they clicked somewhere else and that is where it should go.
    /// </param>
    /// <param name="reason">What closed it, for the log.</param>
    public void Dismiss(bool giveBackForeground, string reason)
    {
        if (_closing) return;
        _closing = true;
        _closeReason = reason;

        if (giveBackForeground && IsActive) GiveBackForeground();
        Close();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Before the first placement the window is still invisible and may be deactivated by the
        // very handoff that is giving it the foreground.
        if (Opacity >= 1)
        {
            Dismiss(giveBackForeground: false, $"deactivated, foreground={GetForegroundWindow().ToInt64():X}");
            return;
        }

        Log.Info(
            "Realtime language list {Kind}: deactivated before it was shown, after {Ms}ms, foreground={Foreground:X}",
            Kind, _age.ElapsedMilliseconds, GetForegroundWindow().ToInt64());
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Log.Info(
            "Realtime language list {Kind}: closed after {Ms}ms, shown={Shown}, reason={Reason}",
            Kind, _age.ElapsedMilliseconds, Opacity >= 1, _closeReason);
    }

    // ── Foreground ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Makes this the window the keyboard talks to; see QuickLookupWindow.TakeForeground.</summary>
    /// <remarks>
    /// Usually allowed outright — the user has just clicked the capsule, which is this process — but
    /// the capsule never activates, and Windows weighs which window is foreground as well as who got
    /// the last input. Sharing the foreground thread's input queue for the call lifts the refusal.
    /// </remarks>
    private void TakeForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Info("Realtime language list {Kind}: no window handle to take the foreground with", Kind);
            return;
        }

        var foreground = GetForegroundWindow();
        var owner = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);
        bool allowed;

        try
        {
            allowed = SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(self, owner, false);
        }

        Log.Info(
            "Realtime language list {Kind}: foreground taken, before={Before:X} attached={Attached} allowed={Allowed} after={After:X} active={Active}",
            Kind, foreground.ToInt64(), attached, allowed, GetForegroundWindow().ToInt64(), IsActive);
    }

    private void GiveBackForeground()
    {
        var previous = _previousForeground;
        if (previous == IntPtr.Zero || !IsWindow(previous)) return;
        if (previous == new WindowInteropHelper(this).Handle) return;

        // Allowed without ceremony: this window holds the foreground, and the holder may pass it on.
        SetForegroundWindow(previous);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
