using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// No graphics adapter, only a software one, the adapters could not be listed, or — found out as
    /// they load — the one chosen has no Direct3D 12.
    /// </summary>
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
/// picks none — no GPU, only WARP — or the models are not downloaded, or loading them or the
/// DirectML runtime fails, the answer is <see cref="MangaReadOutcome.Unavailable"/> and the caller reads the page with
/// the column pipeline instead. Not with these models on the CPU: every CPU configuration measured was
/// 1.7–2.7 times slower than the column pipeline it would replace.</para>
///
/// <para>A load or inference failure is remembered until the models change, so a machine whose GPU
/// cannot run them pays for finding out once, not on every poll.</para>
///
/// <para>One page at a time: a DirectML session takes one Run at a time, and the batch already fills
/// the GPU. Loaded, the models hold about 380MB of video memory and up to about 1GB while a page is
/// read — CRAFT 45MB of the first and, once it has run, 195MB more of the second; released, none.
/// They are released <see cref="IdleReleaseDelay"/> after the last page, as the column pipeline's
/// runtime is, unless a realtime session is holding them warm.</para>
/// </remarks>
internal sealed class MangaOcrEngine : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(1);

    private readonly MangaModelStore _store;
    private readonly Func<(DirectMlDevice.Adapter? Adapter, string? Reason)> _chooseAdapter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _idleRelease;
    private readonly TimeSpan _idleReleaseDelay;
    // Run at the start of a load, before anything is created; tests hold a load open with it.
    private readonly Action? _beforeLoad;
    private readonly object _sync = new();

    // Guarded by _gate.
    private MangaTextDetector? _detector;
    private MangaTextRecognizer? _recognizer;
    private MangaColumnDetector? _columns;
    // Guarded by _sync.
    private (MangaUnavailable Kind, string Reason)? _failure;
    private bool _keepWarm;
    private bool _disposed;
    private bool _enabled = true;
    private (DirectMlDevice.Adapter? Adapter, string? Reason)? _adapter;
    private bool _loading;
    // The reason NoteColumnsInstead last logged.
    private string? _columnsInsteadLogged;

    /// <param name="beforeLoad">For tests: runs as a load starts.</param>
    /// <param name="idleReleaseDelay">For tests: <see cref="IdleReleaseDelay"/> otherwise.</param>
    internal MangaOcrEngine(
        MangaModelStore store,
        Func<(DirectMlDevice.Adapter? Adapter, string? Reason)>? chooseAdapter = null,
        Action? beforeLoad = null,
        TimeSpan? idleReleaseDelay = null)
    {
        _store = store;
        _chooseAdapter = chooseAdapter ?? DirectMlDevice.Choose;
        _beforeLoad = beforeLoad;
        _idleReleaseDelay = idleReleaseDelay ?? IdleReleaseDelay;
        _idleRelease = new System.Threading.Timer(_ => ReleaseIfIdle());
        _store.Changed += OnModelsChanged;
    }

    /// <summary>
    /// Whether the models are in memory, asked without the GPU: <see cref="IsLoaded"/> waits for no
    /// one but answers yes while a page or a load holds it.
    /// </summary>
    // The recogniser is the last thing a load creates and goes with the detector.
    internal bool InMemory => Volatile.Read(ref _recognizer) is not null;

    /// <summary>Whether the models are being loaded right now.</summary>
    internal bool IsLoading
    {
        get
        {
            lock (_sync) return _loading;
        }
    }

    /// <summary>Whether a realtime session is holding the models warm; for tests.</summary>
    internal bool KeepsWarm
    {
        get
        {
            lock (_sync) return _keepWarm;
        }
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
    /// <see cref="MangaUnavailable.NoGpu"/>.
    /// </summary>
    /// <remarks>
    /// <para>The same check that decides it after the download (<see cref="DirectMlDevice.Choose"/>:
    /// DXGI's adapters with WARP and software ones left out; DirectML.dll is not needed to ask), so a
    /// machine told "not supported" here is one the models would never have run on, and 341 MB are
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
                _idleRelease.Change(_idleReleaseDelay, Timeout.InfiniteTimeSpan);
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
    /// Loads the models now, on a background thread, as a page would: for a realtime session over
    /// vertical Japanese, so the first page with text on it does not wait 1.6–2s for them.
    /// </summary>
    /// <remarks>
    /// <para>Holds the GPU while it loads, so a realtime read meanwhile is <see cref="MangaReadOutcome.Busy"/>
    /// and skips its poll rather than loading a second time. Ends as a page ends
    /// (<see cref="EndRead"/>): a session that ended meanwhile leaves them to the idle countdown, and
    /// switched off meanwhile they go at once.</para>
    ///
    /// <para>Each model is then run once on a blank block (<see cref="WarmUp"/>), so the first page
    /// is read as fast as the ones after it.</para>
    /// </remarks>
    internal Task PreloadAsync() => Task.Run(async () =>
    {
        if (UnavailableReason is not null) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoaded(warmUp: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Loading the manga models ahead of a realtime session failed");
        }
        finally
        {
            EndRead();
        }
    });

    /// <summary>
    /// Reads a page whole: every block the detector found that is not too long for manga-ocr.
    /// </summary>
    /// <remarks>
    /// <see cref="DetectAsync"/> and <see cref="MangaDetection.ReadAsync"/> in one, for callers
    /// that have nothing to decide in between.
    /// </remarks>
    internal async Task<(MangaReadOutcome Outcome, MangaPage? Page)> ReadAsync(
        Bitmap bitmap, bool wait, CancellationToken cancellationToken)
    {
        var (outcome, detection) = await DetectAsync(bitmap, wait, cancellationToken).ConfigureAwait(false);
        if (detection is null) return (outcome, null);
        using (detection)
        {
            var blocks = await detection.ReadAsync(
                [.. Enumerable.Range(0, detection.Text.Count)], cancellationToken).ConfigureAwait(false);
            if (blocks is null) return (MangaReadOutcome.Unavailable, null);
            detection.LogPage();
            return (MangaReadOutcome.Read, detection.Page(blocks));
        }
    }

    /// <summary>
    /// Finds the page's blocks and balloons and reads nothing yet, keeping the GPU until the result
    /// is disposed, so the caller can choose what manga-ocr reads without another page getting in
    /// between. <paramref name="wait"/> false turns a busy GPU into <see cref="MangaReadOutcome.Busy"/>
    /// instead of a queue — for a live screen, where a queued read would answer a frame that has
    /// already been replaced.
    /// </summary>
    /// <returns>The detection when the outcome is <see cref="MangaReadOutcome.Read"/>; null otherwise.</returns>
    internal async Task<(MangaReadOutcome Outcome, MangaDetection? Detection)> DetectAsync(
        Bitmap bitmap, bool wait, CancellationToken cancellationToken)
    {
        if (UnavailableReason is { } why)
        {
            NoteColumnsInstead(why);
            return (MangaReadOutcome.Unavailable, null);
        }

        lock (_sync) _columnsInsteadLogged = null;
        if (wait)
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!_gate.Wait(0, cancellationToken))
            return (MangaReadOutcome.Busy, null);

        MangaDetection? detection = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            detection = await Task.Run(() => DetectUnderGate(bitmap), cancellationToken).ConfigureAwait(false);
            return detection is null ? (MangaReadOutcome.Unavailable, null) : (MangaReadOutcome.Read, detection);
        }
        finally
        {
            // Handed on, the gate goes with it; otherwise it is given back here.
            if (detection is null) EndRead();
        }
    }

    // The end of a page: the gate is given back and the idle countdown starts.
    private void EndRead()
    {
        // Switched off while this page was being read: nothing is waiting for the models now.
        if (!Enabled) Unload();
        _gate.Release();
        lock (_sync)
            if (!_keepWarm && !_disposed)
                _idleRelease.Change(_idleReleaseDelay, Timeout.InfiniteTimeSpan);
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

    // Under _gate. Null when the models would not load or inference failed.
    private MangaDetection? DetectUnderGate(Bitmap bitmap)
    {
        if (!EnsureLoaded())
            return null;

        try
        {
            var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var rgb = PillowImage.Rgb(bitmap, area);

            var timer = Stopwatch.StartNew();
            var found = _detector!.Detect(rgb, bitmap.Width, bitmap.Height);
            var detectMs = timer.Elapsed.TotalMilliseconds;

            var luma = new LumaPage(PillowImage.Luma(rgb), bitmap.Width, bitmap.Height);
            List<RectangleF> text = [.. found.Text.Where(box => !MangaPageLayout.IsLong(box.Bounds, luma)).Select(box => box.Bounds)];
            List<RectangleF> longBlocks = [.. found.Text.Where(box => MangaPageLayout.IsLong(box.Bounds, luma)).Select(box => box.Bounds)];

            // Where the blocks that may hold more columns than CPT-flex reads at once are to be cut.
            timer.Restart();
            int asked = 0;
            var pieces = new List<IReadOnlyList<RectangleF>?>(text.Count);
            var leaning = new HashSet<int>();
            foreach (var block in text)
                pieces.Add(MangaColumnCuts.Asks(block) ? Cut(block) : null);

            return new MangaDetection(
                this, rgb, bitmap.Width, bitmap.Height, text, pieces, longBlocks,
                [.. found.Bubbles.Select(box => box.Bounds)],
                luma,
                detectMs, timer.Elapsed.TotalMilliseconds, asked, leaning);

            IReadOnlyList<RectangleF>? Cut(RectangleF block)
            {
                asked++;
                var cut = MangaColumnCuts.Pieces(
                    _columns!.Detect(rgb, bitmap.Width, bitmap.Height, block), block, luma, out bool leans);
                if (leans) leaning.Add(pieces.Count);
                return cut;
            }
        }
        catch (OnnxRuntimeException ex)
        {
            ReadFailed(ex);
            return null;
        }
    }

    // Under _gate. DirectML builds a session's kernels on its first run, and every model here runs at
    // one shape, so one run on a blank block builds all of them. MEASURED on an RTX 3070, first run
    // against the next: the detector 490ms against 34, CRAFT 83 against 9, the recogniser 240 against
    // 110 — what a realtime session's first page paid on top.
    private void WarmUp()
    {
        const int side = 32;
        var blank = new byte[side * side * 3];
        Array.Fill(blank, (byte)255);
        try
        {
            var timer = Stopwatch.StartNew();
            _detector!.Detect(blank, side, side);
            _columns!.Detect(blank, side, side, new RectangleF(0, 0, side, side));
            _recognizer!.Read(blank, side, side, [new Rectangle(0, 0, side, side)]);
            Log.Info("Manga models warmed up in {Ms}ms", timer.ElapsedMilliseconds);
        }
        catch (OnnxRuntimeException ex)
        {
            ReadFailed(ex);
        }
    }

    // Under _gate. One reading for each index asked, empty ones included; null when inference failed.
    private MangaBlock[]? ReadUnderGate(MangaDetection page, IReadOnlyList<int> which)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            // A block that was cut is read piece by piece, its pieces in the same batches as the rest.
            var parts = which.Select(i => page.Pieces[i] ?? [page.Text[i]]).ToList();
            var readings = _recognizer!.Read(
                page.Rgb, page.Width, page.Height,
                [.. parts.SelectMany(pieces => pieces).Select(piece => MangaPageLayout.RecognitionCrop(piece, page.Width, page.Height))]);
            page.ReadMs += timer.Elapsed.TotalMilliseconds;

            var blocks = new MangaBlock[which.Count];
            int at = 0;
            for (int k = 0; k < which.Count; k++)
            {
                var read = readings.Skip(at).Take(parts[k].Count).ToList();
                at += parts[k].Count;
                double weight = read.Sum(r => Math.Max(1, r.Text.Length));
                blocks[k] = new MangaBlock(
                    page.Text[which[k]],
                    string.Concat(read.Select(r => r.Text)),
                    read.Sum(r => r.Confidence * Math.Max(1, r.Text.Length)) / weight,
                    Columns: page.Pieces[which[k]] is not null);
            }
            return blocks;
        }
        catch (OnnxRuntimeException ex)
        {
            ReadFailed(ex);
            return null;
        }
    }

    // A device lost or out of memory mid-page. This page goes to the column pipeline, and so does
    // every one after it until the models change: a GPU that failed once will fail again.
    private void ReadFailed(OnnxRuntimeException ex)
    {
        Fail(MangaUnavailable.ReadFailed, $"inference failed: {Readable(ex.Message)}", ex);
        Unload();
    }

    private static readonly Regex HResult = new(@"\b8[0-9A-Fa-f]{7}\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// An ONNX Runtime message with the system's words for an HRESULT in it made readable again.
    /// </summary>
    /// <remarks>
    /// The DirectML provider puts Windows' message for a failed HRESULT into its own in the system's
    /// ANSI code page, and the message reaches .NET read as UTF-8: on a Chinese Windows every
    /// character of it is lost. Windows is asked for that message again, as UTF-16, and it replaces
    /// the rest of the line.
    /// </remarks>
    internal static string Readable(string message)
    {
        int lost = message.IndexOf('�');
        if (lost < 0) return message;
        var hr = HResult.Matches(message[..lost]).LastOrDefault();
        if (hr is null) return message;

        int lineEnd = message.IndexOf('\n', lost);
        var said = Marshal.GetExceptionForHR(Convert.ToInt32(hr.Value, 16))?.Message.Trim() ?? "";
        return message[..(hr.Index + hr.Length)] + " " + said + (lineEnd < 0 ? "" : message[lineEnd..]);
    }

    /// <summary>
    /// A page's blocks and balloons, found and not yet read, with the GPU held for it until disposed.
    /// </summary>
    internal sealed class MangaDetection : IDisposable
    {
        private readonly MangaOcrEngine _owner;
        private int _ended;
        // What ReadAhead began, for ReadAsync to finish the page with.
        private IReadOnlyList<int> _aheadWhich = [];
        private Task<MangaBlock[]?>? _ahead;
        private int _blocksRead;

        internal MangaDetection(
            MangaOcrEngine owner, byte[] rgb, int width, int height, List<RectangleF> text,
            IReadOnlyList<IReadOnlyList<RectangleF>?> pieces, List<RectangleF> longBlocks, List<RectangleF> bubbles,
            LumaPage luma, double detectMs, double cutMs, int cutsAsked, IReadOnlySet<int> leaning)
        {
            _owner = owner;
            Rgb = rgb;
            Width = width;
            Height = height;
            Text = text;
            Pieces = pieces;
            Long = longBlocks;
            Bubbles = bubbles;
            Luma = luma;
            DetectMs = detectMs;
            CutMs = cutMs;
            CutsAsked = cutsAsked;
            Leaning = leaning;
        }

        internal byte[] Rgb { get; }
        internal int Width { get; }
        internal int Height { get; }

        /// <summary>
        /// The blocks the manga models read, highest score first.
        /// </summary>
        internal List<RectangleF> Text { get; }

        /// <summary>
        /// For each of <see cref="Text"/>, the pieces it is read in when it was found to be columns
        /// (<see cref="MangaColumnCuts.Pieces"/>) — one, the block, when it is read whole — and null
        /// when it was not asked or no columns were found.
        /// </summary>
        internal IReadOnlyList<IReadOnlyList<RectangleF>?> Pieces { get; }

        /// <summary>Blocks too long for the manga models (<see cref="MangaPageLayout.IsLong"/>).</summary>
        internal List<RectangleF> Long { get; }

        internal List<RectangleF> Bubbles { get; }
        internal LumaPage Luma { get; }
        internal double DetectMs { get; }
        internal double CutMs { get; }
        internal int CutsAsked { get; }

        /// <summary>The <see cref="Text"/> indices CRAFT was asked about and did not cut because the columns lean.</summary>
        internal IReadOnlySet<int> Leaning { get; }

        internal double ReadMs { get; set; }

        /// <summary>
        /// Begins reading the <see cref="Text"/> blocks at the given indices and returns at once, so
        /// the GPU reads while the caller decides about the rest; <see cref="ReadAsync"/> finishes the
        /// page. At most once, before <see cref="ReadAsync"/>.
        /// </summary>
        internal void ReadAhead(IReadOnlyList<int> which, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
            if (_ahead is not null) throw new InvalidOperationException("Already reading ahead.");
            if (which.Count == 0) return;
            _aheadWhich = which;
            _ahead = Task.Run(() => _owner.ReadUnderGate(this, which), cancellationToken);
        }

        /// <summary>
        /// Reads the <see cref="Text"/> blocks at the given indices with manga-ocr, together with any
        /// begun by <see cref="ReadAhead"/>, and hands them all back in the order of their indices;
        /// null when inference failed, after which the models are not used again until they change.
        /// </summary>
        internal Task<List<MangaBlock>?> ReadAsync(IReadOnlyList<int> which, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
            return which.Count == 0 && _ahead is null
                ? Task.FromResult<List<MangaBlock>?>([])
                : ReadRestAsync(which, cancellationToken);
        }

        private async Task<List<MangaBlock>?> ReadRestAsync(IReadOnlyList<int> which, CancellationToken cancellationToken)
        {
            // One read at a time: the rest goes in only once what was begun ahead has come out.
            var ahead = _ahead is null ? [] : await _ahead.ConfigureAwait(false);
            if (ahead is null) return null;
            var rest = which.Count == 0
                ? []
                : await Task.Run(() => _owner.ReadUnderGate(this, which), cancellationToken).ConfigureAwait(false);
            if (rest is null) return null;

            var blocks = _aheadWhich.Zip(ahead).Concat(which.Zip(rest))
                .OrderBy(read => read.First)
                .Select(read => read.Second)
                .Where(block => block.Text.Length > 0)
                .ToList();
            _blocksRead = blocks.Count;
            return blocks;
        }

        /// <summary>
        /// The page's line in the log, once it is read: what the manga models found and read, and
        /// <paramref name="routes"/>, how many blocks went to the column pipeline and why.
        /// </summary>
        /// <param name="routes">What <see cref="MangaVerticalReader"/> sent on; nothing when there was no choosing.</param>
        internal void LogPage(MangaRoutes routes = default) =>
            Log.Info(
                "Manga OCR on {W}x{H}: {Blocks} blocks read of {Found}, {Long} long, {Bubbles} bubbles, detect={Detect:F0}ms read={Read:F0}ms columns: {Asked} asked, {Cut} cut, {Columns:F0}ms; column pipeline: {Wide} wide, {Leaning} leaning, {Kept} across of {Sent}",
                Width, Height, _blocksRead, Text.Count, Long.Count, Bubbles.Count, DetectMs, ReadMs,
                CutsAsked, Pieces.Count(p => p is { Count: > 1 }), CutMs,
                routes.Wide, routes.Leaning, routes.AcrossKept, routes.AcrossSent);

        /// <summary>The page as <see cref="MangaOcrEngine.ReadAsync"/> has always handed it on.</summary>
        internal MangaPage Page(List<MangaBlock> blocks) => new(blocks, Long, Bubbles, DetectMs, ReadMs, Luma);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            // A read begun ahead and never finished with is still on the GPU: the gate is not given
            // back under it.
            try
            {
                _ahead?.Wait();
            }
            catch (AggregateException ex) when (ex.InnerException is not OperationCanceledException)
            {
                Log.Warn(ex.InnerException, "A manga read begun ahead failed with nothing waiting for it");
            }
            catch (AggregateException)
            {
            }
            _owner.EndRead();
        }
    }

    // Under _gate. Warmed up too (see WarmUp), when asked, before IsLoading says it is done.
    private bool EnsureLoaded(bool warmUp = false)
    {
        if (_detector is not null) return true;

        DirectMlDevice.Adapter adapter;
        lock (_sync)
        {
            _adapter ??= _chooseAdapter();
            if (_adapter.Value.Adapter is not { } chosen) return false;
            adapter = chosen;
            _loading = true;
        }

        try
        {
            _beforeLoad?.Invoke();
            if (!Load(adapter)) return false;
            if (warmUp) WarmUp();
            return _detector is not null;
        }
        finally
        {
            lock (_sync) _loading = false;
        }
    }

    // Under _gate.
    private bool Load(DirectMlDevice.Adapter adapter)
    {
        var timer = Stopwatch.StartNew();
        if (DirectMlDevice.Direct3D12Problem(adapter) is { } direct3D)
        {
            Fail(MangaUnavailable.NoGpu, direct3D, null);
            return false;
        }

        // Before any DirectML session, and none at all without it: see DirectMlDevice.
        if (DirectMlDevice.Load(_store.RuntimePath, _store.Manifest?.DirectMl?.Files[0]) is { } runtimeProblem)
        {
            Fail(MangaUnavailable.LoadFailed, $"DirectML could not be loaded: {runtimeProblem}", null);
            return false;
        }

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
            using (var columnOptions = Options())
                _columns = new MangaColumnDetector(_store.PathOf("columns"), columnOptions);
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
            Fail(MangaUnavailable.LoadFailed, $"loading on {adapter.Name} failed: {Readable(ex.Message)}", ex);
            Unload();
            return false;
        }
    }

    /// <summary>
    /// Says in the log, once, that vertical Japanese is being read with the column pipeline and why:
    /// not downloaded, switched off, no GPU — none of which says anything otherwise. Once for each
    /// reason in a row, not for every frame; a page read with the models starts it over.
    /// </summary>
    private void NoteColumnsInstead(string reason)
    {
        lock (_sync)
        {
            if (_columnsInsteadLogged == reason) return;
            _columnsInsteadLogged = reason;
        }
        Log.Info("Vertical Japanese is read with the column pipeline: {Reason}", reason);
    }

    private void Fail(MangaUnavailable kind, string reason, Exception? ex)
    {
        lock (_sync) _failure = (kind, reason);
        Log.Warn(ex, "Manga models unavailable, vertical text falls back to the column pipeline: {Reason}", reason);
    }

    // Under _gate.
    private void Unload()
    {
        if (_detector is null && _recognizer is null) return;
        _detector?.Dispose();
        _columns?.Dispose();
        _recognizer?.Dispose();
        _detector = null;
        _columns = null;
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
