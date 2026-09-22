using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Drops a column that is a second detection of writing another column already holds.
/// </summary>
/// <remarks>
/// <para>The detector reads one piece of writing twice. Its boxes come out of a probability map
/// that is thresholded and then expanded, and on a short piece of writing surrounded by white the
/// expansion can leave a small box sitting entirely inside a larger one, each read on its own and
/// both kept. MEASURED over the 15 comic pages and the 17 frames the app captured, the same page
/// gives <c>の</c> twice inside <c>他の</c>, <c>別の魔獣が</c> inside a second box reading
/// <c>別の魔獣が</c>, and <c>の知らない人…</c> inside <c>私の知らないん人</c>. Grouping then puts
/// both into the balloon, in reading order, and the reader is shown
/// <c>のの他のパーティか…</c> and <c>私の知らないん人の知らない人…</c>.</para>
///
/// <para>WHAT THIS MUST NOT TOUCH IS RUBY, and that is most of what sits inside another box: a
/// reading is set hard against its kanji, so the writing's expanded box swallows it. Over the same
/// corpora 62 boxes lie inside another one and 38 of them are readings — <c>えん</c> inside
/// <c>支援魔術【力上昇】！</c>, <c>あつか</c> inside <c>支援魔術を扱う</c>, <c>とき</c> inside
/// <c>いざって時に仲間を</c>. Position alone would throw every one of them away, and
/// <see cref="VerticalRubyColumns"/> is where that decision belongs.</para>
///
/// <para>So the test is on the TEXT. A second reading of the same writing says the same thing; a
/// reading says a pronunciation, which shares no characters with the kanji it is set against. Every
/// ruby pair on the corpora scores 0 here, and every duplicate scores 0.67 or above.</para>
/// </remarks>
internal static class VerticalRepeatedColumns
{
    /// <summary>How much of the smaller box has to lie inside the larger one.</summary>
    /// <remarks>
    /// Not a full containment test, because the two boxes come from separate expansions of the same
    /// blob and the smaller one can stand a pixel or two proud of the larger.
    /// </remarks>
    private const double Inside = 0.8;

    /// <summary>How much of the shorter reading the two have to share to be the same writing.</summary>
    /// <remarks>
    /// The gap either side of it is wide: the duplicates measured run 0.67 to 1.00 and every ruby
    /// pair inside another box runs 0.00, because a pronunciation spelled in kana has nothing in
    /// common with the kanji it annotates. The one pair in between is <c>いっと</c> inside
    /// <c>ずっと一緒に</c> at 0.67, which is a second detection of っと and is meant to go.
    /// </remarks>
    private const double SaysTheSame = 0.6;

    internal static List<OcrTextBlock> Drop(List<OcrTextBlock> columns)
    {
        if (columns.Count < 2) return columns;

        var repeated = new bool[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        for (var j = 0; j < columns.Count; j++)
        {
            if (i == j || repeated[i] || repeated[j]) continue;
            if (!Within(columns[i].LayoutBounds, columns[j].LayoutBounds)) continue;
            if (!SaysWhatItSays(columns[i].Text, columns[j].Text)) continue;

            // The one that says LESS goes. Which of the two is the smaller box does not decide it:
            // 比べて is framed inside a taller box that came back holding only て.
            repeated[SaysLess(columns[i], columns[j]) ? i : j] = true;
        }

        return repeated.Any(gone => gone)
            ? [.. columns.Where((_, i) => !repeated[i])]
            : columns;
    }

    private static bool Within(Rect inner, Rect outer)
    {
        var shared = Rect.Intersect(inner, outer);
        if (shared.IsEmpty) return false;

        var area = inner.Width * inner.Height;
        return area > 0 && shared.Width * shared.Height / area >= Inside;
    }

    private static bool SaysWhatItSays(string one, string other)
    {
        var a = Squeeze(one);
        var b = Squeeze(other);
        var shorter = Math.Min(a.Length, b.Length);
        return shorter > 0 && SharedRun(a, b) >= shorter * SaysTheSame;
    }

    /// <summary>Which of two readings of the same writing to let go: the one holding less of it.</summary>
    private static bool SaysLess(OcrTextBlock mine, OcrTextBlock theirs)
    {
        var characters = Squeeze(mine.Text).Length.CompareTo(Squeeze(theirs.Text).Length);
        if (characters != 0) return characters < 0;

        // Same number of characters, so it is the recogniser's own word against itself.
        return (mine.Confidence ?? 0) < (theirs.Confidence ?? 0);
    }

    private static string Squeeze(string text) =>
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
