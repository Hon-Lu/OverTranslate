using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// The stroke currently under the pointer, drawn in blocks so that only the newest is redrawn.
/// </summary>
/// <remarks>
/// <para>Separate from the finished marks because a line being drawn is the one thing on the surface
/// that changes on every pointer event. Left as a growing <c>Polyline</c> it is described again in
/// full every time a point is added: measured over one stroke that is 0.29ms at the thousandth point
/// and 0.76ms at the four thousandth, 2177ms of rebuilding across a single long scribble — the
/// drawing going gradually stiff that a user reports as "it gets worse the longer I draw, and the
/// next stroke is fine again".</para>
///
/// <para>Blocks rather than one visual per segment. A segment on its own has to be given end caps,
/// and end caps are not what happens at a bend: a 螢光筆 is flat-ended, because that is what a
/// chisel tip looks like, so a line made of separately capped pieces leaves a wedge of bare screen
/// at the outside of every turn and comes out looking dashed while it is being drawn. Inside a block
/// the bends are the line's own joins, drawn round, and the block is rasterised in one go — so the
/// ink is the same ink the finished stroke is made of. Each block starts on the segment the last one
/// ended with, which puts every join inside some block and leaves only the two true ends of the
/// stroke wearing a cap.</para>
///
/// <para>What that costs is one block redrawn per pointer event, which is a fixed sixty-odd points
/// however long the stroke goes on.</para>
///
/// <para>The blocks are drawn opaque and the whole layer is faded instead, which is what keeps a
/// 螢光筆 even. Drawing them at the stroke's own opacity would compose them one over another and
/// leave a darker knot everywhere the line crosses itself or merely turns — the same reason the
/// finished stroke carries its opacity on the element rather than in its brush.</para>
/// </remarks>
internal sealed class WetInkLayer : FrameworkElement
{
    /// <summary>
    /// How many points a block holds before the next one starts.
    /// </summary>
    /// <remarks>
    /// The whole block is described again on every pointer event, so this is the work each event
    /// costs; it is also how much ink is thrown away and redrawn, so it wants to stay small. Sixty
    /// is about a second of unhurried drawing.
    /// </remarks>
    private const int BlockSize = 64;

    private readonly VisualCollection _blocks;

    private DrawingVisual? _active;
    private List<Point> _activePoints = [];
    private Pen? _pen;
    private bool _isFirstBlock;
    private AnnotationTool _shapeTool;

    public WetInkLayer()
    {
        _blocks = new VisualCollection(this);
        IsHitTestVisible = false;
    }

    /// <summary>Starts a stroke at <paramref name="start"/>, throwing away anything left of the last one.</summary>
    public void Begin(Color color, double thickness, double opacity, PenLineCap cap, Point start)
    {
        _blocks.Clear();

        _pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = cap,
            EndLineCap   = cap,
            LineJoin     = PenLineJoin.Round,
        };
        _pen.Freeze();

        Opacity       = opacity;
        _activePoints = [start];
        _isFirstBlock = true;

        _active = new DrawingVisual();
        _blocks.Add(_active);
        Redraw();
    }

    /// <summary>Carries the stroke on to where the pointer is now.</summary>
    public void Extend(Point next)
    {
        if (_pen is null || _active is null) return;

        _activePoints.Add(next);

        // Sealed and left alone, and the next block picks up the segment this one ended with so the
        // bend between them is drawn as a join by one of them rather than as two caps by both.
        if (_activePoints.Count >= BlockSize)
        {
            Redraw();

            _activePoints = [_activePoints[^2], _activePoints[^1]];
            _isFirstBlock = false;
            _active = new DrawingVisual();
            _blocks.Add(_active);
        }

        Redraw();
    }

    /// <summary>Starts a shape being dragged out, throwing away anything left of the last stroke.</summary>
    /// <remarks>
    /// One visual redrawn whole on every move rather than the blocks a freehand line is built from:
    /// a shape is a single segment or outline however long the drag goes on, so there is nothing to
    /// seal and nothing that grows.
    /// </remarks>
    public void BeginShape(Color color, double thickness, AnnotationTool tool, Point start)
    {
        _blocks.Clear();

        _pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap   = PenLineCap.Round,
            LineJoin     = InkSurface.Join(tool),
        };
        _pen.Freeze();

        Opacity       = 1;
        _shapeTool    = tool;
        _activePoints = [];
        _active = new DrawingVisual();
        _blocks.Add(_active);
        ReshapeTo(start, start);
    }

    /// <summary>Redraws the shape for a drag that has now reached <paramref name="end"/>.</summary>
    public void ReshapeTo(Point start, Point end)
    {
        if (_pen is null || _active is null) return;

        using var dc = _active.RenderOpen();
        dc.DrawGeometry(null, _pen, AnnotationStroke.ShapeGeometry(_shapeTool, start, end));
    }

    /// <summary>Takes the stroke away, which is what happens once it has been committed.</summary>
    public void Clear()
    {
        _blocks.Clear();
        _activePoints = [];
        _active = null;
        _pen = null;
    }

    private void Redraw()
    {
        if (_pen is null || _active is null) return;

        using var dc = _active.RenderOpen();

        // A stroke that is still one point has no length to draw along. A round nib leaves the dot
        // the user expects; a chisel one has no width across a point it has not moved off yet, and
        // the finished stroke draws nothing there either.
        if (_activePoints.Count < 2)
        {
            if (_isFirstBlock && _pen.StartLineCap == PenLineCap.Round)
                dc.DrawEllipse(_pen.Brush, null, _activePoints[0], _pen.Thickness / 2, _pen.Thickness / 2);
            return;
        }

        // Only the first block: it owns the stroke's start cap, and a start cap squared against a
        // speck of hand-shake is what leaves a tab of ink pointing off the end of a quick stroke.
        // The trailing end is left alone here because it is wherever the pointer is, and is settled
        // once for good when the stroke is committed.
        var drawn = _isFirstBlock
            ? AnnotationStroke.WithoutEndJitter(_activePoints, _pen.Thickness)
            : _activePoints;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(drawn[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo([.. drawn.Skip(1)], isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();

        dc.DrawGeometry(null, _pen, geometry);
    }

    protected override int VisualChildrenCount => _blocks.Count;

    protected override Visual GetVisualChild(int index) => _blocks[index];
}
