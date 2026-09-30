using System.Diagnostics;
using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using NLog;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>What one attempt to read a page with the manga models came to.</summary>
internal enum MangaReadOutcome
{
    Read,
    /// <summary>Another page is being read on the GPU right now; only a realtime caller sees this.</summary>
    Busy,
    /// <summary>No models, no usable GPU, or they failed: the caller reads with the column pipeline.</summary>
    Unavailable,
}

/// <summary>Why the manga models would not read a page, in terms a person can be told.</summary>
internal enum MangaUnavailable
{
    None,
    NotDownloaded,
    /// <summary>Neither the app folder nor Windows has DirectML.dll.</summary>
    NoDirectMl,
    /// <summary>No graphics adapter, only a software one, or the adapters could not be listed.</summary>
    NoGpu,
    /// <summary>The adapter was there but the models would not load on it.</summary>
    LoadFailed,
    /// <summary>They loaded, then failed while reading a page.</summary>
    ReadFailed,
    /// <summary>Downloaded and able to run, but switched off on the settings page.</summary>
    Disabled,
}

/// <summary>What the models found on a page, before layout.</summary>
/// <param name="Blocks">Every text block they read, in the order they were read.</param>
/// <param name="Long">Blocks too long for manga-ocr, for the column pipeline to read instead.</param>
/// <param name="Luma">The page in grey, for <see cref="MangaPageLayout.Assemble"/> to look between blocks.</param>
internal sealed record MangaPage(
    List<MangaBlock> Blocks, List<RectangleF> Long, List<RectangleF> Bubbles, double DetectMs, double ReadMs,
    LumaPage? Luma = null);

