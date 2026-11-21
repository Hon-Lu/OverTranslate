using System.Globalization;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Views.Realtime;

/// <summary>One value of a 即時翻譯 mode, as the page shows it: one row each.</summary>
internal enum ProfileParam
{
    /// <summary>語句合併.</summary>
    SentenceMerge,

    /// <summary>譯文對齊.</summary>
    Align,

    /// <summary>過濾數字與縮寫: a tick.</summary>
    FilterNumbersAndAbbreviations,

    /// <summary>畫面文字穩定後再讀取.</summary>
    SettleSeconds,

    /// <summary>原文消失後立即清除譯文: a tick.</summary>
    ClearAfterEmptyReads,

    /// <summary>辨識解析度.</summary>
    DetectorPercent,

    /// <summary>過濾小字 (進階).</summary>
    IgnoreSmallPercent,

    /// <summary>短句辨識門檻 (進階).</summary>
    ConfidencePercent,
}

/// <summary>A mode as the edit layer's list and the page's list name it.</summary>
/// <param name="Id">What a block stores — see <see cref="RealtimeProfile.Id"/>.</param>
/// <param name="Name">The preset's name in the interface language, or the custom mode's own.</param>
/// <param name="IsPreset">One of the three built in.</param>
/// <param name="BasedOn">The preset it follows; a preset follows itself.</param>
/// <param name="Modified">
/// How many values a custom mode sets differently from its preset — see
/// <see cref="RealtimeProfileEditing.ModifiedCount(RealtimeProfile)"/>. Zero for a preset.
/// </param>
public sealed record ProfileChoice(string Id, string Name, bool IsPreset, string BasedOn, int Modified = 0);

/// <summary>
/// The rules of editing the modes, kept apart from the controls that apply them so they can be
/// held to by tests: naming, what counts as changed, and what a deletion leaves selected.
/// </summary>
/// <remarks>
/// <para>Every write from the page goes through <see cref="WithoutTemplateValues"/>: a value set
/// back to exactly what the preset holds is stored as following the preset again, so 已修改 only
/// appears for a value that really differs, and a later version that tunes the preset still reaches
/// the mode.</para>
///
/// <para>Names: a custom mode's name is the user's own text, stored as typed (trimmed, one line);
/// the automatic one — 自訂模式 N — is written in the interface language of the moment it was made
/// and never translated afterwards, like anything else the user could have typed.</para>
/// </remarks>
internal static class RealtimeProfileEditing
{
    /// <summary>The longest name the name box takes.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The values under 進階, in the order they are listed there.</summary>
    public static readonly ProfileParam[] Advanced =
        [ProfileParam.IgnoreSmallPercent, ProfileParam.ConfidencePercent];

    /// <summary>Every value, in the order the page lists them.</summary>
    public static readonly ProfileParam[] All = Enum.GetValues<ProfileParam>();

    /// <summary>Every value the page offers.</summary>
    public static readonly ProfileParam[] Shown = All;

    /// <summary>
    /// Whether a block whose text runs vertically ignores this value: everything but the two
    /// timing ones and 過濾數字與縮寫 — see research §3, the vertical path neither groups nor sizes
    /// the detector; what is translated is decided after either path has read.
    /// </summary>
    public static bool IsHorizontalOnly(ProfileParam param) =>
        param is not (ProfileParam.SettleSeconds or ProfileParam.ClearAfterEmptyReads
            or ProfileParam.FilterNumbersAndAbbreviations);

    /// <summary>Whether the page shows this value as a tick rather than a slider or a switch.</summary>
    public static bool IsTick(ProfileParam param) =>
        param is ProfileParam.ClearAfterEmptyReads or ProfileParam.FilterNumbersAndAbbreviations;

