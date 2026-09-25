using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace OverTranslate.Services;

public sealed record UpdateInfo(
    string LatestVersion,
    DeltaAwareUpdateManager Manager,
    VelopackUpdateInfo VelopackInfo);

/// <summary>
/// Velopack's UpdateManager, with two changes to how its download treats the deltas: it says out
/// loud when it gives up on them, and an unfinished download no longer throws away what they need.
/// </summary>
/// <remarks>
/// Velopack catches a failed delta, logs a warning and starts the full package with nothing raised.
/// The window used to infer the switch from the first figure lower than the last, but the full
/// download only reports every 3%, which is several megabytes — a minute or more on a slow link,
/// spent under a line that still said the patches were being merged. This override sits on the
/// method Velopack itself falls back from, so the signal arrives the moment the fallback begins.
/// </remarks>
/// <param name="locator">For tests only, as on Velopack's own constructor; the app leaves it null.</param>
public sealed class DeltaAwareUpdateManager(IUpdateSource source, UpdateOptions? options, IVelopackLocator? locator = null)
    : UpdateManager(source, options, locator)
{
    /// <summary>
    /// Raised on a worker thread when the deltas are abandoned and the full package is about to be
    /// fetched. Not raised for a cancelled attempt, which falls back only to throw straight away.
    /// </summary>
    public Action? FellBackToFull { get; set; }

    // The installed version's full package, for the length of one download; see CleanPackagesExcept.
    private string? _basePackage;

    public override async Task DownloadUpdatesAsync(
        VelopackUpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        _basePackage = updates.BaseRelease?.FileName is { } name ? BasePackagePath(name) : null;
        try
        {
            await base.DownloadUpdatesAsync(updates, progress, cancelToken);
        }
        finally
        {
            _basePackage = null;
        }
    }

    /// <summary>
    /// Velopack's clean-up, except that a download which did not finish keeps the installed
    /// version's full package.
    /// </summary>
    /// <remarks>
    /// Velopack runs this on the way out of every download, finished or not, and it deletes every
    /// package but the target — the base the deltas are merged into included. After a finished
    /// download that costs nothing: the new full package takes over as the next update's base. After
    /// 取消更新 it cost the retry its deltas, and the user most likely to cancel and retry is the one
    /// on a slow link, who is then made to fetch the whole package in place of a few megabytes. So
    /// when the target is not there the base is spared, and everything else goes as Velopack would
    /// have it: the deltas are re-fetched anyway, and a ".partial" is never resumed.
    /// </remarks>
    protected override void CleanPackagesExcept(string? assetToKeep)
    {
        if (_basePackage is null || assetToKeep is null || File.Exists(assetToKeep) || !File.Exists(_basePackage))
        {
            base.CleanPackagesExcept(assetToKeep);
            return;
        }

        Log.Log(VelopackLogLevel.Information, "Download did not finish; keeping base package for the next attempt: " + _basePackage, null);

        var packagesDir = Locator.PackagesDir!;
        var leftovers = Directory.EnumerateFiles(packagesDir, "*.nupkg")
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_basePackage), StringComparison.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(packagesDir, "*.partial"))
            .ToArray();

        foreach (var path in leftovers)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Log(VelopackLogLevel.Warning, "Failed to delete partial package: " + path, ex);
            }
        }
    }

    /// <remarks>
    /// Refuses up front when the package the deltas apply to is gone — cleaned up by a version of
    /// the app from before <see cref="CleanPackagesExcept"/> kept it, or removed by hand. The
    /// UpdateInfo from the check still promises deltas either way, and left alone Velopack would
    /// download every one of them only for the merge to fail on the missing base, then fetch the
    /// full package it could have started on. Throwing here lands in the same fallback, minus the
    /// waste.
    /// </remarks>
    protected override async Task DownloadAndApplyDeltaUpdates(
        VelopackUpdateInfo updates, string targetFile, Action<int> progress, CancellationToken cancelToken)
    {
        try
        {
            var basePackage = BasePackagePath(updates.BaseRelease!.FileName);
            if (!File.Exists(basePackage))
                throw new FileNotFoundException("Base package for delta updates is missing.", basePackage);

            await base.DownloadAndApplyDeltaUpdates(updates, targetFile, progress, cancelToken);
        }
        catch when (!cancelToken.IsCancellationRequested)
        {
            FellBackToFull?.Invoke();
            throw;
        }
    }

    // Where Velopack itself keeps it (its GetLocalPackagePath is internal). That also runs the name
    // through a filename sanitiser, which a name from our own feed never needs.
    private string BasePackagePath(string fileName) => Path.Combine(Locator.PackagesDir!, fileName);
}

