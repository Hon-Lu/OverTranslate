using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// The box a 文字 mark is typed into, for as long as it is being typed (#362).
/// </summary>
/// <remarks>
/// <para>A window of its own rather than a box on the overlay, because typing needs the keyboard and
/// nothing else in a capture session may take it: the overlay, the toolbar and the 標記 panel are all
/// no-activate, so that showing them does not pull the foreground off the application underneath.
/// This one is the exception, for the length of one mark, and it is what makes the input method work
/// — 注音, kana and Hangul are composed by the IME of whichever window holds the keyboard.</para>
///
/// <para>Once the words are finished they stop being a text box. The overlay lays them into the ink
/// picture like any other mark — see OverlayWindow.CommitText — and from then on they are undone,
/// rubbed out and copied as one; they cannot be typed into again.</para>
///
/// <para>It ends on Esc, on a press anywhere but itself and the places that only adjust it, and on
/// the keyboard going to another application. Enter is a new line: a note on a screenshot is often
/// more than one, and Enter is also how an IME candidate is accepted.</para>
/// </remarks>
internal sealed class AnnotationTextEditor : Window
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>Room between the box's edge and the words, in DIP, so the frame does not touch the glyphs.</summary>
    private const double FramePad = 3;

    private readonly TextBox _box;
    private readonly Func<Point, bool> _keepOnPress;

    private IntPtr _previousForeground;
    private MouseDownHook? _pressWatch;
    private Point _anchor;
    private int _sizePx = Models.CaptureAnnotationSettings.DefaultTextSize;
    private bool _finished;

    /// <summary>Raised once, when the typing is over, before the window goes. Read <see cref="Content"/> then.</summary>
    public event EventHandler? Finished;

    /// <param name="keepOnPress">
    /// Whether a press at this physical point leaves the typing going: the 標記 panel, where the
    /// colour, size and style are changed, and the drawing surface, whose own press handler decides.
    /// </param>
    public AnnotationTextEditor(Func<Point, bool> keepOnPress)
    {
        _keepOnPress = keepOnPress;

        WindowStyle   = WindowStyle.None;
        ResizeMode    = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background    = Brushes.Transparent;
        Topmost       = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        // Off screen until it has been measured and put where the click was.
        Left = -32000;
        Top  = -32000;

        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab    = false,
            TextWrapping  = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            Padding       = new Thickness(0),
            Margin        = new Thickness(FramePad),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,

            // Not quite transparent: a layered window is hit-tested against its alpha, and a box
            // with nothing painted in it would let the click that is meant to put the caret
            // somewhere fall through to the overlay, which would end the typing instead.
            Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128)),
        };
        _box.PreviewKeyDown += Box_PreviewKeyDown;

        // Two frames, light under dark and dashed, so the box can be found on any screenshot.
        var light = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.9 };
        var dark  = new Rectangle
        {
            Stroke          = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
        };

        var root = new Grid();
        root.Children.Add(light);
        root.Children.Add(dark);
        root.Children.Add(_box);
        Content = root;
    }

    /// <summary>What has been typed.</summary>
    public string Text => _box.Text;

    /// <summary>Where the first line's top-left is, in physical screen pixels — where the mark goes.</summary>
    public Point TextOriginPhysical => _box.PointToScreen(TextOriginIn(_box));

    /// <summary>Opens the box with its first line's top-left on <paramref name="physical"/>, and takes the keyboard.</summary>
    public void ShowAt(Point physical)
    {
        _anchor = physical;
        Show();
        Place();

        _previousForeground = KeyboardForeground.Take(this);
        _box.Focus();
        Keyboard.Focus(_box);

        _pressWatch = MouseDownHook.Install(OnAnyPress);
    }

    /// <summary>Sets the words' look. Applied at once to what is already typed.</summary>
    /// <param name="sizePx">The em size in physical pixels, which is what the panel shows.</param>
    public void ApplyStyle(FontFamily family, int sizePx, Color color, bool bold, bool italic, bool underline, bool strikethrough)
    {
        _sizePx = sizePx;

        var brush = new SolidColorBrush(color);
        _box.FontFamily = family;
        _box.Foreground = brush;
        _box.CaretBrush = brush;
        _box.FontWeight = bold   ? FontWeights.Bold  : FontWeights.Normal;
        _box.FontStyle  = italic ? FontStyles.Italic : FontStyles.Normal;

        var decorations = new TextDecorationCollection();
        if (underline)     decorations.Add(TextDecorations.Underline);
        if (strikethrough) decorations.Add(TextDecorations.Strikethrough);
        _box.TextDecorations = decorations;

        ApplySize();
        if (IsVisible) Place();
    }

    /// <summary>
    /// The size in this window's DIP. The mark is measured in the capture's physical pixels, and this
    /// window is laid out at the DPI of the monitor it is on, so the two only agree once divided.
    /// </summary>
    private void ApplySize()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _box.FontSize = _sizePx / scale;

        // Wide enough for the caret and the frame round it to be seen before anything is typed.
        _box.MinWidth = _box.FontSize * 0.6;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplySize();

        // Shown first on whichever monitor WPF guessed, then moved onto the one clicked: what the
        // first line's offset is in pixels changes with the scale, so it is placed again.
        if (IsVisible) Place();
    }

    /// <summary>Moves the window so the first line starts exactly on the spot that was clicked.</summary>
    private void Place()
    {
        UpdateLayout();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var origin = _box.TranslatePoint(TextOriginIn(_box), this);
        ScreenGeometry.MoveToPhysical(this,
            (int)Math.Round(_anchor.X - origin.X * scale),
            (int)Math.Round(_anchor.Y - origin.Y * scale));
    }

    /// <summary>Where the box draws its first character, in its own coordinates.</summary>
    /// <remarks>
    /// Asked of the box rather than worked out from its margins: a text box draws its text a couple of
    /// DIP in from its edge, by an amount its template decides, and the mark has to land exactly
    /// where the typed words were.
    /// </remarks>
    private static Point TextOriginIn(TextBox box)
    {
        var rect = box.GetRectFromCharacterIndex(0);
        return rect.IsEmpty ? new Point(2, 0) : rect.TopLeft;
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a candidate is being composed the key arrives as ImeProcessed, so this is only the
        // Esc that is meant for the box: the IME cancels its own composition first.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish();
        }
    }

    private void OnAnyPress(Point physical)
    {
        if (_finished) return;

        var own = ScreenGeometry.PhysicalBounds(this);
        if (own.Contains((int)physical.X, (int)physical.Y)) return;
        if (_keepOnPress(physical)) return;

        Finish();
    }

    /// <summary>
    /// Ends the typing when the keyboard goes to another application. Not when it goes to the 標記
    /// panel, which takes it for a size typed in and hands it straight back.
    /// </summary>
    /// <remarks>
    /// Looked at once the switch has settled: at the moment of deactivation the new foreground can
    /// still be nobody.
    /// </remarks>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Log.Debug("文字 box deactivated; foreground is now {Window}", KeyboardForeground.Describe());
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_finished || IsActive) return;

            // The size field typing: it hands the keyboard back itself when it is done.
            if (TypingWindows.HaveForeground) return;

            if (!KeyboardForeground.IsOwnForeground())
            {
                Log.Info("文字 box finished: the keyboard went to another application");
                Finish();
                return;
            }

            Log.Info("文字 box lost the keyboard to {Window}; taking it back", KeyboardForeground.Describe());
            KeyboardForeground.Take(this);
            _box.Focus();
        });
    }

    /// <summary>Ends the typing. Safe to call more than once and from any of the ways it can end.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        Log.Debug("文字 box finished with {Length} characters", _box.Text.Length);

        _pressWatch?.Dispose();
        _pressWatch = null;

        Finished?.Invoke(this, EventArgs.Empty);

        KeyboardForeground.Release(this, _previousForeground, keepNoActivate: false);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pressWatch?.Dispose();
        _pressWatch = null;
        TypingWindows.Remove(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        base.OnClosed(e);
    }
}
