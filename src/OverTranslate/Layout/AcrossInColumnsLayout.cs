// UseWindowsForms brings System.Drawing into the implicit usings, which has a Size of its own.
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// How far a translation set across a vertical source may reach beyond that source: to either
/// side, and below it. Each is extra room past the source's own edge, never negative.
/// </summary>
internal readonly record struct AcrossInColumnsRoom(double Left, double Right, double Below)
{
    /// <summary>No room past the source at all.</summary>
    public static readonly AcrossInColumnsRoom None = new(0, 0, 0);

    /// <summary>
    /// The room round <paramref name="source"/> before it meets the edge of <paramref name="limit"/>
    /// or another block, all in the same units.
    /// </summary>
    /// <remarks>
    /// <para>Sideways, a neighbour counts only if it shares some of the source's height, and only
    /// half the gap to it is this block's: the neighbour is very likely another balloon being set
    /// across too, and two bubbles each taking the whole gap would meet in the middle of it twice
    /// over. The edge of the limit has nobody on the other side, so that gap is taken whole.</para>
    ///
    /// <para>Below, a neighbour counts only if it shares some of the source's width — a balloon
    /// further down the page in the same strip.</para>
    ///
    /// <para>A neighbour whose box overlaps the source's closes the sides it is on altogether: left
    /// or right by where its centre is, and below too when its centre is lower. That is two reading
    /// blocks of one balloon, which the manga reader pads, so their boxes cross: neither was beside
    /// or below the other by the rules above, and each was handed the whole page on that side. On
    /// vertical-image-ja2 page 19 14 55 the balloon's two blocks (711–794 and 637–718 across,
    /// overlapping by 7px) set their translations straight over each other.</para>
    /// </remarks>
    public static AcrossInColumnsRoom Within(Rect source, IEnumerable<Rect> others, Rect limit)
    {
        double left = Math.Max(0, source.Left - limit.Left);
        double right = Math.Max(0, limit.Right - source.Right);
        double below = Math.Max(0, limit.Bottom - source.Bottom);

        foreach (var other in others)
        {
            if (other.IsEmpty || other == source) continue;

            bool sharesHeight = other.Top < source.Bottom && other.Bottom > source.Top;
            bool sharesWidth = other.Left < source.Right && other.Right > source.Left;
            if (sharesHeight && other.Right <= source.Left)
                left = Math.Min(left, (source.Left - other.Right) / 2);
            else if (sharesHeight && other.Left >= source.Right)
                right = Math.Min(right, (other.Left - source.Right) / 2);
            else if (sharesWidth && other.Top >= source.Bottom)
                below = Math.Min(below, other.Top - source.Bottom);
            else if (sharesHeight && sharesWidth)
            {
                if (other.Left + other.Right < source.Left + source.Right) left = 0;
                else right = 0;
                if (other.Top + other.Bottom > source.Top + source.Bottom) below = 0;
            }
        }

        return new(left, right, below);
    }
}

/// <summary>
/// A translation set across in the place of a vertical source: the size it is drawn at, and the
/// text box it wraps in, placed relative to the source's top-left corner.
/// </summary>
/// <param name="FontSize">The size drawn at.</param>
/// <param name="Left">The text box's left edge, from the source's left edge (negative is further left).</param>
/// <param name="Top">The text box's top edge, from the source's top edge.</param>
/// <param name="Width">The width the TextBlock is given, and wraps at.</param>
/// <param name="Height">How tall the text comes out at that width and size.</param>
internal readonly record struct AcrossInColumnsFit(
    double FontSize, double Left, double Top, double Width, double Height);