    /// <summary>
    /// Whether the stored mode holds anything of its own for this value rather than following its
    /// preset — for 自動｜手動, either half. Not whether it is 已修改: see <see cref="IsModified"/>.
    /// </summary>
    public static bool IsSet(RealtimeProfile stored, ProfileParam param) => param switch
    {
        ProfileParam.SentenceMerge => stored.SentenceMerge is not null,
        ProfileParam.Align => stored.Align is not null,
        ProfileParam.FilterNumbersAndAbbreviations => stored.FilterNumbersAndAbbreviations is not null,
        ProfileParam.SettleSeconds => stored.SettleAutomatic is not null || stored.SettleSeconds is not null,
        ProfileParam.ClearAfterEmptyReads => stored.ClearAfterEmptyReads is not null,
        ProfileParam.DetectorPercent => stored.DetectorAutomatic is not null || stored.DetectorPercent is not null,
        ProfileParam.IgnoreSmallPercent => stored.IgnoreSmallPercent is not null,
        ProfileParam.ConfidencePercent => stored.ConfidencePercent is not null,
        _ => false,
    };

    /// <summary>Puts one value back to following the preset — for 自動｜手動, both halves.</summary>
    public static void Clear(RealtimeProfile stored, ProfileParam param)
    {
        switch (param)
        {
            case ProfileParam.SentenceMerge: stored.SentenceMerge = null; break;
            case ProfileParam.Align: stored.Align = null; break;
            case ProfileParam.FilterNumbersAndAbbreviations: stored.FilterNumbersAndAbbreviations = null; break;
            case ProfileParam.SettleSeconds: stored.SettleAutomatic = null; stored.SettleSeconds = null; break;
            case ProfileParam.ClearAfterEmptyReads: stored.ClearAfterEmptyReads = null; break;
            case ProfileParam.DetectorPercent: stored.DetectorAutomatic = null; stored.DetectorPercent = null; break;
            case ProfileParam.IgnoreSmallPercent: stored.IgnoreSmallPercent = null; break;
            case ProfileParam.ConfidencePercent: stored.ConfidencePercent = null; break;
        }
    }

    /// <summary>A copy with every value following the preset — 還原為範本.</summary>
    public static RealtimeProfile ResetAll(RealtimeProfile stored)
    {
        var copy = stored.Clone();
        foreach (var param in All) Clear(copy, param);
        return copy;
    }

    /// <summary>
    /// A copy in which every value equal to the preset's — as the mode reads it, after clamping — is
    /// put back to following the preset.
    /// </summary>
    /// <remarks>
    /// <para>Compared on the filled values, so 0.30 and 0.3 seconds, or a detector size of 120 and of
    /// 100, count as the same thing, which is what they are to the pipeline.</para>
    ///
    /// <para>自動｜手動 is two halves, tidied one at a time: the figure goes back to the preset's when it
    /// is the preset's, and the switch when it says what the preset says and no figure of the user's
    /// own is left beside it — kept there, it is what tells a figure remembered under 自動 from the
    /// 手動 an older file meant by a figure alone (see <see cref="RealtimeProfile.WithSwitchesWritten"/>).</para>
    /// </remarks>
    public static RealtimeProfile WithoutTemplateValues(RealtimeProfile stored)
    {
        var copy = stored.WithSwitchesWritten();
        var mine = copy.Filled();
        var template = RealtimePresets.Create(mine.BasedOn);
        foreach (var param in All)
        {
            if (param is ProfileParam.SettleSeconds or ProfileParam.DetectorPercent) continue;
            if (IsSet(copy, param) && Equals(Value(mine, param), Value(template, param)))
                Clear(copy, param);
        }

        if (copy.SettleSeconds is not null &&
            Equals(Value(mine, ProfileParam.SettleSeconds), Value(template, ProfileParam.SettleSeconds)))
            copy.SettleSeconds = null;
        if (copy.SettleAutomatic is not null && copy.SettleSeconds is null && mine.SettleAutomatic == template.SettleAutomatic)
            copy.SettleAutomatic = null;
        if (copy.DetectorPercent is not null && mine.DetectorPercent == template.DetectorPercent)
            copy.DetectorPercent = null;
        if (copy.DetectorAutomatic is not null && copy.DetectorPercent is null && mine.DetectorAutomatic == template.DetectorAutomatic)
            copy.DetectorAutomatic = null;
        return copy;
    }

