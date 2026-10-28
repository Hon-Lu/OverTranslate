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
/// <param name="Batch">
/// Whether a call's texts go as few requests as fit rather than one each — see
/// <see cref="OpenAiChatTranslator.TranslateAsync"/>.
/// </param>
public sealed record OpenAiChatRequest(
    Uri Endpoint,
    string Model,
    string ApiKey,
    OpenAiPrompt Prompt,
    double? Temperature = null,
    double? TopP = null,
    int? Seed = null,
    string? ReasoningEffort = null,
    TimeSpan? Timeout = null,
    int ContextTokens = OpenAiChatTranslator.DefaultContextTokens,
    bool Batch = false);

/// <summary>What one call knows beyond its texts.</summary>
/// <param name="History">Earlier lines of the same session, oldest first — see <see cref="OpenAiPrompt.Build"/>.</param>
/// <param name="Screen">
/// Every line on the same screen in screen order, the texts being sent among them, those already
/// translated with their translation; null for a screen that is the texts alone. Only the
/// translated lines are sent, after the history.
/// </param>
public sealed record OpenAiChatContext(
    IReadOnlyList<OpenAiTranslationPair>? History = null,
    IReadOnlyList<OpenAiScreenLine>? Screen = null);

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
/// after another, each request carrying what is already translated.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="ITextTranslator"/>, and not a <see cref="BatchTranslator"/>. The batch
/// base cuts long texts into pieces; a language model wants every text whole, and what was
/// translated around it — the whole point of asking a model rather than an engine. And the instruction is written by
/// the application, naming languages the way the interface does, so there is no language code for
/// this to be given.</para>
///
/// <para>Measured before it was written: a text alone named the same thing five ways across a page
/// where a screen in one request named it one way, and the translations before it carried in each
/// request got that back. How each step falls back is what those measurements settled — see
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

    /// <summary>How many requests are in flight at once under a template with no context.</summary>
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
    /// <para>Each text is its own request, sent once the one before it is answered, carrying the
    /// session's history, the lines on screen already translated, and the call's translations so far
    /// — see <see cref="OpenAiPrompt.Build"/>. Never the text itself nor the lines still to come: what
    /// is not translated yet is not context. One after another rather than many at once because each
    /// carries the translations before it, and because a local server runs one request at a time
    /// anyway. Not streamed: a streamed answer took as long in all and read the same.</para>
    ///
    /// <para>A text with no letter in it — a number, a date, a symbol — is not sent: it is its own
    /// translation, as the model gave back nearly every one measured.</para>
    ///
    /// <para>An answer is taken whatever it says. Only one that cannot be read — empty, nothing but
    /// thinking — or a request that fails in a way the text could have caused is sent again, once,
    /// alone with no context; a text already sent alone is not sent the same again. Nothing is
    /// judged by what the translation says: a check of that kind is a guess about the language, and a
    /// guess that is wrong costs a request on every screen.</para>
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
    /// <para>A template with no <c>{context}</c> — see <see cref="OpenAiPrompt.HasContext"/> — has
    /// nothing to carry from one text to the next: every text goes alone, up to
    /// <see cref="MaxConcurrentRequests"/> at a time, and <paramref name="context"/> is not sent.</para>
    ///
    /// <para><see cref="OpenAiChatRequest.Batch"/> sends the texts as few requests as fit instead,
    /// whatever the template — see <see cref="BatchAsync"/>. Fewer requests is what a hosted server
    /// wants, one that answers many at once and counts them; a local one runs one at a time, and
    /// measured there a batch was no faster.</para>
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

        if (request.Batch)
            await BatchAsync(job);
        else if (request.Prompt.HasContext)
            await SegmentsAsync(job);
        else
            await AlonesAsync(job);

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
    /// ready too.</para>
    ///
    /// <para>Whatever comes back is not read. It throws as <see cref="TranslateAsync"/> does — a
    /// model answering with reasoning only has nothing for this to throw away and fails as
    /// <see cref="OpenAiChatFailure.NoTranslation"/> — and the caller decides what that means.</para>
    /// </remarks>
    public async Task WarmUpAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default) =>
        await SendAsync(BuildMessages(request.Prompt.Build(WarmUpText)), request, cancellationToken, maxTokens: 1);

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
        private int _again;

        /// <summary>How many texts that were sent came back translated.</summary>
        public int Translated => Volatile.Read(ref _translated);
        public int Failed => Volatile.Read(ref _failed);

        /// <summary>How many requests were sent again — see <see cref="OpenAiChatTranslator.TranslateAsync"/>.</summary>
        public int Again => Volatile.Read(ref _again);

        public void CountAgain() => Interlocked.Increment(ref _again);

        /// <summary>Whether no more requests should be sent — see <see cref="OpenAiChatTranslator.TranslateAsync"/>.</summary>
        public bool Stopped => Translated == 0 && Failed >= MaxFailuresBeforeAnyTranslation;

        /// <param name="sent">False for a text that is its own translation and was never asked for.</param>
        public void Succeed(int index, string translation, bool sent = true)
        {
            Outcomes[index] = new OpenAiChatOutcome(translation, null);
            if (sent) Interlocked.Increment(ref _translated);
        }

        public void Fail(int index, Exception error)
        {
            Outcomes[index] = new OpenAiChatOutcome(null, error);
            Interlocked.Increment(ref _failed);
        }
    }

    /// <summary>Every text of the call, one after another — see <see cref="TranslateAsync"/>.</summary>
    private async Task SegmentsAsync(Job job)
    {
        var watch = Stopwatch.StartNew();
        var sent = new List<int>();
        for (var i = 0; i < job.Texts.Count; i++)
        {
            if (HasLetters(job.Texts[i])) sent.Add(i);
            else job.Succeed(i, job.Texts[i], sent: false);
        }

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
                var earlier = Earlier(job.Context.History, job.Context.Screen, soFar);
                var translation = await OnceMoreAsync(job, Message(job.Request, text, earlier), text, job.Token);
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

        // Counts only, never the text, so this stays in the shipped log: it is what explains a screen
        // that took far longer than usual.
        Log.Info("OpenAI 相容翻譯：{Count} 段逐段完成（{Skipped} 段沒有文字不送），{Elapsed} ms，重送 {Again} 段",
            sent.Count, job.Texts.Count - sent.Count, watch.ElapsedMilliseconds, job.Again);
    }

    /// <summary>
    /// <paramref name="message"/>, and when it fails in a way the text could have caused, the text
    /// again alone with no context — see <see cref="TranslateAsync"/>.
    /// </summary>
    /// <param name="ofOne">
    /// Whether <paramref name="message"/> is a batch of one — see <see cref="BatchOfOne"/> — and is
    /// sent again as one.
    /// </param>
    /// <exception cref="Exception">
    /// The first request's failure when the second fails too in a way the text could have caused;
    /// otherwise the second's.
    /// </exception>
    private async Task<string> OnceMoreAsync(Job job, string message, string text, CancellationToken token, bool ofOne = false)
    {
        try
        {
            return await SegmentAsync(message, text, job.Request, token, ofOne);
        }
        catch (Exception ex) when (MightBeTheTexts(ex) && (ex is not OperationCanceledException || !token.IsCancellationRequested))
        {
            var alone = ofOne ? job.Request.Prompt.BuildBatch([text]) : job.Request.Prompt.Build(text);
            if (alone == message) throw;

            job.CountAgain();
            try
            {
                return await SegmentAsync(alone, text, job.Request, token, ofOne);
            }
            catch (Exception again) when (MightBeTheTexts(again) && (again is not OperationCanceledException || !token.IsCancellationRequested))
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }

    /// <summary>How many texts one batch request carries at most.</summary>
    /// <remarks>
    /// Past this a local model's answer, one tag to a text, starts losing numbers; and the longer
    /// one answer is, the longer the screen waits for any of it.
    /// </remarks>
    internal const int MaxBatchTexts = 40;

    /// <summary>
    /// The call's texts as few requests as fit, then each that did not come back as a batch of one —
    /// see <see cref="TranslateAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>Each request is as many texts in a row as <see cref="BatchSize"/> fits, carrying, when
    /// the template has a place for it, what one text at a time carries: the history, the lines on
    /// screen already translated, and what earlier requests of the call translated.</para>
    ///
    /// <para>Only what the answer gave no number for is sent again — on its own, as a batch of one
    /// with the same template and the context, up to <see cref="MaxConcurrentRequests"/> at once
    /// since none of them carries another's answer. The same template so that whatever it says
    /// still fits what is sent: a batch template's line about numbers, or a template written for a
    /// text at a time, whose batches come back with no numbers at all and so go wholly this way. An
    /// answer that has a number is taken whatever it says, as for one text at a time.</para>
    ///
    /// <para>A batch that fails in a way its texts could have caused is sent as batches of one
    /// instead; one that no text could have caused stops the call, as for one text.</para>
    /// </remarks>
    private async Task BatchAsync(Job job)
    {
        var watch = Stopwatch.StartNew();
        var sent = new List<int>();
        for (var i = 0; i < job.Texts.Count; i++)
        {
            if (HasLetters(job.Texts[i])) sent.Add(i);
            else job.Succeed(i, job.Texts[i], sent: false);
        }

        var prompt = job.Request.Prompt;
        var done = new List<OpenAiTranslationPair>();
        List<OpenAiTranslationPair>? Carried() =>
            prompt.HasContext ? Earlier(job.Context.History, job.Context.Screen, done) : null;

        var again = new List<int>();
        var batches = 0;
        Exception? stop = null;

        for (var start = 0; start < sent.Count && stop is null;)
        {
            var rest = sent.Skip(start).Select(i => job.Texts[i]).ToList();
            var (count, carried) = BatchSize(job.Request, rest, Carried());
            var batch = sent.GetRange(start, count);
            start += count;
            if (count == 1)
            {
                again.Add(batch[0]);
                continue;
            }

            try
            {
                batches++;
                var texts = batch.Select(i => job.Texts[i]).ToList();
                var answer = await SendAsync(BuildMessages(prompt.BuildBatch(texts, carried)), job.Request, job.Token);
                var read = OpenAiAnswer.ReadNumbered(answer, count);
                for (var k = 0; k < count; k++)
                {
                    if (read[k] is not { } translation)
                    {
                        again.Add(batch[k]);
                        continue;
                    }

                    job.Succeed(batch[k], translation);
                    done.Add(new OpenAiTranslationPair(texts[k], translation));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !job.Token.IsCancellationRequested)
            {
                if (MightBeTheTexts(ex))
                {
                    again.AddRange(batch);
                    continue;
                }

                stop = ex;
                foreach (var index in batch) job.Fail(index, ex);
                foreach (var index in sent.Skip(start).Concat(again)) job.Outcomes[index] = OpenAiChatOutcome.NotSent;
                Log.Info("OpenAI 相容翻譯：批次失敗（{Reason}），不再送其餘 {Rest} 段", Reason(ex), sent.Count - start + again.Count);
            }
        }

        if (stop is null)
            await OneByOneAsync(job, again, Carried());

        // Counts only, never the text, so this stays in the shipped log: it is what explains a screen
        // that took far longer than usual.
        Log.Info("OpenAI 相容翻譯：{Count} 段批次完成（{Batches} 個批次請求，{Skipped} 段沒有文字不送），{Elapsed} ms，缺號一段一批 {Missing} 段，重送 {Again} 段",
            sent.Count, batches, job.Texts.Count - sent.Count, watch.ElapsedMilliseconds, stop is null ? again.Count : 0, job.Again);
    }

    /// <summary>
    /// Each of <paramref name="again"/> as a batch of one with <paramref name="earlier"/> as its
    /// context, at most <see cref="MaxConcurrentRequests"/> at a time — see <see cref="BatchAsync"/>.
    /// </summary>
    /// <param name="earlier">Null under a template with no context.</param>
    /// <remarks>
    /// One that cannot be read, or fails in a way the text could have caused, is sent again once as
    /// a batch of one with no context, as one text at a time is.
    /// </remarks>
    private async Task OneByOneAsync(Job job, List<int> again, IReadOnlyList<OpenAiTranslationPair>? earlier)
    {
        var unreachable = false;
        await Parallel.ForEachAsync(
            again,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests, CancellationToken = job.Token },
            async (index, token) =>
            {
                if (Volatile.Read(ref unreachable) || job.Stopped)
                {
                    job.Outcomes[index] = OpenAiChatOutcome.NotSent;
                    return;
                }

                var text = job.Texts[index];
                try
                {
                    var message = BatchOfOne(job.Request, text, earlier ?? []);
                    job.Succeed(index, await OnceMoreAsync(job, message, text, token, ofOne: true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    if (!MightBeTheTexts(ex)) Volatile.Write(ref unreachable, true);
                    job.Fail(index, ex);
                }
            });
    }

    /// <summary>
    /// How many of <paramref name="texts"/>, from the first, the next batch carries, and the
    /// translations it carries them with.
    /// </summary>
    /// <remarks>
    /// <para>As many as fit, up to <see cref="MaxBatchTexts"/>: the message's characters, plus 32,
    /// plus twice the texts' characters — room for the answer — against
    /// <see cref="OpenAiChatRequest.ContextTokens"/>, as <see cref="Message"/> counts one text.</para>
    ///
    /// <para>When not even two fit with the translations, with the newer half of them, and so on down
    /// to none — as a session's history is cut. One that still does not fit with another is a text
    /// alone: it goes as one text at a time sends it, cut to fit there.</para>
    /// </remarks>
    internal static (int Count, IReadOnlyList<OpenAiTranslationPair>? Earlier) BatchSize(
        OpenAiChatRequest request,
        IReadOnlyList<string> texts,
        IReadOnlyList<OpenAiTranslationPair>? earlier)
    {
        bool Fits(int count, IReadOnlyList<OpenAiTranslationPair>? carried)
        {
            var some = texts.Take(count).ToList();
            return request.Prompt.BuildBatch(some, carried).Length + 32 + 2 * some.Sum(text => text.Length)
                <= request.ContextTokens;
        }

        if (texts.Count < 2) return (texts.Count, earlier);

        var carried = earlier;
        while (!Fits(2, carried))
        {
            if (carried is not { Count: > 0 }) return (1, earlier);
            carried = carried.Count > 1 ? carried.Skip(carried.Count / 2).ToList() : null;
        }

        var count = 2;
        while (count < texts.Count && count < MaxBatchTexts && Fits(count + 1, carried)) count++;
        return (count, carried);
    }

    /// <summary>Whether <paramref name="text"/> has a letter of any script in it.</summary>
    internal static bool HasLetters(string text) => text.EnumerateRunes().Any(Rune.IsLetter);

    /// <summary>
    /// What a request carries as translations done before: the history, then the lines on screen
    /// already translated, then <paramref name="soFar"/> — a source that is there already only once,
    /// the first time.
    /// </summary>
    /// <remarks>
    /// In that order because it is the order that changes least from one request to the next — see
    /// <see cref="OpenAiPrompt.Build"/>. Lines on screen with no letter in them say nothing a
    /// translation could use.
    /// </remarks>
    internal static List<OpenAiTranslationPair> Earlier(
        IReadOnlyList<OpenAiTranslationPair>? history,
        IReadOnlyList<OpenAiScreenLine>? screen,
        IEnumerable<OpenAiTranslationPair> soFar)
    {
        var seen = new HashSet<string>();
        var onScreen = (screen ?? [])
            .Where(line => line.Translation is not null && HasLetters(line.Source))
            .Select(line => new OpenAiTranslationPair(line.Source, line.Translation!));
        return [.. (history ?? []).Concat(onScreen).Concat(soFar).Where(pair => seen.Add(pair.Source))];
    }

    /// <summary>The message for <paramref name="text"/>, cut to fit <see cref="OpenAiChatRequest.ContextTokens"/>.</summary>
    /// <param name="earlier">What <see cref="Earlier"/> made.</param>
    /// <remarks>
    /// <para>The budget is the message's characters, plus 32, plus twice the text's characters — room
    /// for the answer — against the context. Measured against what a local server counted on some
    /// seven thousand requests, it never came out short. A model's own system prompt, set up on the
    /// server, is the one thing it cannot see.</para>
    ///
    /// <para>Over budget, the oldest translation goes first, one at a time, so what is left is what
    /// came nearest the text. The text and the template stay: a message that still does not fit is
    /// sent as it is, and is the text's own length.</para>
    /// </remarks>
    internal static string Message(OpenAiChatRequest request, string text, IReadOnlyList<OpenAiTranslationPair> earlier) =>
        CutToFit(request, text, earlier, kept => request.Prompt.Build(text, kept));

    /// <summary>
    /// <paramref name="text"/> as a batch of one — see <see cref="OpenAiPrompt.BuildBatch"/> — cut to
    /// fit as <see cref="Message"/> cuts one text.
    /// </summary>
    internal static string BatchOfOne(OpenAiChatRequest request, string text, IReadOnlyList<OpenAiTranslationPair> earlier) =>
        CutToFit(request, text, earlier, kept => request.Prompt.BuildBatch([text], kept));

    private static string CutToFit(
        OpenAiChatRequest request, string text, IReadOnlyList<OpenAiTranslationPair> earlier,
        Func<List<OpenAiTranslationPair>, string> build)
    {
        var kept = earlier.ToList();
        var message = build(kept);
        while (message.Length + 32 + 2 * text.Length > request.ContextTokens && kept.Count > 0)
        {
            kept.RemoveAt(0);
            message = build(kept);
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
    /// One request for <paramref name="text"/>, read back: see <see cref="OpenAiAnswer.Read"/> for the
    /// application's template, the whole answer less its thinking for the user's own.
    /// </summary>
    /// <param name="ofOne">
    /// Whether <paramref name="message"/> is a batch of one: its answer is read by number first —
    /// see <see cref="OpenAiAnswer.ReadOfOne"/>.
    /// </param>
    private async Task<string> SegmentAsync(
        string message, string text, OpenAiChatRequest request, CancellationToken token, bool ofOne = false)
    {
        var answer = await SendAsync(BuildMessages(message), request, token);
        var translation = ofOne
            ? OpenAiAnswer.ReadOfOne(answer, text, request.Prompt.IsDefault)
            : request.Prompt.IsDefault ? OpenAiAnswer.Read(answer, text) : StripThinking(answer);
        return translation is { Length: > 0 } ? translation : throw new OpenAiChatException(OpenAiChatFailure.NoTranslation);
    }

    /// <summary>
    /// Every text alone, with no context, at most <see cref="MaxConcurrentRequests"/> at a time.
    /// </summary>
    private async Task AlonesAsync(Job job)
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
                    var text = job.Texts[index];
                    job.Succeed(index, await SegmentAsync(job.Request.Prompt.Build(text), text, job.Request, token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    job.Fail(index, ex);
                }
            });
    }

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
