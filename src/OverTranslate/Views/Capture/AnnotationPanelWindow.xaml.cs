using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The tools behind 標記, in a panel hung under the button that opened it.
/// </summary>
/// <remarks>
/// A second bar rather than more buttons on the first one. What is on the capture toolbar is the
/// shape of one capture — the languages, the engine, the six things to do with the result — and it is
/// on screen for the whole session. These seven controls only mean anything while a tool is in hand,
/// and putting them on the bar would make everyone who never draws pay for them on every capture.
/// </remarks>
public partial class AnnotationPanelWindow : Window
{
    /// <summary>
    /// The colours 標記 offers, in the order they are shown.
    /// </summary>
    /// <remarks>
    /// A fixed set, not a picker. Someone marking up a screenshot mid-task wants a colour that shows
    /// against what is underneath, and that decision is between about eight answers — a full picker
    /// would be a second window, a mode, and a decision to make before the first stroke.
    ///
    /// White and black are the two ends, and both are here because the ground is somebody else's
    /// screen: a mark on a dark game and a mark on a white document cannot be the same colour.
    /// </remarks>
    private static readonly Color[] PaletteColors =
    [
        Color.FromRgb(0x00, 0x00, 0x00), // 黑
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白
        Color.FromRgb(0xFA, 0xCC, 0x15), // 黃
        Color.FromRgb(0x22, 0xC5, 0x5E), // 綠
        Color.FromRgb(0x38, 0xBD, 0xF8), // 藍
        Color.FromRgb(0xEF, 0x44, 0x44), // 紅
        Color.FromRgb(0xF9, 0x73, 0x16), // 橘
        Color.FromRgb(0x7C, 0x3A, 0xED), // 紫
    ];

    /// <summary>What every capture starts with in hand.</summary>
    /// <remarks>
    /// Not remembered, unlike the rest of the panel — see CaptureSettings.Annotation for why.
    /// </remarks>
    public static AnnotationTool DefaultTool => AnnotationTool.Pen;

    /// <summary>
    /// What the 透明度 slider means, as (faintest, strongest).
    /// </summary>
    /// <remarks>
    /// Neither end is allowed to be useless. At 0 the highlight would not exist, and at 1 it would
    /// cover the words it was drawn to pick out — so the range stops short of both, and every
    /// position on the slider is one somebody might actually want.
    /// </remarks>
    private const double MinOpacity = 0.15;
    private const double MaxOpacity = 0.75;

    /// <summary>
    /// What the one slider means for each tool, as (thinnest, thickest).
    /// </summary>
    /// <remarks>
    /// Three ranges rather than one, because the three tools are not the same kind of mark. A pen at
    /// 30 is a blob; a highlighter at 3 does not cover a line of text; an eraser has no width at all,
    /// only a reach. Mapping one slider position onto each tool's own range is what lets the control
    /// stay a single "粗細" the whole time.
    /// </remarks>
    private static (double Min, double Max) RangeFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlighter => (9, 31),

        // A diameter, where the other two are stroke widths — it is the size of the circle the user
        // can see under the pointer, and a radius would be half of the thing they are looking at.
        AnnotationTool.Eraser      => (14, 64),

