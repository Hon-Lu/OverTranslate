using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <para>The choices are strings, or an enum read through a converter that never throws
/// (<see cref="RealtimeSentenceMergeConverter"/>). The settings file reads a list in one piece, and
/// one enum name a build does not know would throw the whole list away; an unknown value is only
/// that one value falling back to the preset's.</para>
///
/// <para>A value no longer offered is dropped from here rather than kept unread: 行距容許
/// (LineSpacing), 沒有標點也合併 (MergeUnpunctuated) and the nine-step 合併程度 (MergeLevel, 0–8,
/// never released; <see cref="SentenceMerge"/> replaced it) were, and a file that still holds them
/// reads as if it did not, since a member this class does not have is skipped as the file is
/// read.</para>
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

    /// <summary>語句合併 — see <see cref="RealtimeSentenceMerge"/>.</summary>
    /// <remarks>
    /// Horizontal text only. Stored by name; a name, number or anything else this build cannot read
    /// is left unset and follows the preset (<see cref="RealtimeSentenceMergeConverter"/>).
    /// </remarks>
    [JsonConverter(typeof(RealtimeSentenceMergeConverter))]
    public RealtimeSentenceMerge? SentenceMerge { get; set; }

    /// <summary>譯文對齊: one of <see cref="RealtimeTextAligns"/>' values.</summary>
    /// <remarks>
    /// Horizontal text only, and drawing only: how a group is grouped and read does not change with
    /// it. 自動 follows the source's own rows where a group has several and they line up one way,
    /// and where it does not, draws the group as its reading would — a passage centred, separate
    /// items from their left edge. 置中 and 靠左 draw every group so, whichever way it was read.
    /// </remarks>
    public string? Align { get; set; }

    /// <summary>
    /// 過濾數字與縮寫: true to leave untranslated and undrawn the groups with nothing to translate —
    /// numbers, symbols, levels, abbreviations, shapes read as letters — false to translate and draw
    /// every group, as 即時翻譯 did before. Every preset holds true.
    /// </summary>
    /// <remarks>
    /// One switch for the whole of <see cref="Services.Realtime.RealtimeHiddenText.NothingToTranslate"/>,
    /// name beside a level included: it is offered for the few who want the numbers boxed after all,
    /// and nobody could tell one of its rules from another by name. Both directions of text.
    /// </remarks>
    public bool? FilterNumbersAndAbbreviations { get; set; }

    /// <summary>
    /// 畫面文字穩定後再讀取's 自動｜手動: true for 自動, false for the seconds in
    /// <see cref="SettleSeconds"/>.
    /// </summary>
    /// <remarks>
    /// <para>Kept apart from the seconds so that turning to 自動 does not lose them: back on 手動 the
    /// slider is where the user left it. Null follows the preset — or, where only seconds are
    /// stored, is 手動, the way a file written before the two were apart reads; see
    /// <see cref="Filled"/>.</para>
    ///
    /// <para>Whenever the page stores seconds it stores this beside them, even when it says what the
    /// preset says, so that "seconds and no switch" only ever means such a file.</para>
    /// </remarks>
    public bool? SettleAutomatic { get; set; }

    /// <summary>
    /// 畫面文字穩定後再讀取 on 手動, in seconds, 0–1.5 — kept while the switch is on 自動.
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
    ///
    /// <para>Below zero was 自動 before <see cref="SettleAutomatic"/> existed, never released: read as
    /// 自動 with no seconds of the user's own.</para>
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
    /// differently. A 3 or 4 an earlier build wrote reads as 2. Unticked, a still picture takes the
    /// translation down after a second instead — see RealtimeRegionState.LongestHoldOverStillPicture.
    /// </remarks>
    public int? ClearAfterEmptyReads { get; set; }

    /// <summary>
    /// 辨識解析度's 自動｜手動: true for 自動, false for <see cref="DetectorPercent"/>. Kept apart
    /// from the percentage as <see cref="SettleAutomatic"/> is from its seconds.
    /// </summary>
    /// <remarks>Horizontal text only.</remarks>
    public bool? DetectorAutomatic { get; set; }

    /// <summary>
    /// 辨識解析度 on 手動, in percent of the block's longer side, 60–100 — kept while the switch is
    /// on 自動. Below zero was 自動 before <see cref="DetectorAutomatic"/> existed.
    /// </summary>
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
    ///
    /// The two values with 自動｜手動 come out with both halves filled: the switch, and the figure
    /// 手動 runs on, which is there whichever way the switch is — the user's own, or else the
    /// preset's (on 標準, which is 自動, where 手動 starts: see <see cref="RealtimePresets.Create"/>).
    /// </remarks>
    public RealtimeProfile Filled()
    {
        var template = RealtimePresets.Create(RealtimePresets.IsPreset(BasedOn) ? BasedOn : RealtimePresets.StandardId);
        return new RealtimeProfile
        {
            Id = Id,
            Name = Name ?? "",
            BasedOn = template.Id,
            SentenceMerge = SentenceMerge is { } merge && Enum.IsDefined(merge) ? merge : template.SentenceMerge,
            Align = RealtimeTextAligns.Normalize(Align) ?? template.Align,
            FilterNumbersAndAbbreviations = FilterNumbersAndAbbreviations ?? template.FilterNumbersAndAbbreviations,
            SettleAutomatic = StoredSettleAutomatic ?? template.SettleAutomatic,
            SettleSeconds = OwnSettleSeconds is { } seconds ? Math.Min(seconds, MaxSettleSeconds) : template.SettleSeconds,
            ClearAfterEmptyReads = Math.Clamp(
                ClearAfterEmptyReads ?? template.ClearAfterEmptyReads!.Value, MinClearAfterEmptyReads, MaxClearAfterEmptyReads),
            DetectorAutomatic = StoredDetectorAutomatic ?? template.DetectorAutomatic,
            DetectorPercent = OwnDetectorPercent is { } percent
                ? Math.Clamp(percent, MinDetectorPercent, MaxDetectorPercent)
                : template.DetectorPercent,
            IgnoreSmallPercent = Math.Clamp(IgnoreSmallPercent ?? template.IgnoreSmallPercent!.Value, 0, MaxIgnoreSmallPercent),
            ConfidencePercent = Math.Clamp(
                ConfidencePercent ?? template.ConfidencePercent!.Value, MinConfidencePercent, MaxConfidencePercent),
        };
    }

    /// <summary>
    /// A copy that says outright what an older file only implied: the switch written beside any
    /// figure of the user's own, and no figure that only meant 自動 (below zero) or nothing (not a
    /// number). Reads the same — see <see cref="Filled"/>.
    /// </summary>
    /// <remarks>
    /// The page tidies a mode on this before it compares it with the preset, so that putting a figure
    /// back to the preset's never turns an implied 手動 into "follow the preset".
    /// </remarks>
    public RealtimeProfile WithSwitchesWritten()
    {
        var copy = Clone();
        copy.SettleAutomatic = StoredSettleAutomatic;
        copy.SettleSeconds = OwnSettleSeconds;
        copy.DetectorAutomatic = StoredDetectorAutomatic;
        copy.DetectorPercent = OwnDetectorPercent;
        return copy;
    }

    // A figure the user set for 手動: finite and not below zero.
    private double? OwnSettleSeconds => SettleSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;

    private int? OwnDetectorPercent => DetectorPercent is >= 0 ? DetectorPercent : null;

    // The switch as stored, or as a file from before it was stored apart meant it: a figure of the
    // user's own alone is 手動, below zero is 自動, and neither follows the preset.
    private bool? StoredSettleAutomatic =>
        SettleAutomatic ?? (OwnSettleSeconds is not null ? false : SettleSeconds < 0 ? true : null);

    private bool? StoredDetectorAutomatic =>
        DetectorAutomatic ?? (OwnDetectorPercent is not null ? false : DetectorPercent < 0 ? true : null);

    // ── Ranges ──────────────────────────────────────────────────────────────────────────────────

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
/// scale, and the presets differ in 語句合併, on the three places of it the layouts they replace
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

    /// <summary>
    /// The figure that meant 自動 before the switch was stored apart from it, and still reads so —
    /// see <see cref="RealtimeProfile.SettleSeconds"/>.
    /// </summary>
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
            SentenceMerge = id switch
            {
                SubtitleId => RealtimeSentenceMerge.Passage,
                InterfaceId => RealtimeSentenceMerge.Items,
                _ => RealtimeSentenceMerge.Automatic,
            },
            // What each preset's groups were drawn as before 譯文對齊 existed, on the two that read
            // one way only, so they look exactly as they did; 標準 follows the source.
            Align = id switch
            {
                SubtitleId => RealtimeTextAligns.Center,
                InterfaceId => RealtimeTextAligns.Left,
                _ => RealtimeTextAligns.Auto,
            },
            FilterNumbersAndAbbreviations = true,
            // 標準 is 自動 on both; its figures are where 手動 starts on a mode made from it until
            // the user moves the slider: one poll and the strip size, which read a half-drawn line
            // less often than 不等待 and miss small text less often than the panel size.
            SettleAutomatic = id == StandardId,
            SettleSeconds = id == SubtitleId ? 0 : ItemsSettleSeconds,
            ClearAfterEmptyReads = RealtimeProfile.MaxClearAfterEmptyReads,
            DetectorAutomatic = id == StandardId,
            DetectorPercent = id == InterfaceId ? PanelPercent : StripPercent,
            IgnoreSmallPercent = 0,
            ConfidencePercent = RealtimePresets.ConfidencePercent,
        };
    }
}

