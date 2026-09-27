using System.Text.Json;

namespace OverTranslate.Translation.Lookup;

/// <summary>What every dictionary engine here has in common: sending, and turning failures into one kind.</summary>
public abstract class DictionaryEngine(HttpClient http) : IDictionaryEngine
{
    protected HttpClient Http { get; } = http;

    public abstract string Name { get; }

    /// <summary>Sends the requests for one word and reads the entry out of the answers.</summary>
    protected abstract Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken);

    public async Task<DictionaryResult> LookupAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Nothing to look up.", nameof(text));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);

        try
        {
            return await QueryAsync(text, targetLanguage, sourceLanguage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TranslationEngineException)
        {
            // The same sorting SpeechEngine and BatchTranslator do, so the caller has one exception to catch.
            throw ex switch
            {
                HttpRequestException http => new TranslationEngineException(
                    Name, $"request failed ({http.HttpRequestError})", http.StatusCode, http),
                OperationCanceledException timeout => new TranslationEngineException(Name, "timed out", null, timeout),
                JsonException or InvalidOperationException or KeyNotFoundException
                    or IndexOutOfRangeException or FormatException => new TranslationEngineException(
                    Name, $"unreadable answer ({ex.GetType().Name})", null, ex),
                _ => ex,
            };
        }
    }

    /// <summary>Sends a request and hands back the body, or throws with the status.</summary>
    protected async Task<string> ReadStringAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TranslationEngineException(Name, $"HTTP {(int)response.StatusCode}", response.StatusCode);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
