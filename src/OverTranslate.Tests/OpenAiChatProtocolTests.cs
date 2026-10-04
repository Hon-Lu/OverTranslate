using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

// What OpenAiChatTranslator puts on the wire and how it reads the answer, against canned answers.
// The prompts, the settings and the words a failure reaches the user in are the application's —
// see OpenAiCompatibleProviderTests.
public class OpenAiChatProtocolTests
{
    private static readonly Uri Endpoint = new("http://localhost:1234/v1/chat/completions");

    private static OpenAiChatRequest Request(
        string apiKey = "", double? temperature = null, double? topP = null, int? seed = null) =>
        new(Endpoint, "test-model", apiKey, "", "", temperature, topP, seed);

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

    // ---- Messages ---------------------------------------------------------------------------

    /// <summary>
    /// The user prompt goes in front of the text, in the same message, with a blank line between.
    /// </summary>
    /// <remarks>
    /// The format the recommended model documents — an instruction, a blank line, then the segment.
    /// A model trained that way reads two separate user turns as a conversation it is being asked to
    /// continue rather than as a job, so this is not a free choice of message shape.
    /// </remarks>
    [Fact]
    public void BuildMessages_PutsTheUserPromptInFrontOfTheText()
    {
        var messages = OpenAiChatTranslator.BuildMessages(("", "翻成中文：\n\n"), "hello");

        var user = Assert.Single(messages);
        Assert.Equal("user", Role(user));
        Assert.Equal("翻成中文：\n\nhello", Content(user));
    }

    /// <summary>A system prompt becomes a message of its own, first.</summary>
    [Fact]
    public void BuildMessages_SendsTheSystemPromptAsItsOwnMessage()
    {
        var messages = OpenAiChatTranslator.BuildMessages(("be terse", "翻成中文：\n\n"), "hello");

        Assert.Equal(2, messages.Length);
        Assert.Equal("system", Role(messages[0]));
        Assert.Equal("be terse", Content(messages[0]));
        Assert.Equal("user", Role(messages[1]));
        Assert.Equal("翻成中文：\n\nhello", Content(messages[1]));
    }

    /// <summary>With no prompt at all the model is sent the text and nothing else.</summary>
    [Fact]
    public void BuildMessages_SendsTheTextAloneWhenBothHalvesAreEmpty()
    {
        var user = Assert.Single(OpenAiChatTranslator.BuildMessages(("", ""), "hello"));

        Assert.Equal("user", Role(user));
        Assert.Equal("hello", Content(user));
    }

    /// <summary>One message's role, off the anonymous type the payload is built from.</summary>
    private static string? Role(object message) =>
        (string?)message.GetType().GetProperty("role")!.GetValue(message);

    /// <inheritdoc cref="Role"/>
    private static string? Content(object message) =>
        (string?)message.GetType().GetProperty("content")!.GetValue(message);

    // ---- Requests ---------------------------------------------------------------------------

    [Fact]
    public async Task TranslateAsync_LimitsIndependentRequestsToEightAtATime()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var texts = Enumerable.Range(0, 23).Select(index => $"block-{index:D2}").ToList();

        var translated = await new OpenAiChatTranslator(http).TranslateAsync(texts, Request());

        Assert.Equal(23, handler.Requests.Count);
        Assert.Equal(8, handler.MaxConcurrentRequests);
        Assert.Equal(texts.Select(text => $"translated:{text}"), translated);
    }

    [Fact]
    public async Task TranslateAsync_SendsTheKeyAsABearerTokenAndNothingWhenItIsBlank()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var chat = new OpenAiChatTranslator(http);

        await chat.TranslateAsync(["hello"], Request(apiKey: " secret-key "));
        await chat.TranslateAsync(["hello"], Request(apiKey: "  "));

        Assert.Equal(
            new string?[] { "Bearer secret-key", null },
            handler.Requests.OrderBy(request => request.Authorization is null).Select(request => request.Authorization));
    }

    /// <summary>
    /// Each sampling parameter is sent under the name the API gives it, and only when it has a value.
    /// </summary>
    /// <remarks>
    /// <c>top_p</c> in particular is easy to write as <c>topP</c> from the C# side, and a server that
    /// does not recognise a field ignores it — the symptom is not an error but a setting that
    /// silently does nothing.
    /// </remarks>
    [Fact]
    public async Task TranslateAsync_SendsEachSamplingParameterOnlyWhenItHasAValue()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var chat = new OpenAiChatTranslator(http);

        await chat.TranslateAsync(["hello"], Request(temperature: 0.2, topP: 0.6, seed: 42));
        using (var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body))
        {
            Assert.Equal("test-model", sent.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.2, sent.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(0.6, sent.RootElement.GetProperty("top_p").GetDouble());
            Assert.Equal(42, sent.RootElement.GetProperty("seed").GetInt32());
            Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        }

        handler.Requests.Clear();
        await chat.TranslateAsync(["hello"], Request());

        // Nothing else went with them: the request is still a model, its messages and the stream flag.
        using var none = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(new[] { "model", "messages", "stream" }, none.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TranslateAsync_ReadsTextContentPartsFromCompatibleServers()
    {
        const string response =
            """{"choices":[{"message":{"content":[{"type":"text","text":"陣列格式譯文"}]}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));

        var translated = await new OpenAiChatTranslator(http).TranslateAsync(["hello"], Request());

        Assert.Equal("陣列格式譯文", Assert.Single(translated));
    }

    [Theory]
    [InlineData("<think>internal reasoning</think>\n正確譯文", "正確譯文")]
    [InlineData("<THINK mode=\"deep\">hidden</THINK>Visible", "Visible")]
    [InlineData("保留正常的譯文", "保留正常的譯文")]
    public void StripThinking_RemovesCommonThinkingBlocks(string response, string expected)
    {
        Assert.Equal(expected, OpenAiChatTranslator.StripThinking(response));
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
    /// The texts go out in parallel, and one bad answer still comes out as itself rather than as an
    /// aggregate that says one or more errors occurred.
    /// </summary>
    [Fact]
    public async Task ABadAnswerInABatchStillComesOutAsItself()
    {
        using var http = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.OK, """{"choices":[{"message":{"content":""}}]}"""));

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new OpenAiChatTranslator(http).TranslateAsync(
                Enumerable.Range(0, 12).Select(index => $"block-{index}").ToList(), Request()));

        Assert.Equal(OpenAiChatFailure.NoTranslation, Assert.IsType<OpenAiChatException>(error).Failure);
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
            new OpenAiChatTranslator(http).TranslateAsync(["hello"], request, cancel.Token));
    }

    /// <summary>Waits until the request is cancelled, as a server that never answers does.</summary>
    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    /// <summary>Answers every request with its own user message, and counts how many overlap.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _active;
        private int _max;

        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public int MaxConcurrentRequests => Volatile.Read(ref _max);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            for (var seen = Volatile.Read(ref _max); active > seen; seen = Volatile.Read(ref _max))
                if (Interlocked.CompareExchange(ref _max, active, seen) == seen) break;

            try
            {
                await Task.Delay(10, cancellationToken);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Requests.Enqueue(new RecordedRequest(
                    request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));

                using var payload = JsonDocument.Parse(body);
                var messages = payload.RootElement.GetProperty("messages");
                var userText = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString();
                var response = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { role = "assistant", content = $"<think>hidden</think>translated:{userText}" } },
                    },
                });

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
