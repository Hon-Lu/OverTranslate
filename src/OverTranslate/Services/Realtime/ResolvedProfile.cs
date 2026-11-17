using OverTranslate.Models;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// How a change on screen is read — 畫面文字穩定後再讀取 as 不等待, a wait, or 自動. See
/// <see cref="ResolvedProfile.Dialogue"/>.
/// </summary>
internal enum RealtimeReadTiming
{
    /// <summary>As the block's groups read: a passage reads then confirms, items settle then read.</summary>
    Auto,

    /// <summary>Read the change at once, then read once more to confirm it — 字幕's way.</summary>
    ReadThenConfirm,

    /// <summary>Let the change settle for a poll, then read it once — 面板's way.</summary>
    SettleThenRead,
}

/// <summary>
/// How a block's lines are put into groups — 合併程度 (<see cref="RealtimeMergeLevels"/>) as the
/// pipeline runs it, the five 自動判斷 steps being one grouping with different rules.
/// </summary>
internal enum RealtimeMerge
{
    /// <summary>不合併: the panel grouper's lines, none joined to another — see <see cref="OcrTextBlockGrouper.GroupLines"/>.</summary>
    Lines,

    /// <summary>逐項: the panel grouper.</summary>
    Items,

    /// <summary>自動判斷: <see cref="RealtimeAutoLayout"/>, group by group.</summary>
    Auto,

    /// <summary>整段: the dialogue grouper.</summary>
    Passage,

    /// <summary>合併: the dialogue grouper, joining more — see <see cref="DialogueTextGrouper.Group"/>'s merge.</summary>
    Merged,
}

/// <summary>
/// The thresholds 自動 decides with (<see cref="RealtimeAutoLayout"/>), and the two groupers' row
/// spacing, which 自動 and the explicit layouts share.
/// </summary>
/// <param name="SparseZones">See <see cref="RealtimeAutoLayout.SparseZones"/>.</param>
/// <param name="StandsOut">See <see cref="RealtimeAutoLayout.StandsOut"/>.</param>
/// <param name="LetteringZones">See <see cref="RealtimeAutoLayout.LetteringZones"/>.</param>
/// <param name="LetteringAspect">See <see cref="RealtimeAutoLayout.LetteringAspect"/>. Not offered to the user.</param>
/// <param name="PassageLeading">See <see cref="RealtimeAutoLayout.PassageLeading"/>.</param>
/// <param name="Lettering">Whether unpunctuated rows may be a passage at all — 沒有標點也合併.</param>
/// <param name="MaxStackGap">See <see cref="DialogueTextGrouper.MaxStackGap"/>.</param>
/// <param name="PanelGrouping">The panel grouper's profile: <see cref="GroupingProfile.Realtime"/> or a variant of it.</param>
internal sealed record AutoRules(
    int SparseZones,
    double StandsOut,
    int LetteringZones,
    double LetteringAspect,
    double PassageLeading,
    bool Lettering,
    double MaxStackGap,
    GroupingProfile PanelGrouping)
{
    /// <summary>The figures 自動 was measured with — the constants themselves, not copies.</summary>
    public static AutoRules Default { get; } = new(
        RealtimeAutoLayout.SparseZones,
        RealtimeAutoLayout.StandsOut,
        RealtimeAutoLayout.LetteringZones,
        RealtimeAutoLayout.LetteringAspect,
        RealtimeAutoLayout.PassageLeading,
        Lettering: true,
        DialogueTextGrouper.MaxStackGap,
        GroupingProfile.Realtime);
}

