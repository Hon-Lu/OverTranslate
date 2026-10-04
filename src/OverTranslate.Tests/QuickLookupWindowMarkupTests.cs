using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The facts about 取詞翻譯's markup that read like decoration and are not.
/// </summary>
/// <inheritdoc cref="TranslationPageMarkupTests"/>
public class QuickLookupWindowMarkupTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "QuickLookup", "QuickLookupWindow.xaml"));

    /// <summary>
    /// 朗讀原文 is switched off until the engine has said what language the original was in, and its
    /// tooltip is then the only place that reason exists — the popup has no room for a note beside
    /// the button the way 文字翻譯 does. WPF withholds tooltips from disabled controls unless asked.
    /// </summary>
    [Fact]
    public void The_source_speaker_still_explains_itself_while_disabled()
    {
        var speaker = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "SrcTtsBtn");

        Assert.Equal("True", (string?)speaker.Attribute("ToolTipService.ShowOnDisabled"));
    }

    /// <summary>
    /// The popup is placed against the pointer and closes when the pointer leaves, so it has to be
    /// able to sit over a full-screen application and must never take a taskbar button — a window in
    /// the taskbar that vanishes a second later is a button the user cannot hit.
    /// </summary>
    [Fact]
    public void The_popup_floats_over_everything_without_joining_the_taskbar()
    {
        var window = Window().Root!;

        Assert.Equal("True", (string?)window.Attribute("Topmost"));
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));
    }

    /// <summary>
    /// Height follows the content because the popup has three sizes — a bare box, a translation, the
    /// settings panel — and the one thing it must not do is reserve room for the tallest of them
    /// over somebody else's screen.
    /// </summary>
    [Fact]
    public void The_popup_is_only_as_tall_as_what_it_is_showing()
    {
        Assert.Equal("Height", (string?)Window().Root!.Attribute("SizeToContent"));
    }

    /// <summary>
    /// The presentation toggle belongs to the always-visible header rather than either result
    /// panel, so a compact popup can always be expanded again.
    /// </summary>
    [Fact]
    public void The_result_presentation_toggle_is_always_available()
    {
        var toggle = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "CollapseBtn");

        Assert.Null((string?)toggle.Attribute("Visibility"));
        Assert.Equal("0", (string?)toggle.Parent?.Attribute("Grid.Column"));
    }

    /// <summary>The compact preview wraps long translations while keeping the speech action beside them.</summary>
    [Fact]
    public void The_compact_translation_wraps_before_the_tts_action()
    {
        var result = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "CompactTranslatedText");

        Assert.Equal("Wrap", (string?)result.Attribute("TextWrapping"));
        Assert.Equal("440", (string?)result.Attribute("MaxWidth"));
        Assert.Null((string?)result.Attribute("TextTrimming"));
        Assert.Equal("14", (string?)result.Attribute("FontSize"));
        Assert.Equal("SemiBold", (string?)result.Attribute("FontWeight"));
        Assert.Equal("20", (string?)result.Attribute("LineHeight"));
        Assert.Equal("Ideal", (string?)result.Attribute("TextOptions.TextFormattingMode"));
        Assert.Equal("0,5,0,0", (string?)result.Attribute("Margin"));
        Assert.Equal("Top", (string?)result.Attribute("VerticalAlignment"));
        Assert.Equal("{Binding Foreground, ElementName=TranslatedText}", (string?)result.Attribute("Foreground"));
        Assert.Equal("Horizontal", (string?)result.Parent?.Attribute("Orientation"));
    }

    /// <summary>The compact result exposes the same target-language speech action as the full result.</summary>
    [Fact]
    public void The_compact_translation_reuses_the_full_result_tts_action()
    {
        var button = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "CompactTgtTtsBtn");

        Assert.Equal("{StaticResource HeaderIconButton}", (string?)button.Attribute("Style"));
        Assert.Equal("{Binding Content, ElementName=TgtTtsBtn}", (string?)button.Attribute("Content"));
        Assert.Equal("{Binding Visibility, ElementName=CompactTranslatedText}", (string?)button.Attribute("Visibility"));
        Assert.Equal("8,0,0,0", (string?)button.Attribute("Margin"));
        Assert.Equal("Top", (string?)button.Attribute("VerticalAlignment"));
        Assert.Equal("TgtTtsBtn_Click", (string?)button.Attribute("Click"));
    }

    /// <summary>Auto-copy feedback overlays both result layouts without resizing or intercepting them.</summary>
    [Fact]
    public void Auto_copy_confirmation_is_a_non_interactive_overlay()
    {
        var confirmation = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "AutoCopyConfirmation");

        Assert.Equal("1", (string?)confirmation.Attribute("Panel.ZIndex"));
        Assert.Equal("Right", (string?)confirmation.Attribute("HorizontalAlignment"));
        Assert.Equal("Bottom", (string?)confirmation.Attribute("VerticalAlignment"));
        Assert.Equal("2", (string?)confirmation.Attribute("Grid.RowSpan"));
        Assert.Equal("False", (string?)confirmation.Attribute("IsHitTestVisible"));
        Assert.Equal("Collapsed", (string?)confirmation.Attribute("Visibility"));
        Assert.Equal("{DynamicResource AppHeaderBg}", (string?)confirmation.Attribute("Background"));
        Assert.Equal("{DynamicResource AppButtonBorder}", (string?)confirmation.Attribute("BorderBrush"));
        Assert.Equal("Grid", confirmation.Parent?.Name.LocalName);
        Assert.Equal("BodyHost", (string?)confirmation.ElementsBeforeSelf().Last().Attribute(X + "Name"));
    }

    /// <summary>Each speech button is centered on the first line box, independent of script glyph size.</summary>
    [Fact]
    public void The_result_tts_buttons_align_to_the_first_line_box()
    {
        var window = Window();
        XElement Element(string name) => window
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == name);

        Assert.Equal("32", (string?)Element("TranslatedText").Attribute("LineHeight"));
        Assert.Equal("Top", (string?)Element("TranslatedText").Attribute("VerticalAlignment"));
        Assert.Equal("8,1,0,0", (string?)Element("TgtTtsBtn").Attribute("Margin"));
        Assert.Equal("Top", (string?)Element("TgtTtsBtn").Attribute("VerticalAlignment"));
        Assert.Equal("20", (string?)Element("CompactTranslatedText").Attribute("LineHeight"));
        Assert.Equal("0,5,0,0", (string?)Element("CompactTranslatedText").Attribute("Margin"));
        Assert.Equal("8,0,0,0", (string?)Element("CompactTgtTtsBtn").Attribute("Margin"));
    }

    /// <summary>
    /// The transparent shadow canvas is an implementation detail. It must not leave the visible
    /// card below the screen edge when Windows places the popup's actual window against that edge.
    /// </summary>
    [Fact]
    public void The_visible_card_has_no_invisible_gap_above_it()
    {
        var shadowCanvas = Window().Root!
            .Elements()
            .Single(e => e.Name.LocalName == "Grid");

        var margin = ((string?)shadowCanvas.Attribute("Margin"))!
            .Split(',')
            .Select(double.Parse)
            .ToArray();

        Assert.Equal(0, margin[1]);
    }
}
