using SkiaSharp;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Gives a horizontal line back the symbols its box stopped short of — 「……」「——」「。」「！？」 at
/// the end of a line, or at its start — before anything crops from it.
/// </summary>
/// <remarks>
/// <para>The symbols a line of Japanese dialogue so often starts or ends with are where the detector
/// stops. MEASURED on the two captures a user sent and on the typing stage's sentences: the line
/// （ウソでしょ…… is boxed to ょ and the …… sits 90px past the box; the detector's probability over
/// those dots is 0.00-0.23 against a 0.2 threshold to even start a box. The dots are not beyond the
/// recogniser — read at a size where the box happens to reach them, they come back — they are
/// simply never cropped, and so never erased either: what the user saw was a translation with the
/// original ……  still showing beside it. The same holds for a ―― or …… opening a line, and for the
/// first glyph of 二階堂, which the box started inside.</para>
///
/// <para>So the question asked is the one <see cref="VerticalColumnEnds"/> asks of a column: was
/// something of this line cut off here? The walk goes out from the line's last ink along the band
/// its own glyphs occupy, across blank runs no wider than <see cref="GapLimit"/>, taking each piece
/// of ink that is the line's colour, stays inside the line's band, and is shaped like a symbol —
/// a dot, a dash, something no wider than ！？）」. A piece that is a whole glyph past a gap, runs
/// out of the band, is solid like the blinking ▼ a game puts after the last word, or is another
/// colour ends it. Another box on the line is a wall.</para>
///
/// <para>Then the recogniser has the last word: the grown crop and the detector's own are read in
/// the same batch, and the grown reading is only kept when it is the original with nothing but the
/// line's ends added (<see cref="Accepts"/>). The pixels can say ink goes on; only the recogniser
/// can say it is the same line's symbols, and every rule that tried to answer that from the pixels
/// alone was measured letting icons, specks and a neighbouring button in.</para>
///
/// <para>The grown box is used for everything — crop, the block's bounds, grouping — rather than for
/// the crop alone as the column walk does. The bounds are half the point, because the overlay erases
/// and covers with them; and a box that only gains what a line ends with sits against its neighbours
/// as the line itself does.</para>
/// </remarks>
internal static class HorizontalLineEnds
{
    // The tunables are fields rather than constants so OcrHarness can sweep them (--ends-gap and the
    // rest); nothing in the app writes them.

    /// <summary>The widest run of blank columns a line may carry on across, in glyph heights.</summary>
    /// <remarks>
    /// MEASURED gaps the walk has to cross: ょ to the first dot of user-4's …… is 17px on a 40px
    /// glyph (0.43), dot to dot 9 (0.22); the stage font's ―― starts 1px after its word, its …… 0.3
    /// glyph after. A full-width space is a glyph and more, and is not crossed — the next thing past
    /// one is the next thing on the row, not this line's end.
    /// </remarks>
    internal static double GapLimit = 0.8;

    /// <summary>How far past its last ink a line may reach, in glyph heights.</summary>
    /// <remarks>
    /// The widest end measured: user-4's …… spans 2.1 glyphs, the stage's ―― 2.2. Three leaves room
    /// for a 。 or ！？ after them; a piece still going when the budget runs out is a rule or the
    /// edge of a picture, and is not taken.
    /// </remarks>
    internal static double Reach = 3.0;

    /// <summary>Room left past the last ink reached, in glyph heights.</summary>
    /// <remarks>
    /// The crop must not end on the symbol's last column: user-6's ） ran 1px past its box and was
    /// not read until the crop held it with room on the far side. The detector leaves about a
    /// quarter of a glyph around what it boxes; this is a little over half of that.
    /// </remarks>
    internal static double Margin = 0.15;

    /// <summary>Set false to measure the shipped behaviour without this (OcrHarness only).</summary>
    internal static bool Enabled = true;

    /// <summary>Why each walk stopped, for OcrHarness. Null in the app.</summary>
    internal static Action<string>? Trace;

