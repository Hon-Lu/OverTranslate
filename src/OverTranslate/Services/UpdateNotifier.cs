using System.Windows.Threading;
using NLog;
using Velopack;

namespace OverTranslate.Services;

/// <summary>
/// The application's single source of truth for "is there a newer version", and the only thing that
/// goes looking for one.
/// </summary>
/// <remarks>
/// It exists because the main window cannot own this state. <see cref="Views.Shell.ShellWindow"/> is
/// created on demand and destroyed on close, while the application itself lives in the tray for
/// days, so a result found at startup — or four hours into a session with no window open — has
/// nowhere to live unless something outside the window holds it.
///
/// Nothing here shows anything. It answers a question and raises
/// <see cref="AvailabilityChanged"/>; deciding whether that warrants putting something on screen
/// belongs to the callers, and only one of them ever does — the startup check in App. Every
/// subsequent check is deliberately silent, which is the whole point of the periodic one: an
/// application that is left running for a week used to have exactly one chance to notice a release,
/// and taking that chance meant a dialog on every launch.
/// </remarks>
public static class UpdateNotifier
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <remarks>
    /// Paced for the user who never closes the application rather than for the release schedule.
    /// The cost is not a consideration here: one check is a request or two against GitHub's
    /// unauthenticated limit of 60 an hour, and the release listing it reads is tens of kilobytes.
    /// </remarks>
    private static readonly TimeSpan PollInterval = ResolvePollInterval();

    private static DispatcherTimer? _timer;

    /// <summary>
    /// The interval, or whatever OVERTRANSLATE_UPDATE_POLL_SECONDS overrides it to.
    /// </summary>
    /// <remarks>
    /// An hour is not a thing anyone can sit and verify, and the periodic check is the whole reason
    /// this class exists — so it gets the same treatment as the release itself (see
    /// <see cref="UpdateService"/>'s OVERTRANSLATE_FAKE_UPDATE): unset it behaves exactly as
    /// shipped, set it turns "wait an hour" into a few seconds.
    /// </remarks>
    private static TimeSpan ResolvePollInterval()
    {
        var raw = Environment.GetEnvironmentVariable("OVERTRANSLATE_UPDATE_POLL_SECONDS");
        if (double.TryParse(raw, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromHours(1);
    }

    /// <summary>The newest release found so far, or null while none has been seen.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>
    /// When a check last got an answer from the feed — up to date or a release found — or null
    /// while none has. Shared by the periodic check and the one the user asks for from the title
    /// bar's card, which shows it; a failed check leaves it where it was, since nothing was learned.
    /// </summary>
    public static DateTime? LastChecked { get; private set; }

    // Seams for the tests: where a check goes and what time it is.
    internal static Func<Task<UpdateCheckResult>> Checker = UpdateService.CheckAsync;
    internal static Func<DateTime> Clock = () => DateTime.Now;

    /// <summary>Puts the state back as a fresh process has it; tests only.</summary>
    internal static void ResetForTests()
    {
        Available = null;
        LastChecked = null;
        AvailabilityChanged = null;
        Checker = UpdateService.CheckAsync;
        Clock = () => DateTime.Now;
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="Available"/> starts pointing at a different version.
    /// </summary>
    public static event EventHandler? AvailabilityChanged;

    /// <summary>
    /// Begins checking every few hours. Does not check immediately — the caller's own startup check
    /// covers that moment and is the one allowed to prompt.
    /// </summary>
    public static void StartPolling()
    {
        if (_timer is not null) return;

        // Background priority: this is never what the user is waiting on, and the tick fires while
        // they may be mid-capture.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    /// <summary>
    /// Asks for the latest release and records it, along with when the feed was last read.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync()
    {
        UpdateCheckResult result;
        try
        {
            result = await Checker();
        }
        catch (Exception ex)
        {
            // Nothing awaits the timer's call, so an escaping exception would otherwise be silent.
            Log.Warn(ex, "Update check failed");
            return UpdateCheckResult.Failed;
        }

        if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.Available)
            LastChecked = Clock();
        else if (result.Status == UpdateCheckStatus.Failed)
            Log.Info("Update check could not reach the feed");

        // Only a release found changes Available. Up to date never clears it either: the feed
        // seeing nothing newer than this copy cannot make a release found earlier go away short of
        // its being pulled, and applying an update restarts the process, so a stale Available cannot
        // outlive the version it refers to. Keeping it means the title bar's pill never comes and goes.
        if (result.Update is not { } info) return result;

        if (Available?.LatestVersion != info.LatestVersion)
        {
            Available = info;
            AvailabilityChanged?.Invoke(null, EventArgs.Empty);

            // Fetched now so the title bar's entry can open the window with its notes already in hand.
            _ = ReleaseNotesService.GetForUpdateAsync(info);
        }

        return result;
    }

    /// <summary>
    /// Whether <paramref name="info"/> is covered by the user's 跳過此版本 choice.
    /// </summary>
    public static bool IsSkipped(UpdateInfo info)
    {
        var skipped = SettingsService.Instance.Current.SkippedUpdateVersion;
        if (string.IsNullOrWhiteSpace(skipped)) return false;

        // A stored value we cannot read is treated as no choice at all, so a hand-edited settings
        // file cannot silence updates permanently.
        if (!SemanticVersion.TryParse(skipped, out var skippedVersion))
        {
            Log.Warn("Ignoring unreadable SkippedUpdateVersion '{0}'", skipped);
            return false;
        }

        return info.VelopackInfo.TargetFullRelease.Version <= skippedVersion;
    }

    /// <summary>
    /// Records that the startup dialog should stay quiet until something newer than
    /// <paramref name="info"/> is released.
    /// </summary>
    public static void Skip(UpdateInfo info)
    {
        SettingsService.Instance.Current.SkippedUpdateVersion =
            info.VelopackInfo.TargetFullRelease.Version.ToString();
        SettingsService.Instance.Save();
    }
}
