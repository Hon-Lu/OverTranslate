namespace OverTranslate.Models;

/// <summary>
/// Which services the translation-service menus offer: the switches in 設定 › 翻譯服務設定.
/// </summary>
/// <remarks>
/// <para>Hiding is about the menus and nothing else. A hidden service still backs another up and
/// still answers dictionary lookups (TranslationService, DictionaryLookupPlan), and the choice a
/// menu has saved is never rewritten by it: a hidden service that is in use stays in use, and stays
/// in that menu, until the user picks something else.</para>
///
/// <para>Stored as the hidden ones rather than the visible ones, so a service added in a later
/// version is offered from the start instead of arriving switched off.</para>
/// </remarks>
public static class ProviderVisibility
{
    /// <summary>The services <paramref name="names"/> leaves out of the menus.</summary>
    /// <remarks>
    /// A name this build does not know — a file from a later version, or edited by hand — is passed
    /// over, and a retired one is read as the option it became (<see cref="LanguageData.CurrentProvider"/>).
    /// A list that hides everything is read as hiding nothing: the settings page never writes one,
    /// and the alternative is menus with nothing in them but whatever happens to be selected.
    /// </remarks>
    public static IReadOnlySet<TranslationProvider> Hidden(IEnumerable<string>? names)
    {
        var hidden = new HashSet<TranslationProvider>();
        foreach (var name in names ?? [])
            if (Parse(name) is { } provider)
                hidden.Add(provider);

        return LanguageData.Providers.All(item => hidden.Contains(item.Provider))
            ? new HashSet<TranslationProvider>()
            : hidden;
    }

    /// <inheritdoc cref="Hidden(IEnumerable{string}?)"/>
    public static IReadOnlySet<TranslationProvider> Hidden(AppSettings settings) =>
        Hidden(settings.HiddenProviders);

    /// <summary>
    /// What a menu holds: every service not hidden, plus the one that menu has selected, in
    /// <see cref="LanguageData.Providers"/> order.
    /// </summary>
    /// <remarks>
    /// The selected one stays even when hidden, so hiding the service in use neither changes what
    /// translates nor leaves the closed picker blank. It is marked
    /// <see cref="ProviderItem.IsHiddenSelection"/>: the closed picker shows it, the open list does
    /// not, and once the selection leaves it it cannot be chosen again.
    /// </remarks>
    public static List<ProviderItem> MenuItems(IEnumerable<string>? hiddenNames, TranslationProvider selected)
    {
        var hidden = Hidden(hiddenNames);
        selected = LanguageData.CurrentProvider(selected);
        return LanguageData.Providers
            .Where(item => item.Provider == selected || !hidden.Contains(item.Provider))
            .Select(item => hidden.Contains(item.Provider) ? item with { IsHiddenSelection = true } : item)
            .ToList();
    }

    /// <inheritdoc cref="MenuItems(IEnumerable{string}?, TranslationProvider)"/>
    public static List<ProviderItem> MenuItems(AppSettings settings, TranslationProvider selected) =>
        MenuItems(settings.HiddenProviders, selected);

    /// <summary>The list to store for <paramref name="hidden"/>.</summary>
    /// <remarks>
    /// Names in <paramref name="stored"/> that this build cannot read are carried over as they are,
    /// so a service a later version hid is still hidden after a round trip through this one.
    /// </remarks>
    public static List<string> ToStored(IEnumerable<string>? stored, IEnumerable<TranslationProvider> hidden)
    {
        var set = hidden.Select(LanguageData.CurrentProvider).ToHashSet();
        return LanguageData.Providers
            .Where(item => set.Contains(item.Provider))
            .Select(item => item.Provider.ToString())
            .Concat((stored ?? []).Where(name => !string.IsNullOrWhiteSpace(name) && Parse(name) is null))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The menu service a stored name stands for, or null when it stands for none.</summary>
    /// <remarks>
    /// By name only: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would also take
    /// "3" or "0, 4", which no file this application writes contains.
    /// </remarks>
    private static TranslationProvider? Parse(string? name)
    {
        foreach (var provider in Enum.GetValues<TranslationProvider>())
        {
            if (!string.Equals(provider.ToString(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var current = LanguageData.CurrentProvider(provider);
            return LanguageData.Providers.Any(item => item.Provider == current) ? current : null;
        }
        return null;
    }
}