    /// <summary>
    /// The detector's boxes with each line that stopped short of its own ink grown to take it in,
    /// in the detector's coordinates. The same list when nothing grows.
    /// </summary>
    /// <param name="grown">Each box that grew, keyed by the new box: the one it replaced, and how.</param>
    internal static IReadOnlyList<TextBox> Apply(
        SKBitmap source,
        IReadOnlyList<System.Windows.Rect> sourceBounds,
        IReadOnlyList<TextBox> detectorBoxes,
        double ratioX,
        double ratioY,
        int padding,
        out Dictionary<TextBox, Growth>? grown,
        Func<SKRectI, (float Max, float Mean)>? probability = null)
    {
        grown = null;
        if (!Enabled || detectorBoxes.Count == 0) return detectorBoxes;

        var rects = sourceBounds
            .Select(b => new SKRectI(
                (int)Math.Floor(b.Left), (int)Math.Floor(b.Top),
                (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom)))
            .ToList();

        List<TextBox>? result = null;
        for (var i = 0; i < detectorBoxes.Count; i++)
        {
            var p = detectorBoxes[i].BoxPoints;
            // Corners in the library's order — top-left, top-right, bottom-right, bottom-left —
            // and upright or nearly: a tilted line has no single row to walk along. Nearly, because
            // a line the detector leaned by a degree or two is still one row: the line this exists
            // for, （ウソでしょ……, comes back 7px out of level over 261.
            if (p.Length != 4 || p[0].X >= p[1].X || p[3].X >= p[2].X || p[0].Y >= p[3].Y || p[1].Y >= p[2].Y)
                continue;
            var lean = Math.Max(
                Math.Max(Math.Abs(p[0].X - p[3].X), Math.Abs(p[1].X - p[2].X)),
                Math.Max(Math.Abs(p[0].Y - p[1].Y), Math.Abs(p[3].Y - p[2].Y)));
            if (lean > Math.Max(3, (p[3].Y - p[0].Y) * MaxLean))
            {
                Trace?.Invoke($"box {rects[i]}: leans {lean}px");
                continue;
            }

            var others = rects.Where((_, j) => j != i).ToList();
            var ends = Measure(source, rects[i], others, probability);
            if (ends is null) continue;

            // Into the detector's space: the frame's scale, then the library's padding. Floor and
            // ceiling, so the crop never lands a pixel inside the symbol this exists to reach.
            var grow = ends.Value.Right > rects[i].Right
                ? Math.Max(0, (int)Math.Ceiling(ends.Value.Right * ratioX + padding) - Math.Max(p[1].X, p[2].X))
                : 0;
            var back = ends.Value.Left < rects[i].Left
                ? Math.Max(0, Math.Min(p[0].X, p[3].X) - (int)Math.Floor(ends.Value.Left * ratioX + padding))
                : 0;
            if (grow == 0 && back == 0) continue;

            // Each side moved along its own edges, so a leaning box keeps its lean and the part of
            // the line it already held is cropped exactly as before.
            result ??= [.. detectorBoxes];
            result[i] = new TextBox
            {
                Score = detectorBoxes[i].Score,
                BoxPoints =
                [
                    Along(p[1], p[0], back), Along(p[0], p[1], grow),
                    Along(p[3], p[2], grow), Along(p[2], p[3], back),
                ],
            };
            Trace?.Invoke($"grew box {rects[i]} leftCut={ends.Value.LeftCut} rightCut={ends.Value.RightCut}");
            grown ??= new(ReferenceEqualityComparer.Instance);
            grown[result[i]] = new Growth(
                detectorBoxes[i], back > 0, grow > 0, back > 0 && ends.Value.LeftCut, grow > 0 && ends.Value.RightCut,
                ends.Value.LeftThin, ends.Value.RightThin, back > 0 && ends.Value.LeftDash, grow > 0 && ends.Value.RightDash,
                back > 0 && ends.Value.LeftStrokes, grow > 0 && ends.Value.RightStrokes);
        }

        return result ?? detectorBoxes;
    }

    /// <summary>
    /// The scores a grown line is judged on: the symbols the walk took in count for as much as the
    /// words beside them, neither more nor less.
    /// </summary>
    /// <remarks>
    /// The recogniser is less sure of a run of punctuation than of words, and every confidence
    /// floor downstream reads the average. MEASURED on the stage line ……誰？ : the box the detector
    /// drew read 誰？ and was kept on 75 of 77 framings; grown to take in the ……, it read ……誰？ at
    /// 0.70-0.72 and the realtime floor for short readings (0.80) threw the whole line away on 28 of
    /// them — a line lost to recover two dots. Whether this is text was already answered by the box
    /// the detector drew; the symbols past it are not evidence either way, so they are given the
    /// words' own score rather than taken out of the text.
    /// </remarks>
    internal static float[] JudgeOnTheWords(string[]? chars, float[] scores, bool left, bool right)
    {
        if (chars is null || chars.Length != scores.Length) return scores;
        var from = 0;
        if (left)
            while (from < chars.Length && IsSymbol(chars[from])) from++;
        var to = chars.Length;
        if (right)
            while (to > from && IsSymbol(chars[to - 1])) to--;
        if (to <= from || (from == 0 && to == chars.Length)) return scores;

        var words = 0f;
        for (var i = from; i < to; i++) words += scores[i];
        words /= to - from;
        var judged = (float[])scores.Clone();
        for (var i = 0; i < from; i++) judged[i] = words;
        for (var i = to; i < judged.Length; i++) judged[i] = words;
        return judged;

        static bool IsSymbol(string c) =>
            !string.IsNullOrWhiteSpace(c) && Realtime.ShortReadingDetection.IsLineEdgeSymbols(c);
    }

