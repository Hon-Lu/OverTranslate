using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Services;

namespace OverTranslate.Views.Shell;

public partial class UpdateWindow : Window
{
    private static UpdateWindow? _instance;

    // True while the notes are being waited for, so a second press cannot open a second window.
    private static bool _opening;

    /// <summary>
    /// How long the window will wait for its notes before opening without them. Opening first and
    /// filling in afterwards would have it change height in front of the user.
    /// </summary>
    private static readonly TimeSpan NotesPatience = TimeSpan.FromSeconds(3);

    /// <summary>The height with several versions listed. A smaller screen gets what it has; see ApplyMode.</summary>
    private const double TallestWindow = 580;

    // Null when the window is only showing the release history, with nothing on offer.
    private readonly UpdateInfo? _updateInfo;
    private readonly ReleaseNotesFeed? _feed;
    private Phase _phase = Phase.Offering;

    // Alive only for the length of one download attempt.
    private CancellationTokenSource? _cancel;

    // Runs once per download attempt; see StartSlowHintTimer.
    private DispatcherTimer? _slowHintTimer;

    // What the running attempt is fetching. Reset per attempt; see OnFellBackToFull.
    private Fetching _fetching;

    /// <summary>
    /// Opens the update window, or brings the open one forward.
    /// </summary>
    /// <remarks>
    /// Two entry points reach this now — the startup check and the nav rail's 有新版本 — and the
    /// rail's is a button the user can press while the window it opens is already on screen behind
    /// the shell. A second instance would be a second download button for the same release.
    /// </remarks>
    public static void ShowOrActivate(UpdateInfo info) =>
        Open(ReleaseNotesService.GetForUpdateAsync(info), info);

    /// <summary>
    /// Opens the window with nothing on offer, to read what recent releases brought.
    /// </summary>
    /// <remarks>
    /// The same window because it is the same reading, and a second copy of the list would have to be
    /// kept looking like this one by hand.
    /// </remarks>
    public static void ShowHistory() => Open(ReleaseNotesService.GetHistoryAsync(), null);

    /// <summary>
    /// Whether a request should replace the window on screen, rather than simply bring it forward.
    /// </summary>
    /// <remarks>
    /// The update takes priority. Asked for while the history is open, it replaces the history: bringing
    /// the history forward would leave the only way to the update being to close it and ask again. The
    /// other way round the update stays, which also means a running download is never interrupted.
    /// </remarks>
    public static bool ShouldReplace(bool openIsHistory, bool wantHistory) => openIsHistory && !wantHistory;

    private bool IsHistory => _updateInfo is null;

    private static async void Open(Task<ReleaseNotesFeed?> notes, UpdateInfo? info)
    {
        if (_instance is { } open && !ShouldReplace(open.IsHistory, wantHistory: info is null))
        {
            open.Activate();
            return;
        }

        if (_opening) return;
        _opening = true;

        ReleaseNotesFeed? feed;
        try
        {
            feed = await ReleaseNotesService.WithinAsync(notes, NotesPatience);
        }
        finally
        {
            _opening = false;
        }

        var previous = _instance;

        var window = new UpdateWindow(info, feed);
        _instance = window;
        window.Closed += (_, _) =>
        {
            if (_instance == window) _instance = null;
        };

        // Where the old one was rather than the middle of the screen, and opened before it is closed,
        // so this reads as the window changing instead of one leaving and another arriving.
        if (previous is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = previous.Left;
            window.Top = previous.Top;
        }

        window.Show();
        previous?.Close();
    }

