using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace OverTranslate.Translation;

/// <summary>
/// What every engine here has in common: cutting long texts, packing the pieces into as few
/// requests as the engine allows, sending them, and putting the answers back where they belong.
/// </summary>
/// <remarks>
/// <para>An engine only says how much one request may carry and how to send one. Everything
/// between a list of texts and a list of translations is the same work for all of them, and doing
/// it once is what makes "one request per screen" a property of the library rather than something
/// each engine has to get right on its own.</para>
///
/// <para>Texts are packed in order and never reordered. Filling requests tighter by shuffling
/// would save a request now and then, and cost the property that makes a failure easy to reason
/// about: the texts one request carried are a contiguous run of the screen.</para>
/// </remarks>
public abstract class BatchTranslator(HttpClient http) : ITextTranslator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    protected HttpClient Http { get; } = http;

    public abstract string Name { get; }

    /// <summary>The most pieces one request may carry.</summary>
    protected abstract int MaxItemsPerRequest { get; }

    /// <summary>The most characters (<see cref="string.Length"/>) one request may carry in total.</summary>
    protected abstract int MaxCharactersPerRequest { get; }

    /// <summary>
    /// Whether this engine's detector leaves a sentence untranslated for the label in front of it,
    /// so that detected pieces are checked for it and asked again — see <see cref="MixedScriptText"/>.
    /// </summary>
    protected virtual bool RescuesMixedScript => false;

    /// <summary>The longest piece a text is cut into — see <see cref="TranslationRequestChunks"/>.</summary>
    protected virtual int MaxCharactersPerPiece => TranslationRequestChunks.SafeMaxCharacters;

    /// <summary>Cuts one text into the pieces that are actually sent.</summary>
    /// <remarks>Never called with a blank text; those are not sent at all.</remarks>
    private protected virtual IReadOnlyList<TranslationRequestChunk> Split(string text) =>
        TranslationRequestChunks.Split(text, MaxCharactersPerPiece);

    private static readonly Regex ParagraphBreak = new(@"\r?\n[ \t]*\r?\n\s*", RegexOptions.Compiled);
    private static readonly Regex LineBreak = new(@"[ \t]*\r?\n[ \t]*", RegexOptions.Compiled);

    /// <summary>
    /// For an engine that loses blank lines: every paragraph becomes pieces of its own, and the
    /// blank line between two paragraphs is put back by <see cref="TranslationRequestChunks.Join"/>.
    /// </summary>
    /// <param name="joinLines">
    /// Also turn a lone line break into a space, for an engine that would read it as one anyway.
    /// </param>
    private protected IReadOnlyList<TranslationRequestChunk> SplitParagraphs(string text, bool joinLines)
    {
        var paragraphs = ParagraphBreak.Split(text.Trim())
            .Select(paragraph => joinLines ? LineBreak.Replace(paragraph, " ") : paragraph)
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        var pieces = new List<TranslationRequestChunk>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var chunks = TranslationRequestChunks.Split(paragraphs[i], MaxCharactersPerPiece);

            // The last piece of a paragraph is followed by the next paragraph, and says so; within
            // a paragraph the chunker's own boundaries stand.
            if (i < paragraphs.Count - 1)
                chunks[^1] = chunks[^1] with { BoundaryAfter = TranslationChunkBoundary.Paragraph };

            pieces.AddRange(chunks);
        }

        return pieces;
    }

    /// <summary>Sends one request's worth of pieces.</summary>
    /// <returns>
    /// Exactly one entry per piece, in order — a count that does not match is thrown for the
    /// caller. An entry may be null when the engine answered the request but refused that one
    /// piece; those pieces are asked again on their own (see <see cref="SendMeasuredAsync"/>).
    /// </returns>
    protected abstract Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken);

    public IReadOnlyList<IReadOnlyList<int>> Plan(IReadOnlyList<string> texts)
    {
        var groups = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        int items = 0, characters = 0;

        for (var i = 0; i < texts.Count; i++)
        {
            var pieces = IsBlank(texts[i]) ? [] : Split(texts[i]);
            var length = pieces.Sum(piece => piece.Text.Length);

            // A text that is more than a request on its own still starts a group of its own rather
            // than being refused: TranslateAsync spreads its pieces over as many requests as it
            // needs, and they rise and fall together.
            if (current.Count > 0 &&
                (items + pieces.Count > MaxItemsPerRequest || characters + length > MaxCharactersPerRequest))
            {
                groups.Add(current);
                current = [];
                items = characters = 0;
            }

            current.Add(i);
            items += pieces.Count;
            characters += length;
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    public async Task<IReadOnlyList<TextTranslation>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLanguage, string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0) return [];

        var chunks = texts
            .Select(text => IsBlank(text) ? [] : Split(text))
            .ToList();

        // Every piece of every text, in order, remembering whose it is.
        var pieces = new List<(int Text, int Piece, string Content)>();
        for (var t = 0; t < texts.Count; t++)
            for (var p = 0; p < chunks[t].Count; p++)
                pieces.Add((t, p, chunks[t][p].Text));

        var requests = Pack(pieces.Select(piece => piece.Content).ToList());
        var answers = await Task.WhenAll(requests.Select(range =>
            SendMeasuredAsync(pieces.GetRange(range.Start, range.Count).Select(piece => piece.Content).ToList(),
                targetLanguage, sourceLanguage, cancellationToken)));

        var translated = chunks.Select(c => new string[c.Count]).ToList();
        var detected = new string[texts.Count];
        var untranslated = new bool[texts.Count];
        for (var r = 0; r < requests.Count; r++)
        {
            for (var k = 0; k < requests[r].Count; k++)
            {
                var (text, piece, _) = pieces[requests[r].Start + k];
                var answer = answers[r][k];
                translated[text][piece] = answer.Text;
                untranslated[text] |= answer.Untranslated;

                // The first piece that names a language speaks for the text; the rest are the same
                // text and a later disagreement is a shorter piece being read with less to go on.
                if (string.IsNullOrEmpty(detected[text])) detected[text] = answer.DetectedLanguage;
            }
        }

        var results = new TextTranslation[texts.Count];
        for (var t = 0; t < texts.Count; t++)
        {
            results[t] = chunks[t].Count switch
            {
                // Nothing to translate: the text stands for itself, which is also what every
                // engine answers when handed one.
                0 => new TextTranslation(texts[t], ""),
                1 => new TextTranslation(translated[t][0], detected[t] ?? ""),
                _ => new TextTranslation(TranslationRequestChunks.Join(chunks[t], translated[t]), detected[t] ?? ""),
            } with { Untranslated = untranslated[t] };
        }

        if (chunks.Any(c => c.Count > 1))
        {
            // Where a long text was cut, because a translation that reads oddly at a seam is
            // otherwise indistinguishable from one the engine simply got wrong.
            Log.Debug("{Engine}：{Count} 段文字超過 {Limit} 字，已切段送出",
                Name, chunks.Count(c => c.Count > 1), MaxCharactersPerPiece);
        }

        return results;
    }

    /// <summary>Greedy, in order: each request takes pieces until the next one would not fit.</summary>
    private List<(int Start, int Count)> Pack(IReadOnlyList<string> pieces)
    {
        var requests = new List<(int Start, int Count)>();
        int start = 0, characters = 0;

        for (var i = 0; i < pieces.Count; i++)
        {
            var count = i - start;
            if (count > 0 &&
                (count + 1 > MaxItemsPerRequest || characters + pieces[i].Length > MaxCharactersPerRequest))
            {
                requests.Add((start, count));
                start = i;
                characters = 0;
            }

            characters += pieces[i].Length;
        }

        if (pieces.Count > start) requests.Add((start, pieces.Count - start));
        return requests;
    }

    /// <summary>
    /// One request, timed and logged, with whatever it threw turned into something a caller can
    /// sort without knowing which engine it came from.
    /// </summary>
    /// <remarks>
    /// Logs sizes, timings and outcomes, never the text. What is sent is what the user had on
    /// screen, and logs travel in diagnostic bundles; how many texts, how long and how it ended is
    /// what tells a slow engine from a failing one, which is the question these lines are for.
    /// </remarks>
    private async Task<IReadOnlyList<TextTranslation>> SendMeasuredAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var characters = pieces.Sum(piece => piece.Length);

        try
        {
            var answers = await SendCheckedAsync(pieces, targetLanguage, sourceLanguage, cancellationToken);

            // Pieces the engine left unanswered inside an answer that was otherwise fine. A whole
            // request failing for one of them would take a screen to the next engine for the sake
            // of a line, so only those are asked again, once, from this same engine.
            var missing = Enumerable.Range(0, pieces.Count).Where(i => Unanswered(pieces[i], answers[i])).ToList();
            if (missing.Count > 0)
            {
                // Info: rare, and the only trace of an engine that failed in part and recovered —
                // when it does not recover, the failure below is logged at Info as well.
                Log.Info("{Engine}：{Missing}/{Items} 格沒有答案，只重送這幾格", Name, missing.Count, pieces.Count);

                var retried = await SendCheckedAsync(
                    missing.Select(i => pieces[i]).ToList(), targetLanguage, sourceLanguage, cancellationToken);
                for (var k = 0; k < missing.Count; k++)
                    answers[missing[k]] = retried[k];

                var still = Enumerable.Range(0, pieces.Count).Count(i => Unanswered(pieces[i], answers[i]));
                if (still > 0)
                    throw new TranslationEngineException(Name, $"left {still} of {pieces.Count} texts unanswered");
            }

            if (sourceLanguage is null && RescuesMixedScript)
                await RescueMixedScriptAsync(pieces, answers, targetLanguage, cancellationToken);

            Log.Debug("{Engine}：{Items} 格 {Characters} 字，{Elapsed} ms",
                Name, pieces.Count, characters, Elapsed(started));
            return answers!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex switch
            {
                TranslationEngineException engine => engine,
                HttpRequestException http => new TranslationEngineException(
                    Name, $"request failed ({http.HttpRequestError})", http.StatusCode, http),

                // Not the caller's token, so HttpClient's own timeout.
                OperationCanceledException timeout => new TranslationEngineException(
                    Name, "timed out", null, timeout),

                JsonException or InvalidOperationException or KeyNotFoundException
                    or IndexOutOfRangeException or FormatException => new TranslationEngineException(
                    Name, $"unreadable answer ({ex.GetType().Name})", null, ex),

                _ => null,
            };

            Log.Info("{Engine} 失敗：{Items} 格 {Characters} 字，{Elapsed} ms，{Status}，{Reason}",
                Name, pieces.Count, characters, Elapsed(started),
                failure?.StatusCode is { } status ? (int)status : "-",
                failure?.Message ?? ex.GetType().Name);

            if (failure is null || ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
    }

    private async Task<TextTranslation?[]> SendCheckedAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var answers = await SendAsync(pieces, targetLanguage, sourceLanguage, cancellationToken);

        // Positional, so a count that does not match cannot be lined up with anything — which
        // answer belongs to which text is exactly what would be guessed.
        if (answers.Count != pieces.Count)
            throw new TranslationEngineException(Name, $"answered {answers.Count} of {pieces.Count} texts");

        return [.. answers];
    }

    /// <summary>
    /// Asks again for the pieces whose sentence was left in the original, in that sentence's
    /// language — see <see cref="MixedScriptText"/>.
    /// </summary>
    /// <remarks>
    /// <para>More requests only when there is such a piece: the sentences on their own, for
    /// the language each is in — Latin script is English as often as not, but not always — then the
    /// pieces again, one request per language found.</para>
    ///
    /// <para>The first answer was a good one for everything else in the request, so nothing here is
    /// allowed to fail it. A piece that could not be put right keeps what it had and is marked
    /// <see cref="TextTranslation.Untranslated"/>, which keeps it out of any cache and asked again.
    /// One the second answer still leaves as it was is taken as it stands: that is the engine's
    /// answer, not a failure, and marking it would have it asked for forever.</para>
    /// </remarks>
    private async Task RescueMixedScriptAsync(
        IReadOnlyList<string> pieces, TextTranslation?[] answers, string targetLanguage,
        CancellationToken cancellationToken)
    {
        var suspects = new List<(int Index, string Clause)>();
        for (var i = 0; i < pieces.Count; i++)
            if (MixedScriptText.UntranslatedClause(pieces[i], answers[i]!, targetLanguage) is { } clause)
                suspects.Add((i, clause));
        if (suspects.Count == 0) return;

        // Taken off as each is settled; whatever is left at the end could not be.
        var pending = suspects.Select(suspect => suspect.Index).ToHashSet();
        try
        {
            var probed = await SendCheckedAsync(
                suspects.Select(suspect => suspect.Clause).ToList(), targetLanguage, null, cancellationToken);

            var byLanguage = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k < suspects.Count; k++)
            {
                if (probed[k] is not { } probe) continue;   // refused: stays pending

                // A sentence that is in the language asked for, or in none the engine can name, was
                // left because there was nothing to do.
                var language = probe.DetectedLanguage;
                if (language.Length == 0 || MixedScriptText.SameLanguage(language, targetLanguage))
                {
                    pending.Remove(suspects[k].Index);
                    continue;
                }

                if (!byLanguage.TryGetValue(language, out var indices))
                    byLanguage[language] = indices = [];
                indices.Add(suspects[k].Index);
            }

            foreach (var (language, indices) in byLanguage)
            {
                var again = await SendCheckedAsync(
                    indices.Select(i => pieces[i]).ToList(), targetLanguage, language, cancellationToken);
                for (var k = 0; k < indices.Count; k++)
                {
                    if (Unanswered(pieces[indices[k]], again[k])) continue;
                    answers[indices[k]] = again[k]! with { DetectedLanguage = language };
                    pending.Remove(indices[k]);
                }
            }

            Log.Debug("{Engine}：{Count} 格混排文字的句子沒翻，依句子的語言重送",
                Name, byLanguage.Sum(group => group.Value.Count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Info("{Engine}：混排文字重送失敗（{Reason}）",
                Name, ex is TranslationEngineException ? ex.Message : ex.GetType().Name);
        }

        if (pending.Count > 0)
        {
            Log.Info("{Engine}：{Count} 格混排文字沒能重譯，保留首次譯文並標記為未翻譯", Name, pending.Count);
            foreach (var i in pending)
                answers[i] = answers[i]! with { Untranslated = true };
        }
    }

    /// <summary>
    /// No answer, or nothing where there was something to translate.
    /// </summary>
    /// <remarks>
    /// The second is not hypothetical: 「Google 翻譯 (Web)」 has twice answered one line of a batch
    /// with an empty string — the same line alone, and the same batch again, came back fine. Shown,
    /// it is a box with nothing in it, and 即時翻譯 would keep it for the whole session. Blank
    /// pieces are never sent, so a blank answer to a piece is never a real translation.
    /// </remarks>
    private static bool Unanswered(string piece, TextTranslation? answer) =>
        answer is null || (string.IsNullOrWhiteSpace(answer.Text) && !string.IsNullOrWhiteSpace(piece));

    /// <summary>
    /// Sends a request and hands back the body, or throws with the status when there is none worth
    /// reading.
    /// </summary>
    protected async Task<string> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new TranslationEngineException(Name, $"HTTP {(int)response.StatusCode}", response.StatusCode);

        return body;
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);

    /// <summary>Reads a JSON string that has to be there.</summary>
    protected static string RequireString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new FormatException($"expected a string, found {element.ValueKind}");
}
