using OverTranslate.Controls;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The rules the language pickers' search box applies. Worth pinning down because "it finds what I
/// typed" is the whole of the feature, and every one of these is a way someone actually types.
/// </summary>
public class ComboBoxSearchTests
{
    // The real resource keys, so Name resolves through the dictionary the way it does in the app —
    // a made-up key would come back as itself and the search would still "pass" on nothing.
    private static LangItem TraditionalChinese =>
        new("ZH-HANT", "S.Lang.TraditionalChinese", "Traditional Chinese");

    private static LangItem Japanese => new("JA", "S.Lang.Japanese", "Japanese");

    [Theory]
    [InlineData("繁體")]              // the local name, which is what the list shows in Chinese
    [InlineData("中文")]              // and part of it, from the middle of the word
    [InlineData("Traditional")]      // the English name
    [InlineData("traditional")]      // any case
    [InlineData("TRADITIONAL")]
    [InlineData("chinese")]          // the second word of it, not the start of anything
    [InlineData("nes")]              // and the middle of that word
    [InlineData("zh-hant")]          // the code, which is not shown in the list at all
    [InlineData("hant")]
    [InlineData("ｚｈ")]              // full-width, as a Chinese IME left on produces
    [InlineData("  chinese  ")]      // stray spaces around a pasted word
    [InlineData("")]                 // an empty box hides nothing
    public void Matches_FindsALanguageByAnyOfItsNames(string query) =>
        Assert.True(ComboBoxSearch.Matches(TraditionalChinese, query));

    [Theory]
    [InlineData("korean")]
    [InlineData("韓")]
    [InlineData("qq")]
    public void Matches_RejectsWhatIsNotThere(string query) =>
        Assert.False(ComboBoxSearch.Matches(TraditionalChinese, query));

    // Several words narrow from more than one direction at once, in either order, and across the two
    // languages the item is named in — the whole point of searching the names together.
    [Theory]
    [InlineData("trad chinese")]
    [InlineData("chinese trad")]
    [InlineData("繁 chinese")]
    [InlineData("zh 中文")]
    public void Matches_RequiresEveryTermButNotTheirOrder(string query) =>
        Assert.True(ComboBoxSearch.Matches(TraditionalChinese, query));

    [Fact]
    public void Matches_RejectsWhenOnlyOneTermIsThere() =>
        Assert.False(ComboBoxSearch.Matches(TraditionalChinese, "chinese korean"));

    // "ja" is in Japanese's code and in its English name; it is nowhere in 繁體中文's. A prefix-only
    // search would miss the second half of a name, and a search of the displayed label alone would
    // miss the code entirely — this is the pair that catches either mistake.
    [Fact]
    public void Matches_SearchesCodesAsWellAsNames()
    {
        Assert.True(ComboBoxSearch.Matches(Japanese, "ja"));
        Assert.False(ComboBoxSearch.Matches(TraditionalChinese, "ja"));
    }

    [Fact]
    public void Matches_TakesAnItemItKnowsNothingAbout()
    {
        Assert.True(ComboBoxSearch.Matches("Some plain string", "plain"));
        Assert.False(ComboBoxSearch.Matches(null, "plain"));
    }

    // Every entry in every picker has to be reachable by its own code, which is what someone who
    // knows the code will type first.
    [Fact]
    public void Matches_ReachesEveryLanguageByItsOwnCode()
    {
        var all = LanguageData.SourceLanguages
            .Concat(LanguageData.TargetLanguages)
            .Concat(LanguageData.OcrSourceLanguages);

        foreach (var lang in all)
            Assert.True(ComboBoxSearch.Matches(lang, lang.Code), lang.Code);
    }
}
