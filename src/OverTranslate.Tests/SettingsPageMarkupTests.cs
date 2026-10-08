using System.Xml.Linq;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The facts about 設定's markup that the code behind it depends on.
/// </summary>
/// <inheritdoc cref="TranslationPageMarkupTests"/>
public class SettingsPageMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// SettingsPage pairs the rows in 翻譯服務設定 with services by position, so a row moved in the
    /// markup — or a service added to the menus without a row — would switch the wrong service.
    /// Read off each row's accessible name, which is the service's own display string.
    /// </summary>
    [Fact]
    public void The_service_rows_are_in_the_menus_order()
    {
        var page = XDocument.Load(Path.Combine(
            StringsParityTests.ProjectDirectory(), "Views", "Settings", "SettingsPage.xaml"));

        var rows = page.Descendants(Presentation + "CheckBox")
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource ProviderCard}")
            .Select(e => (string?)e.Attribute("AutomationProperties.Name"))
            .ToList();

        Assert.Equal(
            LanguageData.Providers.Select(item => $"{{DynamicResource {item.DisplayKey}}}"),
            rows);
    }
}