    /// <summary>
    /// Whether the grown reading would fall under the realtime floor for short readings where the
    /// detector's own reading did not — see <see cref="Realtime.ShortReadingDetection.IsUnconvincingShortText"/>.
    /// </summary>
    internal static bool SinksAShortLine(string[]? chars, float[]? scores, string[]? originalChars, float[]? originalScores)
    {
        if (scores is not { Length: > 0 } || originalScores is not { Length: > 0 }) return false;
        var grown = Realtime.ShortReadingDetection.IsUnconvincingShortText(
            string.Concat(chars ?? []), scores.Average());
        var before = Realtime.ShortReadingDetection.IsUnconvincingShortText(
            string.Concat(originalChars ?? []), originalScores.Average());
        return grown && !before;
    }

    /// <summary>
    /// Which grown sides the grown reading actually read more on: a character the detector's box
    /// did not give, or more symbols at that end than it gave.
    /// </summary>
    /// <remarks>
    /// A side that read nothing more does not keep its growth: the box would only have moved onto
    /// whatever sat beside the line, and the overlay erases and covers with that box. MEASURED on
    /// the Korean game interfaces: 전설 grew from 50px to 104px onto the icon beside it and read the
    /// same, so the icon would have been painted over. The ―― put back by its shape counts as read.
    /// </remarks>
    internal static (bool Left, bool Right) ReadMore(string? original, string? grown)
    {
        var o = Normalise(original);
        var g = Normalise(grown);
        var wo = Words(o);
        var wg = Words(g);
        if (wo.Length == 0)
        {
            // Symbols alone: the end the original sits against is the end that did not grow.
            var longer = g.Length > o.Length;
            return (longer && g.EndsWith(o, StringComparison.Ordinal), longer && g.StartsWith(o, StringComparison.Ordinal));
        }
        var at = wg.IndexOf(wo, StringComparison.Ordinal);
        if (at < 0) return (false, false);
        return (at > 0 || Lead(g).Length > Lead(o).Length,
            at + wo.Length < wg.Length || Trail(g).Length > Trail(o).Length);
    }

    private static string Normalise(string? text) =>
        string.Concat((text ?? "").Normalize(System.Text.NormalizationForm.FormKC).Where(ch => !char.IsWhiteSpace(ch)));

    private static bool IsSymbolChar(char ch) => Realtime.ShortReadingDetection.IsLineEdgeSymbols(ch.ToString());
    private static string Words(string text) => string.Concat(text.Where(ch => !IsSymbolChar(ch)));
    private static string Lead(string text) => string.Concat(text.TakeWhile(IsSymbolChar));
    private static string Trail(string text) => string.Concat(text.Reverse().TakeWhile(IsSymbolChar));

    /// <summary>
    /// The most a glyph the box cut through may fill of its own box and still count as the line's
    /// strokes when the grown reading read nothing more on that side.
    /// </summary>
    /// <remarks>
    /// The detector's box often reads a glyph from the half it holds — user-4's 二 came back on 12
    /// of 84 framings from a box that started inside it — and the other half is still the line's,
    /// still on screen beside the translation unless the box takes it in. An icon the box clipped
    /// reads the same way and is not the line's. MEASURED on the cut pieces: 二's outer half fills
    /// 0.08-0.13 of its box (two thin strokes); the icons beside the Korean interface labels that
    /// grew without reading anything 0.15-1.00, most of them solid. 0.14 keeps every 二 (user-4
    /// stays 84 of 84 covered) and drops all six Korean growths. What it does not keep is the last
    /// 2-5px of a 。 the box clipped when the box had read the 。 already: their width overlaps the
    /// Korean icons' (2-7px), so no width keeps one and drops the other.
    /// </remarks>
    internal static double CutStrokes = 0.14;

    /// <summary>
    /// The grown box with each side that read nothing more put back where the detector had it.
    /// </summary>
    internal static TextBox Keep(TextBox grown, Growth growth, bool left, bool right)
    {
        var g = grown.BoxPoints;
        var o = growth.Original.BoxPoints;
        return new TextBox
        {
            Score = grown.Score,
            BoxPoints = [left ? g[0] : o[0], right ? g[1] : o[1], right ? g[2] : o[2], left ? g[3] : o[3]],
        };
    }

    /// <summary>
    /// Whether a piece is a ―― by its shape alone: one horizontal stroke, no taller than a quarter
    /// of a glyph, at least one and a half glyphs long, and centred in the middle third of the
    /// line's glyphs.
    /// </summary>
    /// <remarks>
    /// One glyph long is not enough: a stroke that short is as likely 一 or ー, which the recogniser
    /// does read. The middle third keeps an underline and the edge of a dialogue box out.
    /// </remarks>
    internal static bool IsDash(SKRectI piece, double lineCentre, int glyph)
    {
        if (piece.Width < glyph * 1.5 || piece.Height > glyph * 0.25 || piece.Width < piece.Height * 3) return false;
        var centre = (piece.Top + piece.Bottom) / 2.0;
        return Math.Abs(centre - lineCentre) <= glyph / 6.0;
    }

