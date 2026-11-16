using OverTranslate.Models;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// 讀取方式: when a change on screen is read. See <see cref="ResolvedProfile.Dialogue"/>.
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
/// <para>Equality is by value, which is how a change of settings tells whether a block's mode really
/// changed.</para>
/// </remarks>
public sealed record ResolvedProfile
{
    // First in the type: the presets below are resolved as it initialises, and static fields are
    // set in the order they are written.
    //
    // 辨識信心下限, step by step. The middle one is the measured floor itself; the gap it sits in
    // ran from 0.79 (scenery) to 0.92 (real lines), so the steps either side stay near it and the
    // ends are the user's choice of direction rather than anything measured.
    private static readonly double[] ConfidenceFloors =
        [0.50, 0.65, ShortReadingDetection.ShortTextConfidenceFloor, 0.88, 0.95];

    // 行距容許, step by step: the factor on the space between rows. TODO(A3): measure.
    private static readonly double[] LineSpacingFactors = [0.6, 0.8, 1.0, 1.25, 1.5];

    /// <summary>The mode this was resolved from — a preset's id or a custom mode's.</summary>
    internal string Id { get; init; } = RealtimePresets.StandardId;

    /// <summary>分行處理: Auto (自動判斷), Subtitle (整段) or Panel (逐項).</summary>
    internal RealtimeBlockMode Layout { get; init; } = RealtimeBlockMode.Auto;

    /// <summary>讀取方式.</summary>
    internal RealtimeReadTiming Timing { get; init; } = RealtimeReadTiming.Auto;

