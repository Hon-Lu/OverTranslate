using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTranslate.Tests;

/// <summary>
/// An OpenAI-compatible server that reads the segments out of each request and answers them, the
/// way a model that follows the format does unless told otherwise.
/// </summary>
/// <remarks>
/// The segments are every <c>&lt;t id="n"&gt;</c> tag in the user message: the format line names
/// its id in words, and the history and the reference are plain lines, so only the texts match.
/// </remarks>
internal sealed class OpenAiFakeServer : HttpMessageHandler
{
    private static readonly Regex Segment = new(@"<t id=""(\d+)"">(.*?)</t>", RegexOptions.Singleline);

    private int _active;
    private int _max;
    private int _sent;

    /// <summary>
    /// What a request with these segments is answered with, before any thinking is added; null for
    /// the default, every segment as <c>translated:</c> and its text.
    /// </summary>
    public Func<IReadOnlyList<string>, string?>? Answer { get; init; }

    /// <summary>Requests with a segment containing this are refused with a 500.</summary>
    public string? FailOn { get; init; }

    /// <summary>Requests with a segment containing this are never answered.</summary>
    public string? HangOn { get; init; }

    /// <summary>Whether a request asking for a stream gets one. Off, it gets a whole completion.</summary>
    public bool Streams { get; init; } = true;

    /// <summary>A stream that breaks after this many pieces, as a dropped connection does.</summary>
    public int? BreakStreamAfter { get; init; }

    /// <summary>How many characters each streamed piece carries.</summary>
    public int PieceLength { get; init; } = 3;

    public ConcurrentQueue<Recorded> Requests { get; } = new();
    public int MaxConcurrentRequests => Volatile.Read(ref _max);
    public int Sent => Volatile.Read(ref _sent);

    internal sealed record Recorded(string Url, string? Authorization, string Body, string Message, IReadOnlyList<string> Segments)
    {
        public JsonElement Payload => JsonDocument.Parse(Body).RootElement;
        public bool Streamed => Payload.GetProperty("stream").GetBoolean();
    }

    /// <summary>The default answer: every segment translated as <c>translated:</c> and its text.</summary>
    public static string Tagged(IEnumerable<string> translations) =>
        string.Join("\n", translations.Select((text, index) => $"<t id=\"{index + 1}\">{text}</t>"));

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
            var segments = Segment.Matches(message).Select(match => match.Groups[2].Value).ToList();
            Requests.Enqueue(new Recorded(
                request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body, message, segments));

            if (HangOn is { } hang && segments.Any(text => text.Contains(hang, StringComparison.Ordinal)))
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (FailOn is { } fail && segments.Any(text => text.Contains(fail, StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

            var answer = Answer?.Invoke(segments) ?? Tagged(segments.Select(text => $"translated:{text}"));
            var stream = payload.RootElement.GetProperty("stream").GetBoolean() && Streams;
            return stream ? Streamed(answer) : Whole(answer);
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

    private HttpResponseMessage Streamed(string content)
    {
        var events = new List<string>();
        for (var i = 0; i < content.Length; i += PieceLength)
        {
            var piece = content.Substring(i, Math.Min(PieceLength, content.Length - i));
            events.Add("data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = piece } } },
            }) + "\n\n");
        }

        var broken = BreakStreamAfter is { } after && after < events.Count;
        if (broken) events = events.Take(BreakStreamAfter!.Value).ToList();
        else events.Add("data: [DONE]\n\n");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EventStream(Encoding.UTF8.GetBytes(string.Concat(events)), broken)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    /// <summary>The bytes of a stream, then — when <paramref name="breaks"/> — a dropped connection.</summary>
    private sealed class EventStream(byte[] bytes, bool breaks) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read == 0 && breaks ? throw new IOException("connection reset") : read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read == 0 && breaks ? throw new IOException("connection reset") : read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
