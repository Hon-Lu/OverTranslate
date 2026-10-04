using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class OcrDebugBoxesTests
{
    /// <summary>
    /// Every line the recogniser returned gets a box, groups included — the debug view is about
    /// what was read, so a group that swallowed three lines has to show all three.
    /// </summary>
    [Fact]
    public void DrawsABoxForEveryRecognisedLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("a wrapped sentence", new Rect(10, 10, 200, 60),
                [new Rect(10, 10, 200, 28), new Rect(10, 42, 180, 28)]),
            new("Exit", new Rect(10, 120, 70, 28)),
        };

        var lines = OcrDebugBoxes.LineBoxes(blocks);

        Assert.Equal(3, lines.Count);
        Assert.Contains(new Rect(10, 10, 200, 28), lines);
        Assert.Contains(new Rect(10, 42, 180, 28), lines);
        Assert.Contains(new Rect(10, 120, 70, 28), lines);
    }

    /// <summary>
    /// A group box encloses its lines rather than coinciding with them, which is what makes the two
    /// layers readable together — and the case that would otherwise be unreadable is the common
    /// one: a group of a single line, where the two rectangles are the same rectangle.
    /// </summary>
    [Fact]
    public void AGroupBoxSitsOutsideTheLinesItHolds()
    {
        var blocks = new List<OcrTextBlock> { new("Exit", new Rect(10, 120, 70, 28)) };

        var group = Assert.Single(OcrDebugBoxes.GroupBoxes(blocks));
        var line = Assert.Single(OcrDebugBoxes.LineBoxes(blocks));

        Assert.True(group.Contains(line), $"{group} does not enclose {line}");
        Assert.Equal(line.Left - OcrDebugBoxes.GroupOutset, group.Left);
        Assert.Equal(line.Right + OcrDebugBoxes.GroupOutset, group.Right);
    }

    /// <summary>
    /// The 偵錯工具 card opens shut, and its contents are out of the tab order while it is. The load
    /// path shuts it on every visit as well; this pins the markup half, which is what a reader
    /// tidying attributes would take for decoration.
    /// </summary>
    [Fact]
    public void TheDebugToolsCardIsCollapsedInMarkup()
    {
        var page = Path.Combine(
            StringsParityTests.ProjectDirectory(), "Views", "Settings", "SettingsPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var fold = XDocument.Load(page)
            .Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "DebugToolsFold");

        Assert.Equal("Collapsed", (string?)fold.Attribute("Visibility"));
        Assert.Equal("0", (string?)fold.Attribute("Height"));
        // Clipped, or the content would spill out of the card while the height is animating.
        Assert.Equal("True", (string?)fold.Attribute("ClipToBounds"));
    }
}
