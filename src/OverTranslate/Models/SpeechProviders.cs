using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>The services that read aloud. Stored by name, as the translation services are.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechProvider { Google, Microsoft, Bing }

/// <summary>Which service reads aloud, from the two settings that decide it.</summary>
public static class SpeechProviders
{
    /// <summary>In the order the settings menu offers them.</summary>
    public static readonly IReadOnlyList<SpeechProvider> All = [SpeechProvider.Google, SpeechProvider.Microsoft, SpeechProvider.Bing];

    /// <summary>The service's name as the user sees it, which is the same in every language.</summary>
    public static string Display(SpeechProvider provider) => provider.ToString();

    /// <summary>The service that reads aloud for <paramref name="settings"/>.</summary>
    public static SpeechProvider Resolve(AppSettings settings) =>
        Resolve(settings.Provider, settings.SpeechFollowsProvider, settings.SpeechProvider);

    /// <summary>
    /// The translation service's own voice when <paramref name="follow"/> is on and it has one,
    /// otherwise <paramref name="chosen"/>.
    /// </summary>
    public static SpeechProvider Resolve(TranslationProvider translation, bool follow, SpeechProvider chosen)
    {
        if (!follow) return chosen;

        return translation switch
        {
            TranslationProvider.Google or TranslationProvider.Google2 or TranslationProvider.GoogleChrome => SpeechProvider.Google,
            TranslationProvider.Microsoft => SpeechProvider.Microsoft,
            TranslationProvider.Bing => SpeechProvider.Bing,
            _ => chosen,
        };
    }
}
