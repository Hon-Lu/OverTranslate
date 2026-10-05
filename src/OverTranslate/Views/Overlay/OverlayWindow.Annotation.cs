using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// 標記: the marks the user draws over the capture, and the gesture that makes them.
/// </summary>
/// <remarks>
/// <para>This lives on the overlay rather than on the capture window for two reasons. The overlay is
/// already pinned to the whole virtual desktop and never moves, so it is a fixed frame of reference
/// for coordinates that must outlive the selection being dragged around. And it sits above the
/// translation bubbles, which is where a mark has to be: someone annotating after a translation is
/// marking up what is on screen, and after a translation half of what is on screen is bubbles.</para>
///
/// <para>It also means 截圖 picks the marks up for nothing. That copy renders this window's whole
/// content and crops it to the selection — see <c>RenderBubblesForSelection</c> — so a mark is in the
/// picture for the same reason a bubble is.</para>
/// </remarks>
public partial class OverlayWindow
{
    /// <summary>How far the pointer must travel before another point is recorded.</summary>
    /// <remarks>
    /// Mouse moves arrive far denser than a stroke needs. Without this a slow, deliberate line —
    /// which is what someone drawing carefully produces — lands hundreds of points inside a few DIP,
    /// and every one of them is re-rendered and re-hit-tested for the rest of the session.
    /// </remarks>
    private const double MinPointDistance = 1.2;

    /// <summary>
    /// How many steps back 復原 can go.
    /// </summary>
    /// <remarks>
    /// The history is a list of whole stroke lists rather than a log of edits, because a stroke is
    /// immutable once committed: a snapshot is a copy of the list, which is a handful of references.
    /// That makes undo and redo two integer moves and removes the entire class of bug where an
    /// inverse operation does not quite invert.
    /// </remarks>
    private const int HistoryLimit = 60;

    private readonly List<List<AnnotationStroke>> _annotationHistory = [[]];
    private int _annotationHistoryIndex;

    private bool _isAnnotating;
    private AnnotationTool _annotationTool = AnnotationTool.Pen;
    private Color _annotationColor = Colors.Orange;
    private double _annotationThickness = 4;
    private double _annotationOpacity = 0.45;

    private List<Point>? _wetPoints;
    private readonly WetInkLayer _wetLayer = new();

    // A shape being dragged out: where the drag started, and where its far corner is now.
    private Point? _shapeStart;
    private Point _shapeEnd;

    /// <summary>How far a shape's drag must reach before letting go of it leaves a mark.</summary>
    /// <remarks>
    /// A click with a shape tool in hand is almost always a click that meant something else — picking
    /// the spot to start from, or a press that slipped — and the zero-size shape it would leave is a
    /// dot nobody can tell was meant to be a box.
    /// </remarks>
    private const double MinShapeExtent = 3;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;

    /// <summary>Whether Shift is down right now.</summary>
    /// <remarks>
    /// Asked of the keyboard itself rather than of WPF. Nothing in this session holds the focus —
    /// see <see cref="AnnotationShortcutHook"/> — so WPF's own idea of the modifiers is whatever it
    /// was the last time this thread saw a key, which while drawing is never.
    /// </remarks>
    private static bool IsShiftDown => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    // An erase drag in progress: the strokes as they stand right now, where the eraser was last
    // seen, and whether it has taken anything yet.
    private List<Point>? _erasePoints;
    private Point _eraseLast;
    private bool _eraseTookSomething;

    private readonly InkSurface _ink = new();

    private Rect _annotationBounds;
    private Rect _annotationBoundsPhys;

    /// <summary>Where the pointer was last seen over the surface, for the eraser's ring to sit on.</summary>
    private Point? _lastPointer;

    /// <summary>Raised whenever the marks, or what can be undone, changed.</summary>
    public event EventHandler? AnnotationsChanged;

    public bool CanUndoAnnotation => _annotationHistoryIndex > 0;
    public bool CanRedoAnnotation => _annotationHistoryIndex < _annotationHistory.Count - 1;

    private List<AnnotationStroke> CurrentStrokes => _annotationHistory[_annotationHistoryIndex];

