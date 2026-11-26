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

    /// <summary>
    /// The settings page's status line: the service that reads, and whether it is the translation
    /// service's own voice. Always a line, so the card does not change height as settings change.
    /// </summary>
    public static string InUseLine(AppSettings settings)
    {
        var (before, name, after) = InUseParts(settings);
        return before + name + after;
    }

    /// <summary>
    /// <see cref="InUseLine"/> split around the service's name, which the page sets apart from the
    /// rest of the line.
    /// </summary>
    public static (string Before, string Name, string After) InUseParts(AppSettings settings)
    {
        var key = FollowsTranslation(settings) ? "S.Settings.SpeechInUse" : "S.Settings.SpeechInUsePlain";
        var format = Services.LocalizationService.Get(key);
        var at = format.IndexOf("{0}", StringComparison.Ordinal);
        var name = Display(Resolve(settings));
        return at < 0 ? (format, "", "") : (format[..at], name, format[(at + 3)..]);
    }

    /// <summary>
    /// Whether the translation service's own voice reads: the switch is on and that service has one.
    /// Otherwise the picker's choice does, whatever the switch says.
    /// </summary>
    public static bool FollowsTranslation(AppSettings settings) =>
        settings.SpeechFollowsProvider && OwnVoice(settings.Provider) is not null;

    /// <summary>The service that reads aloud for <paramref name="settings"/>.</summary>
    public static SpeechProvider Resolve(AppSettings settings) =>
        Resolve(settings.Provider, settings.SpeechFollowsProvider, settings.SpeechProvider);

    /// <summary>
    /// The translation service's own voice when <paramref name="follow"/> is on and it has one,
    /// otherwise <paramref name="chosen"/>.
    /// </summary>
    public static SpeechProvider Resolve(TranslationProvider translation, bool follow, SpeechProvider chosen)
    {
        return follow ? OwnVoice(translation) ?? chosen : chosen;
    }

    /// <summary>The translation service's own voice, or null for a service that has none.</summary>
    private static SpeechProvider? OwnVoice(TranslationProvider translation) => translation switch
    {
        TranslationProvider.Google or TranslationProvider.Google2 or TranslationProvider.GoogleChrome => SpeechProvider.Google,
        TranslationProvider.Microsoft => SpeechProvider.Microsoft,
        TranslationProvider.Bing => SpeechProvider.Bing,
        _ => null,
    };
}