    /// <summary>
    /// Whether the mode runs this value differently from its preset — 已修改. For 自動｜手動: the
    /// switch differs, or both are on 手動 at different figures; a figure remembered while the
    /// switch is on 自動 is not a change, since nothing runs on it.
    /// </summary>
    public static bool IsModified(RealtimeProfile stored, ProfileParam param)
    {
        var mine = stored.Filled();
        var template = RealtimePresets.Create(mine.BasedOn);
        return param switch
        {
            ProfileParam.SettleSeconds => mine.SettleAutomatic != template.SettleAutomatic ||
                (mine.SettleAutomatic == false && !Equals(Value(mine, param), Value(template, param))),
            ProfileParam.DetectorPercent => mine.DetectorAutomatic != template.DetectorAutomatic ||
                (mine.DetectorAutomatic == false && mine.DetectorPercent != template.DetectorPercent),
            _ => !Equals(Value(mine, param), Value(template, param)),
        };
    }

    /// <summary>How many of <paramref name="among"/> the mode sets differently from its preset.</summary>
    public static int ModifiedCount(RealtimeProfile stored, IEnumerable<ProfileParam> among) =>
        among.Count(param => IsModified(stored, param));

    /// <summary>
    /// How many of the values the page offers (<see cref="Shown"/>) the mode sets differently from
    /// its preset — the N of 以「範本」為範本，已修改 N 項. Zero for a preset.
    /// </summary>
    public static int ModifiedCount(RealtimeProfile stored) =>
        RealtimePresets.IsPreset(stored.Id) ? 0 : ModifiedCount(stored, Shown);

    /// <summary>
    /// One value of a filled mode in a form two of them can be compared by: strings, a bool, whole
    /// numbers, and seconds rounded to the hundredth the page shows them at. For 自動｜手動, the
    /// figure 手動 runs on, whichever way the switch is.
    /// </summary>
    public static object? Value(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.SentenceMerge => filled.SentenceMerge,
        ProfileParam.Align => filled.Align,
        ProfileParam.FilterNumbersAndAbbreviations => filled.FilterNumbersAndAbbreviations,
        ProfileParam.SettleSeconds => filled.SettleSeconds is { } seconds ? Math.Round(seconds, 2) : null,
        ProfileParam.ClearAfterEmptyReads => filled.ClearAfterEmptyReads,
        ProfileParam.DetectorPercent => filled.DetectorPercent,
        ProfileParam.IgnoreSmallPercent => filled.IgnoreSmallPercent,
        ProfileParam.ConfidencePercent => filled.ConfidencePercent,
        _ => null,
    };

