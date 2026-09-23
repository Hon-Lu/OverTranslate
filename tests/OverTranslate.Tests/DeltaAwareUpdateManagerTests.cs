using System.IO;
using System.Security.Cryptography;
using OverTranslate.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace OverTranslate.Tests;

// Runs Velopack's real DownloadUpdatesAsync against a packages directory in a temp folder, with the
// network replaced by a source that writes whatever the test says. What is pinned is what the
// override adds on top: the base package surviving a download that did not finish, the fallback
// being announced when it happens and not when the user cancelled, and a missing base costing no
// delta download at all.
public sealed class DeltaAwareUpdateManagerTests : IDisposable
{
    private readonly string _packages = Directory.CreateTempSubdirectory("ot-update-").FullName;

    private readonly VelopackAsset _base = Asset("1.0.0", VelopackAssetType.Full, [1, 2, 3]);
    private readonly VelopackAsset _delta = Asset("2.0.0", VelopackAssetType.Delta, [4, 5, 6]);
    private readonly VelopackAsset _target = Asset("2.0.0", VelopackAssetType.Full, [7, 8, 9, 10]);

    private readonly FakeSource _source = new();
    private int _fellBack;

    public void Dispose() => Directory.Delete(_packages, recursive: true);

    [Fact]
    public async Task CancelledFullDownload_KeepsTheBaseAndClearsTheRest()
    {
        WritePackage(_base);
        File.WriteAllBytes(Path.Combine(_packages, "App-1.5.0-delta.nupkg"), [0]);
        using var cancel = new CancellationTokenSource();
        _source.OnDownload = (_, file, _) =>
        {
            File.WriteAllBytes(file, [0]);
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager().DownloadUpdatesAsync(Update(deltas: []), cancelToken: cancel.Token));

        Assert.Equal(new[] { "App-1.0.0-full.nupkg" }, PackagesOnDisk());
    }

    [Fact]
    public async Task FinishedDownload_LeavesVelopacksCleanupAlone()
    {
        WritePackage(_base);
        _source.OnDownload = WriteReal;

        await CreateManager().DownloadUpdatesAsync(Update(deltas: []));

        // The new full package is the next update's base, so the old one going is correct.
        Assert.Equal(new[] { "App-2.0.0-full.nupkg" }, PackagesOnDisk());
    }

    [Fact]
    public async Task MissingBase_SkipsTheDeltasAndSaysSo()
    {
        _source.OnDownload = WriteReal;

        await CreateManager().DownloadUpdatesAsync(Update(deltas: [_delta]));

        Assert.Equal(new[] { _target.FileName }, _source.Requested);
        Assert.Equal(1, _fellBack);
    }

    [Fact]
    public async Task DeltaFailingAfterDownload_IsAnnouncedAsTheFallback()
    {
        // A delta that arrives corrupt fails after it is in, the same side of the download as a
        // merge that does not match its checksum.
        WritePackage(_base);
        _source.OnDownload = (asset, file, token) =>
        {
            if (asset.Type == VelopackAssetType.Delta)
            {
                File.WriteAllBytes(file, [0, 0, 0]);
                return Task.CompletedTask;
            }

            return WriteReal(asset, file, token);
        };

        await CreateManager().DownloadUpdatesAsync(Update(deltas: [_delta]));

        Assert.Equal(new[] { _delta.FileName, _target.FileName }, _source.Requested);
        Assert.Equal(1, _fellBack);
    }

    [Fact]
    public async Task CancelDuringDeltas_IsNotAnnouncedAsTheFallback()
    {
        WritePackage(_base);
        using var cancel = new CancellationTokenSource();
        _source.OnDownload = (_, _, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager().DownloadUpdatesAsync(Update(deltas: [_delta]), cancelToken: cancel.Token));

        Assert.Equal(0, _fellBack);
        Assert.Equal(new[] { "App-1.0.0-full.nupkg" }, PackagesOnDisk());
    }

    private DeltaAwareUpdateManager CreateManager()
    {
        // Velopack refuses the deltas outright unless an Update.exe exists; it is never run here.
        var updateExe = Path.Combine(_packages, "Update.exe");
        File.WriteAllBytes(updateExe, []);
        var locator = new TestVelopackLocator("App", "1.0.0", _packages, null, null, updateExe);
        return new DeltaAwareUpdateManager(_source, null, locator) { FellBackToFull = () => _fellBack++ };
    }

    private VelopackUpdateInfo Update(VelopackAsset[] deltas) => new(_target, false, _base, deltas);

    private void WritePackage(VelopackAsset asset) =>
        File.WriteAllBytes(Path.Combine(_packages, asset.FileName), Contents[asset.FileName]);

    private static Task WriteReal(VelopackAsset asset, string file, CancellationToken _)
    {
        File.WriteAllBytes(file, Contents[asset.FileName]);
        return Task.CompletedTask;
    }

    private string[] PackagesOnDisk() =>
        Directory.EnumerateFiles(_packages).Select(Path.GetFileName)
            .Where(name => name!.EndsWith(".nupkg") || name.EndsWith(".partial"))
            .Order().ToArray()!;

    private static readonly Dictionary<string, byte[]> Contents = [];

    private static VelopackAsset Asset(string version, VelopackAssetType type, byte[] contents)
    {
        var fileName = $"App-{version}-{(type == VelopackAssetType.Full ? "full" : "delta")}.nupkg";
        lock (Contents) Contents[fileName] = contents;

        return new VelopackAsset
        {
            PackageId = "App",
            Version = SemanticVersion.Parse(version),
            Type = type,
            FileName = fileName,
            Size = contents.Length,
            SHA1 = Convert.ToHexString(SHA1.HashData(contents)),
            SHA256 = Convert.ToHexString(SHA256.HashData(contents)),
        };
    }

    private sealed class FakeSource : IUpdateSource
    {
        public List<string> Requested { get; } = [];

        public Func<VelopackAsset, string, CancellationToken, Task> OnDownload { get; set; } =
            (_, _, _) => Task.CompletedTask;

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null) => throw new NotSupportedException();

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress,
            CancellationToken cancelToken = default)
        {
            lock (Requested) Requested.Add(releaseEntry.FileName);
            return OnDownload(releaseEntry, localFile, cancelToken);
        }
    }
}
