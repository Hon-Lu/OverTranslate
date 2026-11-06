using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 替換翻譯's languages: a fixed source and target, or 雙語互譯 between two. Drawn over the shell like
/// <see cref="ServiceSettingsOverlay"/>, opened from the 翻譯設定 button on the shortcut's row.
/// </summary>
/// <remarks>
/// Every change is saved as it is made, as on the page behind it; there is no save button, and
/// closing the card is all that is left to do. The settings are read again each time it opens.
/// </remarks>
public partial class QuickTranslateSettingsOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>True while the controls are being filled, so filling them writes nothing back.</summary>
    private bool _loading;

    public QuickTranslateSettingsOverlay()
    {
        InitializeComponent();
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open()
    {
        Load();
        Visibility = Visibility.Visible;
        LocalizationService.LanguageChanged += OnLanguageChanged;

        // Cached as a bitmap for the length of the scale, as the other cards over the shell are, so
        // the text does not soften and re-sharpen on the way in.
        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        Focus();
    }

    public void Close()
    {
        if (Visibility != Visibility.Visible) return;
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        Card.CacheMode = null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the card must not bubble up to the scrim's dismiss handler.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // The fallback line names a language, in the interface's language.
    private void OnLanguageChanged(object? sender, EventArgs e) => Load();

    // ── Settings ─────────────────────────────────────────────────────────────

    private void Load()
    {
        var s = SettingsService.Instance.Current;
        _loading = true;
        try
        {
            QuickTranslateSourceBox.ItemsSource = LanguageData.SourceLanguages;
            QuickTranslateTargetBox.ItemsSource = LanguageData.TargetLanguages;
            QuickTranslateSourceBox.SelectedValue = LanguageData.GetValidSourceCode(s.QuickTranslate.SourceLanguage);
            QuickTranslateTargetBox.SelectedValue = LanguageData.GetValidTargetCode(s.QuickTranslate.TargetLanguage);
            QuickTranslateSourceBox.Items.Refresh();
            QuickTranslateTargetBox.Items.Refresh();

            QuickTranslateBilingualFirstBox.ItemsSource = LanguageData.TargetLanguages;
            QuickTranslateBilingualSecondBox.ItemsSource = LanguageData.TargetLanguages;
            (s.QuickTranslate.BilingualEnabled ? BilingualRadio : OneWayRadio).IsChecked = true;
            ShowQuickTranslatePair(BilingualPair.Load(
                s.QuickTranslate.BilingualFirstLanguage, s.QuickTranslate.BilingualSecondLanguage));
            QuickTranslateBilingualFirstBox.Items.Refresh();
            QuickTranslateBilingualSecondBox.Items.Refresh();
            RenderQuickTranslateMode();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
    }

    private void QuickTranslateLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QuickTranslateSourceBox.SelectedValue is not string source ||
            QuickTranslateTargetBox.SelectedValue is not string target) return;

        Persist(s =>
        {
            s.QuickTranslate.SourceLanguage = LanguageData.GetValidSourceCode(source);
            s.QuickTranslate.TargetLanguage = LanguageData.GetValidTargetCode(target);
        });
        RenderQuickTranslateMode();
    }

    /// <summary>The 雙語互譯 pair as the pickers show it — see <see cref="BilingualPair.Pick"/>.</summary>
    private BilingualPair _quickTranslatePair = BilingualPair.Load(null, null);

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        RenderQuickTranslateMode();
        Persist(s => s.QuickTranslate.BilingualEnabled = BilingualRadio.IsChecked == true);
    }

    private void QuickTranslateBilingualPair_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ((System.Windows.Controls.ComboBox)sender).SelectedValue is not string picked) return;

        var pair = _quickTranslatePair.Pick(
            picked, firstSlot: ReferenceEquals(sender, QuickTranslateBilingualFirstBox));
        ShowQuickTranslatePair(pair);

        Persist(s =>
        {
            s.QuickTranslate.BilingualFirstLanguage = pair.First;
            s.QuickTranslate.BilingualSecondLanguage = pair.Second;
        });
    }

    /// <summary>Writes the pair into both pickers and the fallback line under them.</summary>
    /// <remarks>
    /// Both pickers, because a pick in one can move the other — that is how the pair is turned
    /// around. The line names whichever language is first, so it is rebuilt with the pair, and with
    /// the interface language through LoadSettings.
    /// </remarks>
    private void ShowQuickTranslatePair(BilingualPair pair)
    {
        _quickTranslatePair = pair;

        var wasLoading = _loading;
        _loading = true;
        try
        {
            QuickTranslateBilingualFirstBox.SelectedValue = pair.First;
            QuickTranslateBilingualSecondBox.SelectedValue = pair.Second;
        }
        finally
        {
            _loading = wasLoading;
        }

        QuickTranslateBilingualFallbackHint.Text = LocalizationService.Format(
            "S.QuickLookup.BilingualFallback", LanguageData.GetTargetDisplayName(pair.First));
        RenderQuickTranslateMode();
    }

    /// <summary>
    /// Opens the chosen mode's block and folds the other to its summary line, outlining the one in
    /// use. Also rewrites both summaries, which name the languages each block holds.
    /// </summary>
    private void RenderQuickTranslateMode()
    {
        if (OneWayBody is null) return;

        var bilingual = BilingualRadio.IsChecked == true;
        OneWayBody.Visibility = bilingual ? Visibility.Collapsed : Visibility.Visible;
        BilingualBody.Visibility = bilingual ? Visibility.Visible : Visibility.Collapsed;
        OneWaySummary.Visibility = bilingual ? Visibility.Visible : Visibility.Collapsed;
        BilingualSummary.Visibility = bilingual ? Visibility.Collapsed : Visibility.Visible;
        Outline(OneWayCard, !bilingual);
        Outline(BilingualCard, bilingual);

        OneWaySummary.Text = LocalizationService.Format("S.QuickTranslate.OneWaySummary",
            SourceName(QuickTranslateSourceBox.SelectedValue as string),
            LanguageData.GetTargetDisplayName(QuickTranslateTargetBox.SelectedValue as string));
        BilingualSummary.Text = LocalizationService.Format("S.QuickTranslate.BilingualSummary",
            LanguageData.GetTargetDisplayName(_quickTranslatePair.First),
            LanguageData.GetTargetDisplayName(_quickTranslatePair.Second));
    }

    private static void Outline(System.Windows.Controls.Border card, bool inUse)
    {
        card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, inUse ? "AppAccent" : "AppBorder");
        card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, inUse ? "AppAccentSubtle" : "AppCardBg");
    }

    // The text pickers' source list, which has 自動偵測 and every language a service takes - not the
    // OCR one GetSourceDisplayName reads.
    private static string SourceName(string? code) =>
        LanguageData.SourceLanguages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name
        ?? code ?? "";
}
