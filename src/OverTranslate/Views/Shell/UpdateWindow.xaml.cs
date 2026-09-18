using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Services;

namespace OverTranslate.Views.Shell;

public partial class UpdateWindow : Window
{
    private static UpdateWindow? _instance;

    private readonly UpdateInfo _updateInfo;
    private Phase _phase = Phase.Offering;

    // Alive only for the length of one download attempt.
    private CancellationTokenSource? _cancel;

    // Runs once per download attempt; see StartSlowHintTimer.
    private DispatcherTimer? _slowHintTimer;

    /// <summary>
    /// Opens the update window, or brings the open one forward.
    /// </summary>
    /// <remarks>
    /// Two entry points reach this now — the startup check and the nav rail's 有新版本 — and the
    /// rail's is a button the user can press while the window it opens is already on screen behind
    /// the shell. A second instance would be a second download button for the same release.
    /// </remarks>
    public static void ShowOrActivate(UpdateInfo info)
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        var window = new UpdateWindow(info);
        _instance = window;
        window.Closed += (_, _) => _instance = null;
        window.Show();
    }

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _updateInfo = info;

        var compactIcon = AppIconService.CreateCompactIcon();
        Icon = compactIcon;
        TitleIcon.Source = compactIcon;

        // "v" on both, matching the rail's version line and its update chip — the number is the
        // same number, and dropping the prefix here would make it look like a different notation.
        var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        CurrentVersionText.Text = $"v{current}";
        LatestVersionText.Text  = $"v{info.LatestVersion}";

        // The system title bar is gone, so what it used to do for itself is done here: the rounded
        // corner and the outer edge, in the application's own border colour rather than the
        // system's. The edge is the compositor's, so it has to be handed over again on a theme
        // change — a DynamicResource never reaches it.
        WindowFrame.Attach(this);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowFrame.ApplyAppearance(this);

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
            _cancel?.Cancel();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;

        try
        {
            SetPhase(Phase.Downloading);
            ErrorText.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Visible;
            SetDownloadStatus(0);
            StartSlowHintTimer();

            await UpdateService.DownloadAndApplyAsync(
                _updateInfo, OnDownloadProgress, OnApplyingAsync, cancel.Token);
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
            // The button is the way to try again, so it says so — this is the one thing about it
            // that changes, now that the progress no longer lives on its label.
            DownloadBtnText.Text = LocalizationService.Get("S.Update.Retry");
            DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            SetStatus(null);
            ErrorText.Text = LocalizationService.Format("S.Update.Failed", ex.Message);
            ErrorText.Visibility = Visibility.Visible;
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
        DownloadProgress.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Collapsed;
        SetStatus(null);
    }

    private enum Phase
    {
        /// <summary>Nothing is running; the update is being offered.</summary>
        Offering,

        /// <summary>Fetching the package, and merging the delta into it. Abandonable.</summary>
        Downloading,

        /// <summary>Handing over to Velopack. Not abandonable, and nearly over.</summary>
        Applying,
    }

    /// <summary>
    /// Moves the window between offering the update, running it, and handing over.
    /// </summary>
    /// <remarks>
    /// Downloading and applying are not the same kind of wait, and treating them as one is what
    /// used to leave a user stranded in front of a window they could not dismiss. The download —
    /// the long half, and the half that stalls when the release CDN is slow — writes to a ".partial"
    /// file and is abandoned safely at any point, so the way out stays open for all of it. Applying
    /// replaces the application's own files and restarts the process; there is no way back from
    /// half of that, so everything goes dead, the title bar's close included. The close button says
    /// why rather than simply refusing — SetResourceReference rather than a fetched string, so the
    /// reason follows a language changed in 設定 while this window is still on screen.
    /// </remarks>
    private void SetPhase(Phase phase)
    {
        _phase = phase;

        DismissBtn.IsEnabled = phase == Phase.Offering;
        SkipVersionLink.IsEnabled = phase == Phase.Offering;
        CloseBtn.IsEnabled = phase == Phase.Offering;
        DownloadBtn.IsEnabled = phase != Phase.Applying;

        var cancelling = phase == Phase.Downloading;
        DownloadBtnText.Text = LocalizationService.Get(cancelling ? "S.Update.Cancel" : "S.Update.Now");
        DownloadBtnGlyph.Text = cancelling ? "" : "";

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
    /// </remarks>
    private static readonly TimeSpan SlowHintDelay = TimeSpan.FromSeconds(60);

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

    /// <summary>Puts a line under the progress bar, or takes it away.</summary>
    private void SetStatus(string? text)
    {
        StatusText.Inlines.Clear();
        if (text is not null) StatusText.Inlines.Add(new Run(text));
        StatusText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The download's own line: the same sentence, with the figure in the accent colour.
    /// </summary>
    /// <remarks>
    /// The number is the only part of this line that carries information — the words around it say
    /// the same thing for the whole download — so it is the part that is coloured, and it is
    /// coloured in the same accent as the progress bar directly above it, which is what it is a
    /// readout of. The unit travels with the figure: "45" and "%" are one number, and splitting
    /// them across two colours would read as two.
    ///
    /// Built out of runs rather than formatted into one string, so the placeholder can be found and
    /// what surrounds it left in whatever order the language puts it. The percent sign is inside
    /// the placeholder rather than in the resource for the same reason — a language that writes
    /// "%45" keeps it attached to the figure without the resource having to say so twice.
    ///
    /// SetResourceReference rather than a resolved brush: this line is on screen for the whole
    /// download, which is long enough for the theme to be switched underneath it.
    /// </remarks>
    private void SetDownloadStatus(int percent)
    {
        var template = LocalizationService.Get("S.Update.Downloading");
        var at = template.IndexOf("{0}", StringComparison.Ordinal);

        StatusText.Inlines.Clear();

        if (at < 0)
        {
            // A translation that dropped the placeholder still has to show the number.
            StatusText.Inlines.Add(new Run(LocalizationService.Format("S.Update.Downloading", percent)));
        }
        else
        {
            var figure = new Run($"{percent}%") { FontWeight = FontWeights.SemiBold };
            figure.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");

            if (at > 0) StatusText.Inlines.Add(new Run(template[..at]));
            StatusText.Inlines.Add(figure);
            if (at + 3 < template.Length) StatusText.Inlines.Add(new Run(template[(at + 3)..]));
        }

        StatusText.Visibility = Visibility.Visible;
    }

    private void OnDownloadProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = percent;
            SetDownloadStatus(percent);
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

        await AnimateProgressToFullAsync();

        // The apply step exposes no progress at all, so an indeterminate bar is the honest signal:
        // still working, duration unknown. It keeps moving, which a bar frozen at 100% would not.
        DownloadProgress.IsIndeterminate = true;
        SetStatus(LocalizationService.Get("S.Update.Applying"));

        // Let those two land on screen: ApplyUpdatesAndRestart blocks this thread and then kills the
        // process, so anything not painted by now is never painted at all.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private Task AnimateProgressToFullAsync()
    {
        var completed = new TaskCompletionSource();

        SetDownloadStatus(100);
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
        UpdateNotifier.Skip(_updateInfo);
        Close();
    }

    private void ReleaseNotesLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