    public UpdateWindow(UpdateInfo? info, ReleaseNotesFeed? feed)
    {
        InitializeComponent();
        _updateInfo = info;

        // Releases with nothing to read are no different from having no notes: a list of nothing but
        // links would look broken, where the window's own answer to that already says what to do.
        _feed = feed?.HasAnythingToRead(LocalizationService.Current) == true ? feed : null;

        var compactIcon = AppIconService.CreateCompactIcon();
        Icon = compactIcon;
        TitleIcon.Source = compactIcon;

        ApplyMode();
        ApplyLanguage();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;

        // The system title bar is gone, so what it used to do for itself is done here: the rounded
        // corner and the outer edge, in the application's own border colour rather than the
        // system's. The edge is the compositor's, so it has to be handed over again on a theme
        // change — a DynamicResource never reaches it.
        WindowFrame.Attach(this);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowFrame.ApplyAppearance(this);

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    /// <summary>
    /// Sets up what depends on what the window was opened with, none of which changes afterwards.
    /// </summary>
    /// <remarks>
    /// Several versions get the full height from the start and keep it, so opening a short version
    /// after a long one does not resize the window under the pointer. A single version has nothing to
    /// switch between and is sized to what it says. The ceiling applies to both, and follows the screen
    /// on a display too small to give the full height.
    /// </remarks>
    private void ApplyMode()
    {
        var history = _updateInfo is null;
        var hasNotes = _feed is not null;

        NotesList.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        ExplanationText.Visibility = hasNotes && !history ? Visibility.Visible : Visibility.Collapsed;
        NoNotesCard.Visibility = hasNotes ? Visibility.Collapsed : Visibility.Visible;

        // Everything between the notes and the buttons brings its own space when the notes are there;
        // without them the card above already ends with its own margin.
        if (!hasNotes)
        {
            ErrorText.Margin = new Thickness(0, 0, 0, 12);
            ProgressCard.Margin = new Thickness(0, 0, 0, 12);
        }

        UpdateAnswers.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        SkipLine.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryCloseBtn.Visibility = history ? Visibility.Visible : Visibility.Collapsed;

        if (history)
        {
            HeadingText.SetResourceReference(TextBlock.TextProperty, "S.Update.HistoryHeading");
            SetResourceReference(TitleProperty, "S.Update.HistoryHeading");
            HeadingGlyph.Text = "\uE81C";
            VersionArrow.Visibility = Visibility.Collapsed;
            LatestVersionText.Visibility = Visibility.Collapsed;

            if (!hasNotes)
            {
                NoNotesText.SetResourceReference(TextBlock.TextProperty, "S.Update.HistoryUnavailable");
                NotesBeforeRun.Text = "";
            }
        }

        MaxHeight = Math.Min(TallestWindow, SystemParameters.WorkArea.Height);
        if (_feed?.Shown.Count > 1)
        {
            SizeToContent = SizeToContent.Manual;
            Height = MaxHeight;
        }
    }

    /// <summary>Everything in the window that is composed rather than bound to a resource.</summary>
    private void ApplyLanguage()
    {
        // "v" on all of these, matching the rail's version line and its update chip — the number is the
        // same number, and dropping the prefix here would make it look like a different notation.
        var current = $"v{UpdateService.InstalledVersion()}";
        var history = _updateInfo is null;

        if (history)
        {
            CurrentVersionText.Text = LocalizationService.Format("S.Update.HistoryCurrent", current);
        }
        else
        {
            CurrentVersionText.Text = current;
            LatestVersionText.Text = $"v{_updateInfo!.LatestVersion}";
        }

        if (_feed is null) return;

        var spansSeveral = !history && _feed.Total > 1;
        VersionCountText.Visibility = spansSeveral ? Visibility.Visible : Visibility.Collapsed;
        if (spansSeveral)
        {
            var count = _feed.TotalIsPartial ? $"{_feed.Total}+" : _feed.Total.ToString(CultureInfo.CurrentCulture);
            VersionCountText.Text = "· " + LocalizationService.Format("S.Update.IncludesVersions", count);
        }

        NotesList.Show(_feed, history ? ReleaseNotesTag.Current : ReleaseNotesTag.Latest);
    }

    /// <summary>
    /// Starts the update, or — while one is downloading — abandons it.
    /// </summary>
    /// <remarks>
    /// One button for both because they are the same answer to the same question, asked twice: this
    /// is the control for the update, and pressing it does the thing the update is not currently
    /// doing. A separate cancel button would have to appear from nowhere when the download starts,
    /// which is a row of two buttons becoming three under the user's hand.
    /// </remarks>
    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_phase == Phase.Downloading)
        {
            // Velopack only notices at its next checkpoint, which can be a while off, so the button
            // says it heard straight away rather than looking as if the press did nothing.
            _cancel?.Cancel();
            SetPhase(Phase.Downloading);
            return;
        }

