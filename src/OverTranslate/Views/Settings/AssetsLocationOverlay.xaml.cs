using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.Assets;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 變更模型存放位置: choosing a folder, confirming, and the move with its progress or failure.
/// </summary>
/// <remarks>
/// Hosted by the shell, over everything, because it is opened both from the settings page and from
/// the AI translation panel, which is itself an overlay. Cards only fade in: text moved by an
/// animation is drawn blurred for the length of it.
/// </remarks>
public partial class AssetsLocationOverlay : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Duration FadeIn = new(TimeSpan.FromMilliseconds(160));

    private AssetsContext? _context;
    private string _otherPath = "";
    private AssetsCheck? _otherCheck;
    private AssetsCheck? _defaultCheck;
    private int _checkGeneration;
    private string? _target;
    private CancellationTokenSource? _move;
    private bool _open;

    /// <summary>Raised when the overlay goes away, so the page behind can take focus back.</summary>
    public event EventHandler? Closed;

    public AssetsLocationOverlay() => InitializeComponent();

    private bool Moving => _move is not null;

    internal void Open()
    {
        if (_open || AssetsRelocation.AnyDownloading) return;
        _open = true;
        _context = AssetsRelocation.Context();
        var current = _context.Current;
        var atDefault = AssetsPaths.Same(current, AssetsRelocation.Default);
        _otherPath = atDefault ? "" : current;
        _otherCheck = null;

        DefaultPathText.Text = AssetsRelocation.Default;
        DefaultCurrentTag.Visibility = atDefault ? Visibility.Visible : Visibility.Collapsed;
        OtherCurrentTag.Visibility = atDefault ? Visibility.Collapsed : Visibility.Visible;
        DefaultFreeText.Text = "";
        (atDefault ? DefaultOption : OtherOption).IsChecked = true;

        LocalizationService.LanguageChanged += OnLanguageChanged;
        Visibility = Visibility.Visible;
        Fade(this);
        Show(ChooseCard);
        RunChecks();
        UpdateLayout();
        (atDefault ? DefaultOption : OtherOption).Focus();
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        _checkGeneration++;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RenderChoose();

    // ══════════ Choose ══════════

    private void RunChecks()
    {
        if (_context is not { } context) return;
        var generation = ++_checkGeneration;
        var other = _otherPath;
        RenderChoose();
        _ = Task.Run(() =>
        {
            var machine = SystemAssetsMachine.Instance;
            var defaultCheck = AssetsLocationCheck.Check(AssetsRelocation.Default, context, machine);
            var otherCheck = other.Length > 0 ? AssetsLocationCheck.Check(other, context, machine) : null;
            return (defaultCheck, otherCheck);
        }).ContinueWith(task =>
        {
            if (generation != _checkGeneration || !_open) return;
            if (task.IsFaulted)
            {
                Log.Warn(task.Exception, "Checking the assets location failed");
                return;
            }

            (_defaultCheck, _otherCheck) = task.Result;
            RenderChoose();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RenderChoose()
    {
        if (_context is not { } context) return;
        OtherPathBox.Text = _otherPath;
        OtherPathPlaceholder.Visibility = _otherPath.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The default location is checked like any other, but only a lack of room can stop it.
        DefaultFreeText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        if (_defaultCheck is { Volume: { } dv } dc)
        {
            if (dc.Problem == AssetsProblem.NoSpace)
            {
                DefaultFreeText.Text = ProblemText(dc);
                DefaultFreeText.SetResourceReference(TextBlock.ForegroundProperty, "AppError");
            }
            else
            {
                DefaultFreeText.Text = LocalizationService.Format("S.Assets.Free", dv.Drive, Whole(dv.FreeBytes));
            }
        }

        var otherChosen = OtherOption.IsChecked == true;
        if (_otherCheck is { } check && _otherPath.Length > 0)
        {
            CheckText.Visibility = Visibility.Visible;
            if (check.Ok)
            {
                CheckText.Text = LocalizationService.Format("S.Assets.Free", check.Volume!.Drive, Whole(check.Volume.FreeBytes));
                CheckText.SetResourceReference(TextBlock.ForegroundProperty, "AppSuccess");
                CheckNote.Visibility = AssetsPaths.Same(check.Target!, context.Current) ? Visibility.Collapsed : Visibility.Visible;
                CheckNote.Text = check.HasModels
                    ? LocalizationService.Get("S.Assets.Reuse")
                    : LocalizationService.Format("S.Assets.WillCreate", check.Target!);
            }
            else
            {
                CheckText.Text = ProblemText(check);
                CheckText.SetResourceReference(TextBlock.ForegroundProperty, "AppError");
                CheckNote.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            CheckText.Visibility = Visibility.Collapsed;
            CheckNote.Visibility = Visibility.Collapsed;
        }

        _target = otherChosen
            ? _otherCheck is { Ok: true } ok ? ok.Target : null
            : _defaultCheck is { } d && d.Problem != AssetsProblem.NoSpace ? AssetsRelocation.Default : null;
        ContinueButton.IsEnabled = _target is not null && !AssetsPaths.Same(_target, context.Current);
    }

    internal static string ProblemText(AssetsCheck check) => check.Problem switch
    {
        AssetsProblem.Fat => LocalizationService.Format("S.Assets.Problem.Fat", check.Volume?.Drive ?? "", check.Volume?.Format ?? ""),
        AssetsProblem.NoSpace => LocalizationService.Format("S.Assets.Problem.NoSpace",
            check.Volume?.Drive ?? "", OneDecimal(check.NeededBytes), OneDecimal(check.Volume?.FreeBytes ?? 0)),
        AssetsProblem.None => "",
        _ => LocalizationService.Get("S.Assets.Problem." + check.Problem),
    };

    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (_open) RenderChoose();
    }

    private void OtherPathBox_Click(object sender, MouseButtonEventArgs e) => Browse();

    private void Browse_Click(object sender, RoutedEventArgs e) => Browse();

    private void Browse()
    {
        if (_context is not { } context) return;
        OtherOption.IsChecked = true;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Get("S.Assets.PickFolder"),
            InitialDirectory = InitialFolder(context),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _otherPath = dialog.FolderName;
        _otherCheck = null;
        RunChecks();
    }

    /// <summary>
    /// The portable build opens on its own folder, the natural place to keep it all together; the
    /// installed build on the folder holding the current location.
    /// </summary>
    private string InitialFolder(AssetsContext context)
    {
        string? folder = AssetsRelocation.ProgramRoot;
        if (folder is null)
            folder = _otherPath.Length > 0 ? _otherPath : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(context.Current));
        return AssetsPaths.NearestExisting(folder ?? "") ?? Environment.GetFolderPath(Environment.SpecialFolder.MyComputer);
    }

    private void ChooseCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null || _context is not { } context) return;
        ConfirmFromText.Text = context.Current;
        ConfirmToText.Text = _target;
        ConfirmBody.Text = context.UsedBytes > 0
            ? LocalizationService.Format("S.Assets.ConfirmMove", OneDecimal(context.UsedBytes))
            : LocalizationService.Get("S.Assets.ConfirmEmpty");
        Show(ConfirmCard);
        UpdateLayout();
        ConfirmCancelButton.Focus();
    }

    // ══════════ Confirm ══════════

    // Back to the choice, as it was left: 取消 here takes back the step, not the whole change.
    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => BackToChoose();

    private void BackToChoose()
    {
        Show(ChooseCard);
        UpdateLayout();
        ContinueButton.Focus();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_target is not { } target || _context is not { } context) return;

        if (context.UsedBytes == 0)
        {
            try
            {
                AssetsRelocation.SwitchWithoutMoving(target);
                Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Warn(ex, "Switching the assets location failed");
                ShowFailed(target);
            }

            return;
        }

        MovingToText.Text = LocalizationService.Format("S.Assets.MovingTo", target);
        RenderProgress(0, context.UsedBytes);
        MovingCancelButton.IsEnabled = true;
        Show(MovingCard);
        UpdateLayout();
        MovingCancelButton.Focus();

        _move = new CancellationTokenSource();
        try
        {
            // Nothing should be reading a model while it is copied, and the restart closes it anyway.
            AppServices.LocalAi.Release();
            long lastShown = -1;
            await AssetsRelocation.MoveAsync(target, (done, total) =>
            {
                // Reported per megabyte; the bar only needs a tenth of a gigabyte to move.
                var step = done / 50_000_000;
                if (step == Interlocked.Exchange(ref lastShown, step) && done != total) return;
                Dispatcher.BeginInvoke(() => RenderProgress(done, total));
            }, _move.Token);

            Log.Info("Assets moved; restarting");
            AssetsRelocation.StartReplacement();
            (System.Windows.Application.Current.MainWindow as MainWindow)?.ExitApp();
        }
        catch (OperationCanceledException)
        {
            Log.Info("Moving the assets was cancelled");
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Moving the assets to {Target} failed", target);
            ShowFailed(target);
        }
        finally
        {
            _move?.Dispose();
            _move = null;
        }
    }

    // ══════════ Moving ══════════

    private void RenderProgress(long done, long total)
    {
        MovingBar.Value = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        MovingProgressText.Text = LocalizationService.Format("S.Assets.MovingProgress", OneDecimal(done), OneDecimal(total));
    }

    private void MovingCancel_Click(object sender, RoutedEventArgs e)
    {
        MovingCancelButton.IsEnabled = false;
        _move?.Cancel();
    }

    // ══════════ Failed ══════════

    private void ShowFailed(string target)
    {
        FailedBody.Text = LocalizationService.Format("S.Assets.FailedBody", target);
        Show(FailedCard);
        UpdateLayout();
        FailedOkButton.Focus();
    }

    private void FailedOk_Click(object sender, RoutedEventArgs e) => Close();

    // ══════════ Frame ══════════

    private void Show(Border card)
    {
        foreach (var each in new[] { ChooseCard, ConfirmCard, MovingCard, FailedCard })
            each.Visibility = each == card ? Visibility.Visible : Visibility.Collapsed;
        Fade(card);
    }

    private static void Fade(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled, or the page behind would take the same Escape as well.
        if (e.Key == Key.Escape)
        {
            if (ConfirmCard.Visibility == Visibility.Visible) BackToChoose();
            else if (!Moving) Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    internal static string OneDecimal(long bytes) => (bytes / 1e9).ToString("F1", CultureInfo.InvariantCulture);

    internal static string Whole(long bytes) =>
        (bytes / 1e9).ToString(bytes >= 100_000_000_000 ? "F0" : "F1", CultureInfo.InvariantCulture);
}
