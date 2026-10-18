using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Layout;

/// <summary>
/// How a translation is set into vertical writing: square cells filled downwards, columns running
/// right to left, and the handful of glyphs that have to be turned on their side to look right in
/// them.
/// </summary>
/// <remarks>
/// Shared by both overlays, and that is the point of it being here. The screenshot overlay has laid
/// vertical text out since issue #132 and the live one does now too, and the two disagreeing about
/// which way the columns run — or about whether a bracket is turned — would be the same feature
/// behaving differently in two places for no reason the reader could see. What they do NOT share is
/// what gives once the cell has shrunk as far as it may and the text still does not fit: a still
/// capture grows its bubble down the page, and a live block — which is exactly the rectangle the
/// user drew — opens another column instead and finally loses the tail. That is the difference
/// between <see cref="Fit"/> and <see cref="FitWithin"/>, and it is the only difference.
/// </remarks>
internal static class VerticalTextGrid
{
    private static readonly SearchValues<char> RotatedGlyphs = SearchValues.Create(
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        "…⋯‥");

    /// <summary>Whether this glyph is drawn turned 90° when the text runs down the page.</summary>
    internal static bool RotatesGlyph(char glyph) => RotatedGlyphs.Contains(glyph);

    // The vertical presentation forms of sentence punctuation, keyed by what the translation holds.
    // The half-width forms map too: OCR and some engines hand them back in place of the full-width.
    private static readonly Dictionary<char, char> VerticalForms = new()
    {
        ['。'] = '︒', // U+3002 → U+FE12
        ['｡'] = '︒', // U+FF61 → U+FE12
        ['、'] = '︑', // U+3001 → U+FE11
        ['､'] = '︑', // U+FF64 → U+FE11
        ['，'] = '︐', // U+FF0C → U+FE10
    };

    // What Chinese vertical writing draws in place of a character, in the column's own font.
    //
    // Curly quotes have no vertical form a horizontal font will draw: set down the page they stay
    // two little marks in the top corners of their cells, with nothing to show they are a pair.
    // Chinese vertical writing quotes with corner brackets instead, which RotatedGlyphs turns.
    //
    // The ellipsis … sits on the baseline, so turned it hugs the left of the cell; the midline ⋯
    // turns into dots down the middle, and two of them make an evenly spaced column of six.
    private static readonly Dictionary<char, char> ChineseSubstitutes = new()
    {
        ['“'] = '「', // U+201C → U+300C
        ['”'] = '」', // U+201D → U+300D
        ['‘'] = '『', // U+2018 → U+300E
        ['’'] = '』', // U+2019 → U+300F
        ['…'] = '⋯', // U+2026 → U+22EF
    };

    // The full-width punctuation YaHei draws off to one side — 。、， in the bottom left, ！？：； down
    // the left half — and JhengHei draws centred, which is where Chinese vertical writing is set here.
    private static readonly SearchValues<char> SimplifiedBorrowsCentred = SearchValues.Create("。｡、､，！？：；");

    // The Traditional Chinese translation font, so the two Chinese targets share one look.
    private static readonly FontFamily CentredPunctuationFont = TranslatedTextFont.For("ZH-HANT");
    // And at its weight: Simplified Chinese drops to Normal for YaHei's sake, which is not this font.
    private static readonly FontWeight CentredPunctuationWeight = TranslatedTextFont.WeightFor("ZH-HANT");

    /// <summary>What one cell of a vertical column draws.</summary>
    /// <param name="Glyph">The character to draw, which may not be the one in the translation.</param>
    /// <param name="Font">The family to draw it in, or null for the column's own.</param>
    internal readonly record struct VerticalGlyph(char Glyph, FontFamily? Font)
    {
        /// <summary>
        /// Whether the cell is turned 90°. Asked of what is drawn, not of the translation's character:
        /// a curly quote is not turned, the corner bracket drawn for it is.
        /// </summary>
        public bool Rotates => RotatesGlyph(Glyph);

        /// <summary>
        /// The weight to draw the cell in: <paramref name="columnWeight"/>, unless the glyph is
        /// borrowed from another family, which keeps the weight that family is drawn in.
        /// </summary>
        public FontWeight WeightOr(FontWeight columnWeight) =>
            Font is null ? columnWeight : CentredPunctuationWeight;
    }

