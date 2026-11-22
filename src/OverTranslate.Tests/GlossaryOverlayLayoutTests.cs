using System.IO;
using System.Xml.Linq;
using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The glossary card's size against the window, and what keeps its batch box responsive and its cut
/// texts readable. Read from the card's own XAML and code, as the shell's other layout tests do.
/// </summary>
public class GlossaryOverlayLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Dir => Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Settings");
    private static XDocument Overlay() => XDocument.Load(Path.Combine(Dir, "GlossaryOverlay.xaml"));
    private static XElement Named(XDocument doc, string name) => doc.Descendants().First(e => (string?)e.Attribute(X + "Name") == name);

    private static double ShellMinWidth()
    {
        var shell = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml"));
        return double.Parse((string)shell.Root!.Attribute("MinWidth")!);
    }

    [Fact]
    public void AtTheNarrowestWindowTheCardIsStillAsWideAsItsDesignAndInsideTheWindow()
    {
        var window = ShellMinWidth();
        var card = GlossaryOverlay.CardWidthFor(window);

        // 900 is the width the card was laid out and checked at; narrower would squeeze the toolbar.
        Assert.True(card >= 900, $"{card} at a {window} window");
        Assert.True(card <= window - 32, $"{card} leaves no margin in a {window} window");
    }

    [Theory]
    [InlineData(1280, 1152)]
    [InlineData(1100, 990)]
    public void TheCardFollowsTheWindowsWidth(double window, double card)
    {
        Assert.Equal(card, GlossaryOverlay.CardWidthFor(window), 3);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(3840)]
    public void AWideWindowStopsAtTheMaximumSoTheTableIsNotLeftEmpty(double window)
    {
        Assert.Equal(GlossaryOverlay.CardMaxWidth, GlossaryOverlay.CardWidthFor(window));
    }

    [Fact]
    public void TheCardNeverGrowsNarrowerAsTheWindowWidens()
    {
        var widths = Enumerable.Range(300, 3000).Select(w => GlossaryOverlay.CardWidthFor(w)).ToList();

        Assert.True(widths.Zip(widths.Skip(1)).All(pair => pair.Second >= pair.First));
        Assert.True(widths.All(w => w >= GlossaryOverlay.CardMinWidth));
    }

    [Fact]
    public void TheHeightRuleIsUnchanged()
    {
        Assert.Equal(684, GlossaryOverlay.CardHeightFor(760), 3);
        Assert.Equal(GlossaryOverlay.CardMaxHeight, GlossaryOverlay.CardHeightFor(1400));
    }

    [Fact]
    public void TheSidebarKeepsItsWidthAndTheEditorTakesTheRest()
    {
        var columns = Named(Overlay(), "SplitView").Element(Wpf + "Grid.ColumnDefinitions")!.Elements().ToList();

        Assert.Equal("236", (string?)columns[0].Attribute("Width"));
        Assert.Equal("*", (string?)columns[1].Attribute("Width"));
    }

    [Fact]
    public void TheEntryColumnsShareWhatTheEditorHas()
    {
        // The table's heading, the add row and every entry use the same four columns.
        var grids = Overlay().Descendants(Wpf + "Grid.ColumnDefinitions")
            .Select(defs => defs.Elements().Select(c => (string?)c.Attribute("Width")).ToList())
            .Where(widths => widths.SequenceEqual(new[] { "*", "26", "*", "76" }))
            .ToList();

        Assert.Equal(3, grids.Count);
    }

    [Fact]
    public void TheBatchPreviewIsVirtualized()
    {
        var lines = Named(Overlay(), "PasteLines");

        Assert.Equal("True", (string?)lines.Attribute(Wpf + "VirtualizingPanel.IsVirtualizing") ?? (string?)lines.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.NotNull(lines.Descendants(Wpf + "VirtualizingStackPanel").FirstOrDefault());
        Assert.Contains(lines.Descendants(Wpf + "ScrollViewer"), viewer => (string?)viewer.Attribute("CanContentScroll") == "True");
    }

    [Fact]
    public void TheBatchPreviewIsRedrawnAfterTheKeysNotUnderEachOne()
    {
        // Redrawn inside TextChanged, a long paste held every key up for as long as the preview took.
        var code = File.ReadAllText(Path.Combine(Dir, "GlossaryOverlay.xaml.cs"));
        var start = code.IndexOf("private void PasteBox_TextChanged", StringComparison.Ordinal);
        var body = code.Substring(start, code.IndexOf("\n    }", start, StringComparison.Ordinal) - start);

        Assert.Contains("DispatcherPriority.Background", body);
        Assert.DoesNotContain("=> RenderPaste()", body);
    }

    [Theory]
    [InlineData("CellBox")]
    [InlineData("TitleBox")]
    public void TheBoxesThatCanBeCutShowTheirTextWhenTheyAre(string style)
    {
        var setters = Overlay().Descendants(Wpf + "Style").First(s => (string?)s.Attribute(X + "Key") == style).Elements(Wpf + "Setter");

        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "ctl:TrimmedToolTip.IsEnabled" && (string?)s.Attribute("Value") == "True");
    }

    [Fact]
    public void TheListNameShowsItsTextWhenItIsCut()
    {
        var name = Named(Overlay(), "ListsBox").Descendants(Wpf + "TextBlock").First(t => (string?)t.Attribute("Text") == "{Binding Name}");

        Assert.Equal("True", (string?)name.Attribute(XNamespace.Get("clr-namespace:OverTranslate.Controls") + "TrimmedToolTip.IsEnabled"));
    }
}
