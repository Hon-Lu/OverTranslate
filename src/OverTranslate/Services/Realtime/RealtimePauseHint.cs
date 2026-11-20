namespace OverTranslate.Services.Realtime;

/// <summary>
/// The wording that tells the user which shortcut pauses and resumes a running realtime session.
/// </summary>
/// <remarks>
/// The floating control bar tooltip and 運作方式 both name the key the user actually has bound
/// rather than the default. Shared here so the shortcut is read and normalized in one place.
///
/// The shortcut can be cleared in 設定. In that state the settings-page line disappears, while the
/// icon tooltip still names the clickable action without inventing a keyboard equivalent.
/// </remarks>
public static class RealtimePauseHint
{
    /// <summary>
    /// The shortcut currently bound to 暫停 / 繼續, as 設定 displays it.
    /// </summary>
    /// <remarks>
    /// This used to read the 截圖翻譯 shortcut, because pausing was a second meaning that key took on
    /// while a session was running. It has its own now: sharing meant the reader could not choose what
    /// to press for the thing they reach for most, and a session lasts hours.
    ///
    /// Empty when the shortcut is not set: a cleared shortcut keeps its old combination in the
    /// settings file, and naming that would advertise a key that does nothing.
    /// </remarks>
    public static string CurrentHotkey =>
        SettingsService.Instance.Current is { RealtimePauseHotkeyEnabled: true } settings
            ? settings.RealtimePauseHotkeyDisplay
            : "";

    /// <summary>
    /// For the pause/resume icon in the translating control bar. The button already communicates the
    /// action visually; the tooltip supplies its exact meaning and the equivalent keyboard path.
    /// </summary>
    /// <param name="paused">
    /// The session's current state, not the action — a paused session's button offers 繼續.
    /// </param>
    public static string ForControlTooltip(string? hotkey, bool paused)
    {
        var action = LocalizationService.Get(paused ? "S.Realtime.Resume" : "S.Realtime.Pause");

        return Normalize(hotkey) is { Length: > 0 } key ? $"{action} ({key})" : action;
    }

    /// <summary>
    /// For 運作方式, where there is room to say what pausing actually does — that it releases the
    /// recognition model rather than merely freezing the picture, which is the part that decides
    /// whether a user reaches for it when they want their machine back.
    /// </summary>
    public static string ForSettingsPage(string? hotkey) =>
        Normalize(hotkey) is { Length: > 0 } key
            ? LocalizationService.Format("S.Realtime.Step4", key)
            : "";

    private static string Normalize(string? hotkey) => hotkey?.Trim() ?? "";
}
