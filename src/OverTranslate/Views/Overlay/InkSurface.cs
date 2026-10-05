using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// Every finished mark, kept as one picture rather than as one shape per stroke.
/// </summary>
/// <remarks>
/// <para>The reason is what it costs to have them on screen at all, which is separate from what it
/// costs to draw or rub one out. A finished stroke left as a <c>Polyline</c> is re-rasterised every
/// time anything overlapping it changes, and it cannot be skipped the way a small shape can, because
/// a scribble's box covers most of the screen. Measured with a ring moved over the ink the way the
/// eraser cursor is: an empty surface renders a frame in 6.1ms, ten scribbles in 11.6ms, and thirty
/// in 19.6ms with frames as long as 30ms — visible as a cursor that will not keep up, before any
/// erasing has been asked for. The same thirty baked into one bitmap render in 6.1ms, which is the
/// empty figure again. The cost of showing what has been drawn stops depending on how much it is.</para>
///
/// <para>Ink goes on and the eraser takes off, both straight into the pixels: source-over for one
/// and destination-out for the other, each costing the area it touches and nothing else. Both are a
/// line of arithmetic here because the bitmap is premultiplied — the colour channels already carry
/// their alpha, so fading a pixel out is multiplying all four of them by the same number, and
/// laying one over another is an add and a multiply. Getting that wrong is what leaves coloured
/// fringes along an erased edge, and it is only right if the format is not mixed up.</para>
///
/// <para>What is not here is undo. The picture cannot be stepped backwards — pixels do not remember
/// what they covered — so the marks are still kept as a list of strokes, and going back a step
/// repaints this from that list. See <c>Replay</c>.</para>
/// </remarks>
public sealed class InkSurface
{
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private double _scale = 1;
    private Rect _bounds;

    // What has been painted into the mirror but not yet handed to the bitmap, and whether a hand-over
    // is already booked for the next frame.
    private Int32Rect _pending;
    private bool _flushBooked;

    // Handing over is what makes a transparent window recompose, and that is the expensive part;
    // this is how often it is allowed to happen.
    private readonly System.Diagnostics.Stopwatch _sinceFlush = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The picture itself, for whatever has to include it without going through the tree.</summary>
    public ImageSource? Source => _bitmap;

    /// <summary>Where the picture sits, in the overlay's own DIP coordinates.</summary>
    public Rect Bounds => _bounds;

    /// <summary>The element that shows the marks. Hung in the capture window — see OverlayWindow.InkLayer.</summary>
    public Image Element { get; } = new() { IsHitTestVisible = false };

    /// <summary>Makes the surface cover <paramref name="bounds"/> as well as whatever it covers now.</summary>
    /// <remarks>
    /// <para>Grown to fit the selection rather than allocated across the whole window. The window is
    /// pinned to the entire virtual desktop, so sizing to it costs a desktop-sized picture and a
    /// second copy of it the moment the first mark is made — around 66MB for one 4K screen and more
    /// for several — however small the box the user actually drew in. The selection is what the ink
    /// is clipped to, so it is also all the ink there can be to keep.</para>
    ///
    /// <para>It grows and never shrinks, and what it holds is carried across when it does, because a
    /// mark is not owned by the box it was drawn in: moving the box off a mark hides it and moving
    /// back shows it again, so the ink has to go on living outside whatever the selection is now.
    /// Allocated on the first mark and not before — most captures are never drawn on.</para>
    /// </remarks>
    public void Ensure(Rect bounds, double scale)
    {
        // A change of scale is a change of monitor under the window, and nothing kept at the old one
        // lines up with the new; that is rare enough to start again over.
        bool rescaled = _bitmap is not null && Math.Abs(scale - _scale) > 1e-9;
        var wanted = _bitmap is null || rescaled ? bounds : Rect.Union(_bounds, bounds);

        if (_bitmap is not null && !rescaled && wanted == _bounds) return;

        int width  = Math.Max(1, (int)Math.Ceiling(wanted.Width  * scale));
        int height = Math.Max(1, (int)Math.Ceiling(wanted.Height * scale));

        var kept       = rescaled ? null : _pixels;
        var keptBounds = _bounds;
        int keptWidth  = _width;
        int keptHeight = _height;

        _bounds = wanted;
        _scale  = scale;
        _width  = width;
        _height = height;
        _pixels = new byte[width * height * 4];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);