    /// <summary>
    /// What to draw for <paramref name="glyph"/> when a translation into
    /// <paramref name="targetLanguage"/> runs down the page — only what is drawn; the text itself,
    /// and what is copied out of the overlay, keeps the original character.
    /// </summary>
    /// <remarks>
    /// <para>The fonts draw punctuation where horizontal text wants it, and one TextBlock per glyph
    /// has no way to ask a font for its vertical alternates. What each script wants instead differs,
    /// and is the user's call:</para>
    ///
    /// <para>Japanese sets 。、， in the top right. Unicode's Vertical Forms block (U+FE10–FE19) is
    /// the font's vertical alternates as characters of their own, drawn there by the people who drew
    /// the rest of Yu Gothic UI, so the character is swapped and the placement is theirs — it lands
    /// in the top right of these cells as they are laid out. Small kana have no such forms and stay
    /// centred; putting them where print does would mean measuring each font's ink by hand, which
    /// was tried and dropped.</para>
    ///
    /// <para>Chinese sets its punctuation centred. Traditional Chinese already is: JhengHei draws it
    /// centred. YaHei puts 。、， in the bottom left and ！？：； in the left half, and its vertical
    /// forms reach above the top of these cells into the character before, so Simplified Chinese
    /// borrows JhengHei for just those glyphs rather than a form of YaHei's own. Both Chinese
    /// targets draw curly quotes as corner brackets, in the column's own font like the corner
    /// brackets the translation already had (see ChineseSubstitutes).</para>
    ///
    /// <para>Both also draw … as the midline ⋯, still turned and in the column's own font, which
    /// centres it. ︙ was tried first and rejected on the rendered page: YaHei's reaches above its
    /// cell into the character before, and JhengHei's leaves a wider gap between two cells than
    /// between its own dots, so …… reads as two groups of three.</para>
    ///
    /// <para>Korean sets its sentences with half-width Latin punctuation, which is not in this at
    /// all, and every other target is drawn as it is.</para>
    /// </remarks>
    internal static VerticalGlyph VerticalGlyphFor(char glyph, string? targetLanguage)
    {
        switch (targetLanguage?.Trim().ToUpperInvariant())
        {
            case "JA":
                return new(VerticalForms.TryGetValue(glyph, out var form) ? form : glyph, null);
            case "ZH":
            case "ZH-HANS":
                if (ChineseSubstitutes.TryGetValue(glyph, out var substitute))
                    return new(substitute, null);
                return new(glyph, SimplifiedBorrowsCentred.Contains(glyph) ? CentredPunctuationFont : null);
            case "ZH-HANT":
                return new(ChineseSubstitutes.TryGetValue(glyph, out var drawn) ? drawn : glyph, null);
            default:
                return new(glyph, null);
        }
    }

    // Every class below is asked of the translation's own character, and holds what VerticalGlyphFor
    // draws for it as well, so the answer is the same whichever of the two a caller holds: 。 and the
    // ︒ drawn for it, “ and the 「 Chinese draws for it. Nothing VerticalGlyphFor swaps in crosses
    // from one class to another.

    // What may hang below a full column (JLREQ ぶら下げ): the sentence's commas and full stops,
    // full-width, half-width and in their vertical forms. Only these — JLREQ hangs nothing else.
    private static readonly SearchValues<char> HangingMarks = SearchValues.Create("、。，､｡︑︒︐");

    // What may not head a column (JLREQ 行頭禁則): closing brackets and quotes, ！？ and their
    // doubles, the middle dot and the colon and semicolon, and the commas and full stops. ASCII , . : ;
    // are left out: in a Korean or Latin translation they sit inside numbers and abbreviations, and
    // holding them off a column head would push digits about.
    private static readonly SearchValues<char> NoColumnStart = SearchValues.Create(
        "」』）〕］｝〉》】〗〙〛｠)]}”’" +
        "！？‼⁇⁈⁉!?" +
        "・：；" +
        "、。，､｡︑︒︐");

