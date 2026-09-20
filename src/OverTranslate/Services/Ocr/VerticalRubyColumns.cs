using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Drops the reading columns printed beside the writing they annotate, before grouping sees them.
/// </summary>
/// <remarks>
/// <para>A different stage from <see cref="OcrService.WithoutRuby"/> and a different failure. That
/// one runs on finished groups and stops a reading being DRAWN as a second bubble over a balloon.
/// This one runs on the detector's columns and stops a reading being MERGED INTO a sentence, which
/// is what produces <c>俺たちはあつかまじゅっ支援魔術を扱う</c> — the open half of the problem the
/// group-level rule's own remarks name. Once a reading is inside a group there is nothing left to
/// separate, so the only place to catch that kind is here.</para>
///
/// <para>Ruby in vertical Japanese is a column of its own, set at about half size, hard against the
/// right edge of the kanji it reads. Four things follow from that and all four are asked for,
/// because no one of them is enough on its own — MEASURED over the 15 comic pages in
/// <c>.ai/test-images/vertical-image-ja2</c>, where box width alone puts real dialogue
/// (<c>それに</c> at 0.83, <c>クランとは</c> at 0.87, <c>だから</c> at 0.90) in the same band as
/// readings (<c>ただはんだん</c> at 0.48, <c>たしおれじつりょく</c> at 0.52).</para>
///
/// <para>KANA ONLY, for the candidate. A reading spells out a pronunciation, so it cannot hold a
/// kanji; a misread reading that comes back holding one is simply kept, which is the safe way for
/// this to fail.</para>
///
/// <para>A KANJI IN THE WRITING. Ruby annotates kanji and nothing else, so a column of kana beside
/// another column of kana is two pieces of writing rather than a word and its reading. This is the
/// one test that spares <c>いっん</c> beside <c>やっでる</c>, a pair of mis-read fragments that
/// every other test here calls a reading.</para>
///
/// <para>SMALLER THAN THE WRITING and HARD AGAINST IT, for the geometry: half size, and no room
/// between the two. Real columns of dialogue that are narrow are narrow because the detector
/// clipped them or because they belong to a different balloon, and the ones that clear the width
/// bar sit a column's width away rather than touching.</para>
/// </remarks>
internal static class VerticalRubyColumns
{
    /// <summary>
    /// How large a reading's glyphs may be against those of the column it annotates.
    /// </summary>
    /// <remarks>
    /// GLYPH SIZE rather than box width, and the two do not rank the same material. Box width is
    /// what the detector's unclip expansion distorts most, and it distorts a narrow box far more
    /// than a wide one, so a reading beside a tightly-framed column can measure as wide as the
    /// writing itself. <see cref="GlyphSize"/> divides the box height by what was read out of it
    /// before taking the narrower of the two, which the expansion barely moves.
    /// MEASURED over the 15 comic pages: by width the readings run up to 0.72 and the first real
    /// column of dialogue is at 0.63, so the two overlap; by glyph size every reading this finds
    /// is at 0.60 or below and the first dialogue is at 0.72.
    ///
    /// The bar sits in that gap. Raising it to reach the readings above it — <c>じこしょうかい</c>
    /// at 0.86 is the nearest — would take <c>そして</c>, <c>パティに</c>, <c>ねえ</c> and
    /// <c>それに</c> with it, and a reading left in place is one wrong word in a sentence where a
    /// dropped column is the whole sentence.
    /// </remarks>
    private const double SmallerThanTheWriting = 0.62;

    /// <summary>How far a reading may sit from the writing, as a fraction of the writing's width.</summary>
    /// <remarks>
    /// Generous in both directions on purpose. The detector's unclip expansion puts the two boxes a
    /// few pixels into each other as often as it leaves a gap, so the measured spacing of a reading
    /// from its kanji runs from -8px to +20px on a column 25 to 70px wide. What this has to exclude
    /// is a column standing a full column-width away, which is the next line of the balloon.
    /// </remarks>
    private const double AgainstTheWriting = 0.5;

    /// <summary>How much of the reading has to lie alongside the writing.</summary>
    private const double AlongsideTheWriting = 0.85;

    internal static List<OcrTextBlock> Drop(List<OcrTextBlock> columns) =>
        columns.Count < 2
            ? columns
            : [.. columns.Where(column => !columns.Any(other =>
                !ReferenceEquals(other, column) && IsReadingOf(column, other)))];

    private static bool IsReadingOf(OcrTextBlock reading, OcrTextBlock writing)
    {
        if (!IsKanaOnly(reading.Text) || !HoldsKanji(writing.Text))
            return false;

        var written = GlyphSize(writing);
        if (written <= 0 || GlyphSize(reading) > written * SmallerThanTheWriting)
            return false;

        Rect a = reading.LayoutBounds, b = writing.LayoutBounds;
        if (b.Width <= 0)
            return false;

        // To the right of it, which is the side ruby is set on, and touching.
        var gap = a.Left - b.Right;
        if (gap > b.Width * AgainstTheWriting || gap < -b.Width * AgainstTheWriting)
            return false;

        var shared = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return a.Height > 0 && shared >= a.Height * AlongsideTheWriting;
    }

    /// <summary>How large one glyph of a column is: its advance down the page, or its width.</summary>
    /// <remarks>
    /// <see cref="VerticalOcrGeometry.GlyphPitch"/> deliberately does one thing more, and that one
    /// thing is wrong here. Where a box is three times as wide as its advance it takes the two to
    /// be one box over two columns and recovers the glyph size from the area — which is right for
    /// the box it was written for, and wrong for a single column whose box has swallowed the
    /// reading beside it. On 2026-09-20 19 14 57 (2).png the column 囮にされて死ぬかもしれない is
    /// framed 78px wide around 25px glyphs, and the area rule calls them 45px; measured against
    /// that, the full-size column そのうえ standing beside it looks like a reading of it.
    /// </remarks>
    private static double GlyphSize(OcrTextBlock column)
    {
        var characters = column.Text.Count(character => !char.IsWhiteSpace(character));
        return Math.Min(column.LayoutBounds.Width, column.LayoutBounds.Height / Math.Max(1, characters));
    }

    private static bool IsKanaOnly(string text)
    {
        var any = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            any = true;
            // Hiragana and katakana, plus the marks that are set inside a reading: the prolonged
            // sound mark and the middle dot of a transliterated name.
            if (character is >= 'ぁ' and <= 'ヿ' or 'ー' or '・') continue;
            return false;
        }

        return any;
    }

    private static bool HoldsKanji(string text) =>
        text.Any(character => character is >= '一' and <= '鿿' or >= '㐀' and <= '䶿');
}
