using OverTranslate.Models;

namespace OverTranslate.Services.Realtime;

/// <summary>讀取方式 as a block on a mode is really read.</summary>
internal enum ActualReadTiming
{
    /// <summary>先讀再確認.</summary>
    ReadThenConfirm,

    /// <summary>等穩定再讀.</summary>
    SettleThenRead,

    /// <summary>
    /// 依各組判斷: 自動 on a 自動判斷 step, where it follows what the block was last read as
    /// (<see cref="RealtimeRegionState.AutoReadsAs"/>), so nothing can be said before it is read.
    /// </summary>
    ByContent,
}

/// <summary>
/// What a mode's values come to, for the page to write beside each one: its own where it sets one,
/// what 自動 stands for where it does not. Display only — the pipeline reads
/// <see cref="ResolvedProfile"/>, from which this is worked out by the same rules.
/// </summary>
/// <param name="MergeLevel">合併程度, held to its range.</param>
/// <param name="MergeUnpunctuatedApplies">
/// Whether 沒有標點也合併 has any effect at this 合併程度 — only on 自動判斷's steps (2–6); greyed
/// out elsewhere.
/// </param>
/// <param name="ReadTimingIsAutomatic">讀取方式 is left on 自動.</param>
/// <param name="ReadTiming">How a change is read: as set, or what 自動 comes to.</param>
/// <param name="SettleIsAutomatic">等文字停下 is left on 自動.</param>
/// <param name="SettleSeconds">
/// How long a change inside the text is waited out, in seconds rounded to the hundredth — 0 is
/// 不等待. Null for 依各組判斷, which only 自動 on a 自動判斷 step with 讀取方式 on 自動 is.
/// </param>
/// <param name="DetectorIsAutomatic">辨識解析度 is left on 自動.</param>
/// <param name="DetectorPercent">
/// 辨識解析度 in percent of the block's longer side: as set, or 85 (整段's size) or 68 (逐項's).
/// Null for 依各組判斷 — 自動 on a 自動判斷 step.
/// </param>
internal sealed record RealtimeActualValues(
    int MergeLevel,
    bool MergeUnpunctuatedApplies,
    bool ReadTimingIsAutomatic,
    ActualReadTiming ReadTiming,
    bool SettleIsAutomatic,
    double? SettleSeconds,
    bool DetectorIsAutomatic,
    int? DetectorPercent)
{
    /// <summary>The mode a block names — see <see cref="ResolvedProfile.Resolve(string?, IReadOnlyList{RealtimeProfile}?)"/>.</summary>
    public static RealtimeActualValues Of(string? id, IReadOnlyList<RealtimeProfile>? custom)
    {
        if (RealtimePresets.IsPreset(id)) return Of(RealtimePresets.Create(id));
        var stored = custom?.FirstOrDefault(profile => profile is not null && profile.Id == id);
        return Of(stored ?? RealtimePresets.Create(RealtimePresets.StandardId));
    }

    /// <summary>One mode, preset or custom, as stored.</summary>
    /// <remarks>
    /// 自動 on 不合併 and 逐項 reads as 逐項 does, on 整段 and 整個區塊 as 整段 does — see
    /// <see cref="ResolvedProfile.Layout"/>.
    /// </remarks>
    public static RealtimeActualValues Of(RealtimeProfile profile)
    {
        var filled = profile.Filled();
        var resolved = ResolvedProfile.Resolve(profile);
        var layout = resolved.Layout;

        // Known unless both the timing and the grouping are left to what the block turns out to hold.
        bool? dialogue = resolved.Timing != RealtimeReadTiming.Auto || layout != RealtimeBlockMode.Auto
            ? resolved.Dialogue(layout)
            : null;

        double? settle = resolved.SettlePolls is { } polls ? Seconds(polls)
            : dialogue is { } reads ? Seconds(resolved.TextSettlePolls(reads))
            : null;

        int? detector = resolved.DetectorFraction is { } fraction ? (int)Math.Round(fraction * 100)
            : layout switch
            {
                RealtimeBlockMode.Panel => (int)Math.Round(RealtimeDetectorSize.PanelFraction * 100),
                RealtimeBlockMode.Subtitle => (int)Math.Round(RealtimeDetectorSize.StripFraction * 100),
                _ => null,
            };

        return new RealtimeActualValues(
            filled.MergeLevel!.Value,
            RealtimeMergeLevels.MergeUnpunctuatedApplies(filled.MergeLevel!.Value),
            resolved.Timing == RealtimeReadTiming.Auto,
            dialogue switch
            {
                true => ActualReadTiming.ReadThenConfirm,
                false => ActualReadTiming.SettleThenRead,
                null => ActualReadTiming.ByContent,
            },
            resolved.SettlePolls is null,
            settle,
            resolved.DetectorFraction is null,
            detector);
    }

    private static double Seconds(int polls) =>
        Math.Round(polls * RealtimeRegionState.PollInterval.TotalSeconds, 2);
}
