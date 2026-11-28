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