    private static bool IsDash(SKRectI piece, Band band) => IsDash(piece, (band.Top + band.Bottom) / 2.0, band.Glyph);

    /// <summary>
    /// The grown reading with a ―― put back on each end that grew over a lone dash the recogniser
    /// read nothing of, or the reading itself when there is none to put back.
    /// </summary>
    internal static (string[] Chars, float[] Scores)? WithDashes(string[]? chars, float[] scores, Growth growth)
    {
        if (chars is null || chars.Length != scores.Length || chars.Length == 0) return null;
        if (!AddDashes) return null;
        var left = growth.LeftDash && !IsDashLike(chars[0]);
        var right = growth.RightDash && !IsDashLike(chars[^1]);
        if (!left && !right) return null;
        var words = scores.Average();
        string[] dash = ["―", "―"];
        var newChars = (left ? dash : []).Concat(chars).Concat(right ? dash : []).ToArray();
        var newScores = (left ? new[] { words, words } : []).Concat(scores).Concat(right ? new[] { words, words } : []).ToArray();
        Trace?.Invoke($"dash put back left={left} right={right} \"{string.Concat(newChars)}\"");
        return (newChars, newScores);

        static bool IsDashLike(string c) => c is "―" or "—" or "－" or "-" or "ー" or "一" or "～" or "~" or "‐" or "─" or "━";
    }

    /// <summary>Whether a lone dash the recogniser did not read is put back by shape (see <see cref="IsDash"/>).</summary>
    internal static bool AddDashes = true;

    /// <summary>How far out of level a box may be and still be walked, as a share of its height.</summary>
    private const double MaxLean = 0.15;

    // The point at the far end of the edge from `from` through `to`, moved `by` pixels further
    // along x (and as far along y as the edge slopes).
    private static SKPointI Along(SKPointI from, SKPointI to, int by)
    {
        if (by == 0) return to;
        var dx = to.X - from.X;
        var slope = dx == 0 ? 0 : (double)(to.Y - from.Y) / dx;
        var sign = Math.Sign(dx);
        return new SKPointI(to.X + sign * by, (int)Math.Round(to.Y + slope * sign * by));
    }

    /// <summary>
    /// Where the line in <paramref name="box"/> really starts and ends, in the image's pixels, or
    /// null when neither end grows.
    /// </summary>
    internal static Ends? Measure(
        SKBitmap image, SKRectI box, IReadOnlyList<SKRectI> others,
        Func<SKRectI, (float Max, float Mean)>? probability = null)
    {
        var left = Math.Max(0, box.Left);
        var right = Math.Min(image.Width, box.Right);
        var top = Math.Max(0, box.Top);
        var bottom = Math.Min(image.Height, box.Bottom);
        var width = right - left;
        var height = bottom - top;
        // A line, not a column, and big enough to have a glyph height worth measuring.
        if (height < 10 || width < 4 || height > width * 2.5) return null;

        if (!Colour.Of(image, left, top, right, bottom, out var ink))
        {
            Trace?.Invoke($"box {left},{top}-{right},{bottom}: no text colour");
            return null;
        }

        // The line's own glyph band: the rows inside the box that carry its ink. The box's height
        // carries the detector's unclip padding; this does not.
        var rowFloor = Math.Max(2, width / 50);
        int inkTop = -1, inkBottom = -1;
        for (var y = top; y < bottom; y++)
        {
            var count = 0;
            for (var x = left; x < right && count < rowFloor; x++)
                if (ink.Is(image.GetPixel(x, y))) count++;
            if (count < rowFloor) continue;
            if (inkTop < 0) inkTop = y;
            inkBottom = y + 1;
        }
        if (inkTop < 0) return null;
        var glyph = inkBottom - inkTop;
        // At least two glyphs long: a lone glyph or an icon has no line for symbols to end.
        if (glyph < 8 || width < glyph * 2) return null;

        var slack = Math.Max(1, (int)(glyph * 0.1));
        var band = new Band(Math.Max(0, inkTop - slack), Math.Min(image.Height, inkBottom + slack), glyph);

        // The other boxes on this line, which the walk stops at: what they hold is theirs to read.
        var onLine = others
            .Where(o => Math.Min(o.Bottom, band.Bottom) - Math.Max(o.Top, band.Top) > glyph * 0.5)
            .ToList();

        // The last and first inked columns inside the box. The gap a symbol is allowed is counted
        // from the ink, not from the box's edge, which sits a padding's width further out.
        var lastInk = right - 1;
        while (lastInk > left && !Column(image, lastInk, band, ink)) lastInk--;
        var firstInk = left;
        while (firstInk < lastInk && !Column(image, firstInk, band, ink)) firstInk++;
        if (lastInk <= firstInk) return null;

        Trace?.Invoke($"box {left},{top}-{right},{bottom}: ink {firstInk}-{lastInk} band {band.Top}-{band.Bottom} glyph {glyph} {ink}");
        var (newRight, rightCut, rightThin, rightDash, rightStrokes) = Walk(image, band, ink, lastInk, +1, onLine, right, probability);
        var (newLeft, leftCut, leftThin, leftDash, leftStrokes) = Walk(image, band, ink, firstInk, -1, onLine, left, probability);
        if (newRight <= right && newLeft >= left) return null;
        return new Ends(
            Math.Min(left, newLeft), Math.Max(right, newRight), leftCut, rightCut, leftThin, rightThin, leftDash, rightDash,
            leftStrokes, rightStrokes);
    }