    // What may not end a column (JLREQ 行末禁則): the opening brackets and quotes.
    private static readonly SearchValues<char> NoColumnEnd = SearchValues.Create(
        "「『（〔［｛〈《【〖〘〚｟([{“‘");

    // Drawn as one mark across two cells, so a pair is never split between columns (JLREQ 分離禁止):
    // —— and …… — and the ⋯ Chinese draws for the ellipsis.
    private static readonly SearchValues<char> Inseparable = SearchValues.Create("—―…⋯‥");

    /// <summary>Whether this glyph hangs below a full column rather than heading the next one.</summary>
    internal static bool HangsBelowColumn(char glyph) => HangingMarks.Contains(glyph);

    /// <summary>Whether this glyph is kept from heading a column.</summary>
    internal static bool KeptFromColumnStart(char glyph) => NoColumnStart.Contains(glyph);

    /// <summary>Whether this glyph is kept from ending a column.</summary>
    internal static bool KeptFromColumnEnd(char glyph) => NoColumnEnd.Contains(glyph);

    /// <summary>
    /// What a grid may do at a column break besides breaking where the column is full, as decided by
    /// <see cref="Fit"/> or <see cref="FitWithin"/> and handed on to <see cref="Cells"/>.
    /// </summary>
    [Flags]
    internal enum Breaks
    {
        /// <summary>Columns break where they are full, whatever lands at the head of the next.</summary>
        None = 0,

        /// <summary>
        /// A comma or full stop is hung in a row below the columns. Set only when one was, because the
        /// row is part of the grid's height: <see cref="Cells"/> takes the bottom row of its box for it.
        /// </summary>
        Hang = 1,

        /// <summary>A column ends early so what would have broken the rules goes on to the next.</summary>
        Push = 2,
    }

    // The shortest column the rules are applied to. A hung mark adds a whole cell to the column's
    // length, and the shorter the column the larger a share of it that is: half again over two cells,
    // and a one-cell "column" is a row of single characters with no column to hang from at all. From
    // three it is at most a third, which is a mark sticking out, not a column that ran on. A push
    // leaves cells empty at the foot of a column, and in a column of two that is half of it.
    private const int MinRowsForRules = 3;

    // How many glyphs a column may give up to the next. JLREQ pushes as many as it takes; the cap is
    // a run like ！？」 at a column head, which takes three, and half the column so it never comes out
    // looking like a column that ended. Past either the break stays where it was.
    private static int MaxPush(int rows) => Math.Min(3, rows / 2);

    /// <summary>Where each glyph of a grid goes, and how much of the grid that takes.</summary>
    /// <param name="Places">Column and row of each glyph placed, in reading order; glyphs past the
    /// last column have none.</param>
    /// <param name="Columns">Columns occupied.</param>
    /// <param name="Rows">Cells down the grid, not counting the hanging row: the full column length
    /// once there is more than one column, even where a push has left every column a cell short —
    /// the length is what <see cref="Cells"/> reads the columns back off.</param>
    /// <param name="Hangs">Whether any mark was hung in the row below the columns.</param>
    private readonly record struct Setting(
        IReadOnlyList<(int Column, int Row)> Places, int Columns, int Rows, bool Hangs);

