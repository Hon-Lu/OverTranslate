using NLog;

namespace OverTranslate.Services.LocalAi;

internal enum LocalAiServerState
{
    /// <summary>No model in memory: not used yet, idle for long enough, or released.</summary>
    Stopped,
    Starting,
    Running,
    /// <summary>It exited without being asked to, and is being brought back.</summary>
    Restarting,
    Failed,
}

/// <summary>What a model is started from.</summary>
internal sealed record LocalAiLaunch(string ModelId, string ModelPath);

/// <summary>A running <c>llama-server</c>, ready to answer.</summary>
internal interface ILocalAiServerProcess : IDisposable
{
    Uri BaseAddress { get; }

    string ApiKey { get; }

    /// <summary>Completes when the process has gone, for whatever reason.</summary>
    Task Exited { get; }

    /// <summary>The end of what it wrote, for 查看詳細資訊.</summary>
    string RecentLog { get; }

    void Kill();
}

/// <summary>This machine has no dedicated graphics card to run a model on.</summary>
internal sealed class LocalAiNoGpuException(string message = "no dedicated GPU") : Exception(message);

/// <summary>A model would not start, after the attempts it is given.</summary>
internal sealed class LocalAiStartFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Holds a server up for as long as a request is using it.</summary>
internal sealed class LocalAiLease : IDisposable
{
    private readonly Action _done;
    private int _disposed;

    internal LocalAiLease(ILocalAiServerProcess process, Action done)
    {
        Process = process;
        _done = done;
    }

    internal ILocalAiServerProcess Process { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _done();
    }
}

