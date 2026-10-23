using OverTranslate.Controls;
using OverTranslate.Models;

namespace OverTranslate.Views.Realtime;

/// <summary>Which half of the running capsule's language pair a menu changes.</summary>
internal enum RealtimeLanguageMenuKind
{
    Source,
    Target
}

/// <summary>One row of a language menu: the language, and whether it is the one in use.</summary>
internal sealed record RealtimeLanguageMenuEntry(LangItem Language, bool IsSelected)
{
    public string Code => Language.Code;

    /// <summary>
    /// The name alone — the capsule's own label for the pair. The doubled-up label the pages show
    /// would not fit the menu's width, and the English half is still found by the search.
    /// </summary>
    public string Label => Language.ShortName;
}

/// <summary>
/// What the running capsule's two language menus list (#345), kept apart from the window that
/// draws them so the lists and the search can be checked without one.
/// </summary>
internal static class RealtimeLanguageMenuItems
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

    /// <summary>The menu's rows in the order the pickers list them, with the one in use marked.</summary>
    public static IReadOnlyList<RealtimeLanguageMenuEntry> For(RealtimeLanguageMenuKind kind, string? selectedCode)
    {
        var languages = kind == RealtimeLanguageMenuKind.Source
            ? SourceLanguages
            : LanguageData.TargetLanguages;

        return [.. languages.Select(language => new RealtimeLanguageMenuEntry(
            language, string.Equals(language.Code, selectedCode, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// Whether a row survives the search box — the rule the capture toolbar's target picker applies,
    /// so a language is found here by the same words it is found by there.
    /// </summary>
    public static bool Matches(RealtimeLanguageMenuEntry entry, string query) =>
        ComboBoxSearch.Matches(entry.Language, query);
}
