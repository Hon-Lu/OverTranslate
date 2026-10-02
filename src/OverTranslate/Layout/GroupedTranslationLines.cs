using System.Windows;
using OverTranslate.Services;

namespace OverTranslate.Layout;

/// <summary>
/// Puts a group's translation back onto the source lines it was read from, so that grouping decides
/// what the translator is asked and not where the answer is drawn.
/// </summary>
/// <remarks>
/// <para>Grouping exists so a wrapped sentence reaches the translator whole. Drawing it whole is a
/// separate question and the answer is no: one bubble over the union of a group's lines is a bubble
/// the size of the paragraph, and where each line began and how the block sat against the picture
/// behind it are both gone. Split back onto the original boxes, the layout the user framed survives
/// and the translator is still asked a whole sentence.</para>
///
/// <para>There is no line-for-line correspondence between two languages and this does not pretend
/// there is. Word order moves, so "you would actually" has no counterpart to go looking for in the
/// Chinese. What gets distributed is length: each line receives the share of the translation that
/// its own box is wide, cut at a space where the target language has them. The text then reads
/// straight through, line after line, in the shape the source had.</para>
/// </remarks>
internal static class GroupedTranslationLines
{
    /// <summary>
    /// How far from the proportional cut a better break may be looked for, as a share of one
    /// line's worth of text. Wide enough to reach the nearest word boundary in ordinary prose,
    /// narrow enough that a line cannot take over its neighbour's share.
    /// </summary>
    private const double BreakSearchWindow = 0.35;

    public static List<TranslatedBlock> SplitOntoSourceLines(IReadOnlyList<TranslatedBlock> blocks)
    {
        var split = new List<TranslatedBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            // A group off a tilted card is drawn whole, along the card — see Ocr.TiltedText. Its
            // source lines are upright boxes that overlap one another, and a piece put back on each
            // would be a level line laid across its neighbours.
            if (block.SourceLineBounds is { Count: > 1 } lines && block.Tilt is null)
                split.AddRange(SplitOntoSourceLines(block, lines));
            else
                split.Add(block);
        }

        return split;
    }

    private static IEnumerable<TranslatedBlock> SplitOntoSourceLines(
        TranslatedBlock block, IReadOnlyList<Rect> lines)
    {
        var segments = Distribute(block.TranslatedText.Trim(), lines);

        return lines.Select((bounds, index) => block with
        {
            TranslatedText = segments[index],
            Bounds = bounds,

            // Cleared, so each of these is laid out as the single line it now is rather than as a
            // group. OriginalText stays the group's: its one reader asks only whether the source
            // carried a line break, and neither a group nor a line of one ever does.
            SourceLineBounds = null,
        });
    }

    /// <summary>
    /// Cuts the translation into one piece per source line, each sized to that line's share of the
    /// group's width.
    /// </summary>
    /// <remarks>
    /// Width rather than character count because width is what the piece has to fit into. Lines of
    /// one group are within 12% of each other in text size — the grouper will not join lines that
    /// are not — so a box twice as wide holds about twice the text, and sharing the translation out
    /// by width leaves every line needing about the same font size to fit. Sharing it out by source
    /// characters would say almost the same thing about ordinary prose and the wrong thing about a
    /// line that happens to be mostly spaces.
    /// </remarks>
    private static List<string> Distribute(string text, IReadOnlyList<Rect> lines)
    {
        var segments = new List<string>(lines.Count);
        if (text.Length == 0)
        {
            segments.AddRange(Enumerable.Repeat(string.Empty, lines.Count));
            return segments;
        }

        var widths = lines.Select(line => Math.Max(1, line.Width)).ToList();
        var total = widths.Sum();
        var window = Math.Max(2, (int)(text.Length / (double)lines.Count * BreakSearchWindow));

        var cut = 0;
        double consumed = 0;

        for (var index = 0; index < lines.Count - 1; index++)
        {
            consumed += widths[index];
            var ideal = (int)Math.Round(text.Length * consumed / total);
            var next = Math.Clamp(BreakNear(text, ideal, window, cut), cut, text.Length);
            segments.Add(text[cut..next].Trim());
            cut = next;
        }

        segments.Add(text[cut..].Trim());
        return segments;
    }

    /// <summary>
    /// The best place to cut near <paramref name="ideal"/>: a word boundary if the target language
    /// writes them, otherwise anywhere that does not strand punctuation on the wrong line.
    /// </summary>
    private static int BreakNear(string text, int ideal, int window, int floor)
    {
        for (var offset = 0; offset <= window; offset++)
        {
            if (IsWordBoundary(text, ideal + offset, floor)) return ideal + offset;
            if (IsWordBoundary(text, ideal - offset, floor)) return ideal - offset;
        }

        // Nothing to find in a script that does not space its words, which is most of what this
        // app translates into. Cutting between any two characters is ordinary there; cutting a
        // closing bracket onto the next line is not.
        for (var offset = 0; offset <= window; offset++)
        {
            if (IsCleanBreak(text, ideal + offset, floor)) return ideal + offset;
            if (IsCleanBreak(text, ideal - offset, floor)) return ideal - offset;
        }

        // Neither pass found anywhere better, so the cut lands where the proportion put it — but
        // never between the two halves of a surrogate pair, which would leave a lone surrogate at
        // the end of one line and another at the start of the next.
        return ideal > floor && ideal < text.Length && char.IsLowSurrogate(text[ideal])
            ? ideal - 1
            : ideal;
    }

    private static bool IsWordBoundary(string text, int at, int floor) =>
        IsInside(text, at, floor) && (char.IsWhiteSpace(text[at]) || char.IsWhiteSpace(text[at - 1]));

    private static bool IsCleanBreak(string text, int at, int floor) =>
        IsInside(text, at, floor) &&
        !char.IsLowSurrogate(text[at]) &&
        !StartsNoLine(text[at]) &&
        !EndsNoLine(text[at - 1]);

    private static bool IsInside(string text, int at, int floor) => at > floor && at < text.Length;

    // Punctuation that closes or trails, so it belongs at the end of the line before it.
    private static bool StartsNoLine(char character) =>
        character is '」' or '』' or '）' or '】' or '》' or '〉' or '、' or '，' or '。' or '！' or '？' or
                     '：' or '；' or ')' or ']' or '}' or ',' or '.' or '!' or '?' or ':' or ';';

    // Punctuation that opens, so it belongs at the start of the line after it.
    private static bool EndsNoLine(char character) =>
        character is '「' or '『' or '（' or '【' or '《' or '〈' or '(' or '[' or '{';
}
