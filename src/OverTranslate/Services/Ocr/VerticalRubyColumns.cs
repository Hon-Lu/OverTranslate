using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Takes the reading columns printed beside the writing they annotate out of the writing, before
/// grouping sees them: the certain ones are dropped, the suspected ones are merely set aside.
/// </summary>
/// <remarks>
/// <para>A different stage from <see cref="VerticalColumnGrouping.WithoutRuby"/> and a different
/// failure. That one runs on finished groups and stops a reading being DRAWN as a second bubble
/// over a balloon.
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
/// kanji — but the recogniser is free to read one out of it anyway, and over the 15 comic pages
/// that is the single largest hole in this: of the 86 columns small enough to be a reading of
/// something, 64 are dropped here and 22 escape, and 15 of those 22 escape on this test alone.
/// They are not one kind of thing. Six are readings mis-read — <c>いち見んてきせい</c> for
/// いちばんてきせい, <c>の6</c>, <c>上</c>, <c>大</c> — six are the page number or a mark off the
/// artwork, and three are real writing: <c>仲間だろ</c>, <c>こ…来ないで…</c>, and a caption. NOTHING
/// SEPARATES THEM BY SIZE: the reading いち見んてきせい measures 0.51 of the column beside it and
/// the dialogue 仲間だろ measures 0.49.</para>
///
/// <para>So the test is not loosened. What is loosened is the ACTION — see
/// <see cref="MightBeReadingOf"/>, which asks the same four questions without this one and only
/// ever sets a column aside. A reading left in a sentence is a wrong word inside it and cannot be
/// taken back out; a column set aside is a sentence drawn in two pieces, which the reader can
/// still read.</para>
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

    /// <summary>
    /// Takes the readings out of the text and gives their ROOM to the columns they annotate.
    /// </summary>
    /// <remarks>
    /// The room matters as much as the text. Grouping asks how far apart two columns are against
    /// how big their glyphs are, and a column's box that has had the reading taken off it sits a
    /// reading's width further from its neighbour than the same column measured with the reading
    /// still in it. Simply dropping the reading therefore pushes the columns of one balloon apart
    /// far enough to be read as two — measured, it broke 俺の本職は剣士なんだから into three. So
    /// the reading's rectangle is unioned into the writing's, which leaves every column where the
    /// detector would have put it if it had never separated the two, and leaves the bubble drawn
    /// over the reading rather than beside it.
    /// </remarks>
    /// <summary>
    /// What is left of the page's writing, and the reading columns held out of it.
    /// </summary>
    /// <param name="Writing">The columns to go on grouping into sentences.</param>
    /// <param name="Readings">
    /// The suspected readings, which never join a sentence and are carried to the end of grouping
    /// on their own. Certain readings are not in here; they are gone.
    /// </param>
    internal readonly record struct Separation(
        List<OcrTextBlock> Writing, List<OcrTextBlock> Readings);

    internal static Separation Separate(List<OcrTextBlock> columns)
    {
        if (columns.Count < 2) return new Separation(columns, []);

        // Which column each one is a reading of, or -1 to leave it in the writing. The NEAREST one
        // it could be a reading of: a reading is set against its own kanji, and a column further
        // left of it is a coincidence rather than the word being read.
        var annotates = new int[columns.Count];
        var glosses = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            annotates[i] = glosses[i] = -1;
            for (var j = 0; j < columns.Count; j++)
            {
                if (i == j) continue;
                if (IsReadingOf(columns[i], columns[j]))
                    annotates[i] = Nearer(columns, annotates[i], j);
                else if (MightBeReadingOf(columns[i], columns[j]))
                    glosses[i] = Nearer(columns, glosses[i], j);
            }

            // Certainty wins: a column this is sure about is dropped rather than set aside.
            if (annotates[i] >= 0) glosses[i] = -1;
        }

        var bounds = columns.Select(column => column.Bounds).ToArray();
        var layout = columns.Select(column => column.LayoutBounds).ToArray();
        for (var i = 0; i < columns.Count; i++)
        {
            // The ROOM either way, so that the columns of one balloon stay as far apart as the
            // detector framed them. Only the certain ones give away their Bounds as well: those are
            // gone, so the writing's bubble should cover where they were, while a column merely set
            // aside is still drawn and would be covered by a bubble stretched over it.
            if (annotates[i] >= 0)
            {
                bounds[annotates[i]] = Rect.Union(bounds[annotates[i]], columns[i].Bounds);
                layout[annotates[i]] = Rect.Union(layout[annotates[i]], columns[i].LayoutBounds);
            }
            else if (glosses[i] >= 0)
            {
                layout[glosses[i]] = Rect.Union(layout[glosses[i]], columns[i].LayoutBounds);
            }
        }

        var writing = new List<OcrTextBlock>(columns.Count);
        var readings = new List<OcrTextBlock>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (annotates[i] >= 0) continue;
            if (glosses[i] >= 0) readings.Add(columns[i]);
            else writing.Add(columns[i] with { Bounds = bounds[i], LayoutBounds = layout[i] });
        }

        return new Separation(writing, readings);
    }

    private static int Nearer(List<OcrTextBlock> columns, int chosen, int candidate) =>
        chosen < 0 || columns[candidate].LayoutBounds.Right > columns[chosen].LayoutBounds.Right
            ? candidate
            : chosen;

    /// <summary>
    /// Whether a column is close enough to a reading to be kept out of the sentence beside it,
    /// without being sure enough to throw away.
    /// </summary>
    /// <remarks>
    /// <para><see cref="IsReadingOf"/> without the kana-only test, which is the one the recogniser
    /// breaks: six of the readings on the 15 comic pages come back holding a character that is not
    /// kana and walk straight into a sentence. Dropping on this would cost <c>仲間だろ</c> and
    /// <c>こ…来ないで…</c>, which measure the same, so it does not drop — the column is set aside
    /// and drawn on its own.</para>
    ///
    /// <para>A COLUMN, and taller than it is wide, asked of both sides. The material that escapes
    /// the kana test is mostly not ruby at all: a browser title bar, a caption running across the
    /// panel — <c>各階層の入り口に設置されている</c> at 305x43 — a taskbar button. All of them are
    /// wider than they are tall, and none of them is what this is for.</para>
    /// </remarks>
    private static bool MightBeReadingOf(OcrTextBlock reading, OcrTextBlock writing) =>
        RunsDownThePage(reading) && RunsDownThePage(writing) &&
        HoldsKanji(writing.Text) && SitsAsAReadingOf(reading, writing);

    private static bool RunsDownThePage(OcrTextBlock column) =>
        column.LayoutBounds.Height > column.LayoutBounds.Width;

    private static bool IsReadingOf(OcrTextBlock reading, OcrTextBlock writing)
    {
        if (!IsKanaOnly(reading.Text) || !HoldsKanji(writing.Text))
            return false;

        return SitsAsAReadingOf(reading, writing);
    }

    /// <summary>Half the size of the writing, hard against its right edge, and running with it.</summary>
    private static bool SitsAsAReadingOf(OcrTextBlock reading, OcrTextBlock writing)
    {
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
