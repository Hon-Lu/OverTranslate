using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// How a kept 即時翻譯 framing is named and described: what it is called when the user has not given
/// it a name, and the one line under it on the page.
/// </summary>
/// <remarks>
/// <para>Computed here rather than written into the file when the preset is saved, so that a default
/// name follows the interface language and a monitor's current number: a preset saved as 螢幕 1 while
/// the interface was Chinese does not go on saying so after the user switches to English, and the
/// same monitor keeps the same number when another is plugged in before it.</para>
///
/// <para>A window is named by its title, which is what the user recognises it by; the process is
/// only the fallback for a window that had none, and what the line under it names so that two alike
/// titles can be told apart.</para>
/// </remarks>
internal static class RealtimeFramingDisplay
{
    /// <summary>How much of a window's title is kept in a name, in characters.</summary>
    /// <remarks>
    /// The bar's message is one line that cuts rather than wraps, and a browser's title runs to a
    /// paragraph. Long enough for a game's window name and a video's title, short enough that the
    /// name is still the thing the message is about.
    /// </remarks>
    private const int MaxTitleLength = 48;

    /// <summary>
    /// What the list and the bar call <paramref name="preset"/>: the user's own name where they gave
    /// one, otherwise the target's.
    /// </summary>
    public static string Name(RealtimeFramingPreset preset)
    {
        var name = (preset.Name ?? "").Trim();
        return name.Length > 0 ? name : AutomaticName(preset);
    }

    /// <summary>How much of a name a sentence on the bar carries, in characters.</summary>
    /// <remarks>
    /// A name runs to 60 characters, and the bar's message is one line with a cap on its width. Cut
    /// in the name rather than at the end of the sentence, so that what follows it — 未儲存的修改已捨棄
    /// — is still read.
    /// </remarks>
    private const int MaxMessageNameLength = 16;

    /// <summary>
    /// <see cref="Name"/> as a sentence on the bar quotes it: the first
    /// <see cref="MaxMessageNameLength"/> characters and …, when it is longer.
    /// </summary>
    public static string MessageName(RealtimeFramingPreset preset)
    {
        var name = Name(preset);
        return name.Length > MaxMessageNameLength ? name[..MaxMessageNameLength].TrimEnd() + "…" : name;
    }

    /// <summary>The second line of a row in the bar's list: how many blocks, nothing else.</summary>
    public static string BlockCount(RealtimeFramingPreset preset) =>
        LocalizationService.Format("S.Realtime.FramingBlocksShort", preset.Blocks.Count);

    /// <summary>
    /// Why a preset of this monitor or application cannot be placed now, as the list says it under
    /// the name: the size the screen or window has now, or that the window reaches another screen.
    /// </summary>
    public static string Unusable(RealtimeFramingStore.Miss why, RealtimeFramingStore.Target target) =>
        why switch
        {
            RealtimeFramingStore.Miss.OffScreen => LocalizationService.Get("S.Realtime.FramingOffScreenShort"),
            RealtimeFramingStore.Miss.WindowSize => LocalizationService.Format(
                "S.Realtime.FramingWindowSizeDiffers", target.WindowBounds.Width, target.WindowBounds.Height),
            _ => LocalizationService.Format(
                "S.Realtime.FramingScreenSizeDiffers", target.Screen.Width, target.Screen.Height),
        };

    /// <summary>
    /// The name a preset carries while the user has not given it one — 螢幕 1 · 2560×1440, the
    /// window's title, and （2）, （3） on a target's second and later presets.
    /// </summary>
    public static string AutomaticName(RealtimeFramingPreset preset)
    {
        var name = TargetName(preset);
        return preset.Ordinal > 1
            ? LocalizationService.Format("S.Realtime.FramingNumbered", name, preset.Ordinal)
            : name;
    }

    /// <summary>The target's own name, before the numbering a second preset of it takes.</summary>
    private static string TargetName(RealtimeFramingPreset preset)
    {
        if (preset.IsForWindow)
        {
            var title = (preset.WindowTitle ?? "").Trim();
            if (title.Length > 0) return Shorten(title);

            var process = (preset.WindowProcess ?? "").Trim();
            return process.Length > 0 ? process : LocalizationService.Get("S.Realtime.FramingsWindowUnknown");
        }

        // The monitor's number and 主螢幕 as it is now, but the resolution the framing was saved at:
        // a framing kept at another resolution is listed greyed beside the monitor's current size,
        // and naming it by that size would make the two read the same.
        return ScreenOf(preset) is { } screen
            ? ScreenLabel(screen, preset.Screen.Width, preset.Screen.Height)
            : LocalizationService.Format("S.Realtime.FramingScreenName", preset.Screen.Width, preset.Screen.Height);
    }

    /// <summary>
    /// The line under the name: how many blocks the framing keeps, and for a window the application
    /// they were drawn over.
    /// </summary>
    public static string Detail(RealtimeFramingPreset preset)
    {
        var blocks = LocalizationService.Format("S.Realtime.FramingBlockCount", preset.Blocks.Count);

        var process = (preset.WindowProcess ?? "").Trim();
        return preset.IsForWindow && process.Length > 0
            ? $"{LocalizationService.Format("S.Realtime.CaptureWindowDetail", process)} · {blocks}"
            : blocks;
    }

    /// <summary>
    /// What typing <paramref name="typed"/> into the name box stores: the text itself, or "" when it
    /// only repeats the name the target already gives the preset — so a name the user did not really
    /// choose goes on following the language and the monitor numbering.
    /// </summary>
    public static string StoredName(RealtimeFramingPreset preset, string? typed)
    {
        var clean = RealtimeFramingPreset.CleanName(typed);
        return string.Equals(clean, AutomaticName(preset), StringComparison.CurrentCulture) ? "" : clean;
    }

    /// <summary>
    /// The monitor a preset was saved on, when it is still attached. Found by its device name rather
    /// than by its rectangle, so a monitor that moved keeps its name and number.
    /// </summary>
    public static Screen? ScreenOf(RealtimeFramingPreset preset) =>
        Array.Find(Screen.AllScreens, screen => screen.DeviceName == preset.ScreenDeviceName);

    /// <summary>
    /// The same wording the page's screen picker uses, so the two name a monitor identically — see
    /// <c>RealtimePage</c>, which names a monitor for the framing line.
    /// </summary>
    public static string ScreenLabel(Screen screen) =>
        ScreenLabel(screen, screen.Bounds.Width, screen.Bounds.Height);

    /// <summary><see cref="ScreenLabel(Screen)"/> with another resolution in place of the monitor's own.</summary>
    private static string ScreenLabel(Screen screen, int width, int height)
    {
        var screens = Screen.AllScreens;
        var index = Array.FindIndex(screens, candidate => candidate.DeviceName == screen.DeviceName);

        return LocalizationService.Format(
            screen.Primary ? "S.Realtime.ScreenItemPrimary" : "S.Realtime.ScreenItem",
            index + 1, width, height);
    }

    private static string Shorten(string title) =>
        title.Length > MaxTitleLength ? title[..MaxTitleLength].TrimEnd() + "…" : title;
}