        // The button is disabled past this point, but a press that slips through must not start a
        // second download under the one that is finishing.
        if (_phase != Phase.Offering)
            return;

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;

        try
        {
            SetPhase(Phase.Downloading);
            ErrorText.Visibility = Visibility.Collapsed;
            FallbackNote.Visibility = Visibility.Collapsed;
            _fetching = WillFetchDelta() ? Fetching.Delta : Fetching.Full;
            DownloadProgress.Value = 0;
            SetDownloadStatus(0);
            StartSlowHintTimer();

            await UpdateService.DownloadAndApplyAsync(
                _updateInfo!, OnDownloadProgress, OnFellBackToFull, OnApplyingAsync, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // The user's own decision, so the window simply goes back to offering: no error line,
            // and the button says 立即更新 again rather than 重試, which would imply something failed.
            ResetToOffer();
        }
        catch (Exception ex)
        {
            SetPhase(Phase.Offering);
            StopSlowHintTimer();
            SlowHint.Visibility = Visibility.Collapsed;
            FallbackNote.Visibility = Visibility.Collapsed;
            // The button is the way to try again, so it says so — this is the one thing about it
            // that changes, now that the progress no longer lives on its label.
            DownloadBtnText.Text = LocalizationService.Get("S.Update.Retry");
            DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            DownloadProgress.IsIndeterminate = false;
            SetStatus(null);
            ErrorText.Text = LocalizationService.Format("S.Update.Failed", ex.Message);
            ErrorText.Visibility = Visibility.Visible;
            ExplanationText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _cancel = null;
        }
    }

    /// <summary>Puts the window back the way it opened, after a cancelled download.</summary>
    private void ResetToOffer()
    {
        SetPhase(Phase.Offering);
        StopSlowHintTimer();
        SlowHint.Visibility = Visibility.Collapsed;
        FallbackNote.Visibility = Visibility.Collapsed;
        DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        SetStatus(null);
    }

    private enum Phase
    {
        /// <summary>Nothing is running; the update is being offered.</summary>
        Offering,

        /// <summary>Fetching the package. Abandonable.</summary>
        Downloading,

        /// <summary>
        /// The bytes are in and Velopack is working on them: Update.exe merging the deltas, or the
        /// new Update.exe being pulled out of the full package. Velopack waits on the merge without
        /// looking at the cancel token, so a 取消更新 pressed there would sit unanswered for as long
        /// as it takes; and once a full package is down, throwing it away for the sake of the
        /// seconds left before the handover is not a choice worth offering.
        /// </summary>
        Finishing,

        /// <summary>Handing over to Velopack. Not abandonable, and nearly over.</summary>
        Applying,
    }

    /// <summary>Which package the running attempt is fetching.</summary>
    /// <remarks>
    /// Velopack can start on the delta packages and end up fetching the whole thing anyway. This
    /// follows that switch, so the line under the bar always names the file actually coming down —
    /// which is also the only way its size means anything.
    /// </remarks>
    private enum Fetching
    {
        /// <summary>The deltas, to be merged into the installed version by Update.exe.</summary>
        Delta,

        /// <summary>The whole package.</summary>
        Full,
    }

    /// <summary>Velopack's UpdateOptions.MaximumDeltasBeforeFallback default, which this app leaves alone.</summary>
    private const int MaximumDeltas = 10;

    /// <summary>
    /// Velopack budgets the delta download across 0-70 and only reports 100 once Update.exe has
    /// merged the patches. So 70 means the bytes are all in and the merge is running — a stretch
    /// with no progress of its own, which reads as a stall at an arbitrary number unless the line
    /// underneath stops claiming to be downloading.
    /// </summary>
    private const int DeltaDownloadCeiling = 70;

