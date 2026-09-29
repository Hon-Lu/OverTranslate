using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public sealed class MangaModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-manga-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly byte[] Detector = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] Vocabulary = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\nあ\n");

    private static MangaModelManifest Manifest(string version = "1") => new(
        version,
        "https://models.example/v/",
        [
            new MangaModelFile("detector", "detector.onnx", Detector.Length, Hash(Detector)),
            new MangaModelFile("vocabulary", "vocab.txt", Vocabulary.Length, Hash(Vocabulary)),
        ]);

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task ADownload_EndsReady_WithEveryFileInTheVersionFolder()
    {
        var server = new FakeServer();
        var store = new MangaModelStore(Manifest(), _root, server) { RetryDelay = TimeSpan.Zero };
        Assert.Equal(MangaModelState.NotDownloaded, store.State);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
        Assert.Equal(Path.Combine(_root, "v1", "vocab.txt"), store.PathOf("vocabulary"));
        Assert.Equal(["https://models.example/v/detector.onnx", "https://models.example/v/vocab.txt"],
            server.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task AFileThatDoesNotHashRight_IsFetchedAgain_AndNeverCountsAsReady()
    {
        var server = new FakeServer { Corrupt = "vocab.txt" };
        var store = new MangaModelStore(Manifest(), _root, server) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync());

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.Equal(3, server.Requests.Count(r => r.Url.EndsWith("vocab.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "v1", "vocab.txt")));
    }

    [Fact]
    public async Task AnInterruptedFile_IsResumedWithARangeRequest()
    {
        var folder = Path.Combine(_root, "v1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "detector.onnx.part"), Detector[..1234]);
        var server = new FakeServer();
        var store = new MangaModelStore(Manifest(), _root, server) { RetryDelay = TimeSpan.Zero };

        await store.DownloadAsync();

        Assert.Equal(1234, server.Requests.First().RangeFrom);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task AServerThatIgnoresTheRange_StartsTheFileOver()
    {
        var folder = Path.Combine(_root, "v1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "detector.onnx.part"), Detector[..1234]);
        var server = new FakeServer { IgnoreRange = true };
        var store = new MangaModelStore(Manifest(), _root, server) { RetryDelay = TimeSpan.Zero };

        await store.DownloadAsync();

        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task AFailedRequest_IsRetried()
    {
        var server = new FakeServer { FailFirst = "detector.onnx" };
        var store = new MangaModelStore(Manifest(), _root, server) { RetryDelay = TimeSpan.Zero };

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(2, server.Requests.Count(r => r.Url.EndsWith("detector.onnx")));
    }

    [Fact]
    public async Task ANewVersion_IsNotReadyUntilDownloaded_AndReplacesTheOldOne()
    {
        await new MangaModelStore(Manifest("1"), _root, new FakeServer()).DownloadAsync();

        var newer = new MangaModelStore(Manifest("2"), _root, new FakeServer());
        Assert.Equal(MangaModelState.NotDownloaded, newer.State);

        await newer.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, newer.State);
        Assert.False(Directory.Exists(Path.Combine(_root, "v1")));
    }

    [Fact]
    public async Task ATruncatedFile_IsNoLongerReady()
    {
        var store = new MangaModelStore(Manifest(), _root, new FakeServer());
        await store.DownloadAsync();

        File.WriteAllBytes(store.PathOf("detector"), Detector[..10]);

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
    }

    [Fact]
    public async Task Delete_RemovesEverything_AndSaysSo()
    {
        var store = new MangaModelStore(Manifest(), _root, new FakeServer());
        await store.DownloadAsync();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Delete();

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.False(Directory.Exists(_root));
        Assert.True(changed > 0);
    }

    [Fact]
    public void WithoutAManifest_ThereIsNothingToDownload()
    {
        Assert.Equal(MangaModelState.Unavailable, new MangaModelStore(null, _root).State);
    }

    [Theory]
    [InlineData("""{"version":"1","baseUrl":"x","files":[]}""")]
    [InlineData("""{"version":"","baseUrl":"x","files":[{"role":"a","name":"a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"version":"1","baseUrl":"x","files":[{"role":"a","name":"../a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"version":"1","baseUrl":"x","files":[{"role":"a","name":"a","size":1,"sha256":"00"}]}""")]
    public void AMalformedManifest_IsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => MangaModelManifest.Parse(json));

    [Fact]
    public void TheShippedManifest_NamesEveryFileThePipelineOpens()
    {
        var manifest = MangaModelManifest.Load(MangaModelManifest.ShippedPath);

        foreach (var role in new[] { "detector", "encoder", "decoder-cross", "decoder-step", "vocabulary" })
            Assert.NotNull(manifest.File(role));
        Assert.InRange(manifest.TotalBytes, 300_000_000, 330_000_000);
    }

    [Fact]
    public void TheShippedManifest_PointsAtTheAssetsRepositoryRelease_ForItsVersion()
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath));

        Assert.Equal(
            $"https://github.com/Hon-Lu/OverTranslate-assets/releases/download/manga-vertical-v{manifest.Version}/",
            manifest.BaseUrl);
        Assert.Equal(
            $"https://github.com/Hon-Lu/OverTranslate-assets/releases/download/manga-vertical-v{manifest.Version}/vocab.txt",
            manifest.UrlOf(manifest.File("vocabulary")).ToString());
    }

    [Fact]
    public void TheModelsLive_InLocalAppData_OutsideTheInstallFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "OverTranslate-assets", "manga-vertical"), MangaModelStore.DefaultRoot);
        Assert.False(MangaModelStore.DefaultRoot.StartsWith(
            Path.Combine(local, "OverTranslate") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(MangaModelStore.DefaultRoot.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string? Corrupt { get; init; }
        public string? FailFirst { get; init; }
        public bool IgnoreRange { get; init; }
        public List<(string Url, long? RangeFrom)> Requests { get; } = [];
        private bool _failed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var from = request.Headers.Range?.Ranges.First().From;
            Requests.Add((url, from));

            if (FailFirst is not null && url.EndsWith(FailFirst) && !_failed)
            {
                _failed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var data = url.EndsWith("detector.onnx") ? Detector : Vocabulary;
            if (Corrupt is not null && url.EndsWith(Corrupt))
            {
                data = [.. data];
                data[0] ^= 0xFF;
            }

            if (from is { } start && !IgnoreRange)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(data[(int)start..]),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }
}
