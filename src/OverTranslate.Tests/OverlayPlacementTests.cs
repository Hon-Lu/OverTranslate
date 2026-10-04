using System.Windows;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The one layer that reads the capture mode on the way to the screen, and what it is allowed to
/// hand the renderer.
/// </summary>
public class OverlayPlacementTests
{
    private static TranslatedBlock Group(params Rect[] lines) => new(
        "SOURCE",
        "譯文",
        new Rect(10, 10, 200, 60),
        SourceLineBounds: lines);

    private static readonly Rect[] TwoLines =
        [new Rect(10, 10, 200, 28), new Rect(10, 40, 180, 28)];

    [Fact]
    public void GeneralMode_AsksForTheWholeGroupToBeReset()
    {
        var placed = OverlayPlacement.Place(
            [Group(TwoLines)], CaptureLayoutMode.General, verticalText: false);

        Assert.Equal(OverlayLayoutIntent.GroupReflow, Assert.Single(placed).LayoutIntent);
    }

    /// <summary>
    /// A single line has no group to re-set, and the ordinary path may widen it where there is
    /// room — which is what a caption wants and what a balloon must never do.
    /// </summary>
    [Fact]
    public void GeneralMode_LeavesASingleLineOnTheOrdinaryPath()
    {
        var line = new TranslatedBlock("SOURCE", "譯文", new Rect(10, 10, 200, 28));

        var placed = OverlayPlacement.Place(
            [line], CaptureLayoutMode.General, verticalText: false);

        Assert.Equal(OverlayLayoutIntent.Default, Assert.Single(placed).LayoutIntent);
    }

    /// <summary>
    /// design.md §11.1 #3 — after placement, a horizontal interface capture holds nothing that
    /// still needs re-setting as a group: each source line is back in its own box.
    /// </summary>
    /// <remarks>
    /// What the translator was asked is unaffected, and that is the point of doing this here.
    /// The group went to the engine whole and is still whole in the list the toolbar copies from;
    /// only the drawing is per line.
    /// </remarks>
    [Fact]
    public void InterfaceMode_PutsEachSourceLineBackInItsOwnBox()
    {
        var placed = OverlayPlacement.Place(
            [Group(TwoLines)], CaptureLayoutMode.Interface, verticalText: false);

        Assert.Equal(2, placed.Count);
        Assert.Equal(TwoLines, placed.Select(block => block.Bounds));
        Assert.All(placed, block => Assert.Equal(OverlayLayoutIntent.Default, block.LayoutIntent));
        Assert.All(placed, block => Assert.Null(block.SourceLineBounds));
    }

    /// <summary>
    /// A mode this build does not know — a setting written by a later release — is drawn the way
    /// the default is.
    /// </summary>
    /// <remarks>
    /// It answers the same as General because that is where such a value has already landed: a name
    /// the settings reader cannot deserialize leaves the property on its default, and the default is
    /// General. This is the second half of that same answer rather than a separate policy — see
    /// GroupingProfile.For, which falls the same way for the same reason.
    /// </remarks>
    [Fact]
    public void AModeThisBuildDoesNotKnow_IsDrawnTheWayTheDefaultIs()
    {
        var placed = OverlayPlacement.Place(
            [Group(TwoLines)], (CaptureLayoutMode)99, verticalText: false);

        Assert.Equal(
            OverlayPlacement.Place([Group(TwoLines)], CaptureLayoutMode.General, verticalText: false),
            placed);
        Assert.Equal(OverlayLayoutIntent.GroupReflow, Assert.Single(placed).LayoutIntent);
    }

    // ---- design.md §8.5.1: what vertical text is not allowed to be put through ----

    /// <summary>
    /// §8.5.1 #1 and #2: neither mode changes anything about a vertical capture.
    /// </summary>
    /// <remarks>
    /// SourceLineBounds means something else here. CombineColumns refills it with the
    /// columns a block was assembled from — or, for a lone column, with one cell per character —
    /// so a splitter that shares the translation out by line width would be sharing it out by
    /// character cell, and a "group" to re-set inside its box is not what the list describes.
    /// </remarks>
    [Theory]
    [InlineData(CaptureLayoutMode.Interface)]
    [InlineData(CaptureLayoutMode.General)]
    public void VerticalText_ReachesTheVerticalRendererExactlyAsItArrived(CaptureLayoutMode mode)
    {
        // The shape CombineColumns produces for a lone column: one cell per character.
        var cells = new[]
        {
            new Rect(80, 10, 20, 20), new Rect(80, 30, 20, 20), new Rect(80, 50, 20, 20),
        };
        var column = new TranslatedBlock("縦書き", "直排譯文", new Rect(80, 10, 20, 60), cells);

        var placed = OverlayPlacement.Place([column], mode, verticalText: true);

        var only = Assert.Single(placed);
        Assert.Equal(OverlayLayoutIntent.Default, only.LayoutIntent);
        Assert.Same(cells, only.SourceLineBounds);
        Assert.Equal(column, only);
    }
}
