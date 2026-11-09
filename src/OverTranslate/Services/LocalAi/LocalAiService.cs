using System.IO;
using System.Net.Http;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services.LocalAi;

/// <summary>A model ready to answer, for as long as the lease is held.</summary>
internal sealed record LocalAiReady(LocalAiLease Lease, string Format) : IDisposable
{
    public void Dispose() => Lease.Dispose();
}

/// <summary>What translating asks of 本機 AI 翻譯 — see <see cref="LocalAiProvider"/>.</summary>
internal interface ILocalAiBackend
{
    /// <summary>The model in use, started if it has to be.</summary>
    /// <exception cref="InvalidOperationException">Why it cannot be used, in the user's words.</exception>
    Task<LocalAiReady> ReadyAsync(CancellationToken token);
}

/// <summary>
/// 本機 AI 翻譯 as the application uses it: which model, downloading it, and the server that runs it.
/// </summary>
internal sealed class LocalAiService : ILocalAiBackend
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient WarmUpHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly object _sync = new();
    private bool _initializing;
    private Exception? _downloadFailure;
    private LocalAiModel? _failedModel;

    internal LocalAiService(LocalAiModelStore store)
    {
        Store = store;
        Host = new LocalAiServerHost(
            (launch, token) => LocalAiServerProcess.StartAsync(Store.Runtime!.ServerPath, launch, token),
            () => TimeSpan.FromMinutes(SettingsService.Instance.Current.LocalAi.EffectiveIdleMinutes),
            checkIdleEvery: TimeSpan.FromSeconds(15));
        Store.Changed += (_, _) => RaiseChanged();
        Host.Changed += (_, _) => RaiseChanged();
    }

    internal LocalAiModelStore Store { get; }

    internal LocalAiServerHost Host { get; }

    /// <summary>Raised when anything the settings window shows has changed; on any thread.</summary>
    internal event EventHandler? Changed;

    private static LocalAiSettings Settings => SettingsService.Instance.Current.LocalAi;

    /// <summary>The model picked in settings, downloaded or not.</summary>
    internal LocalAiModel? ChosenModel => Store.Catalog?.Find(Settings.ModelId);

    /// <summary>The model in use, when it is here to use.</summary>
    internal LocalAiModel? ActiveModel =>
        Store.Catalog?.Find(Settings.ModelId) is { } model && Store.IsDownloaded(model) ? model : null;

    /// <summary>True from the end of a download until the new model has answered once.</summary>
    internal bool Initializing
    {
        get
        {
            lock (_sync) return _initializing;
        }
    }

    /// <summary>Why the last download failed, and of which model; cleared by the next attempt.</summary>
    internal (LocalAiModel Model, Exception Error)? LastDownloadFailure
    {
        get
        {
            lock (_sync) return _failedModel is { } model ? (model, _downloadFailure!) : null;
        }
    }

    /// <summary>The dedicated graphics cards, asked once for this run.</summary>
    internal Task<IReadOnlyList<LocalAiGpuDevice>> GpusAsync() => LocalAiGpu.DetectAsync();

    /// <summary>
    /// Downloads a model and makes it the one in use, closing whatever was running.
    /// </summary>
    internal async Task DownloadAsync(LocalAiModel model)
    {
        lock (_sync)
        {
            _failedModel = null;
            _downloadFailure = null;
        }

        try
        {
            await Store.DownloadAsync(model).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Local AI model {Model:l} download failed", model.Id);
            lock (_sync)
            {
                _failedModel = model;
                _downloadFailure = ex;
            }

            RaiseChanged();
            return;
        }

        Use(model);
        await InitializeAsync(model).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a model the one in use and closes whatever was running. One that is not downloaded yet
    /// simply cannot translate until it is.
    /// </summary>
    internal void Use(LocalAiModel model)
    {
        var settings = SettingsService.Instance;
        if (settings.Current.LocalAi.ModelId != model.Id)
        {
            settings.Current.LocalAi.ModelId = model.Id;
            settings.Save();
            Host.Release();
        }

        RaiseChanged();
    }

    /// <summary>Removes a model, closing it first if it is the one in use.</summary>
    internal async Task DeleteAsync(LocalAiModel model)
    {
        if (Settings.ModelId == model.Id)
        {
            Host.Release();
            var settings = SettingsService.Instance;
            settings.Current.LocalAi.ModelId = "";
            settings.Save();
        }

        await Store.DeleteAsync(model);
        RaiseChanged();
    }

    /// <summary>Closes the model now. The next translation starts it again.</summary>
    internal void Release() => Host.Release();

    private LocalAiLaunch LaunchFor(LocalAiModel model) => new(model.Id, Store.PathOf(model));

    // Starts the model and has it read a screenful once. The first long prompt on a Vulkan driver
    // compiles the shaders, measured at ten seconds where the same prompt takes one and a half once
    // the driver has them cached — so it is paid here, while the window says 準備中, and not on the
    // first translation.
    private async Task InitializeAsync(LocalAiModel model)
    {
        lock (_sync) _initializing = true;
        RaiseChanged();
        try
        {
            using var lease = await Host.AcquireAsync(LaunchFor(model)).ConfigureAwait(false);
            await LocalAiCompletion.SendAsync(
                WarmUpHttp, lease.Process, LocalAiPromptFormat.Wrap(model.Format, WarmUpPrompt()),
                maxTokens: 8, timeout: LocalAiServerProcess.StartLimit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Shown by the state of the server, and met again by the first translation.
            Log.Warn(ex, "Local AI model {Model:l} could not be initialized", model.Id);
        }
        finally
        {
            lock (_sync) _initializing = false;
            RaiseChanged();
        }
    }

    private static string WarmUpPrompt() =>
        string.Join("\n", Enumerable.Range(1, 40).Select(i =>
            $"<{i}>The quick brown fox jumps over the lazy dog near the old stone bridge.</{i}>"));

    /// <inheritdoc/>
    public async Task<LocalAiReady> ReadyAsync(CancellationToken token)
    {
        var gpus = await GpusAsync().WaitAsync(token).ConfigureAwait(false);
        if (gpus.Count == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiNoGpu"));

        if (Store.Catalog is null || ActiveModel is not { } model)
        {
            if (Store.Downloading is not null)
                throw new InvalidOperationException(LocalizationService.Format(
                    "S.Error.LocalAiDownloading", (int)(Store.Fraction * 100)));
            throw new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiNoModel"));
        }

        try
        {
            var lease = await Host.AcquireAsync(LaunchFor(model), token).ConfigureAwait(false);
            return new LocalAiReady(lease, model.Format);
        }
        catch (LocalAiNoGpuException)
        {
            throw new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiNoGpu"));
        }
        catch (LocalAiStartFailedException ex)
        {
            throw new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiStartFailed"), ex);
        }
    }

    /// <summary>For app start: nothing yet — a partial download is kept for the retry.</summary>
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