    /// <summary>
    /// Sets <paramref name="text"/> down columns <paramref name="rows"/> cells long, at most
    /// <paramref name="columns"/> of them.
    /// </summary>
    /// <remarks>
    /// <para>Where a column is full the break is checked against the rules, in JLREQ's order: a break
    /// that breaks none stays; a comma or full stop heading the next column is hung below this one
    /// (one per column) if what then heads the next column is allowed to; otherwise the column ends
    /// up to <see cref="MaxPush"/> glyphs early at the first break that breaks none. Failing all of
    /// those the break stays where the column is full, with the comma or full stop still hung if
    /// hanging is allowed at all — the same text as before, never a glyph lost.</para>
    ///
    /// <para>Each break is decided on the glyphs either side of it and nothing else, never on how
    /// many columns are left, so a grid set again with more columns to spare comes out the same.
    /// <see cref="Cells"/> relies on that: the screenshot overlay hands it the whole bubble's
    /// width.</para>
    /// </remarks>
    private static Setting Set(string text, int rows, int columns, Breaks breaks)
    {
        bool rules = rows >= MinRowsForRules;
        bool hang = rules && breaks.HasFlag(Breaks.Hang);
        bool push = rules && breaks.HasFlag(Breaks.Push);

        var places = new List<(int, int)>(text.Length);
        int column = 0, used = 0, deepest = 0, start = 0;
        bool hung = false;
        while (start < text.Length && column < columns)
        {
            int full = start + rows;
            int end = Math.Min(full, text.Length);
            bool hangs = false;
            if (full < text.Length && (hang || push))
                (end, hangs) = Break(text, start, full, hang, push ? MaxPush(rows) : 0);

            for (int i = start; i < end; i++)
                places.Add((column, i - start));
            if (hangs)
            {
                places.Add((column, rows));
                hung = true;
            }

            used = column + 1;
            deepest = Math.Max(deepest, end - start);
            start = hangs ? end + 1 : end;
            column++;
        }

        return new Setting(places, used, used > 1 ? rows : deepest, hung);
    }

    /// <summary>
    /// Where a column from <paramref name="start"/> that is full at <paramref name="full"/> ends,
    /// and whether the glyph at that end is hung below it rather than heading the next.
    /// </summary>
    private static (int End, bool Hangs) Break(string text, int start, int full, bool hang, int maxPush)
    {
        bool Heads(int at) => at >= text.Length || !NoColumnStart.Contains(text[at]);
        bool Fine(int at) =>
            Heads(at)
            && !NoColumnEnd.Contains(text[at - 1])
            && !(text[at - 1] == text[at] && Inseparable.Contains(text[at]));

        if (Fine(full))
            return (full, false);

        bool hangable = hang && HangingMarks.Contains(text[full]);
        if (hangable && Heads(full + 1))
            return (full, true);

        for (int at = full - 1; at >= full - maxPush && at > start; at--)
        {
            if (Fine(at))
                return (at, false);
        }

        return (full, hangable);
    }

    /// <summary>
    /// The breaks to set <paramref name="text"/> with, decided once by the fit and handed to
    /// <see cref="Cells"/> with the grid: the rules as far as they go without costing a column or a
    /// glyph, and where they would, hanging alone, which never costs either.
    /// </summary>
    /// <param name="hang">Whether the grid has room for the hanging row.</param>
    private static (Setting Setting, Breaks Breaks) Arrange(string text, int rows, int columns, bool hang)
    {
        var plain = Set(text, rows, columns, Breaks.None);
        if (rows < MinRowsForRules)
            return (plain, Breaks.None);

        // A push moves glyphs on, so it can want a column the plain grid did not, and in the live
        // overlay a column beside the source is the picture. Held to the plain grid's columns, a
        // setting that then places fewer glyphs is the one that has run out of room.
        var hangs = hang ? Breaks.Hang : Breaks.None;
        var ruled = Set(text, rows, plain.Columns, hangs | Breaks.Push);
        if (ruled.Places.Count >= plain.Places.Count)
            return (ruled, (ruled.Hangs ? Breaks.Hang : Breaks.None) | Breaks.Push);

        if (hang)
        {
            var hung = Set(text, rows, plain.Columns, Breaks.Hang);
            return (hung, hung.Hangs ? Breaks.Hang : Breaks.None);
        }

        return (plain, Breaks.None);
    }