    /// <summary>
    /// Places the box the marks are confined to, in physical desktop pixels.
    /// </summary>
    /// <remarks>
    /// Called again every time the user moves or resizes the selection. Only the clip and the input
    /// surface move: the strokes stay exactly where they were drawn, so a box dragged off them hides
    /// them and a box dragged back shows them again. Clipping is reversible and deleting is not,
    /// which is the whole reason the marks are not owned by the box.
    /// </remarks>
    public void SetAnnotationBounds(double selPhysLeft, double selPhysTop, double selPhysWidth, double selPhysHeight)
    {
        _annotationBoundsPhys = new Rect(selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight);
        ApplyAnnotationBounds();
    }

    /// <summary>
    /// Converts the remembered box into this window's own coordinates and places the clip on it.
    /// </summary>
    /// <remarks>
    /// Split from the setter and run again from Loaded because the conversion needs the window's DPI,
    /// and that is only read once the window has a presentation source. The selection is known before
    /// then — the capture window has it the instant the drag ends — so the first call would otherwise
    /// divide by a placeholder scale of 1 and put the box somewhere else entirely on any monitor that
    /// is not at 100%.
    /// </remarks>
    private void ApplyAnnotationBounds()
    {
        var (selPhysLeft, selPhysTop, selPhysWidth, selPhysHeight) =
            (_annotationBoundsPhys.X, _annotationBoundsPhys.Y,
             _annotationBoundsPhys.Width, _annotationBoundsPhys.Height);
        if (selPhysWidth <= 0 || selPhysHeight <= 0) return;

        _annotationBounds = new Rect(
            (selPhysLeft - _physBounds.Left) / _dpiX,
            (selPhysTop  - _physBounds.Top)  / _dpiY,
            Math.Max(1, selPhysWidth  / _dpiX),
            Math.Max(1, selPhysHeight / _dpiY));

        AnnotationCanvas.Clip = new RectangleGeometry(_annotationBounds);
        AnnotationCursorCanvas.Clip = new RectangleGeometry(_annotationBounds);

        // The finished marks are shown in another window, so their clip goes with them rather than
        // being put on whatever ends up hosting them — the bubbles travel the same way and are not
        // confined to the box at all.
        _inkClip.Clip = new RectangleGeometry(_annotationBounds);
        InkLayerChanged?.Invoke(this, EventArgs.Empty);

        Canvas.SetLeft(AnnotationSurface, _annotationBounds.X);
        Canvas.SetTop(AnnotationSurface,  _annotationBounds.Y);
        AnnotationSurface.Width  = _annotationBounds.Width;
        AnnotationSurface.Height = _annotationBounds.Height;
    }

    /// <summary>Hands the pointer to the pen and shows the drawing surface.</summary>
    /// <remarks>
    /// <para>The window is click-through at the Win32 level for the rest of its life, which is what
    /// lets the user carry on with whatever is underneath while a translation sits on top of it.
    /// Taking that away is what makes drawing possible at all.</para>
    ///
    /// <para>Only over the selection, though. Everything else in this window is either
    /// un-hittable or painted with a fully transparent brush, and a layered window is hit-tested
    /// against its composited alpha — so a click anywhere outside the box still finds nothing here
    /// and lands where it would have landed before. The box is the only thing 標記 takes.</para>
    /// </remarks>
    public void BeginAnnotating(AnnotationTool tool, Color color, double thickness, double opacity)
    {
        _annotationTool      = tool;
        _annotationColor     = color;
        _annotationThickness = thickness;
        _annotationOpacity   = opacity;

        if (_isAnnotating) return;
        _isAnnotating = true;

        WindowStyles.SetClickThrough(this, false);
        IsHitTestVisible = true;
        AnnotationSurface.Visibility = Visibility.Visible;
        ApplyAnnotationCursor();
    }