    /// <summary>
    /// Whether Velopack will start this attempt on the deltas rather than the full package.
    /// </summary>
    /// <remarks>
    /// Mirrors the guards inside Velopack's own DownloadUpdatesAsync — a base package has to be on
    /// disk, a delta has to be offered, and the deltas must be neither too many nor larger than the
    /// full package. Duplicated because the library exposes no way to ask. It decides a caption and
    /// a file size and nothing else, so the cost of drifting out of step with a future Velopack is
    /// a line that names the wrong file for a moment, not a download that misbehaves:
    /// <see cref="OnFellBackToFull"/> corrects it the moment Velopack gives up on the deltas.
    /// </remarks>
    private bool WillFetchDelta()
    {
        var info = _updateInfo!.VelopackInfo;
        if (info.BaseRelease?.FileName is null || info.DeltasToTarget.Length == 0)
            return false;

        return info.DeltasToTarget.Length <= MaximumDeltas
            && info.DeltasToTarget.Sum(d => d.Size) <= info.TargetFullRelease.Size;
    }

    private long CurrentDownloadSize() =>
        _fetching == Fetching.Delta
            ? _updateInfo!.VelopackInfo.DeltasToTarget.Sum(d => d.Size)
            : _updateInfo!.VelopackInfo.TargetFullRelease.Size;

