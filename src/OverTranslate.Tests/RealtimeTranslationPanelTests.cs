using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Views.Realtime;
using Xunit;
using Xunit.Abstractions;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// The running capsule's translation panel, built without being shown: the three columns hold what
/// they should, a pick marks the new choice without rebuilding the language lists, and the panel
/// lays out at a size that fits a screen.
/// </summary>
public class RealtimeTranslationPanelTests(ITestOutputHelper output)
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);

    // In the strings the code reads without an application, which are Traditional Chinese whatever
    // dictionary the window is given: the other languages' widths are for the running app to show.
    [Fact]
    public void ThePanel_LaysOutAtAWidthThatFitsTheScreen()
    {
        OnUiThread(() =>
        {
            const string language = "zh-Hant";
            var panel = Build(language, TranslationProvider.Google, "JA", "ZH-HANT");
            var root = (FrameworkElement)panel.Content;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));

            var chrome = (FrameworkElement)panel.FindName("Chrome");
            output.WriteLine($"{language}: {chrome.ActualWidth:0.#} x {chrome.ActualHeight:0.#} DIP");

            Assert.InRange(chrome.ActualWidth, 300, 760);
            Assert.InRange(chrome.ActualHeight, 380, 440);
            panel.Close();
        });
    }

    [Fact]
    public void TheColumns_HoldTheServicesAndBothLanguageLists()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Bing, "JA", "ZH-HANT");

            Assert.Equal(
                RealtimeTranslationPanelItems.Providers(Services.SettingsService.Instance.Current.HiddenProviders, TranslationProvider.Bing).Count,
                Rows(panel, "ProviderRows").Count);
            Assert.Equal(RealtimeTranslationPanelItems.SourceLanguages.Count, Rows(panel, "SourceRows").Count);
            Assert.Equal(LanguageData.TargetLanguages.Count, Rows(panel, "TargetRows").Count);
            panel.Close();
        });
    }

    // The panel stays open after a pick, so the new choice is marked in place: the language rows are
    // the same rows — the search and the scroll stay where they were — with the tick moved.
    [Fact]
    public void ANewChoice_MovesTheTick_WithoutRebuildingTheLanguageRows()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");
            var before = Rows(panel, "TargetRows");

            panel.ShowSelection(TranslationProvider.Bing, "EN", "EN-US");

            var after = Rows(panel, "TargetRows");
            Assert.Equal(before, after);
            Assert.Equal(["EN-US"], after.Where(Ticked).Select(r => ((RealtimeLanguageEntry)r.Tag).Code));
            Assert.Equal(["EN"], Rows(panel, "SourceRows").Where(Ticked).Select(r => ((RealtimeLanguageEntry)r.Tag).Code));
            Assert.Equal([TranslationProvider.Bing], Rows(panel, "ProviderRows").Where(Ticked).Select(r => ((RealtimeProviderEntry)r.Tag).Provider));
            panel.Close();
        });
    }

    [Fact]
    public void TheSearch_FiltersBothLanguageColumns_AndLeavesTheServicesAlone()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");
            var services = Rows(panel, "ProviderRows").Count(r => r.Visibility == Visibility.Visible);

            ((System.Windows.Controls.TextBox)panel.FindName("SearchBox")).Text = "russian";

            Assert.Equal(["RU"], Rows(panel, "SourceRows").Where(r => r.Visibility == Visibility.Visible).Select(r => ((RealtimeLanguageEntry)r.Tag).Code));
            Assert.Equal(["RU"], Rows(panel, "TargetRows").Where(r => r.Visibility == Visibility.Visible).Select(r => ((RealtimeLanguageEntry)r.Tag).Code));
            Assert.Equal(services, Rows(panel, "ProviderRows").Count(r => r.Visibility == Visibility.Visible));
            panel.Close();
        });
    }

    // A language pack is for reading the source: translating into Korean or Russian needs none, so
    // the target column neither marks those rows nor holds a pick back for a download.
    [Fact]
    public void OnlyTheSourceColumn_MarksLanguagesThatNeedAPack()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");

            Assert.All(Rows(panel, "TargetRows"), row => Assert.Null(PackHintOf(row)));
            var packRows = Rows(panel, "SourceRows").Where(r => ((RealtimeLanguageEntry)r.Tag).Language.Pack is not null).ToList();
            Assert.NotEmpty(packRows);
            Assert.All(packRows, row => Assert.NotNull(PackHintOf(row)));
            panel.Close();
        });
    }

    [Fact]
    public void PickingATargetLanguageWithAPack_TakesEffectAtOnce()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");
            var picked = new List<(RealtimeLanguageKind Kind, string Code)>();
            panel.LanguagePicked += (_, pick) => picked.Add(pick);

            foreach (var code in new[] { "KO", "RU", "UK" })
            {
                var row = Rows(panel, "TargetRows").Single(r => ((RealtimeLanguageEntry)r.Tag).Code == code);
                Assert.NotNull(((RealtimeLanguageEntry)row.Tag).Language.Pack);
                row.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }

            Assert.Equal([(RealtimeLanguageKind.Target, "KO"), (RealtimeLanguageKind.Target, "RU"), (RealtimeLanguageKind.Target, "UK")], picked);
            panel.Close();
        });
    }

    // The source column still asks first, whatever state the packs are in on this machine.
    [Fact]
    public void ASourceLanguage_AsksForItsPackOnlyWhileThePackIsMissing()
    {
        foreach (var language in RealtimeTranslationPanelItems.SourceLanguages.Where(l => l.Pack is not null))
        {
            Assert.Equal(!language.Pack!.IsReady, RealtimeTranslationPanel.NeedsPackFirst(RealtimeLanguageKind.Source, language));
            Assert.False(RealtimeTranslationPanel.NeedsPackFirst(RealtimeLanguageKind.Target, language));
        }
    }

    // The card asks over the whole panel, as 模式's cards do: the columns stay as they were under it
    // and the panel keeps its size.
    [Fact]
    public void ThePackCard_ComesUpOverThePanel_WithoutWideningTheSourceColumn()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");
            var chrome = Lay(panel);
            var source = (FrameworkElement)panel.FindName("SourceColumn");
            var (width, height, sourceWidth) = (chrome.ActualWidth, chrome.ActualHeight, source.ActualWidth);

            panel.PutUpCard(StandIn());
            Lay(panel);

            var layer = (FrameworkElement)panel.FindName("PackLayer");
            Assert.True(panel.IsPackCardShown);
            Assert.Equal(width, chrome.ActualWidth, 1);
            Assert.Equal(height, chrome.ActualHeight, 1);
            Assert.Equal(sourceWidth, source.ActualWidth, 1);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)panel.FindName("SourceScroll")).Visibility);
            // The scrim covers the whole inside of the chrome: search box and all three columns.
            Assert.Equal(chrome.ActualWidth - 2, layer.ActualWidth, 1);
            Assert.Equal(chrome.ActualHeight - 2, layer.ActualHeight, 1);
            panel.Close();
        });
    }

    // The card's own 取消 is the card's (OcrLanguagePackCard, needs the app's styles); the layer's
    // two ways out are the panel's.
    [Fact]
    public void ThePackCard_IsPutAwayByAPressOnTheScrim_AndByEsc()
    {
        OnUiThread(() =>
        {
            var panel = Build("zh-Hant", TranslationProvider.Google, "JA", "ZH-HANT");
            Lay(panel);
            var layer = (FrameworkElement)panel.FindName("PackLayer");
            var card = (FrameworkElement)panel.FindName("PackCard");

            // A press on the card itself is not one on the scrim.
            panel.PutUpCard(StandIn());
            card.RaiseEvent(LeftPress());
            Assert.True(panel.IsPackCardShown);
            layer.RaiseEvent(LeftPress());
            Assert.False(panel.IsPackCardShown);

            // Esc puts the card away and leaves the panel up; the rows under the scrim take no keys.
            panel.PutUpCard(StandIn());
            Assert.True(panel.HandleKey(System.Windows.Input.Key.Down));
            Assert.True(panel.HandleKey(System.Windows.Input.Key.Enter));
            Assert.True(panel.IsPackCardShown);
            Assert.True(panel.HandleKey(System.Windows.Input.Key.Escape));
            Assert.False(panel.IsPackCardShown);
            panel.Close();
        });
    }

    // The language pack card's size: 264 wide, about 130 tall.
    private static FrameworkElement StandIn() => new Border { Width = 264, Height = 130 };

    private static System.Windows.Input.MouseButtonEventArgs LeftPress() =>
        new(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };

    private static Border Lay(Window panel)
    {
        var root = (FrameworkElement)panel.Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        return (Border)panel.FindName("Chrome");
    }

    // The row's content is tick, then the pack hint when there is one, then the label.
    private static FrameworkElement? PackHintOf(System.Windows.Controls.Button row) =>
        ((DockPanel)row.Content).Children.OfType<FrameworkElement>()
            .Where(child => child is not System.Windows.Shapes.Path && child is not TextBlock)
            .FirstOrDefault();

    private static bool Ticked(System.Windows.Controls.Button row) =>
        row.Content is DockPanel content &&
        content.Children.OfType<System.Windows.Shapes.Path>().First().Visibility == Visibility.Visible;

    private static List<System.Windows.Controls.Button> Rows(Window panel, string name) =>
        [.. ((System.Windows.Controls.Panel)panel.FindName(name)).Children.OfType<System.Windows.Controls.Button>()];

    private static RealtimeTranslationPanel Build(string language, TranslationProvider provider, string source, string target)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var panel = new RealtimeTranslationPanel(provider, source, target, new Rectangle(800, 900, 500, 40), Screen);
        foreach (var dictionary in new[] { "Themes/SharedStyles.xaml", "Themes/DarkTheme.xaml", $"Resources/Strings.{language}.xaml" })
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/OverTranslate;component/{dictionary}", UriKind.Absolute)
            });
        return panel;
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
