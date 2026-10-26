using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace OverTranslate.Translation.OpenAi;

/// <summary>What every request of one call is sent with.</summary>
/// <param name="Endpoint">The full chat-completions address — see <see cref="OpenAiChatEndpoint.Resolve"/>.</param>
/// <param name="ApiKey">Sent as a bearer token, or not at all when blank: local servers want none.</param>
/// <param name="Prompt">The instruction every request is written from — see <see cref="OpenAiPrompt.Build"/>.</param>
/// <param name="TargetTag">
/// The target language as a BCP 47 tag, for telling a translation from a copy — see
/// <see cref="OpenAiContentCheck"/>. Not sent.
/// </param>
/// <param name="Temperature">
/// Sent only when not null. A server that refuses one of these fields refuses every value of it, so
/// the only way to say nothing is to send no such field. <paramref name="TopP"/>,
/// <paramref name="Seed"/> and <paramref name="ReasoningEffort"/> are the same.
/// </param>
/// <param name="Timeout">
/// How long each request may take, from sending it to reading the whole answer; null leaves it to
/// the <see cref="HttpClient"/>. Per request rather than per call: a screen of many segments is
/// rightly slower than one line, while a single request that runs past this is a server that is not
/// coming back.
/// </param>
/// <param name="ContextTokens">
/// How many tokens the model can be assumed to read, for cutting a screen into batches — see
/// <see cref="OpenAiChatTranslator.Batches"/>.
/// </param>
public sealed record OpenAiChatRequest(
    Uri Endpoint,
    string Model,
    string ApiKey,
    OpenAiPrompt Prompt,
    string TargetTag = "",
    double? Temperature = null,
    double? TopP = null,
    int? Seed = null,
    string? ReasoningEffort = null,
    TimeSpan? Timeout = null,
    int ContextTokens = OpenAiChatTranslator.DefaultContextTokens);

/// <summary>What one call knows beyond its texts.</summary>
/// <param name="History">Earlier lines of the same session, oldest first — see <see cref="OpenAiPrompt.Build"/>.</param>
/// <param name="Reference">Lines on the same screen that are already translated.</param>
/// <param name="SegmentTranslated">
/// Told each time a text's translation is known or changes, with its index and the translation: as
/// a streamed segment arrives, when the whole answer reads a segment differently, when a segment
/// sent again comes back. Called from whichever thread the answer arrived on, and possibly from
/// several at once.
/// </param>
public sealed record OpenAiChatContext(
    IReadOnlyList<OpenAiTranslationPair>? History = null,
    IReadOnlyList<OpenAiTranslationPair>? Reference = null,
    Action<int, string>? SegmentTranslated = null);

/// <summary>What became of one text: its translation, or why there is none.</summary>
/// <param name="Translation">Null when the text was not translated.</param>
/// <param name="Error">
/// What the request failed with — an <see cref="OpenAiChatException"/>, or what the
/// <see cref="HttpClient"/> threw. Null both for a translation and for a text that was never sent,
/// see <see cref="NotSent"/>.
/// </param>
public sealed record OpenAiChatOutcome(string? Translation, Exception? Error)
{
    /// <summary>A text left unsent because the call had already failed every request it made.</summary>
    public static OpenAiChatOutcome NotSent { get; } = new(null, null);

    public bool Translated => Translation is not null;
}