        if (kept is not null)
        {
            int dx = (int)Math.Round((keptBounds.X - wanted.X) * scale);
            int dy = (int)Math.Round((keptBounds.Y - wanted.Y) * scale);

            for (int y = 0; y < keptHeight; y++)
                Array.Copy(
                    kept, y * keptWidth * 4,
                    _pixels, ((y + dy) * width + dx) * 4,
                    keptWidth * 4);
        }

        _pending = default;
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);

        Element.Source = _bitmap;
        Element.Width  = wanted.Width;
        Element.Height = wanted.Height;
        Canvas.SetLeft(Element, wanted.X);
        Canvas.SetTop(Element, wanted.Y);
    }

    /// <summary>Wipes the picture, which is where repainting from the stroke list starts.</summary>
    public void Clear()
    {
        if (_pixels is null || _bitmap is null) return;

        Array.Clear(_pixels);
        Touched(0, 0, _width, _height);
    }

    /// <summary>
    /// Notes that a patch of the mirror has changed, and books one hand-over for the coming frame.
    /// </summary>
    /// <remarks>
    /// <para>Booked rather than done on the spot, and merged into one rectangle, because a
    /// <c>WriteableBitmap</c> only tracks a few changed rectangles before it gives up and treats the
    /// whole picture as changed. A rub sends a pointer event every millisecond or so and each one
    /// used to hand over its own little patch, so a single frame could carry dozens of them and the
    /// bitmap would fall back to sending all of it — which costs what the whole surface costs, not
    /// what was rubbed.</para>
    ///
    /// <para>That is what a full-screen box felt like: measured against a smaller box with a
    /// <em>larger</em> eraser, a step cost twice as much and frames spiked from 21-28ms to 39-50ms,
    /// while the median frame stayed at 1ms — occasional, which is what an occasional full upload
    /// looks like. Merging leaves one rectangle a frame, so what is sent is what changed.</para>
    ///
    /// <para>This is not the same as coalescing the pointer events themselves, which was measured
    /// and does nothing: the arithmetic of a rub is under a tenth of a millisecond. It is the
    /// hand-over to the bitmap that had to be rationed, not the work.</para>
    /// </remarks>
    private void Touched(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        if (_pending.Width == 0 || _pending.Height == 0)
        {
            _pending = new Int32Rect(x, y, width, height);
        }
        else
        {
            int left   = Math.Min(_pending.X, x);
            int top    = Math.Min(_pending.Y, y);
            int right  = Math.Max(_pending.X + _pending.Width,  x + width);
            int bottom = Math.Max(_pending.Y + _pending.Height, y + height);
            _pending = new Int32Rect(left, top, right - left, bottom - top);
        }

        if (_flushBooked) return;

        _flushBooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Hands over at most sixty times a second, however often frames come round.</summary>
    /// <remarks>
    /// The overlay is a transparent window, and the system composes one of those from a copy of its
    /// whole content whenever any of it changes — measured at 6ms a frame opaque against 15ms and
    /// worse layered, with the surface's own size making little difference and cutting it into tiles
    /// making none. So what costs is how often the picture is handed over, not how much of it
    /// changed; and frames here come round far faster than a screen can show them, the app having
    /// been seen at three hundred a second. That was three hundred recompositions for a rub the eye
    /// reads at sixty.
    /// </remarks>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_sinceFlush.Elapsed.TotalMilliseconds < 15) return;
        Flush();
    }

    /// <summary>Hands over everything painted since the last frame. Safe to call when there is nothing.</summary>
    public void Flush()
    {
        if (_flushBooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _flushBooked = false;
        }

        if (_pixels is null || _bitmap is null) return;
        if (_pending.Width == 0 || _pending.Height == 0) return;

        _bitmap.WritePixels(
            _pending, _pixels, _width * 4, (_pending.Y * _width + _pending.X) * 4);

        _pending = default;
        _sinceFlush.Restart();
    }

    /// <summary>Paints the marks in order, which is the only way an erased-then-drawn-over spot comes out right.</summary>
    /// <remarks>
    /// In runs rather than one at a time. Order only has to be kept where it changes the answer, and
    /// that is at a rub: marks laid down between two rubs cannot reach each other's pixels, so they
    /// can be rasterised together and blended in once. One at a time measured 22ms a mark — 860ms to
    /// undo a step with forty marks on screen and 2.8 seconds with a hundred and twenty, which is a
    /// keypress that looks like a hang. The work becomes one pass per rub instead of one per mark.
    /// </remarks>
    public void Replay(IEnumerable<AnnotationStroke> strokes)
    {
        Clear();

        var run = new List<AnnotationStroke>();
        foreach (var stroke in strokes)
        {
            if (stroke.Tool != AnnotationTool.Eraser)
            {
                run.Add(stroke);
                continue;
            }

            LayAll(run);
            run.Clear();
            Rub(stroke);
        }

        LayAll(run);
    }

    /// <summary>Lays one finished stroke down.</summary>
    public void Lay(AnnotationStroke stroke) => LayAll([stroke]);

    /// <summary>Lays down a run of strokes with nothing rubbed out between them.</summary>
    public void LayAll(IReadOnlyList<AnnotationStroke> strokes)
    {
        if (_pixels is null || _bitmap is null || strokes.Count == 0) return;

        var reach = strokes[0].Bounds;
        for (int i = 1; i < strokes.Count; i++) reach.Union(strokes[i].Bounds);

        var box = Clamp(reach);
        if (box.IsEmpty) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(_scale, _scale));
            dc.PushTransform(new TranslateTransform(-box.DipX, -box.DipY));

            foreach (var stroke in strokes)
            {
                var pen = new Pen(new SolidColorBrush(stroke.Color), stroke.Thickness)
                {
                    StartLineCap = Cap(stroke.Tool),
                    EndLineCap   = Cap(stroke.Tool),
                    LineJoin     = Join(stroke.Tool),
                };

                // Composed whole and then faded, never piece by piece at its own opacity: a 螢光筆
                // crosses itself at every turn, and laying the pieces one over another is what
                // leaves a band with dark knots in it. The fade is per stroke, so two separate
                // marks still darken where they cross, as two passes of a real highlighter do.
                dc.PushOpacity(Math.Clamp(stroke.Opacity, 0, 1));

                // A single point is a tap. A round nib leaves a dot; a chisel one has no width
                // across a point it never moved off, so there is nothing to lay down.
                if (stroke.IsShape && stroke.Points.Count == 2)
                {
                    dc.DrawGeometry(null, pen,
                        AnnotationStroke.ShapeGeometry(stroke.Tool, stroke.Points[0], stroke.Points[1]));
                }
                else if (stroke.Points.Count == 1)
                {
                    if (pen.StartLineCap == PenLineCap.Round)
                        dc.DrawEllipse(pen.Brush, null, stroke.Points[0], stroke.Thickness / 2, stroke.Thickness / 2);
                }
                else
                {
                    dc.DrawGeometry(null, pen,
                        Line(AnnotationStroke.WithoutEndJitter(stroke.Points, stroke.Thickness)));
                }

                dc.Pop();
            }

            dc.Pop();
            dc.Pop();
        }

        var patch = new RenderTargetBitmap(box.Width, box.Height, 96, 96, PixelFormats.Pbgra32);
        patch.Render(visual);

        var src = new byte[box.Width * box.Height * 4];
        patch.CopyPixels(src, box.Width * 4, 0);

        for (int y = 0; y < box.Height; y++)
        {
            int from = y * box.Width * 4;
            int into = ((box.Y + y) * _width + box.X) * 4;

            for (int x = 0; x < box.Width * 4; x += 4)
            {
                // Premultiplied source-over. Alpha is one of the four channels rather than a
                // special case, which is what keeps the colours and the coverage in step.
                int sa = src[from + x + 3];
                if (sa == 0) continue;

                int keep = 255 - sa;
                for (int c = 0; c < 4; c++)
                    _pixels[into + x + c] =
                        (byte)Math.Min(255, src[from + x + c] + _pixels[into + x + c] * keep / 255);
            }
        }

        Touched(box.X, box.Y, box.Width, box.Height);
    }

    /// <summary>Takes the whole path of one erase drag back off again.</summary>
    private void Rub(AnnotationStroke path)
    {
        double radius = path.Thickness / 2;
        for (int i = 1; i < path.Points.Count; i++) Erase(path.Points[i - 1], path.Points[i], radius);
        if (path.Points.Count == 1) Erase(path.Points[0], path.Points[0], radius);
    }

    /// <summary>
    /// Rubs out the circle of <paramref name="radius"/> dragged from one point to the other.
    /// </summary>
    /// <returns>Whether it uncovered anything that was showing.</returns>
    public bool Erase(Point from, Point to, double radius)
    {
        if (_pixels is null || _bitmap is null) return false;

        double x0 = (from.X - _bounds.X) * _scale, y0 = (from.Y - _bounds.Y) * _scale;
        double x1 = (to.X   - _bounds.X) * _scale, y1 = (to.Y   - _bounds.Y) * _scale;
        double r  = radius * _scale;

        int minX = (int)Math.Max(0, Math.Floor(Math.Min(x0, x1) - r - 1));
        int maxX = (int)Math.Min(_width  - 1, Math.Ceiling(Math.Max(x0, x1) + r + 1));
        int minY = (int)Math.Max(0, Math.Floor(Math.Min(y0, y1) - r - 1));
        int maxY = (int)Math.Min(_height - 1, Math.Ceiling(Math.Max(y0, y1) + r + 1));
        if (minX > maxX || minY > maxY) return false;

        double dx = x1 - x0, dy = y1 - y0;
        double lengthSquared = dx * dx + dy * dy;
        bool took = false;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                // Distance to the segment, not to its ends: the pointer is sampled, so a bite taken
                // at each end would leave the middle of a quick drag standing.
                double t = lengthSquared < 1e-9
                    ? 0
                    : Math.Clamp(((x - x0) * dx + (y - y0) * dy) / lengthSquared, 0, 1);
                double cx = x0 + t * dx, cy = y0 + t * dy;
                double distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

                // A one-pixel ramp at the rim, or the channel the eraser cuts has a stepped edge
                // that is plainly visible against a stroke several times wider than the steps.
                double covered = Math.Clamp(r + 0.5 - distance, 0, 1);
                if (covered <= 0) continue;

                int offset = (y * _width + x) * 4;
                if (_pixels[offset + 3] == 0) continue;

                // Destination-out, premultiplied: all four channels fade together.
                int keep = (int)(255 * (1 - covered));
                for (int c = 0; c < 4; c++) _pixels[offset + c] = (byte)(_pixels[offset + c] * keep / 255);

                took = true;
            }
        }

        if (!took) return false;

        Touched(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return true;
    }

    /// <summary>How much ink is showing at this spot: 1 solid, 0 bare. What a test can ask.</summary>
    public double InkAt(Point dip)
    {
        if (_pixels is null) return 0;

        int x = (int)((dip.X - _bounds.X) * _scale);
        int y = (int)((dip.Y - _bounds.Y) * _scale);
        if (x < 0 || y < 0 || x >= _width || y >= _height) return 0;

        return _pixels[(y * _width + x) * 4 + 3] / 255.0;
    }

    private static PenLineCap Cap(AnnotationTool tool) =>
        tool == AnnotationTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;

    /// <summary>Square corners for a box — a 方框 with rounded corners reads as a button, not a frame.</summary>
    internal static PenLineJoin Join(AnnotationTool tool) =>
        tool == AnnotationTool.Rectangle ? PenLineJoin.Miter : PenLineJoin.Round;

    private static StreamGeometry Line(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo([.. points.Skip(1)], isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private readonly record struct Patch(int X, int Y, int Width, int Height, double DipX, double DipY)
    {
        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>The stroke's box in whole pixels of this surface, cut down to what the surface holds.</summary>
    private Patch Clamp(Rect dip)
    {
        int x = (int)Math.Floor((dip.X - _bounds.X) * _scale);
        int y = (int)Math.Floor((dip.Y - _bounds.Y) * _scale);
        int right  = (int)Math.Ceiling((dip.Right  - _bounds.X) * _scale);
        int bottom = (int)Math.Ceiling((dip.Bottom - _bounds.Y) * _scale);

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        right  = Math.Min(_width,  right);
        bottom = Math.Min(_height, bottom);

        return new Patch(
            x, y, right - x, bottom - y,
            _bounds.X + x / _scale, _bounds.Y + y / _scale);
    }
}
