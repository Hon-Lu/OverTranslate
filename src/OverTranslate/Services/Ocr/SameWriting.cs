namespace OverTranslate.Services.Ocr;

/// <summary>
/// Whether two readings are of the same writing, asked of what they say rather than where they are.
/// </summary>
/// <remarks>
/// <para>Two stages need this and neither can get it from the rectangles.
/// <see cref="TurnedFrameDetection"/> asks it of a column found on the turned frame against what the
/// upright pass read, because the two passes frame the same column differently and the boxes overlap
/// by much the same share whether the column is new or not.
/// <see cref="VerticalRepeatedColumns"/> asks it of a box sitting inside another, because most of
/// what sits inside another box is ruby and position alone would take the lot.</para>
///
/// <para>The run is the longest sequence of characters the two hold IN ORDER, against the shorter of
/// them. Order matters: two readings of one column differ by dropped and mis-read glyphs, not by
/// rearranged ones.</para>
/// </remarks>
internal static class SameWriting
{
    /// <summary>
    /// How much of the shorter reading the two have to share before they are one piece of writing.
    /// </summary>
    /// <remarks>
    /// The same number for both callers, and both measured it with room to spare either side. On
    /// the repeated columns the duplicates run 0.67 to 1.00 and every reading sitting inside another
    /// box runs 0.00, because a pronunciation spelled in kana shares nothing with the kanji it
    /// annotates. On the turned frame a column the upright pass had already read comes back saying
    /// nearly all of it again, while the balloon head that pass exists to recover says something
    /// else entirely.
    /// </remarks>
    private const double EnoughOfIt = 0.6;

    /// <summary>Whether two readings say the same thing. Whitespace is ignored on both.</summary>
    internal static bool SaysTheSame(string one, string other)
    {
        var a = Squeeze(one);
        var b = Squeeze(other);
        var shorter = Math.Min(a.Length, b.Length);
        return shorter > 0 && SharedRun(a, b) >= shorter * EnoughOfIt;
    }

    /// <summary>A reading with its whitespace taken out, which is how its length is counted.</summary>
    internal static string Squeeze(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    private static int SharedRun(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        foreach (var left in a)
        {
            for (var j = 0; j < b.Length; j++)
                current[j + 1] = left == b[j]
                    ? previous[j] + 1
                    : Math.Max(current[j], previous[j + 1]);
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
