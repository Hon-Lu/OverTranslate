using OverTranslate.Controls;
using OverTranslate.Models;

namespace OverTranslate.Views.Realtime;

/// <summary>Which of the running capsule's two languages a pick changes.</summary>
internal enum RealtimeLanguageKind
{
    Source,
    Target
}

/// <summary>One row of a language column: the language, and whether it is the one in use.</summary>
internal sealed record RealtimeLanguageEntry(LangItem Language, bool IsSelected)
{
    public string Code => Language.Code;

    /// <summary>
    /// The name alone — the capsule's own label for the pair. The doubled-up label the pages show
    /// would not fit the column's width, and the English half is still found by the search.
    /// </summary>
    public string Label => Language.ShortName;
}

/// <summary>One row of the service column: the service, and whether it is the one in use.</summary>
internal sealed record RealtimeProviderEntry(ProviderItem Item, bool IsSelected)
{
    public TranslationProvider Provider => Item.Provider;

    /// <summary>
    /// The name alone. The detail the pages show beside a service — a local model's name — is left
    /// out: the column says which service, and which model is the settings page's to say.
    /// </summary>
    public string Label => Item.Display;
}

/// <summary>
/// What the running capsule's translation panel lists, kept apart from the window that draws it so
/// the lists and the search can be checked without one.
/// </summary>
internal static class RealtimeTranslationPanelItems
{
    /// <summary>
    /// The languages a realtime session can read: the recogniser's list without 自動.
    /// </summary>
    /// <remarks>
    /// Realtime recognition gets one look at a frame and no retry, so there is no automatic mode to
    /// fall back on — see RealtimePage.ApplyPageDefaults. The page offers this same list.
    /// </remarks>
    public static IReadOnlyList<LangItem> SourceLanguages { get; } =
        [.. LanguageData.OcrSourceLanguages.Where(language => !LanguageData.IsAutomaticSource(language.Code))];

    /// <summary>A language column's rows in the order the pickers list them, with the one in use marked.</summary>
    public static IReadOnlyList<RealtimeLanguageEntry> For(RealtimeLanguageKind kind, string? selectedCode)
    {
        var languages = kind == RealtimeLanguageKind.Source
            ? SourceLanguages
            : LanguageData.TargetLanguages;

        return [.. languages.Select(language => new RealtimeLanguageEntry(
            language, string.Equals(language.Code, selectedCode, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// The service column's rows: the services the settings page leaves in the menus, plus the one in
    /// use even when it is hidden — the rule every service picker follows (see ProviderVisibility).
    /// Once the session has moved off a hidden service, it is not offered again.
    /// </summary>
    public static IReadOnlyList<RealtimeProviderEntry> Providers(IEnumerable<string>? hiddenNames, TranslationProvider selected)
    {
        selected = LanguageData.CurrentProvider(selected);
        return [.. ProviderVisibility.MenuItems(hiddenNames, selected)
            .Select(item => new RealtimeProviderEntry(item, item.Provider == selected))];
    }

    /// <summary>
    /// Whether a row survives the search box — the rule the capture toolbar's target picker applies,
    /// so a language is found here by the same words it is found by there.
    /// </summary>
    public static bool Matches(RealtimeLanguageEntry entry, string query) =>
        ComboBoxSearch.Matches(entry.Language, query);
}
