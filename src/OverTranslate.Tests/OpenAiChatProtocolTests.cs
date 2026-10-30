using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// What OpenAiChatTranslator puts on the wire and how it reads the answer, against canned answers.
// The settings and the words a failure reaches the user in are the application's — see
// OpenAiCompatibleProviderTests. The answers below are the shapes the models were measured giving.
public class OpenAiChatProtocolTests
{
    private static readonly Uri Endpoint = new("http://localhost:1234/v1/chat/completions");

    private static readonly OpenAiPrompt Prompt = Default(OpenAiPromptLanguage.TraditionalChinese, "繁體中文");

    /// <summary>The application's template in <paramref name="language"/>, filled in as the application fills it.</summary>
    private static OpenAiPrompt Default(OpenAiPromptLanguage language, string target) => new(
        language,
        OpenAiPrompt.DefaultTemplate(language).Replace(OpenAiPrompt.TargetNamePlaceholder, target),
        target,
        IsDefault: true);

    /// <summary>A template of the user's own.</summary>
    private static OpenAiPrompt Own(string template) => Prompt with { Template = template, IsDefault = false };

    private static List<OpenAiScreenLine> Screen(IEnumerable<string> texts) =>
        texts.Select(text => new OpenAiScreenLine(text)).ToList();

    private static OpenAiChatRequest Request(
        string apiKey = "", double? temperature = null, double? topP = null, int? seed = null,
        string? reasoningEffort = null) =>
        new(Endpoint, "test-model", apiKey, Prompt, temperature, topP, seed, reasoningEffort);

    private static List<string> Blocks(int count, string prefix = "block-") =>
        Enumerable.Range(0, count).Select(index => $"{prefix}{index:D2}").ToList();

    // ---- Endpoint ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/chat/completions")]
    [InlineData("https://example.test/custom/chat/completions", "https://example.test/custom/chat/completions")]
    public void Endpoint_AcceptsBaseOrFullChatCompletionsUrl(string input, string expected)
    {
        Assert.Equal(expected, OpenAiChatEndpoint.Resolve(input)!.AbsoluteUri);
    }

    [Theory]
    [InlineData("localhost:1234")]
    [InlineData("ftp://example.test/v1")]
    [InlineData("")]
    public void Endpoint_RejectsInvalidUrl(string input)
    {
        Assert.Null(OpenAiChatEndpoint.Resolve(input));
    }

    // ---- The instruction --------------------------------------------------------------------

    /// <summary>
    /// The Traditional Chinese template is the measured one, word for word, and so is what its
    /// context becomes: the translations done before under their heading, each pair two lines
    /// labelled with its languages, a blank line between pairs, each side on one line.
    /// </summary>
    /// <remarks>
    /// Written out whole rather than assembled from the same strings: a reworded line is a new
    /// variable on a measured path, and this is what has to notice it.
    /// </remarks>
    [Fact]
    public void Prompt_TraditionalChineseIsTheMeasuredWording()
    {
        Assert.Equal(
            "以下是之前的翻譯，翻譯時可參考：\n" +
            "日文：前の 台詞\n" +
            "繁體中文：之前的台詞\n\n" +
            "日文：おはよう\n" +
            "繁體中文：早安\n\n" +
            "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：\n" +
            "また\n明日",
            (Prompt with { SourceLabel = "日文" }).Build("また\r\n明日", [new("前の\n台詞", "之前的台詞"), new("おはよう", "早安")]));
    }

    /// <summary>A source language left to be detected labels the source lines 原文, or Source in English.</summary>
    [Fact]
    public void Prompt_ADetectedSourceIsLabelledAsTheSource()
    {
        Assert.StartsWith("以下是之前的翻譯，翻譯時可參考：\n原文：a\n繁體中文：A\n\n", Prompt.Build("x", [new("a", "A")]));
        Assert.StartsWith(
            "Earlier translations, for reference while translating:\nSource: a\nJapanese: A\n\n",
            Default(OpenAiPromptLanguage.English, "Japanese").Build("x", [new("a", "A")]));
    }

