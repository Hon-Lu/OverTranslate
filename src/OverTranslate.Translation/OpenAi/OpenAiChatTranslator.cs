using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTranslate.Translation.OpenAi;

/// <summary>What every request of one batch is sent with.</summary>
/// <param name="Endpoint">The full chat-completions address — see <see cref="OpenAiChatEndpoint.Resolve"/>.</param>
/// <param name="ApiKey">Sent as a bearer token, or not at all when blank: local servers want none.</param>
/// <param name="SystemPrompt">A message of its own, or no system message at all when empty.</param>
/// <param name="UserPrompt">Written in front of the text, in the same message — see <see cref="OpenAiChatTranslator.BuildMessages"/>.</param>
/// <param name="Temperature">
/// Sent only when not null. A server that refuses one of these fields refuses every value of it, so
/// the only way to say nothing is to send no such field. <paramref name="TopP"/> and
/// <paramref name="Seed"/> are the same.
/// </param>
public sealed record OpenAiChatRequest(
    Uri Endpoint,
    string Model,
    string ApiKey,
    string SystemPrompt,
    string UserPrompt,
    double? Temperature = null,
    double? TopP = null,
    int? Seed = null);

/// <summary>
/// Translates through an OpenAI-compatible Chat Completions endpoint: one request per text, each
/// text sent whole, at most <see cref="MaxConcurrentRequests"/> at a time.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="ITextTranslator"/>, and not a <see cref="BatchTranslator"/>. The batch
/// base cuts long texts into pieces and sends every request at once; a language model wants the
/// whole text as context, and a local server handed twenty requests at once queues them or runs out
/// of memory. And the instruction is written by the application — in the user's words, naming
/// languages the way the interface does — so there is no language code for this to be given.</para>
///
/// <para>Failures the server answers with are <see cref="OpenAiChatException"/>. A request that
/// never got an answer — refused connection, timeout, cancellation — goes up as the
/// <see cref="HttpClient"/> threw it: which of those it was is what the user needs to hear, and the
/// application already words them.</para>
/// </remarks>
public sealed class OpenAiChatTranslator(HttpClient http)
{
    /// <summary>How many requests are in flight at once.</summary>
    public const int MaxConcurrentRequests = 8;

    private static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <returns>One translation per text, in the order given.</returns>
    /// <exception cref="OpenAiChatException">A request was refused, or its answer held no translation.</exception>
    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, OpenAiChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var translations = new string[texts.Count];
        if (texts.Count == 0) return translations;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, texts.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentRequests,
                CancellationToken = cancellationToken,
            },
            async (index, token) => translations[index] = await TranslateOneAsync(texts[index], request, token));

        return translations;
    }

    private async Task<string> TranslateOneAsync(string text, OpenAiChatRequest request, CancellationToken cancellationToken)
    {
        // A dictionary rather than an anonymous type because the sampling fields are conditional.
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages((request.SystemPrompt, request.UserPrompt), text),
        };
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["top_p"] = topP;
        if (request.Seed is { } seed) payload["seed"] = seed;
        payload["stream"] = false;

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var key = request.ApiKey.Trim();
        if (key.Length > 0)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await http.SendAsync(message, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new OpenAiChatException(OpenAiChatFailure.Rejected, response.StatusCode, ReadError(json));

        string content;
        try
        {
            using var document = JsonDocument.Parse(json);
            content = ReadContent(document.RootElement.GetProperty("choices")[0].GetProperty("message"));
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new OpenAiChatException(OpenAiChatFailure.Unparsable, inner: ex);
        }

        var translated = StripThinking(content);
        if (translated.Length == 0)
            throw new OpenAiChatException(OpenAiChatFailure.NoTranslation);
        return translated;
    }

    /// <summary>
    /// The messages for one request: the instruction, and the text to translate under it.
    /// </summary>
    /// <remarks>
    /// The user prompt goes in front of the text in the same message rather than in a message of its
    /// own. That is the format the recommended model documents — an instruction, a blank line, then
    /// the segment — and a model trained that way reads two separate user turns as a conversation it
    /// is being asked to continue rather than as a job.
    ///
    /// Joined with nothing at all: the separator belongs to the wording, which is why the built-in
    /// one ends in a colon and two line feeds. A separator added here would be this library deciding
    /// the shape of somebody else's documented prompt format, and it could not be turned off.
    ///
    /// A system message only when there is one to send. An empty system turn is not nothing — it is
    /// a turn — and the setting the application ships with deliberately has none.
    ///
    /// Every line break in the text leaves as a bare \n. The prompts arrive normalised by the
    /// application; the text is the half that comes from outside it — a block the OCR joined, or a
    /// line a capture carried a \r into — and one screen must not reach the model as two different
    /// strings depending on where its line breaks came from.
    /// </remarks>
    internal static object[] BuildMessages((string System, string User) prompts, string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var messages = new List<object>(2);
        if (prompts.System.Length > 0)
            messages.Add(new { role = "system", content = prompts.System });

        messages.Add(new
        {
            role = "user",
            content = prompts.User + text,
        });

        return [.. messages];
    }

    internal static string StripThinking(string value) => ThinkingBlock.Replace(value, "").Trim();

    private static string ReadContent(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray().Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : ""));
        }

        return "";
    }

    /// <summary>What the server said was wrong — see <see cref="OpenAiChatException.ServerMessage"/>.</summary>
    internal static string? ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString();
        }
        catch (JsonException)
        {
            // Non-JSON proxies and local servers are common; return a bounded response below.
        }

        var compact = json.Trim();
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }
}
