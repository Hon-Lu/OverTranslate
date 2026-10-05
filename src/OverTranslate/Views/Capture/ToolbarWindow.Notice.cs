using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Views.Capture;

/// <summary>What kind of outcome a toolbar notice reports, which drives its icon and colour.</summary>
public enum NoticeKind
{
    Info,
    Success,
    Error,
}

/// <summary>
/// The line under the bar that says what came of the last thing pressed on it.
/// </summary>
/// <remarks>
/// Part of the bar rather than a window of its own: every message it carries answers a button on
/// this bar, and one that appeared elsewhere — over the far side of the selection, say — sent the
/// reader's eye across the capture to find out what their own click had done.
/// </remarks>
public partial class ToolbarWindow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int NoticeDisplayMs = 3000;
    private const int NoticeFadeMs = 240;

    // How long a button keeps saying that its action landed before it goes back to its own name:
    // long enough to be seen by someone whose eyes were already on it, short of being mistaken for
    // the button's state.
    private const int ButtonFeedbackMs = 1500;
    private const int CopyShotFadeMs = 280;

    // The icon's second stroke for each kind, inside the ring both share. On a 16 grid.
    private const string ErrorMarkData = "M8,4.4 V8.8 M8,11.4 V11.6";
    private const string SuccessMarkData = "M5,8.2 L7.1,10.3 L11,6";
    private const string InfoMarkData = "M8,7.2 V11.6 M8,4.4 V4.6";

    private DispatcherTimer? _noticeTimer;
    private DispatcherTimer? _noticeCopyTimer;
    private DispatcherTimer? _copyShotTimer;
    private bool _noticeFading;
    private bool _copyShotFading;

    /// <summary>Shows a notice under the bar, replacing whichever one is showing.</summary>
    /// <remarks>
    /// Logged here rather than at the call sites: half of them reach a notice without logging
    /// anything of their own, so a report that says "an error came up" cannot be matched to a
    /// message. The title is enough to identify which one, and unlike the message it is always a
    /// fixed resource string — a message can carry an exception's text or a path.
    /// </remarks>
    public void ShowNotice(string title, string message, NoticeKind kind)
    {
        Log.Info("Toolbar notice shown, kind={Kind}, title=\"{Title}\"", kind, title);

        StopNoticeFade();
        ResetNoticeCopyButton();

        NoticeTitle.Text = title;
        NoticeMessage.Text = message;
        NoticeMark.Data = Geometry.Parse(kind switch
        {
            NoticeKind.Error => ErrorMarkData,
            NoticeKind.Success => SuccessMarkData,
            _ => InfoMarkData,
        });

        // Resource references rather than resolved brushes, so the icon follows a live theme switch.
        string brushKey = kind switch
        {
            NoticeKind.Error => "AppError",
            NoticeKind.Success => "AppSuccess",
            _ => "AppAccent",
        };
        NoticeRing.SetResourceReference(Shape.StrokeProperty, brushKey);
        NoticeMark.SetResourceReference(Shape.StrokeProperty, brushKey);

        // Always under the row, even where that runs it off the bottom of the screen. Moving it above
        // the row meant growing the window and moving it up by as much, and Windows 10 could show
        // the two a frame apart: the row jumped and came back.
        NoticeFooter.Visibility = Visibility.Visible;

        RestartNoticeTimer();
    }

    /// <summary>Takes the notice away now, if one is showing.</summary>
    public void HideNotice()
    {
        _noticeTimer?.Stop();
        if (NoticeFooter.Visibility != Visibility.Visible)
        {
            StopNoticeFade();
            return;
        }

        ResetNoticeCopyButton();

        // The fade's own end lands here too, with the window still held.
        StopNoticeFade();
        NoticeFooter.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Has the screenshot button say that its copy landed, for a moment, in place of its name.
    /// </summary>
    /// <remarks>
    /// On the button because that is where the reader is looking when it happens. The notice under
    /// the bar still comes with it: whether the file was saved, and where, is not something a tick
    /// can say.
    /// </remarks>
    public void ShowScreenshotCopied()
    {
        // Instant on the way in: it is the answer to the click, and an answer that eases in reads
        // as a delay.
        StopCopyShotFade();
        CopyShotCamera.Visibility = Visibility.Collapsed;
        CopyShotDoneMark.Visibility = Visibility.Visible;
        CopyShotLabel.Visibility = Visibility.Hidden;
        CopyShotDoneLabel.Visibility = Visibility.Visible;

        _copyShotTimer ??= NewOneShotTimer(ButtonFeedbackMs, RevertCopyShotButton);
        _copyShotTimer.Stop();
        _copyShotTimer.Start();
    }

    /// <summary>Gives the screenshot button its own name back.</summary>
    /// <remarks>
    /// Cross-faded rather than swapped: by now nobody is waiting on it, and a button that snaps
    /// back on its own, with nothing pressed, reads as a flicker. Both pairs are in the same cells
    /// and the label grid is already as wide as the longer word, so nothing moves while they blend.
    /// </remarks>
    private void RevertCopyShotButton()
    {
        void Settle()
        {
            StopCopyShotFade();
            CopyShotCamera.Visibility = Visibility.Visible;
            CopyShotDoneMark.Visibility = Visibility.Collapsed;
            CopyShotLabel.Visibility = Visibility.Visible;
            CopyShotDoneLabel.Visibility = Visibility.Hidden;
        }

        // Windows' "animation effects" setting: see FadeOutNotice.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Settle();
            return;
        }

        CopyShotCamera.Visibility = Visibility.Visible;
        CopyShotLabel.Visibility = Visibility.Visible;

        var duration = TimeSpan.FromMilliseconds(CopyShotFadeMs);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var fadeOut = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease };
        var fadeIn = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };

        // The flag calls it off when a new copy lands mid-fade — see StopCopyShotFade.
        _copyShotFading = true;
        fadeIn.Completed += (_, _) => { if (_copyShotFading) Settle(); };

        CopyShotDoneMark.BeginAnimation(OpacityProperty, fadeOut);
        CopyShotDoneLabel.BeginAnimation(OpacityProperty, fadeOut);
        CopyShotCamera.BeginAnimation(OpacityProperty, fadeIn);
        CopyShotLabel.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void StopCopyShotFade()
    {
        _copyShotFading = false;
        foreach (var element in new UIElement[] { CopyShotCamera, CopyShotDoneMark, CopyShotLabel, CopyShotDoneLabel })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private void StopNoticeTimers()
    {
        _noticeTimer?.Stop();
        _noticeCopyTimer?.Stop();
        _copyShotTimer?.Stop();
    }

    private void RestartNoticeTimer()
    {
        _noticeTimer ??= NewOneShotTimer(NoticeDisplayMs, FadeOutNotice);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // Hovering holds the notice open. Without this the copy button is decorative: the countdown
    // would run out while the pointer is still on its way there, and the message it exists to copy
    // is exactly the kind (a failure with details) worth keeping.
    private void NoticeFooter_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (NoticeFooter.Visibility != Visibility.Visible) return;
        _noticeTimer?.Stop();
        StopNoticeFade();
    }

    // Restarts the full countdown rather than resuming the remainder: the pointer leaving means the
    // reader just finished, and giving them the leftover 200ms of a spent timer reads as a glitch.
    private void NoticeFooter_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (NoticeFooter.Visibility != Visibility.Visible) return;
        RestartNoticeTimer();
    }

    /// <summary>Takes the notice away by folding it into the bar, fading as it goes.</summary>
    /// <remarks>
    /// Fading alone left the bar to drop back to its own height in one step at the end, which read
    /// as the window jumping. So the line closes up as it fades — down to the manga footer it was
    /// covering, if that is showing, so the hint is uncovered rather than redrawn.
    /// The window itself is held at its size meanwhile and the bar pinned to its top, so the bar
    /// shrinks inside a window that does not move. Only the transparent margin is let go at the end,
    /// and that cannot be seen.
    /// </remarks>
    private void FadeOutNotice()
    {
        _noticeTimer?.Stop();
        if (NoticeFooter.Visibility != Visibility.Visible || _noticeFading) return;

        // Windows' "animation effects" setting is the local equivalent of a reduced-motion
        // preference; a fade is motion the user has asked not to be shown.
        if (!SystemParameters.ClientAreaAnimation || PresentationSource.FromVisual(this) is null)
        {
            HideNotice();
            return;
        }

        SizeToContent = SizeToContent.Manual;
        ((FrameworkElement)Content).VerticalAlignment = VerticalAlignment.Top;

        // It can close onto the manga footer, which sits in the same cell at the same margin;
        // without that footer there is nothing there, so the margin closes too.
        bool ontoHint = MangaFooter.Visibility == Visibility.Visible;
        double toHeight = ontoHint ? MangaFooter.Height : 0;
        var toMargin = ontoHint ? NoticeFooter.Margin : new Thickness(0);

        var duration = TimeSpan.FromMilliseconds(NoticeFadeMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease };
        var fold = new DoubleAnimation(NoticeFooter.ActualHeight, toHeight, duration) { EasingFunction = ease };
        var close = new ThicknessAnimation(NoticeFooter.Margin, toMargin, duration) { EasingFunction = ease };

        // The flag is what actually calls it off: removing the animation does not reliably suppress
        // its Completed handler, so without it the notice would go from under a pointer resting on
        // it to read.
        _noticeFading = true;
        fade.Completed += (_, _) => { if (_noticeFading) HideNotice(); };
        NoticeFooter.BeginAnimation(HeightProperty, fold);
        NoticeFooter.BeginAnimation(MarginProperty, close);
        NoticeFooter.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Calls off a fade in flight and puts the notice and the window back as they were.</summary>
    private void StopNoticeFade()
    {
        _noticeFading = false;
        NoticeFooter.BeginAnimation(HeightProperty, null);
        NoticeFooter.BeginAnimation(MarginProperty, null);
        NoticeFooter.BeginAnimation(OpacityProperty, null);
        NoticeFooter.Opacity = 1;

        // Back to sizing itself. The notice is at full height again by now, which is the height the
        // window was held at, so this changes nothing on screen.
        if (SizeToContent != SizeToContent.WidthAndHeight)
        {
            ((FrameworkElement)Content).ClearValue(VerticalAlignmentProperty);
            SizeToContent = SizeToContent.WidthAndHeight;
        }
    }

    // Folds away like the timed close, rather than vanishing under the pointer that pressed it.
    private void NoticeCloseBtn_Click(object sender, RoutedEventArgs e) => FadeOutNotice();

    /// <remarks>
    /// The clipboard belongs to whatever else is running, and any of it can hold the clipboard open
    /// long enough for this to fail — WPF already retries for about a second before throwing. Left
    /// unhandled that throw reaches the dispatcher and takes the whole app down, over a copy button
    /// on a notice the reader was about to dismiss anyway.
    /// </remarks>
    private void NoticeCopyBtn_Click(object sender, RoutedEventArgs e)
    {
        bool copied = TryCopy($"{NoticeTitle.Text}\n{NoticeMessage.Text}");

        // Answered on the button itself: a second notice announcing the copy would replace this one
        // and lose the message just copied. On a failure the message is still there beside it,
        // which is where the reader gets it from instead.
        NoticeCopyBtn.Content = copied ? "✓" : "⧉";
        NoticeCopyBtn.SetResourceReference(ForegroundProperty, copied ? "AppSuccess" : "AppError");
        NoticeCopyBtn.SetResourceReference(ToolTipProperty, copied ? "S.Toast.Copied" : "S.Toast.CopyFailed");

        _noticeCopyTimer ??= NewOneShotTimer(ButtonFeedbackMs, ResetNoticeCopyButton);
        _noticeCopyTimer.Stop();
        _noticeCopyTimer.Start();
    }

    private void ResetNoticeCopyButton()
    {
        _noticeCopyTimer?.Stop();
        NoticeCopyBtn.Content = "⧉";
        NoticeCopyBtn.ClearValue(ForegroundProperty);
        NoticeCopyBtn.SetResourceReference(ToolTipProperty, "S.Toast.CopyMessage");
    }

    /// <remarks>
    /// A throw does not settle whether the text was copied. SetText publishes it and then flushes
    /// it so it outlives this process, and the flush is the half that fails most often — which
    /// leaves the text on the clipboard, pasteable until OverTranslate exits. Telling the reader it
    /// failed when their next paste would have worked is the worse of the two mistakes, so ask the
    /// clipboard what actually happened rather than inferring it from the exception.
    /// </remarks>
    private static bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);

            // Debug rather than Info: on its own a successful copy is not worth a line in everyone's
            // log, but without it a log cannot tell a copy that worked from a button that was never
            // pressed — which is exactly the question when someone reports the copy doing nothing.
            Log.Debug("Toolbar notice copied to the clipboard");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not copy the toolbar notice to the clipboard");
        }

        try
        {
            var landed = System.Windows.Clipboard.ContainsText()
                && System.Windows.Clipboard.GetText() == text;

            // Pairs with the warning above: it says the copy threw, this says whether the text got
            // there anyway. Without both, that warning reads as a failure the user never saw.
            Log.Debug(landed
                ? "Toolbar notice was on the clipboard despite the failed copy"
                : "Toolbar notice did not reach the clipboard");
            return landed;
        }
        catch (Exception ex)
        {
            // Whatever holds the clipboard shut holds it shut both ways. Unreadable is not the same
            // as absent, but from here the two are indistinguishable and the safe answer is the one
            // that leaves the message on screen.
            Log.Trace(ex, "Could not read the clipboard back after a failed copy");
            return false;
        }
    }

    private DispatcherTimer NewOneShotTimer(int ms, Action onElapsed)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            onElapsed();
        };
        return timer;
    }
}