    /// <summary>
    /// A pair that reads like the context's own lines — a label of its own, a speaker's name, tags,
    /// braces, quotes, a backslash — goes in as it is, after the labels and on its line: nothing is
    /// escaped or taken off. Measured, escaping into another format did no better.
    /// </summary>
    [Fact]
    public void Prompt_APairLikeTheLabelsGoesInAsItIs()
    {
        var message = (Prompt with { SourceLabel = "日文" }).Build("x",
        [
            new("日文：", "日文："),
            new("アリス：おはよう", "繁體中文：早安"),
            new("<s>タグ</s> {name} \"走れ\" C:\\Users", "<t>標籤</t> {name} \"快跑\" C:\\Users"),
        ]);

        Assert.StartsWith(
            "以下是之前的翻譯，翻譯時可參考：\n" +
            "日文：日文：\n" +
            "繁體中文：日文：\n\n" +
            "日文：アリス：おはよう\n" +
            "繁體中文：繁體中文：早安\n\n" +
            "日文：<s>タグ</s> {name} \"走れ\" C:\\Users\n" +
            "繁體中文：<t>標籤</t> {name} \"快跑\" C:\\Users\n\n" +
            "將以下文本翻譯為",
            message);
    }

    /// <summary>
    /// No context to give: <c>{context}</c> goes with the blank lines around it, leaving the
    /// instruction over the text.
    /// </summary>
    [Fact]
    public void Prompt_AloneIsTheInstructionAndTheText()
    {
        const string expected = "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：\nおはよう";

        Assert.Equal(expected, Prompt.Build("おはよう"));
        Assert.Equal(expected, Prompt.Build("おはよう", []));
    }

    /// <summary>
    /// An empty <c>{context}</c> leaves no run of three line breaks wherever it sat, and takes nothing
    /// else with it: whatever words follow it stay as they were written.
    /// </summary>
    [Theory]
    [InlineData("A\n\n{context}\n\n{text}", "A\n\nt")]
    [InlineData("A\n{context}\nB {text}", "A\nB t")]
    [InlineData("A\n\n\n{context}\n{text}", "A\n\nt")]
    [InlineData("{context}\n\nA {text}", "A t")]
    [InlineData("A {text}\n\n{CONTEXT}", "A t")]
    [InlineData("A{context}B{text}", "ABt")]
    [InlineData("{context}\n\n參考上面的上下文，翻成中文：{text}", "參考上面的上下文，翻成中文：t")]
    [InlineData("A\n\n{context}\n\n參考上面的上下文，B\n{text}", "A\n\n參考上面的上下文，B\nt")]
    [InlineData("{context}\n\nusing the context above, translate: {text}", "using the context above, translate: t")]
    public void Prompt_AnEmptyContextTakesItsBlankLinesWithIt(string template, string expected)
    {
        Assert.Equal(expected, Own(template).Build("t"));
    }

    /// <summary>
    /// With no <c>{text}</c> the text is written straight after the template, as the user prompts it
    /// replaces did: no heading, no line break added.
    /// </summary>
    [Fact]
    public void Prompt_WithoutAPlaceForTheTextAppendsItAsItIs()
    {
        Assert.Equal("翻成中文：\n\nline one\nline two", Own("翻成中文：\n\n").Build("line one\r\nline two"));
        Assert.Equal("翻成中文：hello", Own("翻成中文：").Build("hello", [new("前", "之前")]));
    }

    /// <summary>
    /// A template with no <c>{context}</c> carries none, whatever the call knows; the other
    /// placeholders and braces of the user's are left as they wrote them.
    /// </summary>
    [Fact]
    public void Prompt_WithoutAPlaceForTheContextCarriesNone()
    {
        var prompt = Own("Translate {source_code} {x}: {text}");

        Assert.False(prompt.HasContext);
        Assert.Equal(
            "Translate {source_code} {x}: t",
            prompt.Build("t", [new("前", "之前"), new("a", "A")]));
        Assert.True(Prompt.HasContext);
    }

    /// <summary>
    /// What the text or the context say is never read as a placeholder: a translation done before
    /// that reads <c>{text}</c> stays as it was.
    /// </summary>
    [Fact]
    public void Prompt_FillsThePlaceholdersOnce()
    {
        var message = Prompt.Build("{context}", [new("{text}", "{context}")]);

        Assert.Contains("原文：{text}\n繁體中文：{context}\n\n", message);
        Assert.EndsWith("不要解釋：\n{context}", message);
    }

    [Theory]
    [InlineData(OpenAiPromptLanguage.English, "Traditional Chinese",
        "Earlier translations, for reference while translating:\nSource: a\nTraditional Chinese: A\n\n" +
        "Translate the following text into Traditional Chinese. Output only the translation, with no tags and no explanation:\nx",
        "Translate the following text into Traditional Chinese. Output only the translation, with no tags and no explanation:\nx")]
    [InlineData(OpenAiPromptLanguage.SimplifiedChinese, "简体中文",
        "以下是之前的翻译，翻译时可参考：\n原文：a\n简体中文：A\n\n" +
        "将以下文本翻译为简体中文，只输出译文，不加标签，不要解释：\nx",
        "将以下文本翻译为简体中文，只输出译文，不加标签，不要解释：\nx")]
    public void Prompt_TheOtherWordingsSayTheSame(OpenAiPromptLanguage language, string target, string expected, string alone)
    {
        Assert.Equal(expected, Default(language, target).Build("x", [new("a", "A")]));
        Assert.Equal(alone, Default(language, target).Build("x"));
    }