/// <summary>
/// The layout for a vertical source whose translation is set across — see
/// <see cref="VerticalTargetLanguages"/>. The box it fills is the column group, which is narrow
/// and tall: one column is often too narrow for two Latin letters side by side.
/// </summary>
/// <remarks>
/// <para>The translation stays on the source, wrapped inside the column group's height, and is
/// allowed a little wider than the group: the balloon round a column group is wider than the
/// columns, by roughly a column on each side, and that margin is where a lettered translation of a
/// manga page puts its words too. It is centred on the group, both ways — a balloon has no corner
/// the sentence starts from, so a translation shorter than its source sits in the middle of it.</para>
///
/// <para>The search goes size first, then width, then height, and gives up readability last:</para>
/// <list type="number">
/// <item>Each size from the preferred one down to the readable floor, at the narrowest width that
/// holds its longest word and fits the group's height, up to the comfortable width — the group
/// plus one preferred size a side.</item>
/// <item>The same, as wide as the room beside the source allows.</item>
/// <item>The same again, now also allowed to run down into the room below.</item>
/// <item>For a sentence of ordinary words only: below the readable floor, down to the emergency
/// size, every word still whole.</item>
/// <item>The readable sizes once more, now letting a word wider than all of that room break
/// inside it — first only at sizes where it breaks once, then wherever it has to.</item>
/// <item>Below the readable floor, down to the emergency size, in all of that room.</item>
/// <item>The emergency size, as wide as the room allows, however tall that comes out. The box is
/// never cut short: a bubble that runs past its balloon is visible, a sentence that stops early
/// reads as a finished one.</item>
/// </list>
///
/// <para>A long word is split only when no readable size holds it whole in the room — a German
/// or Finnish compound in a one-column balloon by the page edge. Shrinking it whole to the
/// emergency size was tried first and rejected on the rendered page: a 34-letter compound came out
/// as one line of type too small to read, where broken across two lines at a readable size it is
/// merely broken. Breaking it at the largest size that fits the height was rejected too, on the
/// same page: "Donaudampfschifffahrtsgesellschaft!" came out in four pieces, which reads as four
/// words. So a size is given up first for a word in two pieces, and only after that for more.
/// The TextBlock does the breaking, as it does for any run wider than its line, and without a
/// hyphen: WPF hyphenates only with a language dictionary it does not have for most targets, and
/// the measurement would have to agree with it.</para>
///
/// <para>An ordinary word is never split while any size holds it whole. "Yesterd" and "ay's"
/// on two lines of a narrow narration card, on the Win10 VM, read as two fragments, where the
/// whole word a size or two under the readable floor still reads as a word; the compound that
/// justified breaking was 34 letters, and nothing that short can be read in pieces. Long means
/// more than <see cref="OrdinaryWordLength"/> letters.</para>
///
/// <para>The measurement is handed in as a delegate — text, size, and the width to wrap at or null
/// for one line — because in the windows it is a TextBlock's (see <see cref="WrappedTextMeasure"/>),
/// while these rules have nothing to do with WPF.</para>
/// </remarks>
internal static class AcrossInColumnsLayout
{
    private const double SizeStep = 0.5;

    /// <summary>
    /// The longest word that is never broken while some size holds it whole. Twelve letters takes in
    /// almost every everyday word of the Latin, Cyrillic and Greek targets with its punctuation
    /// ("exploration.", "Yesterday's"), and leaves out the compounds German and Finnish build.
    /// </summary>
    internal const int OrdinaryWordLength = 12;

