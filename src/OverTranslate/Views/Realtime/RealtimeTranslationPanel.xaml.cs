using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime.Capture;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The panel the running capsule's translation button opens: the service, the language read and the
/// language translated into, side by side. A pick is in use there and then, and the panel stays open
/// for the next.
/// </summary>
/// <remarks>
/// <para>Only the settings. What the session is doing — paused, loading a model, failing — is said on
/// the capsule, which the panel opens beside rather than over, so it can still be read with the panel
/// open; the panel looks the same whatever the session is doing.</para>
///
/// <para>A window of its own rather than a Popup in the capsule. The capsule never takes activation,
/// so that it cannot pull focus from a full-screen game, and a Popup inside a window that is never
/// active gets no keyboard — the search box would show a caret and type nothing.</para>
///
/// <para>It does not take the foreground when it opens (#358): some applications in the foreground —
/// the Photos app, measured — have Windows refuse the handoff, and a window that closed on losing it
/// was gone before it was seen. The rows are picked with the mouse, so the panel needs no keyboard
/// until the search box is pressed; only then is the foreground asked for, and given back on Esc or
/// when the panel closes. The panel is put away by the button pressed again, Esc, a press anywhere
/// outside it, or the foreground moving to another application — watched for, since a window that
/// never activates hears of neither through a deactivation.</para>
///
/// <para>Nothing about the session's rules for windows of its own is special-cased here. It is a
/// visible top-level window of this process, so the capture leaves it out of every frame
/// (OwnTopLevelWindows), and a topmost one with no owner, so the session's once-a-second re-assert
/// raises it with the capsule (RealtimeSessionController.RaiseCompanions).</para>
/// </remarks>
internal partial class RealtimeTranslationPanel : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Between the capsule's edge and the panel's — the bar's other panels' gap.
    private const double Gap = 6;

    private readonly List<Button> _providerRows = [];
    private readonly List<Button> _sourceRows = [];
    private readonly List<Button> _targetRows = [];
    private System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private TranslationProvider _provider;
    private string _sourceCode;
    private string _targetCode;

    private IntPtr _previousForeground;
    private double _windowScale = 1.0;
    private double _appliedRelative = 1.0;
    private bool _closing;
    private bool _placed;
    private bool _opensUp;

    // While the panel is up: what puts it away when the user moves on — see the remarks.
    private MouseDownHook? _pressWatch;
    private ForegroundChangeWatch? _foregroundWatch;

    // Set while the search box holds the keyboard.
    private bool _typing;

    // The source column's width with its list in it, while a language pack card stands in for the list.
    private double _sourceListWidth = double.NaN;

    // For the log only (#358).
    private readonly System.Diagnostics.Stopwatch _age = System.Diagnostics.Stopwatch.StartNew();
    private string _closeReason = "closed without Dismiss";

    /// <param name="provider">The service in use, marked in its column.</param>
    /// <param name="sourceCode">The language read, marked in its column.</param>
    /// <param name="targetCode">The language translated into, marked in its column.</param>
    /// <param name="bar">The capsule's visible surface, in physical pixels: the panel opens clear of it.</param>
    /// <param name="screen">The session's screen, which the panel has to stay on.</param>
    public RealtimeTranslationPanel(
        TranslationProvider provider,
        string sourceCode,
        string targetCode,
        System.Drawing.Rectangle bar,
        System.Drawing.Rectangle screen)
    {
        InitializeComponent();

        _provider = LanguageData.CurrentProvider(provider);
        _sourceCode = sourceCode;
        _targetCode = targetCode;
        _bar = bar;
        _screen = screen;

        SearchBox.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.Common.SearchLanguage");

        BuildProviderRows();
        foreach (var entry in RealtimeTranslationPanelItems.For(RealtimeLanguageKind.Source, sourceCode))
            AddLanguageRow(entry, RealtimeLanguageKind.Source);
        foreach (var entry in RealtimeTranslationPanelItems.For(RealtimeLanguageKind.Target, targetCode))
            AddLanguageRow(entry, RealtimeLanguageKind.Target);

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(FirstPlace), DispatcherPriority.Loaded);
        SizeChanged += (_, _) =>
        {
            if (_placed) Place();
        };

        Log.Info(
            "Realtime translation panel: created, provider={Provider} pair={Src}->{Tgt} bar={Bar} screen={Screen}",
            _provider, sourceCode, targetCode, bar, screen);
    }

    /// <summary>Raised with the service the user picked. The panel stays open.</summary>
    public event EventHandler<TranslationProvider>? ProviderPicked;

    /// <summary>Raised with the language the user picked, and which of the two it is for. The panel stays open.</summary>
    public event EventHandler<(RealtimeLanguageKind Kind, string Code)>? LanguagePicked;

    /// <summary>
    /// Raised with the code of a language the user asked to download. The capsule switches to it when
    /// the download lands, by which time this panel may well be gone.
    /// </summary>
    public event EventHandler<string>? PackDownloadRequested;

    /// <summary>
    /// Marks what the session now runs with — after a pick here went through, or changed from
    /// elsewhere — without disturbing the search or where each list is scrolled to.
    /// </summary>
    public void ShowSelection(TranslationProvider provider, string sourceCode, string targetCode)
    {
        provider = LanguageData.CurrentProvider(provider);
        if (provider != _provider)
        {
            _provider = provider;
            // Built again rather than re-marked: a hidden service the session has just left goes out
            // of the list with the change.
            BuildProviderRows();
        }

        _sourceCode = sourceCode;
        _targetCode = targetCode;
        foreach (var row in _sourceRows) Mark(row, IsCode(row, sourceCode));
        foreach (var row in _targetRows) Mark(row, IsCode(row, targetCode));
    }

    private static bool IsCode(Button row, string code) =>
        row.Tag is RealtimeLanguageEntry entry && string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase);

    /// <summary>The capsule moved — dragged, or its width changed — and the panel goes with it.</summary>
    public void UpdateAnchor(System.Drawing.Rectangle bar)
    {
        if (bar == _bar || _closing) return;
        _bar = bar;
        if (_placed) Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show could hand it the foreground: see the remarks for why it does not take it.
        WindowStyles.ApplyNoActivate(this);
        _previousForeground = GetForegroundWindow();

        // Opened at the bar, so the window starts on the monitor it will be placed on and the scale
        // read in Place is that monitor's.
        ScreenGeometry.MoveToPhysical(this, _bar.Left, _bar.Top);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────

    private void FirstPlace()
    {
        if (_closing) return;

        // Each column keeps the width it opened with, every row in it: a search that left only short
        // names would otherwise narrow the panel under the pointer, a keystroke at a time.
        UpdateLayout();
        foreach (var column in new FrameworkElement[] { ProviderColumn, SourceColumn, TargetColumn })
            column.Width = column.ActualWidth;
        SourceNoMatch.MaxWidth = Math.Max(0, SourceColumn.Width - 28);
        TargetNoMatch.MaxWidth = Math.Max(0, TargetColumn.Width - 28);

        // Opened on what is in use, so a long list shows its current choice without a scroll.
        BringSelectedIntoView(_sourceRows, _sourceCode);
        BringSelectedIntoView(_targetRows, _targetCode);

        Place();
    }

    private static void BringSelectedIntoView(List<Button> rows, string code)
    {
        var selected = rows.FirstOrDefault(row => IsCode(row, code));
        selected?.BringIntoView();
    }

    /// <summary>
    /// Sizes the panel for the session's screen and sets it against the capsule — above it, or below
    /// when there is more room there — its right edge on the capsule's, which is the edge the capsule
    /// keeps still. The bar's other panels are placed the same way (see RealtimeAppearancePanel).
    /// Shown only once it is there.
    /// </summary>
    private void Place()
    {
        if (_closing) return;

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;

        // The capsule's own correction — see RealtimeControlWindow.ApplyScreenScale — so the panel
        // comes out the same physical size as the bar it opens from.
        double screenScale = ScreenGeometry.ScaleAt(_bar.Left, _bar.Top);
        double relative = screenScale / _windowScale;
        // Set only when it changes: Place also runs on every size change, and setting it again
        // would lay the panel out again for nothing.
        if (Math.Abs(relative - _appliedRelative) >= 0.001)
        {
            _appliedRelative = relative;
            RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(relative, relative);
            UpdateLayout();
        }

        int Physical(double dip) => (int)Math.Round(dip * screenScale);

        var margin = RootChrome.Margin;
        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(Chrome.ActualHeight);
        int gap = Physical(Gap);

        // Decided once, on the first placement: a panel that flipped sides as a card replaced a list
        // would move out from under the pointer.
        (_opensUp, int chromeTop) = RealtimeAppearancePanel.PlaceVertically(
            _bar.Top, _bar.Bottom, _screen.Top, _screen.Bottom, chromeHeight, gap, _placed ? _opensUp : null);

        int chromeLeft = Math.Clamp(
            _bar.Right - chromeWidth, _screen.Left, Math.Max(_screen.Left, _screen.Right - chromeWidth));

        // Cast away from the bar.
        Shadow.Direction = _opensUp ? 90 : 270;

        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));

        if (!_placed)
        {
            _placed = true;
            Opacity = 1;
            _pressWatch = MouseDownHook.Install(OnAnyPress);
            _foregroundWatch = ForegroundChangeWatch.Install(OnForegroundChanged);
            Log.Info(
                "Realtime translation panel: placed after {Ms}ms, chrome={Left},{Top} {Width}x{Height} opensUp={OpensUp}",
                _age.ElapsedMilliseconds, chromeLeft, chromeTop, chromeWidth, chromeHeight, _opensUp);
        }
    }

    // ── Putting it away ─────────────────────────────────────────────────────────────────────────

    /// <summary>A press outside the panel is the user moving on, and whatever they pressed keeps the focus.</summary>
    /// <remarks>
    /// One on the capsule is left to the capsule, which closes the panel itself and has to know it did,
    /// so the press on the button that opened it closes it rather than opening it again — see
    /// RealtimeControlWindow.TranslationBtn_Click.
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

    // ── Rows ─────────────────────────────────────────────────────────────────────────────────────

    private void BuildProviderRows()
    {
        ProviderRows.Children.Clear();
        _providerRows.Clear();

        var hidden = SettingsService.Instance.Current.HiddenProviders;
        foreach (var entry in RealtimeTranslationPanelItems.Providers(hidden, _provider))
        {
            // The mark in a box of its own, so the names line up whatever each mark's proportions.
            var mark = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            mark.Children.Add(new global::OverTranslate.Controls.ProviderMark { Provider = entry.Provider, Size = 16 });
            DockPanel.SetDock(mark, Dock.Left);

            var row = BuildRow(entry.Label, entry.Label, entry.IsSelected, (Style)FindResource("PanelRow"), mark);
            row.Tag = entry;
            row.Click += (_, _) => PickProvider(entry);
            _providerRows.Add(row);
            ProviderRows.Children.Add(row);
        }
    }

    private void AddLanguageRow(RealtimeLanguageEntry entry, RealtimeLanguageKind kind)
    {
        var hint = entry.Language.Pack is { } pack ? PackHint(pack) : null;
        var row = BuildRow(entry.Label, entry.Language.Display, entry.IsSelected, (Style)FindResource("LanguagePanelRow"), null, hint);
        row.Tag = entry;
        if (entry.Language.Display != entry.Label) row.ToolTip = entry.Language.Display;
        row.Click += (_, _) => PickLanguage(entry, kind);

        if (kind == RealtimeLanguageKind.Source)
        {
            _sourceRows.Add(row);
            SourceRows.Children.Add(row);
        }
        else
        {
            _targetRows.Add(row);
            TargetRows.Children.Add(row);
        }
    }

    private static Button BuildRow(
        string label, string accessibleName, bool selected, Style style, FrameworkElement? lead, FrameworkElement? trail = null)
    {
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

        var content = new DockPanel { LastChildFill = true };
        content.Children.Add(tick);
        if (trail is not null)
        {
            DockPanel.SetDock(trail, Dock.Right);
            content.Children.Add(trail);
        }
        if (lead is not null) content.Children.Add(lead);
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new Button { Style = style, Content = content };
        System.Windows.Automation.AutomationProperties.SetName(row, accessibleName);
        Mark(row, selected);
        return row;
    }

    // The tick keeps its room when it is not shown, so marking another row does not shift the names.
    private static void Mark(Button row, bool selected)
    {
        if (selected) row.SetResourceReference(ForegroundProperty, "AppAccent");
        else row.ClearValue(ForegroundProperty);
        System.Windows.Automation.AutomationProperties.SetItemStatus(row, selected ? "✓" : "");

        if (row.Content is DockPanel content && content.Children.OfType<Path>().FirstOrDefault() is { } tick)
            tick.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        Filter(_sourceRows, SourceNoMatch, SourceScroll, query);
        Filter(_targetRows, TargetNoMatch, TargetScroll, query);
    }

    private static void Filter(List<Button> rows, TextBlock noMatch, ScrollViewer scroll, string query)
    {
        var matches = 0;
        foreach (var row in rows)
        {
            var visible = RealtimeTranslationPanelItems.Matches((RealtimeLanguageEntry)row.Tag, query);
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) matches++;
        }

        noMatch.Text = LocalizationService.Format("S.Realtime.LanguageNoMatch", query.Trim());
        noMatch.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        scroll.ScrollToTop();
    }

    /// <summary>
    /// A press in the search box is the one thing here that wants the keyboard, so it is the one
    /// thing that takes the foreground — 顯示外觀's size and colour boxes work the same way.
    /// </summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_typing || !IsInside(e.OriginalSource as DependencyObject, SearchBox)) return;

        var previous = KeyboardForeground.Take(this);
        if (previous != IntPtr.Zero) _previousForeground = previous;
        _typing = true;

        Log.Info("Realtime translation panel: search box pressed, foreground={Foreground:X}", GetForegroundWindow().ToInt64());

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
                // A row picks itself on Enter; the search box picks nothing — there is no highlighted
                // match until the user arrows onto one.
                e.Handled = true;
                if (Keyboard.FocusedElement is Button { Tag: not null } row) row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, row));
                break;

            case Key.Down:
                e.Handled = MoveFocus(+1);
                break;

            case Key.Up:
                e.Handled = MoveFocus(-1) || SearchBox.Focus();
                break;
        }
    }

    /// <summary>
    /// Steps the keyboard through the column it is in. From the search box it goes to the target
    /// column, the one most often searched — the list a source language is picked from is short.
    /// </summary>
    private bool MoveFocus(int step)
    {
        var focused = Keyboard.FocusedElement as Button;
        var rows = focused is null ? _targetRows
            : _providerRows.Contains(focused) ? _providerRows
            : _sourceRows.Contains(focused) ? _sourceRows
            : _targetRows;

        var visible = rows.Where(row => row.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return false;

        var index = focused is null ? -1 : visible.IndexOf(focused);
        var next = index < 0 ? (step > 0 ? 0 : -1) : index + step;
        if (next < 0 || next >= visible.Count) return false;

        visible[next].BringIntoView();
        return visible[next].Focus();
    }

    // Size, or progress, at the row's far end for as long as the language is not ready. Bound rather
    // than set: the download runs on whether or not anyone is looking.
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

    /// <summary>
    /// The language pack card, in the source column in place of its list: in this window, which is
    /// already out of the capture and in the session's re-assert, and already takes the mouse without
    /// the foreground. The other two columns stay as they were.
    /// </summary>
    private void ShowPackCard(RealtimeLanguageEntry entry)
    {
        if (entry.Language.Pack is { IsDownloading: false } idle) idle.ClearFailure();

        var card = new global::OverTranslate.Controls.OcrLanguagePackCard(entry.Language);
        card.DownloadRequested += (_, _) => PackDownloadRequested?.Invoke(this, entry.Code);
        card.Finished += (_, _) => HidePackCard();

        // Asking while it is already on its way means they want this language when it lands.
        if (entry.Language.Pack is { IsDownloading: true })
            PackDownloadRequested?.Invoke(this, entry.Code);

        if (double.IsNaN(_sourceListWidth)) _sourceListWidth = SourceColumn.Width;
        SourceColumn.Width = Math.Max(_sourceListWidth, card.Width + CardHost.Margin.Left + CardHost.Margin.Right + SourceColumn.Padding.Left + SourceColumn.Padding.Right);

        SourceScroll.Visibility = Visibility.Collapsed;
        SourceNoMatch.Visibility = Visibility.Collapsed;
        CardHost.Content = card;
        CardHost.Visibility = Visibility.Visible;

        Log.Info("Realtime translation panel: language pack card for {Code}", entry.Code);
    }

    private void HidePackCard()
    {
        if (_closing || CardHost.Visibility != Visibility.Visible) return;

        CardHost.Content = null;
        CardHost.Visibility = Visibility.Collapsed;
        SourceScroll.Visibility = Visibility.Visible;
        if (!double.IsNaN(_sourceListWidth)) SourceColumn.Width = _sourceListWidth;
        Filter(_sourceRows, SourceNoMatch, SourceScroll, SearchBox.Text);
    }

    private void PickProvider(RealtimeProviderEntry entry)
    {
        if (_closing || entry.Provider == _provider) return;

        Log.Info("Realtime translation panel: picked service {Provider}", entry.Provider);
        ProviderPicked?.Invoke(this, entry.Provider);
    }

    private void PickLanguage(RealtimeLanguageEntry entry, RealtimeLanguageKind kind)
    {
        if (_closing) return;

        if (entry.Language.Pack is { IsReady: false })
        {
            ShowPackCard(entry);
            return;
        }

        Log.Info("Realtime translation panel: picked {Kind} {Code}", kind, entry.Code);
        LanguagePicked?.Invoke(this, (kind, entry.Code));
    }

    /// <summary>Closes the panel. What was picked stays picked.</summary>
    /// <param name="giveBackForeground">
    /// Whether to hand the keyboard back to the window that had it before the search box took it —
    /// true when the user finished here, false when they clicked somewhere else and that is where it
    /// should go.
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
    /// window for an instant only. The panel stays — see the remarks for what puts it away — and a
    /// press on it no longer activates it.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_typing || _closing) return;

        _typing = false;
        TypingWindows.Remove(new WindowInteropHelper(this).Handle);
        WindowStyles.SetNoActivate(this, enabled: true);

        Log.Info("Realtime translation panel: search box lost the keyboard after {Ms}ms", _age.ElapsedMilliseconds);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Log.Info(
            "Realtime translation panel: closed after {Ms}ms, shown={Shown}, reason={Reason}",
            _age.ElapsedMilliseconds, Opacity >= 1, _closeReason);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
