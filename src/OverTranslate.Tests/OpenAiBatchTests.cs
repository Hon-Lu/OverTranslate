using System.Net.Http;
using System.Text.RegularExpressions;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// OpenAiChatRequest.Batch: a call's texts as few requests as fit, read back by number, and what did
// not come back sent again as batches of one. One text at a time is OpenAiChatProtocolTests.
public class OpenAiBatchTests
{
    private static readonly Uri Endpoint = new("http://localhost:1234/v1/chat/completions");

    private static readonly OpenAiPrompt Prompt = new(
        OpenAiPromptLanguage.TraditionalChinese,
        OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese, batch: true)
            .Replace(OpenAiPrompt.TargetNamePlaceholder, "繁體中文"),
        "繁體中文",
        IsDefault: true);

    /// <summary>The application's template for a text at a time, sent as a batch.</summary>
    private static readonly OpenAiPrompt SegmentPrompt = Prompt with
    {
        Template = OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese)
            .Replace(OpenAiPrompt.TargetNamePlaceholder, "繁體中文"),
    };

    private const string Instruction = "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：";

    private const string BatchInstruction = "將以下文本翻譯為繁體中文，英文單字與短語也要翻譯，只輸出譯文，不加標籤，不要解釋：";

    private const string FormatLine = "(依編號輸出譯文，例如 <1>譯文</1>，一段一行)";

    private static OpenAiPrompt Own(string template) => Prompt with { Template = template, IsDefault = false };

    private static OpenAiChatRequest Request(OpenAiPrompt? prompt = null) =>
        new(Endpoint, "test-model", "", prompt ?? Prompt, Batch: true);

    private static List<OpenAiScreenLine> Screen(IEnumerable<string> texts) =>
        texts.Select(text => new OpenAiScreenLine(text)).ToList();

    private static readonly Regex Row = new(@"(?m)^<(\d+)>(.*?)</\1>$");

    /// <summary>
    /// What a model that follows the format answers a batch with: each numbered text as
    /// <paramref name="each"/> makes it, a number left out where it gives null. A request that is
    /// not a batch is answered with <paramref name="alone"/>, or <paramref name="each"/>, of the whole text.
    /// </summary>
    private static Func<string, string?> Numbered(Func<string, string?> each, Func<string, string>? alone = null) => text =>
    {
        var rows = Row.Matches(text);
        if (rows.Count == 0) return alone?.Invoke(text) ?? each(text) ?? text;

        return string.Join("\n", rows
            .Select(row => (Number: row.Groups[1].Value, Answer: each(row.Groups[2].Value)))
            .Where(row => row.Answer is not null)
            .Select(row => $"<{row.Number}>{row.Answer}</{row.Number}>"));
    };

    private static bool IsBatch(OpenAiFakeServer.Recorded request) => request.Text.Contains("<1>", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="many"/> for a batch of two or more, <paramref name="one"/> for a batch of one,
    /// given the one text's source.
    /// </summary>
    private static Func<string, string?> ManyOrOne(Func<string, string?> many, Func<string, string> one) => text =>
    {
        var rows = Row.Matches(text);
        return rows.Count == 1 ? one(rows[0].Groups[2].Value) : many(text);
    };

    // ---- Prompt ----------------------------------------------------------------------------------

    /// <summary>
    /// The texts numbered where <c>{text}</c> is, and after them, past a blank line, the batch
    /// template's own line saying how to answer, with no count in it.
    /// </summary>
    [Fact]
    public void BuildBatch_NumbersTheTextsAboveAFormatLine()
    {
        Assert.Equal(
            BatchInstruction + "\n<1>a b</1>\n<2>c d</2>\n\n" + FormatLine,
            Prompt.BuildBatch(["a b", "c\r\nd"]));
    }

    /// <summary>
    /// <c>{text}</c> is the numbered texts and nothing else: a template that says nothing of numbers
    /// — the application's for a text at a time, or the user's — gets no line added.
    /// </summary>
    [Fact]
    public void BuildBatch_AddsNoFormatLine()
    {
        Assert.Equal(Instruction + "\n<1>a b</1>\n<2>c d</2>", SegmentPrompt.BuildBatch(["a b", "c d"]));
        Assert.Equal("翻成中文：\n<1>a</1>", Own("翻成中文：\n{text}").BuildBatch(["a"]));
    }

    /// <summary>
    /// The two defaults differ in their words alone, the same placeholders in the same places: the
    /// batch one's instruction says more, and after the text comes its line about numbers.
    /// </summary>
    [Theory]
    [InlineData(OpenAiPromptLanguage.TraditionalChinese)]
    [InlineData(OpenAiPromptLanguage.SimplifiedChinese)]
    [InlineData(OpenAiPromptLanguage.English)]
    public void DefaultTemplate_TheBatchOneDiffersInWordsAlone(OpenAiPromptLanguage language)
    {
        var segment = OpenAiPrompt.DefaultTemplate(language);
        var batch = OpenAiPrompt.DefaultTemplate(language, batch: true);
        var head = segment[..segment.IndexOf("{target_name}", StringComparison.Ordinal)];

        Assert.StartsWith(head, batch);
        Assert.NotEqual(segment, batch[..batch.IndexOf("{text}", StringComparison.Ordinal)] + "{text}");
        Assert.Matches(@"\{text\}\n\n[^{}\n]*<1>[^{}\n]*$", batch);
        Assert.DoesNotContain("{count}", batch);
    }

    /// <summary>The context is the translations done before, as for one text.</summary>
    [Fact]
    public void BuildBatch_CarriesTheTranslationsDoneBefore()
    {
        var message = Prompt.BuildBatch(["a b", "c d"], [new("前", "之前")]);

        Assert.StartsWith(
            "以下是之前的翻譯，翻譯時可參考：\n原文：前\n繁體中文：之前\n\n" + BatchInstruction,
            message);
        Assert.EndsWith("<1>a b</1>\n<2>c d</2>\n\n" + FormatLine, message);
    }

    /// <summary>The user's own template works as it is: the numbered texts go where its text goes.</summary>
    [Fact]
    public void BuildBatch_FillsTheUsersOwnTemplate()
    {
        const string body = "<1>a</1>\n<2>b</2>";

        Assert.Equal("翻成中文：\n" + body + "\n完", Own("翻成中文：\n{text}\n完").BuildBatch(["a", "b"]));
        Assert.Equal("翻成中文：\n" + body, Own("翻成中文：\n").BuildBatch(["a", "b"]));
        Assert.Equal("翻成中文：\n" + body, Own("{context}\n\n翻成中文：\n{text}").BuildBatch(["a", "b"]));
    }

    [Fact]
    public void BuildBatch_TheOtherWordingsHaveAFormatLine()
    {
        var hans = new OpenAiPrompt(OpenAiPromptLanguage.SimplifiedChinese, "{text}", "简体中文");
        var english = new OpenAiPrompt(OpenAiPromptLanguage.English, "{text}", "English");

        Assert.Equal("<1>a</1>\n<2>b</2>", hans.BuildBatch(["a", "b"]));
        Assert.Equal("<1>a</1>\n<2>b</2>", english.BuildBatch(["a", "b"]));
        Assert.EndsWith("{text}\n\n(按编号输出译文，例如 <1>译文</1>，一段一行)",
            OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.SimplifiedChinese, batch: true));
        Assert.EndsWith("{text}\n\n(Output each translation by its number, for example <1>translation</1>, one per line.)",
            OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.English, batch: true));
    }

    // ---- Reading ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("<1>甲</1>\n<2>乙</2>\n<3>丙</3>", "甲|乙|丙")]
    [InlineData("<1>甲\n<2>乙\n<3>丙", "甲|乙|丙")]
    [InlineData("<1>甲<1>\n<2>乙<2>\n<3>丙<3>", "甲|乙|丙")]
    [InlineData("<think>想</think>```\n<1>甲</1><2>乙</2>\n<3>丙</3>\n```", "甲|乙|丙")]
    [InlineData("<1>甲</1>\n<3>丙</3>", "甲||丙")]
    [InlineData("<1>甲</1>\n<2></2>\n<3>丙</3>\n<4>丁</4>", "甲||丙")]
    [InlineData("<2>乙</2>\n<2>再</2>\n<1>甲</1>", "甲|乙|")]
    [InlineData("沒有編號", "||")]
    public void ReadNumbered_FindsEachNumber(string answer, string expected)
    {
        var read = OpenAiAnswer.ReadNumbered(answer, 3);

        Assert.Equal(expected.Split('|').Select(part => part.Length == 0 ? null : part), read);
    }

    /// <summary>
    /// A batch of one: its number, else the whole answer with a numbered tag around it taken off;
    /// numbers that are none of the one's are nothing.
    /// </summary>
    [Theory]
    [InlineData("<1>甲</1>", "甲")]
    [InlineData("<1>甲", "甲")]
    [InlineData("甲", "甲")]
    [InlineData("  甲\n", "甲")]
    [InlineData("<2>甲</2>", "甲")]
    [InlineData("<t>甲</t>", "甲")]
    [InlineData("<1></1>", null)]
    [InlineData("<2>甲</2>\n<3>乙</3>", null)]
    public void ReadOfOne_TakesTheWholeAnswerWithoutANumber(string answer, string? expected)
    {
        Assert.Equal(expected, OpenAiAnswer.ReadOfOne(answer, "x", isDefault: true));
    }

    // ---- Sending ---------------------------------------------------------------------------------

    /// <summary>A screen is one request, and every text comes back from it.</summary>
    [Fact]
    public async Task TranslateAsync_SendsTheTextsAsOneRequest()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}") };
        using var http = new HttpClient(server);
        List<string> texts = ["a b", "c d", "e f"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        Assert.Equal(1, server.Sent);
        Assert.Equal(Prompt.BuildBatch(texts), server.Requests.Single().Message);
        Assert.Equal(["譯:a b", "譯:c d", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>A text with no letter is its own translation and not in the batch.</summary>
    [Fact]
    public async Task TranslateAsync_BatchLeavesOutATextWithNoLetters()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}") };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b", "1200", "c d"], Request());

        Assert.Equal(Prompt.BuildBatch(["a b", "c d"]), server.Requests.Single().Message);
        Assert.Equal(["譯:a b", "1200", "譯:c d"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// The lines on screen already translated go after the history, as for one text; the lines not
    /// translated yet do not go at all.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_CarriesTheScreensTranslations()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}") };
        using var http = new HttpClient(server);
        List<string> texts = ["a b", "c d"];

        await new OpenAiChatTranslator(http).TranslateAsync(texts, Request(),
            new OpenAiChatContext([new("前", "之前")], [new("旁", "旁邊"), new("等", null), .. Screen(texts)]));

        Assert.Equal(Prompt.BuildBatch(texts, [new("前", "之前"), new("旁", "旁邊")]), server.Requests.Single().Message);
    }

    /// <summary>
    /// A number the answer left out is sent again as a batch of one with the same template, carrying
    /// the history and what the batch translated.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_SendsAMissingNumberAgainWithWhatIsTranslated()
    {
        var server = new OpenAiFakeServer
        {
            Answer = ManyOrOne(Numbered(text => text == "c d" ? null : $"譯:{text}"), text => $"<1>譯:{text}</1>"),
        };
        using var http = new HttpClient(server);
        List<string> texts = ["a b", "c d", "e f"];
        var history = new List<OpenAiTranslationPair> { new("前", "之前") };

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            texts, Request(), new OpenAiChatContext(history));

        Assert.Equal(2, server.Sent);
        Assert.Equal(Prompt.BuildBatch(["c d"], [new("前", "之前"), new("a b", "譯:a b"), new("e f", "譯:e f")]),
            server.Requests.Last().Message);
        Assert.Equal(["譯:a b", "譯:c d", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// A batch of one answered without its number is answered all the same: the whole answer is
    /// the one text's translation.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_ABatchOfOneWithNoNumberIsTheWholeAnswer()
    {
        var server = new OpenAiFakeServer
        {
            Answer = ManyOrOne(Numbered(text => text == "c d" ? null : $"譯:{text}"), text => $"譯:{text}"),
        };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b", "c d", "e f"], Request());

        Assert.Equal(2, server.Sent);
        Assert.Equal(["譯:a b", "譯:c d", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>An answer with no number at all sends every text again, as batches of one.</summary>
    [Fact]
    public async Task TranslateAsync_AnAnswerWithNoNumbersGoesAsBatchesOfOne()
    {
        var server = new OpenAiFakeServer { Answer = ManyOrOne(_ => "甲\n乙", text => $"譯:{text}") };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b", "c d"], Request());

        Assert.Equal(3, server.Sent);
        Assert.Equal(
            [Prompt.BuildBatch(["a b"]), Prompt.BuildBatch(["c d"])],
            server.Requests.Skip(1).Select(request => request.Message).Order());
        Assert.Equal(["譯:a b", "譯:c d"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// The default for a text at a time, kept when switching to a batch, says nothing of numbers: a
    /// model answers it a text to a line with none, and every text still comes back, as batches of one
    /// carrying what came before.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_TheDefaultForATextAtATimeStillTranslatesEveryText()
    {
        var server = new OpenAiFakeServer
        {
            Answer = ManyOrOne(text => string.Join("\n", Row.Matches(text).Select(row => $"譯:{row.Groups[2].Value}")),
                text => $"譯:{text}"),
        };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "c d"], Request(SegmentPrompt), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(3, server.Sent);
        Assert.Equal(
            [SegmentPrompt.BuildBatch(["a b"], [new("前", "之前")]), SegmentPrompt.BuildBatch(["c d"], [new("前", "之前")])],
            server.Requests.Skip(1).Select(request => request.Message).Order());
        Assert.Equal(["譯:a b", "譯:c d"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// A missing number sent as a batch of one and answered with nothing is sent once more, a batch
    /// of one with no context, as for one text.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_SendsAnEmptyAnswerToAMissingNumberAgainAlone()
    {
        var alone = 0;
        var server = new OpenAiFakeServer
        {
            Answer = ManyOrOne(Numbered(text => text == "c d" ? null : $"譯:{text}"), text => ++alone == 1 ? "" : $"譯:{text}"),
        };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "c d"], Request(), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(3, server.Sent);
        Assert.Equal(Prompt.BuildBatch(["c d"]), server.Requests.Last().Message);
        Assert.Equal("譯:c d", outcomes[1].Translation);
    }

    /// <summary>An answer that has its number is taken whatever it says: a copy is not sent again.</summary>
    [Fact]
    public async Task TranslateAsync_TakesACopyAsItIs()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => text.StartsWith("ああ") || text == "Odd Dice" ? text : $"譯:{text}") };
        using var http = new HttpClient(server);
        List<string> texts = ["a b", "ああ次の探索の準備か", "Odd Dice"];

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        Assert.Equal(1, server.Sent);
        Assert.Equal(["譯:a b", "ああ次の探索の準備か", "Odd Dice"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>Under the user's own template nothing is checked: only a missing number goes again.</summary>
    [Fact]
    public async Task TranslateAsync_TheUsersOwnTemplateSendsOnlyMissingNumbersAgain()
    {
        var server = new OpenAiFakeServer
        {
            Answer = ManyOrOne(Numbered(text => text == "e f" ? null : text.StartsWith("ああ") ? text : $"譯:{text}"), _ => "譯:e f"),
        };
        using var http = new HttpClient(server);
        var prompt = Own("翻成中文：\n{text}");

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "ああ次の探索の準備か", "e f"], Request(prompt));

        Assert.Equal(2, server.Sent);
        Assert.Equal("翻成中文：\n<1>e f</1>", server.Requests.Last().Message);
        Assert.Equal(["譯:a b", "ああ次の探索の準備か", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>A batch the server refuses goes as batches of one, so one bad text fails alone.</summary>
    [Fact]
    public async Task TranslateAsync_AFailedBatchGoesOneAtATime()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}"), FailOn = "bad" };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b", "bad x", "c d"], Request());

        Assert.Equal(4, server.Sent);
        Assert.Equal(["譯:a b", null, "譯:c d"], outcomes.Select(outcome => outcome.Translation));
        Assert.IsType<OpenAiChatException>(outcomes[1].Error);
    }

    /// <summary>At most <see cref="OpenAiChatTranslator.MaxBatchTexts"/> texts to a request, in order.</summary>
    [Fact]
    public async Task TranslateAsync_CutsAtTheMostTextsABatchCarries()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}") };
        using var http = new HttpClient(server);
        var texts = Enumerable.Range(0, OpenAiChatTranslator.MaxBatchTexts + 5).Select(i => $"t{i}").ToList();

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        var requests = server.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Contains($"<{OpenAiChatTranslator.MaxBatchTexts}>t{OpenAiChatTranslator.MaxBatchTexts - 1}<", requests[0].Message);
        Assert.Contains("<1>t40</1>", requests[1].Message);
        Assert.Equal(texts.Select(text => $"譯:{text}"), outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// Each batch fits the context it is given: the message, 32, and twice its texts. A later batch
    /// carries the earlier ones' translations.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_CutsEachBatchToFitTheContext()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => $"譯:{text}") };
        using var http = new HttpClient(server);
        var texts = Enumerable.Range(0, 12).Select(i => $"line number {i:D2} of the screen").ToList();
        var request = Request() with { ContextTokens = 900 };

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(texts, request);

        var batches = server.Requests.Where(IsBatch).ToList();
        Assert.True(batches.Count > 1);
        Assert.Equal(batches.Count, server.Sent);
        Assert.All(batches, batch =>
        {
            var asked = Row.Matches(batch.Text).Select(row => row.Groups[2].Value).ToList();
            Assert.True(batch.Message.Length + 32 + 2 * asked.Sum(text => text.Length) <= request.ContextTokens);
        });
        var next = texts.IndexOf(Row.Match(batches[1].Text).Groups[2].Value);
        Assert.Contains($"原文：{texts[next - 1]}\n繁體中文：譯:{texts[next - 1]}\n", batches[1].Message);
        Assert.Equal(texts.Select(text => $"譯:{text}"), outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// When not even two texts fit with the translations, the batch carries the newer half of them,
    /// and so on down to none.
    /// </summary>
    [Fact]
    public void BatchSize_HalvesTheTranslationsBeforeTheBatch()
    {
        var history = Enumerable.Range(0, 30).Select(i => new OpenAiTranslationPair($"earlier line {i}", $"之前 {i}")).ToList();
        List<string> texts = ["a b", "c d", "e f"];
        var request = Request() with { ContextTokens = Prompt.BuildBatch(texts).Length + 32 + 24 };

        var (count, carried) = OpenAiChatTranslator.BatchSize(request, texts, history);

        Assert.Equal(3, count);
        Assert.Null(carried);

        var half = history.Skip(15).ToList();
        var (more, kept) = OpenAiChatTranslator.BatchSize(
            Request() with { ContextTokens = Prompt.BuildBatch(texts, half).Length + 32 + 18 }, texts, history);
        Assert.Equal(3, more);
        Assert.Equal(half, kept);
    }
}