    // ── Naming ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The smallest number from 1 whose <paramref name="format"/>ted name nobody has yet — so
    /// deleting 自訂模式 2 of three frees 2 for the next one rather than going on to 4.
    /// </summary>
    public static int NextNumber(IEnumerable<string> names, Func<int, string> format)
    {
        var taken = new HashSet<string>(names.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        int number = 1;
        while (taken.Contains(format(number).Trim())) number++;
        return number;
    }

    /// <summary>
    /// The name of a mode split off a preset where the preset would not take a change: the preset's
    /// name marked as the user's own — 字幕・漫畫（自訂）, then 字幕・漫畫（自訂 2） and on, the
    /// smallest number nobody has (see <see cref="NextNumber"/>).
    /// </summary>
    /// <remarks>
    /// The preset's name rather than 自訂模式 N, which 新增 keeps: the edit layer's list shows a mode
    /// by its name first, and a mode split off this way is that preset with something changed.
    /// </remarks>
    public static string ForkName(
        string presetName, IEnumerable<string> names, Func<string, string> first, Func<string, int, string> numbered)
    {
        string Format(int number) => CleanName(number == 1 ? first(presetName) : numbered(presetName, number));
        return Format(NextNumber(names, Format));
    }

    /// <summary><see cref="ForkName(string, IEnumerable{string}, Func{string, string}, Func{string, int, string})"/> in the interface language.</summary>
    public static string ForkName(string presetId, IEnumerable<string> names) =>
        ForkName(PresetName(presetId), names,
            name => LocalizationService.Format("S.Realtime.ProfileForkName", name),
            (name, number) => LocalizationService.Format("S.Realtime.ProfileForkNameNumbered", name, number));

    /// <summary>
    /// The blocks with every one on <paramref name="fromId"/> put on <paramref name="toId"/>: the
    /// blocks on a preset go with the mode just split off it (the user's call), so what is changed
    /// on that mode is what those blocks show.
    /// </summary>
    public static List<RealtimeBlockPlacement> Follow(IEnumerable<RealtimeBlockPlacement> blocks, string fromId, string toId) =>
        [.. blocks.Select(block => block.ProfileId == fromId ? block with { ProfileId = toId } : block)];

    /// <summary>
    /// What typing <paramref name="typed"/> into the name box stores: one line — a line break or tab
    /// pasted in is a space — trimmed, and no longer than <see cref="MaxNameLength"/>.
    /// </summary>
    public static string CleanName(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var chars = typed.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>
    /// Whether another mode in <paramref name="choices"/> — a preset by its name in the interface
    /// language, or a custom one — already goes by <paramref name="name"/>, ignoring case.
    /// </summary>
    public static bool NameTaken(string name, string selfId, IEnumerable<ProfileChoice> choices) =>
        choices.Any(choice => choice.Id != selfId &&
            string.Equals(choice.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which mode the page shows after <paramref name="deletedId"/> goes: the custom mode after it,
    /// or else the one before it, or else — the last custom mode gone — the preset it was made from.
    /// </summary>
    public static string SelectionAfterDelete(IReadOnlyList<RealtimeProfile> before, string deletedId)
    {
        int index = -1;
        for (int i = 0; i < before.Count; i++)
            if (before[i].Id == deletedId) { index = i; break; }
        if (index < 0) return RealtimePresets.StandardId;
        if (index + 1 < before.Count) return before[index + 1].Id;
        if (index > 0) return before[index - 1].Id;
        return RealtimePresets.IsPreset(before[index].BasedOn) ? before[index].BasedOn : RealtimePresets.StandardId;
    }

    // ── The list ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The modes in the order every list shows them: the three presets, then the custom ones in the
    /// order they were made.
    /// </summary>
    /// <remarks>
    /// A custom mode stored with no name — written by hand, or by a build that crashed between
    /// making and naming it — is listed as the next free 自訂模式 N rather than as a blank row. Only
    /// listed so: the file is not written for it.
    /// </remarks>
    public static IReadOnlyList<ProfileChoice> Choices(
        IReadOnlyList<RealtimeProfile> custom, Func<string, string> presetName, Func<int, string> newName)
    {
        var choices = new List<ProfileChoice>
        {
            new(RealtimePresets.StandardId, presetName(RealtimePresets.StandardId), true, RealtimePresets.StandardId),
            new(RealtimePresets.SubtitleId, presetName(RealtimePresets.SubtitleId), true, RealtimePresets.SubtitleId),
            new(RealtimePresets.InterfaceId, presetName(RealtimePresets.InterfaceId), true, RealtimePresets.InterfaceId),
        };

        var names = custom.Select(profile => profile.Name ?? "").Where(name => name.Trim().Length > 0).ToList();
        foreach (var profile in custom)
        {
            var name = (profile.Name ?? "").Trim();
            if (name.Length == 0)
            {
                name = newName(NextNumber(names, newName));
                names.Add(name);
            }
            var basedOn = RealtimePresets.IsPreset(profile.BasedOn) ? profile.BasedOn : RealtimePresets.StandardId;
            choices.Add(new ProfileChoice(profile.Id, name, false, basedOn, ModifiedCount(profile)));
        }
        return choices;
    }

    /// <summary><see cref="Choices(IReadOnlyList{RealtimeProfile}, Func{string, string}, Func{int, string})"/> in the interface language.</summary>
    public static IReadOnlyList<ProfileChoice> Choices(IReadOnlyList<RealtimeProfile> custom) =>
        Choices(custom, PresetName, NewName);

    /// <summary>A preset's name in the interface language.</summary>
    public static string PresetName(string id) => id switch
    {
        RealtimePresets.SubtitleId => LocalizationService.Get("S.Realtime.ProfileSubtitle"),
        RealtimePresets.InterfaceId => LocalizationService.Get("S.Realtime.ProfileInterface"),
        _ => LocalizationService.Get("S.Realtime.ProfileStandard"),
    };

    /// <summary>A preset's one-line summary — the tooltip on it wherever it is offered.</summary>
    public static string PresetSummary(string id) => id switch
    {
        RealtimePresets.SubtitleId => LocalizationService.Get("S.Realtime.ProfileSubtitleSummary"),
        RealtimePresets.InterfaceId => LocalizationService.Get("S.Realtime.ProfileInterfaceSummary"),
        _ => LocalizationService.Get("S.Realtime.ProfileStandardSummary"),
    };

    /// <summary>A preset's sentence on when to use it — its tooltip on the edit layer.</summary>
    public static string PresetGuidance(string id) => id switch
    {
        RealtimePresets.SubtitleId => LocalizationService.Get("S.Realtime.ProfileSubtitleGuidance"),
        RealtimePresets.InterfaceId => LocalizationService.Get("S.Realtime.ProfileInterfaceGuidance"),
        _ => LocalizationService.Get("S.Realtime.ProfileStandardGuidance"),
    };

    /// <summary>
    /// What a custom mode's line under its name says: the preset it is made from, and how many
    /// values it changed when it changed any.
    /// </summary>
    public static string TemplateLine(ProfileChoice choice) => choice.Modified > 0
        ? LocalizationService.Format("S.Realtime.ProfileTemplateModified", PresetName(choice.BasedOn), choice.Modified)
        : LocalizationService.Format("S.Realtime.ProfileTemplateOf", PresetName(choice.BasedOn));

    /// <summary>
    /// A custom mode's tooltip on the edit layer: the preset it is made from, and how many values it
    /// changed when it changed any.
    /// </summary>
    public static string BasedOnSentence(ProfileChoice choice) => choice.Modified > 0
        ? LocalizationService.Format("S.Realtime.ProfileBasedOnModified", PresetName(choice.BasedOn), choice.Modified)
        : LocalizationService.Format("S.Realtime.ProfileBasedOn", PresetName(choice.BasedOn));

    /// <summary>A value of 語句合併 by name: 不合併, 逐項, 自動判斷, 整段, 合併.</summary>
    public static string SentenceMergeName(RealtimeSentenceMerge merge) => LocalizationService.Get(merge switch
    {
        RealtimeSentenceMerge.None => "S.Realtime.ProfileMergeNone",
        RealtimeSentenceMerge.Items => "S.Realtime.ProfileMergeItems",
        RealtimeSentenceMerge.Passage => "S.Realtime.ProfileMergePassage",
        RealtimeSentenceMerge.All => "S.Realtime.ProfileMergeBlock",
        _ => "S.Realtime.ProfileMergeAuto",
    });

    /// <summary>
    /// The value of 語句合併 a slider position stands for: the values in their order, left to right,
    /// held to the ends.
    /// </summary>
    public static RealtimeSentenceMerge SentenceMergeAt(double position) =>
        (RealtimeSentenceMerge)Math.Clamp((int)Math.Round(position), (int)RealtimeSentenceMerge.None, (int)RealtimeSentenceMerge.All);

    /// <summary>A value of 譯文對齊 by name: 自動, 置中, 靠左.</summary>
    public static string AlignName(string? align) => LocalizationService.Get(RealtimeTextAligns.Normalize(align) switch
    {
        RealtimeTextAligns.Center => "S.Realtime.ProfileAlignCenter",
        RealtimeTextAligns.Left => "S.Realtime.ProfileAlignLeft",
        _ => "S.Realtime.ProfileAuto",
    });

    /// <summary>What a value of 語句合併 does, in a sentence.</summary>
    public static string SentenceMergeDescription(RealtimeSentenceMerge merge) => LocalizationService.Get(merge switch
    {
        RealtimeSentenceMerge.None => "S.Realtime.ProfileMergeNoneDesc",
        RealtimeSentenceMerge.Items => "S.Realtime.ProfileMergeItemsDesc",
        RealtimeSentenceMerge.Passage => "S.Realtime.ProfileMergePassageDesc",
        RealtimeSentenceMerge.All => "S.Realtime.ProfileMergeBlockDesc",
        _ => "S.Realtime.ProfileMergeAutoDesc",
    });

    /// <summary>自訂模式 N, in the interface language.</summary>
    public static string NewName(int number) =>
        LocalizationService.Format("S.Realtime.ProfileNewName", number);

    /// <summary>Seconds as the page writes them: invariant, up to two decimals, no trailing zeros.</summary>
    public static string Seconds(double seconds) =>
        seconds.ToString("0.##", CultureInfo.InvariantCulture);
}