/// <summary>譯文對齊's values, as <see cref="RealtimeProfile.Align"/> stores them.</summary>
public static class RealtimeTextAligns
{
    /// <summary>自動: the source's own alignment where its rows show one, else as the group was read.</summary>
    public const string Auto = "Auto";

    /// <summary>置中.</summary>
    public const string Center = "Center";

    /// <summary>靠左.</summary>
    public const string Left = "Left";

    /// <summary>
    /// One of the three as this class spells it, whatever the case it was written in; null for
    /// anything else, which then follows the preset.
    /// </summary>
    public static string? Normalize(string? value) => value?.Trim() switch
    {
        { } text when text.Equals(Auto, StringComparison.OrdinalIgnoreCase) => Auto,
        { } text when text.Equals(Center, StringComparison.OrdinalIgnoreCase) => Center,
        { } text when text.Equals(Left, StringComparison.OrdinalIgnoreCase) => Left,
        _ => null,
    };
}

/// <summary>
/// 語句合併: one scale from reading every line on its own to joining all but what is clearly apart,
/// which is what the old 分行處理 and 合併傾向 became. The settings file stores the name.
/// </summary>
/// <remarks>
/// <para>Each value names how stacked lines are joined. Pieces side by side on one line are joined by
/// the same rules on every value, <see cref="None"/> included: a sentence the detector framed in two
/// pieces is still one line.</para>
///
/// <para>The order is the slider's, left to right, and only that: what a value means is its name.
/// Every value but <see cref="None"/> and <see cref="All"/> is exactly what a layout before modes read
/// as. MEASURED (OcrHarness --auto-probe slider, 591 captures: 270 dialogue, 179 panels, 142 both)
/// a value parts what the one before it joined only on 2 panel frames from 逐項 to 自動判斷 and on
/// 62 frames, 59 of them panels, from 自動判斷 to 整段, where the two groupers break a paragraph at
/// different places: the scale mostly joins more the further right it goes.</para>
///
/// <para><see cref="Automatic"/> was five steps (偏逐項 … 偏整段, the 1–5 of 合併傾向) and is one.
/// MEASURED (--auto-probe replay --profile, same 591 captures; frames whose groups differ from the
/// step before, dialogue+mixed / panels): 中→稍偏整段 1/12, 稍偏整段→偏整段 0/14 — a dialogue frame
/// almost never changed on the right half. Every group 偏逐項 split off from 中 was split exactly as
/// 逐項 splits it (128 of 128), and 42 of the 43 groups 偏整段 joined beyond 中 整段 joins too: each
/// step only handed a few more groups to a neighbour's answer, and which ones could not be told from
/// the screen. Of the 26 frames 偏整段 changed, 11 read better and 13 worse.</para>
/// </remarks>
public enum RealtimeSentenceMerge
{
    /// <summary>不合併: every line on its own, drawn as items.</summary>
    None,