    // How many whole cells a length holds. The tolerance is there because a box cut to an exact
    // number of cells is the ordinary case on the live path, where the caller sizes it from this very
    // grid, and `columns * cellSize / cellSize` is not reliably `columns` in binary. Without the slack
    // a three-column grid measures itself at two and the last column of the sentence is dropped —
    // silently, because every glyph in it simply never gets a cell.
    private static int CellsIn(double length, double cellSize) =>
        Math.Max(1, (int)Math.Floor((length + 0.01) / cellSize));

    /// <summary>Returns cells in vertical reading order: downwards, then one column left.</summary>
    /// <remarks>
    /// <para>Against the top of the box, centred across it — the horizontal overlay's rule given a
    /// quarter turn, where a line starts hard against the left edge and the lines together sit
    /// centred down the box. Along the writing there is a corner the reader is already looking at:
    /// vertical text begins at the top of the rightmost column, so a translation shorter than its
    /// source has to start where the source started and simply run out early. Across the writing
    /// there is no such corner, and a translation that needs fewer columns than the box holds leaves
    /// slack that belongs on both sides: hanging all of it off one edge slides the whole block away
    /// from the writing it replaces, by a different amount for every block on the page.</para>
    ///
    /// <para><paramref name="breaks"/> is what <see cref="Fit"/> or <see cref="FitWithin"/> returned
    /// for this grid, passed on as it was: the grid is set with it rather than decided again, so the
    /// placement is the one that was measured. With <see cref="Breaks.Hang"/> the bottom row of the
    /// box is the hanging row the fit made room for and the columns run one cell short of it. With
    /// none, columns break where they are full — how a tilted group is set.</para>
    /// </remarks>
    internal static IEnumerable<(char Glyph, Rect Cell)> Cells(
        string text,
        Rect bounds,
        double cellSize,
        Breaks breaks = Breaks.None)
    {
        int columns = CellsIn(bounds.Width, cellSize);
        int rows = CellsIn(bounds.Height, cellSize);
        if (breaks.HasFlag(Breaks.Hang))
        {
            if (rows > MinRowsForRules)
                rows--;
            else
                breaks &= ~Breaks.Hang;
        }

        var setting = Set(text, rows, columns, breaks);

        // The columns actually occupied are the ones that get centred. A hung mark sits under its
        // column and adds none.
        double right = bounds.Left + (bounds.Width + setting.Columns * cellSize) / 2;

        for (int i = 0; i < setting.Places.Count; i++)
        {
            var (column, row) = setting.Places[i];
            yield return (text[i], new Rect(
                right - (column + 1) * cellSize,
                bounds.Top + row * cellSize,
                cellSize,
                cellSize));
        }
    }

    /// <summary>Puts one glyph in its cell, filling it rather than sitting on a baseline in it.</summary>
    internal static void PositionGlyph(TextBlock glyph, Rect bounds)
    {
        glyph.Width = bounds.Width;
        glyph.Height = bounds.Height;
        glyph.LineHeight = bounds.Height;
        glyph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Canvas.SetLeft(glyph, bounds.X);
        Canvas.SetTop(glyph, bounds.Y);
    }

