using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// How a mode's values read and are written from a control: the words a value is shown in, a
/// slider's position as the value it stands for. Shared by the mode card on the page
/// (<see cref="RealtimeProfileOverlay"/>) and the running bar's 模式 panel
/// (<see cref="RealtimeProfilePanel"/>), so the two never say the same value two ways.
/// </summary>
internal static class RealtimeProfileDisplay
{
    internal static double PollSeconds => Services.Realtime.RealtimeRegionState.PollInterval.TotalSeconds;

    /// <summary>How a value reads on its row.</summary>
    internal static string Display(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.SentenceMerge => RealtimeProfileEditing.SentenceMergeName(filled.SentenceMerge ?? RealtimeSentenceMerge.Automatic),
        ProfileParam.Align => RealtimeProfileEditing.AlignName(filled.Align),
        ProfileParam.SettleSeconds => filled.SettleAutomatic == true
            ? Services.Realtime.RealtimeActualValues.Of(filled).SettleSeconds is { } seconds
                ? SecondsText(seconds)
                : LocalizationService.Get("S.Realtime.ProfilePerGroup")
            : SecondsText(filled.SettleSeconds!.Value),
        ProfileParam.DetectorPercent => filled.DetectorAutomatic == true
            ? Services.Realtime.RealtimeActualValues.Of(filled).DetectorPercent is { } percent
                ? LocalizationService.Format("S.Realtime.ProfilePercent", percent)
                : LocalizationService.Get("S.Realtime.ProfilePerGroup")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.DetectorPercent),
        ProfileParam.IgnoreSmallPercent => filled.IgnoreSmallPercent is 0 or null
            ? LocalizationService.Get("S.Realtime.ProfileOff")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.IgnoreSmallPercent),
        ProfileParam.ConfidencePercent => LocalizationService.Format("S.Realtime.ProfilePercent", filled.ConfidencePercent),
        _ => "",
    };

    /// <summary>
    /// The preset's value as the 還原 tip names it: 自動 where the preset leaves a value to 自動,
    /// rather than what 自動 happens to come to under this mode's 語句合併.
    /// </summary>
    internal static string TemplateDisplay(RealtimeProfile template, ProfileParam param) => param switch
    {
        ProfileParam.SettleSeconds when template.SettleAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        ProfileParam.DetectorPercent when template.DetectorAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        _ => Display(template, param),
    };

    internal static string SecondsText(double seconds) =>
        seconds < 0.005 ? LocalizationService.Get("S.Realtime.ProfileSettleNone")
        : LocalizationService.Format("S.Realtime.ProfileSeconds", RealtimeProfileEditing.Seconds(seconds));

    /// <summary>What a slider's position reads as while it is being moved, before it is written.</summary>
    internal static string SliderReading(ProfileParam param, double value) => param switch
    {
        ProfileParam.SentenceMerge => RealtimeProfileEditing.SentenceMergeName(RealtimeProfileEditing.SentenceMergeAt(value)),
        ProfileParam.SettleSeconds => SecondsText(Math.Round(value * PollSeconds, 2)),
        ProfileParam.DetectorPercent => LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        ProfileParam.IgnoreSmallPercent => value < 0.5
            ? LocalizationService.Get("S.Realtime.ProfileOff")
            : LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        ProfileParam.ConfidencePercent => LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        _ => "",
    };

    internal static int AlignIndex(string? align) => RealtimeTextAligns.Normalize(align) switch
    {
        RealtimeTextAligns.Center => 1,
        RealtimeTextAligns.Left => 2,
        _ => 0,
    };

    /// <summary>
    /// Turns a value to 自動 or 手動 and nothing else: the figure for 手動 stays as it was, so 手動
    /// comes back where the user left it — or, never moved, at the preset's figure (on 標準, about
    /// 0.15 seconds and 85%: see <see cref="RealtimePresets.Create"/>).
    /// </summary>
    internal static void SetAutomatic(RealtimeProfile profile, ProfileParam param, bool automatic)
    {
        if (param == ProfileParam.SettleSeconds) profile.SettleAutomatic = automatic;
        else profile.DetectorAutomatic = automatic;
    }

    /// <summary>Writes a slider position as the value it stands for, in the user's units.</summary>
    internal static void SetNumber(RealtimeProfile profile, ProfileParam param, double position)
    {
        int whole = (int)Math.Round(position);
        switch (param)
        {
            case ProfileParam.SentenceMerge: profile.SentenceMerge = RealtimeProfileEditing.SentenceMergeAt(position); break;
            case ProfileParam.IgnoreSmallPercent: profile.IgnoreSmallPercent = whole; break;
            case ProfileParam.ConfidencePercent: profile.ConfidencePercent = whole; break;
            // A slider is only shown on 手動; the switch is written with the figure, as the file
            // keeps it whenever a figure is kept — see RealtimeProfile.SettleAutomatic.
            case ProfileParam.DetectorPercent:
                profile.DetectorAutomatic = false;
                profile.DetectorPercent = whole;
                break;
            case ProfileParam.SettleSeconds:
                profile.SettleAutomatic = false;
                profile.SettleSeconds = Math.Round(whole * PollSeconds, 2);
                break;
        }
    }

    /// <summary>A value's row by name, as the card's line about the change names it.</summary>
    internal static string NameKey(ProfileParam param) => param switch
    {
        ProfileParam.SentenceMerge => "S.Realtime.ProfileMerge",
        ProfileParam.Align => "S.Realtime.ProfileAlign",
        ProfileParam.FilterNumbersAndAbbreviations => "S.Realtime.ProfileFilterNumbers",
        ProfileParam.SettleSeconds => "S.Realtime.ProfileSettle",
        ProfileParam.ClearAfterEmptyReads => "S.Realtime.ProfileKeep",
        ProfileParam.DetectorPercent => "S.Realtime.ProfileDetector",
        ProfileParam.IgnoreSmallPercent => "S.Realtime.ProfileIgnoreSmall",
        _ => "S.Realtime.ProfileConfidence",
    };

    /// <summary>A value as the card's line about the change gives it: 自動 for 自動, 開啟／關閉 for a switch.</summary>
    internal static string ValueName(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.FilterNumbersAndAbbreviations => OnOff(filled.FilterNumbersAndAbbreviations != false),
        ProfileParam.ClearAfterEmptyReads => OnOff(filled.ClearAfterEmptyReads == RealtimeProfile.MinClearAfterEmptyReads),
        ProfileParam.SettleSeconds when filled.SettleAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        ProfileParam.DetectorPercent when filled.DetectorAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        _ => Display(filled, param),
    };

    internal static string OnOff(bool on) => LocalizationService.Get(on ? "S.Realtime.ProfileOn" : "S.Realtime.ProfileOff");
}