        _                          => (2, 13),
    };

    private readonly List<ToggleButton> _swatches = [];
    private bool _initializing = true;

    /// <summary>
    /// The preferences this panel shows and edits, in place — the caller saves them when it is done.
    /// </summary>
    private readonly CaptureAnnotationSettings _prefs;

    /// <summary>Raised whenever the tool, the colour or the width changed.</summary>
    public event EventHandler? SettingsChanged;

    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;

    public AnnotationTool Tool { get; private set; }
    public Color InkColor { get; private set; }

    /// <summary>How see-through a highlight drawn now would be.</summary>
    /// <remarks>
    /// Named for the ink and not just "Opacity" because a Window already has one of those, and it
    /// means the transparency of this panel. Two properties one letter apart, on the same object,
    /// one of which would make the toolbar itself fade — see also <see cref="InkColor"/>.
    /// </remarks>
    public double InkOpacity => MinOpacity + (MaxOpacity - MinOpacity) * Math.Clamp(_prefs.HighlighterOpacity, 0, 1);

    /// <summary>
    /// Where the slider sits for the tool in hand, 0 to 1. Each tool keeps its own, so widening the
    /// highlighter does not leave the pen wide as well.
    /// </summary>
    private double ThicknessFraction
    {
        get => Math.Clamp(Tool switch
        {
            AnnotationTool.Highlighter => _prefs.HighlighterThickness,
            AnnotationTool.Eraser      => _prefs.EraserSize,
            AnnotationTool.Pen         => _prefs.PenThickness,
            _                          => _prefs.ShapeThickness,
        }, 0, 1);
        set
        {
            switch (Tool)
            {
                case AnnotationTool.Highlighter: _prefs.HighlighterThickness = value; break;
                case AnnotationTool.Eraser:      _prefs.EraserSize           = value; break;
                case AnnotationTool.Pen:         _prefs.PenThickness         = value; break;
                default:                         _prefs.ShapeThickness       = value; break;
            }
        }
    }

    public double Thickness
    {
        get
        {
            var (min, max) = RangeFor(Tool);
            return min + (max - min) * Math.Pow(ThicknessFraction, CurveFor(Tool));
        }
    }

    /// <summary>
    /// How much the 粗細 slider bends towards the thin end, given as where its middle lands along the
    /// tool's range.
    /// </summary>
    /// <remarks>
    /// A curve rather than a narrower range, so both ends still reach as far as they did — the
    /// thickest pen is still there for whoever wants it — while the default, which sits in the
    /// middle, is the width most marks are actually made at: 6 for the pen and the shapes, a little
    /// under the straight halfway for the highlighter.
    ///
    /// Not for the eraser: its slider is a reach rather than the width of a mark, and a wide rub in
    /// the middle is what it is for.
    /// </remarks>
    private static double CurveFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Eraser      => 1,
        AnnotationTool.Highlighter => MiddleAt(0.4),
        _                          => MiddleAt((6.0 - 2) / (13 - 2)),
    };

    /// <summary>The exponent that puts the slider's middle at <paramref name="share"/> of the range.</summary>
    private static double MiddleAt(double share) => Math.Log(share) / Math.Log(0.5);

    public AnnotationPanelWindow(AnnotationTool tool, CaptureAnnotationSettings prefs)
    {
        InitializeComponent();

        _prefs = prefs;
        Tool   = tool;

        // A colour the palette does not offer — hand-edited, or from a palette since changed — is
        // put back on the first swatch by BuildPalette rather than drawn with.
        InkColor = TryParseColor(prefs.Color) ?? PaletteColors[0];
        if (AnnotationStroke.IsShapeTool(prefs.Shape)) _lastShape = prefs.Shape;

        BuildPalette();
        RenderToolSelection();
        OpacitySlider.Value = Math.Clamp(prefs.HighlighterOpacity, 0, 1);
        _initializing = false;
    }

    private static Color? TryParseColor(string? text)
    {
        try { return text is null ? null : (Color)System.Windows.Media.ColorConverter.ConvertFromString(text); }
        catch (Exception e) when (e is FormatException or NotSupportedException) { return null; }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Never takes focus. The capture toolbar above it does not either, and a panel that stole
        // activation from the application being captured would be the one piece of this session that
        // interrupted what the user was doing.
        WindowStyles.ApplyNoActivate(this);
    }

    /// <summary>Reflects whether there is anything left to undo or redo.</summary>
    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        UndoBtn.IsEnabled = canUndo;
        RedoBtn.IsEnabled = canRedo;
    }

    /// <summary>The gap left between the capture toolbar and this panel, in DIP.</summary>
    /// <remarks>
    /// Small on purpose. The two bars are one control opened in two pieces, and the distance between
    /// them is what says so: far enough apart to read as two surfaces rather than one tall one,
    /// close enough that the eye does not have to decide whether they belong together.
    /// </remarks>
    private const double GapFromToolbar = 6;

    /// <summary>
    /// Puts the panel under the capture toolbar, centred on it — or above it where there is no room.
    /// </summary>
    /// <remarks>
    /// <para>Run once, when the panel opens, and never again while it is up. Switching to 螢光筆
    /// makes the panel wider; left alone the window keeps its left edge and sizes to its content,
    /// so it grows rightwards, away from the tool buttons the user is pressing. Centring it again
    /// on every change would drag that row out from under the pointer.</para>
    ///
    /// <para>Measured against the toolbar's visible surface and this panel's own, not against either
    /// window's edges: both windows are larger than the bars they show, because each leaves a margin
    /// for its shadow to fade out in. Placing window to window would leave a gap of both margins
    /// added together, which is most of a centimetre of nothing.</para>
    ///
    /// <para>Below first because that is where a thing opened from a bar is looked for, and the
    /// toolbar itself has already been placed with the same preference. Above is the fallback, not a
    /// second option: it is used only when the panel would otherwise hang off the bottom of the
    /// monitor.</para>
    /// </remarks>
    public void PlaceNear(Rect toolbarVisiblePhys, double scale)
    {
        // Which side the tray opens on depends on where this lands, so it is put away first.
        HideShapeTray();
        UpdateLayout();

        // Where the visible panel starts inside its own window, and how big it is, in pixels.
        var inset = PanelSurface.TranslatePoint(new Point(0, 0), this);
        double visW = PanelSurface.ActualWidth  * scale;
        double visH = PanelSurface.ActualHeight * scale;
        double gap  = GapFromToolbar * scale;

        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(toolbarVisiblePhys.Left + toolbarVisiblePhys.Width / 2),
            (int)(toolbarVisiblePhys.Top  + toolbarVisiblePhys.Height / 2))).WorkingArea;

        double margin  = 4 * scale;
        double minLeft = wa.Left + margin;
        double maxLeft = Math.Max(minLeft, wa.Right - visW - margin);
        double visLeft = Math.Clamp(
            toolbarVisiblePhys.Left + (toolbarVisiblePhys.Width - visW) / 2, minLeft, maxLeft);

        double visTop = toolbarVisiblePhys.Bottom + gap;
        _placedAbove = visTop + visH > wa.Bottom;
        if (_placedAbove) visTop = toolbarVisiblePhys.Top - gap - visH;

        // On a monitor with room for neither, the panel goes wherever it fits rather than off the
        // top. It is the only way to change tools, so it can never be the thing that ends up out of
        // reach — losing the gap to the toolbar is the cheaper failure.
        visTop = Math.Clamp(visTop, wa.Top + margin, Math.Max(wa.Top + margin, wa.Bottom - visH - margin));

        ScreenGeometry.MoveToPhysical(this,
            (int)Math.Round(visLeft - inset.X * scale),
            (int)Math.Round(visTop  - inset.Y * scale));
    }

    private void BuildPalette()
    {
        foreach (var color in PaletteColors)
        {
            var swatch = new ToggleButton
            {
                Style      = (Style)FindResource("AnnotationSwatchButton"),
                Background = new SolidColorBrush(color),
                IsChecked  = color == InkColor,
                Tag        = color,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, color.ToString());
            swatch.Click += Swatch_Click;
            _swatches.Add(swatch);
            Palette.Children.Add(swatch);
        }

        // A palette showing eight unchecked swatches is a state the user cannot get out of by
        // looking at it. A saved colour reaches here when it is not one of these eight — a hand-
        // edited settings file, or a palette that has since changed — and the ring is the only thing
        // saying what is in hand, so it has to be on something.
        if (!_swatches.Any(s => s.IsChecked == true))
        {
            InkColor = PaletteColors[0];
            _swatches[0].IsChecked = true;
        }

        _prefs.Color = ToHex(InkColor);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not Color color) return;

        // Clicking the one already chosen must leave it chosen: a ToggleButton unchecks itself on the
        // second press, and a palette with nothing selected is not a state this control has.
        InkColor = color;
        _prefs.Color = ToHex(color);
        foreach (var swatch in _swatches) swatch.IsChecked = ReferenceEquals(swatch, clicked);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToolBtn_Click(object sender, RoutedEventArgs e)
    {
        Tool = sender switch
        {
            _ when ReferenceEquals(sender, HighlighterTool) => AnnotationTool.Highlighter,
            _ when ReferenceEquals(sender, EraserTool)      => AnnotationTool.Eraser,
            _                                               => AnnotationTool.Pen,
        };

        HideShapeTray();
        RenderToolSelection();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 形狀 picks up the shape last used and opens the tray; pressed again, it only opens or closes it.
    /// </summary>
    /// <remarks>
    /// The first press does both because the common case is one shape drawn again and again: that is
    /// a single press, and the tray beside it is how the other two are found the first time.
    /// </remarks>
    private void ShapeTool_Click(object sender, RoutedEventArgs e)
    {
        if (!AnnotationStroke.IsShapeTool(Tool))
        {
            Tool = _lastShape;
            RenderToolSelection();
            ShowShapeTray();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Already in hand: a ToggleButton would uncheck itself here, and a tool that is still in
        // use must not look put down.
        ShapeTool.IsChecked = true;
        if (ShapeTray.Visibility == Visibility.Visible) HideShapeTray();
        else ShowShapeTray();
    }

    private void ShapeChoice_Click(object sender, RoutedEventArgs e)
    {
        Tool = sender switch
        {
            _ when ReferenceEquals(sender, LineTool)    => AnnotationTool.Line,
            _ when ReferenceEquals(sender, EllipseTool) => AnnotationTool.Ellipse,
            _                                           => AnnotationTool.Rectangle,
        };

        HideShapeTray();
        RenderToolSelection();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shape 形狀 gives when pressed, and the one its button shows while it is not in hand.</summary>
    private AnnotationTool _lastShape = AnnotationTool.Rectangle;

    /// <summary>Whether this panel was put above the capture toolbar rather than under it.</summary>
    private bool _placedAbove;

    /// <summary>The gap between the bar and the tray, in DIP — the same one left above the bar.</summary>
    private const double ShapeTrayGap = GapFromToolbar;

    /// <summary>Opens the tray under the 形狀 button, or above it when below is not an option.</summary>
    /// <remarks>
    /// Away from the capture toolbar where it can be: hung on the toolbar's side it lands in the gap
    /// between the two bars and over the toolbar itself. Off the bottom of the monitor is worse than
    /// that, though — a choice the user cannot reach — so a panel with no room under it opens upward
    /// anyway.
    /// </remarks>
    private void ShowShapeTray()
    {
        if (ShapeTray.Visibility == Visibility.Visible) return;

        // Laid out below first: that is where it goes unless it will not fit, and the room it needs
        // is only known once it has been measured.
        PlaceTray(upward: false);
        ShapeTray.Visibility = Visibility.Visible;
        UpdateLayout();

        // Centred under the button, but never past either end of the bar.
        double buttonCentre = ShapeTool.TranslatePoint(new Point(ShapeTool.ActualWidth / 2, 0), PanelBar).X;
        double left = Math.Clamp(
            buttonCentre - ShapeTray.ActualWidth / 2, 0, Math.Max(0, PanelBar.ActualWidth - ShapeTray.ActualWidth));
        ShapeTray.Margin = new Thickness(left, ShapeTray.Margin.Top, 0, ShapeTray.Margin.Bottom);

        // Above instead when the panel is above the toolbar — below would land on the toolbar — or
        // when below would run off the bottom of the monitor.
        if (_placedAbove || RunsOffScreenBottom(ShapeTraySurface))
        {
            PlaceTray(upward: true);
            ShapeTray.Margin = new Thickness(left, 0, 0, ShapeTrayGap);

            // The window sizes to its content and grows from its top edge, so it is moved up by what
            // the tray added: the bar stays where the user is looking.
            _trayShift = ShapeTray.ActualHeight + ShapeTrayGap;
            Top -= _trayShift;
        }

        _trayCloser ??= MouseDownHook.Install(OnAnyMouseDown);
    }

    private void PlaceTray(bool upward)
    {
        PanelStack.Children.Remove(ShapeTray);
        PanelStack.Children.Insert(upward ? 0 : 1, ShapeTray);
        ShapeTray.Margin = upward ? new Thickness(0, 0, 0, ShapeTrayGap) : new Thickness(0, ShapeTrayGap, 0, 0);
    }

    private static bool RunsOffScreenBottom(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null) return false;
        var bottom = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight));
        var wa = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)bottom.X, (int)bottom.Y - 1)).WorkingArea;
        return bottom.Y > wa.Bottom;
    }

    /// <summary>How far the window was moved up to open the tray above the bar, to move it back by.</summary>
    private double _trayShift;

    private void HideShapeTray()
    {
        if (ShapeTray.Visibility == Visibility.Visible)
        {
            ShapeTray.Visibility = Visibility.Collapsed;
            Top += _trayShift;
            _trayShift = 0;
        }

        _trayCloser?.Dispose();
        _trayCloser = null;
    }

    /// <summary>Watches for the press that puts the tray away, only while it is open.</summary>
    private MouseDownHook? _trayCloser;

    /// <summary>
    /// Any button pressed anywhere closes the tray — except on the tray itself or on 形狀, whose own
    /// clicks decide what happens to it.
    /// </summary>
    /// <remarks>
    /// The tray is a choice made on the way to drawing, so the first press that is not that choice
    /// is the user having moved on: starting a mark, reaching for the palette, or clicking in some
    /// other window altogether. Left open it would sit there over nothing until something closed it.
    /// </remarks>
    private void OnAnyMouseDown(Point screenPoint)
    {
        if (ShapeTray.Visibility != Visibility.Visible) return;
        if (ContainsScreenPoint(ShapeTraySurface, screenPoint) || ContainsScreenPoint(ShapeTool, screenPoint)) return;
        HideShapeTray();
    }

    private static bool ContainsScreenPoint(FrameworkElement element, Point screenPoint)
    {
        if (PresentationSource.FromVisual(element) is null) return false;
        var topLeft     = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight).Contains(screenPoint);
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayCloser?.Dispose();
        _trayCloser = null;
        base.OnClosed(e);
    }

    private void RenderToolSelection()
    {
        bool shape = AnnotationStroke.IsShapeTool(Tool);
        if (shape) _lastShape = _prefs.Shape = Tool;

        PenTool.IsChecked         = Tool == AnnotationTool.Pen;
        HighlighterTool.IsChecked = Tool == AnnotationTool.Highlighter;
        EraserTool.IsChecked      = Tool == AnnotationTool.Eraser;
        ShapeTool.IsChecked       = shape;
        LineTool.IsChecked        = Tool == AnnotationTool.Line;
        RectangleTool.IsChecked   = Tool == AnnotationTool.Rectangle;
        EllipseTool.IsChecked     = Tool == AnnotationTool.Ellipse;

        ShapeIconLine.Visibility      = _lastShape == AnnotationTool.Line      ? Visibility.Visible : Visibility.Collapsed;
        ShapeIconRectangle.Visibility = _lastShape == AnnotationTool.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        ShapeIconEllipse.Visibility   = _lastShape == AnnotationTool.Ellipse   ? Visibility.Visible : Visibility.Collapsed;
        ShapeLabel.Text = LocalizationService.Get(_lastShape switch
        {
            AnnotationTool.Line    => "S.Annotate.Line",
            AnnotationTool.Ellipse => "S.Annotate.Ellipse",
            _                      => "S.Annotate.Rectangle",
        });

        // The eraser takes a colour from nothing and gives one to nothing. Left enabled it would be
        // eight buttons that quietly do not apply to what is in hand.
        Palette.IsEnabled = Tool != AnnotationTool.Eraser;
        Palette.Opacity   = Tool == AnnotationTool.Eraser ? 0.35 : 1.0;

        // Taken away rather than greyed out, unlike the palette. A dimmed control says "not right
        // now", which is true of the swatches — put the pen back in hand and they apply again to the
        // very same marks. 透明度 is not that: for a pen there is no faded answer to be had, so the
        // control is not unavailable, it is inapplicable, and the panel is simply shorter without it.
        var opacityVisibility = Tool == AnnotationTool.Highlighter
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpacityBlock.Visibility   = opacityVisibility;
        OpacityDivider.Visibility = opacityVisibility;

        // 粗細 is how wide a mark is. The eraser makes no mark: what its slider sets is how much of
        // the picture the circle covers, and calling that 粗細 would be asking the user to translate
        // it every time they pick the tool up.
        bool erasing = Tool == AnnotationTool.Eraser;
        ThicknessTitle.Text    = LocalizationService.Get(erasing ? "S.Annotate.Size"  : "S.Annotate.Thickness");
        ThicknessMinLabel.Text = LocalizationService.Get(erasing ? "S.Annotate.Small" : "S.Annotate.Thin");
        ThicknessMaxLabel.Text = LocalizationService.Get(erasing ? "S.Annotate.Large" : "S.Annotate.Thick");
        System.Windows.Automation.AutomationProperties.SetName(ThicknessSlider, ThicknessTitle.Text);

        // Each tool's own width, so the slider jumps to it when the tool changes.
        _showingToolThickness = true;
        ThicknessSlider.Value = ThicknessFraction;
        _showingToolThickness = false;
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _showingToolThickness) return;
        ThicknessFraction = e.NewValue;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Set while the slider is being moved to the new tool's width, which is not the user dragging it.</summary>
    private bool _showingToolThickness;

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _prefs.HighlighterOpacity = e.NewValue;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where a slider goes back to when its knob is double-clicked: the default, halfway.</summary>
    private const double DefaultSliderPosition = 0.5;

    /// <summary>
    /// A double-click on a slider's knob puts it back to the default.
    /// </summary>
    /// <remarks>
    /// The knob only, not the track: a double-click on the track is two clicks aimed at a position,
    /// and throwing that away for the middle would be the opposite of what was pointed at.
    /// </remarks>
    private void Slider_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

        for (DependencyObject? node = e.OriginalSource as Visual; node is not null && !ReferenceEquals(node, slider);
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is Thumb)
            {
                slider.Value = DefaultSliderPosition;
                e.Handled = true;
                return;
            }
        }
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
        => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void RedoBtn_Click(object sender, RoutedEventArgs e)
        => RedoRequested?.Invoke(this, EventArgs.Empty);

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