/// <summary>
/// A mode as the realtime pipeline runs it: the user's values (<see cref="RealtimeProfile"/>)
/// turned into polls, fractions and thresholds, every one of them in range.
/// </summary>
/// <remarks>
/// <para>Immutable, and built once per block when a session starts or the modes change, so nothing
/// on the poll path parses or clamps anything. Public only because <see cref="RealtimeRegion"/>
/// carries it; every member is internal.</para>
///
/// <para>The three presets resolve to exactly what 即時翻譯 ran on before modes existed: each 自動
/// and each middle step hands back the original constant or the original instance
/// (<see cref="AutoRules.Default"/>, <see cref="GroupingProfile.Realtime"/>) rather than a figure
/// computed from a multiplier, whose floating point need not land on the constant.</para>
///
/// <para>字幕・漫畫 and 遊戲介面・聊天室 carry their wait and detector size as figures rather than as
/// 自動, and the figures are what 自動 came to on them, step for step: 不等待 reads at once and
/// confirms with a wait of 0 polls, which is what a passage's null came to; one poll is what items'
/// null came to; and 85 and 68 percent divide back to <see cref="RealtimeDetectorSize.StripFraction"/>
/// and <see cref="RealtimeDetectorSize.PanelFraction"/> exactly, each starting the same chain of
/// fallbacks — see <see cref="RealtimeDetectorSize.For"/>. Pinned by RealtimeTimingDetectorTests.</para>
///
/// <para>Equality is by value, which is how a change of settings tells whether a block's mode really
/// changed.</para>
/// </remarks>
public sealed record ResolvedProfile
{
    // First in the type: the presets below are resolved as it initialises, and static fields are
    // set in the order they are written.
    //
    // 辨識信心下限, step by step. The middle one is the measured floor itself.
    //
    // NOT OFFERED: measured, and no step but the middle one did any good. Over the 自動 corpus the
    // short readings each step lets back in or takes out (D+M+P, of 6,620 detected lines):
    //
    //   0.60–0.65  105   scenery but for a label or two (設定, ムービー). The recogniser drops below 0.60
    //                    (OnnxOcrEngine.MinRecognitionConfidence), so 0.50 is 0.60.
    //   0.65–0.80  279   nine in ten scenery — 回, ①, {0}2, 4344726 — with the odd YAWN or label
    //   0.80–0.88  234   about half real: あれ?, ここは...?, 決定, セーブ, Skill, a name plate
    //   0.88–0.95  450   mostly real: 何なんですか？, names, a webtoon balloon's lines, menu items
    //
    // Lower only lets scenery back in, higher takes out short lines people read. Kept as a value so
    // a mode can carry it, but the scale is left for a reason to move it to turn up.
    private static readonly double[] ConfidenceFloors =
        [0.50, 0.65, ShortReadingDetection.ShortTextConfidenceFloor, 0.88, 0.95];

    // 行距容許 — a factor on the space between rows in all three groupers — is NOT OFFERED, and the
    // groupers always run on their measured row spacing.
    //
    // MEASURED on the 自動 corpus, where every line of dialogue already sits well inside the
    // measured spacing (rows step 0.74–1.18 line heights), so the corpus mostly showed what moving
    // it costs: frames whose groups differ from 標準, 自動 D/P/M and 整段 on the panel captures —
    //
    //   factor   自動 D/P/M   整段 P
    //    0.6     2 / 22 / 1     63     titles off their text, menus apart; a wrapped comment too
    //    0.8     0 / 13 / 0     35
    //    1.25    0 /  6 / 0     25
    //    1.5     1 / 10 / 2     41     also a menu joined; 逐項 moves on 4–6 frames only
    //
    // All it was for was prose set with readings between its lines (ja-ruby/novel.png, one passage
    // from 1.25 up): too rare to be worth a value nobody could tell from 合併程度.

    /// <summary>The mode this was resolved from — a preset's id or a custom mode's.</summary>
    internal string Id { get; init; } = RealtimePresets.StandardId;

    /// <summary>合併程度: how the lines are grouped. The 自動判斷 steps differ in <see cref="Auto"/>.</summary>
    internal RealtimeMerge Merge { get; init; } = RealtimeMerge.Auto;

    /// <summary>
    /// How the block is drawn and, with 自動 timing and detector size, read: Auto (自動判斷), Subtitle
    /// (整段 and 合併) or Panel (逐項 and 不合併).
    /// </summary>
    /// <remarks>
    /// The two ends of 合併程度 read as their neighbours: lines left apart are drawn as items, the
    /// whole block as one passage, and each is read the way its neighbour is.
    /// </remarks>
    internal RealtimeBlockMode Layout => Merge switch
    {
        RealtimeMerge.Lines or RealtimeMerge.Items => RealtimeBlockMode.Panel,
        RealtimeMerge.Passage or RealtimeMerge.Merged => RealtimeBlockMode.Subtitle,
        _ => RealtimeBlockMode.Auto,
    };

    /// <summary>
    /// How a change is read: Auto for 畫面文字穩定後再讀取 left on 自動, ReadThenConfirm for 不等待,
    /// SettleThenRead for any wait.
    /// </summary>
    internal RealtimeReadTiming Timing { get; init; } = RealtimeReadTiming.Auto;

    /// <summary>
    /// 畫面文字穩定後再讀取, in polls: the most a change inside the watched text is waited out before
    /// it is read. Null is 自動 — none for a passage, <see cref="RealtimeRegionState.MaxTextUnsettledPolls"/>
    /// for items.
    /// </summary>
    internal int? SettlePolls { get; init; }

    /// <summary>譯文保留 — see <see cref="RealtimeRegionState.EmptyPassesBeforeClearing"/>.</summary>
    internal int ClearAfterEmptyReads { get; init; } = RealtimeRegionState.EmptyPassesBeforeClearing;

