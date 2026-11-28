using OverTranslate.Models;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The rules of changing a running session's translation service from the floating bar, kept
/// apart from the controller so they can be checked without a session.
/// </summary>
internal static class RealtimeProviderSwitch
{
    /// <summary>
    /// Whether picking <paramref name="requested"/> is a change from <paramref name="current"/>. A
    /// retired option counts as the one it became, so picking 「(標準)」 over a saved 「(RPC)」 does not
    /// restart anything.
    /// </summary>
    public static bool IsChange(TranslationProvider current, TranslationProvider requested) =>
        LanguageData.CurrentProvider(current) != LanguageData.CurrentProvider(requested);

    /// <summary>
    /// Writes the pick where 即時翻譯 keeps its own service, for the page to show and the next
    /// session to start on. The service the rest of the application translates with is left alone:
    /// the two features keep their choices apart.
    /// </summary>
    public static void Remember(AppSettings settings, TranslationProvider provider) =>
        settings.Realtime.Provider = LanguageData.CurrentProvider(provider);
}
