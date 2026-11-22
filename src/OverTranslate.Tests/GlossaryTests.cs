using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// #228: the glossary the two AI services send the entries a text contains with — matching, where
// the entries go in a request, and how a list is pasted, imported, exported and read from settings.
public class GlossaryTests
{
    private const string Instruction = "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：";
    private const string Heading = "以下專有名詞須使用指定譯名：";

    private static readonly GlossaryMatcher Work = Matcher(
        ("アリサ", "艾莉莎"), ("リオン", "黎昂"), ("リオ", "莉歐"), ("団長", "團長大人"),
        ("Hope", "霍普"), ("Moonfall Keep", "月墜要塞"));

    private static GlossaryMatcher Matcher(params (string Source, string Target)[] terms) =>
        new(terms.Select(term => new GlossaryTerm(term.Source, term.Target)));

    private static OpenAiPrompt Prompt(GlossaryMatcher? glossary = null, string? template = null, bool batch = false) => new(
        OpenAiPromptLanguage.TraditionalChinese,
        (template ?? OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese, batch))
            .Replace(OpenAiPrompt.TargetNamePlaceholder, "繁體中文"),
        "繁體中文",
        IsDefault: template is null,
        SourceLabel: "日文")
    {
        Glossary = glossary,
    };

    private static string[] Sources(IEnumerable<GlossaryTerm> terms) => [.. terms.Select(term => term.Source)];

    private static void Same(string[] expected, string[] actual) => Assert.Equal(expected, actual);

    // ── Matching ─────────────────────────────────────────────────────────────

    [Fact]
    public void LongerEntriesWin_AndAnOverlapIsNotReadTwice()
    {
        Same(["団長", "リオン", "リオ"], Sources(Work.Find("団長、リオンとリオが戻ってきました。")));
        Same(["リオン"], Sources(Work.Find("リオンです。")));
    }

    [Theory]
    [InlineData("Hope said she would come.", true)]
    [InlineData("Hope you're well.", true)]
    [InlineData("Hopeless, all of it.", false)]
    [InlineData("I hope so.", false)]
    [InlineData("NoHope here.", false)]
    [InlineData("「Hope」は来ない。", true)]
    public void AnEntryWithLatinLetters_MatchesAWholeWordWithItsCase(string text, bool found) =>
        Assert.Equal(found, Work.Find(text).Count == 1);

    [Fact]
    public void CjkEntriesMatchAnywhere_SoAnAccidentalMatchIsStillSent()
    {
        // The model is given it and translates the sentence as meant — measured 0 of 8 forced.
        Same(["リオ"], Sources(Work.Find("いつかリオデジャネイロに行きたい。")));
    }

    [Fact]
    public void EntriesComeInTheOrderTheyAppear_AcrossTheTextsOfABatch()
    {
        Same(["リオ", "アリサ"], Sources(Work.Find("リオはアリサの妹だ。")));
        Same(["Moonfall Keep", "アリサ", "団長"],
            Sources(Work.Find(["Go to Moonfall Keep.", "アリサ、団長、アリサ。"])));
    }

    [Fact]
    public void NoMatch_NoEntries() => Assert.Empty(Work.Find("今日はいい天気だね。"));

    [Fact]
    public void ARequestCarriesAtMostTwentyEntries_TheFirstFound()
    {
        var many = Matcher([.. Enumerable.Range(0, 30).Select(i => ($"語{i:00}", $"詞{i:00}"))]);
        var text = string.Concat(Enumerable.Range(0, 30).Select(i => $"語{i:00}、"));

        var found = many.Find(text);

        Assert.Equal(GlossaryMatcher.MaxTermsPerRequest, found.Count);
        Assert.Equal("語00", found[0].Source);
        Assert.Equal("語19", found[^1].Source);
    }

    [Fact]
    public void ARequestCarriesNoMoreThanItsCharacterBudget()
    {
        var long40 = new string('長', 40);
        var many = Matcher([.. Enumerable.Range(0, 15).Select(i => ($"{i:00}{long40[..38]}", long40))]);
        var text = string.Join("。", Enumerable.Range(0, 15).Select(i => $"{i:00}{long40[..38]}"));

        var found = many.Find(text);

        Assert.True(found.Sum(term => term.Source.Length + term.Target.Length) <= GlossaryMatcher.MaxTermCharactersPerRequest);
        Assert.Equal(7, found.Count);
    }

    [Fact]
    public void BlankEntriesAreLeftOut_AndARepeatedSourceKeepsItsLastTranslation()
    {
        var matcher = Matcher(("アリサ", "亞莉莎"), (" ", "x"), ("リオ", ""), ("アリサ", "艾莉莎"));

        Assert.Equal(1, matcher.Count);
        Assert.Equal("艾莉莎", matcher.Find("アリサ")[0].Target);
    }

    // ── Where the entries go ─────────────────────────────────────────────────

    [Fact]
    public void TheEntriesAreTheirOwnSection_StraightBeforeTheInstruction()
    {
        var message = Prompt(Work).Build("団長、リオンが戻った。");

        Assert.Equal(
            Heading + "\n日文：団長\n繁體中文：團長大人\n\n日文：リオン\n繁體中文：黎昂\n\n" + Instruction + "\n団長、リオンが戻った。",
            message);
    }

    [Fact]
    public void AfterTheEarlierTranslations_WhichStayFirst()
    {
        var message = Prompt(Work).Build("アリサ！", [new OpenAiTranslationPair("おはよう", "早安")]);

        Assert.Equal(
            "以下是之前的翻譯，翻譯時可參考：\n日文：おはよう\n繁體中文：早安\n\n" +
            Heading + "\n日文：アリサ\n繁體中文：艾莉莎\n\n" + Instruction + "\nアリサ！",
            message);
    }

    [Fact]
    public void ATextWithNoEntry_IsTheMessageItWasWithoutAGlossary()
    {
        var earlier = new[] { new OpenAiTranslationPair("おはよう", "早安") };
        Assert.Equal(Prompt().Build("いい天気。", earlier), Prompt(Work).Build("いい天気。", earlier));
    }

    [Fact]
    public void ATemplateWithNoContext_HasTheEntriesAtTheStart()
    {
        var message = Prompt(Work, "Translate into Chinese:\n{text}").Build("アリサ");

        Assert.Equal(Heading + "\n日文：アリサ\n繁體中文：艾莉莎\n\nTranslate into Chinese:\nアリサ", message);
    }

    [Fact]
    public void ABatch_CarriesWhatAnyOfItsTextsContains()
    {
        var message = Prompt(Work, batch: true).BuildBatch(["アリサ", "今日は", "Hope is here."]);

        Assert.StartsWith(Heading + "\n日文：アリサ\n繁體中文：艾莉莎\n\n日文：Hope\n繁體中文：霍普\n\n", message);
        Assert.Contains("<1>アリサ</1>\n<2>今日は</2>\n<3>Hope is here.</3>", message);
    }

    [Theory]
    [InlineData(OpenAiPromptLanguage.SimplifiedChinese, "以下专有名词须使用指定译名：")]
    [InlineData(OpenAiPromptLanguage.English, "Use these translations for the following names and terms:")]
    public void EachWordingHasItsHeading(OpenAiPromptLanguage language, string heading)
    {
        var prompt = new OpenAiPrompt(language, OpenAiPrompt.DefaultTemplate(language), "Chinese") { Glossary = Work };
        Assert.StartsWith(heading + "\n", prompt.Build("アリサ"));
    }

    [Fact]
    public void ADetectedSource_IsLabelledAsTheContextLabelsIt()
    {
        var prompt = Prompt(Work) with { SourceLabel = null };
        Assert.Contains("\n原文：アリサ\n繁體中文：艾莉莎", prompt.Build("アリサ"));
    }

    [Fact]
    public void CutToFit_DropsTheHistoryFirst_NeverTheEntries()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new OpenAiTranslationPair($"これは{i}番目のとても長い文です。", $"這是第{i}句很長的句子。"))
            .ToList();
        var request = new OpenAiChatRequest(new Uri("http://localhost/"), "m", "", Prompt(Work), ContextTokens: 400);

        var message = OpenAiChatTranslator.Message(request, "アリサ、来て。", history);

        Assert.Contains(Heading + "\n日文：アリサ\n繁體中文：艾莉莎", message);
        Assert.DoesNotContain("0番目", message);
    }

    // ── Paste, import, export ────────────────────────────────────────────────

    [Fact]
    public void ALineIsSplitAtItsFirstEqualsSign_HalfOrFullWidth()
    {
        var lines = GlossaryText.Parse("アリサ=艾莉莎\nミラの森 ＝ 蜜菈森林\nx=y=z");

        Assert.Equal([("アリサ", "艾莉莎"), ("ミラの森", "蜜菈森林"), ("x", "y=z")],
            lines.Select(line => (line.Source, line.Target)));
        Assert.All(lines, line => Assert.True(line.IsEntry));
    }

    [Fact]
    public void ATabWins_AsTwoCellsFromASpreadsheet()
    {
        var lines = GlossaryText.Parse("a=b\tc\td\n\"Snow\"\t\"史諾\"");

        Assert.Equal([("a=b", "c"), ("Snow", "史諾")], lines.Select(line => (line.Source, line.Target)));
    }

    [Fact]
    public void CommasAndArrows_AreStillRead()
    {
        var lines = GlossaryText.Parse("Hope, 霍普\nKael，凱伊爾\nA→B\n\"Snow, the Elder\",史諾長老");

        Assert.Equal([("Hope", "霍普"), ("Kael", "凱伊爾"), ("A", "B"), ("Snow, the Elder", "史諾長老")],
            lines.Select(line => (line.Source, line.Target)));
    }

    [Fact]
    public void BlankLines_Comments_AndAFirstLineOfHeadings_AreSkipped()
    {
        var lines = GlossaryText.Parse("﻿# target=ZH-HANT\r\n原文\t譯文\r\n\r\nアリサ\t艾莉莎\r\n原文=譯文");

        Assert.Equal([("アリサ", "艾莉莎"), ("原文", "譯文")], lines.Select(line => (line.Source, line.Target)));
        Assert.Equal([4, 5], lines.Select(line => line.Number));
    }

    [Fact]
    public void WhatCannotBeRead_IsReportedWithItsLine_AndLeftOut()
    {
        var lines = GlossaryText.Parse($"星詠みの塔 詠星之塔\n=y\nアリサ=\n{new string('長', 41)}=x\nゼノ=傑諾");

        Assert.Equal(
            [GlossaryLineProblem.NoSeparator, GlossaryLineProblem.Empty, GlossaryLineProblem.Empty,
             GlossaryLineProblem.TooLong, GlossaryLineProblem.None],
            lines.Select(line => line.Problem));
        Assert.Equal("星詠みの塔 詠星之塔", lines[0].Text);
        Assert.Equal([("ゼノ", "傑諾")], GlossaryText.Entries(lines).Select(entry => (entry.Source, entry.Target)));
    }

    [Fact]
    public void AnExport_ReadsBackAsTheSameListInTheSameLanguage()
    {
        var list = new GlossaryList
        {
            Name = "星環旅記",
            TargetLanguage = "ZH-HANT",
            Entries = [new() { Source = "アリサ", Target = "艾莉莎" }, new() { Source = "Moonfall Keep", Target = "月墜要塞" }],
        };

        var text = GlossaryText.Export(list);

        Assert.StartsWith("# target=ZH-HANT\r\n", text);
        Assert.Equal("ZH-HANT", GlossaryText.TargetOf(text));
        Assert.Equal(list.Entries.Select(entry => (entry.Source, entry.Target)),
            GlossaryText.Entries(GlossaryText.Parse(text)).Select(entry => (entry.Source, entry.Target)));
        Assert.True(GlossaryText.ExportEncoding.GetPreamble().Length > 0);
    }

    [Fact]
    public void ATextWithNoLanguageLine_SaysNone() => Assert.Null(GlossaryText.TargetOf("アリサ=艾莉莎"));

    // ── Settings ─────────────────────────────────────────────────────────────

    // Reading and writing glossaries.json: GlossaryStoreTests.

    [Fact]
    public void TheLimitsHold()
    {
        var read = new GlossarySettings
        {
            Lists = [.. Enumerable.Range(0, 25).Select(i => new GlossaryList
            {
                Id = $"g{i}",
                Name = new string('名', 50),
                Entries = [.. Enumerable.Range(0, 1100).Select(k => new GlossaryEntry { Source = $"s{k}", Target = "t" })],
            })],
        };

        var clean = GlossarySettings.Clean(read);

        Assert.Equal(GlossarySettings.MaxLists, clean.Lists.Count);
        Assert.Equal(GlossarySettings.MaxNameLength, clean.Lists[0].Name.Length);
        Assert.Equal(GlossarySettings.MaxEntries, clean.Lists[0].Entries.Count);
    }

    // ── Which glossary a request gets ────────────────────────────────────────

    private static GlossarySettings OneList(string language, params (string, string)[] entries)
    {
        var list = new GlossaryList
        {
            Id = "g", Name = "作品", TargetLanguage = language,
            Entries = [.. entries.Select(entry => new GlossaryEntry { Source = entry.Item1, Target = entry.Item2 })],
        };
        return new GlossarySettings { Lists = [list], ActiveId = "g" };
    }

    [Fact]
    public void OnlyARequestIntoTheGlossarysLanguage_GetsIt()
    {
        var settings = OneList("ZH-HANT", ("アリサ", "艾莉莎"));

        Assert.NotNull(GlossaryRuntime.For(settings, "zh-hant"));
        Assert.Null(GlossaryRuntime.For(settings, "EN-US"));
        Assert.Null(GlossaryRuntime.For(new GlossarySettings { Lists = settings.Lists }, "ZH-HANT"));
        Assert.Null(GlossaryRuntime.For(OneList("ZH-HANT"), "ZH-HANT"));
    }

    [Fact]
    public void AnEditedList_HasAnotherKey()
    {
        var before = OneList("ZH-HANT", ("アリサ", "艾莉莎"));
        var after = before.Clone();
        after.Lists[0].Entries[0].Target = "亞莉莎";

        var first = GlossaryRuntime.For(before, "ZH-HANT")!;
        Assert.Same(first, GlossaryRuntime.For(before, "ZH-HANT"));
        Assert.NotEqual(first.Key, GlossaryRuntime.For(after, "ZH-HANT")!.Key);
        Assert.Equal("艾莉莎", before.Lists[0].Entries[0].Target);
    }
}
