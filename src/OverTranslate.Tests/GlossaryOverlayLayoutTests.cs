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
    public void WithoutThePageAWideWindowStopsAtTheWindowMaximum(double window)
    {
        Assert.Equal(GlossaryOverlay.CardMaxWidth, GlossaryOverlay.CardWidthFor(window));
    }

    /// <summary>The page's card in a window <paramref name="window"/> wide: the window less the rail and the card's margins.</summary>
    private static double Page(double window, bool rail = true) => window - (rail ? 248 : 0) - 32;

    [Fact]
    public void ThePageWidthUsedHereIsTheShells()
    {
        var shell = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml"));
        var rail = shell.Descendants(Wpf + "ColumnDefinition").First(c => (string?)c.Attribute(X + "Name") == "SidebarColumn");
        var host = shell.Descendants(Wpf + "Border").First(b => (string?)b.Attribute(X + "Name") == "ContentHost");

        Assert.Equal("248", (string?)rail.Attribute("Width"));
        Assert.Equal("16,4,16,16", (string?)host.Attribute("Margin"));
    }

    [Fact]
    public void TheShellHandsTheCardItsPage()
    {
        var code = File.ReadAllText(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml.cs"));

        Assert.Contains("Glossary.WidthReference = ContentHost;", code);
    }

    [Theory]
    [InlineData(2560)]
    [InlineData(2048)]
    [InlineData(1920)]
    public void InAWideWindowTheCardIsThePageLessAMargin(double window)
    {
        var page = Page(window);

        Assert.Equal(page - 2 * GlossaryOverlay.PageInset, GlossaryOverlay.CardWidthFor(window, page), 3);
    }

    [Fact]
    public void WithTheRailPutAwayTheCardStillStaysInsideTheWindow()
    {
        foreach (var window in new[] { 1024.0, 1280, 2048, 3840 })
        {
            var card = GlossaryOverlay.CardWidthFor(window, Page(window, rail: false));
            Assert.True(card <= window - 32, $"{card} in a {window} window");
            Assert.True(card < Page(window, rail: false), $"{card} is not narrower than the page in a {window} window");
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1100)]
    [InlineData(1280)]
    public void ASmallWindowKeepsTheCardItHadBefore(double window)
    {
        Assert.Equal(GlossaryOverlay.CardWidthFor(window), GlossaryOverlay.CardWidthFor(window, Page(window)), 3);
    }

    /// <summary>The middle of the page's card, from the overlay's left edge (the overlay spans the rail too).</summary>
    private static double PageCenter(double window, bool rail = true) => (rail ? 248 : 0) + 16 + Page(window, rail) / 2;

    [Theory]
    [InlineData(2560)]
    [InlineData(2048)]
    [InlineData(1920)]
    public void InAWideWindowTheCardSitsOverThePage(double window)
    {
        var card = GlossaryOverlay.CardWidthFor(window, Page(window));
        var center = window / 2 + GlossaryOverlay.CardShiftFor(window, card, PageCenter(window));

        Assert.Equal(PageCenter(window), center, 3);
        // So it covers the page and leaves the rail alone, PageInset in from the page on both sides.
        Assert.Equal(248 + 16 + GlossaryOverlay.PageInset, center - card / 2, 3);
        Assert.Equal(window - 16 - GlossaryOverlay.PageInset, center + card / 2, 3);
    }

    [Fact]
    public void TheCardIsNeverPushedPastTheWindowsEdges()
    {
        foreach (var window in Enumerable.Range(1024, 3000).Select(w => (double)w))
        {
            foreach (var rail in new[] { true, false })
            {
                var card = GlossaryOverlay.CardWidthFor(window, Page(window, rail));
                var center = window / 2 + GlossaryOverlay.CardShiftFor(window, card, PageCenter(window, rail));
                Assert.True(center - card / 2 >= 16 - 1e-9 && center + card / 2 <= window - 16 + 1e-9, $"{card} at {center} in {window} (rail {rail})");
            }
        }
    }

    [Fact]
    public void ItSlidesRatherThanJumpsAsTheWindowIsResized()
    {
        var lefts = Enumerable.Range(1024, 2000).Select(w =>
        {
            var card = GlossaryOverlay.CardWidthFor(w, Page(w));
            return w / 2.0 + GlossaryOverlay.CardShiftFor(w, card, PageCenter(w)) - card / 2;
        }).ToList();

        Assert.True(lefts.Zip(lefts.Skip(1)).All(pair => Math.Abs(pair.Second - pair.First) <= 1.0));
    }

    [Fact]
    public void WithoutThePageItStaysInTheMiddle()
    {
        Assert.Equal(0, GlossaryOverlay.CardShiftFor(1280, 1152, 640));
    }

    [Fact]
    public void TheLayersAboveTheCardMoveWithIt()
    {
        var code = File.ReadAllText(Path.Combine(Dir, "GlossaryOverlay.xaml.cs"));

        Assert.Contains("Card.Margin = BatchCard.Margin = ConfirmCard.Margin = margin;", code);
    }

    [Fact]
    public void TheCardNeverGrowsNarrowerAsTheWindowWidens()
    {
        var widths = Enumerable.Range(300, 3000).Select(w => GlossaryOverlay.CardWidthFor(w, Page(w))).ToList();

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
