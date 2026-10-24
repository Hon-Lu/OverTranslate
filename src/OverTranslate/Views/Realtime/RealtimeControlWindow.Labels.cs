using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Which of the bar's buttons the label card names, and what it says about each — the card itself
/// is <see cref="Controls.ToolbarLabel"/>, the same one the capture toolbar uses.
/// </summary>
/// <remarks>
/// <para>The card grows towards the middle of the screen: over the bar where it sits in the lower
/// half, which is where it starts, and under it once it has been dragged up into the upper half.
/// There is no selection here to keep clear of, and the middle is where there is room.</para>
///
/// <para>The status text and the dot are left to the capsule's own tooltip: they are not buttons,
/// and what a collapsed capsule shows there is the words it has no room for, not a name.</para>
/// </remarks>
public partial class RealtimeControlWindow
{
    private void InitializeLabels()
    {
        Labels.Attach(this, Chrome);
        Labels.PrefersAbove = () =>
        {
            var bar = PhysicalRect(Chrome);
            return bar.Top + bar.Height / 2 > _screenBounds.Top + _screenBounds.Height / 2;
        };

        // Edit mode.
        Labels.Add(CompareEditBtn, CompareName, CompareDescription(CompareEditBtn));
        Labels.Add(CrosshairBtn, () => LocalizationService.Get(
            _crosshairEnabled ? "S.Realtime.CrosshairOff" : "S.Realtime.CrosshairOn"));
        Labels.Add(StartBtn, () => LocalizationService.Get("S.Realtime.StartTranslating"));
        Labels.Add(EditCloseBtn, () => LocalizationService.Get("S.Realtime.StopSession"));

        // Running.
        Labels.Add(SourceLangBtn, () => LocalizationService.Get("S.Realtime.ChangeSourceLanguage"));
        Labels.Add(TargetLangBtn, () => LocalizationService.Get("S.Realtime.ChangeTargetLanguage"));
        Labels.Add(CompareRunBtn, CompareName, CompareDescription(CompareRunBtn));
        Labels.Add(CaptureBtn, () => LocalizationService.Get("S.Shell.CaptureTranslate"));

        // The shortcut on a line of its own under the name, rather than in brackets after it as the
        // tooltip had it: the line under the name is where the card says more, and a key combination
        // on its own there needs no word to say what it is.
        Labels.Add(PauseBtn,
            () => LocalizationService.Get(_isPaused ? "S.Realtime.Resume" : "S.Realtime.Pause"),
            () => RealtimePauseHint.CurrentHotkey?.Trim() is { Length: > 0 } key ? key : null);
        Labels.Add(EditBtn, () => LocalizationService.Get("S.Realtime.EditBlocks"));
        Labels.Add(CollapseBtn, () => LocalizationService.Get(
            _collapsed ? "S.Realtime.ExpandBar" : "S.Realtime.CollapseBar"));
        Labels.Add(RunCloseBtn, () => LocalizationService.Get("S.Realtime.StopSession"));
    }

    private static string CompareName() => LocalizationService.Get("S.Realtime.CompareDisplay");

    /// <summary>
    /// What 對照顯示 does when pressed, from the button's tooltip, without the name it opens with.
    /// </summary>
    /// <remarks>
    /// The tooltip has to say which switch it belongs to, so it starts "原文對照：…" in every language;
    /// under a card already headed 原文對照 that would be said twice.
    /// </remarks>
    private static Func<string?> CompareDescription(FrameworkElement button) =>
        () => StripLeadingName(button.ToolTip as string, CompareName());

    internal static string? StripLeadingName(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith(name, StringComparison.Ordinal)) return text;

        var rest = text[name.Length..].TrimStart(':', '：', ' ');
        if (rest.Length == 0) return null;
        return char.ToUpper(rest[0], System.Globalization.CultureInfo.CurrentCulture) + rest[1..];
    }
}
