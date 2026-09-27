using System.Text.Json;

namespace OverTranslate.Translation.Speech;

/// <summary>What every speech engine here has in common: sending, and turning failures into one kind.</summary>
public abstract class SpeechEngine(HttpClient http) : ISpeechEngine
{
    protected HttpClient Http { get; } = http;

    public abstract string Name { get; }

    public abstract bool Supports(string language);

    /// <summary>Sends the requests for one text and returns the MP3 they add up to.</summary>
    protected abstract Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken);

    public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Nothing to read aloud.", nameof(text));
        if (!Supports(language)) throw new TranslationEngineException(Name, $"no voice for {language}");

        byte[] audio;
        try
        {
            audio = await SpeakAsync(text, language, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TranslationEngineException)
        {
            // The same sorting BatchTranslator does, so the caller has one exception to catch.
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

        // An empty body with a 200 is not speech, and the player would open it and play silence.
        if (audio.Length == 0) throw new TranslationEngineException(Name, "empty audio");
        return audio;
    }

    /// <summary>Sends a request and hands back the body, or throws with the status.</summary>
    protected async Task<byte[]> ReadBytesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TranslationEngineException(Name, $"HTTP {(int)response.StatusCode}", response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <inheritdoc cref="ReadBytesAsync"/>
    protected async Task<string> ReadStringAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TranslationEngineException(Name, $"HTTP {(int)response.StatusCode}", response.StatusCode);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// The MP3s of consecutive pieces as one. MP3 is a run of self-contained frames, so pieces
    /// placed end to end play as one recording — which is how GTranslate joined them too.
    /// </summary>
    protected static byte[] Concatenate(IReadOnlyList<byte[]> pieces)
    {
        if (pieces.Count == 1) return pieces[0];

        var joined = new byte[pieces.Sum(piece => piece.Length)];
        var offset = 0;
        foreach (var piece in pieces)
        {
            piece.CopyTo(joined, offset);
            offset += piece.Length;
        }

        return joined;
    }
}
