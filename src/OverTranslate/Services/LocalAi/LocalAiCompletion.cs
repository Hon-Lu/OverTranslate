using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.LocalAi;

/// <summary>One prompt to a running <c>llama-server</c>, through <c>/completion</c>.</summary>
internal static class LocalAiCompletion
{
    /// <param name="prompt">The whole prompt, the model's markers around the message.</param>
    /// <param name="maxTokens">The longest answer to allow.</param>
    /// <param name="timeout">
    /// How long the model may take. On running out the connection is dropped, which is what stops a
    /// model generating: the server has no other way of being told.
    /// </param>
    /// <exception cref="OpenAiChatException">It refused, ran out of time, or answered something unreadable.</exception>
    internal static async Task<string> SendAsync(
        HttpClient http, ILocalAiServerProcess server, string prompt, int maxTokens, TimeSpan timeout, CancellationToken token,
        double? temperature = null, double? topP = null, int? seed = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["n_predict"] = maxTokens,
            ["cache_prompt"] = true,
            ["stream"] = false,
        };
        if (temperature is { } t) payload["temperature"] = t;
        if (topP is { } p) payload["top_p"] = p;
        if (seed is { } s) payload["seed"] = s;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseAddress, "completion"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenAiChatException(OpenAiChatFailure.Rejected, response.StatusCode, OpenAiChatTranslator.ReadError(body));

            return ReadContent(body);
        }
        catch (OperationCanceledException ex) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new OpenAiChatException(OpenAiChatFailure.TimedOut, inner: ex);
        }
    }

    private static string ReadContent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new OpenAiChatException(OpenAiChatFailure.Unparsable, inner: ex);
        }
    }
}
