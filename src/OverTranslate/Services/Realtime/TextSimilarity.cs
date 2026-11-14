using System.Text;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Decides whether two recognitions of the same region are showing the same words.
/// </summary>
/// <remarks>
/// Recognition is not deterministic across frames. Reading the same unchanged subtitle repeatedly
/// returned 222, 227, 222 and 230 characters in one measured session — a glyph on a boundary,
/// antialiasing that shifted as something moved behind it, a stray mark picked up and dropped again.
/// Compared with <c>==</c>, every one of those reads as new text: it is translated again and the
/// overlay is rebuilt, which the reader sees as the line flickering while it says the same thing.
///
/// So a small difference over a long line is treated as the same line. The tolerance is proportional
/// because that is how the noise behaves — a few characters in two hundred — and it is withheld
/// entirely from short text, where a couple of characters is not noise but the whole meaning
/// ("HP 100" against "HP 190", "Yes" against "No").
/// </remarks>
internal static class TextSimilarity
{
    /// <summary>
    /// Below this length every character is compared exactly. Short strings carry too much meaning
    /// per character to spend any of it on tolerance.
    /// </summary>
    public const int MinLengthForTolerance = 12;

    /// <summary>
    /// The share of a line that may differ and still count as the same line. Comfortably above the
    /// couple of percent that recognition noise produces, and far below what replacing the words
    /// would produce.
    /// </summary>
    public const double MaxDifferenceRatio = 0.1;

    /// <summary>
    /// How far two readings may diverge and still be the same sentence read twice rather than two
    /// different sentences.
    /// </summary>
    /// <remarks>
    /// Measured over 69 consecutive reads of a live subtitle track. The distribution is strongly
    /// two-humped: 22 pairs under 10% (the same reading, which <see cref="IsSameContent"/> already
    /// absorbs), a handful between 10% and 30% — every one of them the same sentence read
    /// differently ("Where haveyongone!" against "Where have you gone?!") — and 42 pairs above 50%,
    /// every one a real change of line. <b>No pair of genuinely different sentences came in under
    /// 50%, at any length</b>, so anything up to 30% would have been safe on this sample.
    ///
    /// Set at 20% on that first sample and raised to 30% on a much larger one: 693 consecutive
    /// translations from live sessions, with every pair between 20% and 50% read by hand. All 73 of
    /// them were one line read twice — "heard a bunch of experts are there to study it." against
    /// "Mheard a bunchc are there to study it.", the same player UI re-read a dozen ways — and not
    /// one was a real change of line. Raising the bound to 30% catches 52 of them against 32 at
    /// 20%, with nothing wrongly caught at either.
    ///
    /// It stops at 30% because past 40% the pairs stop being one line. There the region holds a
    /// player's furniture as well as its subtitles, and a reading that found a new subtitle differs
    /// from one that missed it by about that much — treating those as the same line would hold the
    /// old translation on screen and lose the new subtitle, which is the failure this whole feature
    /// exists to prevent, and it is far more expensive than a needless repaint.
    ///
    /// Not scaled further by length, though the temptation is real. A proportional bound already
    /// tightens with length on its own — 3 characters at the 12-character floor against 12 at 40 —
    /// and every additional narrowing tried against the sample removed a true re-reading before it
    /// removed any false one, starting with a 21-character line that differed by 19%.
    ///
    /// Short text is kept out entirely by <see cref="MinLengthForTolerance"/>, and stays out even
    /// though it churns the most: 86 of the sample's pairs were under 12 characters, "Yay!" read as
    /// "Yay" and "Yaya" and "Yav!" over and over. No ratio can separate those from "Yes" against
    /// "No", so they are a job for not recognising rubbish in the first place, not for tolerance.
    /// </remarks>
    public const double MaxRereadDifferenceRatio = 0.3;

    /// <summary>
    /// How much longer a reading has to be, against what is on screen, before the extra characters
    /// are the sentence still being drawn rather than recognition wobbling at its end.
    /// </summary>
    /// <remarks>
    /// The two look alike from here — both are "the same line, a bit longer" — so the only thing
    /// separating them is how much longer. Recognition's own wobble at the end of a line is a
    /// character or three: <c>"guitr."</c> against <c>"guitar..."</c> is three in thirty-seven, and
    /// <see cref="MaxDifferenceRatio"/> is set at 10% precisely because that is where noise lives. A
    /// line that is still animating in is missing a clause, not a character.
    ///
    /// So this sits above the noise band rather than at it, and is bounded at both ends.
    ///
    /// It is bounded from above by pairing: a reading more than <see cref="MaxRereadDifferenceRatio"/>
    /// away from the shown line is never paired with it — it is simply a new line, drawn on its own
    /// terms — which puts a ceiling near 43% of the shown length on anything this could ever see.
    /// 15% leaves that band usable, and the case this exists for (a line drawn to about four fifths
    /// before it was read, so a quarter of its length still to come) sits in the middle of it.
    /// </remarks>
    public const double MinGrowthRatio = 0.15;

