using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 翻譯區域預設, opened from the framing bar: every framing kept for the screen or window being
/// framed, the one this target opens on ticked, and one press puts another of them on the screen.
/// </summary>
/// <remarks>
/// <para>A window of its own beside the bar, built the way 顯示外觀 and 模式 are and for their
/// reasons: <c>ShowActivated="False"</c> and a no-activate ex-style, because pulling the foreground
/// off a full-screen game can minimise it, and every row is picked with the mouse so nothing here
/// needs the keyboard. It is owned by the bar while framing, so it stays above the layer that covers
/// the whole screen.</para>
///
/// <para>It closes on a pick, on its ✕, and when the bar puts it away — pressing the bar's button
/// again, opening 模式 or 顯示外觀, or leaving framing. It is not closed by a press outside it: like
/// the other panels over a session it stays until the user is done with it, and closing on a stray
/// press would be the surprising direction.</para>
/// </remarks>
internal partial class RealtimeFramingPanel : Window
{
    // Between the bar's edge and the panel's.
    private const double Gap = 6;

    // The panel's width: one line of a window's title and its block count, which is what a row says.
    private const double PanelWidth = 288;

    // The list's own limits, in DIP: enough for a few rows, never taller than the room beside the
    // bar allows — see Place.
    private const double MinListHeight = 96;
    private const double MaxListHeight = 320;

    // What the chrome around the list takes: the heading, the frame and the padding. The shadow's
    // margin is read off RootChrome itself.
    private const double Header = 38;
    private const double Frame = 2;

    private System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private double _windowScale = 1.0;
    private bool _opensUp = true;
    private bool _placed;
    private bool _closing;

    /// <param name="bar">The framing bar's visible surface, in physical pixels: the panel opens clear of it.</param>
    /// <param name="screen">The session's screen, which the panel has to stay on.</param>
    /// <param name="presets">The framings kept for this target, newest first.</param>
    public RealtimeFramingPanel(
        System.Drawing.Rectangle bar,
        System.Drawing.Rectangle screen,
        IReadOnlyList<RealtimeFramingPreset> presets)
    {
        InitializeComponent();

        _bar = bar;
        _screen = screen;

        Chrome.Width = PanelWidth;
        Build(presets);

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>Raised with the id of the framing a row was pressed on. The list closes with the pick.</summary>
    public event EventHandler<string>? Picked;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show could hand it the foreground: see the remarks.
        WindowStyles.ApplyNoActivate(this);

        // Opened at the bar, so the window starts on the monitor it will be placed on and the scale
        // read in Place is that monitor's.
        ScreenGeometry.MoveToPhysical(this, _bar.Left, _bar.Top);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>The bar moved — dragged, or its width changed — and the panel goes with it.</summary>
    public void UpdateAnchor(System.Drawing.Rectangle bar)
    {
        if (bar == _bar || _closing) return;
        _bar = bar;
        if (IsLoaded) Place();
    }

    /// <summary>Closes the panel; the bar hears about it and lets go of its button.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Build(IReadOnlyList<RealtimeFramingPreset> presets)
    {
        RowsHost.Children.Clear();
        foreach (var preset in presets) RowsHost.Children.Add(Row(preset));

        bool any = presets.Count > 0;
        RowsScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// One framing: its name over how many blocks and which application, with a tick on the one this
    /// target opens on. The whole row is the press.
    /// </summary>
    private Button Row(RealtimeFramingPreset preset)
    {
        var name = new TextBlock
        {
            Text = RealtimeFramingDisplay.Name(preset),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var detail = new TextBlock
        {
            Text = RealtimeFramingDisplay.Detail(preset),
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var text = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(detail);

        var check = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
            Visibility = preset.Selected ? Visibility.Visible : Visibility.Collapsed,
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AppAccent");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var row = new Button
        {
            Content = grid,
            ToolTip = RealtimeFramingDisplay.Name(preset),
        };
        row.SetResourceReference(StyleProperty, "FramingMenuRow");
        AutomationProperties.SetName(row, RealtimeFramingDisplay.Name(preset));

        string id = preset.Id;
        row.Click += (_, _) => Picked?.Invoke(this, id);
        return row;
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the panel against the bar — above it, or below when there is more room there — and shows
    /// it once it is there. The list is capped to the room on that side, and never made taller than
    /// <see cref="MaxListHeight"/>.
    /// </summary>
    private void Place()
    {
        if (_closing) return;

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;

        // The bar's own correction — see RealtimeControlWindow.ApplyScreenScale — so the panel comes
        // out the same physical size as the bar it opens from.
        double screenScale = ScreenGeometry.ScaleAt(_bar.Left, _bar.Top);
        double relative = screenScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);

        int Physical(double dip) => (int)Math.Round(dip * screenScale);
        double Dip(int physical) => physical / screenScale;

        double above = Dip(_bar.Top - _screen.Top);
        double below = Dip(_screen.Bottom - _bar.Bottom);

        // Decided once, on the first placement: a panel that flipped sides as the list changed would
        // move out from under the pointer that opened it.
        if (!_placed) _opensUp = above >= below;

        var margin = RootChrome.Margin;
        double room = (_opensUp ? above : below) - Gap - Header - Frame - margin.Bottom;
        RowsScroll.MaxHeight = Math.Clamp(room, MinListHeight, MaxListHeight);

        RootChrome.VerticalAlignment = _opensUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        UpdateLayout();

        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(Chrome.ActualHeight);
        int gap = Physical(Gap);

        int chromeTop = _opensUp
            ? Math.Max(_screen.Top, _bar.Top - gap - chromeHeight)
            : Math.Min(_bar.Bottom + gap, Math.Max(_screen.Top, _screen.Bottom - chromeHeight));
        int chromeLeft = Math.Clamp(
            _bar.Left, _screen.Left, Math.Max(_screen.Left, _screen.Right - chromeWidth));

        // Cast away from the bar.
        Shadow.Direction = _opensUp ? 90 : 270;

        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));
        _placed = true;

        Opacity = 1;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Dismiss();
}
