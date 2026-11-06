using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Controls;

/// <summary>
/// What a source-language picker shows when the language picked has not been downloaded: asks,
/// then follows the download. The same card in all three pickers; each puts it where its list was.
/// </summary>
/// <remarks>
/// Only the contents. A picker with a Popup and the capsule's menu, which is a window of its own, put
/// different surfaces under it.
/// </remarks>
internal partial class OcrLanguagePackCard : UserControl
{
    private readonly LangItem _language;
    private readonly OcrLanguagePack _pack;

    public OcrLanguagePackCard(LangItem language)
    {
        InitializeComponent();

        _language = language;
        _pack = language.Pack ?? throw new ArgumentException("this language needs no download", nameof(language));

        // Subscribed while shown: the pack outlives every card.
        Loaded += (_, _) =>
        {
            _pack.Changed += OnPackChanged;
            Render();
        };
        Unloaded += (_, _) => _pack.Changed -= OnPackChanged;
        Render();
    }

    /// <summary>The user pressed 下載 or 重試.</summary>
    public event EventHandler? DownloadRequested;

    /// <summary>The card has nothing more to say: cancelled, or the model is here.</summary>
    public event EventHandler? Finished;

    private void OnPackChanged(object? sender, EventArgs e)
    {
        if (_pack.IsReady)
        {
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        Render();
    }

    private void Render()
    {
        var name = _language.Name;
        var downloading = _pack.IsDownloading;
        var failure = _pack.FailureText;

        TitleText.Text = downloading ? LocalizationService.Format("S.OcrPack.Downloading", name)
            : failure is not null ? LocalizationService.Get("S.Settings.MangaModelsFailed")
            : LocalizationService.Format("S.OcrPack.AskTitle", name);

        BodyText.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        BodyText.Text = failure
            ?? (_pack.Codes.Count > 1
                ? LocalizationService.Format("S.OcrPack.AskBody", _pack.LanguagesText)
                : LocalizationService.Get("S.OcrPack.AskBodySingle"));
        BodyText.SetResourceReference(ForegroundProperty, failure is null ? "AppTextSecondary" : "AppError");

        ProgressTrack.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        ProgressDone.Width = new GridLength(_pack.Percent, GridUnitType.Star);
        ProgressLeft.Width = new GridLength(100 - _pack.Percent, GridUnitType.Star);

        var megabytes = _pack.Megabytes;
        DetailText.Text = downloading
            ? LocalizationService.Format("S.Settings.MangaModelsProgress",
                Math.Round(megabytes * _pack.Percent / 100.0, 1), megabytes, _pack.Percent)
            : failure is not null ? ""
            : LocalizationService.Format("S.OcrPack.Size", megabytes);

        PrimaryButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = LocalizationService.Get(
            failure is null ? "S.OcrPack.Download" : "S.Settings.MangaModelsRetry");
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadRequested?.Invoke(this, EventArgs.Empty);
        _pack.StartDownload();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pack.IsDownloading) _pack.CancelDownload();
        _pack.ClearFailure();
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