    /// <summary>Where a line really starts and ends, and whether each end was a glyph cut through.</summary>
    /// <param name="LeftThin">Every piece taken on the left was a dash: wide and no taller than a quarter of a glyph.</param>
    /// <param name="LeftDash">The left end is one lone ―― (see <see cref="IsDash"/>).</param>
    /// <param name="LeftStrokes">The glyph the box cut through on the left is strokes, not a filled shape (see <see cref="CutStrokes"/>).</param>
    internal readonly record struct Ends(
        int Left, int Right, bool LeftCut, bool RightCut, bool LeftThin = false, bool RightThin = false,
        bool LeftDash = false, bool RightDash = false, bool LeftStrokes = false, bool RightStrokes = false);

    /// <summary>
    /// A box that grew: the detector's own box it replaced, which sides grew, and whether each of
    /// those went through a glyph the box had cut rather than across a gap to a symbol.
    /// </summary>
    internal sealed record Growth(
        TextBox Original, bool Left, bool Right, bool LeftCut, bool RightCut, bool LeftThin = false, bool RightThin = false,
        bool LeftDash = false, bool RightDash = false, bool LeftStrokes = false, bool RightStrokes = false);

    /// <summary>
    /// Whether the grown box's reading is the detector's box's reading with only the line's ends
    /// added to it — and so whether to keep it.
    /// </summary>
    /// <remarks>
    /// <para>Both crops are read and the grown one has to agree with the original everywhere the
    /// original reached. The pixels can only say that ink of the line's colour goes on past the
    /// box; what it says is the recogniser's to answer, and a crop that changes the words it
    /// already had is a crop that read something else. MEASURED over the corpora below without
    /// this: the walk was right about the 「……」 and the ー it was built for, and also turned
    /// 絡みに行く into 絡みに行< on a video subtitle, put X in front of 待ち続けることなんて,
    /// and added S, G, Q, 6, el to game interfaces.</para>
    ///
    /// <para>What may be added is symbols (see <see cref="Realtime.ShortReadingDetection.IsLineEdgeSymbols"/>),
    /// and only on a side that grew: the end that did not grow has to read exactly as before. A side
    /// that grew through a glyph the box had cut may add up to two characters of anything: that is
    /// the glyph itself, 二 of user-4's 二階堂, which the box started inside. Nothing added at all
    /// is a yes too — the ―― the recogniser cannot read is still the line's, and the grown box is
    /// what lets the overlay cover it.</para>
    /// </remarks>
    internal static bool Accepts(string? original, string? grown, Growth growth)
    {
        var o = Normalise(original);
        var g = Normalise(grown);
        Trace?.Invoke($"read \"{o}\" grown \"{g}\"");

        // The words, compared without the symbols: the two crops disagree about symbols all the
        // time and harmlessly — （ against (, ！？ against !?, a lone . against …… — and on user-4
        // and user-6 that alone turned away 61 of 93 grown readings that were right.
        var wo = Words(o);
        var wg = Words(g);
        if (o.Length == 0) return false;
        // Lines in Chinese, Japanese or Korean only — the scripts whose symbols sit in a square of
        // their own, a gap away from the word. MEASURED over the corpora: what the walk changed on
        // Latin lines was the odd recovered letter (FPS, Comment) and four wrapped English
        // paragraphs that stopped grouping (a chat log, a card description, a tooltip, a dialogue
        // log) once one of their lines was a few pixels wider, against no Latin line that had lost
        // an ending the way 「……」 is lost. A box of symbols alone is judged by what it grows into.
        // Read off the text as the recogniser wrote it: normalising folds ！ into ! and … into ...,
        // which is exactly the full-width evidence a box of symbols alone has.
        var raw = grown ?? "";
        if (!(wo.Length > 0 ? raw.Any(IsCjk) : raw.Any(IsCjkSymbol))) return false;
        // A box that held nothing but symbols — the ……！？ the detector cut off user-6's line —
        // may only gain more of them, at the end that grew.
        if (wo.Length == 0)
            return wg.Length == 0 &&
                (growth.Left || g.StartsWith(o, StringComparison.Ordinal)) &&
                (growth.Right || g.EndsWith(o, StringComparison.Ordinal));
        var at = wg.IndexOf(wo, StringComparison.Ordinal);
        if (at < 0) return false;
        if (!Cut(wg[..at], growth.LeftCut) || !Cut(wg[(at + wo.Length)..], growth.RightCut)) return false;

        // A side that did not grow reads as it did, and one that grew reads no less than it did.
        // MEASURED on the stage's ――返事をしてよ。: the detector's box read the dash as -, the grown
        // crop read no dash at all, and taking it lost the symbol this exists to find.
        if (!growth.Left && Lead(g) != Lead(o)) return false;
        if (!growth.Right && Trail(g) != Trail(o)) return false;
        if (growth.Left && Lead(g).Length < Lead(o).Length) return false;
        if (growth.Right && Trail(g).Length < Trail(o).Length) return false;
        // A side that read nothing more is not refused here but put back on its own — see ReadMore.
        return true;

        static bool IsCjk(char ch) =>
            ch is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or
                >= '\uF900' and <= '\uFAFF';

        // Full-width forms, CJK punctuation, and the ellipsis and dashes a Japanese line uses.
        static bool IsCjkSymbol(char ch) =>
            ch is >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF' or '\u2026' or '\u2025' or
                '\u2014' or '\u2015';

        // A cut glyph is a character of the line's own script: MEASURED, letting digits through read
        // 3日前 as 33日前 off the edge of a counter beside it.
        static bool Cut(string added, bool cut) => added.Length == 0 || cut && added.Length <= 2 && added.All(IsCjk);

        static string Normalise(string? text) =>
            string.Concat((text ?? "").Normalize(System.Text.NormalizationForm.FormKC).Where(ch => !char.IsWhiteSpace(ch)));

        static bool IsSymbol(char ch) => Realtime.ShortReadingDetection.IsLineEdgeSymbols(ch.ToString());
        static string Words(string text) => string.Concat(text.Where(ch => !IsSymbol(ch)));
        static string Lead(string text) => string.Concat(text.TakeWhile(IsSymbol));
        static string Trail(string text) => string.Concat(text.Reverse().TakeWhile(IsSymbol));
    }

