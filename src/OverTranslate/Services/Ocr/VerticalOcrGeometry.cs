using System.Windows;
using TextBox = RapidOcrNet.TextBox;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>Vertical recognition crops and grouping, always retaining source-frame bounds.</summary>
internal static class VerticalOcrGeometry
{
    /// <summary>
    /// PaddleOCR rectifies a tall quad then rotates it counterclockwise (numpy.rot90).
    /// RapidOcrNet 3.0.0's GetPartImages instead rotates tall crops clockwise. Starting the
    /// crop at the top-right corner produces the required horizontal crop directly, with
    /// top-to-bottom text running left-to-right, and avoids its automatic tall-crop turn.
    /// The detection quad must stay unchanged: it is still used to map results to the source.
    /// </summary>
    internal static TextBox ForRecognition(TextBox box)
    {
        var p = box.BoxPoints;
        var width = Distance(p[0], p[1]);
        var height = Distance(p[0], p[3]);
        return height >= width * 1.5
            ? new TextBox { Score = box.Score, BoxPoints = [p[1], p[2], p[3], p[0]] }
            : box;
    }

    private static double Distance(SKPointI a, SKPointI b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // Horizontal normalisation shrinks a row's height and strips icon-like ideographs.
    // Neither is justified for a column. Keep detection coverage intact and size on its width.
    internal static List<OcrTextBlock> PrepareBlocks(List<OcrTextBlock> blocks) =>
        blocks.Where(b => b.Bounds.Width > 0 && b.Bounds.Height > 0)
            .Select(b => b with
            {
                LayoutBounds = b.Bounds,
                LayoutScript = LayoutScriptDetection.For(b.Text),
                LayoutGlyphHeight = GlyphPitch(b),
                RenderGlyphHeight = GlyphPitch(b),
            }).ToList();

    internal static double GlyphPitch(OcrTextBlock block)
    {
        var width = block.LayoutBounds.Width > 0 ? block.LayoutBounds.Width : block.Bounds.Width;
        var advance = block.Bounds.Height / Math.Max(1, block.Text.Count(c => !char.IsWhiteSpace(c)));
        // An unresolved two-column box has roughly four times as much width as advance:
        // 2g wide, but 2n characters along ng height. Recover g from area in that case.
        return Math.Min(width, width >= advance * 3 ? Math.Sqrt(width * advance) : advance);
    }

    /// <summary>Joins fragments down the same column before the right-to-left column merge.</summary>
    internal static List<OcrTextBlock> JoinColumnFragments(List<OcrTextBlock> blocks, SKBitmap? pixels = null)
    {
        var columns = new List<OcrTextBlock>();
        foreach (var block in blocks.OrderBy(b => b.LayoutBounds.Top).ThenByDescending(b => b.LayoutBounds.Right))
        {
            var b = block.LayoutBounds;
            var match = columns.Select((column, index) => (column, index))
                .Where(item => Continues(item.column, block) &&
                    !HasDivider(pixels, item.column.LayoutBounds, b))
                .OrderBy(item => b.Top - item.column.LayoutBounds.Bottom)
                .Select(item => (int?)item.index).FirstOrDefault();
            if (match is not { } index)
            {
                columns.Add(block);
                continue;
            }

            var previous = columns[index];
            var text = previous.Text + block.Text;
            var count = previous.Text.Length + block.Text.Length;
            columns[index] = previous with
            {
                Text = text,
                Bounds = Rect.Union(previous.Bounds, block.Bounds),
                LayoutBounds = Rect.Union(previous.LayoutBounds, block.LayoutBounds),
                LayoutScript = LayoutScriptDetection.For(text),
                SourceLineBounds = null,
                Confidence = previous.Confidence.HasValue && block.Confidence.HasValue && count > 0
                    ? (previous.Confidence.Value * previous.Text.Length + block.Confidence.Value * block.Text.Length) / count
                    : null,
            };
        }
        return columns;
    }

    private static bool Continues(OcrTextBlock previous, OcrTextBlock next)
    {
        var a = previous.LayoutBounds;
        var b = next.LayoutBounds;
        var width = Math.Min(a.Width, b.Width);
        if (width <= 0 || Math.Max(a.Width, b.Width) > width * 1.5)
            return false;
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var gap = b.Top - a.Bottom;
        var pitch = Math.Min(GlyphPitch(previous), GlyphPitch(next));
        return overlap >= width * 0.7 && gap >= 0 && gap <= pitch * 0.4;
    }

    private static bool HasDivider(SKBitmap? pixels, Rect a, Rect b)
    {
        if (pixels is null) return false;
        var width = Math.Min(a.Width, b.Width);
        var left = Math.Max(0, (int)Math.Floor(Math.Max(a.Left, b.Left) - width * 0.35));
        var right = Math.Min(pixels.Width, (int)Math.Ceiling(Math.Min(a.Right, b.Right) + width * 0.35));
        var top = Math.Max(0, (int)Math.Floor(a.Bottom - width * 0.5));
        var bottom = Math.Min(pixels.Height, (int)Math.Ceiling(b.Top + width * 0.5));
        // Balloon contours may slope and may sit inside detector padding. Look for a rule
        // spanning beyond the glyph width, allowing its y coordinate to vary across the gap.
        var covered = 0;
        for (var x = left; x < right; x++)
        {
            for (var y = top; y < bottom; y++)
            {
                var c = pixels.GetPixel(x, y);
                if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) >= 128) continue;
                covered++;
                break;
            }
        }
        return right > left && covered >= (right - left) * 0.8;
    }
}
