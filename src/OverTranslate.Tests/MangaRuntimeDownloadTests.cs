using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The DirectML runtime as part of the manga model download: from the package a range at a time,
/// from the plain release file when that fails, and kept apart from the models.
/// </summary>
public sealed class MangaRuntimeDownloadTests : IDisposable
{
    private const string Entry = "bin/x64-win/DirectML.dll";
    private const string Package = "https://packages.example/directml.1.2.3.nupkg";
    private const string Release = "https://release.example/directml-1.2.3/{name}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-manga-runtime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Models => Path.Combine(_root, "models", "manga-vertical");
    private string Runtimes => Path.Combine(_root, "runtimes", "directml");

    private static readonly byte[] Library = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("MZ DirectML stand-in. ", 4000)));
    private static readonly byte[] Vocabulary = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\nあ\n");

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static MangaModelManifest Manifest(string runtimeVersion = "1.2.3", byte[]? library = null) =>
        new MangaModelManifest(
            "1",
            [new MangaModelSource("models", "https://models.example/{name}", null)],
            [new MangaModelFile("vocabulary", "vocab.txt", Vocabulary.Length, Hash(Vocabulary))])
        {
            DirectMl = new MangaModelManifest(
                runtimeVersion,
                [
                    new MangaModelSource("nuget", Package, ["DirectML.dll"]) { Entry = Entry },
                    new MangaModelSource("github", Release, null),
                ],
                [new MangaModelFile("directml", "DirectML.dll", (library ?? Library).Length, Hash(library ?? Library))]),
        };

    private MangaModelStore Store(Server server, MangaModelManifest? manifest = null, Func<string, string?>? check = null) =>
        new(manifest ?? Manifest(), Models, server, Runtimes)
        {
            RetryDelay = TimeSpan.Zero,
            CheckRuntime = check ?? (_ => null),
        };

    [Fact]
    public async Task TheRuntime_ComesFromThePackage_OnlyItsOwnBytesFetched()
    {
        var server = new Server(ZipEntryLocatorTests.Zip(noise: 300_000));
        var store = Store(server);
        var totals = new List<long>();
        store.Changed += (_, _) => totals.Add(store.TotalBytes);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Library, File.ReadAllBytes(Path.Combine(Runtimes, "1.2.3", "DirectML.dll")));
        Assert.Equal(store.RuntimePath, Path.Combine(Runtimes, "1.2.3", "DirectML.dll"));
        Assert.Empty(Directory.GetFiles(Path.Combine(Runtimes, "1.2.3"), "*.part"));
        Assert.DoesNotContain(server.Requests, r => r.Host == "release.example");
        // The tail, the local header and the DLL's compressed bytes — none of the 600 KB beside it.
        var deflated = server.EntryCompressedSize;
        Assert.Equal(ZipEntryLocator.TailLength + ZipEntryLocator.LocalHeaderLength + deflated, server.PackageBytesSent);
        // The total shown is what came over the wire: the compressed DLL and the models.
        Assert.Equal(deflated + Vocabulary.Length, store.DownloadedBytes);
        Assert.Contains(deflated + Vocabulary.Length, totals);
    }

    [Fact]
    public async Task APackageWithTheWrongBytes_IsLeftForTheReleaseFile()
    {
        // The same length, so only the hash can tell.
        var wrong = Library.ToArray();
        wrong[100] ^= 0xFF;
        var server = new Server(ZipEntryLocatorTests.Zip(library: wrong));
        var store = Store(server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Library, File.ReadAllBytes(store.RuntimePath!));
        Assert.Contains(server.Requests, r => r.Host == "release.example");
    }

    [Theory]
    [InlineData(Server.Failure.NotFound)]
    [InlineData(Server.Failure.IgnoresRanges)]
    [InlineData(Server.Failure.NotAZip)]
    public async Task APackageThatCannotBeRead_IsLeftForTheReleaseFile(Server.Failure failure)
    {
        var server = new Server(ZipEntryLocatorTests.Zip(noise: 300_000)) { PackageFailure = failure };
        var store = Store(server);

        await store.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, store.State);
        Assert.Equal(Library, File.ReadAllBytes(store.RuntimePath!));
        // A server that ignores ranges is not read to the end of a whole package.
        Assert.InRange(server.PackageBytesSent, 0, 70_000);
    }

    [Fact]
    public async Task ARuntimeWithoutTheSignature_FailsTheDownload_AndLeavesNothing()
    {
        var server = new Server(ZipEntryLocatorTests.Zip());
        var store = Store(server, check: _ => "signed by someone else");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync());

        Assert.Contains("signed by someone else", ex.Message);
        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.False(Directory.Exists(Path.Combine(Runtimes, "1.2.3")));
        Assert.False(File.Exists(Path.Combine(Models, "v1", "vocab.txt")));
    }

    [Fact]
    public async Task DeletingTheModels_KeepsTheRuntime_AndTheNextDownloadUsesItAgain()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();

        store.Delete();

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.False(Directory.Exists(Models));
        Assert.True(File.Exists(store.RuntimePath));
        Assert.Equal(Vocabulary.Length, store.TotalBytes);

        var server = new Server(ZipEntryLocatorTests.Zip());
        var again = Store(server);
        await again.DownloadAsync();

        Assert.Equal(MangaModelState.Ready, again.State);
        Assert.All(server.Requests, r => Assert.Equal("models.example", r.Host));
    }

    [Fact]
    public async Task DeletingTheModels_WhileTheRuntimeIsHeldOpen_StillSucceeds()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();

        // As a loaded DLL is: open, not deletable.
        using (new FileStream(store.RuntimePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            store.Delete();

        Assert.False(Directory.Exists(Models));
        Assert.True(File.Exists(store.RuntimePath));
    }

    [Fact]
    public async Task AKeptRuntimeThatNoLongerHashesRight_IsFetchedAgain()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();
        store.Delete();
        var bytes = File.ReadAllBytes(store.RuntimePath!);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(store.RuntimePath!, bytes);

        var server = new Server(ZipEntryLocatorTests.Zip());
        await Store(server).DownloadAsync();

        Assert.Contains(server.Requests, r => r.Host == "packages.example");
        Assert.Equal(Library, File.ReadAllBytes(store.RuntimePath!));
    }

    [Fact]
    public async Task ModelsWithoutTheRuntime_AreNotReady_AndOnlyTheRuntimeIsFetched()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();
        Directory.Delete(Runtimes, recursive: true);

        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.Equal(Library.Length, store.TotalBytes);

        var server = new Server(ZipEntryLocatorTests.Zip());
        await Store(server).DownloadAsync();

        Assert.DoesNotContain(server.Requests, r => r.Host == "models.example");
        Assert.Equal(MangaModelState.Ready, store.State);
    }

    [Fact]
    public async Task AtStart_OtherRuntimeVersions_AndAnUnfinishedOne_AreRemoved_TheCurrentKept()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();
        var older = Path.Combine(Runtimes, "1.0.0");
        Directory.CreateDirectory(older);
        File.WriteAllBytes(Path.Combine(older, "DirectML.dll"), [1, 2, 3]);

        store.DiscardIncomplete();

        Assert.False(Directory.Exists(older));
        Assert.Equal(MangaModelState.Ready, store.State);

        // An unfinished download of this version's runtime goes too.
        var newer = Store(new Server(ZipEntryLocatorTests.Zip()), Manifest("2.0.0"));
        Directory.CreateDirectory(newer.RuntimeFolder!);
        File.WriteAllBytes(newer.RuntimePath! + ".0123abcd.part", [1, 2, 3]);
        newer.DiscardIncomplete();
        Assert.False(Directory.Exists(newer.RuntimeFolder));
        Assert.False(Directory.Exists(Path.Combine(Runtimes, "1.2.3")));
    }

    [Fact]
    public async Task AnOlderRuntimeThatCannotBeRemoved_IsLeftForNextTime()
    {
        var store = Store(new Server(ZipEntryLocatorTests.Zip()));
        await store.DownloadAsync();
        var older = Path.Combine(Runtimes, "1.0.0");
        Directory.CreateDirectory(older);
        var dll = Path.Combine(older, "DirectML.dll");
        File.WriteAllBytes(dll, [1, 2, 3]);

        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
            store.DiscardIncomplete();
        Assert.True(File.Exists(dll));

        store.DiscardIncomplete();
        Assert.False(Directory.Exists(older));
    }

    // ── The manifest ─────────────────────────────────────────────────────────

    [Fact]
    public void TheShippedManifest_TakesDirectMlFromNuGetFirst_ThenTheAssetsRelease()
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath));
        var runtime = manifest.DirectMl!;
        var dll = Assert.Single(runtime.Files);

        Assert.Equal("DirectML.dll", dll.Name);
        Assert.Equal(18_527_776, dll.Size);
        var sources = runtime.SourcesOf(dll);
        Assert.Equal(2, sources.Count);
        Assert.Equal($"https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/{runtime.Version}/microsoft.ai.directml.{runtime.Version}.nupkg",
            sources[0].Url.AbsoluteUri);
        Assert.Equal("bin/x64-win/DirectML.dll", sources[0].Source.Entry);
        Assert.Equal($"https://github.com/Hon-Lu/OverTranslate-assets/releases/download/directml-{runtime.Version}/DirectML.dll",
            sources[1].Url.AbsoluteUri);
        Assert.Null(sources[1].Source.Entry);
    }

    [Fact]
    public void TheShippedManifest_NamesTheDirectMlVersionTheBuildReferences()
    {
        // The NuGet package the build restores is the one whose DLL the manifest pins.
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath));
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var restored = Path.Combine(packages, "microsoft.ai.directml", manifest.DirectMl!.Version, "bin", "x64-win", "DirectML.dll");
        if (!File.Exists(restored)) return;

        Assert.Equal(manifest.DirectMl.Files[0].Size, new FileInfo(restored).Length);
        Assert.Equal(manifest.DirectMl.Files[0].Sha256, Hash(File.ReadAllBytes(restored)));
    }

    [Fact]
    public void TheOverride_ReplacesTheRuntimeSourcesToo()
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(MangaModelManifest.ShippedPath))
            .WithOnlySource("http://127.0.0.1:8791/");
        var runtime = manifest.DirectMl!;

        Assert.Equal(["http://127.0.0.1:8791/DirectML.dll"],
            runtime.SourcesOf(runtime.Files[0]).Select(s => s.Url.AbsoluteUri));
    }

    private const string AFile = """{"role":"a","name":"a","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""";

    [Theory]
    // A zip source with {name}, or naming no file, or with an empty entry.
    [InlineData("1", """{"name":"n","url":"https://x/{name}","entry":"a","files":["a"]}""")]
    [InlineData("1", """{"name":"n","url":"https://x/p.zip","entry":"a"}""")]
    [InlineData("1", """{"name":"n","url":"https://x/p.zip","entry":"","files":["a"]}""")]
    // A version that would leave its folder.
    [InlineData("..", """{"name":"s","url":"https://x/{name}"}""")]
    [InlineData("a/b", """{"name":"s","url":"https://x/{name}"}""")]
    public void AMalformedRuntimeEntry_IsRefused(string version, string source) =>
        Assert.Throws<InvalidDataException>(() => MangaModelManifest.Parse(
            """{"version":"1","sources":[{"name":"s","url":"https://x/{name}"}],"files":[""" + AFile +
            "],\"directml\":{\"version\":\"" + version + "\",\"sources\":[" + source + "],\"files\":[" + AFile + "]}}"));

    [Fact]
    public void AWellFormedRuntimeEntry_IsAccepted() =>
        Assert.NotNull(MangaModelManifest.Parse(
            """{"version":"1","sources":[{"name":"s","url":"https://x/{name}"}],"files":[""" + AFile +
            """],"directml":{"version":"1.2.3","sources":[{"name":"n","url":"https://x/p.zip","entry":"bin/a","files":["a"]}],"files":[""" +
            AFile + "]}}").DirectMl);

    [Fact]
    public void AStoreForAManifestWithARuntime_NeedsSomewhereToPutIt() =>
        Assert.Throws<ArgumentNullException>(() => new MangaModelStore(Manifest(), Models));

    /// <summary>
    /// The package as a CDN serves it — ranges, suffix ranges included — the release file and the
    /// models, each by host.
    /// </summary>
    public sealed class Server : HttpMessageHandler
    {
        public enum Failure { None, NotFound, IgnoresRanges, NotAZip }

        public Server(byte[] package)
        {
            Package = package;
            using var zip = new ZipArchive(new MemoryStream(package));
            EntryCompressedSize = zip.GetEntry(Entry)!.CompressedLength;
        }

        public byte[] Package { get; }
        public long EntryCompressedSize { get; }
        public Failure PackageFailure { get; init; }
        public List<(string Host, string Path, RangeHeaderValue? Range)> Requests { get; } = [];
        public long PackageBytesSent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var range = request.Headers.Range;
            lock (Requests) Requests.Add((url.Host, url.AbsolutePath, range));

            return Task.FromResult(url.Host switch
            {
                "packages.example" => ServePackage(range),
                "release.example" => Whole(Library),
                "models.example" => Whole(Vocabulary),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Whole(byte[] data) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

        private HttpResponseMessage ServePackage(RangeHeaderValue? range)
        {
            var data = PackageFailure == Failure.NotAZip ? new byte[Package.Length] : Package;
            if (PackageFailure == Failure.NotFound)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (PackageFailure == Failure.IgnoresRanges || range is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Counted(data, this)) };

            var item = range.Ranges.Single();
            long from = item.From ?? Math.Max(0, data.Length - item.To!.Value);
            long to = item.From is null ? data.Length - 1 : Math.Min(item.To ?? data.Length - 1, data.Length - 1);
            var body = data[(int)from..(int)(to + 1)];
            var content = new StreamContent(new Counted(body, this));
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }

        /// <summary>Counts what is actually read off the response, not what it could have been.</summary>
        private sealed class Counted(byte[] data, Server server) : MemoryStream(data)
        {
            public override int Read(byte[] buffer, int offset, int count) => Note(base.Read(buffer, offset, count));

            // MemoryStream's span reads of a derived type come back through Read(byte[]), counted there.
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                Task.FromResult(Read(buffer, offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
                ValueTask.FromResult(Read(buffer.Span));

            private int Note(int read)
            {
                Interlocked.Add(ref server.PackageBytesSent, read);
                return read;
            }
        }
    }
}