    /// <summary>
    /// 辨識解析度, as a fraction of the block's longer side; null is 自動 — see
    /// <see cref="RealtimeDetectorSize.For"/>.
    /// </summary>
    internal double? DetectorFraction { get; init; }

    /// <summary>What 自動 decides with, and the row spacing both groupers use.</summary>
    internal AutoRules Auto { get; init; } = AutoRules.Default;

    /// <summary>The dialogue grouper's widest gap between stacked rows — see <see cref="AutoRules.MaxStackGap"/>.</summary>
    internal double MaxStackGap => Auto.MaxStackGap;

    /// <summary>The panel grouper's profile — see <see cref="AutoRules.PanelGrouping"/>.</summary>
    internal GroupingProfile PanelGrouping => Auto.PanelGrouping;

    /// <summary>
    /// 忽略小字: lines lower than this fraction of the frame's main text height are dropped before
    /// grouping. Zero is off, and then nothing is measured at all — see
    /// <see cref="OcrService.IgnoreSmallText"/>.
    /// </summary>
    internal double IgnoreSmallFraction { get; init; }

    /// <summary>
    /// 辨識信心下限: the score a short reading of text written across needs to be believed — see
    /// <see cref="ShortReadingDetection.ShortTextConfidenceFloor"/>. Columns keep the constant.
    /// </summary>
    internal double ConfidenceFloor { get; init; } = ShortReadingDetection.ShortTextConfidenceFloor;

    /// <summary>標準.</summary>
    public static ResolvedProfile Standard { get; } = Resolve(RealtimePresets.Create(RealtimePresets.StandardId));

    /// <summary>字幕・漫畫.</summary>
    public static ResolvedProfile Subtitle { get; } = Resolve(RealtimePresets.Create(RealtimePresets.SubtitleId));

    /// <summary>遊戲介面・聊天室.</summary>
    public static ResolvedProfile Interface { get; } = Resolve(RealtimePresets.Create(RealtimePresets.InterfaceId));

    /// <summary>
    /// The preset a layout used to stand for, for the callers that still name one: tools, tests and
    /// the column path's rows across a page, which always read on the presets' figures.
    /// </summary>
    internal static ResolvedProfile ForLayout(RealtimeBlockMode layout) => layout switch
    {
        RealtimeBlockMode.Subtitle => Subtitle,
        RealtimeBlockMode.Panel => Interface,
        _ => Standard,
    };

    /// <summary>
    /// The mode a block names: a preset, one of <paramref name="custom"/>, or 標準 for an id that names
    /// neither — a mode deleted since, or a file from another build.
    /// </summary>
    public static ResolvedProfile Resolve(string? id, IReadOnlyList<RealtimeProfile>? custom)
    {
        if (RealtimePresets.IsPreset(id))
            return id switch
            {
                RealtimePresets.SubtitleId => Subtitle,
                RealtimePresets.InterfaceId => Interface,
                _ => Standard,
            };
        var stored = custom?.FirstOrDefault(profile => profile is not null && profile.Id == id);
        return stored is null ? Standard : Resolve(stored);
    }

    /// <summary>
    /// One stored mode: each value it sets, held to its range, and the preset's wherever it sets
    /// none or one this build cannot read — see <see cref="RealtimeProfile.Filled"/>.
    /// </summary>
    public static ResolvedProfile Resolve(RealtimeProfile stored)
    {
        var filled = stored.Filled();
        int level = filled.MergeLevel!.Value;
        var settle = SettlePollsOf(filled.SettleSeconds!.Value);
        return new ResolvedProfile
        {
            Id = filled.Id,
            Merge = MergeOf(level),
            // The one value decides both: no wait is read at once and confirmed, any wait is waited
            // out and read once, and 自動 leaves both to what the block is read as.
            Timing = settle switch
            {
                null => RealtimeReadTiming.Auto,
                0 => RealtimeReadTiming.ReadThenConfirm,
                _ => RealtimeReadTiming.SettleThenRead,
            },
            SettlePolls = settle,
            ClearAfterEmptyReads = filled.ClearAfterEmptyReads!.Value,
            DetectorFraction = filled.DetectorPercent < 0 ? null : filled.DetectorPercent!.Value / 100.0,
            // Off 自動判斷 沒有標點也合併 is not read, and is held at the presets' value so that a
            // mode differing from one in nothing that is read resolves equal to it.
            Auto = RulesFor(RealtimeMergeLevels.Tendency(level),
                !RealtimeMergeLevels.MergeUnpunctuatedApplies(level) || filled.MergeUnpunctuated!.Value),
            IgnoreSmallFraction = filled.IgnoreSmallPercent!.Value / 100.0,
            ConfidenceFloor = ConfidenceFloors[filled.ConfidenceLevel!.Value - RealtimeProfile.MinStep],
        };
    }

