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
    /// Reads <paramref name="text"/> in pieces of at most <paramref name="limit"/> characters, at
    /// most <paramref name="parallel"/> of them at a time, and joins what comes back in order.
    /// </summary>
    /// <remarks>
    /// <para>The first piece to fail ends the text: the rest are cancelled and that failure is
    /// thrown. Left running, the pieces behind it on a host that does not answer each waited out a
    /// connection timeout of their own — six seconds for two pieces where one took three.</para>
    ///
    /// <para>A limit on how many go at once is for the services that answer the pieces of one text
    /// slower, or refuse them, the more arrive together.</para>
    /// </remarks>
    protected static Task<byte[]> SpeakInPiecesAsync(
        string text, int limit, int parallel,
        Func<string, CancellationToken, Task<byte[]>> speak, CancellationToken cancellationToken)
    {
        var pieces = SpeechText.Split(text, limit);
        return SpeakInPiecesAsync(pieces, parallel, (i, token) => speak(pieces[i], token), cancellationToken);
    }

    /// <inheritdoc cref="SpeakInPiecesAsync(string, int, int, Func{string, CancellationToken, Task{byte[]}}, CancellationToken)"/>
    /// <param name="speak">Reads the piece at the index given.</param>
    protected static async Task<byte[]> SpeakInPiecesAsync(
        IReadOnlyList<string> pieces, int parallel,
        Func<int, CancellationToken, Task<byte[]>> speak, CancellationToken cancellationToken)
    {
        if (pieces.Count == 1) return await speak(0, cancellationToken);

        using var rest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var turns = new SemaphoreSlim(Math.Min(parallel, pieces.Count));
        var requests = Enumerable.Range(0, pieces.Count).Select(async i =>
        {
            await turns.WaitAsync(rest.Token);
            try { return await speak(i, rest.Token); }
            finally { turns.Release(); }
        }).ToList();

        var pending = new List<Task<byte[]>>(requests);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (done.IsCompletedSuccessfully) continue;

            rest.Cancel();
            try { await Task.WhenAll(pending); }
            catch { /* cancelled behind the failure; that one is the answer */ }
            await done;   // throws its failure
        }

        return Concatenate(requests.Select(request => request.Result).ToList());
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