    private readonly record struct Band(int Top, int Bottom, int Glyph);

    // Out from the line's last ink in one direction, piece by piece: a piece is a run of inked
    // columns, and the walk carries on across a blank run no wider than GapLimit. Every piece has
    // to look like part of this line; the first that does not ends the walk where it is.
    private static (int Edge, bool Cut, bool Thin, bool Dash, bool Strokes) Walk(
        SKBitmap image, Band band, Colour ink, int from, int step, IReadOnlyList<SKRectI> onLine, int edge,
        Func<SKRectI, (float Max, float Mean)>? probability)
    {
        var firstGap = -1;
        var cutFill = 1.0;
        var thin = true;
        var pieces = 0;
        var lone = default(SKRectI);
        var glyph = band.Glyph;
        var maxGap = Math.Max(2, (int)(glyph * GapLimit));
        var budget = from + step * (int)(glyph * Reach);
        var limit = budget;
        // Another box on the line is a wall: the walk does not go into what it holds.
        foreach (var o in onLine)
        {
            if (step > 0 && o.Right > from + 1) limit = Math.Min(limit, Math.Max(from + 1, o.Left));
            if (step < 0 && o.Left < from) limit = Math.Max(limit, Math.Min(from, o.Right));
        }
        var wall = limit != budget;
        limit = Math.Clamp(limit, 0, image.Width);

        var reached = from;
        var x = from + step;
        while (InRange(x))
        {
            // Across the blank run.
            var gap = 0;
            while (InRange(x) && !Column(image, x, band, ink))
            {
                if (++gap > maxGap)
                {
                    Trace?.Invoke($"walk {step:+0;-0} from {from}: gap over {maxGap} at {x}");
                    return Done();
                }
                x += step;
            }
            if (!InRange(x)) return Done();

            // The piece.
            var start = x;
            while (InRange(x) && Column(image, x, band, ink)) x += step;
            var end = x - step;
            // A piece the budget cut through is not a symbol that ended: a rule, a picture's edge.
            if (!InRange(x) && x == limit && !onLine.Any(o => Touches(o, x)))
            {
                Trace?.Invoke($"walk {step:+0;-0} from {from}: piece {start}-{end} cut by budget");
                return Done();
            }
            if (!LooksLikeGlyph(image, band, ink, Math.Min(start, end), Math.Max(start, end), cut: gap == 0, out var dash,
                    probability, out var shape, out var fill))
            {
                Trace?.Invoke($"walk {step:+0;-0} from {from}: piece {start}-{end} not a glyph");
                return Done();
            }
            Trace?.Invoke($"walk {step:+0;-0} from {from}: piece {start}-{end} taken (gap {gap})");
            reached = end;
            if (firstGap < 0 && gap == 0) cutFill = fill;
            if (firstGap < 0) firstGap = gap;
            thin &= dash;
            pieces++;
            lone = shape;
        }
        return Done();

        bool InRange(int c) => step > 0 ? c < limit : c >= limit && c >= 0;
        bool Touches(SKRectI o, int c) => step > 0 ? o.Left <= c : o.Right >= c;

        (int, bool, bool, bool, bool) Done()
        {
            var edgeNow = NewEdge();
            var grew = step > 0 ? edgeNow > edge : edgeNow < edge;
            return (edgeNow, firstGap == 0, firstGap >= 0 && thin, grew && pieces == 1 && IsDash(lone, band),
                firstGap == 0 && cutFill <= CutStrokes);
        }

        int NewEdge()
        {
            // Ink that never gets past the box's own edge is already in the crop: nothing was cut.
            // Even one column past it counts — the ） closing user-6 ran 1px past its box and was
            // not read until the crop took it in with room to spare.
            if (step > 0 ? reached < edge : reached >= edge) return edge;
            // Ink that runs all the way to the next box on the line is the middle of one line the
            // detector cut in two, not an end of it. Both boxes' walks reach it, and both taking it
            // makes two overlapping crops that read the symbols twice — measured on user-6 nudged
            // 8px, 化け物…… and …！？ came back as two lines. The box on the left takes it; the walk
            // leftwards out of the box on the right gives it up.
            if (wall && Math.Abs(limit - reached) <= maxGap && step < 0) return edge;
            var margin = Math.Max(1, (int)Math.Round(glyph * Margin));
            return step > 0
                ? Math.Min(wall ? limit : image.Width, Math.Max(edge, reached + 1 + margin))
                : Math.Max(wall ? limit : 0, Math.Min(edge, reached - margin));
        }
    }

