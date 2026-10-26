using System.Diagnostics;
using System.Net;
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
/// How many tokens the model can be assumed to read, for cutting what a request carries to fit —
/// see <see cref="OpenAiChatTranslator.Message"/>.
/// </param>
/// <param name="Custom">
/// The user's own instruction, sent instead of <paramref name="Prompt"/>: each text alone, with
/// nothing else — see <see cref="OpenAiChatTranslator.TranslateAsync"/>. Null for the application's.
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
    int ContextTokens = OpenAiChatTranslator.DefaultContextTokens,
    OpenAiCustomPrompt? Custom = null);

/// <summary>An instruction the user wrote: a system message and what goes in front of the text.</summary>
/// <param name="System">A message of its own, or no system message at all when empty.</param>
/// <param name="User">Written in front of the text, in the same message, as it is — blank line and all.</param>
public sealed record OpenAiCustomPrompt(string System, string User);

/// <summary>What one call knows beyond its texts.</summary>
/// <param name="History">Earlier lines of the same session, oldest first — see <see cref="OpenAiPrompt.Build"/>.</param>
/// <param name="Reference">Lines on the same screen that are already translated.</param>
public sealed record OpenAiChatContext(
    IReadOnlyList<OpenAiTranslationPair>? History = null,
    IReadOnlyList<OpenAiTranslationPair>? Reference = null);

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
/// Translates through an OpenAI-compatible Chat Completions endpoint: one text to a request, one
/// after another, each request carrying the rest of the screen and what is already translated.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="ITextTranslator"/>, and not a <see cref="BatchTranslator"/>. The batch
/// base cuts long texts into pieces; a language model wants every text whole, and the screen around
/// it — the whole point of asking a model rather than an engine. And the instruction is written by
/// the application, naming languages the way the interface does, so there is no language code for
/// this to be given.</para>
///
/// <para>Measured before it was written: a text alone named the same thing five ways across a page
/// where a screen in one request named it one way, and the screen's texts and translations carried
/// in each request got that back. How each step falls back is what those measurements settled — see
/// <see cref="TranslateAsync"/>.</para>
///
/// <para>Failures the server answers with are <see cref="OpenAiChatException"/>, and so is a request
/// that ran past <see cref="OpenAiChatRequest.Timeout"/>. One that never got an answer otherwise —
/// refused connection, cancellation — goes up as the <see cref="HttpClient"/> threw it: which of
/// those it was is what the user needs to hear, and the application already words them.</para>
/// </remarks>
public sealed class OpenAiChatTranslator(HttpClient http)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How many requests are in flight at once under the user's own instruction.</summary>
    public const int MaxConcurrentRequests = 8;

    /// <summary>
    /// How many failed requests in a row, with nothing translated yet, stop the rest being sent.
    /// </summary>
    /// <remarks>
    /// A server that answers nothing would otherwise be waited on one timeout per text instead of a
    /// few in all.
    /// </remarks>
    private const int MaxFailuresBeforeAnyTranslation = MaxConcurrentRequests;

    /// <summary>
    /// How many tokens a model is assumed to read when nothing says otherwise.
    /// </summary>
    /// <remarks>
    /// The context a local server gives a model it was not told the size of, on the older releases
    /// that gave the least; newer ones give more. Assumed rather than asked, because the
    /// OpenAI-compatible route has no way to ask or to set it. Past it, a local model is cut from the
    /// front without a word — the instruction is what goes first — and starts writing anything at
    /// all; a smaller assumption costs only some context left out of a request.
    /// </remarks>
    public const int DefaultContextTokens = 4096;

    private static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex UpToClosingTag = new(
        @"^.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex UnclosedLeadingBlock = new(
        @"^\s*<think(?:\s[^>]*)?>.*\z",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <returns>One outcome per text, in the order given.</returns>
    /// <exception cref="OpenAiChatException">
    /// Not one text that was sent was translated: the first text's failure, as it was thrown.
    /// </exception>
    /// <remarks>
    /// <para>Each text is its own request, sent once the one before it is answered — see
    /// <see cref="OpenAiPrompt.Build"/> for what each carries. One after another rather than many at
    /// once because each carries the translations before it, and because a local server runs one
    /// request at a time anyway: measured there, a screen sent this way took less time in all than
    /// the same screen as one tagged request. Not streamed: a streamed answer took as long in all
    /// and read the same.</para>
    ///
    /// <para>A text with no letter in it — a number, a date, a symbol — is not sent: it is its own
    /// translation, as the model gave back nearly every one measured, and it is left out of what the
    /// other requests carry.</para>
    ///
    /// <para>Once every text is in, each is checked — see <see cref="OpenAiContentCheck"/> — and each
    /// that fails is sent again once, alone, with no context: the request it came from would only
    /// come back the same under a fixed seed. What comes back is taken whatever it says: sending
    /// again was measured to rescue most of what it could, and the rest was the model's own
    /// limit.</para>
    ///
    /// <para>A request that fails in a way no text could have caused — no server answering at all, a
    /// key or an address or a model it refuses, a limit on how often it is asked — stops the rest
    /// being sent: they would only fail as many more times. Any other failure is that text's alone.
    /// So is a run of failures with nothing translated yet, once it reaches
    /// <see cref="MaxFailuresBeforeAnyTranslation"/>.</para>
    ///
    /// <para>One text failing does not take the others with it: which texts failed, and how, is in
    /// the outcomes for the caller to show. Every text that was sent failing is still thrown, as the
    /// first one's failure — a server that is not running, a wrong key, a model that is not there is
    /// one cause the user has to read.</para>
    ///
    /// <para>The caller's cancellation is not a failure of any text: it goes up as it always did.</para>
    ///
    /// <para>With <see cref="OpenAiChatRequest.Custom"/>, none of the above but the last two: every
    /// text goes alone under the user's instruction, up to <see cref="MaxConcurrentRequests"/> at a
    /// time, the whole answer less its thinking is the translation, and <paramref name="context"/> is
    /// not sent. Nothing is checked or sent again — what the instruction asks for is the user's to
    /// say, and a check written for the application's instruction would be wrong about theirs.</para>
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

        if (request.Custom is { } custom)
            await CustomsAsync(job, custom);
        else
            await SegmentsAsync(job);

        if (job.Translated == 0 && job.Outcomes.Any(outcome => !outcome.Translated))
        {
            var error = job.Outcomes.FirstOrDefault(outcome => outcome.Error is not null)?.Error
                ?? new OpenAiChatException(OpenAiChatFailure.NoTranslation);
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return job.Outcomes;
    }

    /// <summary>
    /// Sends the shortest request that still makes the server load the model: the instruction with
    /// one word in it, and an answer of one token.
    /// </summary>
    /// <remarks>
    /// <para>For the moment before the first real request, when nothing on screen has been read yet:
    /// a local model that is not in memory takes seconds to load on the request that finds it
    /// missing, and the server unloads it again after a few idle minutes without being asked — and
    /// cannot be asked not to through this route. Sent now, the load happens while the screen is
    /// still being read rather than in front of the first line.</para>
    ///
    /// <para>The instruction rather than a bare greeting because it is what every request after this
    /// one starts with: a server that keeps the start of the last request it read has that part
    /// ready too. The user's own, with <see cref="OpenAiChatRequest.Custom"/>.</para>
    ///
    /// <para>Whatever comes back is not read. It throws as <see cref="TranslateAsync"/> does — a
    /// model answering with reasoning only has nothing for this to throw away and fails as
    /// <see cref="OpenAiChatFailure.NoTranslation"/> — and the caller decides what that means.</para>
    /// </remarks>
    public async Task WarmUpAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default) =>
        await SendAsync(
            request.Custom is { } custom ? CustomMessages(custom, WarmUpText) : BuildMessages(request.Prompt.Build(WarmUpText)),
            request, cancellationToken, maxTokens: 1);

    /// <summary>The one word <see cref="WarmUpAsync"/> asks for.</summary>
    internal const string WarmUpText = "Hello";

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

        /// <summary>How many texts that were sent came back translated.</summary>
        public int Translated => Volatile.Read(ref _translated);
        public int Failed => Volatile.Read(ref _failed);

        /// <summary>Whether no more requests should be sent — see <see cref="OpenAiChatTranslator.TranslateAsync"/>.</summary>
        public bool Stopped => Translated == 0 && Failed >= MaxFailuresBeforeAnyTranslation;

        /// <param name="sent">False for a text that is its own translation and was never asked for.</param>
        public void Succeed(int index, string translation, bool sent = true)
        {
            var counted = Outcomes[index] is { Translated: true };
            Outcomes[index] = new OpenAiChatOutcome(translation, null);
            if (sent && !counted) Interlocked.Increment(ref _translated);
        }

        public void Fail(int index, Exception error)
        {
            Outcomes[index] = new OpenAiChatOutcome(null, error);
            Interlocked.Increment(ref _failed);
        }
    }

    /// <summary>Every text of the call, one after another, then the check — see <see cref="TranslateAsync"/>.</summary>
    private async Task SegmentsAsync(Job job)
    {
        var watch = Stopwatch.StartNew();
        var sent = new List<int>();
        for (var i = 0; i < job.Texts.Count; i++)
        {
            if (HasLetters(job.Texts[i])) sent.Add(i);
            else job.Succeed(i, job.Texts[i], sent: false);
        }

        var round = sent.Select(i => job.Texts[i]).ToList();
        var soFar = new List<OpenAiTranslationPair>();
        var unreachable = false;
        for (var k = 0; k < sent.Count; k++)
        {
            var index = sent[k];
            if (unreachable || job.Stopped)
            {
                job.Outcomes[index] = OpenAiChatOutcome.NotSent;
                continue;
            }

            var text = job.Texts[index];
            try
            {
                var message = Message(job.Request, text, job.Context, round, k, soFar);
                var translation = await SegmentAsync(message, text, job.Request, job.Token);
                job.Succeed(index, translation);
                soFar.Add(new OpenAiTranslationPair(text, translation));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !job.Token.IsCancellationRequested)
            {
                job.Fail(index, ex);
                if (MightBeTheTexts(ex)) continue;

                unreachable = true;
                Log.Info("OpenAI 相容翻譯：第 {Index} 段失敗（{Reason}），不再送其餘 {Rest} 段",
                    k + 1, Reason(ex), sent.Count - k - 1);
            }
        }

        var resend = sent.Where(i => !unreachable && job.Outcomes[i] is { Translation: { } translation }
            && !Passes(job.Texts[i], translation, job.Request.TargetTag)).ToList();

        // Counts only, never the text, so this stays in the shipped log: it is what explains a screen
        // that took far longer than usual.
        Log.Info("OpenAI 相容翻譯：{Count} 段逐段完成（{Skipped} 段沒有文字不送），{Elapsed} ms，內容檢查重送 {Resend} 段",
            sent.Count, job.Texts.Count - sent.Count, watch.ElapsedMilliseconds, resend.Count);

        foreach (var index in resend)
        {
            var text = job.Texts[index];
            try
            {
                job.Succeed(index, await SegmentAsync(job.Request.Prompt.Build(text), text, job.Request, job.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !job.Token.IsCancellationRequested)
            {
                // The first answer stands.
                if (!MightBeTheTexts(ex)) break;
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> has a letter of any script in it.</summary>
    internal static bool HasLetters(string text) => text.EnumerateRunes().Any(Rune.IsLetter);

    /// <summary>
    /// The message for the text at <paramref name="position"/> of <paramref name="round"/>, cut to fit
    /// <see cref="OpenAiChatRequest.ContextTokens"/>.
    /// </summary>
    /// <remarks>
    /// <para>The budget is the message's characters, plus 32, plus twice the text's characters — room
    /// for the answer — against the context. Measured against what a local server counted on some
    /// seven thousand requests, it never came out short. A model's own system prompt, set up on the
    /// server, is the one thing it cannot see.</para>
    ///
    /// <para>Over budget, what this round carries goes first, a line at a time — the round's text
    /// farthest from this one, or the oldest translation so far, whichever list is longer — so what
    /// is left is what sits nearest the text. Then the history, oldest first. The screen's own
    /// translated lines and the instruction stay: a message that still does not fit is sent as it
    /// is, and is the text's own length.</para>
    /// </remarks>
    internal static string Message(
        OpenAiChatRequest request,
        string text,
        OpenAiChatContext context,
        IReadOnlyList<string> round,
        int position,
        IReadOnlyList<OpenAiTranslationPair> soFar)
    {
        var history = context.History?.ToList() ?? [];
        var nearby = Enumerable.Range(0, round.Count).ToList();
        var done = soFar.ToList();

        string Build() => request.Prompt.Build(
            text, history, context.Reference, nearby.Select(i => round[i]).ToList(), done);

        var message = Build();
        while (message.Length + 32 + 2 * text.Length > request.ContextTokens)
        {
            if (nearby.Count > 1 && nearby.Count >= done.Count)
                nearby.Remove(nearby.MaxBy(i => Math.Abs(i - position)));
            else if (done.Count > 0)
                done.RemoveAt(0);
            else if (nearby.Count > 1)
                nearby.Remove(nearby.MaxBy(i => Math.Abs(i - position)));
            else if (history.Count > 0)
                history.RemoveAt(0);
            else
                break;

            message = Build();
        }

        return message;
    }

    /// <summary>
    /// Whether a failed request could have gone through with another text in it — and so whether
    /// the texts after it could still be translated.
    /// </summary>
    /// <remarks>
    /// No answer at all is a server that is not there, not running, or not reachable. The statuses
    /// are a key, an address or a model the server refuses whatever is asked, and asking too often —
    /// which more requests would only make worse. Every other refusal can be the text's own doing.
    /// </remarks>
    private static bool MightBeTheTexts(Exception error) => error switch
    {
        HttpRequestException => false,
        OpenAiChatException
        {
            Failure: OpenAiChatFailure.Rejected,
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests,
        } => false,
        _ => true,
    };

    /// <summary>A failure's kind for the log, with the status when the server gave one.</summary>
    private static string Reason(Exception error) => error switch
    {
        OpenAiChatException { StatusCode: { } status } chat => $"{chat.Failure} {(int)status}",
        OpenAiChatException chat => chat.Failure.ToString(),
        _ => error.GetType().Name,
    };

    /// <summary>
    /// Whether a segment passes for a translation: not empty where the source was not, and see
    /// <see cref="OpenAiContentCheck"/>.
    /// </summary>
    private static bool Passes(string source, string translation, string targetTag) =>
        (translation.Length > 0 || source.Trim().Length == 0)
        && OpenAiContentCheck.Passes(source, translation, targetTag);

    /// <summary>One request for <paramref name="text"/>, read back — see <see cref="OpenAiAnswer.Read"/>.</summary>
    private async Task<string> SegmentAsync(
        string message, string text, OpenAiChatRequest request, CancellationToken token) =>
        OpenAiAnswer.Read(await SendAsync(BuildMessages(message), request, token), text)
            ?? throw new OpenAiChatException(OpenAiChatFailure.NoTranslation);

    /// <summary>
    /// Every text alone under the user's instruction, at most <see cref="MaxConcurrentRequests"/> at
    /// a time.
    /// </summary>
    private async Task CustomsAsync(Job job, OpenAiCustomPrompt custom)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(0, job.Texts.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentRequests,
                CancellationToken = job.Token,
            },
            async (index, token) =>
            {
                if (job.Stopped)
                {
                    job.Outcomes[index] = OpenAiChatOutcome.NotSent;
                    return;
                }

                try
                {
                    job.Succeed(index, await CustomAsync(job.Texts[index], job.Request, custom, token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    job.Fail(index, ex);
                }
            });
    }

    /// <summary>One text under the user's instruction: the whole answer, less its thinking.</summary>
    private async Task<string> CustomAsync(
        string text, OpenAiChatRequest request, OpenAiCustomPrompt custom, CancellationToken token) =>
        StripThinking(await SendAsync(CustomMessages(custom, text), request, token));

    /// <summary>Sends one request and returns the answer's content, thinking and all.</summary>
    /// <param name="maxTokens">The longest answer to allow, or null to leave it to the server.</param>
    private async Task<string> SendAsync(
        object[] messages, OpenAiChatRequest request, CancellationToken cancellationToken, int? maxTokens = null)
    {
        // A dictionary rather than an anonymous type because the sampling fields are conditional.
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
        };
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["top_p"] = topP;
        if (request.Seed is { } seed) payload["seed"] = seed;
        if (request.ReasoningEffort is { } effort) payload["reasoning_effort"] = effort;
        if (maxTokens is { } most) payload["max_tokens"] = most;
        payload["stream"] = false;

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

            var content = ReadCompletion(await response.Content.ReadAsStringAsync(timeout.Token));

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
    /// The messages for one request: the whole instruction, texts included, as the one user message.
    /// </summary>
    /// <remarks>
    /// No system message: where the instruction sits made no measured difference, and a system turn
    /// is what some models' templates have nowhere to put — see <see cref="OpenAiPrompt"/>.
    /// </remarks>
    internal static object[] BuildMessages(string message) => [new { role = "user", content = message }];

    /// <summary>
    /// The messages for one text under the user's instruction: the system message when there is one,
    /// then the user message, the instruction straight in front of the text.
    /// </summary>
    internal static object[] CustomMessages(OpenAiCustomPrompt custom, string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var user = new { role = "user", content = custom.User + text };
        return custom.System.Length > 0 ? [new { role = "system", content = custom.System }, user] : [user];
    }

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
