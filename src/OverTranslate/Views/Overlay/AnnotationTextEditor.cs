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
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using OverTranslate.Models;
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
        System.Windows.Automation.AutomationProperties.SetAutomationId(_box, "AnnotationTextBox");
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.TextChanged += (_, _) => _colorView.InvalidateVisual();

        // What the input method is still composing, which the box would underline in its own text
        // colour — and that colour is now see-through, so the view draws the underline instead.
        TextCompositionManager.AddPreviewTextInputStartHandler(_box, OnComposing);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_box, OnComposing);
        TextCompositionManager.AddPreviewTextInputHandler(_box, (_, _) => _colorView.Composing = null);

        // The box's own glyphs are not shown: WPF draws an emoji as a one-colour outline, so the
        // words are drawn over it by the same code that lays the finished mark down (#373). The box
        // still places the caret and the selection, and the two agree because both lay the words
        // out the same way.
        _box.Foreground = Brushes.Transparent;
        _colorView = new ColorTextView(_box) { Margin = _box.Margin };

        // Two frames, light under dark and dashed, so the box can be found on any screenshot.
        var light = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.9 };
        var dark  = new Rectangle
        {
            Stroke          = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
        };

        var framed = new Grid();
        framed.Children.Add(light);
        framed.Children.Add(dark);
        framed.Children.Add(_box);
        framed.Children.Add(_colorView);

        // The handle the box is moved by (#373): a band round the frame, and the room between the
        // frame and the words. Painted, not left empty, for the same reason as the box's background.
        var handle = new Border
        {
            Padding    = new Thickness(HandleOutside),
            Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128)),
            Cursor     = Cursors.SizeAll,
            Child      = framed,
        };
        handle.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
        handle.MouseMove           += Handle_MouseMove;
        handle.MouseLeftButtonUp   += Handle_MouseLeftButtonUp;
        handle.LostMouseCapture    += (_, _) => _dragFrom = null;
        _box.Cursor = Cursors.IBeam;

        Content = handle;
    }

    /// <summary>How far outside the frame the handle reaches, in DIP.</summary>
    private const double HandleOutside = 5;

    private readonly ColorTextView _colorView;

    /// <summary>Where a drag of the handle started — the pointer and the anchor, physical pixels — while one is going on.</summary>
    private (Point Pointer, Point Anchor)? _dragFrom;

    /// <summary>
    /// Where the first line may be dragged to, in physical pixels: the selection, so the words are
    /// never moved out of the picture they are going into.
    /// </summary>
    public Rect AnchorBounds { get; set; } = Rect.Empty;

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The words themselves place the caret as before; only the band round them moves the box.
        if (IsInside(_box, e.OriginalSource)) return;

        var handle = (UIElement)sender;
        _dragFrom = (handle.PointToScreen(e.GetPosition(handle)), _anchor);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;

        // In screen pixels, because the window moves under the pointer and anything measured
        // against the window would move with it.
        var handle = (UIElement)sender;
        var now = handle.PointToScreen(e.GetPosition(handle));
        var anchor = new Point(from.Anchor.X + now.X - from.Pointer.X, from.Anchor.Y + now.Y - from.Pointer.Y);
        if (!AnchorBounds.IsEmpty)
        {
            // The words kept whole inside the selection — clipped at its edge they would simply
            // vanish from the picture. Words bigger than the selection keep their first line in it.
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var origin = TextOriginIn(_box);
            double wordsW = Math.Max(0, _box.ActualWidth  - origin.X) * scale;
            double wordsH = Math.Max(0, _box.ActualHeight - origin.Y) * scale;
            anchor = new Point(
                Math.Clamp(anchor.X, AnchorBounds.Left, Math.Max(AnchorBounds.Left, AnchorBounds.Right  - wordsW)),
                Math.Clamp(anchor.Y, AnchorBounds.Top,  Math.Max(AnchorBounds.Top,  AnchorBounds.Bottom - wordsH)));
        }

        if (anchor == _anchor) return;
        _anchor = anchor;
        Place();
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        ((UIElement)sender).ReleaseMouseCapture();

        // Still typing: the words were moved, not finished.
        _box.Focus();
        e.Handled = true;
    }

    private void OnComposing(object sender, TextCompositionEventArgs e)
    {
        // The length is taken from the composed text, not CompositionLength: measured with 注音, that
        // reports the length before the update — 0 for ㄨ, 1 for ㄨㄛ, 2 for 我. The offset is right.
        _colorView.Composing =
            e.TextComposition is System.Windows.Documents.FrameworkTextComposition { CompositionText.Length: > 0 } c
                ? (c.CompositionOffset, c.CompositionText.Length)
                : null;
    }

    private static bool IsInside(DependencyObject ancestor, object source)
    {
        for (var node = source as DependencyObject; node is not null; node = Parent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }

    /// <summary>
    /// What has been typed, drawn the way the mark will be — emoji in colour — over the box that
    /// holds the caret.
    /// </summary>
    private sealed class ColorTextView : FrameworkElement
    {
        private readonly TextBox box;

        public ColorTextView(TextBox box)
        {
            this.box = box;
            IsHitTestVisible = false;
        }

        public Color Color { get; set; }
        public AnnotationTextStyle? Style { get; set; }

        /// <summary>The characters the input method is still composing, as (offset, length) into the box's text.</summary>
        public (int Offset, int Length)? Composing
        {
            get => _composing;
            set
            {
                if (_composing == value) return;
                _composing = value;
                InvalidateVisual();
            }
        }

        private (int Offset, int Length)? _composing;

        protected override void OnRender(DrawingContext dc)
        {
            if (Style is not { } style || box.Text.Length == 0) return;

            var text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var laid = style.Layout(text, Color, ppd);
            var origin = TextOriginIn(box);
            ColorGlyphText.Draw(dc, laid, origin);

            DrawCompositionUnderline(dc, laid, origin, style.Size);
        }

        /// <summary>A dotted line under what is still being composed, a line at a time.</summary>
        /// <remarks>
        /// Placed from the same layout the words were just drawn from, not asked of the box: while a
        /// composition is changing, the box's own layout is not up to date when this is drawn, and
        /// it answers with nothing.
        /// </remarks>
        private void DrawCompositionUnderline(DrawingContext dc, FormattedText laid, Point origin, double emSize)
        {
            if (_composing is not { } c) return;

            // Offsets into the box's text, which keeps "\r\n" where the layout has "\n".
            int start = ToLaid(c.Offset);
            int end   = ToLaid(c.Offset + c.Length);
            if (end <= start) return;

            double thickness = Math.Max(1, emSize / 16);
            var pen = new Pen(new SolidColorBrush(Color), thickness) { DashStyle = new DashStyle([2, 1.5], 0) };
            pen.Freeze();

            Rect? run = null;
            for (int i = start; i < end; i++)
            {
                if (laid.BuildHighlightGeometry(origin, i, 1) is not { } g || g.Bounds.IsEmpty) continue;
                var piece = g.Bounds;
                if (run is { } r && Math.Abs(r.Top - piece.Top) < 0.5) run = Rect.Union(r, piece);
                else
                {
                    if (run is { } done) Underline(done);
                    run = piece;
                }
            }
            if (run is { } last) Underline(last);

            void Underline(Rect line)
            {
                double y = line.Bottom - thickness * 1.5;
                dc.DrawLine(pen, new Point(line.Left, y), new Point(line.Right, y));
            }

            int ToLaid(int offset)
            {
                var raw = box.Text;
                offset = Math.Clamp(offset, 0, raw.Length);
                int crlf = 0;
                for (int i = 0; i + 1 < offset; i++)
                    if (raw[i] == '\r' && raw[i + 1] == '\n') crlf++;
                return offset - crlf;
            }
        }

        protected override Size MeasureOverride(Size availableSize) => default;
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
        _box.CaretBrush = brush;
        _box.FontWeight = bold   ? FontWeights.Bold  : FontWeights.Normal;
        _box.FontStyle  = italic ? FontStyles.Italic : FontStyles.Normal;

        var decorations = new TextDecorationCollection();
        if (underline)     decorations.Add(TextDecorations.Underline);
        if (strikethrough) decorations.Add(TextDecorations.Strikethrough);
        _box.TextDecorations = decorations;

        _style = (family, bold, italic, underline, strikethrough);
        _colorView.Color = color;

        ApplySize();
        if (IsVisible) Place();
    }

    private (FontFamily Family, bool Bold, bool Italic, bool Underline, bool Strikethrough) _style =
        (new FontFamily("Segoe UI"), false, false, false, false);

    /// <summary>Hands the drawn words the box's current look, in the box's own DIP.</summary>
    private void SyncColorView()
    {
        _colorView.Style = new AnnotationTextStyle(
            _style.Family, _box.FontSize, _style.Bold, _style.Italic, _style.Underline, _style.Strikethrough);
        _colorView.InvalidateVisual();
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
        SyncColorView();
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
        if (IsSystemTextInput(physical)) return;

        Finish();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>
    /// Whether the press was on Windows' own text input — the emoji panel (Win+.) or the touch
    /// keyboard (#373). Those type into this box; a click on an emoji in them is choosing what to
    /// type, not moving on from the words.
    /// </summary>
    /// <remarks>
    /// Told by the process, not the window class, which has changed between Windows releases. Both
    /// 10 and 11 draw the panel in a TextInputHost window — older builds of 10 in an InputApp — but
    /// hang it inside a frame that belongs to explorer, so the window under the pointer is asked and
    /// then each window it sits in, not only the top one.
    /// </remarks>
    private static bool IsSystemTextInput(Point physical)
    {
        const uint GA_PARENT = 1;
        uint own = (uint)Environment.ProcessId;

        // Bounded, in case a parent chain ever loops back on itself; real ones are a handful deep.
        var hwnd = WindowFromPoint(new System.Drawing.Point((int)physical.X, (int)physical.Y));
        for (int depth = 0; depth < 32 && hwnd != IntPtr.Zero; depth++, hwnd = GetAncestor(hwnd, GA_PARENT))
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == own) continue;

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                if (name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("TextInput.InputApp", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone between the two calls: not something to keep typing for.
            }
        }

        return false;
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
