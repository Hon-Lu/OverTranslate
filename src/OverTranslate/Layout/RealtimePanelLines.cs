using System.Globalization;
using System.Windows;
using OverTranslate.Services;

namespace OverTranslate.Layout;

/// <summary>Flows a complete translation through the original UI rows, in source reading order.</summary>
internal static class RealtimePanelLines
{
    /// <param name="alignment">
    /// Which end of its source row each row's box keeps when it is wider than the row — see
    /// <see cref="AvailableWidths"/>. Null is the left end, as rows have always been set.
    /// </param>
    public static List<TranslatedBlock> Split(TranslatedBlock block, Func<string, double> measure,
        IReadOnlyList<double>? availableWidths = null, SourceAlignment? alignment = null)
    {
        if (block.SourceLineBounds is not { Count: > 1 } rows ||
            rows.Any(r => r.IsEmpty || !double.IsFinite(r.Width) || !double.IsFinite(r.Height) ||
                !double.IsFinite(r.X) || !double.IsFinite(r.Y) || r.Width <= 0 || r.Height <= 0))
            return [block];

        string text = block.TranslatedText.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var starts = StringInfo.ParseCombiningCharacters(text);
        var elements = starts.Select((start, i) =>
            text[start..(i + 1 < starts.Length ? starts[i + 1] : text.Length)]).ToArray();
        var advances = new double[elements.Length + 1];
        for (int i = 0; i < elements.Length; i++)
            advances[i + 1] = advances[i] + Math.Max(0, measure(elements[i]));

        var widths = rows.Select((r, i) => availableWidths is not null && availableWidths.Count == rows.Count &&
            double.IsFinite(availableWidths[i]) && availableWidths[i] >= r.Width ? availableWidths[i] : r.Width).ToArray();
        var boundaries = Enumerable.Range(0, elements.Length + 1)
            .Where(at => IsBoundary(elements, at)).ToArray();

        int[] Flow(double scale)
        {
            var ends = new int[rows.Count];
            int position = 0;
            for (int row = 0; row < rows.Count; row++)
            {
                double limit = advances[position] + widths[row] / scale;
                int lo = 0, hi = boundaries.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (advances[boundaries[mid]] <= limit + 0.000001) lo = mid;
                    else hi = mid - 1;
                }
                position = Math.Max(position, boundaries[lo]);
                ends[row] = position;
            }
            return ends;
        }

        // Wrap like ordinary text: fill the first available row, then continue.
        // Only shrink when the full translation cannot fit through all source rows.
        // Proportional cuts manufactured breaks even when a short translation fit.
        double fittedScale = 1;
        var cuts = Flow(1);
        if (cuts[^1] != elements.Length)
        {
            double low = 0, high = 1;
            for (int iteration = 0; iteration < 24; iteration++)
            {
                double scale = (low + high) / 2;
                var attempt = Flow(scale);
                if (attempt[^1] == elements.Length) { low = scale; fittedScale = scale; cuts = attempt; }
                else high = scale;
            }
            // Every finite text has a fitting scale. Retain all content even if an
            // extreme input falls below the binary search's numerical resolution.
            if (cuts[^1] != elements.Length)
            {
                fittedScale = Math.Min(1, widths.Min() / Math.Max(1, advances[^1]));
                cuts = Flow(fittedScale);
            }
        }

        // Avoid leaving one or two CJK glyphs alone on the final used row. Move a
        // few glyphs from the preceding row only when that row has room for them.
        int lastUsed = Array.FindLastIndex(cuts, end => end < elements.Length) + 1;
        if (lastUsed > 0 && lastUsed < cuts.Length)
        {
            int beforeLast = cuts[lastUsed - 1];
            int previousStart = lastUsed > 1 ? cuts[lastUsed - 2] : 0;
            int cjkTail = elements.Skip(beforeLast).Count(e => IsCjk(e[0]) && char.IsLetter(e, 0));
            if (cjkTail is > 0 and < 3)
            {
                var earlier = boundaries.Where(b => b > previousStart && b < beforeLast).Reverse().ToArray();
                // A nearby clause boundary is stronger evidence than an arbitrary
                // four-glyph tail; use it when the whole clause fits the final row.
                int clause = earlier.FirstOrDefault(b =>
                    "，、。！？；,:;!?".Contains(elements[b - 1][^1]) &&
                    (advances[^1] - advances[b]) * fittedScale <= widths[lastUsed]);
                if (clause > previousStart)
                    cuts[lastUsed - 1] = clause;
                else foreach (int candidate in earlier)
                {
                    if ((advances[^1] - advances[candidate]) * fittedScale > widths[lastUsed]) break;
                    if (elements.Skip(candidate).Count(e => IsCjk(e[0]) && char.IsLetter(e, 0)) >= 4)
                    {
                        cuts[lastUsed - 1] = candidate;
                        break;
                    }
                }
            }
        }

