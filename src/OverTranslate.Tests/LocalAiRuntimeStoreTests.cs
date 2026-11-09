using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OverTranslate.Services.LocalAi;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LocalAiRuntimeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-localai-rt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Zip(params string[] names)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }

        return memory.ToArray();
    }

    private const string Official = "https://official.example/llama.zip";
    private const string Backup = "https://backup.example/llama.zip";

    private LocalAiRuntimeStore Store(byte[] zip, Serve serve) => new(
        new LocalAiRuntime("b1", zip.Length, Hash(zip), [Official, Backup]),
        _root,
        new LocalAiDownloader(serve) { RetryDelay = TimeSpan.Zero, StallTimeout = TimeSpan.FromSeconds(5) });

    [Theory]
    [InlineData("llama-server.exe", true)]
    [InlineData("llama-server-impl.dll", true)]
    [InlineData("llama.dll", true)]
    [InlineData("llama-common.dll", true)]
    [InlineData("ggml-vulkan.dll", true)]
    [InlineData("LICENSE-curl", true)]
    [InlineData("llama-cli.exe", false)]
    [InlineData("llama-cli-impl.dll", false)]
    [InlineData("readme.txt", false)]
    [InlineData("../llama-server.exe", false)]
    public void OnlyWhatTheServerLoadsIsUnpacked(string entry, bool needed) =>
        Assert.Equal(needed, LocalAiRuntimeStore.IsNeeded(entry));

    [Fact]
    public async Task ADownload_UnpacksTheServerAndDropsTheZip()
    {
        var zip = Zip("llama-server.exe", "ggml.dll", "llama-cli.exe");
        var store = Store(zip, new Serve(_ => zip));
        Assert.False(store.IsReady);

        await store.EnsureAsync();

        Assert.True(store.IsReady);
        Assert.True(File.Exists(store.ServerPath));
        Assert.True(File.Exists(Path.Combine(store.Folder, "ggml.dll")));
        Assert.False(File.Exists(Path.Combine(store.Folder, "llama-cli.exe")));
        Assert.False(File.Exists(Path.Combine(store.Folder, "llama.zip")));
    }

    [Fact]
    public async Task TheBackupIsUsedWhenTheOfficialReleaseIsGone()
    {
        var zip = Zip("llama-server.exe");
        var serve = new Serve(url => url.AbsoluteUri == Official ? null : zip);
        var store = Store(zip, serve);

        await store.EnsureAsync();

        Assert.True(store.IsReady);
        Assert.Equal([Official, Backup], serve.Asked.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task AFileThatHashesWrongFromEverySource_IsRefused_AndNothingIsReady()
    {
        var zip = Zip("llama-server.exe");
        var tampered = zip.ToArray();
        tampered[10] ^= 0xFF;
        var store = Store(zip, new Serve(_ => tampered));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAsync());

        Assert.False(store.IsReady);
        Assert.False(File.Exists(store.ServerPath));
    }

    [Fact]
    public async Task ADownloadThatDrops_CarriesOnFromWhereItStopped()
    {
        var zip = Zip("llama-server.exe", "ggml.dll", "a.dll", "b.dll");
        var serve = new Serve(_ => zip) { DropFirstAfter = zip.Length / 2 };
        var store = Store(zip, serve);

        await store.EnsureAsync();

        Assert.True(store.IsReady);
        Assert.Contains(serve.Ranges, range => range == zip.Length / 2);
    }

    /// <summary>Serves bytes per URL, honouring a range; a null answer is a 404.</summary>
    private sealed class Serve(Func<Uri, byte[]?> files) : HttpMessageHandler
    {
        private bool _dropped;

        public int DropFirstAfter { get; init; }
        public List<Uri> Asked { get; } = [];
        public List<long?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            var from = request.Headers.Range?.Ranges.First().From;
            Ranges.Add(from);

            if (files(request.RequestUri!) is not { } file)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var start = (int)(from ?? 0);
            var drop = DropFirstAfter > 0 && !_dropped;
            _dropped |= drop;
            var body = file[start..];
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(drop ? new Dropping(body, DropFirstAfter) : new MemoryStream(body)),
            };
            if (from is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, file.Length - 1, file.Length);
            return Task.FromResult(response);
        }

        // Gives so many bytes, then fails as a lost connection does.
        private sealed class Dropping(byte[] data, int after) : MemoryStream(data)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var left = after - (int)Position;
                if (left <= 0) throw new IOException("connection lost");
                return base.ReadAsync(buffer[..Math.Min(buffer.Length, left)], cancellationToken);
            }
        }
    }
}