public static class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/Hon-Lu/OverTranslate";
    private const string StableChannel = "win";
    private const string BetaChannel = "beta";

    // How many times the faked check has been asked; see OVERTRANSLATE_FAKE_UPDATE_AFTER.
    private static int _fakeCheckCount;

    public static async Task<UpdateInfo?> CheckAsync()
    {
        var fake = CreateFakeUpdate();
        if (fake is not null)
            return fake;

        try
        {
            var manager = CreateManager();
            // In-app updates are only available for the installed Velopack build.
            if (!manager.IsInstalled)
                return null;

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return null;

            return new UpdateInfo(
                update.TargetFullRelease.Version.ToString(),
                manager,
                update);
        }
        catch
        {
            return null;
        }
    }

    /// <param name="onApplying">
    /// Raised once the download has genuinely finished, before the (blocking, progress-less) apply
    /// step begins. Callers must not rely on <paramref name="onProgress"/> reaching 100 to detect
    /// this: Velopack budgets its progress across download/extract/delta-merge phases and regularly
    /// stops reporting partway (a delta update commonly ends around 70), which would otherwise leave
    /// the UI reading "downloading" while the update is already being applied. Awaited, so the
    /// caller can repaint before ApplyUpdatesAndRestart takes over the thread and closes the app.
    /// </param>
    /// <param name="onFellBackToFull">
    /// Raised on a worker thread when the deltas are abandoned for the full package; see
    /// <see cref="DeltaAwareUpdateManager"/>. Progress starts again from zero after it.
    /// </param>
    /// <param name="cancelToken">
    /// Abandons the download. Safe for the whole of it: Velopack writes to "&lt;package&gt;.partial"
    /// and renames it only once the package is complete, so what a cancelled attempt leaves behind
    /// is a partial file the next attempt deletes. Not honoured during the delta merge, though —
    /// Velopack waits on Update.exe for up to five minutes without looking at it, and the request
    /// only takes effect once that returns. It is not passed to the apply step, which does not take
    /// one and must not be interrupted — that step replaces the application's own files and then
    /// restarts the process.
    /// </param>
    public static async Task DownloadAndApplyAsync(
        UpdateInfo info, Action<int>? onProgress = null, Action? onFellBackToFull = null,
        Func<Task>? onApplying = null, CancellationToken cancelToken = default)
    {
        info.Manager.FellBackToFull = onFellBackToFull;
        try
        {
            await info.Manager.DownloadUpdatesAsync(info.VelopackInfo, onProgress, cancelToken);
        }
        finally
        {
            info.Manager.FellBackToFull = null;
        }

        cancelToken.ThrowIfCancellationRequested();

        if (onApplying is not null)
            await onApplying();

        info.Manager.ApplyUpdatesAndRestart(info.VelopackInfo);
    }

    /// <summary>
    /// Pretends a release exists, when OVERTRANSLATE_FAKE_UPDATE names a version. Returns null —
    /// meaning "check for real" — when it does not.
    /// </summary>
    /// <remarks>
    /// Everything the update notification does now — the startup dialog, 跳過此版本, the nav rail's
    /// row, the periodic re-check — is decided before a single byte is downloaded, and none of it
    /// can be exercised without a release to react to. Without this hook the only ways to see any of
    /// it are publishing to GitHub, which pushes the release at real users, or installing a private
    /// build, which cannot show the "no update yet" side at all. It also unblocks running from the
    /// IDE, where <see cref="UpdateManager.IsInstalled"/> is false and the real check therefore
    /// always answers null.
    ///
    /// An environment variable rather than a setting or a #if, matching OVERTRANSLATE_CHANNEL below:
    /// it does nothing whatsoever unless deliberately set, it needs no build of its own, and it can
    /// be pointed at a Release build. Downloading is the one thing it cannot fake — there is no
    /// package behind the version it invents, so 立即更新 will fail and report so. That path is
    /// unchanged by this work; test it against a real release.
    ///
    /// OVERTRANSLATE_FAKE_UPDATE_AFTER holds the release back for that many checks, which is the
    /// only way to see the half that matters most: a release that did not exist at launch has to
    /// reach the nav rail of an already-open window without a dialog appearing. Left unset, the
    /// release is there from the first check and the startup dialog is what you get.
    /// </remarks>
    private static UpdateInfo? CreateFakeUpdate()
    {
        var version = Environment.GetEnvironmentVariable("OVERTRANSLATE_FAKE_UPDATE");
        if (string.IsNullOrWhiteSpace(version))
            return null;

        if (!SemanticVersion.TryParse(version, out var parsed))
            return null;

        var checksSoFar = Interlocked.Increment(ref _fakeCheckCount);
        if (int.TryParse(Environment.GetEnvironmentVariable("OVERTRANSLATE_FAKE_UPDATE_AFTER"), out var after)
            && checksSoFar <= after)
        {
            return null;
        }

        var asset = new VelopackAsset
        {
            PackageId = "OverTranslate",
            Version = parsed,
            Type = VelopackAssetType.Full,
            FileName = $"OverTranslate-{parsed}-full.nupkg"
        };

        return new UpdateInfo(
            parsed.ToString(),
            CreateManager(),
            new VelopackUpdateInfo(asset, false, null!, []));
    }

    /// <remarks>
    /// OVERTRANSLATE_UPDATE_REPO points the update feed at a second GitHub repository — a staging
    /// repo with no users — so a release can be rehearsed end to end without one reaching anybody.
    /// It keeps <see cref="GithubSource"/> in the loop, so the release listing, the asset lookup and
    /// the HTTP download are all the code that ships; only the repository differs.
    ///
    /// Rehearsing in the real repo cannot be made safe. A release there is visible to every user on
    /// the channel the moment it exists, and the only thing separating a rehearsal from a real launch
    /// is remembering to tick pre-release. The staging repo removes that lever entirely, and lets the
    /// stable (win) channel be rehearsed too, which pre-releases by definition cannot.
    ///
    /// OVERTRANSLATE_UPDATE_TOKEN carries a PAT for it. Supplying one lets the staging repo stay
    /// private, which is worth doing — rehearsal builds are then not downloadable by anyone who
    /// wanders past. Verified against a private repo: Velopack authenticates both the feed request
    /// and the asset download, including the redirect to GitHub's CDN. Omit it for a public one.
    ///
    /// OVERTRANSLATE_UPDATE_PRERELEASE makes pre-releases visible without switching channel, which
    /// the beta channel cannot do: it moves both levers at once, and a beta subscriber reads
    /// releases.beta.json — a different feed, holding differently-named packages. Releases built by
    /// CI are packed for the stable channel and merely flagged pre-release, so testing them needs
    /// exactly this: see the pre-release, stay on win. What gets tested is then the same bytes users
    /// receive once the flag is cleared, rather than a parallel beta build of the same source.
    ///
    /// Unset — the only state a user's machine is ever in — all three do nothing and the real
    /// repository is used exactly as before. Distinct from <see cref="CreateFakeUpdate"/>, which
    /// invents a version with no package behind it: enough to drive the notification UI, never
    /// enough to download.
    /// </remarks>
    private static DeltaAwareUpdateManager CreateManager()
    {
        // 設 OVERTRANSLATE_CHANNEL=beta → 訂閱 beta 先行版管線；未設 → 穩定版 (win)。
        var envChannel = Environment.GetEnvironmentVariable("OVERTRANSLATE_CHANNEL");
        var isBeta = string.Equals(envChannel, BetaChannel, StringComparison.OrdinalIgnoreCase);
        var channel = isBeta ? BetaChannel : StableChannel;
        var options = new UpdateOptions { ExplicitChannel = channel };

        // beta 的 GitHub Release 會標記為 pre-release，需 prerelease:true 才找得到。
        // 設 OVERTRANSLATE_UPDATE_PRERELEASE（"0" 以外的任何值）可單獨打開這個開關而不換 channel。
        var prereleaseOverride = Environment.GetEnvironmentVariable("OVERTRANSLATE_UPDATE_PRERELEASE");
        var seesPrerelease = isBeta
            || (!string.IsNullOrWhiteSpace(prereleaseOverride) && prereleaseOverride != "0");

        var repoUrl = Environment.GetEnvironmentVariable("OVERTRANSLATE_UPDATE_REPO");
        var token = string.IsNullOrWhiteSpace(repoUrl)
            ? null
            : Environment.GetEnvironmentVariable("OVERTRANSLATE_UPDATE_TOKEN");
        if (string.IsNullOrWhiteSpace(repoUrl))
            repoUrl = GitHubRepoUrl;

        return new DeltaAwareUpdateManager(new GithubSource(repoUrl, token, prerelease: seesPrerelease), options);
    }
}
