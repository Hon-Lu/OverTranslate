using System.Net.Http;
using System.Text.RegularExpressions;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// OpenAiChatRequest.Batch: a call's texts as few requests as fit, read back by number, and what did
// not come back sent again one at a time. One text at a time is OpenAiChatProtocolTests.
public class OpenAiBatchTests
{
    private static readonly Uri Endpoint = new("http://localhost:1234/v1/chat/completions");

    private static readonly OpenAiPrompt Prompt = new(
        OpenAiPromptLanguage.TraditionalChinese,
        OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese)
            .Replace(OpenAiPrompt.TargetNamePlaceholder, "繁體中文"),
        "繁體中文",
        IsDefault: true);

    private const string Instruction = "將以下文本翻譯為繁體中文，只輸出譯文，不加標籤，不要解釋：";

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

    // ---- Prompt ----------------------------------------------------------------------------------

    /// <summary>
    /// The texts numbered under the line saying how to answer — the real count, the first tag as an
    /// example — where <c>{text}</c> is.
    /// </summary>
    [Fact]
    public void BuildBatch_NumbersTheTextsUnderAFormatLine()
    {
        Assert.Equal(
            Instruction + "\n（共 2 段，依編號輸出譯文，例如 <1>譯文</1>，一段一行）\n<1>a b</1>\n<2>c d</2>",
            Prompt.BuildBatch(["a b", "c\r\nd"]));
    }

    /// <summary>The context is the translations done before, as for one text.</summary>
    [Fact]
    public void BuildBatch_CarriesTheTranslationsDoneBefore()
    {
        var message = Prompt.BuildBatch(["a b", "c d"], [new("前", "之前")]);

        Assert.StartsWith(
            "之前的翻譯，只供參考：\n原文：前\n繁體中文：之前\n\n參考上面，",
            message);
        Assert.EndsWith("<1>a b</1>\n<2>c d</2>", message);
    }

    /// <summary>The user's own template works as it is: the numbered texts go where its text goes.</summary>
    [Fact]
    public void BuildBatch_FillsTheUsersOwnTemplate()
    {
        const string body = "（共 2 段，依編號輸出譯文，例如 <1>譯文</1>，一段一行）\n<1>a</1>\n<2>b</2>";

        Assert.Equal("翻成中文：\n" + body + "\n完", Own("翻成中文：\n{text}\n完").BuildBatch(["a", "b"]));
        Assert.Equal("翻成中文：\n" + body, Own("翻成中文：\n").BuildBatch(["a", "b"]));
        Assert.Equal("翻成中文：\n" + body, Own("{context}\n\n翻成中文：\n{text}").BuildBatch(["a", "b"]));
    }

    [Fact]
    public void BuildBatch_TheOtherWordingsHaveAFormatLine()
    {
        var hans = new OpenAiPrompt(OpenAiPromptLanguage.SimplifiedChinese, "{text}", "简体中文");
        var english = new OpenAiPrompt(OpenAiPromptLanguage.English, "{text}", "English");

        Assert.StartsWith("（共 2 段，按编号输出译文，例如 <1>译文</1>，一段一行）\n<1>", hans.BuildBatch(["a", "b"]));
        Assert.StartsWith("(2 parts: output each translation by its number, for example <1>translation</1>, one per line.)\n<1>",
            english.BuildBatch(["a", "b"]));
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
    /// A number the answer left out is sent again on its own, carrying the history and what the batch
    /// translated — as one text at a time would send it.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_SendsAMissingNumberAgainWithWhatIsTranslated()
    {
        var server = new OpenAiFakeServer { Answer = Numbered(text => text == "c d" ? null : $"譯:{text}", text => $"譯:{text}") };
        using var http = new HttpClient(server);
        List<string> texts = ["a b", "c d", "e f"];
        var history = new List<OpenAiTranslationPair> { new("前", "之前") };

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            texts, Request(), new OpenAiChatContext(history));

        Assert.Equal(2, server.Sent);
        Assert.Equal(Prompt.Build("c d", [new("前", "之前"), new("a b", "譯:a b"), new("e f", "譯:e f")]),
            server.Requests.Last().Message);
        Assert.Equal(["譯:a b", "譯:c d", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>An answer with no number at all sends every text again, one at a time.</summary>
    [Fact]
    public async Task TranslateAsync_AnAnswerWithNoNumbersGoesOneAtATime()
    {
        var server = new OpenAiFakeServer { Answer = text => text.Contains("<1>") ? "甲\n乙" : $"譯:{text}" };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(["a b", "c d"], Request());

        Assert.Equal(3, server.Sent);
        Assert.Equal(["譯:a b", "譯:c d"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>
    /// A missing number sent on its own and answered with nothing is sent once more, alone with no
    /// context, as for one text.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_SendsAnEmptyAnswerToAMissingNumberAgainAlone()
    {
        var alone = 0;
        var server = new OpenAiFakeServer
        {
            Answer = Numbered(text => text == "c d" ? null : $"譯:{text}", text => ++alone == 1 ? "" : $"譯:{text}"),
        };
        using var http = new HttpClient(server);

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "c d"], Request(), new OpenAiChatContext([new("前", "之前")]));

        Assert.Equal(3, server.Sent);
        Assert.Equal(Prompt.Build("c d"), server.Requests.Last().Message);
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
            Answer = Numbered(text => text == "e f" ? null : text.StartsWith("ああ") ? text : $"譯:{text}", _ => "譯:e f"),
        };
        using var http = new HttpClient(server);
        var prompt = Own("翻成中文：\n{text}");

        var outcomes = await new OpenAiChatTranslator(http).TranslateAsync(
            ["a b", "ああ次の探索の準備か", "e f"], Request(prompt));

        Assert.Equal(2, server.Sent);
        Assert.Equal("翻成中文：\ne f", server.Requests.Last().Message);
        Assert.Equal(["譯:a b", "ああ次の探索の準備か", "譯:e f"], outcomes.Select(outcome => outcome.Translation));
    }

    /// <summary>A batch the server refuses goes one text at a time, so one bad text fails alone.</summary>
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
