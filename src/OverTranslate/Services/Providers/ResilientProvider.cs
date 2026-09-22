using NLog;

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
/// Wraps several keyless GTranslate engines and serves each block with a hedged-request
/// strategy: the primary engine starts first, and if it has not answered within
/// <see cref="_hedgeDelay"/> a backup engine is launched in parallel — whichever succeeds
/// first wins. This turns the per-block latency into roughly the *fastest* engine instead of
/// being stuck on whichever free endpoint happens to be throttled/slow that moment.
///
/// A hard <see cref="_timeout"/> per block guarantees the UI never hangs indefinitely; if every
/// engine fails the original text is returned untranslated so the rest of the batch still shows.
/// </summary>
public class ResilientProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly GTranslateProvider[] _engines;
    private readonly TimeSpan _hedgeDelay;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Which engine(s) actually produced the most recent batch. Useful for surfacing the real
    /// translation source to the user (toolbar badge) / harness.
    /// </summary>
    public EngineUsage? LastUsage { get; private set; }

    /// <summary>Friendly per-engine breakdown of the most recent batch (e.g. "Bing×3, Google×1").</summary>
    public string LastBatchSummary => LastUsage?.Summary ?? "";

    // Friendly names mirror the toolbar provider dropdown (LanguageData.Providers) so the badge
    // matches what the user actually picked — note the two Google engines are distinct (Web vs RPC).
    private static string Friendly(string engineName) => engineName switch
    {
        "GoogleTranslator"    => "Google (Web)",
        "GoogleTranslator2"   => "Google (RPC)",
        "BingTranslator"      => "Bing",
        "MicrosoftTranslator" => "Microsoft",
        "(none)"              => LocalizationService.Get("S.Error.NotTranslated"),
        _                     => engineName,
    };

    public ResilientProvider(
        IReadOnlyList<GTranslateProvider> engines,
        TimeSpan? hedgeDelay = null,
        TimeSpan? timeout = null)
    {
        if (engines.Count == 0) throw new ArgumentException("At least one engine is required.", nameof(engines));
        _engines    = [.. engines];
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

        var tasks   = blocks.Select(b => TranslateBlockHedged(b.Text, sourceLang, targetLang, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var langVotes   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var engineVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var translated  = new List<TranslatedBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var (translation, detLang, engine) = results[i];
            if (!string.IsNullOrEmpty(detLang))
                langVotes[detLang] = langVotes.GetValueOrDefault(detLang) + 1;
            engineVotes[engine] = engineVotes.GetValueOrDefault(engine) + 1;
            translated.Add(new TranslatedBlock(blocks[i].Text, translation, blocks[i].Bounds, blocks[i].Lines, blocks[i].RenderGlyphHeight)
                { RunsAcross = blocks[i].RunsAcross });
        }

        string primary   = _engines[0].Name;
        var ordered      = engineVotes.OrderByDescending(kv => kv.Value).ToList();
        string summary   = string.Join(", ", ordered.Select(kv => $"{Friendly(kv.Key)}×{kv.Value}"));

        // The badge should name the *backup* that stepped in, never the user's own pick — otherwise
        // "selected Bing → ⚡由 Bing" looks self-contradictory. Prefer a real backup over "(none)".
        var backups       = ordered.Where(kv => kv.Key != primary).ToList();
        var backupEngine  = backups.FirstOrDefault(kv => kv.Key != "(none)").Key
                            ?? backups.FirstOrDefault().Key;
        bool fallbackUsed = backups.Count > 0;
        LastUsage = new EngineUsage(summary, Friendly(backupEngine ?? ""), Friendly(primary), fallbackUsed);

        Log.Info("翻譯完成：{Count} 個區塊，實際使用引擎 {Engines}（主力 {Primary}）",
            blocks.Count, summary, Friendly(primary));

        string detectedLang = langVotes.Count > 0 ? langVotes.MaxBy(kv => kv.Value).Key : "";
        return (translated, detectedLang);
    }

    private async Task<(string Translation, string DetectedLang, string Engine)> TranslateBlockHedged(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken)
    {
        var pending   = new List<Task<(string Translation, string DetectedLang, string Engine)>>();
        int next      = 0;
        // Tied to the token so an abandoned batch does not leave timers armed for the full timeout.
        var deadline  = Task.Delay(_timeout, cancellationToken);

        StartNext(); // always launch the primary engine immediately

        while (pending.Count > 0)
        {
            // Launch a backup once the hedge delay elapses (until engines run out).
            Task trigger = next < _engines.Length ? Task.Delay(_hedgeDelay, cancellationToken) : deadline;

            var finished = await Task.WhenAny(pending.Cast<Task>().Append(trigger));

            // Checked before interpreting the result: cancellation must propagate rather than be
            // mistaken for "every engine failed", which would silently return the untranslated text
            // as if it were a real answer.
            cancellationToken.ThrowIfCancellationRequested();

            if (finished == trigger)
            {
                if (next < _engines.Length) { StartNext(); continue; } // hedge: add a backup
                break;                                                 // global deadline hit
            }

            var t = (Task<(string, string, string)>)finished;
            pending.Remove(t);

            if (t.Status == TaskStatus.RanToCompletion)
            {
                Observe(pending); // let the slower engines die quietly in the background
                return t.Result;  // first success wins
            }

            _ = t.Exception;      // mark the failed engine's exception as observed

            if (next < _engines.Length)
                StartNext();      // an engine failed early — bring the next one online
        }

        Observe(pending);
        cancellationToken.ThrowIfCancellationRequested();
        return (text, "", "(none)"); // every engine failed/timed out — fall back to the original text

        void StartNext()
        {
            var engine = _engines[next++];
            pending.Add(RunAsync(engine));
        }

        async Task<(string, string, string)> RunAsync(GTranslateProvider engine)
        {
            var (translation, detLang) = await engine.TranslateOneAsync(text, sourceLang, targetLang, cancellationToken);
            return (translation, detLang, engine.Name);
        }

        static void Observe(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks)
                t.ContinueWith(static x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