/// <summary>
/// Translates through an OpenAI-compatible Chat Completions endpoint: a whole screen in one request,
/// read back by segment id, with the request for each text alone as the way back.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="ITextTranslator"/>, and not a <see cref="BatchTranslator"/>. The batch
/// base cuts long texts into pieces; a language model wants every text whole, and the screen around
/// it — the whole point of asking a model rather than an engine. And the instruction is written by
/// the application, naming languages the way the interface does, so there is no language code for
/// this to be given.</para>
///
/// <para>Measured before it was written: with the screen in one request, the good models named the
/// same thing the same way across a page where one request per text named it five ways, and fixed
/// subjects a single line cannot show. How each step falls back is what those measurements settled —
/// see <see cref="TranslateAsync"/>.</para>
///
/// <para>Failures the server answers with are <see cref="OpenAiChatException"/>, and so is a request
/// that ran past <see cref="OpenAiChatRequest.Timeout"/>. One that never got an answer otherwise —
/// refused connection, cancellation — goes up as the <see cref="HttpClient"/> threw it: which of
/// those it was is what the user needs to hear, and the application already words them.</para>
/// </remarks>
public sealed class OpenAiChatTranslator(HttpClient http)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How many requests are in flight at once, when texts are sent one by one.</summary>
    public const int MaxConcurrentRequests = 8;

    /// <summary>
    /// The most segments one request carries.
    /// </summary>
    /// <remarks>
    /// Measured, the good models got every id right from ten segments to eighty, in time that grew
    /// with the count; forty is where a page that does fail costs a bounded amount to send again one
    /// by one.
    /// </remarks>
    public const int MaxSegmentsPerBatch = 40;

    /// <summary>
    /// How many tokens a model is assumed to read when nothing says otherwise.
    /// </summary>
    /// <remarks>
    /// The context a local server gives a model it was not told the size of, on the older releases
    /// that gave the least; newer ones give more. Assumed rather than asked, because the
    /// OpenAI-compatible route has no way to ask or to set it. Past it, a local model is cut from the
    /// front without a word — the instruction is what goes first — and starts writing anything at
    /// all; a smaller assumption costs only a screen sent as two requests instead of one.
    /// </remarks>
    public const int DefaultContextTokens = 4096;

    internal static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    internal static readonly Regex UpToClosingTag = new(
        @"^.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>An answer that opens with a thinking block, closed later or not.</summary>
    internal static readonly Regex LeadingOpeningTag = new(
        @"^\s*<think(?:\s[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnclosedLeadingBlock = new(
        @"^\s*<think(?:\s[^>]*)?>.*\z",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <returns>One outcome per text, in the order given.</returns>
    /// <exception cref="OpenAiChatException">
    /// Not one text was translated: the first text's failure, as it was thrown.
    /// </exception>
    /// <remarks>
    /// <para>The texts go in batches — see <see cref="Batches"/> — one after another, each streamed
    /// so that a segment is told to <see cref="OpenAiChatContext.SegmentTranslated"/> as soon as its
    /// closing tag is in. The whole answer, once in, is what counts.</para>
    ///
    /// <para>An answer whose ids are not exactly 1 to n — a segment merged into the next, one left
    /// out, the request failing — is set aside whole, and every text of that batch is sent alone.
    /// Keeping the ids that did come back was measured and rejected: in a failed answer they are
    /// often already one place off.</para>
    ///
    /// <para>An answer whose ids are right is checked segment by segment — see
    /// <see cref="OpenAiContentCheck"/> — and each that fails is sent alone, once. What comes back
    /// is taken whatever it says: sending again was measured to rescue most of what it could, and
    /// the rest was the model's own limit.</para>
    ///
    /// <para>One text failing does not take the others with it: which texts failed, and how, is in
    /// the outcomes for the caller to show. Every text failing is still thrown, as the first one's
    /// failure — a server that is not running, a wrong key, a model that is not there is one cause
    /// the user has to read. Once a whole round of single requests has failed with nothing
    /// translated, the rest are not sent: a server that answers nothing would otherwise be waited on
    /// one timeout per round instead of one in all.</para>
    ///
    /// <para>The caller's cancellation is not a failure of any text: it goes up as it always did.</para>
    /// </remarks>
    public async Task<IReadOnlyList<OpenAiChatOutcome>> TranslateAsync(
        IReadOnlyList<string> texts,
        OpenAiChatRequest request,
        OpenAiChatContext? context = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = new Job(texts, request, context ?? new OpenAiChatContext(), cancellationToken);
        if (texts.Count == 0) return job.Outcomes;

        foreach (var (start, count) in Batches(texts, request, job.Context))
        {
            if (count == 1) await SinglesAsync(job, [start], resend: false);
            else await BatchAsync(job, start, count);
        }

        if (job.Translated == 0)
        {
            var error = job.Outcomes.FirstOrDefault(outcome => outcome.Error is not null)?.Error
                ?? new OpenAiChatException(OpenAiChatFailure.NoTranslation);
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return job.Outcomes;
    }

    /// <summary>One call's texts and what has become of them so far.</summary>
    private sealed class Job(
        IReadOnlyList<string> texts, OpenAiChatRequest request, OpenAiChatContext context, CancellationToken token)
    {
        public IReadOnlyList<string> Texts { get; } = texts;
        public OpenAiChatRequest Request { get; } = request;
        public OpenAiChatContext Context { get; } = context;
        public CancellationToken Token { get; } = token;
        public OpenAiChatOutcome[] Outcomes { get; } = new OpenAiChatOutcome[texts.Count];

        private int _translated;
        private int _failed;

        public int Translated => Volatile.Read(ref _translated);
        public int Failed => Volatile.Read(ref _failed);

        /// <param name="shown">What the caller was already told for this text, if anything.</param>
        public void Succeed(int index, string translation, bool alreadyCounted = false, string? shown = null)
        {
            Outcomes[index] = new OpenAiChatOutcome(translation, null);
            if (!alreadyCounted) Interlocked.Increment(ref _translated);
            if (translation != shown) Context.SegmentTranslated?.Invoke(index, translation);
        }

        public void Fail(int index, Exception error)
        {
            Outcomes[index] = new OpenAiChatOutcome(null, error);
            Interlocked.Increment(ref _failed);
        }
    }

    /// <summary>
    /// The texts cut into batches, in order: at most <see cref="MaxSegmentsPerBatch"/> each, and
    /// each within <see cref="OpenAiChatRequest.ContextTokens"/>, whichever comes first.
    /// </summary>
    /// <remarks>
    /// The budget is the message's characters, plus 32, plus twice the source's characters — room
    /// for the answer — against the context. Measured against what a local server counted on some
    /// seven thousand requests, it never came out short. A model's own system prompt, set up on the
    /// server, is the one thing it cannot see.
    /// </remarks>
    internal static List<(int Start, int Count)> Batches(
        IReadOnlyList<string> texts, OpenAiChatRequest request, OpenAiChatContext context)
    {
        bool Fits(int start, int count)
        {
            var slice = Slice(texts, start, count);
            var message = request.Prompt.Build(slice, context.History, context.Reference);
            return message.Length + 32 + 2 * slice.Sum(text => text.Length) <= request.ContextTokens;
        }

        var batches = new List<(int, int)>();
        int first = 0, size = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            if (size > 0 && (size >= MaxSegmentsPerBatch || !Fits(first, size + 1)))
            {
                batches.Add((first, size));
                first = i;
                size = 0;
            }

            size++;
        }

        if (size > 0) batches.Add((first, size));
        return batches;
    }

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> texts, int start, int count) =>
        Enumerable.Range(start, count).Select(i => texts[i]).ToList();

    /// <summary>One batch: sent whole and streamed, then read, checked and mended.</summary>
    private async Task BatchAsync(Job job, int start, int count)
    {
        var texts = Slice(job.Texts, start, count);
        var arriving = new OpenAiAnswer.Arriving(count);
        var watch = Stopwatch.StartNew();

        OpenAiAnswer? answer = null;
        string failure = "";
        try
        {
            var message = job.Request.Prompt.Build(texts, job.Context.History, job.Context.Reference);
            var content = await SendAsync(message, job.Request, stream: true, piece =>
            {
                foreach (var (index, text) in arriving.Add(piece))
                    job.Context.SegmentTranslated?.Invoke(start + index, text);
            }, job.Token);
            answer = OpenAiAnswer.Read(content, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !job.Token.IsCancellationRequested)
        {
            failure = ex is OpenAiChatException chat ? chat.Failure.ToString() : ex.GetType().Name;
        }

        if (answer is not { Complete: true })
        {
            // Counts and kinds only, never the text, so this stays in the shipped log: it is what
            // explains a screen that took far longer than usual.
            Log.Info("OpenAI 相容翻譯：{Count} 段整批失敗（{Reason}，{Elapsed} ms），改逐段重送",
                count, answer is null ? failure : answer.Tagged ? "id 不符" : "沒有段落標籤",
                watch.ElapsedMilliseconds);
            await SinglesAsync(job, Enumerable.Range(start, count).ToList(), resend: false);
            return;
        }

        var resend = new List<int>();
        for (var k = 0; k < count; k++)
        {
            var translation = answer.Translations[k]!;
            job.Succeed(start + k, translation, shown: arriving.Shown(k));
            if (!Passes(texts[k], translation, job.Request.TargetTag)) resend.Add(start + k);
        }

        Log.Info("OpenAI 相容翻譯：{Count} 段一批完成，{Elapsed} ms，內容檢查重送 {Resend} 段",
            count, watch.ElapsedMilliseconds, resend.Count);

        if (resend.Count > 0) await SinglesAsync(job, resend, resend: true);
    }

    /// <summary>
    /// Whether a segment passes for a translation: not empty where the source was not, and see
    /// <see cref="OpenAiContentCheck"/>.
    /// </summary>
    private static bool Passes(string source, string translation, string targetTag) =>
        (translation.Length > 0 || source.Trim().Length == 0)
        && OpenAiContentCheck.Passes(source, translation, targetTag);

    /// <summary>
    /// Sends each of <paramref name="indices"/> alone, at most <see cref="MaxConcurrentRequests"/>
    /// at a time.
    /// </summary>
    /// <param name="resend">
    /// A second try at segments that already have a translation: whatever comes back replaces it,
    /// and a failure leaves it as it was.
    /// </param>
    private async Task SinglesAsync(Job job, IReadOnlyList<int> indices, bool resend)
    {
        await Parallel.ForEachAsync(
            indices,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentRequests,
                CancellationToken = job.Token,
            },
            async (index, token) =>
            {
                if (!resend && job.Translated == 0 && job.Failed >= MaxConcurrentRequests)
                {
                    job.Outcomes[index] = OpenAiChatOutcome.NotSent;
                    return;
                }

                try
                {
                    job.Succeed(index, await SingleAsync(job.Texts[index], job.Request, token), alreadyCounted: resend);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    if (!resend) job.Fail(index, ex);
                }
            });
    }

    /// <summary>One text, alone, as the one segment of its own request.</summary>
    /// <remarks>
    /// An answer with no tag at all is taken as the translation itself. A model asked for one line
    /// that answers with the line and no markup has still translated it, and turning that into a
    /// failure would be worse than the request it replaced.
    /// </remarks>
    private async Task<string> SingleAsync(string text, OpenAiChatRequest request, CancellationToken token)
    {
        var content = await SendAsync(request.Prompt.Build([text]), request, stream: false, null, token);
        var answer = OpenAiAnswer.Read(content, 1);

        var translation = answer.Complete ? answer.Translations[0]
            : answer.Tagged ? null
            : StripThinking(content);

        return string.IsNullOrEmpty(translation)
            ? throw new OpenAiChatException(OpenAiChatFailure.NoTranslation)
            : translation;
    }

    /// <summary>Sends one request and returns the answer's content, thinking and all.</summary>
    /// <param name="onPiece">
    /// Told each piece of a streamed answer as it arrives. A server that answers a streamed request
    /// with a whole completion is read as one — streaming is a request, not a requirement.
    /// </param>
    private async Task<string> SendAsync(
        string message, OpenAiChatRequest request, bool stream, Action<string>? onPiece, CancellationToken cancellationToken)
    {
        // A dictionary rather than an anonymous type because the sampling fields are conditional.
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(message),
        };
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["top_p"] = topP;
        if (request.Seed is { } seed) payload["seed"] = seed;
        if (request.ReasoningEffort is { } effort) payload["reasoning_effort"] = effort;
        payload["stream"] = stream;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var key = request.ApiKey.Trim();
        if (key.Length > 0)
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // A timeout of our own rather than the HttpClient's: that one surfaces as a cancellation,
        // which every caller rightly takes for the user walking away and says nothing about.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit) timeout.CancelAfter(limit);

        try
        {
            using var response = await http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                throw new OpenAiChatException(OpenAiChatFailure.Rejected, response.StatusCode, ReadError(error));
            }

            var content = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
                ? await ReadEventsAsync(response, onPiece, timeout.Token)
                : ReadCompletion(await response.Content.ReadAsStringAsync(timeout.Token));

            if (StripThinking(content).Length == 0)
                throw new OpenAiChatException(OpenAiChatFailure.NoTranslation);
            return content;
        }
        catch (OperationCanceledException ex) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new OpenAiChatException(OpenAiChatFailure.TimedOut, inner: ex);
        }
    }

    /// <summary>The content of a whole completion.</summary>
    private static string ReadCompletion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadContent(document.RootElement.GetProperty("choices")[0].GetProperty("message"));
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new OpenAiChatException(OpenAiChatFailure.Unparsable, inner: ex);
        }
    }

    /// <summary>
    /// The content of a streamed completion, each piece told to <paramref name="onPiece"/> as it
    /// comes.
    /// </summary>
    /// <remarks>
    /// Reasoning sent in a field of its own is not content and is skipped. An error sent part way
    /// through, in the body of an answer that started out fine, is a refusal like any other: the
    /// server's own words are the only thing that says what went wrong.
    /// </remarks>
    private static async Task<string> ReadEventsAsync(
        HttpResponseMessage response, Action<string>? onPiece, CancellationToken token)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token), Encoding.UTF8);
        var content = new StringBuilder();

        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            string? piece;
            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out _))
                    throw new OpenAiChatException(OpenAiChatFailure.Rejected, response.StatusCode, ReadError(data));

                piece = root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var text)
                        && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
            }
            catch (JsonException ex)
            {
                throw new OpenAiChatException(OpenAiChatFailure.Unparsable, inner: ex);
            }

            if (string.IsNullOrEmpty(piece)) continue;
            content.Append(piece);
            onPiece?.Invoke(piece);
        }

        return content.ToString();
    }

    /// <summary>
    /// The messages for one request: the whole instruction, texts included, as the one user message.
    /// </summary>
    /// <remarks>
    /// No system message: where the instruction sits made no measured difference, and a system turn
    /// is what some models' templates have nowhere to put — see <see cref="OpenAiPrompt"/>.
    /// </remarks>
    internal static object[] BuildMessages(string message) => [new { role = "user", content = message }];

    /// <summary>The answer with the model's thinking taken out.</summary>
    /// <remarks>
    /// <para>Three shapes, taken in this order. Whole blocks first, wherever they are. Then a closing
    /// tag with nothing opening it: a chat template that writes the opening tag itself hands the
    /// model a reply that already starts inside the thinking, so everything up to that first closing
    /// tag is thinking. Then an opening tag that never closes, which is thinking the token limit cut
    /// off — none of it is translation, and an answer that is nothing else comes out empty, which is
    /// <see cref="OpenAiChatFailure.NoTranslation"/>.</para>
    ///
    /// <para>The last only when the answer begins with it. A translation that merely mentions the
    /// tag somewhere in its text is far likelier than thinking that starts part way through an
    /// answer, and taking everything after it would cut a real translation short.</para>
    /// </remarks>
    internal static string StripThinking(string value)
    {
        value = ThinkingBlock.Replace(value, "");
        value = UpToClosingTag.Replace(value, "", 1);
        value = UnclosedLeadingBlock.Replace(value, "", 1);
        return value.Trim();
    }

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
