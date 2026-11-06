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
using OverTranslate.Services.Realtime.Capture;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The list that drops from one half of the running capsule's language pair (#345): pick a
/// language and it is in use, there and then.
/// </summary>
/// <remarks>
/// <para>A window of its own rather than a Popup in the capsule. The capsule never takes
/// activation, so that it cannot pull focus from a full-screen game, and a Popup inside a window
/// that is never active gets no keyboard — the target list's search box would show a caret and type
/// nothing.</para>
///
/// <para>Like 顯示外觀, it does not take the foreground when it opens (#358). It used to, and close
/// on losing it; but some applications in the foreground — the Photos app, measured — have Windows
/// refuse the handoff, and the list was activated for an instant, deactivated at once and closed
/// before it was ever seen. The rows are picked with the mouse, so the list needs no keyboard
/// until the search box is pressed; only then is the foreground asked for, and given back on a
/// choice, Enter or Esc. The list is put away by a press anywhere outside it or the foreground
/// moving to another application — watched for, since a window that never activates hears of
/// neither through a deactivation.</para>
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

    // The two widths the design settled on: the source list is a few short names, the target list
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

    // While the list is up: what puts it away when the user moves on — see the remarks.
    private MouseDownHook? _pressWatch;
    private ForegroundChangeWatch? _foregroundWatch;

    // Set while the search box holds the keyboard.
    private bool _typing;

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

        // A few names need no search box, and without one the list needs a word to say what it is a
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
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);

        Log.Info(
            "Realtime language list {Kind}: created, selected={Selected} entries={Count} anchor={Anchor} bar={Bar} screen={Screen}",
            kind, selectedCode, _entries.Count, anchor, bar, screen);
    }

    public RealtimeLanguageMenuKind Kind { get; }

    /// <summary>Raised with the code of the language the user picked, just before the menu closes.</summary>
    public event EventHandler<string>? LanguagePicked;

    /// <summary>
    /// Raised with the code of a language the user asked to download. The capsule switches to it when
    /// the download lands, by which time this menu may well be gone.
    /// </summary>
    public event EventHandler<string>? PackDownloadRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show could hand it the foreground: see the remarks for why it does not take it.
        WindowStyles.ApplyNoActivate(this);
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
            _pressWatch = MouseDownHook.Install(OnAnyPress);
            _foregroundWatch = ForegroundChangeWatch.Install(OnForegroundChanged);
        }
    }

    // ── Putting it away ─────────────────────────────────────────────────────────────────────────

    /// <summary>A press outside the list is the user moving on, and whatever they pressed keeps the focus.</summary>
    /// <remarks>
    /// One on the capsule is left to the capsule, which closes the list itself and has to know it did,
    /// so the press on an open list's own name closes it rather than opening it again — see
    /// RealtimeControlWindow.ToggleMenu.
    /// </remarks>
    private void OnAnyPress(Point screenPoint)
    {
        if (_closing) return;
        if (ContainsScreenPoint(Chrome, screenPoint)) return;
        if (_bar.Contains((int)screenPoint.X, (int)screenPoint.Y)) return;

        Dismiss(giveBackForeground: false, $"press outside at {(int)screenPoint.X},{(int)screenPoint.Y}");
    }

    /// <summary>Another application in the foreground — Alt+Tab, or one coming forward by itself.</summary>
    private void OnForegroundChanged()
    {
        if (_closing || KeyboardForeground.IsOwnForeground()) return;
        Dismiss(giveBackForeground: false, $"foreground moved to {KeyboardForeground.Describe()}");
    }

    private static bool ContainsScreenPoint(FrameworkElement element, Point screenPoint)
    {
        if (PresentationSource.FromVisual(element) is null) return false;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight).Contains(screenPoint);
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
        if (entry.Language.Pack is { } pack)
        {
            var hint = PackHint(pack);
            DockPanel.SetDock(hint, Dock.Right);
            content.Children.Add(hint);
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
    /// A press in the search box is the one thing here that wants the keyboard, so it is the one
    /// thing that takes the foreground — 顯示外觀's size and colour boxes work the same way.
    /// </summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_typing || !IsInside(e.OriginalSource as DependencyObject, SearchBox)) return;

        var before = GetForegroundWindow();
        var previous = KeyboardForeground.Take(this);
        if (previous != IntPtr.Zero) _previousForeground = previous;
        _typing = true;

        Log.Info(
            "Realtime language list {Kind}: search box pressed, foreground before={Before:X} after={After:X}",
            Kind, before.ToInt64(), GetForegroundWindow().ToInt64());

        // The activation that hands this window the keyboard also puts WPF's focus back where the
        // window last had it — nowhere — after the press has already given it to the box.
        FocusWhenReady(SearchBox, attemptsLeft: 8);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
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

    // Size, or progress, at the row's far end for as long as the language is not ready. Bound rather
    // than set: the download runs on whether or not anyone is looking, and this menu may be the one
    // open while it does.
    private static FrameworkElement PackHint(OverTranslate.Services.Ocr.OcrLanguagePack pack)
    {
        var mark = new Path
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 0, 4, 0),
            Stretch = Stretch.None,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M6,1 L6,8 M3,5.2 L6,8.2 L9,5.2 M1.5,11 L10.5,11")
        };
        mark.SetResourceReference(Shape.StrokeProperty, "AppTextSecondary");
        var visible = new System.Windows.Controls.BooleanToVisibilityConverter();
        mark.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(pack.ShowsDownloadMark)) { Source = pack, Converter = visible });

        var text = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(ForegroundProperty, "AppTextSecondary");
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(pack.RowText)) { Source = pack });

        var hint = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        hint.Children.Add(mark);
        hint.Children.Add(text);
        hint.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(pack.ShowsRowHint)) { Source = pack, Converter = visible });
        return hint;
    }

    // In this window, in place of the list, rather than one of its own: it is already out of the
    // capture and in the session's re-assert, and it already takes the mouse without the foreground.
    private void ShowPackCard(RealtimeLanguageMenuEntry entry)
    {
        if (entry.Language.Pack is { IsDownloading: false } idle) idle.ClearFailure();

        var card = new global::OverTranslate.Controls.OcrLanguagePackCard(entry.Language);
        card.DownloadRequested += (_, _) => PackDownloadRequested?.Invoke(this, entry.Code);
        card.Finished += (_, _) => Dismiss(giveBackForeground: true, "language pack card finished");

        // Asking while it is already on its way means they want this language when it lands.
        if (entry.Language.Pack is { IsDownloading: true })
            PackDownloadRequested?.Invoke(this, entry.Code);

        HeaderText.Visibility = Visibility.Collapsed;
        ItemsScroll.Visibility = Visibility.Collapsed;
        CardHost.Content = card;
        CardHost.Visibility = Visibility.Visible;
        Chrome.Width = card.Width + CardHost.Margin.Left + CardHost.Margin.Right + (MenuBorder + MenuPadding) * 2;
        Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    private void Pick(RealtimeLanguageMenuEntry entry)
    {
        if (_closing) return;

        if (entry.Language.Pack is { IsReady: false })
        {
            ShowPackCard(entry);
            return;
        }

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

        _pressWatch?.Dispose();
        _pressWatch = null;
        _foregroundWatch?.Dispose();
        _foregroundWatch = null;

        // Handed back only if the search box still holds it: a press somewhere else has already
        // moved it there, and pulling it back would undo the press.
        if (_typing)
        {
            _typing = false;
            if (giveBackForeground) KeyboardForeground.Release(this, _previousForeground);
            else TypingWindows.Remove(new WindowInteropHelper(this).Handle);
        }

        Close();
    }

    /// <summary>
    /// The typing is over: the keyboard went elsewhere, or a handoff Windows refused gave it to this
    /// window for an instant only. The list stays — it is a press outside it or the foreground
    /// moving to another application that puts it away — and a press on it no longer activates it.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_typing || _closing) return;

        _typing = false;
        TypingWindows.Remove(new WindowInteropHelper(this).Handle);
        WindowStyles.SetNoActivate(this, enabled: true);

        Log.Info(
            "Realtime language list {Kind}: search box lost the keyboard after {Ms}ms, foreground={Foreground:X}",
            Kind, _age.ElapsedMilliseconds, GetForegroundWindow().ToInt64());
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Log.Info(
            "Realtime language list {Kind}: closed after {Ms}ms, shown={Shown}, reason={Reason}",
            Kind, _age.ElapsedMilliseconds, Opacity >= 1, _closeReason);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