    /// <summary>
    /// Whether a block that reads as <paramref name="readsAs"/> right now is read the passage way —
    /// at once, then confirmed — rather than settled and read once.
    /// </summary>
    internal bool Dialogue(RealtimeBlockMode readsAs) => Timing switch
    {
        RealtimeReadTiming.ReadThenConfirm => true,
        RealtimeReadTiming.SettleThenRead => false,
        _ => readsAs == RealtimeBlockMode.Subtitle,
    };

    /// <summary>The polls a change in the watched text is waited out, as <see cref="RealtimeRegionState"/> applies it.</summary>
    internal int TextSettlePolls(bool dialogue) =>
        SettlePolls ?? (dialogue ? 0 : RealtimeRegionState.MaxTextUnsettledPolls);

    /// <summary>One line for the log: every value this block reads with, and nothing it read.</summary>
    internal string Describe() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"profile={Id} merge={Merge} timing={Timing} settle={(SettlePolls is { } polls ? polls.ToString() : "auto")} " +
            $"clearAfter={ClearAfterEmptyReads} detector={(DetectorFraction is { } fraction ? fraction.ToString("0.00") : "auto")} " +
            $"sparse={Auto.SparseZones} standsOut={Auto.StandsOut:0.###} lettering={Auto.LetteringZones}/{(Auto.Lettering ? "on" : "off")} " +
            $"leading={Auto.PassageLeading:0.###} stackGap={Auto.MaxStackGap:0.###} wrapAdvance={Auto.PanelGrouping.SolidLineAdvanceWhenWrapped:0.###} " +
            $"ignoreSmall={IgnoreSmallFraction:0.##} confidence={ConfidenceFloor:0.##}");

    // Filled values only: RealtimeProfile.Filled has already held the level to its range.
    private static RealtimeMerge MergeOf(int level) => level switch
    {
        RealtimeMergeLevels.Min => RealtimeMerge.Lines,
        RealtimeMergeLevels.Items => RealtimeMerge.Items,
        RealtimeMergeLevels.Passage => RealtimeMerge.Passage,
        RealtimeMergeLevels.Max => RealtimeMerge.Merged,
        _ => RealtimeMerge.Auto,
    };

    // Seconds to polls by the poll interval itself rather than PollsIn, which never answers less than
    // one: 0 seconds is "do not wait", and must stay that.
    private static int? SettlePollsOf(double seconds) =>
        seconds < 0 ? null : (int)Math.Round(seconds / RealtimeRegionState.PollInterval.TotalSeconds);

    // 自動判斷's five steps of 合併程度 (2–6, once 合併傾向 1–5 — the table's "step") bind three of
    // 自動's thresholds into one scale. The middle step is the measured rules themselves — the
    // instance, so a preset reads exactly as before.
    //
    // MEASURED with OcrHarness --auto-probe replay --profile over the 自動 corpus (591 captures:
    // 270 of dialogue D, 179 of panels P, 142 of both M). Frames whose groups (not just how they are
    // drawn) differ from 標準, and frames 自動 reads exactly as 字幕 on D / as 面板 on P:
    //
    //   step  sparse standsOut lettering   changed D/P/M   =字幕 on D   =面板 on P
    //    1      2      1.6        2        13 / 21 / 10      247         176
    //    2      3      1.45       3         4 / 15 /  4      256         170
    //    3      5      1.3        4         — (標準) —        259         155
    //    4      8      1.2        6         1 / 12 /  0      260         145
    //    5     12      1.1        8         1 / 25 /  0      260         133
    //
    // The low end splits game dialogue from the name plate and buttons beside it, and titles from
    // the text under them; the high end joins wrapped chat messages, reviews and item text. Step 5
    // stops short of the draft's "every frame is sparse" and 1.0: with those, 3,993 of 4,279 groups
    // on the panel captures were drawn as subtitles, and forum threads were joined across their
    // comments. No dialogue capture held more than eleven clusters.
    private static AutoRules RulesFor(int tendency, bool lettering)
    {
        if (tendency == RealtimeProfile.MiddleStep)
            return lettering ? AutoRules.Default : AutoRules.Default with { Lettering = false };

        var rules = AutoRules.Default with { Lettering = lettering };
        return tendency switch
        {
            1 => rules with { SparseZones = 2, StandsOut = 1.6, LetteringZones = 2 },
            2 => rules with { SparseZones = 3, StandsOut = 1.45, LetteringZones = 3 },
            4 => rules with { SparseZones = 8, StandsOut = 1.2, LetteringZones = 6 },
            5 => rules with { SparseZones = 12, StandsOut = 1.1, LetteringZones = 8 },
            _ => rules,
        };
    }
}