    /// <summary>Sizes a download the way Explorer does: kilobytes below a megabyte, megabytes above.</summary>
    /// <remarks>
    /// Not fixed to megabytes. A delta for a release that only touched a few small files runs to
    /// tens of kilobytes, and "0.0 MB" sitting next to a live percentage reads as a bug rather than
    /// as a small file.
    ///
    /// The space before the unit is non-breaking. The line can wrap in the longer languages, and left
    /// to itself WPF will happily put "139.8" on one line and "MB" on the next.
    /// </remarks>
    private static string FormatSize(long bytes)
    {
        const double Kilobyte = 1024d;
        const double Megabyte = Kilobyte * 1024d;

        return bytes >= Megabyte
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / Megabyte)
            : string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Max(1d, bytes / Kilobyte));
    }

    /// <summary>
    /// Moves the window between offering the update, running it, and handing over.
    /// </summary>
    /// <remarks>
    /// Downloading and applying are not the same kind of wait, and treating them as one is what
    /// used to leave a user stranded in front of a window they could not dismiss. The download —
    /// the long half, and the half that stalls when the release CDN is slow — writes to a ".partial"
    /// file and is abandoned safely at any point, so the way out stays open for all of it. Once the
    /// bytes are in — the delta merge, or 準備安裝… after the full package — the button goes dead
    /// for those seconds rather than accept a press it cannot act on, or one that would only throw
    /// away a finished download. Applying replaces the application's own files and restarts the
    /// process; there is no way back from half of that, so everything goes dead, the title bar's
    /// close included. The close button says why rather than simply refusing — SetResourceReference
    /// rather than a fetched string, so the reason follows a language changed in 設定 while this
    /// window is still on screen.
    ///
    /// A cancel already asked for turns the button into 取消中… and takes it away: the press has
    /// been heard, and there is nothing left for a second one to do.
    /// </remarks>
    private void SetPhase(Phase phase)
    {
        _phase = phase;

        DismissBtn.IsEnabled = phase == Phase.Offering;
        SkipVersionLink.IsEnabled = phase == Phase.Offering;
        CloseBtn.IsEnabled = phase == Phase.Offering;

        var cancelRequested = _cancel?.IsCancellationRequested == true;
        DownloadBtn.IsEnabled = phase == Phase.Offering || (phase == Phase.Downloading && !cancelRequested);

        var cancelling = phase is Phase.Downloading or Phase.Finishing;
        DownloadBtnText.Text = LocalizationService.Get(
            !cancelling ? "S.Update.Now" : cancelRequested ? "S.Update.Cancelling" : "S.Update.Cancel");
        DownloadBtnGlyph.Text = cancelling ? "" : "";

        ProgressCard.Visibility = phase == Phase.Offering ? Visibility.Collapsed : Visibility.Visible;
        HeadingText.SetResourceReference(TextBlock.TextProperty, phase switch
        {
            Phase.Offering => "S.Update.Heading",
            Phase.Downloading => "S.Update.HeadingDownloading",
            _ => "S.Update.HeadingInstalling",
        });

        // The progress card takes the explanation's place: by now it has been acted on. A failure
        // takes it back out again, once its message is up.
        if (_feed is not null)
            ExplanationText.Visibility = phase == Phase.Offering ? Visibility.Visible : Visibility.Collapsed;

        if (phase == Phase.Offering) CloseBtn.ToolTip = null;
        else CloseBtn.SetResourceReference(ToolTipProperty, "S.Update.CloseBlocked");
    }

    /// <summary>
    /// Puts up the "this is taking too long" line, once, after <see cref="SlowHintDelay"/>.
    /// </summary>
    /// <remarks>
    /// A download that is merely slow never fails, so <see cref="ErrorText"/> never appears and the
    /// window goes on saying 下載中 for as long as it takes — which, when GitHub's release-asset
    /// path is having a bad day, is tens of minutes for a delta that normally lands in seconds.
    /// Nothing else on screen distinguishes that from a wait the user should simply sit through.
    ///
    /// Delayed rather than always on: on a healthy connection the whole thing is over well inside
    /// the delay, and a standing offer to go and do it by hand would be noise in front of every
    /// update. The link is not touched by <see cref="SetPhase"/>, so it stays live while the
    /// buttons around it are disabled — fetching the installer by hand is the one thing left that
    /// the user can usefully do — and if they take it, 取消更新 is right there to let go of this.
    ///
    /// Counts download time only. The delta merge is not a download and a slow one says nothing
    /// about the connection, so the timer stops for it, as it does once the full package is in; if
    /// the merge fails and the full package starts, that is a fresh download and gets a fresh delay.
    /// </remarks>
    private static readonly TimeSpan SlowHintDelay = TimeSpan.FromSeconds(180);

    private void StartSlowHintTimer()
    {
        StopSlowHintTimer();
        SlowHint.Visibility = Visibility.Collapsed;

        _slowHintTimer = new DispatcherTimer { Interval = SlowHintDelay };
        _slowHintTimer.Tick += (_, _) =>
        {
            StopSlowHintTimer();
            SlowHint.Visibility = Visibility.Visible;
        };
        _slowHintTimer.Start();
    }

    private void StopSlowHintTimer()
    {
        _slowHintTimer?.Stop();
        _slowHintTimer = null;
    }

    /// <summary>Sets the progress card's line, and clears its figure: this is a stretch with none.</summary>
    private void SetStatus(string? text)
    {
        StatusText.Text = text ?? "";
        PercentText.Text = "";
    }

    /// <summary>
    /// The progress card: what is being fetched and how big it is, how far in — or, once the bytes
    /// are in, what is being done with them.
    /// </summary>
    /// <remarks>
    /// Naming the file is what keeps the fallback from looking like a fault. When Velopack gives up
    /// on the deltas it starts the full package from zero, and a line that only ever said
    /// "downloading 45%" left the bar dropping back to the start with nothing to explain it. Saying
    /// "full installer" alongside a size that jumped from 21 MB to 140 MB does explain it, and the
    /// note in <c>FallbackNote</c> says the rest.
    ///
    /// The bar moves on its own wherever there is no figure to show: from the press until the first
    /// report, which is the moment that used to look as if nothing had happened, and for the two
    /// stretches with no progress of their own — the delta merge at
    /// <see cref="DeltaDownloadCeiling"/>, and everything between the last byte and the handover.
    /// </remarks>
    private void SetDownloadStatus(int percent)
    {
        var bytesIn = percent >= 100 || (_fetching == Fetching.Delta && percent >= DeltaDownloadCeiling);
        DownloadProgress.IsIndeterminate = bytesIn || percent <= 0;

        if (bytesIn)
        {
            SetStatus(LocalizationService.Get(percent >= 100 ? "S.Update.Preparing" : "S.Update.Patching"));
            return;
        }

        var key = _fetching == Fetching.Delta ? "S.Update.DownloadingDelta" : "S.Update.DownloadingFull";
        StatusText.Text = LocalizationService.Format(key, FormatSize(CurrentDownloadSize()));
        PercentText.Text = percent > 0 ? $"{percent}%" : "";
    }

    /// <summary>
    /// The progress callback, and where the end of the download is recognised.
    /// </summary>
    /// <remarks>
    /// <see cref="DeltaDownloadCeiling"/> is only reached once every delta is in, and Velopack goes
    /// straight from there into the merge; 100 on the full package means it is on disk and
    /// Velopack is preparing the handover.
    /// </remarks>
    private void OnDownloadProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = percent;
            SetDownloadStatus(percent);

            var bytesIn = percent >= 100 || (_fetching == Fetching.Delta && percent >= DeltaDownloadCeiling);
            if (_phase == Phase.Downloading && bytesIn)
            {
                SetPhase(Phase.Finishing);
                StopSlowHintTimer();
                SlowHint.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>
    /// The deltas were given up on — the merge failed, or there was nothing left to merge them
    /// into — and the full package is starting from zero.
    /// </summary>
    /// <remarks>
    /// Before this was signalled directly, the switch only showed once the full download reported
    /// its first figure, and on a slow link that was a minute or more of 套用更新中 at 70% over a
    /// download that was already running.
    /// </remarks>
    private void OnFellBackToFull()
    {
        Dispatcher.Invoke(() =>
        {
            _fetching = Fetching.Full;
            FallbackNote.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            SetDownloadStatus(0);
            SetPhase(Phase.Downloading);
            StartSlowHintTimer();
        });
    }

    // The download is finished here regardless of what the last reported percentage was, so the bar
    // is carried the rest of the way before handing over. Without this the display would jump
    // straight from whatever Velopack last reported (often ~70) to the apply phase, making the
    // remaining percent look like it vanished.
    private async Task OnApplyingAsync()
    {
        // Past the point of no return: the hint's offer to fetch the installer by hand is no longer
        // worth anything, and the button that was 取消更新 a moment ago goes dead.
        SetPhase(Phase.Applying);
        StopSlowHintTimer();
        SlowHint.Visibility = Visibility.Collapsed;

        // Said before the bar finishes moving rather than after it: this is the step there is no
        // way back from, and the button that could have stopped it went dead a line ago.
        SetStatus(LocalizationService.Get("S.Update.Applying"));
        DownloadProgress.IsIndeterminate = false;
        await AnimateProgressToFullAsync();

        // The apply step exposes no progress at all, so an indeterminate bar is the honest signal:
        // still working, duration unknown. It keeps moving, which a bar frozen at 100% would not.
        DownloadProgress.IsIndeterminate = true;

        // Let those two land on screen: ApplyUpdatesAndRestart blocks this thread and then kills the
        // process, so anything not painted by now is never painted at all.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private Task AnimateProgressToFullAsync()
    {
        var completed = new TaskCompletionSource();

        var toFull = new DoubleAnimation(100, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        toFull.Completed += (_, _) =>
        {
            // Release the animation's hold on Value so IsIndeterminate can take the bar over.
            DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            DownloadProgress.Value = 100;
            completed.TrySetResult();
        };
        DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, toFull);

        return completed.Task;
    }

    /// <summary>
    /// Refuses to close while the update is running.
    /// </summary>
    /// <remarks>
    /// The title bar's own close button is disabled for the same stretch, so this is what catches
    /// Alt+F4, the taskbar's close and anything else that never goes near a button of ours.
    /// </remarks>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_phase != Phase.Offering)
            e.Cancel = true;
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Silences the startup dialog for this release and anything older, then closes.
    /// </summary>
    /// <remarks>
    /// Deliberately not a refusal of the update: the nav rail keeps offering it, so a user who
    /// skips a release in the morning and changes their mind that evening has somewhere to go. What
    /// this turns off is the interruption, which is the part they actually objected to.
    /// </remarks>
    private void SkipVersionLink_Click(object sender, RoutedEventArgs e)
    {
        UpdateNotifier.Skip(_updateInfo!);
        Close();
    }

    private void ReleaseNotesLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