    /// <summary>The labels name the languages as the prompt does.</summary>
    [Fact]
    public void Prompt_TheLabelsNameTheLanguages()
    {
        Assert.StartsWith(
            "Earlier translations, for reference while translating:\nKorean: a\nJapanese: A",
            (Default(OpenAiPromptLanguage.English, "Japanese") with { SourceLabel = "Korean" }).Build("x", [new("a", "A")]));
    }

    /// <summary>One user message and no system message, whatever the instruction.</summary>
    [Fact]
    public void BuildMessages_SendsOneUserMessage()
    {
        var message = Assert.Single(OpenAiChatTranslator.BuildMessages("整段指示"));

        Assert.Equal("user", message.GetType().GetProperty("role")!.GetValue(message));
        Assert.Equal("整段指示", message.GetType().GetProperty("content")!.GetValue(message));
    }

    // ---- Reading an answer ------------------------------------------------------------------

    [Theory]
    [InlineData("甲乙丙", "甲乙丙")]
    [InlineData("<think>想一下</think>\n第一段。\n\n第二段。", "第一段。\n\n第二段。")]
    [InlineData("<t id=\"1\">甲</t>", "甲")]
    [InlineData("<t id=\"1\">あ</t>\n<t id=\"1\">甲</t>", "甲")]
    [InlineData("<t id=\"1>甲\n多的", "甲")]
    [InlineData("<t id=\"1\">第一段\n第二段</t>", "第一段\n第二段")]
    [InlineData("<t>甲</t>", "甲")]
    [InlineData("<t>第一段\n第二段</t>", "第一段\n第二段")]
    [InlineData("<t>甲</t><t>乙</t>", "<t>甲</t><t>乙</t>")]
    [InlineData("```\n甲\n```", "甲")]
    [InlineData("<think><t id=\"1\">錯</t></think>```xml\n<t id=\"1\">甲</t>\n```", "甲")]
    [InlineData("\"你好\"", "你好")]
    [InlineData("「你好」", "你好")]
    [InlineData("「甲」和「乙」", "「甲」和「乙」")]
    public void Read_TakesOffWhatTheModelAdded(string answer, string expected)
    {
        Assert.Equal(expected, OpenAiAnswer.Read(answer, "hello"));
    }

    /// <summary>
    /// Quotation marks the source had are the translation's too — also a title the source opened
    /// and left open, which the model closed.
    /// </summary>
    [Theory]
    [InlineData("「你好」", "「こんにちは」")]
    [InlineData("「アイライク ユアライク」", "「アイライク ユアライク")]
    public void Read_KeepsQuotesTheSourceHad(string answer, string source)
    {
        Assert.Equal(answer, OpenAiAnswer.Read(answer, source));
    }

    /// <summary>Tags, but none numbered 1: whatever it is, it is not the answer.</summary>
    [Theory]
    [InlineData("<t id=\"2\">別的</t>")]
    [InlineData("<t id=\"1\"></t>")]
    public void Read_FindsNoTranslationInTheWrongTag(string answer)
    {
        Assert.Null(OpenAiAnswer.Read(answer, "hello"));
    }

    // ---- How long a request may be -------------------------------------------------------------

    private static bool Fits(string message, string text, int context) =>
        message.Length + 32 + 2 * text.Length <= context;

    /// <summary>Within budget, every translation goes as it is.</summary>
    [Fact]
    public void Message_KeepsEverythingThatFits()
    {
        var earlier = Blocks(3).Select(text => new OpenAiTranslationPair(text, "譯" + text)).ToList();

        Assert.Equal(Prompt.Build("x y", earlier), OpenAiChatTranslator.Message(Request(), "x y", earlier));
    }

    /// <summary>Over budget, the oldest translations go first: what is left came nearest the text.</summary>
    [Fact]
    public void Message_CutsTheOldestFirst()
    {
        var earlier = Enumerable.Range(0, 20)
            .Select(i => new OpenAiTranslationPair($"line-{i:D2}-" + new string('字', 40), "譯")).ToList();
        const string text = "x y";
        var whole = Prompt.Build(text, earlier);

        var cut = OpenAiChatTranslator.Message(Request() with { ContextTokens = whole.Length - 300 }, text, earlier);

        Assert.True(Fits(cut, text, whole.Length - 300));
        Assert.DoesNotContain("line-00-", cut);
        Assert.Contains("line-19-", cut);
        Assert.Equal(Prompt.Build(text, earlier.SkipWhile(pair => !cut.Contains(pair.Source)).ToList()), cut);
        Assert.EndsWith("不要解釋：\n" + text, cut);
    }