    /// <summary>Gives the pointer back to whatever is underneath. The marks stay on screen.</summary>
    public void EndAnnotating()
    {
        if (!_isAnnotating) return;
        _isAnnotating = false;

        AbandonWetStroke();
        _lastPointer = null;
        HideEraserRing();
        AnnotationSurface.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        WindowStyles.SetClickThrough(this, true);
    }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        _annotationTool = tool;
        ApplyAnnotationCursor();
    }

    public void SetAnnotationColor(Color color) => _annotationColor = color;

    public void SetAnnotationThickness(double thickness) => _annotationThickness = thickness;

    /// <summary>How see-through the next highlight will be. Ignored by the other two tools.</summary>
    public void SetAnnotationOpacity(double opacity) => _annotationOpacity = opacity;

    /// <summary>Half the width the 大小 slider is showing, which is what the ring is drawn at.</summary>
    /// <remarks>
    /// The slider gives a diameter because that is what "大小" means for a circle you can see, and
    /// because it is then the same kind of number as 粗細 — how wide the mark is. Everything inside
    /// works in radius.
    /// </remarks>
    private double EraserRadius => Math.Max(1, _annotationThickness / 2);

    /// <summary>The opacity a stroke made right now would carry.</summary>
    /// <remarks>
    /// Only the highlighter is ever see-through. A pen the user had made translucent would be a pen
    /// that no longer covers what it is drawn over, which is the one thing separating the two tools.
    /// </remarks>
    private double CurrentStrokeOpacity =>
        _annotationTool == AnnotationTool.Highlighter ? _annotationOpacity : 1.0;

    public void UndoAnnotation()
    {
        if (!CanUndoAnnotation) return;
        _annotationHistoryIndex--;
        RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RedoAnnotation()
    {
        if (!CanRedoAnnotation) return;
        _annotationHistoryIndex++;
        RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the right pointer in the user's hand, and the ring on screen when the eraser is in it.
    /// </summary>
    /// <remarks>
    /// The system pointer is taken away entirely for the eraser rather than left inside the ring.
    /// The ring is the pointer — it is drawn at the exact size of what it will remove, which an
    /// arrow or a crosshair beside it can only contradict.
    /// </remarks>
    private void ApplyAnnotationCursor()
    {
        bool erasing = _annotationTool == AnnotationTool.Eraser;
        // A crosshair for the shapes: what they need from the pointer is the exact corner to start
        // from, and the pen's nib hides the very pixel it is on.
        AnnotationSurface.Cursor = erasing ? Cursors.None
            : AnnotationStroke.IsShapeTool(_annotationTool) ? Cursors.Cross
            : Cursors.Pen;
        if (!erasing) HideEraserRing();
        else RenderEraserRing();
    }

    private void RenderEraserRing()
    {
        if (!_isAnnotating || _annotationTool != AnnotationTool.Eraser || _lastPointer is not { } point)
        {
            HideEraserRing();
            return;
        }

        double radius = EraserRadius;
        EraserRing.Width  = radius * 2;
        EraserRing.Height = radius * 2;
        Canvas.SetLeft(EraserRing, point.X - radius);
        Canvas.SetTop(EraserRing,  point.Y - radius);
        EraserRing.Visibility = Visibility.Visible;
    }

    private void HideEraserRing() => EraserRing.Visibility = Visibility.Collapsed;

    private void AnnotationSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isAnnotating) return;
        AnnotationSurface.CaptureMouse();
        var point = e.GetPosition(AnnotationInputCanvas);

        if (_annotationTool == AnnotationTool.Eraser)
        {
            EnsureInkSurface();
            _erasePoints = [point];
            _eraseLast = point;
            _eraseTookSomething = _ink.Erase(point, point, EraserRadius);
            return;
        }

        if (AnnotationStroke.IsShapeTool(_annotationTool))
        {
            _shapeStart = point;
            _shapeEnd   = point;
            _wetLayer.BeginShape(_annotationColor, _annotationThickness, _annotationTool, point);
            AnnotationCanvas.Children.Add(_wetLayer);
            return;
        }

        _wetPoints = [point];

        _wetLayer.Begin(
            _annotationColor, _annotationThickness, CurrentStrokeOpacity,
            _annotationTool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
            point);
        AnnotationCanvas.Children.Add(_wetLayer);
    }

    private void AnnotationSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isAnnotating) return;

        var point = e.GetPosition(AnnotationInputCanvas);

        // Tracked on every move, button or no button: the ring has to follow the pointer before the
        // user commits to rubbing anything out. That is the whole point of it.
        _lastPointer = point;
        if (_annotationTool == AnnotationTool.Eraser) RenderEraserRing();

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_erasePoints is not null)
        {
            if (FarEnough(_eraseLast, point))
            {
                if (_ink.Erase(_eraseLast, point, EraserRadius)) _eraseTookSomething = true;
                _erasePoints.Add(point);
                _eraseLast = point;
            }
            return;
        }

        if (_shapeStart is { } start)
        {
            _shapeEnd = IsShiftDown ? AnnotationStroke.ConstrainShape(_annotationTool, start, point) : point;
            _wetLayer.ReshapeTo(start, _shapeEnd);
            return;
        }

        if (_wetPoints is null) return;
        if (!FarEnough(_wetPoints[^1], point)) return;

        _wetPoints.Add(point);
        _wetLayer.Extend(point);
    }

    private void AnnotationSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        // Only once the drag is over. Running the eraser off the edge of the box and back in is one
        // continuous rub, and a ring that vanished halfway through it would read as the tool having
        // been dropped.
        if (AnnotationSurface.IsMouseCaptured) return;
        _lastPointer = null;
        HideEraserRing();
    }

    private static bool FarEnough(Point last, Point next)
    {
        double dx = next.X - last.X, dy = next.Y - last.Y;
        return dx * dx + dy * dy >= MinPointDistance * MinPointDistance;
    }

    private void AnnotationSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isAnnotating) return;
        AnnotationSurface.ReleaseMouseCapture();

        if (_erasePoints is not null)
        {
            var path = _erasePoints;
            bool took = _eraseTookSomething;
            _erasePoints = null;
            _eraseTookSomething = false;

            // Nothing changed if the drag never met a mark — and a history entry for that would be a
            // press of 復原 that visibly does nothing.
            if (!took) return;

            // The rub is recorded as a stroke of its own rather than as a change to the strokes it
            // crossed. The picture cannot be stepped backwards, so going back a step repaints it
            // from this list, and a rub has to be one of the things on the list to be repainted —
            // in its place, so that a mark drawn afterwards is not rubbed out along with it.
            CommitStrokes([.. CurrentStrokes, new AnnotationStroke
            {
                Tool      = AnnotationTool.Eraser,
                Color     = Colors.Transparent,
                Thickness = EraserRadius * 2,
                Opacity   = 1,
                Points    = path,
            }], alreadyPainted: true);
            return;
        }

        if (_shapeStart is { } shapeStart)
        {
            var shapeEnd = _shapeEnd;
            AbandonWetStroke();

            // A line by its length, a box by its longer side: a long, flat box is still a deliberate
            // drag, however little it is tall.
            double dx = Math.Abs(shapeEnd.X - shapeStart.X), dy = Math.Abs(shapeEnd.Y - shapeStart.Y);
            double extent = _annotationTool == AnnotationTool.Line ? Math.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
            if (extent < MinShapeExtent) return;

            var shape = new AnnotationStroke
            {
                Tool      = _annotationTool,
                Color     = _annotationColor,
                Thickness = _annotationThickness,
                Opacity   = 1,
                Points    = [shapeStart, shapeEnd],
            };

            EnsureInkSurface();
            _ink.Lay(shape);
            CommitStrokes([.. CurrentStrokes, shape], alreadyPainted: true);
            return;
        }

        if (_wetPoints is null) return;

        var points = _wetPoints;
        AbandonWetStroke();

        var drawn = new AnnotationStroke
        {
            Tool      = _annotationTool,
            Color     = _annotationColor,
            Thickness = _annotationThickness,
            Opacity   = CurrentStrokeOpacity,
            Points    = points,
        };

        EnsureInkSurface();
        _ink.Lay(drawn);
        CommitStrokes([.. CurrentStrokes, drawn], alreadyPainted: true);
    }

    /// <summary>
    /// Drops whatever is half-drawn without committing it.
    /// </summary>
    /// <remarks>
    /// The line being drawn is a child of the ink canvas like any other, so leaving it there and
    /// redrawing from the committed list would show it until the next redraw and then lose it. It is
    /// removed rather than committed because the two callers are 標記 being switched off mid-stroke
    /// and the session ending, and neither is the user finishing a mark.
    /// </remarks>
    private void AbandonWetStroke()
    {
        AnnotationCanvas.Children.Remove(_wetLayer);
        _wetLayer.Clear();
        _wetPoints = null;
        _shapeStart = null;

        // A rub that is walked away from has already taken ink off the picture, and the picture is
        // the only place that happened — so the marks that are still on the list are painted again.
        bool wasRubbing = _erasePoints is not null;
        _erasePoints = null;
        _eraseTookSomething = false;
        if (wasRubbing) RedrawAnnotations();
        if (AnnotationSurface.IsMouseCaptured) AnnotationSurface.ReleaseMouseCapture();
    }



    private void CommitStrokes(List<AnnotationStroke> next, bool alreadyPainted = false)
    {
        // Anything that was undone and not redone is gone the moment a new mark is made, which is
        // what every editor does and what the user means by carrying on from here.
        if (_annotationHistoryIndex < _annotationHistory.Count - 1)
            _annotationHistory.RemoveRange(
                _annotationHistoryIndex + 1, _annotationHistory.Count - _annotationHistoryIndex - 1);

        _annotationHistory.Add(next);
        if (_annotationHistory.Count > HistoryLimit) _annotationHistory.RemoveAt(0);
        _annotationHistoryIndex = _annotationHistory.Count - 1;

        if (!alreadyPainted) RedrawAnnotations();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RedrawAnnotations()
    {
        EnsureInkSurface();
        _ink.Replay(CurrentStrokes);
    }

    /// <summary>
    /// Makes sure there is a picture to draw on, and that it is under the stroke being drawn.
    /// </summary>
    /// <remarks>
    /// Grown to the selection rather than made the size of the window — see <see cref="InkSurface"/>.
    /// Anything drawn outside the box is clipped away unseen, so the box is all the picture has to
    /// hold; a box that is moved or stretched takes the picture with it.
    /// </remarks>
    private void EnsureInkSurface()
    {
        if (_annotationBounds.Width > 0 && _annotationBounds.Height > 0)
            _ink.Ensure(_annotationBounds, _dpiX);

        InkLayerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The marks, in a holder of their own that keeps them inside the box.</summary>
    /// <remarks>
    /// Shown by the capture window rather than by this one. This window has to be transparent — the
    /// bubbles float over what was on screen — and a transparent window is presented by copying all
    /// of its content out of video memory and back whenever any of it changes. For a window the size
    /// of the desktop that measured 2.38ms per change against 0.035ms opaque, which is what an
    /// eraser dropping frames was made of. Anything that changes while the pointer is moving pays
    /// that price on every move, so anything that does has to be somewhere else. The wet stroke
    /// stays: it is a small vector shape, and it only changes while a stroke is being drawn, which
    /// is a state the marks are being uploaded in anyway.
    /// </remarks>
    private readonly Canvas _inkClip = new() { IsHitTestVisible = false };

    /// <summary>
    /// Everything of this window's that the capture window shows instead, bottom first.
    /// </summary>
    /// <remarks>
    /// The bubbles go across with the marks rather than staying behind, because the two have to keep
    /// their order and the order is marks on top: a mark is drawn over a translation the user is
    /// looking at. Left here they would be above the marks on screen and below them in the saved
    /// picture, which is the same disagreement between what is aimed at and what comes out that
    /// putting the marks under the selection tint caused. They cost nothing to move — they change
    /// when a translation arrives, not while a rub is going on.
    ///
    /// The stroke being drawn and the eraser ring go across because they are what follows the
    /// pointer, and repainting anything at all in a transparent window costs a whole-desktop
    /// upload — which is charged per mouse move whether or not the move did any work. The ring is
    /// the plainest case: it made the whole screen stutter the moment the eraser was picked up,
    /// before a rub had even started. Nothing that moves is left behind now; what stays in that
    /// window is the input surface, which is a fixed rectangle, and the indicator.
    ///
    /// Both stay out of the saved picture exactly as they did before the move — the ring because
    /// RenderOverlayForSelection names the layers it draws and the ring is not one of them, the wet
    /// stroke because by the time anything is saved it has been baked into the marks and taken off
    /// its canvas again.
    /// </remarks>
    public IReadOnlyList<UIElement> CaptureHostedLayers
    {
        get
        {
            if (!_inkClip.Children.Contains(_ink.Element)) _inkClip.Children.Add(_ink.Element);
            return [
                BubbleBackgroundCanvas, BubbleTextCanvas, _inkClip, AnnotationCanvas,
                AnnotationCursorCanvas];
        }
    }

    /// <summary>How much of the ink layer the selection lets through.</summary>
    public Rect InkClip => _annotationBounds;

    /// <summary>The picture the marks are in, and where it sits — what the capture has to include.</summary>
    internal ImageSource? InkSource => _ink.Source;

    internal Rect InkBounds => _ink.Bounds;

    /// <summary>Whether anything has been drawn at all.</summary>
    internal bool HasInk => CurrentStrokes.Count > 0;

    /// <summary>Raised when the ink layer, or how much of it shows, has changed.</summary>
    public event EventHandler? InkLayerChanged;



    /// <summary>
    /// Flat ends for the highlighter, round for the pen: a chisel tip is what makes a highlight look
    /// laid down over a line of text rather than piped onto it.
    /// </summary>
    private static PenLineCap CapFor(AnnotationTool tool) =>
        tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;

}
