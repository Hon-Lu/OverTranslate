using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OverTranslate.Services.LocalAi;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LocalAiModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-localai-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly byte[] Weights = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 13)).ToArray();

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static LocalAiModel Model(string id = "m", byte[]? weights = null) => new(
        id, "M", "M", true, "r", id + ".gguf", $"https://models.example/{id}.gguf",
        (weights ?? Weights).Length, Hash(weights ?? Weights), 1000, "gemma-4", "S.X");

    /// <summary>A store whose runtime is already in place, so only the model is fetched.</summary>
    private LocalAiModelStore Store(Serve serve, params LocalAiModel[] models)
    {
        var runtime = new LocalAiRuntime("b1", 1, new string('0', 64), ["https://runtime.example/z.zip"]);
        var catalog = new LocalAiCatalog(runtime, models);
        var downloader = new LocalAiDownloader(serve) { RetryDelay = TimeSpan.Zero, StallTimeout = TimeSpan.FromSeconds(5) };
        var runtimeRoot = Path.Combine(_root, "rt");
        var runtimeStore = new LocalAiRuntimeStore(runtime, runtimeRoot, downloader);
        Directory.CreateDirectory(runtimeStore.Folder);
        File.WriteAllText(runtimeStore.ServerPath, "x");
        File.WriteAllText(Path.Combine(runtimeStore.Folder, "verified.json"), "{\"version\":\"b1\"}");
        return new LocalAiModelStore(catalog, Path.Combine(_root, "models"), runtimeStore, downloader);
    }

    [Fact]
    public async Task ADownload_EndsDownloaded_AndThePhaseWalksThroughVerifying()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        var phases = new List<LocalAiDownloadPhase>();
        store.Changed += (_, _) => { if (phases.LastOrDefault() != store.Phase) phases.Add(store.Phase); };
        Assert.False(store.IsDownloaded(model));

        await store.DownloadAsync(model);

        Assert.True(store.IsDownloaded(model));
        Assert.Equal([LocalAiDownloadPhase.Downloading, LocalAiDownloadPhase.Verifying, LocalAiDownloadPhase.None], phases);
        Assert.Equal([model], store.Downloaded);
        Assert.Null(store.Downloading);
    }

    [Fact]
    public async Task Deleting_WaitsForAFileThatIsStillHeld()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        await store.DownloadAsync(model);

        var held = new FileStream(store.PathOf(model), FileMode.Open, FileAccess.Read, FileShare.None);
        var deleting = store.DeleteAsync(model, TimeSpan.FromSeconds(10));
        await Task.Delay(250);
        Assert.False(deleting.IsCompleted);
        held.Dispose();

        await deleting;
        Assert.False(File.Exists(store.PathOf(model)));
        Assert.Empty(store.Downloaded);
    }

    [Fact]
    public async Task Deleting_GivesUpOnAFileThatStaysHeld()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        await store.DownloadAsync(model);

        using var held = new FileStream(store.PathOf(model), FileMode.Open, FileAccess.Read, FileShare.None);
        var failure = await Record.ExceptionAsync(() => store.DeleteAsync(model, TimeSpan.FromMilliseconds(200)));

        Assert.True(failure is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task AFailedDownload_KeepsItsBytes_AndTheRetryCarriesOn()
    {
        var model = Model();
        var failing = new Serve(Weights) { FailAlwaysAfter = 100_000 };
        var store = Store(failing, model);

        await Assert.ThrowsAnyAsync<Exception>(() => store.DownloadAsync(model));

        Assert.False(store.IsDownloaded(model));
        var part = Directory.EnumerateFiles(store.FolderOf(model), "*.part").Single();
        Assert.Equal(100_000, new FileInfo(part).Length);

        failing.FailAlwaysAfter = 0;
        failing.Ranges.Clear();
        await store.DownloadAsync(model);

        Assert.True(store.IsDownloaded(model));
        Assert.Equal(100_000, failing.Ranges[0]);
        Assert.Empty(Directory.EnumerateFiles(store.FolderOf(model), "*.part"));
    }

    [Fact]
    public async Task ARetry_ShowsWhatIsAlreadyHere_BeforeTheServerHasAnswered()
    {
        var model = Model();
        var failing = new Serve(Weights) { FailAlwaysAfter = 100_000 };
        var store = Store(failing, model);
        await Assert.ThrowsAnyAsync<Exception>(() => store.DownloadAsync(model));

        failing.FailAlwaysAfter = 0;
        long? shownWhenAsked = null;
        failing.OnRequest = () => shownWhenAsked ??= store.DownloadedBytes;
        await store.DownloadAsync(model);

        Assert.Equal(100_000, shownWhenAsked);
    }

    [Fact]
    public async Task CancellingADownload_TakesItsBytesWithIt()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        store.Changed += (_, _) =>
        {
            if (store.DownloadedBytes > 0) store.CancelDownload();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(model));

        Assert.False(Directory.Exists(store.FolderOf(model)));
    }

    [Fact]
    public async Task AFileChangedAfterTheDownload_IsNoLongerDownloaded()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        await store.DownloadAsync(model);

        File.WriteAllBytes(store.PathOf(model), [1, 2, 3]);

        Assert.False(store.IsDownloaded(model));
    }

    [Fact]
    public async Task DeletingAModel_RemovesItsFolder()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);
        await store.DownloadAsync(model);

        store.Delete(model);

        Assert.False(store.IsDownloaded(model));
        Assert.False(Directory.Exists(store.FolderOf(model)));
    }

    [Fact]
    public void WithTheRuntimeInPlace_OnlyTheModelCountsTowardsTheSize()
    {
        var model = Model();
        var store = Store(new Serve(Weights), model);

        Assert.Equal(model.Size, store.DownloadSize(model));
        Assert.Equal(model.Size, store.SpaceNeeded(model));
    }

    [Fact]
    public void TheFreeSpaceIsOfTheDriveTheModelsAreOn()
    {
        var store = Store(new Serve(Weights), Model());

        var free = store.FreeSpace();

        Assert.NotNull(free);
        Assert.Equal(Path.GetPathRoot(_root)!.TrimEnd('\\', ':'), free!.Value.Drive);
        Assert.True(free.Value.FreeBytes > 0);
    }

    private sealed class Serve(byte[] file) : HttpMessageHandler
    {
        public int FailAlwaysAfter { get; set; }
        public List<long?> Ranges { get; } = [];
        public Action? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnRequest?.Invoke();
            var from = request.Headers.Range?.Ranges.First().From;
            Ranges.Add(from);
            var start = (int)(from ?? 0);
            if (FailAlwaysAfter > 0 && start >= FailAlwaysAfter)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));

            var body = FailAlwaysAfter > 0 ? file[start..FailAlwaysAfter] : file[start..];
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };
            if (from is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, file.Length - 1, file.Length);
            return Task.FromResult(response);
        }
    }
}