    /// <summary>
    /// The screenshot overlay's fit: shrink the cell until the text fits the width it has, and grow
    /// the bubble downwards if it still does not.
    /// </summary>
    /// <remarks>
    /// The breaks are decided on the grid <see cref="Cells"/> will be handed — the whole width, and
    /// the height returned here — so they are decided on what will be drawn. A hanging row is one
    /// cell under the rows that height holds, and is taken only if it ends within
    /// <paramref name="hangingReach"/>; without one nothing hangs, and the rest of the rules still
    /// apply.
    /// </remarks>
    /// <param name="hangingReach">How tall the grid may grow for a hanging row, or null for no
    /// hanging at all.</param>
    internal static (double CellSize, double Height, Breaks Breaks) Fit(
        double width,
        double height,
        double preferredCellSize,
        double minCellSize,
        double emergencyCellSize,
        string text,
        double? hangingReach = null)
    {
        int needed = Math.Max(1, text.Length);
        double cellSize = Math.Max(minCellSize, preferredCellSize);
        while (cellSize > emergencyCellSize && Capacity(width, height, cellSize) < needed)
        {
            cellSize = Math.Max(emergencyCellSize, cellSize - 0.5);
        }

        int columns = Math.Max(1, (int)Math.Floor(width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling((double)needed / columns));
        double gridHeight = Math.Max(height, rows * cellSize);

        int placedRows = CellsIn(gridHeight, cellSize);
        bool hangRoom = hangingReach is { } reach && (placedRows + 1) * cellSize <= reach + 0.01;
        var (setting, breaks) = Arrange(text, placedRows, CellsIn(width, cellSize), hangRoom);
        return setting.Hangs
            ? (cellSize, (placedRows + 1) * cellSize, breaks)
            : (cellSize, gridHeight, breaks);
    }

    /// <summary>
    /// The live overlay's fit: the source's own footprint if the type can be made to sit in it, and
    /// never wider than the block.
    /// </summary>
    /// <remarks>
    /// <para>The order of the two concessions is the whole of this method, and getting it backwards
    /// is what <see cref="Fit"/> already had right. A translation too long for the column it
    /// replaces can either be set a little smaller in that column or be spread over more columns
    /// than the source had, and only the first of those stays inside the balloon. The second walks
    /// out of it: the columns are laid right to left, so the extra ones land on whatever is drawn
    /// beside the source — the next balloon, the next panel, the picture. So the cell shrinks first,
    /// down to the caller's readability floor, and columns are added only once that has run out.
    /// The screenshot overlay reaches the same answer by shrinking before it grows the bubble.</para>
    ///
    /// <para><paramref name="columnLength"/> is how long one column may run, and it is the source's
    /// own length rather than the block's. Over a comic page the block IS the page, so a column
    /// given the block's height fits every sentence into one and runs it from the top of the
    /// picture to the bottom, straight through the panels either side of the balloon it came
    /// from.</para>
    ///
    /// <para>Returned as a size rather than a count because the caller is placing a rectangle over
    /// the source and has to know how far it reaches to do that. The size is read off the same
    /// setting <see cref="Cells"/> places the glyphs by, breaks and all, and a hanging row is in the
    /// height: the caller cuts its grid to exactly this, and anything outside it is clipped.</para>
    /// </remarks>
    /// <param name="columnRoom">How wide the source group was: the grid stays inside it if it can.</param>
    /// <param name="columnLength">How far down one column may run.</param>
    /// <param name="maxWidth">The block. The grid is never wider, whatever is left unplaced.</param>
    /// <param name="hangingReach">How far down the grid may run with a hanging row under its
    /// columns, or null for no hanging at all.</param>
    internal static (double CellSize, double Width, double Height, Breaks Breaks) FitWithin(
        double columnRoom,
        double columnLength,
        double maxWidth,
        double preferredCellSize,
        double minCellSize,
        string text,
        double? hangingReach = null)
    {
        int needed = Math.Max(1, text.Length);
        double cellSize = Math.Max(minCellSize, preferredCellSize);

        while (cellSize > minCellSize && Capacity(columnRoom, columnLength, cellSize) < needed)
        {
            cellSize = Math.Max(minCellSize, cellSize - 0.5);
        }

        int rows = Math.Max(1, (int)Math.Floor(columnLength / cellSize));

        // At the floor the text may still not fit; keep the grid inside the block anyway and let
        // the tail be the thing that is lost, rather than the block's own edge.
        int maxColumns = Math.Max(1, (int)Math.Floor(maxWidth / cellSize));

        bool hangRoom = hangingReach is { } reach && (rows + 1) * cellSize <= reach + 0.01;
        var (setting, breaks) = Arrange(text, rows, maxColumns, hangRoom);

        // A grid over one column is the only one shorter than the room it was given; see Setting.Rows.
        int columns = Math.Max(1, setting.Columns);
        int usedRows = Math.Max(1, setting.Rows) + (setting.Hangs ? 1 : 0);
        return (cellSize, columns * cellSize, usedRows * cellSize, breaks);
    }

    private static int Capacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));
}
