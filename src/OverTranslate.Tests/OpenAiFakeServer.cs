using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace OverTranslate.Tests;

/// <summary>
/// An OpenAI-compatible server that reads the text out of each request and answers it, the way a
/// model that follows the format does unless told otherwise.
/// </summary>
/// <remarks>
/// The text is whatever follows the last line asking for it — 要翻譯的原文： and its two other
/// wordings — in the user message; under the user's own instruction, which has no such line, it is
/// the whole user message.
/// </remarks>
internal sealed class OpenAiFakeServer : HttpMessageHandler
{
    private static readonly string[] TargetHeaders = ["要翻譯的原文：\n", "要翻译的原文：\n", "Source text to translate:\n"];

    private int _active;
    private int _max;
    private int _sent;

    /// <summary>
    /// What a request for this text is answered with; null for the default, <c>translated:</c> and
    /// the text.
    /// </summary>
    public Func<string, string?>? Answer { get; init; }

    /// <summary>Requests for a text containing this are refused with a 500.</summary>
    public string? FailOn { get; init; }

    /// <summary>Requests for a text containing this are never answered.</summary>
    public string? HangOn { get; init; }

    public ConcurrentQueue<Recorded> Requests { get; } = new();
    public int MaxConcurrentRequests => Volatile.Read(ref _max);
    public int Sent => Volatile.Read(ref _sent);

    internal sealed record Recorded(string Url, string? Authorization, string Body, string Message, string Text)
    {
        public JsonElement Payload => JsonDocument.Parse(Body).RootElement;
        public bool Streamed => Payload.GetProperty("stream").GetBoolean();
    }

    /// <summary>The text a user message asks for — see the remarks on the class.</summary>
    public static string TextOf(string message)
    {
        foreach (var header in TargetHeaders)
        {
            var at = message.LastIndexOf(header, StringComparison.Ordinal);
            if (at >= 0) return message[(at + header.Length)..];
        }

        return message;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sent);
        var active = Interlocked.Increment(ref _active);
        for (var seen = Volatile.Read(ref _max); active > seen; seen = Volatile.Read(ref _max))
            if (Interlocked.CompareExchange(ref _max, active, seen) == seen) break;

        try
        {
            await Task.Delay(10, cancellationToken);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(body);
            var messages = payload.RootElement.GetProperty("messages");
            var message = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            var text = TextOf(message);
            Requests.Enqueue(new Recorded(
                request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body, message, text));

            if (HangOn is { } hang && text.Contains(hang, StringComparison.Ordinal))
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (FailOn is { } fail && text.Contains(fail, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

            return Whole(Answer?.Invoke(text) ?? $"translated:{text}");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public static HttpResponseMessage Whole(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
        })),
    };
}