    /// <summary>
    /// The fewest characters that can ever count as growth, whatever <see cref="MinGrowthRatio"/>
    /// works out to on a short line.
    /// </summary>
    /// <remarks>
    /// This is the side of the trade that has to be accepted rather than solved: a line whose last
    /// read caught it this close to finished keeps its truncated ending, because three characters
    /// appearing at the end of a line is exactly what recognition noise looks like and there is no
    /// signal separating the two. Chosen against what noise actually produces — a stray
    /// <c>"This is nice!ブハ"</c>, a full stop read as an ellipsis — and against what is lost by
    /// being wrong the other way, which is a word ending rather than a clause. The complaint this
    /// whole rule answers is a line read at four fifths; that is nowhere near here.
    /// </remarks>
    public const int MinGrowthChars = 4;

    /// <summary>
    /// The most that can ever be demanded, however long the shown line is.
    /// </summary>
    /// <remarks>
    /// Without it the proportional rule scales the wrong way on both counts: a two-hundred-character
    /// line would keep its last thirty characters truncated, and reaching that line's full length
    /// four characters at a time is not the alternative either — everything between 70% and 100% of
    /// it is paired, so a small threshold there is a dozen redraws and a dozen translations for one
    /// subtitle. Capped, no line takes more than about five steps to finish arriving, and none
    /// loses more than a couple of words to the floor.
    /// </remarks>
    public const int MaxGrowthChars = 12;

    /// <summary>
    /// Whether <paramref name="grown"/> is <paramref name="shown"/> with more of the same sentence
    /// after it — one line caught mid-animation and then read again once it had finished.
    /// </summary>
    /// <remarks>
    /// Dialogue rarely arrives all at once. A visual novel types its line out, a subtitle track
    /// fades one in, a game reveals a clause at a time — and a poll landing part way through reads
    /// four fifths of a sentence perfectly, because what is on screen at that instant really is
    /// four fifths of a sentence. Everything downstream then agrees it is the same line
    /// (<see cref="IsSameSentence"/>) read no better (the animation adds words, not certainty), so
    /// the finished sentence is turned down and the truncated one stays up for as long as the line
    /// is on screen — the reader loses the end of the line, which is usually where its meaning is.
    ///
    /// The score cannot arbitrate that, because it is not answering the question: it says how
    /// confident the recogniser is in the characters it read, and it is entirely right to be
    /// confident about a half-drawn line. What tells the two apart is the shape of the difference.
    /// A re-reading disagrees <em>within</em> the line; a line still being drawn agrees with all of
    /// it and continues past the end.
    /// </remarks>
    public static bool IsContinuationOf(string grown, string shown)
    {
        var longer = NormaliseWhitespace(grown);
        var start = NormaliseWhitespace(shown);

        if (start.Length == 0) return false;

        // The shown line has to still be there at the front of this one. Short lines are compared
        // exactly, as everywhere else. Longer ones are compared with a tolerance, because the last
        // glyph of a line mid-animation is a half-drawn one — read as something else, or not at all
        // — and against SOME prefix of the new reading rather than the one of exactly the shown
        // length: punctuation earlier in the line is routinely read at a different length from one
        // pass to the next (an ellipsis read as "." and then as "..."), and cutting at the shown
        // length then misaligns everything after it. Measured on the typing-animation stage: a
        // two-line sentence whose first line ended in "……" grew by its whole second line (7
        // characters) and was held for five seconds on that one ellipsis.
        var matched = start.Length < MinLengthForTolerance
            ? (longer.StartsWith(start, StringComparison.Ordinal) ? start.Length : -1)
            : BestMatchingPrefix(start, longer,
                Math.Max(MinPrefixDifference, (int)(start.Length * MaxPrefixDifferenceRatio)));
        if (matched < 0) return false;

        // Growth is what follows the part that matched, not the difference in length: two
        // characters gained by the ellipsis earlier in the line are not the sentence growing.
        var required = Math.Clamp(
            (int)Math.Ceiling(start.Length * MinGrowthRatio), MinGrowthChars, MaxGrowthChars);
        return longer.Length - matched >= required;
    }

    /// <summary>
    /// How far the shown line may differ from the front of a longer reading and still be its
    /// beginning, as a share of the shown line's length — see <see cref="IsContinuationOf"/>.
    /// </summary>
    /// <remarks>
    /// Above <see cref="MaxDifferenceRatio"/> because the prefix is where both kinds of wobble meet:
    /// the ordinary few percent of misreadings, plus a run of punctuation read at another length
    /// (two characters for "." against "..."). Still far below <see cref="MaxRereadDifferenceRatio"/>,
    /// and growth past the matched prefix has to clear <see cref="MinGrowthChars"/> on top of it.
    /// </remarks>
    public const double MaxPrefixDifferenceRatio = 0.15;