    /// <summary>A text too long for the budget on its own is still sent, with nothing else in it.</summary>
    [Fact]
    public void Message_SendsATooLongTextWithNothingElse()
    {
        var text = new string('長', 5000);

        var message = OpenAiChatTranslator.Message(Request(), text, [new("前", "之前"), new("短", "短的")]);

        Assert.Equal(Prompt.Build(text), message);
    }

    /// <summary>
    /// What a request carries: the history, then the lines on screen already translated, then the
    /// round's — a source there already only the first time, a line with no letter not at all, and a
    /// line not translated yet never.
    /// </summary>
    [Fact]
    public void Earlier_IsTheHistoryThenTheScreenThenTheRound()
    {
        var earlier = OpenAiChatTranslator.Earlier(
            [new("h", "H"), new("a", "甲")],
            [new("b"), new("1200", "1200"), new("a", "甲"), new("c", "丙"), new("d")],
            [new("b", "乙"), new("c", "又丙")]);

        Assert.Equal([new("h", "H"), new("a", "甲"), new("c", "丙"), new("b", "乙")], earlier);
    }

    // ---- Requests ---------------------------------------------------------------------------

    /// <summary>Each text is a request of its own, one after another, not streamed.</summary>
    [Fact]
    public async Task TranslateAsync_SendsEachTextAloneOneAfterAnother()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var texts = Blocks(6);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        Assert.Equal(texts, server.Requests.Select(request => request.Text));
        Assert.Equal(1, server.MaxConcurrentRequests);
        Assert.All(server.Requests, request => Assert.False(request.Streamed));
        Assert.Equal(texts.Select(text => $"translated:{text}"), outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// Each request carries the history, the lines on screen already translated and the round's
    /// translations before it, and is the one before it with one more translation on the end: a
    /// server keeping the start of the last request keeps all but the text.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_CarriesWhatIsTranslatedAndOnlyAppends()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var texts = Blocks(4);
        List<OpenAiScreenLine> screen = [new("旁", "旁邊"), .. Screen(texts)];
        var context = new OpenAiChatContext([new("前", "之前")], screen);

        await new OpenAiChatTranslator(http).TranslateAsync(texts, Request(), context);

        var messages = server.Requests.Select(request => request.Message).ToList();
        for (var k = 0; k < texts.Count; k++)
        {
            List<OpenAiTranslationPair> earlier =
                [new("前", "之前"), new("旁", "旁邊"), .. texts.Take(k).Select(text => new OpenAiTranslationPair(text, $"translated:{text}"))];
            Assert.Equal(Prompt.Build(texts[k], earlier), messages[k]);
        }

        for (var k = 1; k < texts.Count; k++)
        {
            var last = messages[k - 1];
            Assert.StartsWith(last[..last.LastIndexOf("\n\n將以下文本", StringComparison.Ordinal)], messages[k]);
        }
    }

    /// <summary>
    /// The text itself and the lines still to come are never context: the first text of a screen
    /// with nothing translated goes alone.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_NeverCarriesWhatIsNotTranslatedYet()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);

