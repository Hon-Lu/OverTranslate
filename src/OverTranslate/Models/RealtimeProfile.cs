namespace OverTranslate.Models;

/// <summary>
/// One 即時翻譯 mode as the settings file keeps it: the values the user sees, in the units they see
/// them in, each one either set or left to follow the preset it was made from.
/// </summary>
/// <remarks>
/// <para>The three presets are built in (see <see cref="RealtimePresets"/>) and every value of theirs
/// is filled. A custom mode is a preset to follow — <see cref="BasedOn"/> — and the values the user
/// moved away from it; null is "as the preset has it". A value nobody touched therefore keeps
/// following the preset, so a preset tuned in a later version improves the custom modes made from
/// it too, and the page can mark which values were changed and put back any one of them.</para>
///
/// <para>Stored as the user sees it — seconds, percentages, steps — rather than as the polls and
/// fractions the pipeline runs on (<see cref="Services.Realtime.ResolvedProfile"/>), so a change of
/// poll interval or a wider range in a later version reads an old file the same way. Nothing here is
/// clamped: what a value means is decided as the mode is resolved, and a value out of range there is
/// held to the range rather than refused.</para>
///
/// <para>The choices are strings, not enums. The settings file reads a list in one piece, and one
/// enum name a build does not know would throw the whole list away; an unknown string is only that
/// one value falling back to the preset's.</para>
///
/// <para>A value no longer offered is dropped from here rather than kept unread: 行距容許
/// (LineSpacing) was, and a file that still holds it reads as if it did not, since a member this
/// class does not have is skipped as the file is read.</para>
/// </remarks>
public sealed class RealtimeProfile
{
    /// <summary>
    /// What a block and <see cref="RealtimeSettings.NewBlockProfile"/> name this mode by: one of
    /// <see cref="RealtimePresets"/>' fixed ids, or a Guid ("N") given to a custom mode as it is made
    /// and never rewritten, so a rename keeps every block on it.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The user's own name for a custom mode, kept as typed. Empty on a preset, whose name is a
    /// string of the interface language.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The preset whose values this mode follows wherever it sets none of its own.</summary>
    /// <remarks>Always a preset: a custom mode is never made from another custom one.</remarks>
    public string BasedOn { get; set; } = RealtimePresets.StandardId;

    /// <summary>合併程度, 0–8 — see <see cref="RealtimeMergeLevels"/>.</summary>
    /// <remarks>Horizontal text only.</remarks>
    public int? MergeLevel { get; set; }

    /// <summary>
    /// 沒有標點也合併. Only read while <see cref="MergeLevel"/> is one of 自動判斷's — see
    /// <see cref="RealtimeMergeLevels.IsAutomatic"/>.
    /// </summary>
    public bool? MergeUnpunctuated { get; set; }

    /// <summary>
    /// 畫面文字穩定後再讀取, in seconds, 0–1.5; below zero is 自動.
    /// </summary>
    /// <remarks>
    /// <para>0 is 不等待: a change is read at once and read again once to confirm it (what was
    /// 先讀再確認). Above it a change inside the text is waited out up to that long and read once
    /// (等穩定再讀), counted in polls of 0.15 s — see <see cref="MaxSettleSeconds"/>. 自動 is each
    /// group's own way: 不等待 for a passage, one poll for items, and on 自動判斷's steps whichever
    /// the block was last read as — see <see cref="Services.Realtime.ResolvedProfile.Dialogue"/>.</para>
    ///
    /// <para>One value where there used to be two, 讀取方式 and 等文字停下: a wait only means
    /// anything when a change is settled before it is read, and reading at once only when it is
    /// not waited for, so every pair but these was the same thing said twice.</para>
    /// </remarks>
    public double? SettleSeconds { get; set; }

    /// <summary>
    /// 原文消失後立即清除譯文: how many readings in a row must find nothing before a translation goes —
    /// 1 when ticked, 2 (what every preset holds) when not.
    /// </summary>
    /// <remarks>
    /// A count rather than a bool because that is what the pipeline runs on, and so that a later
    /// version may offer more than the two. It was a 1–4 slider first, but on a still picture a count
    /// above one is never reached (a frame that does not change is not read again), and nothing
    /// measured made a case for 3 or 4 elsewhere, so what is left is the one choice that behaves
    /// differently. A 3 or 4 an earlier build wrote reads as 2.
    /// </remarks>
    public int? ClearAfterEmptyReads { get; set; }