    private static bool Column(SKBitmap image, int x, Band band, Colour ink)
    {
        for (var y = band.Top; y < band.Bottom; y++)
            if (ink.Is(image.GetPixel(x, y))) return true;
        return false;
    }

    // Whether a piece of ink is part of this line rather than something that happens to sit beside
    // it: inside the line's band and not running out of it, no taller than the line's glyphs, and
    // not a solid shape the size of a glyph (the blinking ▼ a game puts after the last word).
    private static bool LooksLikeGlyph(
        SKBitmap image, Band band, Colour ink, int x0, int x1, bool cut, out bool dash,
        Func<SKRectI, (float Max, float Mean)>? probability, out SKRectI shape, out double fill)
    {
        dash = false;
        shape = default;
        fill = 1;
        var glyph = band.Glyph;
        // The piece's connected ink, followed up and down past the band: a glyph stops inside it,
        // a picture or a frame does not.
        var reach = Math.Max(2, glyph / 2);
        var wTop = Math.Max(0, band.Top - reach);
        var wBottom = Math.Min(image.Height, band.Bottom + reach);
        var wLeft = Math.Max(0, x0 - 2);
        var wRight = Math.Min(image.Width, x1 + 3);
        var w = wRight - wLeft;
        var h = wBottom - wTop;
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (var x = x0; x <= x1; x++)
            for (var y = band.Top; y < band.Bottom; y++)
            {
                var i = (y - wTop) * w + (x - wLeft);
                if (seen[i] || !ink.Is(image.GetPixel(x, y))) continue;
                seen[i] = true;
                stack.Push(i);
            }

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue, count = 0;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            int x = i % w + wLeft, y = i / w + wTop;
            count++;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < wLeft || ny < wTop || nx >= wRight || ny >= wBottom) continue;
                    var n = (ny - wTop) * w + (nx - wLeft);
                    if (seen[n] || !ink.Is(image.GetPixel(nx, ny))) continue;
                    seen[n] = true;
                    stack.Push(n);
                }
        }
        if (count == 0) return false;
        shape = new SKRectI(minX, minY, maxX + 1, maxY + 1);
        fill = (double)count / ((maxX - minX + 1) * (maxY - minY + 1));
        Trace?.Invoke($"  piece x={minX}-{maxX} y={minY}-{maxY} band={band.Top}-{band.Bottom} glyph={glyph} fill={(double)count / ((maxX - minX + 1) * (maxY - minY + 1)):0.00}");

        var slack = Math.Max(1, glyph / 5);
        // Runs out of the line: a frame, a picture, the line above or below.
        if (minY < band.Top - slack || maxY >= band.Bottom + slack) return false;
        var pw = maxX - minX + 1;
        var ph = maxY - minY + 1;
        dash = pw > ph * 3 && ph <= glyph * 0.25;
        if (ph > glyph * 1.25) return false;
        // Specks: video grain and compression noise beside a subtitle are pixels, not symbols.
        // MEASURED: the smallest real symbol on the material this was built on is a dot of the
        // stage font's 「……」, 3px on a 30px glyph (0.10); the specks that walked 絡みに行く into
        // 絡みに行< on a video subtitle were 1-3px on a 49px glyph (0.06 and under).
        if (Math.Max(pw, ph) < glyph * 0.09 || Math.Min(pw, ph) < 2 && pw * ph < 4) return false;
        // Past a blank run only symbols are taken: a dot, a dash, or something no wider than ！？）」
        // are. A whole character beyond a gap is a different question — a speaker's icon, a button
        // beside the line — and the one this answers is only whether the line's symbols were cut.
        // MEASURED: taking any glyph-sized piece put an X in front of 待ち続けることなんて (an icon
        // 0.95 of a glyph wide, 14px before it). The glyph the box cut through, with no blank
        // between, is any shape: user-4's 二, cut off 二階堂.
        if (!cut && pw > glyph * PieceWidth && ph > glyph * 0.25) return false;
        // The detector's probability does not tell a cut glyph from an icon the box cut into, so it
        // is only traced, for OcrHarness (the map is kept only there). MEASURED over every glyph
        // this rule recovered or wrongly took in the corpora: the hook icon read into レフックを使用
        // peaks at 0.98, while エマ's エ peaks at 0.01, 一週年's 一 at 0.00 and しろは's し at 0.11.
        if (cut && probability is not null)
        {
            var p = probability(new SKRectI(minX, minY, maxX + 1, maxY + 1));
            Trace?.Invoke($"  cut piece x={minX}-{maxX} y={minY}-{maxY} pmax={p.Max:0.000} pmean={p.Mean:0.000}");
        }
        // A wide piece has to be thin — a dash. A wide, tall one is a picture.
        if (pw > glyph * 1.3 && ph > glyph * 0.3) return false;
        // A solid shape the size of a glyph: the ▼ prompt. Glyphs are strokes and leave most of
        // their box empty.
        if (pw > glyph * 0.3 && ph > glyph * 0.3 && count > pw * ph * Solid) return false;
        return true;
    }

    /// <summary>The widest a piece past a blank run may be, in glyph heights, unless it is a dash.</summary>
    /// <remarks>
    /// ！？）」 measure 0.15-0.5 of a glyph in the fonts on the stage and in the user's captures; a kana
    /// or an icon is 0.8 and up. MEASURED: without it the walk put X in front of
    /// 待ち続けることなんて (an icon 0.95 of a glyph wide) and read buttons' icons into labels.
    /// </remarks>
    internal static double PieceWidth = 0.6;

    /// <summary>Share of its own box a piece may fill and still be strokes rather than a shape.</summary>
    /// <remarks>
    /// The stage's ▼ prompt fills about half of its box, as a triangle does; strokes leave most of
    /// it empty (。 0.3, ？ 0.25). Dots are exempt by size — a 6px dot is solid and is a symbol.
    /// </remarks>
    internal static double Solid = 0.42;

    /// <summary>The line's text colour, and the test for a pixel being more of it.</summary>
    internal readonly record struct Colour(bool Light, int Threshold, int Chroma)
    {
        internal bool Is(SKColor c)
        {
            var lum = Luminance(c);
            if (Light ? lum < Threshold : lum > Threshold) return false;
            return Math.Abs(ChromaOf(c) - Chroma) <= 48;
        }

        // The background is what the box's top and bottom rows hold — the detector's padding — and
        // the text is the far end of the box's luminance from it.
        internal static bool Of(SKBitmap image, int left, int top, int right, int bottom, out Colour colour)
        {
            colour = default;
            var edge = new List<int>();
            for (var x = left; x < right; x += 2)
            {
                edge.Add(Luminance(image.GetPixel(x, top)));
                edge.Add(Luminance(image.GetPixel(x, bottom - 1)));
            }
            edge.Sort();
            var background = edge[edge.Count / 2];

            var lums = new List<int>();
            for (var y = top; y < bottom; y += 2)
                for (var x = left; x < right; x += 2)
                    lums.Add(Luminance(image.GetPixel(x, y)));
            lums.Sort();
            var low = lums[(int)(lums.Count * 0.02)];
            var high = lums[(int)(lums.Count * 0.98)];
            var light = high - background >= background - low;
            var text = light ? high : low;
            var contrast = Math.Abs(text - background);
            if (contrast < 64) return false;

            // The colour of the strongest ink, for telling white text from a pale pink picture.
            var near = light ? background + contrast * 4 / 5 : background - contrast * 4 / 5;
            long chroma = 0, n = 0;
            for (var y = top; y < bottom; y += 2)
                for (var x = left; x < right; x += 2)
                {
                    var c = image.GetPixel(x, y);
                    var lum = Luminance(c);
                    if (light ? lum < near : lum > near) continue;
                    chroma += ChromaOf(c);
                    n++;
                }
            if (n == 0) return false;

            colour = new Colour(light, light ? background + contrast / 2 : background - contrast / 2, (int)(chroma / n));
            return true;
        }
    }

    private static int ChromaOf(SKColor c) =>
        Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));

    private static int Luminance(SKColor c) => (c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8;
}
