using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.Ocr.Manga;

namespace OverTranslate.Controls;

/// <summary>
/// The line under a source picker saying what vertical text will be read with: the manga models,
/// how to get them, or how to bring them in.
/// </summary>
/// <remarks>
/// <para>One small line, not a card: it sits in a toolbar the user is about to press 翻譯 on, and
/// all it has to do is say whether the page will go to the models. Its room is always kept and it is
/// only ever hidden, never collapsed, so choosing 直排 or another language never moves a button the
/// user is reaching for.</para>
///
/// <para>Where it stands is the host's to say (<see cref="Show"/>); where the models stand it
/// watches itself, through <see cref="MangaModelOptions.Changed"/>, so a download finishing or the
/// switch on the settings page being turned while the bar is open is on it at once.</para>
///
/// <para>Text only, never a link, even for 尚未安裝. The capture overlay is full screen and
/// topmost, and a settings window opened from it would come up underneath it.</para>
/// </remarks>
public sealed class MangaModelHintText : TextBlock
{
    private bool _vertical;
    private string? _sourceLanguage;

    public MangaModelHintText()
    {
        FontSize = 11;
        // A line of 13 rather than the font's own 14.6: under the capture toolbar's 32px row it is
        // what keeps the two inside the 46 the stacked actions already make the bar.
        LineHeight = 13;
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TextWrapping = TextWrapping.NoWrap;
        Visibility = Visibility.Hidden;
        IsHitTestVisible = false;
        SetResourceReference(FontFamilyProperty, "AppFont");

        // Static events holding an instance handler: let go of them when the host goes.
        Loaded += (_, _) =>
        {
            MangaModelOptions.Changed += OnChanged;
            LocalizationService.LanguageChanged += OnChanged;
            Render();
        };
        Unloaded += (_, _) =>
        {
            MangaModelOptions.Changed -= OnChanged;
            LocalizationService.LanguageChanged -= OnChanged;
        };
    }

    /// <summary>What the host's pickers say now.</summary>
    public void Show(bool vertical, string? sourceLanguage)
    {
        _vertical = vertical;
        _sourceLanguage = sourceLanguage;
        Render();
    }

    /// <summary>The hint now showing; for tests and the probe.</summary>
    internal MangaModelHint Hint { get; private set; }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Render, DispatcherPriority.Background);

    private void Render()
    {
        Hint = MangaModelOptions.HintFor(_vertical, _sourceLanguage);
        if (Hint == MangaModelHint.None)
        {
            Visibility = Visibility.Hidden;
            return;
        }

        var (key, brush) = Hint switch
        {
            MangaModelHint.Applied => ("S.Toolbar.MangaHintApplied", "MangaHintApplied"),
            MangaModelHint.NotInstalled => ("S.Toolbar.MangaHintNotInstalled", "AppWarning"),
            MangaModelHint.Downloading => ("S.Toolbar.MangaHintDownloading", "AppTextSecondary"),
            _ => ("S.Toolbar.MangaHintChooseJapanese", "AppTextSecondary"),
        };
        Text = "✦ " + LocalizationService.Get(key);
        SetResourceReference(ForegroundProperty, brush);
        Visibility = Visibility.Visible;
    }
}