    /// <param name="text">The translation.</param>
    /// <param name="sourceWidth">The column group's width.</param>
    /// <param name="sourceHeight">The column group's height: what the wrapped text is fitted to.</param>
    /// <param name="preferredFontSize">The size to start from.</param>
    /// <param name="readableFontSize">The floor of the first three passes.</param>
    /// <param name="emergencyFontSize">The floor of everything.</param>
    /// <param name="room">How far past the source the text box may reach.</param>
    /// <param name="keepSize">
    /// The size is the user's (realtime 固定): take all the room there is, below as well as beside,
    /// before giving any of the size back, rather than shrinking within the comfortable width and
    /// the group's own height first. The size still goes before a word is broken: a fixed size that
    /// has to put "exploratio" and "n." on two lines was measured in the real app and reads worse
    /// than the same word a few pixels smaller — so the caller passes its usual readable floor, not
    /// the fixed size, and the size is given back only where the room has run out.
    /// </param>
    public static AcrossInColumnsFit Fit(
        string text,
        double sourceWidth,
        double sourceHeight,
        double preferredFontSize,
        double readableFontSize,
        double emergencyFontSize,
        AcrossInColumnsRoom room,
        Func<string, double, double?, Size> measure,
        bool keepSize = false)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);
        emergencyFontSize = Math.Max(1, emergencyFontSize);
        readableFontSize = Math.Max(emergencyFontSize, readableFontSize);
        preferredFontSize = Math.Max(readableFontSize, preferredFontSize);

        double widest = sourceWidth + room.Left + room.Right;
        double comfortable = keepSize ? widest : Math.Min(widest, sourceWidth + preferredFontSize * 2);
        double tallest = sourceHeight + room.Below;
        double groupHeight = keepSize ? tallest : sourceHeight;
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var found =
            Search(preferredFontSize, readableFontSize, comfortable, groupHeight)
            ?? Search(preferredFontSize, readableFontSize, widest, groupHeight)
            ?? Search(preferredFontSize, readableFontSize, widest, tallest)
            ?? (words.All(word => word.Length <= OrdinaryWordLength)
                ? Search(readableFontSize - SizeStep, emergencyFontSize, widest, tallest)
                : null)
            ?? Search(preferredFontSize, readableFontSize, widest, tallest, maxPieces: 2)
            ?? Search(preferredFontSize, readableFontSize, widest, tallest, maxPieces: int.MaxValue)
            ?? Search(readableFontSize - SizeStep, emergencyFontSize, widest, tallest, maxPieces: int.MaxValue);

        if (found is not { } fit)
        {
            // Nothing fits: the smallest size, as wide as there is room for (and never narrower than
            // the source, which it has to cover), however tall that is.
            double width = Math.Max(sourceWidth, Math.Min(widest, LongestWord(emergencyFontSize)));
            fit = (emergencyFontSize, width, measure(text, emergencyFontSize, width).Height);
        }

        return Place(fit.Size, fit.Width, fit.Height);

        (double Size, double Width, double Height)? Search(
            double from, double to, double maxWidth, double maxHeight, int maxPieces = 1)
        {
            for (double size = from; size >= to - 0.001; size -= SizeStep)
            {
                double narrowest = Math.Max(sourceWidth, LongestWord(size));
                if (narrowest > maxWidth)
                {
                    // How many lines the longest word would take at the widest the room allows.
                    if (narrowest > maxWidth * maxPieces) continue;
                    narrowest = Math.Max(sourceWidth, maxWidth);
                }

                // Height falls as the width grows, so if the widest allowed does not fit, nothing
                // narrower will — one measurement rules a size out.
                var atWidest = measure(text, size, maxWidth);
                if (atWidest.Height > maxHeight) continue;

                return Narrowest(size, narrowest, maxWidth, atWidest.Height, maxHeight);
            }

            return null;
        }

        // The narrowest width that still fits, by bisection: the text fills the group's shape rather
        // than spreading into a wide, short band across the middle of a tall balloon.
        (double Size, double Width, double Height) Narrowest(
            double size, double low, double high, double highHeight, double maxHeight)
        {
            var atLow = measure(text, size, low);
            if (atLow.Height <= maxHeight) return (size, low, atLow.Height);

            while (high - low > 1)
            {
                double middle = (low + high) / 2;
                var atMiddle = measure(text, size, middle);
                if (atMiddle.Height <= maxHeight)
                {
                    high = middle;
                    highHeight = atMiddle.Height;
                }
                else
                {
                    low = middle;
                }
            }

            return (size, high, highHeight);
        }

        double LongestWord(double size)
        {
            double longest = 0;
            foreach (var word in words)
                longest = Math.Max(longest, measure(word, size, null).Width);
            // A pixel of slack so rounding cannot leave the TextBlock a hair narrower than its word.
            return Math.Ceiling(longest) + 1;
        }

        AcrossInColumnsFit Place(double size, double width, double height)
        {
            // Centred on the source, then moved back inside the room on whichever side ran out.
            double left = (sourceWidth - width) / 2;
            left = Math.Max(left, -room.Left);
            left = Math.Min(left, sourceWidth + room.Right - width);
            left = Math.Min(0, left);

            // Centred down the source while it fits; past that it starts at the source's top and
            // runs into the room below, never above, which is the balloon's neighbour.
            double top = height <= sourceHeight ? (sourceHeight - height) / 2 : 0;
            return new(size, left, top, width, height);
        }
    }
}
