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

    private static readonly byte[] Detector = Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] Vocabulary = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\nあ\n");

    private const string First = "https://first.example/v/{name}";
    private const string Second = "https://second.example/v/{name}";

    private static MangaModelManifest Manifest(string version = "1", params string[] sources) => new(
        version,
        [.. (sources.Length == 0 ? [First] : sources).Select((url, i) => new MangaModelSource($"source{i}", url, null))],
        [
            new MangaModelFile("detector", "detector.onnx", Detector.Length, Hash(Detector)),
            new MangaModelFile("vocabulary", "vocab.txt", Vocabulary.Length, Hash(Vocabulary)),
        ]);

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private MangaModelStore Store(MangaModelManifest manifest, FakeServer server) =>
        new(manifest, _root, server)
        {
            RetryDelay = TimeSpan.Zero,
            // Judged over a fraction of a second, so a slow source shows itself inside a test.
            SlowWindow = TimeSpan.FromMilliseconds(300),
            SlowBytesPerSecond = 200_000,
            StallTimeout = TimeSpan.FromMilliseconds(400),
        };

    private string PartFor(MangaModelManifest manifest, int source, string name) =>
        MangaModelStore.PartPath(Path.Combine(_root, "v" + manifest.Version, name),
            manifest.SourcesOf(manifest.Files.First(file => file.Name == name))[source].Url);

    [Fact]
    public async Task ADownload_EndsReady_WithEveryFileInTheVersionFolder()
    {
        var server = new FakeServer();
        var store = Store(Manifest(), server);
        Assert.Equal(MangaModelState.NotDownloaded, store.State);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
        Assert.Equal(Path.Combine(_root, "v1", "vocab.txt"), store.PathOf("vocabulary"));
        Assert.Equal(["https://first.example/v/detector.onnx", "https://first.example/v/vocab.txt"],
            server.Requests.Select(r => r.Url));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "v1"), "*.part"));
    }

    // ── Moving on to the next source ─────────────────────────────────────────

    [Fact]
    public async Task AFirstSourceThatKeepsFailing_IsRetried_ThenTheSecondIsUsed()
    {
        var server = new FakeServer { ["first.example"] = new() { Status = HttpStatusCode.ServiceUnavailable } };
        var store = Store(Manifest("1", First, Second), server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(3, server.Requests.Count(r => r.Url == "https://first.example/v/detector.onnx"));
        Assert.Equal(1, server.Requests.Count(r => r.Url == "https://second.example/v/detector.onnx"));
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task AFirstSourceWithTheWrongBytes_IsLeftAtOnce_ForTheSecond()
    {
        var server = new FakeServer { ["first.example"] = new() { Corrupt = true } };
        var store = Store(Manifest("1", First, Second), server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        // A whole file that does not hash right is that source's content; asking again changes nothing.
        Assert.Equal(1, server.Requests.Count(r => r.Url == "https://first.example/v/detector.onnx"));
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task ASlowFirstSource_IsLeftForTheSecond_AndItsPartCleanedUp()
    {
        // 20 KB/s against a 200 KB/s floor: 40 KB would take two seconds.
        var server = new FakeServer { ["first.example"] = new() { BytesPerTick = 1000 } };
        var manifest = Manifest("1", First, Second);
        var store = Store(manifest, server);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
        Assert.Contains(server.Requests, r => r.Url == "https://second.example/v/detector.onnx");
        Assert.InRange(timer.Elapsed.TotalSeconds, 0, 1.8);
        Assert.False(File.Exists(PartFor(manifest, 0, "detector.onnx")));
    }

    [Fact]
    public async Task WhenEverySourceIsSlow_TheFastestIsKept_AndResumedWhereItLeftOff()
    {
        var server = new FakeServer
        {
            ["first.example"] = new() { BytesPerTick = 500 },     // 10 KB/s
            ["second.example"] = new() { BytesPerTick = 3000 },   // 60 KB/s
        };
        var store = Store(Manifest("1", First, Second), server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
        var second = server.Requests.Where(r => r.Url == "https://second.example/v/detector.onnx").ToList();
        Assert.Equal(2, second.Count);
        Assert.Null(second[0].RangeFrom);
        Assert.True(second[1].RangeFrom > 0);
        // The slower one is not gone back to.
        Assert.Single(server.Requests, r => r.Url == "https://first.example/v/detector.onnx");
    }

    [Fact]
    public async Task EverySourceFailing_EndsAsAServerFailure()
    {
        var server = new FakeServer
        {
            ["first.example"] = new() { Status = HttpStatusCode.ServiceUnavailable },
            ["second.example"] = new() { Status = HttpStatusCode.NotFound },
        };
        var store = Store(Manifest("1", First, Second), server);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => store.DownloadAsync());

        Assert.Equal(MangaDownloadFailure.Server, MangaModelStore.Classify(ex));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(MangaModelState.NotDownloaded, store.State);
    }

    [Fact]
    public async Task EverySourceWithTheWrongBytes_EndsAsAChecksumFailure()
    {
        var server = new FakeServer
        {
            ["first.example"] = new() { Corrupt = true },
            ["second.example"] = new() { Corrupt = true },
        };
        var store = Store(Manifest("1", First, Second), server);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync());

        Assert.Equal(MangaDownloadFailure.Checksum, MangaModelStore.Classify(ex));
        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.False(File.Exists(Path.Combine(_root, "v1", "detector.onnx")));
    }

    [Fact]
    public async Task ATransferThatStalls_IsDropped_AndRetried()
    {
        var server = new FakeServer { ["first.example"] = new() { StallFirst = true } };
        var store = Store(Manifest(), server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(2, server.Requests.Count(r => r.Url.EndsWith("detector.onnx")));
    }

    // ── Resuming ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInterruptedFile_IsResumedWithARangeRequest_FromTheSameSource()
    {
        var manifest = Manifest("1", First, Second);
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
        File.WriteAllBytes(PartFor(manifest, 0, "detector.onnx"), Detector[..1234]);
        var server = new FakeServer();
        var store = Store(manifest, server);

        await store.DownloadAsync();

        Assert.Equal(("https://first.example/v/detector.onnx", (long?)1234), server.Requests.First());
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task APartFromOneSource_IsNeverResumedFromAnother()
    {
        var manifest = Manifest("1", First, Second);
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
        File.WriteAllBytes(PartFor(manifest, 0, "detector.onnx"), Detector[..1234]);
        var server = new FakeServer { ["first.example"] = new() { Status = HttpStatusCode.ServiceUnavailable } };
        var store = Store(manifest, server);

        await store.DownloadAsync();

        Assert.Null(server.Requests.Single(r => r.Url == "https://second.example/v/detector.onnx").RangeFrom);
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "v1"), "*.part"));
    }

    [Fact]
    public async Task APartThatDoesNotCheckOut_IsFetchedWholeOnceFromTheSameSource()
    {
        var manifest = Manifest();
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
        var wrong = Detector[..1234].ToArray();
        wrong[0] ^= 0xFF;
        File.WriteAllBytes(PartFor(manifest, 0, "detector.onnx"), wrong);
        var server = new FakeServer();
        var store = Store(manifest, server);

        await store.DownloadAsync();

        Assert.Equal([(long?)1234, null],
            server.Requests.Where(r => r.Url.EndsWith("detector.onnx")).Select(r => r.RangeFrom));
        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task AServerThatIgnoresTheRange_StartsTheFileOver()
    {
        var manifest = Manifest();
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
        File.WriteAllBytes(PartFor(manifest, 0, "detector.onnx"), Detector[..1234]);
        var server = new FakeServer { ["first.example"] = new() { IgnoreRange = true } };
        var store = Store(manifest, server);

        await store.DownloadAsync();

        Assert.Equal(Detector, File.ReadAllBytes(store.PathOf("detector")));
    }

    [Fact]
    public async Task APartLeftByAnOlderBuild_IsCleanedUp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
        File.WriteAllBytes(Path.Combine(_root, "v1", "detector.onnx.part"), Detector[..1234]);
        var store = Store(Manifest(), new FakeServer());

        await store.DownloadAsync();

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "v1"), "*.part"));
    }

    [Fact]
    public async Task AFailedRequest_IsRetried()
    {
        var server = new FakeServer { ["first.example"] = new() { FailFirst = true } };
        var store = Store(Manifest(), server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(2, server.Requests.Count(r => r.Url.EndsWith("detector.onnx")));
    }

    // ── State ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewVersion_IsNotReadyUntilDownloaded_AndReplacesTheOldOne()
    {
        await Store(Manifest("1"), new FakeServer()).DownloadAsync();

        var newer = Store(Manifest("2"), new FakeServer());
        Assert.Equal(MangaModelState.NotDownloaded, newer.State);

        await newer.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, newer.State);
        Assert.False(Directory.Exists(Path.Combine(_root, "v1")));
    }

    [Fact]
    public async Task ATruncatedFile_IsNoLongerReady()
    {
        var store = Store(Manifest(), new FakeServer());
        await store.DownloadAsync();

        File.WriteAllBytes(store.PathOf("detector"), Detector[..10]);

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
    }

    [Fact]
    public async Task Delete_RemovesEverything_AndSaysSo()
    {
        var store = Store(Manifest(), new FakeServer());
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

    // ── The manifest ─────────────────────────────────────────────────────────

    private const string AFile = """{"role":"a","name":"a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""";

    [Theory]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/{name}"}],"files":[]}""")]
    [InlineData("""{"version":"","sources":[{"name":"s","url":"https://x/{name}"}],"files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/{name}"}],"files":[{"role":"a","name":"../a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/{name}"}],"files":[{"role":"a","name":"a","size":1,"sha256":"00"}]}""")]
    [InlineData("""{"version":"1","files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[],"files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/a"}],"files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"ftp://x/{name}"}],"files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/{name}","files":["b"]}],"files":[""" + AFile + "]}")]
    [InlineData("""{"version":"1","sources":[{"name":"s","url":"https://x/{name}","files":[]}],"files":[""" + AFile + "]}")]
    public void AMalformedManifest_IsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => MangaModelManifest.Parse(json));

    [Fact]
    public void ASourceWithAFileList_OnlyServesThoseFiles()
    {
        var manifest = MangaModelManifest.Parse("""
            {"version":"1",
             "sources":[{"name":"one","url":"https://one/{name}","files":["a"]},
                        {"name":"two","url":"https://two/r/{name}"}],
             "files":[{"role":"a","name":"a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"},
                      {"role":"b","name":"b c","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}
            """);

        Assert.Equal(["https://one/a", "https://two/r/a"],
            manifest.SourcesOf(manifest.File("a")).Select(s => s.Url.AbsoluteUri));
        Assert.Equal(["https://two/r/b%20c"], manifest.SourcesOf(manifest.File("b")).Select(s => s.Url.AbsoluteUri));
    }

    [Fact]
    public void TheShippedManifest_NamesEveryFileThePipelineOpens()
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath));

        foreach (var role in new[] { "detector", "encoder", "decoder-cross", "decoder-step", "vocabulary" })
            Assert.NotNull(manifest.File(role));
        Assert.InRange(manifest.TotalBytes, 300_000_000, 330_000_000);
    }

    [Fact]
    public void TheShippedManifest_TriesHuggingFaceFirst_ThenTheGitHubRelease_ForEveryFile()
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath));
        var detectorRepo = "https://huggingface.co/hon-lu/comic-text-and-bubble-detector-onnx-fp16/resolve/v1/";
        var ocrRepo = "https://huggingface.co/hon-lu/manga-ocr-base-onnx-fp16/resolve/v1/";
        var release = $"https://github.com/Hon-Lu/OverTranslate-assets/releases/download/manga-vertical-v{manifest.Version}/";

        foreach (var file in manifest.Files)
        {
            var urls = manifest.SourcesOf(file).Select(s => s.Url.AbsoluteUri).ToList();
            var hub = file.Role == "detector" ? detectorRepo : ocrRepo;
            Assert.Equal([hub + file.Name, release + file.Name], urls);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8791/{name}", "http://127.0.0.1:8791/vocab.txt")]
    [InlineData("http://127.0.0.1:8791/models/", "http://127.0.0.1:8791/models/vocab.txt")]
    [InlineData("http://127.0.0.1:8791/models", "http://127.0.0.1:8791/models/vocab.txt")]
    public void TheOverride_IsTheOnlySource(string url, string expected)
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath)).WithOnlySource(url);

        Assert.Single(manifest.Sources);
        Assert.Equal([expected], manifest.SourcesOf(manifest.File("vocabulary")).Select(s => s.Url.AbsoluteUri));
        Assert.Single(manifest.SourcesOf(manifest.File("detector")));
    }

    // ── Failures, as the card words them ─────────────────────────────────────

    [Fact]
    public void DownloadFailures_AreSortedIntoWhatThePersonCanBeTold()
    {
        Assert.Equal(MangaDownloadFailure.Server,
            MangaModelStore.Classify(new HttpRequestException("404", null, HttpStatusCode.NotFound)));
        Assert.Equal(MangaDownloadFailure.Network, MangaModelStore.Classify(new HttpRequestException("no route")));
        Assert.Equal(MangaDownloadFailure.Network,
            MangaModelStore.Classify(new HttpIOException(HttpRequestError.ResponseEnded, "short")));
        Assert.Equal(MangaDownloadFailure.Network,
            MangaModelStore.Classify(new IOException("reset", new System.Net.Sockets.SocketException())));
        Assert.Equal(MangaDownloadFailure.Checksum, MangaModelStore.Classify(new InvalidDataException("hash")));
        Assert.Equal(MangaDownloadFailure.Disk, MangaModelStore.Classify(new IOException("disk full")));
        Assert.Equal(MangaDownloadFailure.Disk, MangaModelStore.Classify(new UnauthorizedAccessException()));
        Assert.Equal(MangaDownloadFailure.Other, MangaModelStore.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void TheModelsLive_WithTheSettings_OutsideTheInstallFolder()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(roaming, "OverTranslate", "models", "manga-vertical"), MangaModelStore.DefaultRoot);
        Assert.False(MangaModelStore.DefaultRoot.StartsWith(local, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Range-aware file server, one behaviour per host, standing in at the HttpClient's handler.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Host> _hosts = [];
        private readonly object _sync = new();

        public Host this[string host]
        {
            get => _hosts.TryGetValue(host, out var found) ? found : _hosts[host] = new Host();
            init => _hosts[host] = value;
        }

        public List<(string Url, long? RangeFrom)> Requests { get; } = [];

        internal sealed class Host
        {
            public HttpStatusCode? Status { get; init; }
            public bool Corrupt { get; init; }
            public bool FailFirst { get; init; }
            public bool StallFirst { get; init; }
            public bool IgnoreRange { get; init; }
            /// <summary>When set, the body trickles out this many bytes every 50ms.</summary>
            public int? BytesPerTick { get; init; }
            internal bool Failed;
            internal bool Stalled;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var from = request.Headers.Range?.Ranges.First().From;
            Host host;
            lock (_sync)
            {
                Requests.Add((url.AbsoluteUri, from));
                host = this[url.Host];
            }

            if (host.Status is { } status)
                return Task.FromResult(new HttpResponseMessage(status));
            if (host.FailFirst && url.AbsolutePath.EndsWith("detector.onnx") && !host.Failed)
            {
                host.Failed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var data = url.AbsolutePath.EndsWith("detector.onnx") ? Detector : Vocabulary;
            if (host.Corrupt)
            {
                data = [.. data];
                data[0] ^= 0xFF;
            }

            var partial = from is not null && !host.IgnoreRange;
            var body = partial ? data[(int)from!.Value..] : data;
            Stream stream = new MemoryStream(body);
            if (host.StallFirst && url.AbsolutePath.EndsWith("detector.onnx") && !host.Stalled)
            {
                host.Stalled = true;
                stream = new Trickle(body, 100, stallAfter: 1);
            }
            else if (host.BytesPerTick is { } perTick)
            {
                stream = new Trickle(body, perTick);
            }

            return Task.FromResult(new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
        }
    }

    /// <summary>A body that arrives a little at a time, or stops arriving.</summary>
    private sealed class Trickle(byte[] data, int perTick, int? stallAfter = null) : Stream
    {
        private int _position;
        private int _reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length) return 0;
            if (stallAfter is { } limit && _reads >= limit)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Delay(50, cancellationToken);
            _reads++;
            var count = Math.Min(Math.Min(perTick, buffer.Length), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
