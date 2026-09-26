using NLog;
using OverTranslate.Engines;

namespace OverTranslate.Services.Providers;

/// <summary>
/// Which engines actually served the most recent batch.
/// <paramref name="Summary"/> is a friendly per-engine breakdown (e.g. "Bing×3, Google×1"),
/// <paramref name="BackupEngine"/> is the friendly name of the backup that stepped in (the most-used
/// engine that is *not* the user's pick), <paramref name="Primary"/> is the friendly name of the
/// chosen engine, and <paramref name="FallbackUsed"/> is true when any block was served by something
/// other than the primary (or failed entirely).
/// </summary>
public sealed record EngineUsage(string Summary, string BackupEngine, string Primary, bool FallbackUsed);

/// <summary>
/// Serves a batch from the user's engine, and from the others only when it cannot.
/// </summary>
/// <remarks>
/// <para>The unit is a request, not a block. The primary engine says how it would divide the
/// batch (<see cref="ITextTranslator.Plan"/>) — for every engine but Bing that is the whole screen
/// in one request — and each of those groups is served as one: by one engine, entirely, or not at
/// all. That is what keeps a screen in one voice. It used to be each block on its own clock, and a
/// screen of twenty blocks sent as twenty requests would now and then have one of them answered by
/// a backup, in a different engine's wording (<c>.ai/translation-service-analysis/README.md</c>).</para>
///
/// <para>Per group, in order, each step starting when the one before has failed or been waiting
/// for <see cref="_hedgeDelay"/>, earlier steps left running and the first answer winning:</para>
/// <list type="number">
/// <item>the primary engine;</item>
/// <item>the primary again, as a fresh request. A slow or failed request is far more often that one
/// request than the engine as a whole, and asking again keeps the screen in the engine the user
/// chose — which a backup, however fast, cannot;</item>
/// <item>each backup in turn.</item>
/// </list>
///
/// <para>Slow and failing are therefore told apart without anyone having to classify an error: a
/// failure moves to the next step at once, a slow answer only after the hedge delay, and a backup is
/// two steps away either way. If every step has failed and <see cref="_timeout"/> has not passed,
/// the whole ladder is climbed once more from the primary — these endpoints' errors are mostly
/// passing ones. When the deadline is reached, whatever has not been answered is shown in its
/// original text and marked <see cref="TranslatedBlock.Untranslated"/>, so the rest of the screen
/// still shows and a caller that can retry knows which lines to.</para>
///
/// <para>Whatever is still running when a group is decided is cancelled — the requests really are
/// aborted now, which GTranslate could not do.</para>
/// </remarks>
public class ResilientProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ITextTranslator[] _engines;
    private readonly int[] _ladder;
    private readonly TimeSpan _hedgeDelay;
    private readonly TimeSpan _timeout;

    // Stands in for an engine name on a block that every engine failed to translate.
    private const string NoEngine = "(none)";

    /// <summary>
    /// Which engine(s) actually produced the most recent batch. Useful for surfacing the real
    /// translation source to the user (toolbar badge) / harness.
    /// </summary>
    public EngineUsage? LastUsage { get; private set; }

    /// <summary>Friendly per-engine breakdown of the most recent batch (e.g. "Bing×3, Google×1").</summary>
    public string LastBatchSummary => LastUsage?.Summary ?? "";

    // The engines' own names are the ones the provider dropdown shows (LanguageData.Providers), so
    // the badge matches what the user actually picked.
    private static string Friendly(string engineName) => engineName switch
    {
        NoEngine => LocalizationService.Get("S.Error.NotTranslated"),
        _        => engineName,
    };

    /// <param name="engines">The user's engine first, then the backups in the order to try them.</param>
    public ResilientProvider(
        IReadOnlyList<ITextTranslator> engines,
        TimeSpan? hedgeDelay = null,
        TimeSpan? timeout = null)
    {
        if (engines.Count == 0) throw new ArgumentException("At least one engine is required.", nameof(engines));
        _engines    = [.. engines];
        _ladder     = [0, .. Enumerable.Range(0, engines.Count)];
        _hedgeDelay = hedgeDelay ?? TimeSpan.FromSeconds(2.5);
        _timeout    = timeout    ?? TimeSpan.FromSeconds(12);
    }

    public bool RequiresApiKey => false;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var texts = blocks.Select(block => block.Text).ToList();
        var to    = EngineLanguage.ToEngine(targetLang);
        var from  = EngineLanguage.SourceToEngine(sourceLang);

        // One clock for the whole batch, as there always was: every group starts together.
        // Tied to the token so an abandoned batch does not leave a timer armed for the full timeout.
        var deadline = Task.Delay(_timeout, cancellationToken);

        var served = new (TextTranslation Answer, string Engine)?[texts.Count];
        var groups = _engines[0].Plan(texts);

        await Task.WhenAll(groups.Select(async group =>
        {
            var result = await TranslateGroupAsync(
                group.Select(i => texts[i]).ToList(), to, from, deadline, cancellationToken);
            if (result is not { } answered) return;

            for (var k = 0; k < group.Count; k++)
                served[group[k]] = (answered.Answers[k], answered.Engine);
        }));

        var engineVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var translated  = new List<TranslatedBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            // Nobody answered: the original text stands in, and says so.
            var (answer, engine) = served[i] ?? (new TextTranslation(blocks[i].Text, ""), NoEngine);
            engineVotes[engine] = engineVotes.GetValueOrDefault(engine) + 1;
            translated.Add(new TranslatedBlock(blocks[i].Text, answer.Text, blocks[i].Bounds, blocks[i].Lines, blocks[i].RenderGlyphHeight)
                { RunsAcross = blocks[i].RunsAcross, Untranslated = engine == NoEngine });
        }

        string primary  = _engines[0].Name;
        var ordered      = engineVotes.OrderByDescending(kv => kv.Value).ToList();
        string summary   = string.Join(", ", ordered.Select(kv => $"{Friendly(kv.Key)}×{kv.Value}"));

        // The badge should name the *backup* that stepped in, never the user's own pick — otherwise
        // "selected Bing → ⚡由 Bing" looks self-contradictory. Prefer a real backup over "(none)".
        var backups       = ordered.Where(kv => kv.Key != primary).ToList();
        var backupEngine  = backups.FirstOrDefault(kv => kv.Key != NoEngine).Key
                            ?? backups.FirstOrDefault().Key;
        bool fallbackUsed = backups.Count > 0;
        LastUsage = new EngineUsage(summary, Friendly(backupEngine ?? ""), Friendly(primary), fallbackUsed);

        Log.Info("翻譯完成：{Count} 個區塊分 {Groups} 組送出，實際使用引擎 {Engines}（主力 {Primary}）",
            blocks.Count, groups.Count, summary, Friendly(primary));

        return (translated, DetectedLanguage.Vote(served.Select(s => s?.Answer.DetectedLanguage ?? "")));
    }

    /// <summary>One group, up the ladder until something answers or the deadline passes.</summary>
    /// <returns>The answers and the engine that gave them, or null if nothing did in time.</returns>
    private async Task<(IReadOnlyList<TextTranslation> Answers, string Engine)?> TranslateGroupAsync(
        IReadOnlyList<string> texts, string to, string? from, Task deadline, CancellationToken cancellationToken)
    {
        // Cancelled the moment this group is decided, so the requests that lost are aborted
        // rather than left to finish for nobody.
        using var decided = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<(IReadOnlyList<TextTranslation>, string)>>();
        int next     = 0;
        bool retried = false;

        StartNext(); // always launch the primary engine immediately

        try
        {
            while (pending.Count > 0)
            {
                // The next step goes up after the hedge delay, until the ladder runs out.
                Task hedge = next < _ladder.Length ? Task.Delay(_hedgeDelay, cancellationToken) : deadline;

                var finished = await Task.WhenAny(pending.Cast<Task>().Append(hedge).Append(deadline));

                // Checked before interpreting the result: cancellation must propagate rather than be
                // mistaken for "every engine failed", which would silently return the untranslated text
                // as if it were a real answer.
                cancellationToken.ThrowIfCancellationRequested();

                if (finished == deadline) break;
                if (finished == hedge)
                {
                    StartNext();
                    continue;
                }

                var t = (Task<(IReadOnlyList<TextTranslation>, string)>)finished;
                pending.Remove(t);

                if (t.Status == TaskStatus.RanToCompletion)
                    return t.Result; // first success wins

                _ = t.Exception;     // mark the failed request's exception as observed

                if (next < _ladder.Length)
                    StartNext();     // failed early — the next step now, not after the delay
                else if (pending.Count == 0 && !retried)
                {
                    // Every step said no, and did so before the deadline — an error, not a hang, and
                    // errors from these free endpoints are mostly passing ones. Once more from the
                    // user's own engine, still under the same deadline: a group that would have been
                    // shown in its original text gets another chance, and the wait can never grow
                    // past what it already was. A deadline hit is not retried; that is the hang case,
                    // where asking again at once would only be waiting again.
                    retried = true;
                    next    = 0;
                    Log.Debug("每一步都失敗，於時限內從主力 {Primary} 重試一輪", _engines[0].Name);
                    StartNext();
                }
            }

            return null; // nothing answered in time — the caller shows the original text
        }
        finally
        {
            decided.Cancel();
            foreach (var t in pending)
                _ = t.ContinueWith(static x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        void StartNext()
        {
            var engine = _engines[_ladder[next++]];
            pending.Add(RunAsync(engine));
        }

        async Task<(IReadOnlyList<TextTranslation>, string)> RunAsync(ITextTranslator engine) =>
            (await engine.TranslateAsync(texts, to, from, decided.Token), engine.Name);
    }
}
