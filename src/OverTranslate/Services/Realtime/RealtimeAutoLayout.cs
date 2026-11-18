using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// 自動 — one frame read both ways, and each group of text kept as the reading its own content
/// calls for.
/// </summary>
/// <remarks>
/// <para>The two modes are two priors about lines set one under another. 字幕 takes them for one
/// passage unless the geometry says otherwise (<see cref="DialogueTextGrouper"/>); 面板 takes them
/// for separate items unless a line shows it ran out of room and wrapped
/// (<see cref="OcrTextBlockGrouper"/> on <see cref="GroupingProfile.Realtime"/>). Each is right
/// about its own material and wrong about the other's: a speech balloon's four short lines never
/// look wrapped, and a menu's four entries are exactly the stack a passage is.</para>
///
/// <para>So neither prior is chosen for the block. Both groupers run — grouping costs microseconds
/// beside the recognition that fed it — and they only ever differ in one way that matters: the
/// dialogue grouper joined rows the panel grouper left apart. Each such group is asked whether it
/// reads as a passage (<see cref="IsPassage"/>). The ones that do are kept as the dialogue grouper
/// made them; everything else in the frame is the panel grouper's, which joins only on evidence
/// and is therefore the safe reading of whatever was not recognised as a passage. That is how a
/// dialogue box and the menu that replaces it are told apart in the same block, and a subtitle
/// from the interface it is shown over.</para>
///
/// <para>MEASURED on 520 captures (.ai/test-images — 270 of dialogue, subtitles and comics, 179 of
/// panels, menus, chats and pages, 71 of both on one screen) with OcrHarness --auto-probe; the
/// figures are with the rules they decided.</para>
/// </remarks>
internal static class RealtimeAutoLayout
{
    /// <summary>One group as a grouper built it, with the detected blocks it was built from.</summary>
    internal sealed record TextGroup(OcrTextBlock Output, IReadOnlyList<OcrTextBlock> Members);

    /// <summary>A group only the dialogue grouper made, and whether it was taken for a passage.</summary>
    internal sealed record Candidate(TextGroup Group, Shape Shape, bool Passage);

    /// <summary>One frame as 自動 read it.</summary>
    /// <param name="Groups">The groups, in reading order, each marked with what it was read as.</param>
    /// <param name="Candidates">Every group the two groupers disagreed over. Diagnostic.</param>
    /// <param name="Zones">How many separate clusters of text the frame holds — see <see cref="SparseZones"/>.</param>
    internal sealed record Reading(
        List<OcrTextBlock> Groups, IReadOnlyList<Candidate> Candidates, int Zones, bool Sparse);

    /// <summary>
    /// A frame with this many clusters of text or fewer is taken for one thing being read — a
    /// subtitle, a balloon or two, a dialogue box with its name plate — rather than a screen of
    /// interface.
    /// </summary>
    /// <remarks>
    /// A cluster is a set of lines either grouper joins. Measured: comic panels and subtitle strips
    /// hold 1 to 4, a dialogue box framed with its quick menu up to 7 or so, a game screen or a page
    /// from 10 to 78.
    /// </remarks>
    internal const int SparseZones = 5;

    /// <summary>
    /// The widest leading, in line heights, that rows with no punctuation may have and still be one
    /// passage. Lettering and subtitles measured 0.74 to 1.18; a menu's entries start at 1.33.
    /// </summary>
    internal const double PassageLeading = 1.25;

    /// <summary>
    /// How much taller than the frame's typical line text has to be, on a busy screen, to be read
    /// as the thing being followed rather than as more of the interface.
    /// </summary>
    internal const double StandsOut = 1.3;

    /// <summary>
    /// The most clusters a frame may hold for unpunctuated rows to be taken for lettering, and the
    /// longest such a row may be, in line heights.
    /// </summary>
    /// <remarks>
    /// Tighter than <see cref="SparseZones"/> because there is nothing but geometry to go by: a
    /// balloon's rows measured 2.9 to 13.8 line heights long in frames of 1 to 4 clusters, and the
    /// two wrong joins this rule made at five were a chat log's messages (17.6 long) and two lines
    /// of a character sheet.
    /// </remarks>
    internal const int LetteringZones = 4;

    /// <inheritdoc cref="LetteringZones"/>
    internal const double LetteringAspect = 14;