        await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "c d"], Request(), new OpenAiChatContext(Screen: Screen(["a b", "c d", "e f"])));

        Assert.Equal(Prompt.Build("a b"), server.Requests.First().Message);
        Assert.All(server.Requests, request => Assert.DoesNotContain("e f", request.Message));
    }

    /// <summary>
    /// A text with no letter is its own translation: not sent, not in the round, not in what is
    /// done so far.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_DoesNotSendATextWithNoLetters()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        List<string> texts = ["1200", "a b", "2026/10/27 ★ 12:30", "c d"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        Assert.Equal(["a b", "c d"], server.Requests.Select(request => request.Text));
        Assert.Equal(["1200", "translated:a b", "2026/10/27 ★ 12:30", "translated:c d"], outcomes.Select(outcome => outcome.Translation));
        Assert.All(server.Requests, request => Assert.DoesNotContain("1200", request.Message));
        Assert.Equal(Prompt.Build("c d", [new("a b", "translated:a b")]), server.Requests.Last().Message);
    }

    [Fact]
    public async Task TranslateAsync_SendsNothingWhenNoTextHasLetters()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["1", "+2"], Request());

        Assert.Equal(0, server.Sent);
        Assert.Equal(["1", "+2"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>Letters of every script count: kana, Han, Hangul, Cyrillic.</summary>
    [Theory]
    [InlineData("ー々", true)]
    [InlineData("한", true)]
    [InlineData("Я", true)]
    [InlineData("𠮷", true)]
    [InlineData("123 / 456 … ★ ¥", false)]
    public void HasLetters_CountsEveryScript(string text, bool letters)
    {
        Assert.Equal(letters, OpenAiChatTranslator.HasLetters(text));
    }

    /// <summary>
    /// An answer is taken whatever it says: a text given back as it was, kana and all, is not sent
    /// again.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_TakesACopyAsItIs()
    {
        var server = new OpenAiFakeServer { Answer = text => text.StartsWith("ああ") ? text : $"譯:{text}" };
        using var http = new HttpClient(server);
        List<string> texts = ["a b c", "ああ次の探索の準備か", "d e f"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            texts, Request(), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(3, server.Sent);
        Assert.Equal(["譯:a b c", "ああ次の探索の準備か", "譯:d e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// An empty answer, or a failure the text could have caused, is sent again once, alone with no
    /// context; when that fails too the text has the first failure.
    /// </summary>
    [Theory]
    [InlineData("", "譯:b c", "譯:b c")]
    [InlineData("", "", null)]
    [InlineData("<think>只想不答</think>", "譯:b c", "譯:b c")]
    public async Task TranslateAsync_SendsAnEmptyAnswerAgainAloneOnce(string first, string second, string? expected)
    {
        var asked = 0;
        var server = new OpenAiFakeServer { Answer = text => text == "b c" ? (++asked == 1 ? first : second) : null };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "b c", "d e"], Request(), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(4, server.Sent);
        Assert.Equal(Prompt.Build("b c"), server.Requests.ElementAt(2).Message);
        Assert.Equal(expected, outcomes[1].Translation);
        if (expected is null)
            Assert.Equal(OpenAiChatFailure.NoTranslation, Assert.IsType<OpenAiChatException>(outcomes[1].Error).Failure);
        Assert.Equal("translated:d e", outcomes[2].Translation);
    }

    /// <summary>A text that already went alone is not sent the same again.</summary>
    [Fact]
    public async Task TranslateAsync_DoesNotSendTheSameAloneAgain()
    {
        var server = new OpenAiFakeServer { Answer = _ => "" };
        using var http = new HttpClient(server);

        await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["a b"], Request()));

        Assert.Equal(1, server.Sent);
    }

    /// <summary>An answer to a lone text with no tag at all is the translation.</summary>
    [Fact]
    public async Task TranslateAsync_TakesAnUntaggedAnswer()
    {
        var server = new OpenAiFakeServer { Answer = _ => "<think>hidden</think>直接的譯文" };
        using var http = new HttpClient(server);

        var outcome = Assert.Single(await new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.False(Assert.Single(server.Requests).Streamed);
        Assert.Equal("直接的譯文", outcome.Translation);
    }

    /// <summary>A lone text of two paragraphs goes out as two and comes back as two.</summary>
    [Fact]
    public async Task TranslateAsync_KeepsTheParagraphsOfALoneText()
    {
        var server = new OpenAiFakeServer { Answer = _ => "第一段。\n\n第二段。" };
        using var http = new HttpClient(server);

        var outcome = Assert.Single(await new OpenAiChatTranslator(http).TranslateAsync(["First.\r\n\r\nSecond."], Request()));

        Assert.EndsWith("不要解釋：\nFirst.\n\nSecond.", Assert.Single(server.Requests).Message);
        Assert.Equal("第一段。\n\n第二段。", outcome.Translation);
    }

    /// <summary>A lone text answered under the wrong id is no translation.</summary>
    [Fact]
    public async Task TranslateAsync_RefusesAnAnswerUnderTheWrongId()
    {
        using var http = new HttpClient(new OpenAiFakeServer { Answer = _ => "<t id=\"2\">別的</t>" });

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.Equal(OpenAiChatFailure.NoTranslation, error.Failure);
    }

    /// <summary>
    /// A template with no <c>{context}</c>: each text alone, up to eight at a time, nothing of the
    /// context in any of them.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_WithoutContextSendsUpToEightAtATime()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var texts = Blocks(23);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            texts, Request() with { Prompt = Own("翻成中文：\n\n") }, new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(23, server.Sent);
        Assert.Equal(8, server.MaxConcurrentRequests);
        Assert.Equal(texts.Select(text => "翻成中文：\n\n" + text).Order(), server.Requests.Select(request => request.Message).Order());
        Assert.Equal(texts.Select(text => $"translated:翻成中文：\n\n{text}"), outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// The user's own template with <c>{context}</c>: one after another with the context, the
    /// answer taken as it came.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_TheUsersOwnTemplateTakesTheAnswerAsItCame()
    {
        var server = new OpenAiFakeServer { Answer = text => text };
        using var http = new HttpClient(server);
        var prompt = Own("{context}\n\n只輸出譯文，不加標籤，不要解釋：\n{text}");
        List<string> texts = ["ああ次の探索の準備か", "e"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request() with { Prompt = prompt });

        Assert.Equal(2, server.Sent);
        Assert.Equal(1, server.MaxConcurrentRequests);
        Assert.Contains("翻譯時可參考：\n原文：ああ次の探索の準備か\n", server.Requests.Last().Message);
        Assert.Equal(texts, outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>An answer with the context's tags in it is taken as it came, not sent again.</summary>
    [Fact]
    public async Task TranslateAsync_TakesAnAnswerWithTheContextsTags()
    {
        var server = new OpenAiFakeServer { Answer = text => text == "e" ? "<s>e</s><t>e</t>" : $"譯:{text}" };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b c", "e"], Request());

        Assert.Equal(2, server.Sent);
        Assert.Equal(["譯:a b c", "<s>e</s><t>e</t>"], outcomes.Select(outcome => outcome.Translation));
    }

    [Fact]
    public async Task TranslateAsync_SendsTheKeyAsABearerTokenAndNothingWhenItIsBlank()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var chat = new OpenAiChatTranslator(http);

        await chat.TranslateAsync(["hello"], Request(apiKey: " secret-key "));
        await chat.TranslateAsync(["hello"], Request(apiKey: "  "));

        Assert.Equal(new string?[] { "Bearer secret-key", null }, server.Requests.Select(request => request.Authorization));
    }

    /// <summary>
    /// Each parameter is sent under the name the API gives it, and only when it has a value.
    /// </summary>
    /// <remarks>
    /// <c>top_p</c> and <c>reasoning_effort</c> in particular are easy to write the C# way, and a
    /// server that does not recognise a field ignores it — the symptom is not an error but a setting
    /// that silently does nothing.
    /// </remarks>
    [Fact]
    public async Task TranslateAsync_SendsEachParameterOnlyWhenItHasAValue()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);
        var chat = new OpenAiChatTranslator(http);

        await chat.TranslateAsync(["hello"], Request(temperature: 0.2, topP: 0.6, seed: 42, reasoningEffort: "none"));
        var sent = Assert.Single(server.Requests).Payload;
        Assert.Equal("test-model", sent.GetProperty("model").GetString());
        Assert.Equal(0.2, sent.GetProperty("temperature").GetDouble());
        Assert.Equal(0.6, sent.GetProperty("top_p").GetDouble());
        Assert.Equal(42, sent.GetProperty("seed").GetInt32());
        Assert.Equal("none", sent.GetProperty("reasoning_effort").GetString());

        server.Requests.Clear();
        await chat.TranslateAsync(["hello"], Request());

        // Nothing else went with them: the request is still a model, its messages and the stream flag.
        var none = Assert.Single(server.Requests).Payload;
        Assert.Equal(new[] { "model", "messages", "stream" }, none.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TranslateAsync_ReadsTextContentPartsFromCompatibleServers()
    {
        const string response =
            """{"choices":[{"message":{"content":[{"type":"text","text":"<t id=\"1\">陣列格式譯文</t>"}]}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));

        var translated = await new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request());

        Assert.Equal("陣列格式譯文", Assert.Single(translated).Translation);
    }

    [Theory]
    [InlineData("<think>internal reasoning</think>\n正確譯文", "正確譯文")]
    [InlineData("<THINK mode=\"deep\">hidden</THINK>Visible", "Visible")]
    [InlineData("保留正常的譯文", "保留正常的譯文")]
    public void StripThinking_RemovesCommonThinkingBlocks(string response, string expected)
    {
        Assert.Equal(expected, OpenAiChatTranslator.StripThinking(response));
    }

    /// <summary>
    /// A chat template that writes the opening tag itself leaves the model's answer starting inside
    /// the thinking: everything up to the first closing tag is thinking, not translation.
    /// </summary>
    [Theory]
    [InlineData("推理過程\n</think>\n\n正確譯文", "正確譯文")]
    [InlineData("hidden</THINK >Visible", "Visible")]
    public void StripThinking_DropsThinkingWhoseOpeningTagTheTemplateWrote(string response, string expected)
    {
        Assert.Equal(expected, OpenAiChatTranslator.StripThinking(response));
    }

    /// <summary>
    /// Thinking cut off by the token limit never closes, and none of it is translation.
    /// </summary>
    [Theory]
    [InlineData("<think>還沒想完就被截斷")]
    [InlineData("\n<think mode=\"deep\">still thinking")]
    [InlineData("<think>first</think>\n<think>second, cut off")]
    public void StripThinking_LeavesNothingOfThinkingThatNeverClosed(string response)
    {
        Assert.Equal("", OpenAiChatTranslator.StripThinking(response));
    }

    /// <summary>
    /// Ordinary text that merely mentions a tag in passing is left alone: only an unclosed tag the
    /// answer opens with is thinking.
    /// </summary>
    [Fact]
    public void StripThinking_LeavesATranslationThatOnlyMentionsAnOpeningTag()
    {
        Assert.Equal("請輸入 <think> 標籤", OpenAiChatTranslator.StripThinking("請輸入 <think> 標籤"));
    }

    /// <summary>
    /// Servers that parse the reasoning out put it in a field of its own; only the content is the
    /// translation.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_IgnoresReasoningInItsOwnField()
    {
        const string response =
            """{"choices":[{"message":{"reasoning_content":"先想一想","content":"<t id=\"1\">乾淨譯文</t>"}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));

        var translated = await new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request());

        Assert.Equal("乾淨譯文", Assert.Single(translated).Translation);
    }

    /// <summary>
    /// Reasoning and no content — the model thought until it ran out of tokens — is no translation,
    /// never the reasoning passed off as one.
    /// </summary>
    [Theory]
    [InlineData("""{"choices":[{"message":{"reasoning_content":"想到一半","content":""}}]}""")]
    [InlineData("""{"choices":[{"message":{"reasoning_content":"想到一半","content":null}}]}""")]
    public async Task OnlyReasoningAndNoContentIsNoTranslation(string body)
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.Equal(OpenAiChatFailure.NoTranslation, error.Failure);
    }

    // ---- Failures ---------------------------------------------------------------------------

    /// <summary>
    /// A refusal carries the status and the server's own words, apart, for the application to word.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers: the application says "unknown error" for the first and
    /// "no error content" for the second, as it did before the request moved here.
    /// </remarks>
    [Theory]
    [InlineData("""{"error":{"message":"model not found"}}""", "model not found")]
    [InlineData("""{"error":{"message":null}}""", null)]
    [InlineData("  ", "")]
    [InlineData("Bad Gateway", "Bad Gateway")]
    public async Task Refusal_CarriesTheStatusAndTheServersWords(string body, string? expected)
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadRequest, body));

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.Equal(OpenAiChatFailure.Rejected, error.Failure);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(expected, error.ServerMessage);
    }

    [Fact]
    public async Task Refusal_CutsALongBodyAtThreeHundredCharacters()
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadGateway, new string('x', 500)));

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.Equal(new string('x', 300) + "…", error.ServerMessage);
    }

    [Theory]
    [InlineData("""not json""", OpenAiChatFailure.Unparsable)]
    [InlineData("""{"choices":[]}""", OpenAiChatFailure.Unparsable)]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""", OpenAiChatFailure.NoTranslation)]
    [InlineData("""{"choices":[{"message":{"content":"<think>只想不答</think>"}}]}""", OpenAiChatFailure.NoTranslation)]
    public async Task AnAnswerWithNoTranslationSaysWhichWay(string body, OpenAiChatFailure failure)
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request()));

        Assert.Equal(failure, error.Failure);
    }

    /// <summary>
    /// Every text failing still comes out as one failure rather than as an aggregate that says one
    /// or more errors occurred.
    /// </summary>
    [Fact]
    public async Task ABadAnswerToEveryRequestStillComesOutAsItself()
    {
        using var http = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.OK, """{"choices":[{"message":{"content":""}}]}"""));

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(Blocks(12), Request()));

        Assert.Equal(OpenAiChatFailure.NoTranslation, Assert.IsType<OpenAiChatException>(error).Failure);
    }

    /// <summary>
    /// One text the server keeps refusing — sent again alone, refused again — leaves the others
    /// translated, and says which one failed and how.
    /// </summary>
    [Fact]
    public async Task OneFailedTextLeavesTheOthersTranslated()
    {
        var server = new OpenAiFakeServer { FailOn = "block-03" };
        using var http = new HttpClient(server);
        var texts = Blocks(6);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        for (var i = 0; i < texts.Count; i++)
        {
            if (i == 3) continue;
            Assert.Equal($"translated:{texts[i]}", outcomes[i].Translation);
            Assert.Null(outcomes[i].Error);
        }

        Assert.False(outcomes[3].Translated);
        var error = Assert.IsType<OpenAiChatException>(outcomes[3].Error);
        Assert.Equal(OpenAiChatFailure.Rejected, error.Failure);
        Assert.Equal(7, server.Sent);
        Assert.Equal(Prompt.Build("block-03"), server.Requests.ElementAt(4).Message);
        Assert.DoesNotContain("block-03", server.Requests.Last().Message);
    }

    /// <summary>
    /// A server that fails eight requests with nothing translated is not sent the rest: every
    /// further one would only wait out the same failure.
    /// </summary>
    [Fact]
    public async Task EightFailuresWithNothingTranslatedStopTheRestBeingSent()
    {
        var server = new OpenAiFakeServer { FailOn = "block" };
        using var http = new HttpClient(server);

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(Blocks(40), Request()));

        Assert.Equal(OpenAiChatFailure.Rejected, error.Failure);
        Assert.Equal(8, server.Sent);
    }

    /// <summary>
    /// The caller cancelling part way through is a cancellation, not a call with some texts
    /// translated and the rest failed.
    /// </summary>
    [Fact]
    public async Task CancellingPartWayThroughIsStillACancellation()
    {
        using var http = new HttpClient(new OpenAiFakeServer { HangOn = "block-02" });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var request = Request() with { Timeout = TimeSpan.FromMinutes(5) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(Blocks(4), request, cancellationToken: cancel.Token));
    }

    /// <summary>
    /// A request past its timeout is a failure of its own, not the cancellation it starts as:
    /// callers stay silent on a cancellation, taking it for the user having walked away.
    /// </summary>
    [Fact]
    public async Task ARequestPastItsTimeoutFailsAsTimedOut()
    {
        using var http = new HttpClient(new NeverAnsweringHandler());
        var request = Request() with { Timeout = TimeSpan.FromMilliseconds(50) };

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], request));

        Assert.Equal(OpenAiChatFailure.TimedOut, error.Failure);
    }

    /// <summary>The caller's own cancellation still comes out as a cancellation.</summary>
    [Fact]
    public async Task CancellingByTheCallerIsNotATimeout()
    {
        using var http = new HttpClient(new NeverAnsweringHandler());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = Request() with { Timeout = TimeSpan.FromMinutes(5) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], request, cancellationToken: cancel.Token));
    }

    /// <summary>
    /// No server there: the first request fails, and no text after it is sent — each would fail the
    /// same way.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(50)]
    public async Task ARefusedConnectionIsOneRequest(int count)
    {
        var handler = new RefusingHandler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(Blocks(count), Request()));

        Assert.Equal(1, handler.Sent);
    }

    /// <summary>
    /// A refusal no text could have caused stops the rest being sent; one that a text could have —
    /// a filter on what it says, a text too long — does not.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    public async Task OnlyARefusalTheTextCouldHaveCausedLetsTheRestBeSent(HttpStatusCode status, bool rest)
    {
        var handler = new StaticResponseHandler(status, """{"error":{"message":"no"}}""");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<OpenAiChatException>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(Blocks(12), Request()));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(rest ? 8 : 1, handler.Sent);
    }

    /// <summary>
    /// Part way through, a refusal no text could have caused leaves what is done translated and the
    /// rest unsent, with no error of their own, and is not sent again.
    /// </summary>
    [Fact]
    public async Task ARefusalPartWayThroughLeavesTheRestUnsent()
    {
        var handler = new RefusingAfterHandler(1, HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["ああ次の探索の準備か", "b c", "d e", "f g"], Request());

        Assert.Equal(2, handler.Sent);
        Assert.Equal("ああ次の探索の準備か", outcomes[0].Translation);
        Assert.Equal(HttpStatusCode.TooManyRequests, Assert.IsType<OpenAiChatException>(outcomes[1].Error).StatusCode);
        Assert.Same(OpenAiChatOutcome.NotSent, outcomes[2]);
        Assert.Same(OpenAiChatOutcome.NotSent, outcomes[3]);
    }

    /// <summary>Answers the first requests by copying the text, then refuses every one after.</summary>
    private sealed class RefusingAfterHandler(int answered, HttpStatusCode status) : HttpMessageHandler
    {
        private int _sent;
        public int Sent => Volatile.Read(ref _sent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sent) > answered)
                return new HttpResponseMessage(status) { Content = new StringContent("{}") };

            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var message = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            return OpenAiFakeServer.Whole(OpenAiFakeServer.TextOf(message));
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        private int _sent;
        public int Sent => Volatile.Read(ref _sent);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            throw new HttpRequestException("connection refused");
        }
    }

    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _sent;
        public int Sent => Volatile.Read(ref _sent);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
