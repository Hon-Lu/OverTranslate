using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// One fact about 文字翻譯's markup that nothing else would notice going missing.
/// </summary>
/// <remarks>
/// Building the page itself would be the better test, and it is not available here: constructing it
/// needs an <c>Application</c> for its StaticResource styles, and WPF allows one per process — a
/// fixture for that would break the moment a second test wanted one. The behaviour was verified by
/// running the real page; what is pinned here is the single attribute that behaviour depends on and
/// that reads like decoration to anyone tidying the file.
/// </remarks>
public class TranslationPageMarkupTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The 原文 speaker is disabled whenever the source language is 自動 — see
    /// TranslationPage.RenderSourceActions for why — and its tooltip is then the only place the
    /// reason exists. WPF withholds tooltips from disabled controls unless asked, so without this
    /// attribute the user gets a greyed button and no explanation anywhere in the interface.
    /// </summary>
    [Fact]
    public void The_source_speaker_still_explains_itself_while_disabled()
    {
        var page = Path.Combine(
            StringsParityTests.ProjectDirectory(), "Views", "Translation", "TranslationPage.xaml");

        var speaker = XDocument.Load(page)
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "SrcTtsBtn");

        Assert.Equal("True", (string?)speaker.Attribute("ToolTipService.ShowOnDisabled"));
    }
}
