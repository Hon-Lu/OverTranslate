using System.Windows;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 雙語互譯 from start to finish, against a scripted engine — every case here is one an engine was
/// seen to produce (#339), or one the rules already promised and must keep promising.
/// </summary>
public class BilingualTranslationTests
{
    private const string English = "EN-US";

    /// <summary>An engine that answers each target language from a script and records what it was asked.</summary>
    private sealed class ScriptedEngine(string detected, params (string Target, string Answer)[] answers)
    {
        public List<string> Asked { get; } = [];

        /// <summary>The source each request named, null for automatic.</summary>
        public List<string?> Sources { get; } = [];

        public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> Translate(string? source, string target)
        {
            Asked.Add(target);
            Sources.Add(source);
            var answer = answers.First(a => a.Target.Equals(target, StringComparison.OrdinalIgnoreCase)).Answer;
            return Task.FromResult((new List<TranslatedBlock> { new("", answer, new Rect()) }, detected));
        }
    }

    private static Task<BilingualAnswer> Run(string text, string first, string second, ScriptedEngine engine) =>
        BilingualTranslation.TranslateAsync(text, new BilingualPair(first, second), engine.Translate);

    private static string TextOf(BilingualAnswer answer) => answer.Blocks.Single().TranslatedText;

    [Fact]
    public async Task AnEngineThatAgreesIsAskedOnce()
    {
        var engine = new ScriptedEngine("zh-TW", (English, "Hello"));

        var answer = await Run("你好", "ZH-HANT", English, engine);

        Assert.Equal("Hello", TextOf(answer));
        Assert.Equal(English, answer.Target);
        Assert.Equal("ZH-HANT", answer.Source);
        Assert.Equal([English], engine.Asked);
    }

    [Fact]
    public async Task AnEngineThatReportsNothingIsAskedOnce()
    {
        var engine = new ScriptedEngine("", (English, "water"));

        var answer = await Run("вода", "RU", English, engine);

        Assert.Equal("water", TextOf(answer));
        Assert.Equal([English], engine.Asked);
    }

    // Han alone cannot say 日文 or 中文, so the guess goes to the first language; the engine reads
    // Japanese, which belongs in the second. The correction this was built for, still taken.
    [Fact]
    public async Task ACorrectionThatTranslatesIsTaken()
    {
        var engine = new ScriptedEngine("ja", ("JA", "大丈夫"), ("ZH-HANT", "沒關係"));

        var answer = await Run("大丈夫", "JA", "ZH-HANT", engine);

        Assert.Equal("沒關係", TextOf(answer));
        Assert.Equal("ZH-HANT", answer.Target);
        Assert.Equal("JA", answer.Source);
        Assert.Equal(["JA", "ZH-HANT"], engine.Asked);
    }

    // #339 as reported: Google reads 「вода」 as Macedonian, and Russian into Russian is the word.
    [Theory]
    [InlineData("вода")]
    [InlineData("Вода.")]               // Microsoft capitalises; some add a full stop
    public async Task AMisreadWordKeepsTheAnswerTheCharactersGot(string echo)
    {
        var engine = new ScriptedEngine("mk", (English, "water"), ("RU", echo));

        var answer = await Run("вода", "RU", English, engine);

        Assert.Equal("water", TextOf(answer));
        Assert.Equal(English, answer.Target);
        Assert.Equal("RU", answer.Source);
        Assert.Equal([English, "RU"], engine.Asked);
    }

    // Google reads a lone 「水」 as Chinese under 日文 ⇄ 英文; Bing reads 「大丈夫」 the same way.
    [Fact]
    public async Task AMisreadKanjiKeepsTheAnswerTheCharactersGot()
    {
        var engine = new ScriptedEngine("zh-CN", (English, "water"), ("JA", "水"));

        var answer = await Run("水", "JA", English, engine);

        Assert.Equal("water", TextOf(answer));
        Assert.Equal("JA", answer.Source);
    }

    // German under 英文 ⇄ 俄文 is a genuine third language, and the settings promise it goes into
    // the first one. That has to survive the new rule: the correction translates, so it is kept.
    [Fact]
    public async Task AThirdLanguageStillGoesToTheFirst()
    {
        var engine = new ScriptedEngine("de", ("RU", "Собака"), (English, "Dog"));

        var answer = await Run("Hund", English, "RU", engine);

        Assert.Equal("Dog", TextOf(answer));
        Assert.Equal(English, answer.Target);
        Assert.Equal("DE", answer.Source);
        Assert.Equal(["RU", English], engine.Asked);
    }

    // 德文 ⇄ 英文 is one script, so the characters cannot tell and the guess is the first
    // language — German into German. The engine called it Norwegian, which corrects nothing.
    [Fact]
    public async Task ASameScriptPairTriesTheOtherLanguageWhenTheFirstEchoes()
    {
        var engine = new ScriptedEngine("no", ("DE", "Hund"), (English, "Dog"));

        var answer = await Run("Hund", "DE", English, engine);

        Assert.Equal("Dog", TextOf(answer));
        Assert.Equal(English, answer.Target);
        Assert.Equal("DE", answer.Source);
        Assert.Equal(["DE", English], engine.Asked);

        // Named rather than automatic: left to guess again, Google reads 「chat」 under 法文 ⇄ 英文
        // as English and hands it back a second time.
        Assert.Equal([null, "DE"], engine.Sources);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("DE", "DE")]
    [InlineData("EN-US", "EN")]
    [InlineData("ZH-HANS", "ZH")]
    [InlineData("ZH-HANT", "ZH-HANT")]
    public void TheSourceIsSentAsASourceCode(string? language, string expected)
    {
        Assert.Equal(expected, BilingualTranslation.SourceCode(language), ignoreCase: true);
    }

    // A name translates to itself everywhere. One more request than before, then it stops.
    [Fact]
    public async Task ATextThatIsItselfEverywhereStopsAfterBothLanguages()
    {
        var engine = new ScriptedEngine("en", ("DE", "OK"), (English, "OK"));

        var answer = await Run("OK", "DE", English, engine);

        Assert.Equal("OK", TextOf(answer));
        Assert.Equal("DE", answer.Target);
        Assert.Equal(["DE", English], engine.Asked);
    }

    // Misread, corrected into the language it is in, and echoed both ways: every language is asked
    // for once, never twice.
    [Fact]
    public async Task NoLanguageIsAskedForTwice()
    {
        var engine = new ScriptedEngine("mk", (English, "вода"), ("RU", "вода"));

        var answer = await Run("вода", "RU", English, engine);

        Assert.Equal(English, answer.Target);
        Assert.Equal([English, "RU"], engine.Asked);
    }
}
