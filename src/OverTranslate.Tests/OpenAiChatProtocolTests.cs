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
        new(Endpoint, "test-model", apiKey, Prompt, "zh-Hant", temperature, topP, seed, reasoningEffort);

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
    /// context becomes: the blocks in their order under their headings, a pair to a line in
    /// <c>&lt;s&gt;</c> and <c>&lt;t&gt;</c>, the screen as the round found it — a line there twice
    /// twice — and the screen's heading saying what the tags are when no history has.
    /// </summary>
    /// <remarks>
    /// Written out whole rather than assembled from the same strings: a reworded line is a new
    /// variable on a measured path, and this is what has to notice it.
    /// </remarks>
    [Fact]
    public void Prompt_TraditionalChineseIsTheMeasuredWording()
    {
        Assert.Equal(
            "之前的翻譯（<s> 原文，<t> 繁體中文譯文），只供參考：\n" +
            "<history>\n" +
            "<s>前の 台詞</s><t>之前的台詞</t>\n" +
            "</history>\n\n" +
            "目前畫面（沒有 <t> 的還沒翻），只供參考，不用翻譯：\n" +
            "<page>\n" +
            "<s>おはよう</s>\n" +
            "<s>画面の文</s><t>畫面上的句子</t>\n" +
            "<s>また 明日</s>\n" +
            "<s>また 明日</s>\n" +
            "</page>\n\n" +
            "這個畫面剛翻好的，只供參考：\n" +
            "<translated>\n" +
            "<s>おはよう</s><t>早安</t>\n" +
            "</translated>\n\n" +
            "參考上面，將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：\n" +
            "また\n明日",
            Prompt.Build(
                "また\r\n明日",
                history: [new("前の\n台詞", "之前的台詞")],
                screen: [new("おはよう"), new("画面の文", "畫面上的句子"), new("また\r\n明日"), new("また 明日")],
                soFar: [new("おはよう", "早安")]));

        Assert.Equal(
            "目前畫面（<s> 原文，<t> 繁體中文譯文，沒有 <t> 的還沒翻），只供參考，不用翻譯：\n" +
            "<page>\n<s>おはよう</s>\n<s>また</s>\n</page>\n\n" +
            "參考上面，將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：\nまた",
            Prompt.Build("また", screen: [new("おはよう"), new("また")]));
    }

    /// <summary>
    /// No context to give: <c>{context}</c> goes with the blank lines around it and the 參考上面，
    /// after it, leaving the instruction over the text — also when the screen is the text alone.
    /// </summary>
    [Fact]
    public void Prompt_AloneIsTheInstructionAndTheText()
    {
        const string expected = "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：\nおはよう";

        Assert.Equal(expected, Prompt.Build("おはよう"));
        Assert.Equal(expected, Prompt.Build("おはよう", screen: [new("おはよう")]));
        Assert.Equal(expected, Prompt.Build("おはよう", [], [], []));
    }

    /// <summary>An empty <c>{context}</c> leaves no run of three line breaks wherever it sat.</summary>
    [Theory]
    [InlineData("A\n\n{context}\n\n{text}", "A\n\nt")]
    [InlineData("A\n{context}\nB {text}", "A\nB t")]
    [InlineData("A\n\n\n{context}\n{text}", "A\n\nt")]
    [InlineData("{context}\n\nA {text}", "A t")]
    [InlineData("A {text}\n\n{CONTEXT}", "A t")]
    [InlineData("A{context}B{text}", "ABt")]
    [InlineData("{context}\n\n參考上面，翻成中文：{text}", "翻成中文：t")]
    [InlineData("A\n\n{context}\n\n參考上面，B\n{text}", "A\n\nB\nt")]
    [InlineData("A 參考上面，B {context}{text}", "A 參考上面，B t")]
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
        Assert.Equal("翻成中文：hello", Own("翻成中文：").Build("hello", [new("前", "之前")], Screen(["a", "hello"])));
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
            prompt.Build("t", [new("前", "之前")], Screen(["a", "t"]), [new("a", "A")]));
        Assert.True(Prompt.HasContext);
    }

    /// <summary>
    /// What the text or the context say is never read as a placeholder: a line on screen that
    /// reads <c>{text}</c> stays as it was.
    /// </summary>
    [Fact]
    public void Prompt_FillsThePlaceholdersOnce()
    {
        var message = Prompt.Build("{context}", screen: Screen(["{text}", "{context}"]));

        Assert.Contains("<s>{text}</s>\n<s>{context}</s>\n</page>", message);
        Assert.EndsWith("不要解釋：\n{context}", message);
    }

    [Theory]
    [InlineData(OpenAiPromptLanguage.English, "Traditional Chinese",
        "Just translated on this screen, for reference only:\n<translated>\n<s>a</s><t>A</t>\n</translated>\n\n" +
        "Using the above, translate the following text into Traditional Chinese. Output only the translation, with no tags and no explanation:\nx",
        "Translate the following text into Traditional Chinese. Output only the translation, with no tags and no explanation:\nx")]
    [InlineData(OpenAiPromptLanguage.SimplifiedChinese, "简体中文",
        "这个画面刚翻好的，仅供参考：\n<translated>\n<s>a</s><t>A</t>\n</translated>\n\n" +
        "参考上面，将以下文本翻译为简体中文，只输出译文，不加标签，不要解释：\nx",
        "将以下文本翻译为简体中文，只输出译文，不加标签，不要解释：\nx")]
    public void Prompt_TheOtherWordingsSayTheSame(OpenAiPromptLanguage language, string target, string expected, string alone)
    {
        Assert.Equal(expected, Default(language, target).Build("x", soFar: [new("a", "A")]));
        Assert.Equal(alone, Default(language, target).Build("x"));
    }

    /// <summary>The context's headings name the target language as the prompt does.</summary>
    [Fact]
    public void Prompt_TheHeadingsNameTheTarget()
    {
        Assert.StartsWith(
            "Earlier translations (<s> source, <t> Japanese translation), for reference only:",
            Default(OpenAiPromptLanguage.English, "Japanese").Build("x", [new("a", "A")]));
    }

    // ---- The context's tags in the answer -----------------------------------------------------

    /// <summary>The shapes measured: a tag the context gives come back with the translation.</summary>
    [Theory]
    [InlineData("e", "<s>e</s><t>e</t>")]
    [InlineData("[全城性] a: usa withdrew", "[全城性] a: 美國撤軍了</s>")]
    [InlineData("<page>要素は使えません", "<page>無法使用該元素</page>")]
    [InlineData("a", "<page>\n<s>a</s>")]
    [InlineData("b", "乙\n</translated>")]
    public void LeaksTags_CatchesTheContextsTags(string source, string answer)
    {
        Assert.True(OpenAiPrompt.LeaksTags(source, answer));
    }

    /// <summary>A translation, and the tags the source itself had, are not a leak.</summary>
    [Theory]
    [InlineData("TAKE ITEM", "拿取物品")]
    [InlineData("<s>タグ</s>を入力してください", "請輸入<s>標籤</s>")]
    [InlineData("x < y かつ y > z", "x < y 且 y > z")]
    [InlineData("<b>太字</b>", "<b>粗體</b>")]
    public void LeaksTags_PassesATranslation(string source, string answer)
    {
        Assert.False(OpenAiPrompt.LeaksTags(source, answer));
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

    // ---- Is it a translation ----------------------------------------------------------------

    [Theory]
    [InlineData("ああ次の探索の準備か", "啊，是為下次的探索做準備嗎")]
    [InlineData("Where did you put the key?", "你把鑰匙放哪了？")]
    public void Content_PassesATranslation(string source, string translation)
    {
        Assert.Null(OpenAiContentCheck.Failure(source, translation, "zh-Hant"));
    }

    [Theory]
    [InlineData("ああ次の探索の準備か")]
    [InlineData("Do you want to change your equipment?")]
    [InlineData("[TEAM] Taz: where are you going")]
    [InlineData("这个问题我们明天再讨论吧")]
    public void Content_FailsACopy(string source)
    {
        Assert.Equal("copied", OpenAiContentCheck.Failure(source, source, "zh-Hant"));
    }

    /// <summary>
    /// What legitimately stays as it is: numbers, names, titles, acronyms, copyright lines, names
    /// in katakana, and text already in the target language.
    /// </summary>
    [Theory]
    [InlineData("1200")]
    [InlineData("HP 350/350")]
    [InlineData("gg")]
    [InlineData("Taz")]
    [InlineData("OK")]
    [InlineData("Level")]
    [InlineData("[ALL] Taz: gg")]
    [InlineData("$200")]
    [InlineData("AUTO ESKIP")]
    [InlineData("STR DEX CON INT WIS CHA")]
    [InlineData("Mar-Adetta")]
    [InlineData("The Unbidden")]
    [InlineData("MegaCrit the Ironclad")]
    [InlineData("全城性ViridianRogue:@BROTHERSZ")]
    [InlineData("[GLOBAL1 Vespy238:")]
    [InlineData("©07th Expansion/MangaGamer.com")]
    [InlineData("DIO/SQUARE ENIX All Rights Reserved.")]
    [InlineData("ベジャイア")]
    [InlineData("オブ・ムーンセ")]
    [InlineData("這家店的咖啡很好喝")]
    [InlineData("通知")]
    public void Content_PassesWhatStaysAsItIs(string source)
    {
        Assert.Null(OpenAiContentCheck.Failure(source, source, "zh-Hant"));
    }

    [Theory]
    [InlineData("どうしたんだ改まって", "怎麼了改まって", "kana")]
    [InlineData("オルン", "奧倫ン", null)]
    [InlineData("ああ次の探索の準備か", "啊，是为下次的探索做准备吗", "simplified")]
    [InlineData("なあ オルン", "哎呀，奥爾恩。", null)]
    [InlineData("오늘 저녁에 뭐 먹을까?", "今晚 뭐 먹을까?", "hangul")]
    public void Content_FailsWhatWasLeftUntranslated(string source, string translation, string? failure)
    {
        Assert.Equal(failure, OpenAiContentCheck.Failure(source, translation, "zh-Hant"));
    }

    /// <summary>
    /// A Japanese name in kanji alone shares one character with Simplified at most, and copied it is
    /// left as it is; two Simplified characters copied are still a copy.
    /// </summary>
    [Theory]
    [InlineData("凉泉桜花", null)]
    [InlineData("佐々木学子", null)]
    [InlineData("这个问题", "copied")]
    public void Content_TakesTwoSimplifiedCharactersForAHanCopy(string source, string? failure)
    {
        Assert.Equal(failure, OpenAiContentCheck.Failure(source, source, "zh-Hant"));
    }

    /// <summary>A title in katakana is one with a bracket or punctuation around it too.</summary>
    [Theory]
    [InlineData("「アイライク ユアライク")]
    [InlineData("『アイライク・ユアライク』！")]
    public void Content_PassesKatakanaInBrackets(string source)
    {
        Assert.Null(OpenAiContentCheck.Failure(source, source, "zh-Hant"));
    }

    /// <summary>
    /// A title kept in its corner brackets as the source had it is not kana left behind; kana in
    /// brackets the source did not have still is.
    /// </summary>
    [Theory]
    [InlineData("「~BanG Dream! Our Notes~だんらんTV!」 初回放送のお知らせ", "「~BanG Dream! Our Notes~だんらんTV!」首次播出的公告", null)]
    [InlineData("ー家Dumb Rock!「アイライク・ユアライク」 がデジタルリリース！", "ー家Dumb Rock!「アイライク・ユアライク」將以數位形式發行！", null)]
    [InlineData("「アイライク ユアライク", "「アイライク ユアライク」", null)]
    [InlineData("素晴らしきラジオ でも どこにもないラジオ", "「素晴らしきラジオ でも どこにもないラジオ」", "copied")]
    [InlineData("どうしたんだ", "「どうしたんだ。」", "copied")]
    [InlineData("「アイライク」がリリース", "「アイライク」がリリース了", "kana")]
    [InlineData("新曲がリリース", "新曲「リリースされた」", "kana")]
    public void Content_PassesATitleKeptInItsBrackets(string source, string translation, string? failure)
    {
        Assert.Equal(failure, OpenAiContentCheck.Failure(source, translation, "zh-Hant"));
    }

    /// <summary>
    /// The target decides what counts as left untranslated: kana is Japanese's own, Latin text is
    /// English's, and only Traditional Chinese is checked for Simplified characters.
    /// </summary>
    [Theory]
    [InlineData("どうしたんだ", "どうしたんだ", "ja")]
    [InlineData("Do you want to change your equipment?", "Do you want to change your equipment?", "en")]
    [InlineData("오늘 저녁에 뭐 먹을까?", "오늘 저녁에 뭐 먹을까?", "ko")]
    [InlineData("ああ次の探索の準備か", "啊，是为下次的探索做准备吗", "zh-Hans")]
    public void Content_LetsTheTargetKeepItsOwnScript(string source, string translation, string target)
    {
        Assert.Null(OpenAiContentCheck.Failure(source, translation, target));
    }

    // ---- How long a request may be -------------------------------------------------------------

    private static bool Fits(string message, string text, int context) =>
        message.Length + 32 + 2 * text.Length <= context;

    /// <summary>Within budget, every part goes as it is.</summary>
    [Fact]
    public void Message_KeepsEverythingThatFits()
    {
        var screen = Screen(Blocks(5));
        var soFar = Blocks(2).Select(text => new OpenAiTranslationPair(text, "譯" + text)).ToList();
        List<OpenAiTranslationPair> history = [new("前", "之前")];

        Assert.Equal(
            Prompt.Build(screen[2].Source, history, screen, soFar),
            OpenAiChatTranslator.Message(Request(), screen[2].Source, history, screen, soFar));
    }

    /// <summary>
    /// Over budget, what the round carries goes first — the screen's lines farthest from the text
    /// and the oldest of its translations — and the history only after, oldest first.
    /// </summary>
    [Fact]
    public void Message_CutsTheRoundFromFarthestThenTheHistory()
    {
        var round = Enumerable.Range(0, 20).Select(i => $"round-{i:D2}-" + new string('字', 40)).ToList();
        var screen = Screen(round);
        var soFar = round.Take(10).Select(text => new OpenAiTranslationPair(text, "譯" + text)).ToList();
        var history = Enumerable.Range(0, 6).Select(i => new OpenAiTranslationPair($"hist-{i}-" + new string('前', 40), "之前")).ToList();
        var text = round[10];
        var whole = Prompt.Build(text, history, screen, soFar);

        var cut = OpenAiChatTranslator.Message(Request() with { ContextTokens = whole.Length - 600 }, text, history, screen, soFar);
        Assert.True(Fits(cut, text, whole.Length - 600));
        Assert.Contains("<s>round-09-", cut);
        Assert.Contains("<s>round-11-", cut);
        Assert.DoesNotContain("round-19-", cut);
        Assert.Contains("<t>譯round-09-", cut);
        Assert.DoesNotContain("譯round-00-", cut);
        Assert.Contains("hist-0-", cut);
        Assert.EndsWith("不要解釋：\n" + text, cut);

        var bare = Prompt.Build(text);
        var tighter = OpenAiChatTranslator.Message(
            Request() with { ContextTokens = bare.Length + 32 + 2 * text.Length + 200 }, text, history, screen, soFar);
        Assert.DoesNotContain("round-0", tighter);
        Assert.DoesNotContain("hist-0-", tighter);
        Assert.Contains("hist-5-", tighter);
    }

    /// <summary>A text too long for the budget on its own is still sent, with nothing else in it.</summary>
    [Fact]
    public void Message_SendsATooLongTextWithNothingElse()
    {
        var text = new string('長', 5000);

        var message = OpenAiChatTranslator.Message(
            Request(), text, [new("前", "之前")], Screen(["短", text]), [new("短", "短的")]);

        Assert.Equal(Prompt.Build(text), message);
    }

    /// <summary>
    /// The screen as a request shows it: lines with a letter in their order, a line there twice
    /// twice.
    /// </summary>
    [Fact]
    public void Screen_KeepsEachLineWithALetterInOrder()
    {
        var screen = OpenAiChatTranslator.Screen(
            [new("b"), new("1200"), new("a", "甲"), new("b"), new("★"), new("c")]);

        Assert.Equal([new("b"), new("a", "甲"), new("b"), new("c")], screen);
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
    /// Each request carries the screen as the round found it and the translations before it, and
    /// is the one before it with one more translation on the end: a server keeping the start of the
    /// last request keeps all but the text.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_CarriesTheRoundAndOnlyAppends()
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
            var soFar = texts.Take(k).Select(text => new OpenAiTranslationPair(text, $"translated:{text}")).ToList();
            Assert.Equal(Prompt.Build(texts[k], context.History, screen, soFar), messages[k]);
        }

        for (var k = 1; k < texts.Count; k++)
        {
            var last = messages[k - 1];
            var end = last.LastIndexOf("\n</translated>", StringComparison.Ordinal);
            if (end < 0) end = last.LastIndexOf("\n\n參考上面，", StringComparison.Ordinal);
            Assert.StartsWith(last[..end], messages[k]);
        }
    }

    /// <summary>
    /// Without a screen from the caller, the screen is the texts themselves, untranslated, and stays
    /// so for the whole round.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_WithoutAScreenTheTextsAreTheScreen()
    {
        var server = new OpenAiFakeServer();
        using var http = new HttpClient(server);

        await new OpenAiChatTranslator(http).TranslateAsync(["a b", "c d"], Request());

        Assert.All(server.Requests, request =>
            Assert.Contains("<page>\n<s>a b</s>\n<s>c d</s>\n</page>", request.Message));
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
        Assert.Equal(Prompt.Build("c d", screen: Screen(["a b", "c d"]), soFar: [new("a b", "translated:a b")]), server.Requests.Last().Message);
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
    /// A text copied through is sent again once all are in, with no context — the same request
    /// would come back the same — and what comes back is taken whatever it says.
    /// </summary>
    [Theory]
    [InlineData("譯:再一次", "譯:再一次")]
    [InlineData(null, "ああ次の探索の準備か")]
    public async Task TranslateAsync_SendsACopiedTextAgainWithoutContextAndTakesTheAnswer(string? second, string expected)
    {
        var asked = 0;
        var server = new OpenAiFakeServer
        {
            Answer = text => text.StartsWith("ああ") ? (++asked == 1 ? text : second ?? text) : $"譯:{text}",
        };
        using var http = new HttpClient(server);
        List<string> texts = ["a b c", "ああ次の探索の準備か", "d e f"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            texts, Request(), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(4, server.Sent);
        Assert.Equal(Prompt.Build("ああ次の探索の準備か"), server.Requests.Last().Message);
        Assert.Equal(["譯:a b c", expected, "譯:d e f"], outcomes.Select(outcome => outcome.Translation));
        Assert.All(outcomes, outcome => Assert.True(outcome.Translated));
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
    /// answer taken as it came — nothing checked or sent again.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_TheUsersOwnTemplateIsNeitherCheckedNorSentAgain()
    {
        var server = new OpenAiFakeServer { Answer = text => text };
        using var http = new HttpClient(server);
        var prompt = Own("{context}\n\n只輸出譯文，不加標籤，不要解釋：\n{text}");
        List<string> texts = ["ああ次の探索の準備か", "e"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request() with { Prompt = prompt });

        Assert.Equal(2, server.Sent);
        Assert.Equal(1, server.MaxConcurrentRequests);
        Assert.Contains("<translated>\n<s>ああ次の探索の準備か</s>", server.Requests.Last().Message);
        Assert.Equal(texts, outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// Under the application's template an answer with the context's tags in it is sent again,
    /// alone, with no context.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_SendsAnAnswerWithTheContextsTagsAgain()
    {
        var asked = 0;
        var server = new OpenAiFakeServer { Answer = text => text == "e" && ++asked == 1 ? "<s>e</s><t>e</t>" : $"譯:{text}" };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b c", "e"], Request());

        Assert.Equal(3, server.Sent);
        Assert.Equal(Prompt.Build("e"), server.Requests.Last().Message);
        Assert.Equal(["譯:a b c", "譯:e"], outcomes.Select(outcome => outcome.Translation));
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
    /// One text the server keeps refusing leaves the others translated, and says which one failed
    /// and how.
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
        Assert.Equal(6, server.Sent);
        Assert.DoesNotContain("block-03 →", server.Requests.Last().Message);
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
    /// rest unsent, with no error of their own; the content check sends nothing after it.
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