    /// <summary>辨識解析度, in percent of the block's longer side, 50–100; below zero is 自動.</summary>
    /// <remarks>Horizontal text only.</remarks>
    public int? DetectorPercent { get; set; }

    /// <summary>過濾小字, in percent of the frame's main text height, 0–80; 0 is off.</summary>
    /// <remarks>Horizontal text only.</remarks>
    public int? IgnoreSmallPercent { get; set; }

    /// <summary>
    /// 短句辨識門檻, in percent, 60–95: the score a reading shorter than ten characters needs to be
    /// believed. The presets hold <see cref="RealtimePresets.ConfidencePercent"/>, 80.
    /// </summary>
    /// <remarks>
    /// Horizontal text only. In percent rather than the 1–5 steps it was drafted with, which were
    /// never released: a ConfidenceLevel a file may still hold is not read.
    /// </remarks>
    public int? ConfidencePercent { get; set; }

    /// <summary>A copy to edit without touching this one.</summary>
    public RealtimeProfile Clone() => (RealtimeProfile)MemberwiseClone();

    /// <summary>
    /// A new custom mode on <paramref name="basedOn"/>, every value following it until the user
    /// moves one.
    /// </summary>
    public static RealtimeProfile CreateCustom(string? basedOn, string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        BasedOn = RealtimePresets.IsPreset(basedOn) ? basedOn! : RealtimePresets.StandardId,
    };

    /// <summary>
    /// Every value as this mode runs it, in the units the user sees: its own where it sets one, the
    /// preset's where it sets none or one this build cannot read, each held to its range.
    /// </summary>
    /// <remarks>
    /// What the page shows on each control; whether a value is the mode's own is whether this
    /// instance's property is null. A mode made from anything but a preset follows 標準.
    /// </remarks>
    public RealtimeProfile Filled()
    {
        var template = RealtimePresets.Create(RealtimePresets.IsPreset(BasedOn) ? BasedOn : RealtimePresets.StandardId);
        int percent = DetectorPercent ?? template.DetectorPercent!.Value;
        return new RealtimeProfile
        {
            Id = Id,
            Name = Name ?? "",
            BasedOn = template.Id,
            MergeLevel = Math.Clamp(MergeLevel ?? template.MergeLevel!.Value, RealtimeMergeLevels.Min, RealtimeMergeLevels.Max),
            MergeUnpunctuated = MergeUnpunctuated ?? template.MergeUnpunctuated,
            SettleSeconds = SettleSeconds is { } seconds && double.IsFinite(seconds)
                ? seconds < 0 ? RealtimePresets.Automatic : Math.Min(seconds, MaxSettleSeconds)
                : template.SettleSeconds,
            ClearAfterEmptyReads = Math.Clamp(
                ClearAfterEmptyReads ?? template.ClearAfterEmptyReads!.Value, MinClearAfterEmptyReads, MaxClearAfterEmptyReads),
            DetectorPercent = percent < 0
                ? RealtimePresets.Automatic
                : Math.Clamp(percent, MinDetectorPercent, MaxDetectorPercent),
            IgnoreSmallPercent = Math.Clamp(IgnoreSmallPercent ?? template.IgnoreSmallPercent!.Value, 0, MaxIgnoreSmallPercent),
            ConfidencePercent = Math.Clamp(
                ConfidencePercent ?? template.ConfidencePercent!.Value, MinConfidencePercent, MaxConfidencePercent),
        };
    }

    // ── Ranges ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The steps of 自動判斷's place on 合併程度, which was the 1–5 scale of 合併傾向.</summary>
    public const int MinStep = 1;

    /// <inheritdoc cref="MinStep"/>
    public const int MaxStep = 5;

    /// <summary>The middle step, which every preset holds and which is the measured rules themselves.</summary>
    public const int MiddleStep = 3;

    /// <summary>等文字停下's range, in seconds, from 0.</summary>
    /// <remarks>
    /// Counted in polls of <see cref="Services.Realtime.RealtimeRegionState.PollInterval"/>, 0.15 s,
    /// so a slider steps by that much: 1.5 s is ten polls.
    /// </remarks>
    public const double MaxSettleSeconds = 1.5;

    /// <summary>原文消失後立即清除譯文 ticked: the first reading that finds nothing clears.</summary>
    public const int MinClearAfterEmptyReads = 1;

