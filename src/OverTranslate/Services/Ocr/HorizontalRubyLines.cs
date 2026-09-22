using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Takes the readings printed above the writing they annotate out of a horizontal page, before
/// grouping sees them.
/// </summary>
/// <remarks>
/// <para>A DIFFERENT PROBLEM FROM THE VERTICAL ONE, and worth saying so first because the code
/// looks alike. <see cref="VerticalRubyColumns"/> exists because a reading column was being MERGED
/// INTO a sentence, which puts a wrong word inside the text handed to the translator. That does not
/// happen across the page: a reading is about half the size of its base line, so the grouper's size
/// gate refuses it long before any geometry is consulted. MEASURED on the three pages under
/// <c>.ai/test-images/ja-ruby</c>, every base line came back whole and correct, with the readings
/// beside them as groups of their own.</para>
///
/// <para>What they cost instead is the screen and the budget. The textbook page reads 16 lines and
/// sends 15 groups to be translated, of which ten are readings — ten requests spent on kana that
/// says nothing the base line does not, and ten bubbles drawn over the page the user is trying to
/// read. There is a quieter cost too: a reading sits in the leading BETWEEN two lines of a
/// paragraph, which is exactly where <c>OcrTextBlockGrouper.NothingLiesBetween</c> looks, so a
/// reading can stop the two lines it sits between from joining.</para>
///
/// <para>THE ACTION IS ONLY EVER TO DROP, which is the other difference. The vertical rule has a
/// second, gentler verdict — set the column aside, keep it out of the sentence, still draw it — and
/// that verdict is already what happens here without anyone asking for it. So there is nothing
/// between doing nothing and dropping, and the bar for dropping is therefore the bar for losing
/// text the user wanted. It is set where nothing measured comes near it, rather than where the last
/// reading is caught.</para>
///
/// <para>MEASURED, over the three ruby pages and the 374 real captures under
/// <c>.ai/test-images</c>: 24 pairs in the whole corpus satisfy the geometry below. Twenty-one are
/// the ruby on those three pages and run from 0.36 to 0.66 of their base line's box height; two are
/// Wikipedia's reading of its own title, にほんご over 日本語, which is the same thing on a real
/// page; and then there is nothing at all until 0.89, which is that same pair rendered at 1.0
/// scale. <b>No pair that is not a reading appears anywhere in the corpus.</b> The two shapes that
/// come closest are both refused, and by different tests — see <see cref="IsHiraganaOnly"/> and
/// <see cref="AgainstTheWriting"/>.</para>
/// </remarks>
internal static class HorizontalRubyLines
{
    /// <summary>
    /// How tall a reading's box may be against the box of the line it annotates.
    /// </summary>
    /// <remarks>
    /// <para>BOX HEIGHT, not <see cref="VerticalRubyColumns"/>'s glyph size. That one takes the
    /// narrower of the box's width-per-character and its cross measure, which is right for a column
    /// and wrong here: the detector reads several readings of one line as a single box spanning the
    /// space between them, so width-per-character comes back too large and the reading looks nearly
    /// full size. きょうがっこう over 今日は学校の図書館で… measures 0.78 that way and 0.65 on the
    /// box, and the box is the honest number — across the page a line's box height IS its glyph
    /// size plus the same unclip expansion on both.</para>
    ///
    /// <para>0.75 sits in the empty band measured above, between the largest reading at 0.66 and
    /// the next candidate of any kind at 0.89. It is deliberately not tightened onto 0.66: ruby is
    /// set at half the base size by every renderer that sets it, so what puts a reading at 0.66
    /// rather than 0.50 is the detector's expansion, which grows a small box proportionally more —
    /// and how much of the box that expansion is depends on how large the text is on screen, not on
    /// the page.</para>
    /// </remarks>
    private const double SmallerThanTheWriting = 0.75;

    /// <summary>
    /// How far into its base line a reading's box may reach, as a fraction of that line's height.
    /// </summary>
    /// <remarks>
    /// <para>Ruby carries no leading of its own — it is set hard on top of the base line's ascent —
    /// so once the detector has grown both boxes the two OVERLAP. Every reading measured does:
    /// they reach 0.05 to 0.26 of the base line's height into it, and not one leaves clear space. A
    /// line that is merely stacked above another does leave it, which is what refuses a game
    /// panel's 「しゅとく」 standing over 「第三階層 西の祭壇」 at +0.40, and a poster's
    /// 「エンディングテーマ」 over a song title at +0.03.</para>
    ///
    /// <para>So there is nothing to choose above zero: the boxes must touch. This is the limit
    /// below it, and it keeps out a small run of kana lying WITHIN a box rather than on top of it —
    /// a caption inside a panel the detector framed generously. The deepest real reading reaches
    /// 0.26.</para>
    /// </remarks>
    private const double AgainstTheWriting = 0.45;

