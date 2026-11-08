using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.LocalAi;
using OverTranslate.Services.Providers;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

public class LocalAiProviderTests
{
    private sealed class FakeServer : ILocalAiServerProcess
    {
        public Uri BaseAddress { get; } = new("http://127.0.0.1:9/");
        public string ApiKey => "secret";
        public Task Exited => Task.Delay(-1);
        public string RecentLog => "";
        public void Kill() { }
        public void Dispose() { }
    }

    private sealed class Backend(string format = "hy-mt2-7b") : ILocalAiBackend
    {
        public Exception? Refuse { get; set; }
        public int Asked { get; private set; }

        public Task<LocalAiReady> ReadyAsync(CancellationToken token)
        {
            Asked++;
            if (Refuse is { } refuse) return Task.FromException<LocalAiReady>(refuse);
            return Task.FromResult(new LocalAiReady(new LocalAiLease(new FakeServer(), () => { }), format));
        }
    }

    private sealed class Server(Func<JsonElement, int, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone();
            Bodies.Add(body);
            Keys.Add(request.Headers.Authorization?.Parameter);
            var (status, text) = answer(body, Bodies.Count);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static (HttpStatusCode, string) Reply(string content) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new { content }));

    private static OcrTextBlock Block(string text) => new(text, new System.Windows.Rect(0, 0, 10, 10));

    private static LocalAiProvider Provider(Server server, Backend? backend = null, bool batch = false, string style = "", int timeout = 60) =>
        new(backend ?? new Backend(), () => new LocalAiOptions(batch, style, timeout), new HttpClient(server));

    [Fact]
    public async Task ATextGoesInTheModelsMarkers_ToTheCompletionRoute_WithTheKey()
    {
        var server = new Server((_, _) => Reply("你好"));

        var (blocks, _) = await Provider(server).TranslateAsync([Block("Hello")], "EN-US", "ZH-HANT", "");

        Assert.Equal("你好", blocks[0].TranslatedText);
        var prompt = server.Bodies[0].GetProperty("prompt").GetString()!;
        Assert.StartsWith("<|startoftext|>", prompt);
        Assert.EndsWith("Hello<|extra_0|>", prompt);
        Assert.Equal("secret", server.Keys[0]);
        Assert.True(server.Bodies[0].GetProperty("n_predict").GetInt32() > 0);
    }

    [Fact]
    public async Task AStyle_GoesInsideTheInstruction_NeverOnALineOfItsOwn()
    {
        var server = new Server((_, _) => Reply("x"));

        await Provider(server, style: "語氣口語、輕鬆。").TranslateAsync([Block("Hello")], "EN-US", "ZH-HANT", "");

        var prompt = server.Bodies[0].GetProperty("prompt").GetString()!;
        Assert.DoesNotContain("\n語氣", prompt);
        Assert.Contains("語氣口語、輕鬆", prompt);
        Assert.DoesNotContain("輕鬆。。", prompt);
    }

    [Theory]
    [InlineData(OpenAiPromptLanguage.TraditionalChinese, false, "將以下文本翻譯為{target_name}。口語。只輸出譯文，不加標籤，不要解釋：")]
    [InlineData(OpenAiPromptLanguage.TraditionalChinese, true, "將以下文本翻譯為{target_name}，英文單字與短語也要翻譯。口語。只輸出譯文，不加標籤，不要解釋：")]
    [InlineData(OpenAiPromptLanguage.SimplifiedChinese, false, "将以下文本翻译为{target_name}。口語。只输出译文，不加标签，不要解释：")]
    [InlineData(OpenAiPromptLanguage.English, false, "Translate the following text into {target_name}. 口語. Output only the translation, with no tags and no explanation:")]
    public void TheStyleSitsBetweenTheTargetAndOutputOnly(OpenAiPromptLanguage language, bool batch, string expected) =>
        Assert.Contains(expected, OpenAiPrompt.DefaultTemplate(language, batch, "口語"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  。")]
    public void NoStyleLeavesTheTemplateAsItWas(string? style) =>
        Assert.Equal(
            OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese),
            OpenAiPrompt.DefaultTemplate(OpenAiPromptLanguage.TraditionalChinese, false, style));

    [Fact]
    public async Task ATextTooLongForTheContext_IsCutAndJoinedBack()
    {
        var sentence = "This is one sentence of a very long article that keeps on going. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 60));
        Assert.True(text.Length > LocalAiProvider.MaxPieceCharacters);
        var server = new Server((body, n) => Reply($"第{n}段。"));

        var (blocks, _) = await Provider(server).TranslateAsync([Block(text)], "EN-US", "ZH-HANT", "");

        Assert.True(server.Bodies.Count > 1);
        Assert.All(server.Bodies, body => Assert.True(body.GetProperty("prompt").GetString()!.Length < 2200));
        for (var i = 1; i <= server.Bodies.Count; i++)
            Assert.Contains($"第{i}段。", blocks[0].TranslatedText);
        Assert.False(blocks[0].Untranslated);
    }

    [Fact]
    public async Task ABlockWithAPieceMissing_StandsAsItsOriginal()
    {
        var sentence = "This is one sentence of a very long article that keeps on going. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 60));
        var server = new Server((_, n) => n == 1 ? (HttpStatusCode.InternalServerError, "{}") : Reply("x"));
        // Fails the first piece's request and its one retry.
        var failing = new Server((_, n) => n <= 2 ? (HttpStatusCode.InternalServerError, "{}") : Reply("x"));

        var (blocks, _) = await Provider(failing).TranslateAsync([Block(text), Block("short")], "EN-US", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);
        Assert.Equal(text, blocks[0].TranslatedText);
        Assert.False(blocks[1].Untranslated);
    }

    [Fact]
    public async Task WhatTheBackendRefuses_ReachesTheUserAsItIs_AndNothingIsSent()
    {
        var server = new Server((_, _) => Reply("x"));
        var backend = new Backend { Refuse = new InvalidOperationException("尚未下載") };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(server, backend).TranslateAsync([Block("a"), Block("b")], "EN-US", "ZH-HANT", ""));

        Assert.Equal("尚未下載", thrown.Message);
        Assert.Empty(server.Bodies);
    }

    [Fact]
    public async Task ARequestThatRunsOut_IsATimeout_NotASilentCancellation()
    {
        var server = new Hanging();

        await Assert.ThrowsAsync<TimeoutException>(
            () => new LocalAiProvider(new Backend(), () => new LocalAiOptions(false, "", 1), new HttpClient(server))
                .TranslateAsync([Block("a")], "EN-US", "ZH-HANT", ""));
    }

    /// <summary>The timeout starts once the model is up, so a slow start cannot use it up.</summary>
    [Fact]
    public async Task ASlowStart_DoesNotCountAgainstTheTimeout()
    {
        var server = new Server((_, _) => Reply("x"));
        var backend = new SlowBackend(TimeSpan.FromSeconds(2));

        var (blocks, _) = await new LocalAiProvider(backend, () => new LocalAiOptions(false, "", 1), new HttpClient(server))
            .TranslateAsync([Block("a")], "EN-US", "ZH-HANT", "");

        Assert.Equal("x", blocks[0].TranslatedText);
    }

    private sealed class SlowBackend(TimeSpan wait) : ILocalAiBackend
    {
        private bool _started;

        public async Task<LocalAiReady> ReadyAsync(CancellationToken token)
        {
            if (!_started)
            {
                _started = true;
                await Task.Delay(wait, token);
            }

            return new LocalAiReady(new LocalAiLease(new FakeServer(), () => { }), "gemma-4");
        }
    }

    [Fact]
    public async Task AServerThatDropsARequest_IsAskedAgain()
    {
        var server = new DropsOnce();

        var (blocks, _) = await new LocalAiProvider(new Backend(), () => new LocalAiOptions(false, "", 60), new HttpClient(server))
            .TranslateAsync([Block("a")], "EN-US", "ZH-HANT", "");

        Assert.Equal("ok", blocks[0].TranslatedText);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task AnAnswerLimitGrowsWithTheText_AndHasACeiling()
    {
        Assert.True(LocalAiProvider.AnswerLimit(2000) > LocalAiProvider.AnswerLimit(100));
        Assert.Equal(3000, LocalAiProvider.AnswerLimit(100_000));
        await Task.CompletedTask;
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(-1, token);
            throw new InvalidOperationException();
        }
    }

    private sealed class DropsOnce : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (++Calls == 1) throw new HttpRequestException("connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { content = "ok" }), Encoding.UTF8, "application/json"),
            });
        }
    }
}