    /// <summary>原文消失後立即清除譯文 not ticked — the presets' value, and the most a mode can hold.</summary>
    public const int MaxClearAfterEmptyReads = 2;

    /// <summary>辨識解析度's range, in percent.</summary>
    /// <remarks>
    /// <para>60, not the 50 first guessed. The strip fraction's own table has subtitles at 78% correct
    /// at half size against 94% at 0.85 — see
    /// <see cref="Services.Realtime.RealtimeDetectorSize.StripFraction"/> — and on the live stage
    /// (a 1396x756 block: dialogue, a 22px HUD, an 18px quick bar) 50% read the dialogue but left
    /// two of the ten HUD labels unread and ran the quick bar's buttons together, where 60% read
    /// everything 自動 did. What the last ten points saved was little: 247ms a read at 50%, 284ms
    /// at 60%, 338ms on 自動 and 474ms at 100%.</para>
    /// </remarks>
    public const int MinDetectorPercent = 60;

    /// <inheritdoc cref="MinDetectorPercent"/>
    public const int MaxDetectorPercent = 100;

    /// <summary>短句辨識門檻's range, in percent.</summary>
    /// <remarks>
    /// From 60 because the recogniser itself drops anything below 0.60
    /// (<see cref="Services.Ocr.OnnxOcrEngine"/>'s floor), so a lower figure would do nothing 60 does
    /// not; up to 95, past which hardly a short line of real text is left. Whole percents, so the
    /// presets' 80 divides back to <see cref="Services.Realtime.ShortReadingDetection.ShortTextConfidenceFloor"/>
    /// exactly.
    /// </remarks>
    public const int MinConfidencePercent = 60;

    /// <inheritdoc cref="MinConfidencePercent"/>
    public const int MaxConfidencePercent = 95;

    /// <summary>過濾小字's range, in percent, from 0 (off).</summary>
    public const int MaxIgnoreSmallPercent = 80;
}

/// <summary>The three built-in modes, and the values every one of them is filled with.</summary>
/// <remarks>
/// <para>The ids are written into settings files and name the mode a block was on, so they are fixed
/// for good once released: renaming one would put everybody on it back on 標準 without a word.</para>
///
/// <para>Every preset reads exactly as 即時翻譯 did before modes existed: the middle step of each
/// scale, and the presets differ in 合併程度, on the three places of it the layouts they replace
/// stood for. 標準 leaves the wait and the detector size to 自動, because they follow what each
/// reading turns out to hold; the other two read one way only, so they carry the figures that way
/// had written out — 不等待 and 85% for 字幕・漫畫, one poll and 68% for 遊戲介面・聊天室 — which is
/// what 自動 comes to on them, value for value. Pinned by tests, because "the presets read as
/// before" is a promise the whole feature was built on.</para>
/// </remarks>
public static class RealtimePresets
{
    /// <summary>標準: each group of text read as what its own content calls for.</summary>
    public const string StandardId = "standard";

    /// <summary>字幕・漫畫: every group read as one passage.</summary>
    public const string SubtitleId = "subtitle";

    /// <summary>遊戲介面・聊天室: every group read as separate items.</summary>
    public const string InterfaceId = "interface";

    /// <summary>
    /// 字幕・漫畫's detector size, in percent: <see cref="Services.Realtime.RealtimeDetectorSize.StripFraction"/>.
    /// </summary>
    public const int StripPercent = 85;

    /// <summary>
    /// 遊戲介面・聊天室's detector size, in percent: <see cref="Services.Realtime.RealtimeDetectorSize.PanelFraction"/>.
    /// </summary>
    public const int PanelPercent = 68;

    /// <summary>
    /// 遊戲介面・聊天室's wait, in seconds: one poll —
    /// <see cref="Services.Realtime.RealtimeRegionState.MaxTextUnsettledPolls"/> of
    /// <see cref="Services.Realtime.RealtimeRegionState.PollInterval"/>.
    /// </summary>
    public const double ItemsSettleSeconds = 0.15;

    /// <summary>
    /// 短句辨識門檻 on every preset, in percent:
    /// <see cref="Services.Realtime.ShortReadingDetection.ShortTextConfidenceFloor"/>.
    /// </summary>
    public const int ConfidencePercent = 80;

    /// <summary>The value of a nullable number that means 自動 — see <see cref="RealtimeProfile.SettleSeconds"/>.</summary>
    public const int Automatic = -1;

