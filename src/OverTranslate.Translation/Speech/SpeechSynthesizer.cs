using System.Diagnostics;
using NLog;

namespace OverTranslate.Translation.Speech;

/// <summary>Reads a text aloud with the first engine that has a voice for it and answers.</summary>
/// <remarks>
/// <para>One after another as a rule: a press of the speaker is one sound, and asking four engines
/// at once for it would be four downloads to play one. The next engine is asked when the one before
/// has no voice for the language or fails — and, as a backstop, beside it when the one before has
/// not answered within <see cref="DefaultHedge"/>. The slowest ordinary answer measured from Taiwan
/// was 0.3 s (Bing's first, with its credentials, 2.8 s), so the backstop only goes up for a request
/// that is stuck; whichever then answers first is played.</para>
///
/// <para>A host that gave no answer at all — no connection, no handshake, no response before the
/// timeout — is put to the back of the line for a while, along with every engine on it. Without
/// that, a network that cannot reach Google (#436) waited out both Google engines, ten seconds each,
/// on every press before Microsoft spoke. The while starts at <see cref="FirstCooldown"/>, doubles
/// each time the host is tried again and still gives nothing, up to <see cref="LongestCooldown"/>,
/// and ends at the first answer. A host that answered with an error is not put back: it is there,
/// and the next press may well succeed. Kept for this run only; a run on a blocked network pays one
/// connection timeout on its first press.</para>
///
/// <para>Logs engines, languages, sizes and timings, never the text — the same rule as
/// <see cref="BatchTranslator"/>, for the same reason.</para>
/// </remarks>
public sealed class SpeechSynthesizer
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long an engine may go without answering before the next one is asked beside it.</summary>
    public static readonly TimeSpan DefaultHedge = TimeSpan.FromSeconds(3);

    /// <summary>How long a host that gave no answer goes to the back of the line, the first time.</summary>
    public static readonly TimeSpan FirstCooldown = TimeSpan.FromMinutes(2);

    /// <summary>The longest it stays there, however many times in a row it gave nothing.</summary>
    public static readonly TimeSpan LongestCooldown = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<ISpeechEngine> _engines;
    private readonly TimeSpan _hedge;
    private readonly TimeProvider _time;

    // Hosts that gave no answer lately. Every window speaks through one synthesizer, so presses in
    // two windows, and requests a press left running, report here at the same time.
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Strikes, DateTimeOffset Until)> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="engines">In the order they are asked.</param>
    /// <param name="hedge">Defaults to <see cref="DefaultHedge"/>.</param>
    /// <param name="time">The clock the cooldowns and the hedge run on; the system's by default.</param>
    public SpeechSynthesizer(IReadOnlyList<ISpeechEngine> engines, TimeSpan? hedge = null, TimeProvider? time = null)
    {
        _engines = engines;
        _hedge = hedge ?? DefaultHedge;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether any engine has a voice for <paramref name="language"/>.</summary>
    public bool Supports(string language) => _engines.Any(engine => engine.Supports(language));

    /// <returns>The speech as MP3, and the name of the engine that produced it.</returns>
    /// <exception cref="TranslationEngineException">
    /// No engine has a voice for the language, or every one that has failed — the last failure.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled; every request this press started is aborted, the ones left running
    /// behind an answer too.
    /// </exception>
    public async Task<(byte[] Audio, string Engine)> SynthesizeAsync(
        string text, string language, CancellationToken cancellationToken = default)
    {
        var supported = _engines.Where(engine => engine.Supports(language)).ToList();
        if (supported.Count == 0) throw new TranslationEngineException("Speech", $"no voice for {language}");

        // Last rather than left out: when every host is cooling down, one of them is still asked
        // instead of nothing being read.
        List<ISpeechEngine> waiting;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            waiting = [.. supported.Where(e => !IsCooling(e.Host, now)), .. supported.Where(e => IsCooling(e.Host, now))];
        }

        var running = new List<(ISpeechEngine Engine, Task<byte[]> Task, long Started)>();
        var unreachableThisPress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TranslationEngineException? last = null;

        using var stopHedge = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hedgeTimer = Task.CompletedTask;

        StartNext(_ => true);
        while (running.Count > 0)
        {
            var canHedge = waiting.Any(CanHedgeTo);
            var finished = await Task.WhenAny(
                running.Select(r => (Task)r.Task).Concat(canHedge ? [hedgeTimer] : []));
            cancellationToken.ThrowIfCancellationRequested();

            if (finished == hedgeTimer)
            {
                var stuck = running[^1];
                var next = StartNext(CanHedgeTo)!;
                Log.Info("朗讀 {Engine} {Elapsed} ms 未回應，同時改試 {Next}",
                    stuck.Engine.Name, Elapsed(stuck.Started), next.Name);
                continue;
            }

            var done = running.First(r => r.Task == finished);
            running.Remove(done);
            try
            {
                var audio = await done.Task;
                Log.Debug("朗讀：{Engine}，{Language}，{Characters} 字，{Bytes} bytes，{Elapsed} ms",
                    done.Engine.Name, language, text.Length, audio.Length, Elapsed(done.Started));
                return (audio, done.Engine.Name);
            }
            catch (TranslationEngineException ex)
            {
                Log.Info("朗讀 {Engine} 失敗：{Language}，{Characters} 字，{Elapsed} ms，{Status}，{Reason}，改試下一個",
                    done.Engine.Name, language, text.Length, Elapsed(done.Started),
                    ex.StatusCode is { } status ? (int)status : "-", ex.Message);
                last = ex;
                if (GotNoAnswer(ex)) unreachableThisPress.Add(done.Engine.Host);
                if (running.Count == 0) StartNext(_ => true);
            }
        }

        throw last ?? new TranslationEngineException("Speech", $"no voice for {language}");

        // Never beside a request to the same host: if that one is stuck, so is this.
        bool CanHedgeTo(ISpeechEngine engine) => running.TrueForAll(r => !SameHost(r.Engine, engine));

        ISpeechEngine? StartNext(Func<ISpeechEngine, bool> allowed)
        {
            var next = waiting.FirstOrDefault(e => !unreachableThisPress.Contains(e.Host) && allowed(e));
            if (next is null) return null;

            waiting.Remove(next);
            var started = Stopwatch.GetTimestamp();
            var task = next.SynthesizeAsync(text, language, cancellationToken);
            running.Add((next, task, started));

            // Whether this press still waits for it or not: a request left behind by an answer
            // still says whether its host can be reached.
            _ = RecordAsync(next, task, cancellationToken);

            hedgeTimer = Task.Delay(_hedge, _time, stopHedge.Token);
            return next;
        }
    }

    private static bool SameHost(ISpeechEngine a, ISpeechEngine b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A failure that never reached the service: no connection, no handshake, or no response in
    /// time. One with a status, or an answer that could not be used, came from the service.
    /// </summary>
    internal static bool GotNoAnswer(TranslationEngineException ex) =>
        ex.StatusCode is null && ex.InnerException is HttpRequestException { StatusCode: null } or OperationCanceledException;

    private async Task RecordAsync(ISpeechEngine engine, Task<byte[]> request, CancellationToken cancellationToken)
    {
        try
        {
            await request;
            Answered(engine.Host);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped: says nothing about the host.
        }
        catch (TranslationEngineException ex) when (GotNoAnswer(ex))
        {
            NoAnswer(engine.Host);
        }
        catch (TranslationEngineException)
        {
            Answered(engine.Host);
        }
        catch (Exception ex)
        {
            // Not a failure this sorts; the press that waits for it sees it, and one left behind
            // must not end up unobserved.
            Log.Debug(ex, "朗讀 {Engine} 例外", engine.Name);
        }
    }

    private bool IsCooling(string host, DateTimeOffset now) =>
        _unreachable.TryGetValue(host, out var entry) && entry.Until > now;

    private void Answered(string host)
    {
        lock (_gate)
        {
            if (_unreachable.Remove(host)) Log.Info("朗讀：{Host} 恢復回應", host);
        }
    }

    private void NoAnswer(string host)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            // Requests sent before the first failure was known, or a host asked as the last resort
            // while cooling down, fail alongside it; that is not another try.
            if (IsCooling(host, now)) return;

            var strikes = _unreachable.TryGetValue(host, out var entry) ? entry.Strikes + 1 : 1;
            var wait = FirstCooldown * (1 << Math.Min(strikes - 1, 5));
            if (wait > LongestCooldown) wait = LongestCooldown;

            _unreachable[host] = (strikes, now + wait);
            Log.Info("朗讀：{Host} 沒有回應，{Minutes} 分鐘內先用其他引擎", host, wait.TotalMinutes);
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