    /// <summary>
    /// How much of the reading has to lie within the horizontal span of its base line.
    /// </summary>
    /// <remarks>
    /// A reading annotates a word inside the line, so it lies within it — usually far within. The
    /// same figure as the vertical rule uses along its own axis, and it is not the test doing the
    /// work here; it is what stops a reading being attached to a line in the next column.
    /// </remarks>
    private const double AlongsideTheWriting = 0.85;

    /// <summary>The page with its readings removed, and their room given to the lines they read.</summary>
    /// <remarks>
    /// The room is <see cref="OcrTextBlock.Bounds"/> only, which is the rectangle the overlay
    /// covers, so the translation is drawn over the reading rather than leaving it showing above the
    /// band. <see cref="OcrTextBlock.LayoutBounds"/> is deliberately left alone: that is what
    /// grouping measures leading and alignment with, and growing a line's box upward by half a line
    /// would shrink every ratio taken against it. The vertical rule unions both because there the
    /// reading sits BESIDE the writing and removing it really does move the columns apart; here it
    /// sits in the leading, where nothing is measured.
    /// </remarks>
    internal static List<OcrTextBlock> Drop(List<OcrTextBlock> lines)
    {
        if (lines.Count < 2) return lines;

        // Which line each one is a reading of, or -1 to leave it on the page. The nearest one it
        // could be a reading of, which is the one directly under it.
        var annotates = new int[lines.Count];
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            annotates[i] = -1;
            // Asked once rather than inside the loop: it is the cheapest of the tests and the one
            // that refuses nearly every line, so it is what keeps this from scanning each line's
            // text once per other line on the page.
            if (!IsHiraganaOnly(lines[i].Text)) continue;

            for (var j = 0; j < lines.Count; j++)
            {
                if (i == j || !IsReadingOf(lines[i], lines[j])) continue;
                annotates[i] = Nearer(lines, annotates[i], j);
                found = true;
            }
        }

        if (!found) return lines;

        var bounds = lines.Select(line => line.Bounds).ToArray();
        for (var i = 0; i < lines.Count; i++)
            if (annotates[i] >= 0)
                bounds[annotates[i]] = Rect.Union(bounds[annotates[i]], lines[i].Bounds);

        var writing = new List<OcrTextBlock>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            if (annotates[i] < 0)
                writing.Add(lines[i] with { Bounds = bounds[i] });

        return writing;
    }

    /// <summary>Of two lines this could be a reading of, the one directly beneath it.</summary>
    private static int Nearer(List<OcrTextBlock> lines, int chosen, int candidate) =>
        chosen < 0 || lines[candidate].LayoutBounds.Top < lines[chosen].LayoutBounds.Top
            ? candidate
            : chosen;

    /// <remarks>The reading has already been checked; see the loop in <see cref="Drop"/>.</remarks>
    private static bool IsReadingOf(OcrTextBlock reading, OcrTextBlock writing)
    {
        if (!HoldsKanji(writing.Text))
            return false;

        Rect a = reading.LayoutBounds, b = writing.LayoutBounds;

        // The writing runs across the page. Asked of the base line and not of the reading: a
        // one-character reading is as tall as it is wide, and 「よ」 over a sentence is the
        // commonest shape there is.
        if (a.Width <= 0 || a.Height <= 0 || b.Height <= 0 || b.Width <= b.Height)
            return false;

        if (a.Height > b.Height * SmallerThanTheWriting)
            return false;

        // Above it, and touching: see AgainstTheWriting for why there is no slack above zero.
        var gap = b.Top - a.Bottom;
        if (gap > 0 || gap < -b.Height * AgainstTheWriting)
            return false;

        var shared = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return shared >= a.Width * AlongsideTheWriting;
    }

    /// <summary>
    /// Whether a line is nothing but hiragana, which is what a reading is written in.
    /// </summary>
    /// <remarks>
    /// <para>NARROWER THAN THE VERTICAL RULE, which takes katakana too, and the difference is the
    /// verdict rather than the language. Katakana ruby is real — a game or a comic glosses 魔術 as
    /// マジック — but katakana standing over kanji is far more often a label: a poster's
    /// 「エンディングテーマ」 over the song title beneath it, a site's 「ハッカソン」 over an event
    /// name. Both are in the corpus, and at any setting that takes katakana both are thrown away.
    /// The vertical rule can afford katakana because its doubtful verdict merely sets a column
    /// aside; this one only deletes, and a katakana reading left on screen is the behaviour the app
    /// has today.</para>
    ///
    /// <para>The prolonged sound mark, the middle dot and the iteration marks are set inside
    /// readings and are taken with them. They are not enough on their own — a line has to hold at
    /// least one kana.</para>
    /// </remarks>
    private static bool IsHiraganaOnly(string text)
    {
        var any = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            if (character is >= 'ぁ' and <= 'ゖ') { any = true; continue; }
            if (character is 'ー' or '・' or '゛' or '゜' or 'ゝ' or 'ゞ') continue;
            return false;
        }

        return any;
    }

    private static bool HoldsKanji(string text) =>
        text.Any(character => character is >= '一' and <= '鿿' or >= '㐀' and <= '䶿');
}