    /// <summary>The three presets, in the order they are listed. New instances on every call.</summary>
    public static IReadOnlyList<RealtimeProfile> All =>
        [Create(StandardId), Create(SubtitleId), Create(InterfaceId)];

    /// <summary>Whether this id names a preset rather than a custom mode.</summary>
    public static bool IsPreset(string? id) => id is StandardId or SubtitleId or InterfaceId;

    /// <summary>A new copy of one preset, every value filled; 標準 for an id that names none.</summary>
    public static RealtimeProfile Create(string? id)
    {
        id = IsPreset(id) ? id! : StandardId;
        return new RealtimeProfile
        {
            Id = id,
            BasedOn = id,
            MergeLevel = id switch
            {
                SubtitleId => RealtimeMergeLevels.Passage,
                InterfaceId => RealtimeMergeLevels.Items,
                _ => RealtimeMergeLevels.AutomaticMiddle,
            },
            MergeUnpunctuated = true,
            SettleSeconds = id switch
            {
                SubtitleId => 0,
                InterfaceId => ItemsSettleSeconds,
                _ => Automatic,
            },
            ClearAfterEmptyReads = RealtimeProfile.MaxClearAfterEmptyReads,
            DetectorPercent = id switch
            {
                SubtitleId => StripPercent,
                InterfaceId => PanelPercent,
                _ => Automatic,
            },
            IgnoreSmallPercent = 0,
            ConfidencePercent = RealtimePresets.ConfidencePercent,
        };
    }
}

/// <summary>
/// 合併程度: one scale from reading every line on its own to joining all but what is clearly apart, which
/// is what the old 分行處理 and 合併傾向 became.
/// </summary>
/// <remarks>
/// <para>Each step names how stacked lines are joined. Pieces side by side on one line are joined by
/// the same rules on every step, 不合併 included: a sentence the detector framed in two
/// pieces is still one line.</para>
///
/// <list type="table">
/// <item><term>0 不合併</term><description>Every line on its own; drawn as items.</description></item>
/// <item><term>1 逐項</term><description>The panel grouper — 遊戲介面・聊天室.</description></item>
/// <item><term>2–6 自動判斷</term><description>Group by group, 1–5 of what was 合併傾向; 4 is 標準.</description></item>
/// <item><term>7 整段</term><description>The dialogue grouper — 字幕・漫畫.</description></item>
/// <item><term>8 合併</term><description>整段 joining past size, alignment and a short item beside the dialogue; still parts what is clearly apart.</description></item>
/// </list>
///
/// <para>Steps 1–7 are exactly what the layouts and the tendency they replace read as. MEASURED
/// (OcrHarness --auto-probe slider, 591 captures): no step parts what the one before it joined,
/// but for a few frames between 自動判斷 5 and 整段 — the two groupers break a paragraph at
/// different places — so the scale only mostly joins more the further right it goes.</para>
/// </remarks>
public static class RealtimeMergeLevels
{
    /// <summary>不合併: no line joined to the one under it.</summary>
    public const int Min = 0;

    /// <summary>逐項.</summary>
    public const int Items = 1;

    /// <summary>The first of 自動判斷's steps, which was 合併傾向 1.</summary>
    public const int AutomaticLowest = 2;

    /// <summary>自動判斷 on the measured rules themselves — 標準, and what 合併傾向 3 was.</summary>
    public const int AutomaticMiddle = 4;

    /// <summary>The last of 自動判斷's steps, which was 合併傾向 5.</summary>
    public const int AutomaticHighest = 6;

    /// <summary>整段.</summary>
    public const int Passage = 7;

    /// <summary>合併: more than 整段, short of joining what is clearly apart.</summary>
    public const int Max = 8;

    /// <summary>
    /// Whether this step decides group by group — the only steps 沒有標點也合併 has any effect on.
    /// </summary>
    public static bool IsAutomatic(int level) => level is >= AutomaticLowest and <= AutomaticHighest;

    /// <summary>Whether 沒有標點也合併 does anything at this step; the page greys it out where not.</summary>
    public static bool MergeUnpunctuatedApplies(int level) => IsAutomatic(level);

    /// <summary>
    /// The step's place on the old 1–5 合併傾向 scale; the middle one off 自動判斷, where the
    /// thresholds are not read.
    /// </summary>
    public static int Tendency(int level) =>
        IsAutomatic(level) ? level - AutomaticLowest + RealtimeProfile.MinStep : RealtimeProfile.MiddleStep;
}