    /// <summary>逐項: the panel grouper — 遊戲介面・聊天室.</summary>
    Items,

    /// <summary>自動判斷: group by group, on 自動's measured rules — 標準.</summary>
    Automatic,

    /// <summary>整段: the dialogue grouper — 字幕・漫畫.</summary>
    Passage,

    /// <summary>
    /// 合併: 整段 joining past size, alignment and a short item beside the dialogue; still parts what
    /// is clearly apart.
    /// </summary>
    All,
}

/// <summary>
/// Reads <see cref="RealtimeProfile.SentenceMerge"/> by name and never throws: a name this build does
/// not know, a number, true, an object — anything but one of the names — reads as unset, so the mode
/// follows its preset there and keeps every other value it holds.
/// </summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> throws on an unknown name, and a mode that throws is dropped
/// whole (SettingsService reads the modes one by one for that reason). Numbers are refused rather
/// than taken as positions: the file has only ever held names here, and the 0–8 of the field before it
/// would otherwise read as values they never meant.
/// </remarks>
public sealed class RealtimeSentenceMergeConverter : JsonConverter<RealtimeSentenceMerge?>
{
    public override bool HandleNull => true;

    public override RealtimeSentenceMerge? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // Past the whole value, whatever it is, so the properties after it still read.
            reader.Skip();
            return null;
        }
        var name = reader.GetString();
        return Parse(name);
    }

    public override void Write(Utf8JsonWriter writer, RealtimeSentenceMerge? value, JsonSerializerOptions options)
    {
        if (value is { } merge && Enum.IsDefined(merge)) writer.WriteStringValue(merge.ToString());
        else writer.WriteNullValue();
    }

    /// <summary>One of the names, in any case; null for anything else — digits and lists of names included.</summary>
    internal static RealtimeSentenceMerge? Parse(string? name)
    {
        var text = name?.Trim();
        foreach (var merge in Enum.GetValues<RealtimeSentenceMerge>())
            if (string.Equals(text, merge.ToString(), StringComparison.OrdinalIgnoreCase)) return merge;
        return null;
    }
}