/// <summary>
/// The manga models on the GPU: loaded on first use, one page at a time, and let go when idle.
/// </summary>
/// <remarks>
/// <para>Everything runs on DirectML, on the adapter <see cref="DirectMlDevice"/> picks. When it
/// picks none — no GPU, only WARP, no DirectML — or the models are not downloaded, or loading them
/// fails, the answer is <see cref="MangaReadOutcome.Unavailable"/> and the caller reads the page with
/// the column pipeline instead. Not with these models on the CPU: every CPU configuration measured was
/// 1.7–2.7 times slower than the column pipeline it would replace.</para>
///
/// <para>A load or inference failure is remembered until the models change, so a machine whose GPU
/// cannot run them pays for finding out once, not on every poll.</para>
///
/// <para>One page at a time: a DirectML session takes one Run at a time, and the batch already fills
/// the GPU. Loaded, the models hold about 330MB of video memory and up to about 770MB while a page is
/// read; released, none. They are released <see cref="IdleReleaseDelay"/> after the last page, as the
/// column pipeline's runtime is, unless a realtime session is holding them warm.</para>
/// </remarks>
internal sealed class MangaOcrEngine : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(1);

    private readonly MangaModelStore _store;
    private readonly Func<(DirectMlDevice.Adapter? Adapter, string? Reason)> _chooseAdapter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _idleRelease;
    private readonly object _sync = new();

    // Guarded by _gate.
    private MangaTextDetector? _detector;
    private MangaTextRecognizer? _recognizer;
    // Guarded by _sync.
    private (MangaUnavailable Kind, string Reason)? _failure;
    private bool _keepWarm;
    private bool _disposed;
    private bool _enabled = true;
    private (DirectMlDevice.Adapter? Adapter, string? Reason)? _adapter;

    internal MangaOcrEngine(
        MangaModelStore store,
        Func<(DirectMlDevice.Adapter? Adapter, string? Reason)>? chooseAdapter = null)
    {
        _store = store;
        _chooseAdapter = chooseAdapter ?? DirectMlDevice.Choose;
        _idleRelease = new System.Threading.Timer(_ => ReleaseIfIdle());
        _store.Changed += OnModelsChanged;
    }

    /// <summary>
    /// Why the models would not be used for a page right now, or null when they would. Answers
    /// without loading anything; a load failure it has not met yet is not known here.
    /// </summary>
    internal string? UnavailableReason => Why().Reason;

    /// <summary><see cref="UnavailableReason"/> as a kind, for the settings page to put into words.</summary>
    internal MangaUnavailable Unavailable => Why().Kind;

    private (MangaUnavailable Kind, string? Reason) Why()
    {
        if (_store.State != MangaModelState.Ready) return (MangaUnavailable.NotDownloaded, "models not downloaded");
        lock (_sync)
        {
            if (_failure is { } failure) return failure;
            var device = Device();
            if (device.Kind != MangaUnavailable.None) return device;
            return _enabled ? device : (MangaUnavailable.Disabled, "switched off in settings");
        }
    }

    /// <summary>
    /// Whether the models may be used at all. Switched off, every page goes to the column pipeline
    /// and the loaded sessions are let go of straight away — the reason to switch them off is
    /// usually a game that wants the video memory, and a minute's idle countdown is a minute of it.
    /// </summary>
    /// <remarks>
    /// <para>A page being read when it is switched off finishes on the models and releases them as
    /// it ends; the next page, realtime or not, is read with the columns. Switched back on, they are
    /// loaded by the next page that wants them, as on first use.</para>
    ///
    /// <para>Only means anything while the models are downloaded. A delete or the start of a new
    /// download puts it back on (<see cref="EnabledChanged"/>), so models downloaded again start in
    /// use, as they did the first time, rather than on a choice made about the ones removed.</para>
    /// </remarks>
    internal bool Enabled
    {
        get
        {
            lock (_sync) return _enabled;
        }
        set
        {
            lock (_sync)
            {
                if (_enabled == value) return;
                _enabled = value;
            }

            if (!value) UnloadIfIdle();
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="Enabled"/> changes, including when a delete puts it back on.</summary>
    internal event EventHandler? EnabledChanged;

    /// <summary>
    /// Whether this machine has anything the models could run on — a hardware adapter DirectML can
    /// use — asked before they are downloaded: <see cref="MangaUnavailable.None"/>, or
    /// <see cref="MangaUnavailable.NoGpu"/> / <see cref="MangaUnavailable.NoDirectMl"/>.
    /// </summary>
    /// <remarks>
    /// <para>The same check that decides it after the download (<see cref="DirectMlDevice.Choose"/>:
    /// DXGI's adapters with WARP and software ones left out, and DirectML.dll to be found), so a
    /// machine told "not supported" here is one the models would never have run on, and 301 MB are
    /// not fetched to find that out. It takes milliseconds and is asked once per run.</para>
    ///
    /// <para>Video memory is not part of it. Integrated graphics report 128–512 MB of their own and
    /// borrow the rest from system memory; judged by the number they report, machines that can run
    /// the models would be turned away. The card only recommends an amount.</para>
    /// </remarks>
    internal MangaUnavailable DeviceSupport
    {
        get
        {
            lock (_sync) return Device().Kind;
        }
    }

    // Under _sync.
    private (MangaUnavailable Kind, string? Reason) Device()
    {
        _adapter ??= _chooseAdapter();
        return _adapter.Value.Reason switch
        {
            null => (MangaUnavailable.None, null),
            DirectMlDevice.NoLibrary => (MangaUnavailable.NoDirectMl, DirectMlDevice.NoLibrary),
            var reason => (MangaUnavailable.NoGpu, reason),
        };
    }

    /// <summary>
    /// Downloads the models, unless this machine could not run them (<see cref="DeviceSupport"/>), in
    /// which case nothing is fetched and <see cref="NotSupportedException"/> is thrown.
    /// </summary>
    internal Task DownloadModelsAsync(CancellationToken cancellationToken = default)
    {
        if (DeviceSupport != MangaUnavailable.None)
            throw new NotSupportedException("no hardware adapter DirectML can use");
        return _store.DownloadAsync(cancellationToken);
    }

    /// <summary>The adapter the models run on, for the settings page to name.</summary>
    internal string? AdapterName
    {
        get
        {
            lock (_sync)
            {
                _adapter ??= _chooseAdapter();
                return _adapter.Value.Adapter?.Name;
            }
        }
    }

    /// <inheritdoc cref="OnnxOcrEngine.SetKeepWarm"/>
    internal void SetKeepWarm(bool keepWarm)
    {
        lock (_sync)
        {
            _keepWarm = keepWarm;
            if (!keepWarm && !_disposed)
                _idleRelease.Change(IdleReleaseDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <inheritdoc cref="OnnxOcrEngine.ReleaseNow"/>
    internal void ReleaseNow()
    {
        lock (_sync) _keepWarm = false;
        // A page being read keeps the models; it re-arms the countdown as it finishes.
        if (!_gate.Wait(0)) return;
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads a page. <paramref name="wait"/> false turns a busy GPU into
    /// <see cref="MangaReadOutcome.Busy"/> instead of a queue — for a live screen, where a queued read
    /// would answer a frame that has already been replaced.
    /// </summary>
    internal async Task<(MangaReadOutcome Outcome, MangaPage? Page)> ReadAsync(
        Bitmap bitmap, bool wait, CancellationToken cancellationToken)
    {
        if (UnavailableReason is not null)
            return (MangaReadOutcome.Unavailable, null);

        if (wait)
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!_gate.Wait(0, cancellationToken))
            return (MangaReadOutcome.Busy, null);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => ReadUnderGate(bitmap), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Switched off while this page was being read: nothing is waiting for the models now.
            if (!Enabled) Unload();
            _gate.Release();
            lock (_sync)
                if (!_keepWarm && !_disposed)
                    _idleRelease.Change(IdleReleaseDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Whether sessions are loaded right now; for tests.</summary>
    internal bool IsLoaded
    {
        get
        {
            if (!_gate.Wait(0)) return true;
            try
            {
                return _detector is not null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private (MangaReadOutcome, MangaPage?) ReadUnderGate(Bitmap bitmap)
    {
        if (!EnsureLoaded())
            return (MangaReadOutcome.Unavailable, null);

        try
        {
            var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var rgb = PillowImage.Rgb(bitmap, area);

            var timer = Stopwatch.StartNew();
            var found = _detector!.Detect(rgb, bitmap.Width, bitmap.Height);
            var detectMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();

            var toRead = found.Text.Where(box => !MangaPageLayout.IsLong(box.Bounds)).ToList();
            var readings = _recognizer!.Read(
                rgb, bitmap.Width, bitmap.Height,
                [.. toRead.Select(box => MangaPageLayout.RecognitionCrop(box.Bounds, bitmap.Width, bitmap.Height))]);
            var readMs = timer.Elapsed.TotalMilliseconds;

            var blocks = toRead
                .Select((box, i) => new MangaBlock(box.Bounds, readings[i].Text, readings[i].Confidence))
                .Where(block => block.Text.Length > 0)
                .ToList();
            var longBlocks = found.Text.Where(box => MangaPageLayout.IsLong(box.Bounds)).Select(box => box.Bounds).ToList();

            Log.Info(
                "Manga OCR on {W}x{H}: {Blocks} blocks, {Long} long, {Bubbles} bubbles, detect={Detect:F0}ms read={Read:F0}ms",
                bitmap.Width, bitmap.Height, blocks.Count, longBlocks.Count, found.Bubbles.Count, detectMs, readMs);

            return (MangaReadOutcome.Read, new MangaPage(
                blocks, longBlocks, [.. found.Bubbles.Select(box => box.Bounds)], detectMs, readMs,
                new LumaPage(PillowImage.Luma(rgb), bitmap.Width, bitmap.Height)));
        }
        catch (OnnxRuntimeException ex)
        {
            // A device lost or out of memory mid-page. This page goes to the column pipeline, and so
            // does every one after it until the models change: a GPU that failed once will fail again.
            Fail(MangaUnavailable.ReadFailed, $"inference failed: {ex.Message}", ex);
            Unload();
            return (MangaReadOutcome.Unavailable, null);
        }
    }

    // Under _gate.
    private bool EnsureLoaded()
    {
        if (_detector is not null) return true;

        DirectMlDevice.Adapter adapter;
        lock (_sync)
        {
            _adapter ??= _chooseAdapter();
            if (_adapter.Value.Adapter is not { } chosen) return false;
            adapter = chosen;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            SessionOptions Options()
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // DirectML supports neither memory patterns nor parallel execution.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    // Session creation warns that shape arithmetic was placed on the CPU. That is
                    // where it belongs, and the warning would otherwise go to stderr on every load.
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                try
                {
                    options.AppendExecutionProvider_DML(adapter.Index);
                }
                catch
                {
                    options.Dispose();
                    throw;
                }

                return options;
            }

            using (var detectorOptions = Options())
                _detector = new MangaTextDetector(_store.PathOf("detector"), detectorOptions);
            _recognizer = new MangaTextRecognizer(
                _store.PathOf("encoder"), _store.PathOf("decoder-cross"), _store.PathOf("decoder-step"),
                _store.PathOf("vocabulary"), Options, adapter.Index);

            Log.Info("Manga models loaded on {Adapter} (device {Index}) in {Ms}ms",
                adapter.Name, adapter.Index, timer.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or System.IO.IOException or
                                       UnauthorizedAccessException or InvalidOperationException or
                                       System.IO.InvalidDataException or EntryPointNotFoundException or
                                       DllNotFoundException)
        {
            Fail(MangaUnavailable.LoadFailed, $"loading on {adapter.Name} failed: {ex.Message}", ex);
            Unload();
            return false;
        }
    }

    private void Fail(MangaUnavailable kind, string reason, Exception ex)
    {
        lock (_sync) _failure = (kind, reason);
        Log.Warn(ex, "Manga models unavailable, vertical text falls back to the column pipeline: {Reason}", reason);
    }

    // Under _gate.
    private void Unload()
    {
        if (_detector is null && _recognizer is null) return;
        _detector?.Dispose();
        _recognizer?.Dispose();
        _detector = null;
        _recognizer = null;
        Log.Info("Manga models released");
    }

    private void ReleaseIfIdle()
    {
        lock (_sync)
            if (_keepWarm || _disposed) return;
        UnloadIfIdle();
    }

    // Unlike ReleaseNow, leaves _keepWarm alone: a realtime session switched off and on again is
    // still running, and still wants the models kept once they are back.
    private void UnloadIfIdle()
    {
        lock (_sync)
            if (_disposed) return;
        if (!_gate.Wait(0)) return;
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    // New files, or none: whatever failed before is a different question now, and loaded sessions
    // must not outlive the files they came from.
    private void OnModelsChanged(object? sender, EventArgs e)
    {
        var state = _store.State;
        // Before the early return: a download starting is the other way new models arrive, and
        // they start switched on — see Enabled.
        if (state != MangaModelState.Ready) Enabled = true;
        if (state == MangaModelState.Downloading) return;
        lock (_sync) _failure = null;
        if (state != MangaModelState.Ready) ReleaseNow();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _store.Changed -= OnModelsChanged;
        _idleRelease.Dispose();
        _gate.Wait();
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }
}
