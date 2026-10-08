using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// 設定 › 翻譯服務設定 decides which services the four menus offer. What is pinned here is the part a
// file and a menu can get wrong without anyone noticing: reading a list this build did not write,
// and keeping the service in use in its menu after it has been hidden.
public class ProviderVisibilityTests
{
    private static List<TranslationProvider> All =>
        LanguageData.Providers.Select(item => item.Provider).ToList();

    private static List<TranslationProvider> Menu(string json, TranslationProvider selected) =>
        ProviderVisibility.MenuItems(SettingsService.Parse(json), selected)
            .Select(item => item.Provider)
            .ToList();

    // ---- Reading the file ------------------------------------------------------------------

    [Fact]
    public void A_file_that_never_mentions_it_offers_every_service()
    {
        var settings = SettingsService.Parse("""{"Provider":"Bing"}""");

        Assert.Empty(settings.HiddenProviders);
        Assert.Empty(ProviderVisibility.Hidden(settings));
        Assert.Equal(All, ProviderVisibility.MenuItems(settings, settings.Provider).Select(i => i.Provider));
    }

    [Fact]
    public void Hidden_services_are_read_back_by_name()
    {
        var settings = SettingsService.Parse("""{"HiddenProviders":["Youdao","DeepL"]}""");

        Assert.Equal(
            new HashSet<TranslationProvider> { TranslationProvider.Youdao, TranslationProvider.DeepL },
            ProviderVisibility.Hidden(settings));
    }

    // A later version's service, a typo, a number: each costs only itself, and the rest of the list
    // and of the file still read.
    [Fact]
    public void Names_this_build_does_not_know_are_passed_over()
    {
        var settings = SettingsService.Parse(
            """{"Provider":"Bing","HiddenProviders":["Papago","Youdao","","3","bing2"]}""");

        Assert.Equal(TranslationProvider.Bing, settings.Provider);
        Assert.Equal(new HashSet<TranslationProvider> { TranslationProvider.Youdao }, ProviderVisibility.Hidden(settings));
    }

    // Not a list of names at all: the field falls back to empty, the file around it does not.
    [Fact]
    public void A_list_that_is_not_names_costs_only_itself()
    {
        var settings = SettingsService.Parse("""{"Provider":"Bing","HiddenProviders":[1,{"x":2}]}""");

        Assert.Equal(TranslationProvider.Bing, settings.Provider);
        Assert.Empty(ProviderVisibility.Hidden(settings));
    }

    [Fact]
    public void Hiding_every_service_is_read_as_hiding_none()
    {
        var names = string.Join(",", All.Select(provider => $"\"{provider}\""));
        var settings = SettingsService.Parse($$"""{"HiddenProviders":[{{names}}]}""");

        Assert.Empty(ProviderVisibility.Hidden(settings));
        Assert.Equal(All, Menu($$"""{"HiddenProviders":[{{names}}]}""", TranslationProvider.Bing));
    }

    // 「Google 翻譯 (RPC)」 became 「(標準)」, so hiding the one hides the other — the same reading
    // the saved choice gets (LanguageData.CurrentProvider).
    [Fact]
    public void The_retired_Google_RPC_name_hides_the_standard_Google_option()
    {
        var settings = SettingsService.Parse("""{"HiddenProviders":["Google2"]}""");

        Assert.Equal(new HashSet<TranslationProvider> { TranslationProvider.Google }, ProviderVisibility.Hidden(settings));
    }

    // ---- What a menu lists -----------------------------------------------------------------

    [Fact]
    public void A_menu_lists_the_visible_services_in_the_menus_order()
    {
        var menu = Menu("""{"HiddenProviders":["Bing","TranSmart","OpenAI"]}""", TranslationProvider.Google);

        Assert.Equal(
            All.Where(p => p is not (TranslationProvider.Bing or TranslationProvider.TranSmart or TranslationProvider.OpenAI)),
            menu);
    }

    // Hiding the service in use does not change what translates, so the menu has to go on showing
    // it — in its usual place, not tacked on at the end.
    [Fact]
    public void The_selected_service_stays_in_its_menu_while_hidden()
    {
        var menu = Menu("""{"HiddenProviders":["Bing","Microsoft"]}""", TranslationProvider.Microsoft);

        Assert.Equal(All.Where(p => p != TranslationProvider.Bing), menu);
    }

    [Fact]
    public void A_menu_whose_selection_is_the_retired_Google_RPC_option_shows_standard_Google()
    {
        var menu = Menu("""{"HiddenProviders":["Google"]}""", TranslationProvider.Google2);

        Assert.Equal(All, menu);
        Assert.DoesNotContain(TranslationProvider.Google2, menu);
    }

    // Only the hidden service kept for being selected is marked; the picker shows it closed and
    // leaves it out of the open list.
    [Fact]
    public void Only_a_hidden_selection_is_marked_as_one()
    {
        var items = ProviderVisibility.MenuItems(
            SettingsService.Parse("""{"HiddenProviders":["Bing","Microsoft"]}"""), TranslationProvider.Microsoft);

        Assert.Equal(
            [TranslationProvider.Microsoft],
            items.Where(item => item.IsHiddenSelection).Select(item => item.Provider));
    }

    [Fact]
    public void A_visible_selection_is_not_marked()
    {
        var items = ProviderVisibility.MenuItems(
            SettingsService.Parse("""{"HiddenProviders":["Bing"]}"""), TranslationProvider.Microsoft);

        Assert.DoesNotContain(items, item => item.IsHiddenSelection);
    }

    [Fact]
    public void A_retired_Google_RPC_selection_is_marked_when_Google_is_hidden()
    {
        var items = ProviderVisibility.MenuItems(
            SettingsService.Parse("""{"HiddenProviders":["Google"]}"""), TranslationProvider.Google2);

        Assert.True(items.Single(item => item.Provider == TranslationProvider.Google).IsHiddenSelection);
    }

    // ---- Writing it back -------------------------------------------------------------------

    [Fact]
    public void Stored_in_the_menus_order_with_unknown_names_kept()
    {
        var stored = ProviderVisibility.ToStored(
            ["Papago", "Bing", "Google2"],
            [TranslationProvider.OpenAI, TranslationProvider.Bing]);

        Assert.Equal(["Bing", "OpenAI", "Papago"], stored);
    }

    [Fact]
    public void Survives_a_save_and_a_read()
    {
        var settings = new AppSettings
        {
            HiddenProviders = ProviderVisibility.ToStored([], [TranslationProvider.Youdao, TranslationProvider.DeepL]),
        };

        var read = SettingsService.Parse(SettingsService.Serialize(settings));

        Assert.Equal(["Youdao", "DeepL"], read.HiddenProviders);
    }
}
