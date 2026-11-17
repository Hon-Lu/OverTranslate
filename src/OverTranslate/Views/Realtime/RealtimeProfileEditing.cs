using System.Globalization;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Views.Realtime;

/// <summary>One value of a 即時翻譯 mode, as the page shows it: one row each.</summary>
internal enum ProfileParam
{
    /// <summary>合併程度.</summary>
    MergeLevel,

    /// <summary>沒有標點也合併.</summary>
    MergeUnpunctuated,

    /// <summary>畫面文字穩定後再讀取.</summary>
    SettleSeconds,

    /// <summary>譯文保留.</summary>
    ClearAfterEmptyReads,

    /// <summary>辨識解析度.</summary>
    DetectorPercent,

    /// <summary>忽略小字 (進階).</summary>
    IgnoreSmallPercent,

    /// <summary>辨識信心下限 (進階).</summary>
    ConfidenceLevel,
}

/// <summary>A mode as the edit layer's list and the page's list name it.</summary>
/// <param name="Id">What a block stores — see <see cref="RealtimeProfile.Id"/>.</param>
/// <param name="Name">The preset's name in the interface language, or the custom mode's own.</param>
/// <param name="IsPreset">One of the three built in.</param>
/// <param name="BasedOn">The preset it follows; a preset follows itself.</param>
public sealed record ProfileChoice(string Id, string Name, bool IsPreset, string BasedOn);

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
    /// <remarks>
    /// 辨識信心下限 is not among them, nor anywhere on the page: measured, its steps did not change
    /// enough to be worth a control. The mode still carries the value, and 還原為範本 still clears
    /// one written into the file by hand.
    /// </remarks>
    public static readonly ProfileParam[] Advanced =
        [ProfileParam.IgnoreSmallPercent];

    /// <summary>Every value, in the order the page lists them.</summary>
    public static readonly ProfileParam[] All = Enum.GetValues<ProfileParam>();

    /// <summary>Every value the page offers: all but 辨識信心下限 — see <see cref="Advanced"/>.</summary>
    public static readonly ProfileParam[] Shown = [.. All.Where(param => param != ProfileParam.ConfidenceLevel)];

    /// <summary>
    /// Whether a block whose text runs vertically ignores this value: everything but the two
    /// timing ones — see research §3, the vertical path neither groups nor sizes the detector.
    /// </summary>
    public static bool IsHorizontalOnly(ProfileParam param) =>
        param is not (ProfileParam.SettleSeconds or ProfileParam.ClearAfterEmptyReads);

    /// <summary>
    /// Whether this value has any effect on the mode as it is filled: 沒有標點也合併 only while
    /// 合併程度 is on one of 自動判斷's steps (<see cref="RealtimeMergeLevels.MergeUnpunctuatedApplies"/>);
    /// everything else always.
    /// </summary>
    public static bool InEffect(RealtimeProfile filled, ProfileParam param) =>
        param != ProfileParam.MergeUnpunctuated ||
        RealtimeMergeLevels.MergeUnpunctuatedApplies(filled.MergeLevel ?? RealtimeMergeLevels.AutomaticMiddle);

    /// <summary>Whether the stored mode sets this value of its own rather than following its preset.</summary>
    public static bool IsSet(RealtimeProfile stored, ProfileParam param) => param switch
    {
        ProfileParam.MergeLevel => stored.MergeLevel is not null,
        ProfileParam.MergeUnpunctuated => stored.MergeUnpunctuated is not null,
        ProfileParam.SettleSeconds => stored.SettleSeconds is not null,
        ProfileParam.ClearAfterEmptyReads => stored.ClearAfterEmptyReads is not null,
        ProfileParam.DetectorPercent => stored.DetectorPercent is not null,
        ProfileParam.IgnoreSmallPercent => stored.IgnoreSmallPercent is not null,
        ProfileParam.ConfidenceLevel => stored.ConfidenceLevel is not null,
        _ => false,
    };

    /// <summary>Puts one value back to following the preset.</summary>
    public static void Clear(RealtimeProfile stored, ProfileParam param)
    {
        switch (param)
        {
            case ProfileParam.MergeLevel: stored.MergeLevel = null; break;
            case ProfileParam.MergeUnpunctuated: stored.MergeUnpunctuated = null; break;
            case ProfileParam.SettleSeconds: stored.SettleSeconds = null; break;
            case ProfileParam.ClearAfterEmptyReads: stored.ClearAfterEmptyReads = null; break;
            case ProfileParam.DetectorPercent: stored.DetectorPercent = null; break;
            case ProfileParam.IgnoreSmallPercent: stored.IgnoreSmallPercent = null; break;
            case ProfileParam.ConfidenceLevel: stored.ConfidenceLevel = null; break;
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
    /// Compared on the filled values, so 0.30 and 0.3 seconds, or a detector size of -5 and of -1
    /// (both 自動), count as the same thing, which is what they are to the pipeline.
    /// </remarks>
    public static RealtimeProfile WithoutTemplateValues(RealtimeProfile stored)
    {
        var copy = stored.Clone();
        var mine = copy.Filled();
        var template = RealtimePresets.Create(mine.BasedOn);
        foreach (var param in All)
            if (IsSet(copy, param) && Equals(Value(mine, param), Value(template, param)))
                Clear(copy, param);
        return copy;
    }

    /// <summary>How many of <paramref name="among"/> the mode sets differently from its preset.</summary>
    public static int ModifiedCount(RealtimeProfile stored, IEnumerable<ProfileParam> among)
    {
        var tidy = WithoutTemplateValues(stored);
        return among.Count(param => IsSet(tidy, param));
    }

    /// <summary>
    /// How many of the values the page offers (<see cref="Shown"/>) the mode sets differently from
    /// its preset — the N of 以「範本」為範本，已修改 N 項. Zero for a preset.
    /// </summary>
    /// <remarks>
    /// A value with no effect at the mode's 合併程度 still counts: it is the mode's own, marked
    /// 已修改 on its row, and takes effect again as soon as 合併程度 comes back to where it acts.
    /// </remarks>
    public static int ModifiedCount(RealtimeProfile stored) =>
        RealtimePresets.IsPreset(stored.Id) ? 0 : ModifiedCount(stored, Shown);

    /// <summary>
    /// One value of a filled mode in a form two of them can be compared by: strings, a bool, whole
    /// numbers, and seconds rounded to the hundredth the page shows them at.
    /// </summary>
    public static object? Value(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.MergeLevel => filled.MergeLevel,
        ProfileParam.MergeUnpunctuated => filled.MergeUnpunctuated,
        ProfileParam.SettleSeconds => filled.SettleSeconds is { } seconds
            ? seconds < 0 ? RealtimePresets.Automatic : Math.Round(seconds, 2)
            : null,
        ProfileParam.ClearAfterEmptyReads => filled.ClearAfterEmptyReads,
        ProfileParam.DetectorPercent => filled.DetectorPercent,
        ProfileParam.IgnoreSmallPercent => filled.IgnoreSmallPercent,
        ProfileParam.ConfidenceLevel => filled.ConfidenceLevel,
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
            choices.Add(new ProfileChoice(profile.Id, name, false, basedOn));
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

    /// <summary>A preset's sentence on when to use it — the edit layer's guidance plate.</summary>
    public static string PresetGuidance(string id) => id switch
    {
        RealtimePresets.SubtitleId => LocalizationService.Get("S.Realtime.ProfileSubtitleGuidance"),
        RealtimePresets.InterfaceId => LocalizationService.Get("S.Realtime.ProfileInterfaceGuidance"),
        _ => LocalizationService.Get("S.Realtime.ProfileStandardGuidance"),
    };

    /// <summary>自訂模式 N, in the interface language.</summary>
    public static string NewName(int number) =>
        LocalizationService.Format("S.Realtime.ProfileNewName", number);

    // ── What 自動 stands for ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where 畫面文字穩定後再讀取's slider rests while 自動 is ticked: what the mode waits with — see
    /// <see cref="RealtimeActualValues.SettleSeconds"/>. 依各組判斷 is shown at the settled
    /// reading's wait.
    /// </summary>
    public static double AutomaticSettleSeconds(RealtimeProfile filled)
    {
        var automatic = filled.Clone();
        automatic.SettleSeconds = RealtimePresets.Automatic;
        return RealtimeActualValues.Of(automatic).SettleSeconds
            ?? Math.Round(RealtimeRegionState.MaxTextUnsettledPolls * RealtimeRegionState.PollInterval.TotalSeconds, 2);
    }

    /// <summary>
    /// Where 辨識解析度's slider rests while 自動 is ticked: the size the mode reads at — see
    /// <see cref="RealtimeActualValues.DetectorPercent"/>. 依各組判斷 is shown at the strip size, the
    /// one a block starts on.
    /// </summary>
    public static int AutomaticDetectorPercent(RealtimeProfile filled)
    {
        var automatic = filled.Clone();
        automatic.DetectorPercent = RealtimePresets.Automatic;
        return RealtimeActualValues.Of(automatic).DetectorPercent ?? (int)Math.Round(100 * RealtimeDetectorSize.StripFraction);
    }

    /// <summary>Seconds as the page writes them: invariant, up to two decimals, no trailing zeros.</summary>
    public static string Seconds(double seconds) =>
        seconds.ToString("0.##", CultureInfo.InvariantCulture);
}