    /// <summary>The floor under <see cref="MaxPrefixDifferenceRatio"/>: one ellipsis read two ways.</summary>
    public const int MinPrefixDifference = 2;

    /// <summary>
    /// The length of the prefix of <paramref name="longer"/> closest to <paramref name="start"/> by
    /// edit distance, if that distance is within <paramref name="allowed"/>; -1 otherwise. Ties go to
    /// the longest such prefix, so growth past it is never overstated.
    /// </summary>
    private static int BestMatchingPrefix(string start, string longer, int allowed)
    {
        // Rows over the shown line, columns over prefixes of the longer reading; the last row is the
        // distance from the whole shown line to every prefix, and its minimum is the answer.
        var previous = new int[longer.Length + 1];
        var current = new int[longer.Length + 1];
        for (int j = 0; j <= longer.Length; j++) previous[j] = j;

        for (int i = 1; i <= start.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= longer.Length; j++)
            {
                var substitution = previous[j - 1] + (start[i - 1] == longer[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        int best = -1;
        int bestDistance = allowed + 1;
        for (int j = 0; j <= longer.Length; j++)
        {
            if (previous[j] <= bestDistance && previous[j] <= allowed)
            {
                bestDistance = previous[j];
                best = j;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether these are two readings of one sentence rather than two sentences — close enough to
    /// be the same words, too far apart to be the same reading of them.
    /// </summary>
    /// <remarks>
    /// Recognition of an unchanged subtitle wobbles by more than <see cref="IsSameContent"/>
    /// tolerates surprisingly often: a lost space, a run of i/l confusions, a fragment ordered
    /// differently. Treated as a new sentence, each of those replaces a correct translation with a
    /// worse one — which is the reader's whole experience of "the OCR is unreliable", because the
    /// overlay shows the newest reading rather than the best one.
    /// </remarks>
    public static bool IsSameSentence(string a, string b)
    {
        if (IsSameContent(a, b)) return true;

        var left = NormaliseWhitespace(a);
        var right = NormaliseWhitespace(b);
        if (left.Length == 0 || right.Length == 0) return false;

        var longest = Math.Max(left.Length, right.Length);
        if (longest < MinLengthForTolerance) return false;

        var allowed = (int)(longest * MaxRereadDifferenceRatio);
        if (allowed < 1) return false;
        if (Math.Abs(left.Length - right.Length) > allowed) return false;

        return EditDistanceWithin(left, right, allowed);
    }

    /// <summary>
    /// Whether two readings say the very same thing, spacing aside — the one comparison here with no
    /// tolerance in it at all.
    /// </summary>
    /// <remarks>
    /// What <see cref="RealtimeReadingMerge"/> asks to decide whether a line has anything new in it.
    /// The tolerant comparisons cannot answer that: <c>"weird guitr."</c> and <c>"weird guitar..."</c>
    /// are three characters apart in forty, well inside what <see cref="IsSameContent"/> forgives,
    /// and they are also the difference between the reader getting the sentence and not. Tolerance is
    /// for deciding whether to pay for a translation and whether two readings are of one sentence;
    /// it is not for deciding whether the better of two readings of that sentence is worth showing.
    /// </remarks>
    public static bool IsSameWording(string a, string b) =>
        ReferenceEquals(a, b) || NormaliseWhitespace(a) == NormaliseWhitespace(b);

    public static bool IsSameContent(string a, string b)
    {
        if (ReferenceEquals(a, b)) return true;

        // Spacing is where recognition wobbles most, and it is also what carries the least meaning,
        // so it is normalised away before anything is measured.
        var left = NormaliseWhitespace(a);
        var right = NormaliseWhitespace(b);

        if (left == right) return true;
        if (left.Length == 0 || right.Length == 0) return false;

        var longest = Math.Max(left.Length, right.Length);
        if (longest < MinLengthForTolerance) return false;

        var allowed = Math.Max(1, (int)(longest * MaxDifferenceRatio));

        // Two strings whose lengths already differ by more than the budget cannot be within it, and
        // this is much cheaper than finding out the long way.
        if (Math.Abs(left.Length - right.Length) > allowed) return false;

        return EditDistanceWithin(left, right, allowed);
    }

    /// <summary>
    /// Levenshtein distance, answered only as "within <paramref name="allowed"/> or not" so the
    /// computation can stop as soon as the answer is no.
    /// </summary>
    private static bool EditDistanceWithin(string a, string b, int allowed)
    {
        // Two rows rather than the full matrix: nothing here needs the path, only the distance.
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var bestInRow = current[0];

            for (int j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;

                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
                bestInRow = Math.Min(bestInRow, current[j]);
            }

            // Every remaining path runs through this row, so once its best cell is over budget the
            // final distance must be too.
            if (bestInRow > allowed) return false;

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= allowed;
    }

    private static string NormaliseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && builder.Length > 0) builder.Append(' ');
            inWhitespace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
}