/// <summary>
/// The one model in memory: starting it when a translation needs it, keeping it while it is used,
/// letting go of it when it is not.
/// </summary>
/// <remarks>
/// <para>Nothing runs until something asks: opening the app costs no VRAM. From then on the model
/// stays until it has been idle for the time the user chose. Being asked for a different model
/// closes the one running first, because two would not fit.</para>
///
/// <para>A request holds a <see cref="LocalAiLease"/> for as long as it runs, and the idle check
/// passes over a server with one out. So a long batch is never released under itself, however long
/// it takes, and a realtime session keeps the model simply by asking.</para>
///
/// <para>A server that exits without being asked is brought back at once with a short back-off, and
/// anyone asking in the meantime waits for that rather than being told it failed: the screen says
/// 翻譯中… for a few seconds longer. Crashing over and over is the one thing that is reported — after
/// <see cref="MaxCrashes"/> in a row — and then not retried for <see cref="CoolDown"/>, since a
/// realtime session asks several times a second. A server that stays up for
/// <see cref="StableAfter"/> has its count cleared.</para>
/// </remarks>
internal sealed class LocalAiServerHost
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const int MaxCrashes = 3;
    internal static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private readonly Func<LocalAiLaunch, CancellationToken, Task<ILocalAiServerProcess>> _start;
    private readonly Func<TimeSpan> _idleLimit;
    private readonly Func<DateTime> _now;
    private readonly IReadOnlyList<TimeSpan> _backOff;
    private readonly object _sync = new();
    private readonly System.Threading.Timer? _idleTimer;

    private ILocalAiServerProcess? _process;
    private LocalAiLaunch? _launch;
    private Task? _starting;
    private CancellationTokenSource? _startCancel;
    private int _leases;
    private DateTime _lastUsed;
    private DateTime _startedAt;
    private DateTime? _failedAt;
    private int _crashes;

    /// <param name="backOff">The wait before each attempt to bring a crashed server back.</param>
    /// <param name="checkIdleEvery">Null leaves <see cref="CheckIdle"/> to the caller.</param>
    internal LocalAiServerHost(
        Func<LocalAiLaunch, CancellationToken, Task<ILocalAiServerProcess>> start,
        Func<TimeSpan> idleLimit,
        Func<DateTime>? now = null,
        IReadOnlyList<TimeSpan>? backOff = null,
        TimeSpan? checkIdleEvery = null)
    {
        _start = start;
        _idleLimit = idleLimit;
        _now = now ?? (() => DateTime.UtcNow);
        _backOff = backOff ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];
        if (checkIdleEvery is { } every)
            _idleTimer = new System.Threading.Timer(_ => CheckIdle(), null, every, every);
    }

    internal LocalAiServerState State { get; private set; }

    /// <summary>Which model is in memory or on its way there.</summary>
    internal string? ModelId { get; private set; }

    /// <summary>What the server last wrote before it failed.</summary>
    internal string FailureDetail { get; private set; } = "";

    /// <summary>Raised on any change of <see cref="State"/>; on any thread.</summary>
    internal event EventHandler? Changed;

    /// <summary>The server for <paramref name="launch"/>, started if it has to be.</summary>
    /// <exception cref="LocalAiNoGpuException">There is nothing to run it on.</exception>
    /// <exception cref="LocalAiStartFailedException">It would not start.</exception>
    internal async Task<LocalAiLease> AcquireAsync(LocalAiLaunch launch, CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task starting;
            var changed = false;
            lock (_sync)
            {
                if (_process is { } process && _launch!.ModelId == launch.ModelId)
                {
                    _leases++;
                    _lastUsed = _now();
                    return new LocalAiLease(process, ReturnLease);
                }

                if (_launch?.ModelId != launch.ModelId && (_process is not null || _starting is not null))
                {
                    StopLocked();
                    changed = true;
                }

                if (_starting is null)
                {
                    if (_failedAt is { } failed && _now() - failed < CoolDown)
                        throw new LocalAiStartFailedException(FailureDetail);

                    _failedAt = null;
                    _crashes = 0;
                    Begin(launch, LocalAiServerState.Starting, TimeSpan.Zero);
                    changed = true;
                }

                starting = _starting!;
            }

            if (changed) RaiseChanged();

            try
            {
                await starting.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // The start was called off under this request — a release, another model. Looked at
                // again: what is asked for may still be wanted.
            }
        }
    }

    private void Begin(LocalAiLaunch launch, LocalAiServerState state, TimeSpan delay)
    {
        _launch = launch;
        ModelId = launch.ModelId;
        State = state;
        _startCancel = new CancellationTokenSource();
        _starting = RunStartAsync(launch, delay, _startCancel);
    }

    private async Task RunStartAsync(LocalAiLaunch launch, TimeSpan delay, CancellationTokenSource cancel)
    {
        // Off the caller's stack, and out of its lock: Begin has to have stored this task first.
        await Task.Yield();

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancel.Token).ConfigureAwait(false);

            var process = await _start(launch, cancel.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (cancel.IsCancellationRequested)
                {
                    process.Kill();
                    process.Dispose();
                    cancel.Token.ThrowIfCancellationRequested();
                }

                _process = process;
                _startedAt = _lastUsed = _now();
                State = LocalAiServerState.Running;
                FailureDetail = "";
                _ = process.Exited.ContinueWith(_ => OnExited(process), TaskScheduler.Default);
            }

            Log.Info("Local AI server is up with {Model:l}", launch.ModelId);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Local AI server did not start with {Model:l}", launch.ModelId);
            lock (_sync)
            {
                State = LocalAiServerState.Failed;
                FailureDetail = ex.Message;
                _failedAt = _now();
            }

            if (ex is LocalAiNoGpuException) throw;
            throw new LocalAiStartFailedException(ex.Message, ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_startCancel, cancel))
                {
                    _starting = null;
                    _startCancel = null;
                }
            }

            cancel.Dispose();
            RaiseChanged();
        }
    }

    private void OnExited(ILocalAiServerProcess process)
    {
        lock (_sync)
        {
            // Not one that was stopped on purpose: those are taken out of _process first.
            if (!ReferenceEquals(_process, process)) return;

            FailureDetail = process.RecentLog;
            Log.Warn("Local AI server exited unexpectedly: {Log}", process.RecentLog);
            _process = null;
            process.Dispose();

            if (_now() - _startedAt >= StableAfter) _crashes = 0;
            _crashes++;

            if (_crashes > MaxCrashes)
            {
                State = LocalAiServerState.Failed;
                _failedAt = _now();
            }
            else
            {
                Begin(_launch!, LocalAiServerState.Restarting, _backOff[Math.Min(_crashes - 1, _backOff.Count - 1)]);
                _ = _starting!.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            }
        }

        RaiseChanged();
    }

    private void ReturnLease()
    {
        lock (_sync)
        {
            _leases--;
            _lastUsed = _now();
        }
    }

    /// <summary>Closes the model if it has gone unused for the time the user chose.</summary>
    internal void CheckIdle()
    {
        lock (_sync)
        {
            if (_process is null || _leases > 0 || _now() - _lastUsed < _idleLimit()) return;
            Log.Info("Local AI server released after being idle");
            StopLocked();
        }

        RaiseChanged();
    }

    /// <summary>Closes the model now. Asking for it again starts it again.</summary>
    internal void Release()
    {
        lock (_sync)
        {
            StopLocked();
            _failedAt = null;
            _crashes = 0;
        }

        RaiseChanged();
    }

    private void StopLocked()
    {
        var process = _process;
        _process = null;
        _launch = null;
        ModelId = null;
        State = LocalAiServerState.Stopped;
        try
        {
            _startCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Left to its own finally to clear: nothing else may start until that one has wound down.
        _startCancel = null;
        _starting = null;

        if (process is null) return;
        process.Kill();
        process.Dispose();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