    /// <summary>
    /// 等文字停下, in polls: the most a change inside the watched text is waited out before it is
    /// read. Null is 自動 — none for a passage, <see cref="RealtimeRegionState.MaxTextUnsettledPolls"/>
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
    /// none or one this build cannot read.
    /// </summary>
    public static ResolvedProfile Resolve(RealtimeProfile stored)
    {
        // A custom mode follows a preset, and only a preset: one naming anything else follows 標準.
        var template = RealtimePresets.Create(stored.BasedOn);

        var layout = LayoutOf(stored.Layout) ?? LayoutOf(template.Layout)!.Value;
        var timing = TimingOf(stored.ReadTiming) ?? TimingOf(template.ReadTiming)!.Value;
        bool lettering = stored.MergeUnpunctuated ?? template.MergeUnpunctuated!.Value;
        int tendency = Step(stored.MergeTendency, template.MergeTendency!.Value);
        int spacing = Step(stored.LineSpacing, template.LineSpacing!.Value);
        int confidence = Step(stored.ConfidenceLevel, template.ConfidenceLevel!.Value);

        return new ResolvedProfile
        {
            Id = stored.Id,
            Layout = layout,
            Timing = timing,
            SettlePolls = SettlePollsOf(Number(stored.SettleSeconds, template.SettleSeconds!.Value)),
            ClearAfterEmptyReads = Math.Clamp(
                stored.ClearAfterEmptyReads ?? template.ClearAfterEmptyReads!.Value,
                MinClearAfterEmptyReads, MaxClearAfterEmptyReads),
            DetectorFraction = DetectorFractionOf(stored.DetectorPercent ?? template.DetectorPercent!.Value),
            Auto = RulesFor(tendency, spacing, lettering),
            IgnoreSmallFraction = Math.Clamp(
                stored.IgnoreSmallPercent ?? template.IgnoreSmallPercent!.Value,
                0, MaxIgnoreSmallPercent) / 100.0,
            ConfidenceFloor = ConfidenceFloors[confidence - 1],
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
            $"profile={Id} layout={Layout} timing={Timing} settle={(SettlePolls is { } polls ? polls.ToString() : "auto")} " +
            $"clearAfter={ClearAfterEmptyReads} detector={(DetectorFraction is { } fraction ? fraction.ToString("0.00") : "auto")} " +
            $"sparse={Auto.SparseZones} standsOut={Auto.StandsOut:0.###} lettering={Auto.LetteringZones}/{(Auto.Lettering ? "on" : "off")} " +
            $"leading={Auto.PassageLeading:0.###} stackGap={Auto.MaxStackGap:0.###} wrapAdvance={Auto.PanelGrouping.SolidLineAdvanceWhenWrapped:0.###} " +
            $"ignoreSmall={IgnoreSmallFraction:0.##} confidence={ConfidenceFloor:0.##}");

    // ── Ranges ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The steps of each 1–5 scale; 3 is what the presets use.</summary>
    public const int MinStep = 1;

    /// <inheritdoc cref="MinStep"/>
    public const int MaxStep = 5;

    /// <summary>The middle step, which every preset holds — see the type's remarks.</summary>
    public const int MiddleStep = 3;

    /// <summary>等文字停下's range, in seconds.</summary>
    public const double MaxSettleSeconds = 1.5;

    /// <summary>譯文保留's range, in readings.</summary>
    public const int MinClearAfterEmptyReads = 1;

    /// <inheritdoc cref="MinClearAfterEmptyReads"/>
    public const int MaxClearAfterEmptyReads = 4;

    /// <summary>辨識解析度's range, in percent.</summary>
    /// <remarks>
    /// 50 is a guess to be measured on a live stage: the strip fraction's own table has subtitles at
    /// 78% correct at half size against 94% at 0.85 — see <see cref="RealtimeDetectorSize.StripFraction"/>.
    /// </remarks>
    public const int MinDetectorPercent = 50;

    /// <inheritdoc cref="MinDetectorPercent"/>
    public const int MaxDetectorPercent = 100;

    /// <summary>忽略小字's range, in percent.</summary>
    public const int MaxIgnoreSmallPercent = 80;

    private static RealtimeBlockMode? LayoutOf(string? value) => value switch
    {
        RealtimePresets.LayoutAuto => RealtimeBlockMode.Auto,
        RealtimePresets.LayoutPassage => RealtimeBlockMode.Subtitle,
        RealtimePresets.LayoutItems => RealtimeBlockMode.Panel,
        _ => null,
    };

    private static RealtimeReadTiming? TimingOf(string? value) => value switch
    {
        RealtimePresets.TimingAuto => RealtimeReadTiming.Auto,
        RealtimePresets.TimingConfirm => RealtimeReadTiming.ReadThenConfirm,
        RealtimePresets.TimingSettle => RealtimeReadTiming.SettleThenRead,
        _ => null,
    };

    private static int Step(int? value, int template) => Math.Clamp(value ?? template, MinStep, MaxStep);

    private static double Number(double? value, double template) =>
        value is { } v && double.IsFinite(v) ? v : template;

    // Seconds to polls by the poll interval itself rather than PollsIn, which never answers less than
    // one: 0 seconds is "do not wait", and must stay that.
    private static int? SettlePollsOf(double seconds) =>
        seconds < 0
            ? null
            : (int)Math.Round(
                Math.Min(seconds, MaxSettleSeconds) / RealtimeRegionState.PollInterval.TotalSeconds);

    private static double? DetectorFractionOf(int percent) =>
        percent < 0 ? null : Math.Clamp(percent, MinDetectorPercent, MaxDetectorPercent) / 100.0;

    // 合併傾向 binds three of 自動's thresholds into one scale, and 行距容許 scales the space between
    // rows in all three groupers by one factor. The middle step of both is the measured rules
    // themselves — the instances, so a preset reads exactly as before.
    //
    // TODO(A3): every other step is a draft to be measured with OcrHarness --auto-probe replay
    // --profile; see .ai/realtime-profiles/tools.md. Until then they are the HANDOFF's figures.
    private static AutoRules RulesFor(int tendency, int spacing, bool lettering)
    {
        if (tendency == MiddleStep && spacing == MiddleStep)
            return lettering ? AutoRules.Default : AutoRules.Default with { Lettering = false };

        var rules = AutoRules.Default with { Lettering = lettering };
        rules = tendency switch
        {
            1 => rules with { SparseZones = 2, StandsOut = 1.6, LetteringZones = 2 },
            2 => rules with { SparseZones = 3, StandsOut = 1.45, LetteringZones = 3 },
            4 => rules with { SparseZones = 8, StandsOut = 1.15, LetteringZones = 6 },
            5 => rules with { SparseZones = int.MaxValue, StandsOut = 1.0, LetteringZones = 8 },
            _ => rules,
        };
        if (spacing == MiddleStep) return rules;

        // Only the space between the rows is scaled. Two of the three figures are advances — a
        // line's own height plus the gap — and multiplying those whole would loosen them far more
        // than the gap the third one measures.
        double k = LineSpacingFactors[spacing - 1];
        var wrapped = GroupingProfile.Realtime.SolidLineAdvanceWhenWrapped;
        return rules with
        {
            MaxStackGap = DialogueTextGrouper.MaxStackGap * k,
            PassageLeading = 1 + (RealtimeAutoLayout.PassageLeading - 1) * k,
            PanelGrouping = GroupingProfile.Realtime with { SolidLineAdvanceWhenWrapped = 1 + (wrapped - 1) * k },
        };
    }
}
