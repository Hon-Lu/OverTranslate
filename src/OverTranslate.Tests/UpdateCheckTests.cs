using System.IO;
using System.Net.Http;
using OverTranslate.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace OverTranslate.Tests;

// The update check has to tell "up to date", "not an installed build" and "the feed could not be
// read" apart, because the title bar's card says different things for each; it used to answer null
// for all three. Run against Velopack's own CheckForUpdatesAsync with the feed faked.
public sealed class UpdateCheckClassificationTests : IDisposable
{
    private readonly string _packages = Directory.CreateTempSubdirectory("ot-check-").FullName;
    private readonly FeedSource _source = new();

    public void Dispose() => Directory.Delete(_packages, recursive: true);

    [Fact]
    public async Task NothingNewerInTheFeed_IsUpToDate()
    {
        _source.Assets = [Asset("1.0.0")];

        var result = await UpdateService.CheckAsync(Manager("1.0.0"));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task ANewerRelease_IsAvailableAndCarriesItsVersion()
    {
        _source.Assets = [Asset("1.0.0"), Asset("1.2.0")];

        var result = await UpdateService.CheckAsync(Manager("1.0.0"));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("1.2.0", result.Update?.LatestVersion);
    }

    [Fact]
    public async Task AFeedThatCannotBeRead_IsFailed()
    {
        _source.Error = new HttpRequestException("No such host is known.");

        var result = await UpdateService.CheckAsync(Manager("1.0.0"));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task ABuildVelopackDoesNotManage_IsNotInstalledAndNeverAsksTheFeed()
    {
        _source.Error = new InvalidOperationException("the feed must not be read");

        var result = await UpdateService.CheckAsync(Manager(installedVersion: null));

        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
        Assert.Equal(0, _source.Reads);
    }

    private DeltaAwareUpdateManager Manager(string? installedVersion)
    {
        var updateExe = Path.Combine(_packages, "Update.exe");
        File.WriteAllBytes(updateExe, []);
        var locator = installedVersion is null
            ? new UninstalledLocator(_packages, updateExe)
            : new TestVelopackLocator("App", installedVersion, _packages, null, _packages, updateExe);
        return new DeltaAwareUpdateManager(_source, null, locator);
    }

    private static VelopackAsset Asset(string version) => new()
    {
        PackageId = "App",
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = $"App-{version}-full.nupkg",
    };

    // What an IDE run looks like to Velopack: no installed version to update from.
    private sealed class UninstalledLocator(string packages, string updateExe)
        : TestVelopackLocator("App", "1.0.0", packages, null, packages, updateExe)
    {
        public override SemanticVersion? CurrentlyInstalledVersion => null;
    }

    private sealed class FeedSource : IUpdateSource
    {
        public VelopackAsset[] Assets { get; set; } = [];
        public Exception? Error { get; set; }
        public int Reads { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
        {
            Reads++;
            if (Error is not null) throw Error;
            return Task.FromResult(new VelopackAssetFeed { Assets = Assets });
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress,
            CancellationToken cancelToken = default) => throw new NotSupportedException();
    }
}

// UpdateNotifier's record of when the feed last answered, shared by the hourly check and the one
// asked for from the card.
[Collection(nameof(UpdateNotifierState))]
public sealed class UpdateNotifierLastCheckedTests : IDisposable
{
    private DateTime _now = new(2026, 11, 26, 14, 20, 0);

    public UpdateNotifierLastCheckedTests()
    {
        UpdateNotifier.ResetForTests();
        UpdateNotifier.Clock = () => _now;
    }

    public void Dispose() => UpdateNotifier.ResetForTests();

    [Fact]
    public async Task AnUpToDateAnswer_RecordsTheTime()
    {
        UpdateNotifier.Checker = () => Task.FromResult(UpdateCheckResult.UpToDate);

        var result = await UpdateNotifier.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(_now, UpdateNotifier.LastChecked);
    }

    [Fact]
    public async Task EachAnswer_MovesItOn()
    {
        UpdateNotifier.Checker = () => Task.FromResult(UpdateCheckResult.UpToDate);
        await UpdateNotifier.CheckAsync();

        _now = _now.AddHours(1);
        await UpdateNotifier.CheckAsync();

        Assert.Equal(_now, UpdateNotifier.LastChecked);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.Failed)]
    [InlineData(UpdateCheckStatus.NotInstalled)]
    public async Task ACheckThatLearnedNothing_LeavesItWhereItWas(UpdateCheckStatus status)
    {
        UpdateNotifier.Checker = () => Task.FromResult(UpdateCheckResult.UpToDate);
        await UpdateNotifier.CheckAsync();
        var first = _now;

        _now = _now.AddHours(1);
        UpdateNotifier.Checker = () => Task.FromResult(new UpdateCheckResult(status));
        var result = await UpdateNotifier.CheckAsync();

        Assert.Equal(status, result.Status);
        Assert.Equal(first, UpdateNotifier.LastChecked);
    }

    [Fact]
    public async Task ACheckThatThrows_IsReportedAsFailed()
    {
        UpdateNotifier.Checker = () => throw new InvalidOperationException();

        var result = await UpdateNotifier.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(UpdateNotifier.LastChecked);
    }
}

[CollectionDefinition(nameof(UpdateNotifierState), DisableParallelization = true)]
public sealed class UpdateNotifierState;
