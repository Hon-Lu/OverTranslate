using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 漫畫直排模型 in 設定: what the models are, whether they are here, getting them and removing them.
/// </summary>
/// <remarks>
/// Its own control rather than part of <see cref="SettingsPage"/>: it follows the download, not
/// the settings, and the page has nothing to say to it but where to report a failed delete.
/// </remarks>
public partial class MangaModelCard : UserControl
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public MangaModelCard()
    {
        InitializeComponent();

        // Subscribed while shown, like the page it sits on: static events holding an instance
        // handler have to let go of it when the page is navigated away from.
        Loaded += (_, _) =>
        {
            LocalizationService.LanguageChanged += OnLanguageChanged;
            AppServices.MangaModels.Changed += OnMangaModelsChanged;
            UpdateMangaModels();
        };
        Unloaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            AppServices.MangaModels.Changed -= OnMangaModelsChanged;
        };
    }

    /// <summary>A failure the card cannot show itself; the page puts it on its status line.</summary>
    public event EventHandler<string>? ErrorRaised;

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateMangaModels();

    // The last download's failure, shown until the next attempt or a delete. Null otherwise. Kept as
    // a kind rather than as text, so a change of interface language re-words it.
    private (Services.Ocr.Manga.MangaDownloadFailure Kind, int? Status)? _mangaModelsFailure;

    internal enum MangaCardState
    {
        /// <summary>No manifest in this build: nothing to offer.</summary>
        Hidden,
        NotDownloaded,
        Downloading,
        Ready,
        /// <summary>Downloaded, but they would not load or read here (a driver, a device lost).</summary>
        Unusable,
        Failed,
        /// <summary>No hardware adapter DirectML can use, known before downloading anything.</summary>
        Unsupported,
    }

    /// <summary>What the card shows, from the store, the machine and the last download.</summary>
    /// <param name="device"><see cref="Services.Ocr.Manga.MangaOcrEngine.DeviceSupport"/>.</param>
    /// <param name="engine"><see cref="Services.Ocr.Manga.MangaOcrEngine.Unavailable"/>.</param>
    internal static MangaCardState StateOf(
        Services.Ocr.Manga.MangaModelState store,
        Services.Ocr.Manga.MangaUnavailable device,
        Services.Ocr.Manga.MangaUnavailable engine,
        bool downloadFailed) => store switch
    {
        Services.Ocr.Manga.MangaModelState.Unavailable => MangaCardState.Hidden,
        Services.Ocr.Manga.MangaModelState.Downloading => MangaCardState.Downloading,
        _ when device != Services.Ocr.Manga.MangaUnavailable.None => MangaCardState.Unsupported,
        Services.Ocr.Manga.MangaModelState.Ready =>
            engine == Services.Ocr.Manga.MangaUnavailable.None ? MangaCardState.Ready : MangaCardState.Unusable,
        _ => downloadFailed ? MangaCardState.Failed : MangaCardState.NotDownloaded,
    };

    // What the card showed last, so that only a change of state fades; progress does not.
    private MangaCardState? _mangaCardShown;
    private FrameworkElement? _mangaCardFoot;

    private static readonly TimeSpan MangaCardFade = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MangaCardProgressGlide = TimeSpan.FromMilliseconds(200);

    // Progress arrives from the download's thread, many times a second.
    private void OnMangaModelsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateMangaModels, DispatcherPriority.Background);

    /// <summary>
    /// The manga model card: a chip saying where things stand, and at its foot the one thing that
    /// applies — the download button, the progress, or a line saying what is in use or why not.
    /// </summary>
    private void UpdateMangaModels()
    {
        var store = AppServices.MangaModels;
        var manga = AppServices.Ocr.Manga;
        var total = store.Manifest?.TotalBytes ?? 0;
        const double megabyte = 1 << 20;

        var storeState = store.State;
        var state = StateOf(
            storeState,
            manga?.DeviceSupport ?? Services.Ocr.Manga.MangaUnavailable.None,
            manga?.Unavailable ?? Services.Ocr.Manga.MangaUnavailable.None,
            _mangaModelsFailure is not null);

        MangaCard.Visibility = state == MangaCardState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        if (state == MangaCardState.Hidden)
        {
            _mangaCardShown = state;
            return;
        }

        var (chip, dot) = state switch
        {
            MangaCardState.Downloading => ("S.Settings.MangaModelsDownloading", "#52C4FA"),
            MangaCardState.Ready => ("S.Settings.MangaModelsReady", "#34C759"),
            MangaCardState.Unusable => ("S.Settings.MangaModelsNoGpu", "#FFB800"),
            MangaCardState.Failed => ("S.Settings.MangaModelsFailed", "#FF453A"),
            MangaCardState.Unsupported => ("S.Settings.MangaModelsUnsupported", "#8E8E93"),
            _ => ("S.Settings.MangaModelsNotDownloaded", "#8E8E93"),
        };
        MangaChipText.Text = LocalizationService.Get(chip);
        PlaceMangaChip();
        MangaChipDot.Fill = (Brush)new BrushConverter().ConvertFromString(dot)!;

        var unsupported = state == MangaCardState.Unsupported;
        MangaCardDesc2.SetResourceReference(System.Windows.Documents.Run.TextProperty,
            unsupported ? "S.Settings.MangaModelsDesc2Unsupported" : "S.Settings.MangaModelsDesc2");
        MangaModelsDownloadBtn.IsEnabled = !unsupported;
        MangaModelsDownloadIcon.Visibility = unsupported ? Visibility.Collapsed : Visibility.Visible;
        MangaModelsDownloadText.Margin = new Thickness(unsupported ? 0 : 8, 0, 0, 0);
        MangaModelsDownloadText.Text = unsupported
            ? LocalizationService.Get("S.Settings.MangaModelsUnsupported")
            : LocalizationService.Format("S.Settings.MangaModelsDownload", Math.Round(total / megabyte));
        MangaModelsUnsupportedDeleteBtn.Visibility =
            unsupported && storeState == Services.Ocr.Manga.MangaModelState.Ready ? Visibility.Visible : Visibility.Collapsed;

        var fraction = total > 0 ? Math.Clamp((double)store.DownloadedBytes / total, 0, 1) : 0;
        MangaModelsProgressText.Text = LocalizationService.Format(
            "S.Settings.MangaModelsProgress",
            Math.Round(store.DownloadedBytes / megabyte), Math.Round(total / megabyte), (int)(100 * fraction));

        var adapter = manga?.AdapterName ?? "GPU";
        MangaModelsStatus.Text = state switch
        {
            MangaCardState.Ready => LocalizationService.Get("S.Settings.MangaModelsEnabled"),
            MangaCardState.Unusable => manga!.Unavailable == Services.Ocr.Manga.MangaUnavailable.ReadFailed
                ? LocalizationService.Format("S.Settings.MangaModelsReasonReadFailed", adapter)
                : LocalizationService.Format("S.Settings.MangaModelsReasonLoadFailed", adapter),
            MangaCardState.Failed => _mangaModelsFailure!.Value switch
            {
                (Services.Ocr.Manga.MangaDownloadFailure.Server, var status) =>
                    LocalizationService.Format("S.Settings.MangaModelsErrorServer", status ?? 0),
                (Services.Ocr.Manga.MangaDownloadFailure.Network, _) => LocalizationService.Get("S.Settings.MangaModelsErrorNetwork"),
                (Services.Ocr.Manga.MangaDownloadFailure.Checksum, _) => LocalizationService.Get("S.Settings.MangaModelsErrorChecksum"),
                (Services.Ocr.Manga.MangaDownloadFailure.Disk, _) => LocalizationService.Get("S.Settings.MangaModelsErrorDisk"),
                _ => LocalizationService.Get("S.Settings.MangaModelsErrorOther"),
            },
            _ => "",
        };

        var tone = state switch
        {
            MangaCardState.Unusable => "Warn",
            MangaCardState.Failed => "Error",
            _ => "Ready",
        };
        MangaModelsStatusBox.SetResourceReference(Border.BackgroundProperty, $"MangaCard{tone}Bg");
        MangaModelsStatusBox.SetResourceReference(Border.BorderBrushProperty, $"MangaCard{tone}Border");
        MangaModelsDeleteBtn.Visibility = state is MangaCardState.Ready or MangaCardState.Unusable
            ? Visibility.Visible : Visibility.Collapsed;
        MangaModelsRetryBtn.Visibility = state == MangaCardState.Failed ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement foot = state switch
        {
            MangaCardState.NotDownloaded or MangaCardState.Unsupported => MangaModelsDownloadPanel,
            MangaCardState.Downloading => MangaModelsProgressPanel,
            _ => MangaModelsStatusBox,
        };

        // Fading needs something to fade from: the first time the card is drawn it is just drawn.
        var animate = SystemParameters.ClientAreaAnimation &&
                      _mangaCardShown is { } shown && shown != MangaCardState.Hidden && IsLoaded;
        var changed = _mangaCardShown != state;
        if (changed)
        {
            ShowMangaCardFoot(foot, animate);
            FadeIn(MangaChip, animate);
        }

        SetMangaProgress(fraction, animate && !changed);
        _mangaCardShown = state;
    }

    // Cross-fades the card's foot; the old part goes once it has faded, so the card never jumps
    // to a height that belongs to neither.
    private void ShowMangaCardFoot(FrameworkElement foot, bool animate)
    {
        _mangaCardFoot = foot;
        foreach (var part in new FrameworkElement[] { MangaModelsDownloadPanel, MangaModelsProgressPanel, MangaModelsStatusBox })
        {
            if (part == foot)
            {
                part.Visibility = Visibility.Visible;
                FadeIn(part, animate);
            }
            else if (part.Visibility == Visibility.Visible)
            {
                if (!animate)
                {
                    part.BeginAnimation(OpacityProperty, null);
                    part.Visibility = Visibility.Collapsed;
                    continue;
                }

                var fade = new DoubleAnimation(0, MangaCardFade);
                fade.Completed += (_, _) =>
                {
                    if (_mangaCardFoot == part) return;
                    part.Visibility = Visibility.Collapsed;
                    part.BeginAnimation(OpacityProperty, null);
                };
                part.BeginAnimation(OpacityProperty, fade);
            }
        }
    }

    private static void FadeIn(UIElement element, bool animate)
    {
        if (animate)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, MangaCardFade));
        else
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private void SetMangaProgress(double fraction, bool animate)
    {
        var width = fraction * MangaModelsProgressTrack.ActualWidth;
        if (animate)
        {
            MangaModelsProgressFill.BeginAnimation(WidthProperty,
                new DoubleAnimation(width, MangaCardProgressGlide) { EasingFunction = new QuadraticEase() });
        }
        else
        {
            MangaModelsProgressFill.BeginAnimation(WidthProperty, null);
            MangaModelsProgressFill.Width = width;
        }
    }

    // The track only has a width once it is laid out, which is after the first update.
    private void MangaModelsProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var total = AppServices.MangaModels.Manifest?.TotalBytes ?? 0;
        SetMangaProgress(total > 0 ? Math.Clamp((double)AppServices.MangaModels.DownloadedBytes / total, 0, 1) : 0, false);
    }

    // Round the card's contents to the inside of its border, so the corner glow does not square it off.
    private void MangaCardBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MangaCardBody.Clip = new RectangleGeometry(new Rect(e.NewSize), 11, 11);
        if (!e.WidthChanged) return;
        PlaceMangaChip();

        // At the window's narrowest the card is barely 200px across: the feature names get the
        // side margins back so four characters still fit a line, and the action in the status box
        // goes under its sentence instead of squeezing it into a column of one word per line.
        var compact = e.NewSize.Width < 260;
        foreach (var feature in new[] { MangaFeature1, MangaFeature2, MangaFeature3 })
            feature.Margin = new Thickness(compact ? 0 : 4, 5, compact ? 0 : 4, 0);
        foreach (var action in new FrameworkElement[] { MangaModelsDeleteBtn, MangaModelsRetryBtn })
        {
            Grid.SetColumn(action, compact ? 0 : 1);
            Grid.SetRow(action, compact ? 1 : 0);
            action.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Stretch;
            action.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
        }
    }

    // Beside the title when the two fit on one line, else under it. Titles run long in some
    // languages ("Manga vertical-text models") and the card is only a column wide.
    private void PlaceMangaChip()
    {
        if (MangaCardHead.ActualWidth <= 0) return;
        var unbounded = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
        MangaCardTitle.Measure(unbounded);
        MangaChip.Measure(unbounded);
        var fits = MangaCardTitle.DesiredSize.Width + MangaChip.DesiredSize.Width <= MangaCardHead.ActualWidth - 44;

        Grid.SetColumn(MangaChip, fits ? 2 : 1);
        Grid.SetRow(MangaChip, fits ? 0 : 1);
        Grid.SetRowSpan(MangaChip, fits ? 2 : 1);
        // Beside it, both centre on the icon; under it, the title takes the first row.
        Grid.SetRowSpan(MangaCardTitle, fits ? 2 : 1);
        MangaChip.HorizontalAlignment = fits ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Left;
        MangaChip.Margin = fits ? new Thickness(0) : new Thickness(12, 4, 0, 0);
        // With nothing beside it, the title may use the room the chip left.
        MangaCardTitle.Margin = new Thickness(12, 0, fits ? 12 : 0, 0);
    }

    private async void MangaModelsDownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        _mangaModelsFailure = null;
        try
        {
            // Refused, without a request, on a machine that could not run them; the button is
            // disabled there, so this is only the backstop.
            if (AppServices.Ocr.Manga is { } manga)
                await manga.DownloadModelsAsync();
            else
                await AppServices.MangaModels.DownloadAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the link under the bar; the store has removed what arrived.
        }
        catch (NotSupportedException)
        {
            // Not supported here; the card already says so.
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Manga model download failed");
            _mangaModelsFailure = (Services.Ocr.Manga.MangaModelStore.Classify(ex),
                ex is System.Net.Http.HttpRequestException { StatusCode: { } status } ? (int)status : null);
        }

        UpdateMangaModels();
    }

    private void MangaModelsCancelBtn_Click(object sender, RoutedEventArgs e) =>
        AppServices.MangaModels.CancelDownload();

    // No confirmation: a deleted model is one download away, not lost.
    private void MangaModelsDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        _mangaModelsFailure = null;
        try
        {
            // Sessions first, so no model file is still open when the folder goes.
            AppServices.Ocr.Manga?.ReleaseNow();
            AppServices.MangaModels.Delete();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorRaised?.Invoke(this, LocalizationService.Format("S.Settings.MangaModelsDeleteFailed", ex.Message));
        }

        UpdateMangaModels();
    }
}
