using System.Windows;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Layout;

/// <summary>Which edge of its source a group's translation is set against.</summary>
/// <remarks>
/// No right: 譯文對齊 offers no 靠右, and 自動 setting a group flush right where the user can never
/// ask for it would only puzzle — see <see cref="RealtimeSourceAlignment.Of"/>.
/// </remarks>
internal enum SourceAlignment
{
    Left,
    Center,
}

/// <summary>
/// 譯文對齊 on one group: the source's own alignment, read off the edges of its rows, or the one the
/// user asked for.
/// </summary>
/// <remarks>
/// <para>Before 譯文對齊 a group's alignment came from what it was read as: a passage centred on its
/// source (#19), separate items from their left edge (dc996ab0, #316). Where those two meet on one
/// screen — an item's description among the menu round it, a visual novel's text box — the passage
/// was centred though its source was set flush left, and that, more than the two looks side by
/// side, is what read wrong.</para>
///
/// <para>MEASURED (OcrHarness --auto-probe align, the 自動 corpus: 591 captures, 標準's own groups,
/// 2026-11-18): of 5,375 groups, 416 have two rows or more, and 210 of those are now set otherwise
/// than before — 209 passages from their left edge (dialogue boxes, event and item text, comments,
/// chat) and a centred button caption. Ten of the 209 drawn at random were all flush left. Rows
/// that line up on the right are read as saying nothing: of the 9 groups read so, 3 were wrong —
/// two lists whose bullets or avatars put their left edges apart, and a centred line cut off by
/// the edge of the capture — and 譯文對齊 offers no 靠右 to explain one that was right (a comic's
/// reward list, a page footer). The rest of the 416 either line up as they were drawn
/// already (a comic balloon, a centred subtitle) or say nothing: rows of one width, where left,
/// centre and right all agree, and ragged ones (a name plate joined to its line, a timestamp to its
/// message). Groups of one row are nine in ten of all groups and say nothing about alignment on
/// their own; read off their neighbours they contradicted each other a quarter of the time and were
/// fooled by balloons that happen to share an edge, so they are left as they were.</para>
///
/// <para>The tolerance is the corpus's: 0.3 of a row's height, and no less than 6 pixels so that a
/// row of small type is not held to its own anti-aliasing. Rows narrower than one another by less
/// than twice that are "one width" and say nothing.</para>
/// </remarks>
internal static class RealtimeSourceAlignment
{
    /// <summary>How much of the shorter box two pieces must share vertically to be one row.</summary>
    private const double SameRowOverlap = 0.6;

    /// <summary>How far edges may stray and still count as lined up, as a fraction of a row's height.</summary>
    private const double ToleranceOfHeight = 0.3;

    /// <summary>The least that tolerance is, in pixels.</summary>
    private const double MinTolerance = 6;

    /// <summary>
    /// Where a group's translation is set, or null to set it as the way it is drawn always has: a
    /// line centred on its source, a panel's rows from their left edge.
    /// </summary>
    /// <param name="setting">譯文對齊.</param>
    /// <param name="drawnAs">
    /// How the group is drawn: <see cref="RealtimeBlockMode.Subtitle"/> as one band,
    /// <see cref="RealtimeBlockMode.Panel"/> split back into its source rows.
    /// </param>
    /// <param name="lines">The group's source lines, in physical pixels.</param>
    /// <remarks>
    /// 靠左 on a panel answers null rather than Left: its rows already start at their left edge, and
    /// the panel's own fallback — a group that cannot be split, drawn as one band — keeps the
    /// centring it has always had, so 遊戲介面・聊天室, whose value this is, draws as it did before
    /// 譯文對齊 existed, to the pixel. 置中 on a band is what a band always did.
    /// </remarks>
    public static SourceAlignment? For(RealtimeTextAlign setting, RealtimeBlockMode drawnAs, IReadOnlyList<Rect>? lines) => setting switch
    {
        RealtimeTextAlign.Center => SourceAlignment.Center,
        RealtimeTextAlign.Left => drawnAs == RealtimeBlockMode.Panel ? null : SourceAlignment.Left,
        _ => Of(lines),
    };

    /// <summary>
    /// The alignment of a group's rows, or null where they do not show one: a single row, rows of one
    /// width, rows whose edges agree on no side, or rows that agree on the right — see the class
    /// remarks.
    /// </summary>
    /// <param name="lines">The group's source lines; pieces of one row are taken together.</param>
    public static SourceAlignment? Of(IReadOnlyList<Rect>? lines)
    {
        if (lines is not { Count: > 1 }) return null;
        var valid = lines.Where(line => !line.IsEmpty && line.Width > 0 && line.Height > 0
            && double.IsFinite(line.X) && double.IsFinite(line.Y) && double.IsFinite(line.Width) && double.IsFinite(line.Height)).ToList();
        if (valid.Count < 2) return null;

        var rows = Rows(valid);
        if (rows.Count < 2) return null;

        var heights = valid.Select(line => line.Height).Order().ToList();
        double tolerance = Math.Max(ToleranceOfHeight * heights[heights.Count / 2], MinTolerance);

        if (rows.Max(row => row.Width) - rows.Min(row => row.Width) < tolerance * 2) return null;

        // The right edge is still weighed, so rows set flush right are not taken for centred ones.
        static double Spread(IEnumerable<double> values) => values.Max() - values.Min();
        (SourceAlignment? Alignment, double Spread)[] sides =
        [
            (SourceAlignment.Left, Spread(rows.Select(row => row.Left))),
            (SourceAlignment.Center, Spread(rows.Select(row => row.Left + row.Width / 2))),
            (null, Spread(rows.Select(row => row.Right))),
        ];
        var best = sides[0];
        foreach (var side in sides)
            if (side.Spread < best.Spread) best = side;
        return best.Spread <= tolerance ? best.Alignment : null;
    }

    // Top to bottom; a piece joins the first row it shares SameRowOverlap of its height with.
    private static List<Rect> Rows(IEnumerable<Rect> lines)
    {
        var rows = new List<List<Rect>>();
        foreach (var line in lines.OrderBy(line => line.Y))
        {
            var row = rows.FirstOrDefault(row => row.Any(other => SameRow(other, line)));
            if (row is null) rows.Add([line]);
            else row.Add(line);
        }
        return rows.Select(row => row.Aggregate(Rect.Union)).ToList();
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * SameRowOverlap;
}