        int cut = 0;
        var result = new List<TranslatedBlock>(rows.Count);
        for (int row = 0; row < rows.Count; row++)
        {
            int end = cuts[row];
            int from = cut < starts.Length ? starts[cut] : text.Length;
            int to = end < starts.Length ? starts[end] : text.Length;
            result.Add(block with
            {
                TranslatedText = text[from..to].Trim(),
                Bounds = new Rect(alignment switch
                {
                    SourceAlignment.Right => rows[row].Right - widths[row],
                    SourceAlignment.Center => rows[row].X + rows[row].Width / 2 - widths[row] / 2,
                    _ => rows[row].X,
                }, rows[row].Y, widths[row], rows[row].Height),
                SourceLineBounds = null,
            });
            cut = end;
        }
        return result;
    }

    /// <summary>
    /// OCR bounds describe ink, not a chat column's capacity. A short wrapped tail
    /// may use the rest of its own paragraph's width, stopping before another block.
    /// </summary>
    /// <remarks>
    /// The room is on the side the rows are set from (譯文對齊, see <see cref="RealtimeSourceAlignment"/>):
    /// past the right end of a row set flush left, as before; past the left end of one set flush
    /// right; and for a centred row the same either way, as far as the nearer side allows. Where
    /// <see cref="Split"/> then places a row is that side's too.
    /// </remarks>
    public static double[] AvailableWidths(TranslatedBlock block, IReadOnlyList<TranslatedBlock> scene,
        SourceAlignment? alignment = null)
    {
        var rows = block.SourceLineBounds ?? [block.Bounds];
        return rows.Select(row =>
        {
            double right = block.Bounds.Right;
            double left = block.Bounds.Left;
            foreach (var other in scene)
            {
                if (ReferenceEquals(other, block)) continue;
                foreach (var obstacle in other.SourceLineBounds ?? [other.Bounds])
                {
                    if (obstacle.Top >= row.Bottom || obstacle.Bottom <= row.Top) continue;
                    if (obstacle.Left >= row.Right) right = Math.Min(right, obstacle.Left - 2);
                    if (obstacle.Right <= row.Left) left = Math.Max(left, obstacle.Right + 2);
                }
            }
            double centre = row.Left + row.Width / 2;
            return alignment switch
            {
                SourceAlignment.Right => Math.Max(row.Width, row.Right - left),
                SourceAlignment.Center => Math.Max(row.Width, 2 * Math.Min(centre - left, right - centre)),
                _ => Math.Max(row.Width, right - row.Left),
            };
        }).ToArray();
    }

    private static bool IsBoundary(string[] elements, int at)
    {
        if (at == 0 || at == elements.Length) return true;
        int previous = at - 1, next = at;
        while (previous > 0 && string.IsNullOrWhiteSpace(elements[previous])) previous--;
        while (next < elements.Length - 1 && string.IsNullOrWhiteSpace(elements[next])) next++;
        string before = elements[previous], after = elements[next];
        if ("」』）】》〉、，。！？：；)]},.!?:;".Contains(after[0]) ||
            "「『（【《〈([{".Contains(before[^1]))
            return false;
        if (string.IsNullOrWhiteSpace(elements[at - 1]) || string.IsNullOrWhiteSpace(elements[at])) return true;
        // CJK permits breaks between glyphs; Latin/Hangul words and numbers stay whole.
        return IsCjk(before[0]) || IsCjk(after[0]) ||
            (!char.IsLetterOrDigit(before, 0) && !char.IsLetterOrDigit(after, 0));
    }

    private static bool IsCjk(char c) =>
        c is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}