    /// <summary>The frame's groups, in reading order, each marked with what it was read as.</summary>
    internal static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks) => Read(blocks).Groups;

    /// <inheritdoc cref="Group(IReadOnlyList{OcrTextBlock})"/>
    /// <param name="rules">The block's mode's figures — see <see cref="ResolvedProfile.Auto"/>.</param>
    internal static List<OcrTextBlock> Group(IReadOnlyList<OcrTextBlock> blocks, AutoRules rules) =>
        Read(blocks, rules).Groups;

    /// <inheritdoc cref="Reading"/>
    internal static Reading Read(IReadOnlyList<OcrTextBlock> blocks) => Read(blocks, AutoRules.Default);

    /// <inheritdoc cref="Reading"/>
    /// <param name="rules">The block's mode's figures — see <see cref="ResolvedProfile.Auto"/>.</param>
    internal static Reading Read(IReadOnlyList<OcrTextBlock> blocks, AutoRules rules)
    {
        var input = blocks.ToList();
        var dialogueTrace = new GroupingTrace();
        var panelTrace = new GroupingTrace();
        var dialogue = Members(
            DialogueTextGrouper.Group(HorizontalRubyLines.Drop(input), dialogueTrace, null, rules.MaxStackGap),
            dialogueTrace, input);
        var panel = Members(
            OcrTextBlockGrouper.Group(input, rules.PanelGrouping, null, panelTrace), panelTrace, input);

        int zones = CountZones(input, dialogue, panel);
        bool sparse = zones <= rules.SparseZones;

        // What the dialogue grouper joined that the panel grouper did not: the only groups whose
        // reading is in question.
        var candidates = dialogue
            .Where(group => group.Members.Count > 1 && !panel.Any(other => Contains(other, group)))
            .Select(group =>
            {
                var shape = Describe(group.Members);
                return new Candidate(group, shape, IsPassage(shape, zones, StandsOutOf(group, shape, input, rules), rules));
            })
            .ToList();

        var passages = candidates.Where(candidate => candidate.Passage).Select(candidate => candidate.Group).ToList();

        // The rows of a stack that was not a passage: a menu, a list. Drawn as items wherever they
        // are, so a menu framed on its own is not set as so many subtitles.
        var stacked = new HashSet<OcrTextBlock>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates.Where(candidate => !candidate.Passage && candidate.Shape.Rows >= 3))
            stacked.UnionWith(candidate.Group.Members);

        // No passage among them, and the frame is the panel grouper's own answer, in its own order.
        // With some, the passages are taken out and the panel grouper is asked about what is left —
        // asked again rather than filtered, because a group of its first answer may hold a line a
        // passage has just claimed.
        List<TextGroup> kept;
        if (passages.Count == 0) kept = panel;
        else
        {
            var claimed = new HashSet<OcrTextBlock>(ReferenceEqualityComparer.Instance);
            foreach (var passage in passages) claimed.UnionWith(passage.Members);
            // And whatever else sits inside a passage: the readings printed over its kanji, which
            // the dialogue grouper took out before it grouped. Left for the panel grouper they
            // would come back as lines of their own, with the writing they belong to gone.
            var rest = input
                .Where(block => !claimed.Contains(block) &&
                    !passages.Any(passage => passage.Output.LayoutBounds.Contains(Centre(block.LayoutBounds))))
                .ToList();
            var restTrace = new GroupingTrace();
            kept = [.. passages, .. Members(
                OcrTextBlockGrouper.Group(rest, rules.PanelGrouping, null, restTrace), restTrace, rest)];
            kept = [.. kept.OrderBy(group => First(group).Y).ThenBy(group => First(group).X)];
        }

        var groups = kept
            .Select(group => group.Output with
            {
                ReadAs = passages.Contains(group)
                    ? RealtimeBlockMode.Subtitle
                    : ReadAs(group, zones, input, stacked, rules),
            })
            .ToList();
        return new Reading(groups, candidates, zones, sparse);
    }

    /// <summary>
    /// Whether rows the dialogue grouper joined read as one passage — a sentence wrapped across
    /// them — rather than as separate items that happen to be stacked.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED on the groups the dialogue grouper joined and the panel grouper did not: 60 in
    /// the dialogue captures, 281 in the interface ones. Their geometry does not part them — a
    /// balloon's four centred lines and a battle menu's seven are the same stack, leading and all
    /// (0.74–1.18 against 1.06–1.16) — but their text does. 55 of the 60 passages carry sentence
    /// punctuation, and an interface stack that carries it is prose in a panel — an item's
    /// description, a mission briefing — which is a passage there too.</para>
    ///
    /// <para>On a busy screen punctuation is not enough: a page of headlines or a forum index is
    /// rows of punctuated text that are not each other's continuation, and the dialogue grouper
    /// joins them by the dozen. There the passage also has to be what the screen is about — set
    /// visibly larger than the interface around it, as a subtitle or a dialogue box is. Prose that
    /// is the same size as everything else is left to the panel grouper, which joins the lines that
    /// show they wrapped.</para>
    ///
    /// <para>Without punctuation there is only the geometry, so it is believed only where there is
    /// little else in the frame to be confused with: rows set solid, each of them more than a word,
    /// in a frame that holds a handful of things. That is hand lettering with its full stop misread,
    /// a Korean caption, an unpunctuated English subtitle.</para>
    ///
    /// <para>One row the dialogue grouper put together from pieces is a passage on the same terms:
    /// two short sentences set apart on one subtitle line. Without punctuation it is a row of
    /// buttons.</para>
    /// </remarks>
    internal static bool IsPassage(Shape shape, int zones, bool standsOut) =>
        IsPassage(shape, zones, standsOut, AutoRules.Default);

    /// <inheritdoc cref="IsPassage(Shape, int, bool)"/>
    internal static bool IsPassage(Shape shape, int zones, bool standsOut, AutoRules rules)
    {
        bool sparse = zones <= rules.SparseZones;
        if (shape.Rows < 2) return sparse && shape.Marks > 0 && shape.MaxPerRow <= 3;
        if (shape.Marks > 0) return sparse || standsOut;
        return Lettering(shape, zones, rules);
    }

    // Rows with no punctuation that are a passage all the same — see IsPassage. Short rows only: a
    // long row that wrapped shows it, and the panel grouper joins it on that evidence; what it
    // cannot see is a balloon, whose lines end where the balloon does.
    //
    // And never rows that each open on a capital in text that has small letters too. A sentence
    // wrapped across rows carries on in lower case; a menu set solid — New Game, Continue, Game
    // Options — starts every row afresh. Lettering is capitals throughout and is not touched by
    // this. Found on a live stage, where exactly that menu was drawn as one paragraph.
    //
    // AutoRules.Lettering off turns this rule off, and unpunctuated rows are then never a passage;
    // no mode does that any more (沒有標點也合併 was taken back), but the rules still say it.
    private static bool Lettering(Shape shape, int zones, AutoRules rules) =>
        rules.Lettering &&
        zones <= rules.LetteringZones && shape.MaxAdvance <= rules.PassageLeading && shape.Wordy &&
        shape.WidestAspect <= rules.LetteringAspect && !shape.TitledRows;

    // Against the rest of the frame, not the whole of it: two lines of dialogue are half the
    // text on a screen that holds a name plate and three buttons besides, and would set the
    // typical height themselves.
    private static bool StandsOutOf(TextGroup group, Shape shape, List<OcrTextBlock> frame, AutoRules rules)
    {
        double typical = Median(frame
            .Where(block => !group.Members.Contains(block, ReferenceEqualityComparer.Instance))
            .Select(block => block.LayoutBounds.Height));
        return typical > 0 && shape.Height >= typical * rules.StandsOut;
    }

    /// <summary>How a group that is not a passage of the dialogue grouper's is to be drawn.</summary>
    /// <remarks>
    /// A subtitle's band grows about its source and may run past it; a panel row is held to the row
    /// it replaces. So the doubtful case is drawn as a row: a sentence held to its own box is a
    /// little small, a label let out of its box lies across its neighbours.
    /// </remarks>
    private static RealtimeBlockMode ReadAs(
        TextGroup group, int zones, List<OcrTextBlock> frame, HashSet<OcrTextBlock> stacked, AutoRules rules)
    {
        var shape = Describe(group.Members);
        bool sparse = zones <= rules.SparseZones;
        bool standsOut = StandsOutOf(group, shape, frame, rules);

        // Rows the panel grouper joined itself: a paragraph that showed it wrapped. Nothing is
        // being joined on this answer, only drawn, so punctuation is enough wherever it is — on a
        // live stage a dialogue box 1.2 times its interface's size was joined rightly and then set
        // row by row, because it did not stand out by the figure a join is held to.
        if (shape.Rows > 1)
            return shape.Marks > 0 || Lettering(shape, zones, rules)
                ? RealtimeBlockMode.Subtitle
                : RealtimeBlockMode.Panel;

        if (group.Members.Any(stacked.Contains)) return RealtimeBlockMode.Panel;
        if (sparse) return RealtimeBlockMode.Subtitle;

        // One line on a busy screen: a subtitle over an interface is larger than the interface, as
        // long as a sentence, and reads like one.
        return standsOut && shape.WidestAspect >= 5 && (shape.Marks > 0 || shape.Wordy)
            ? RealtimeBlockMode.Subtitle
            : RealtimeBlockMode.Panel;
    }

    private static int CountZones(List<OcrTextBlock> input, List<TextGroup> dialogue, List<TextGroup> panel)
    {
        var index = new Dictionary<OcrTextBlock, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < input.Count; i++) index[input[i]] = i;
        var parent = Enumerable.Range(0, input.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        var grouped = new bool[input.Count];
        foreach (var group in dialogue.Concat(panel))
        {
            int first = -1;
            foreach (var member in group.Members)
            {
                if (!index.TryGetValue(member, out var at)) continue;
                grouped[at] = true;
                if (first < 0) first = at;
                else parent[Find(at)] = Find(first);
            }
        }

        int zones = 0;
        for (int i = 0; i < input.Count; i++)
            if (grouped[i] && Find(i) == i) zones++;
        return zones;
    }

    /// <summary>Each group with the detected blocks it was built from.</summary>
    /// <remarks>
    /// <para>From the grouper's own trace, which is exact wherever it has an entry. It does not
    /// always: a line the grouper rebuilt on the way — around the readings printed over kanji, for
    /// one — is not the line it registered, and the group made of it traces back to nothing. Met on
    /// a dialogue box with furigana, where a group with no members had no rows to measure and the
    /// pass threw.</para>
    ///
    /// <para>So a group the trace cannot account for takes the blocks that lie inside it and
    /// belong to no other group, and one that still has none stands for itself. Every group the
    /// grouper returned is kept whatever the trace says: a group dropped here would be text read
    /// off the screen and never shown.</para>
    /// </remarks>
    private static List<TextGroup> Members(
        List<OcrTextBlock> outputs, GroupingTrace trace, IReadOnlyList<OcrTextBlock> input)
    {
        var blocks = trace.Blocks.ToDictionary(line => line.Id, line => line.Block);
        var sources = trace.Lines.ToDictionary(line => line.Id, line => line.SourceIds);
        var members = new List<List<OcrTextBlock>>(outputs.Count);
        var traced = new bool[outputs.Count];
        var taken = new HashSet<OcrTextBlock>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < outputs.Count; i++)
        {
            var own = new List<OcrTextBlock>();
            traced[i] = i < trace.Groups.Count && trace.Groups[i].Count > 0;
            if (i < trace.Groups.Count)
                foreach (var line in trace.Groups[i])
                {
                    if (!sources.TryGetValue(line, out var ids) || ids.Count == 0) { traced[i] = false; continue; }
                    foreach (var id in ids)
                    {
                        if (blocks.TryGetValue(id, out var block)) own.Add(block);
                        else traced[i] = false;
                    }
                }
            taken.UnionWith(own);
            members.Add(own);
        }

        var groups = new List<TextGroup>(outputs.Count);
        for (int i = 0; i < outputs.Count; i++)
        {
            if (!traced[i])
                foreach (var block in input)
                    if (!taken.Contains(block) && outputs[i].LayoutBounds.Contains(Centre(block.LayoutBounds)))
                    {
                        members[i].Add(block);
                        taken.Add(block);
                    }
            groups.Add(new TextGroup(outputs[i], members[i].Count > 0 ? members[i] : [outputs[i]]));
        }
        return groups;
    }

    private static System.Windows.Point Centre(Rect bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static bool Contains(TextGroup outer, TextGroup inner)
    {
        foreach (var member in inner.Members)
        {
            bool found = false;
            foreach (var other in outer.Members)
                if (ReferenceEquals(other, member)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    // Where a group starts, as both groupers order theirs: its top row, and that row's left end.
    private static System.Windows.Point First(TextGroup group)
    {
        if (group.Members.Count == 0) return group.Output.LayoutBounds.TopLeft;
        var top = group.Members.MinBy(member => member.LayoutBounds.Y)!.LayoutBounds;
        double left = group.Members
            .Where(member => SameRow(member.LayoutBounds, top))
            .Min(member => member.LayoutBounds.X);
        return new System.Windows.Point(left, top.Y);
    }

    /// <summary>What one group of detected blocks looks like as text set in rows.</summary>
    /// <param name="MaxAdvance">The widest step from one row's centre to the next, in line heights.</param>
    /// <param name="Marks">Sentence punctuation in it — see <see cref="CountMarks"/>.</param>
    /// <param name="Wordy">Whether each row holds more than a word: prose, where a label is a word or two.</param>
    /// <param name="TitledRows">Whether every row opens on a capital although the text has small letters: a list of titles.</param>
    internal readonly record struct Shape(
        int Rows, int MaxPerRow, double MaxAdvance, double MinAdvance, double Height,
        double CharactersPerRow, int LongestRow, int Marks, bool Wordy, double WidestAspect, bool TitledRows);

    internal static Shape Describe(IReadOnlyList<OcrTextBlock> members)
    {
        var rows = new List<List<OcrTextBlock>>();
        foreach (var block in members.OrderBy(member => member.LayoutBounds.Y))
        {
            List<OcrTextBlock>? row = null;
            foreach (var candidate in rows)
                if (candidate.Any(other => SameRow(other.LayoutBounds, block.LayoutBounds))) { row = candidate; break; }
            if (row is null) rows.Add([block]);
            else row.Add(block);
        }

        var spans = rows.Select(row => row.Select(block => block.LayoutBounds).Aggregate(Rect.Union)).ToList();
        double height = Median(members.Select(member => member.LayoutBounds.Height));
        double maxAdvance = 0, minAdvance = 0;
        for (int i = 1; i < spans.Count; i++)
        {
            double advance = ((spans[i].Top + spans[i].Bottom) - (spans[i - 1].Top + spans[i - 1].Bottom))
                / 2 / Math.Max(1, height);
            maxAdvance = i == 1 ? advance : Math.Max(maxAdvance, advance);
            minAdvance = i == 1 ? advance : Math.Min(minAdvance, advance);
        }

        var texts = rows
            .Select(row => string.Join(' ', row.OrderBy(block => block.LayoutBounds.X).Select(block => block.Text.Trim())))
            .ToList();
        int solid = texts.Sum(text => text.Count(c => !char.IsWhiteSpace(c)));
        int words = texts.Sum(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => word.Any(char.IsLetter)));
        int ideographs = texts.Sum(text => text.Count(IsUnspaced));
        // Six to a row for writing without spaces: a menu's entries run two to five characters.
        // Korean is counted in words, like anything else written with spaces.
        bool wordy = ideographs * 2 >= solid
            ? ideographs >= 12 && ideographs >= rows.Count * 6
            : words >= 3 && words * 2 >= rows.Count * 3;
        bool titled = rows.Count > 1 && texts.Any(text => text.Any(char.IsLower)) &&
            texts.All(text => text.FirstOrDefault(char.IsLetter) is var first && char.IsUpper(first));

        return new Shape(
            rows.Count, rows.Max(row => row.Count), maxAdvance, minAdvance, height,
            solid / (double)rows.Count, texts.Max(text => text.Count(c => !char.IsWhiteSpace(c))),
            texts.Sum(CountMarks), wordy,
            spans.Max(span => span.Width) / Math.Max(1, height), titled);
    }

    /// <summary>
    /// Sentence punctuation: what running text has and labels do not.
    /// </summary>
    /// <remarks>
    /// A full stop or a comma inside a number, a version or an address is not one — 1.5, 3,149,
    /// forum.example.com — so those two count only where a sentence would put them: before a space,
    /// a closing quote or the end of the row, and the full stop only after a whole word, which Lv.
    /// and No. are not. A colon is never one; it is what a label ends with.
    /// </remarks>
    internal static int CountMarks(string text)
    {
        int marks = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (Terminal.Contains(c)) marks++;
            else if (c is '.' or ',')
            {
                bool closes = i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]) || Closing.Contains(text[i + 1]) || text[i + 1] == '.';
                if (closes && (c == ',' ? i > 0 && !char.IsDigit(text[i - 1]) : EndsAWord(text, i))) marks++;
            }
        }
        return marks;
    }

    private static bool EndsAWord(string text, int stop)
    {
        if (stop == 0) return false;
        char before = text[stop - 1];
        if (before == '.' || Closing.Contains(before) || IsCjk(before)) return true;
        int letters = 0;
        for (int i = stop - 1; i >= 0 && char.IsLetter(text[i]); i--) letters++;
        return letters >= 3;
    }

    private const string Terminal = "!?。！？…‥、，~～";
    private const string Closing = "\"'”’」』)）";

    private static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '가' and <= '힣';

    // Kana and Han: writing set without spaces, where a word count says nothing.
    private static bool IsUnspaced(char c) => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿';

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * 0.6;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
