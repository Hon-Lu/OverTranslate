using System.Drawing;
using System.Reflection;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

public class VerticalTextCaptureTests
{
    [Fact]
    public async Task VerticalRecognition_PreservesSourceFrameAndRequestsVerticalRecognition()
    {
        using var source = new Bitmap(100, 60);
        using var engine = new RecordingOcrEngine(
            new OcrTextBlock("縦", new System.Windows.Rect(72, 10, 8, 30), Confidence: 0.75));

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "JA", CancellationToken.None);

        Assert.Equal(source.Size, engine.RecognizedSize);
        Assert.True(engine.VerticalText);
        var block = Assert.Single(result);
        Assert.Equal(new System.Windows.Rect(72, 10, 8, 30), block.Bounds);
        Assert.Equal(8, block.RenderGlyphHeight);
        Assert.Equal(0.75, block.Confidence);
        Assert.Equal(4, block.Lines.Count);
    }

    [Fact]
    public void MergeColumns_JoinsRightToLeftAndKeepsOtherGroupsSeparate()
    {
        var columns = new List<OcrTextBlock>
        {
            new("左", new System.Windows.Rect(56, 9, 10, 60), Confidence: 0.4),
            new("右", new System.Windows.Rect(80, 10, 10, 60), Confidence: 0.8),
            new("別", new System.Windows.Rect(20, 50, 10, 40), Confidence: 0.9),
            new("中", new System.Windows.Rect(68, 12, 10, 60), Confidence: 0.6),
        };

        var result = VerticalColumnGrouping.MergeColumns(columns.AsDetected());

        Assert.Equal(2, result.Count);
        Assert.Equal("右中左", result[0].Text);
        Assert.Equal(new System.Windows.Rect(56, 9, 34, 63), result[0].Bounds);
        Assert.Equal(3, result[0].Lines.Count);
        Assert.Equal(0.6, result[0].Confidence!.Value, precision: 10);
        Assert.Equal("別", result[1].Text);
    }

    /// <summary>
    /// The columns of one balloon do not start at one height, and are still one balloon.
    /// </summary>
    /// <remarks>
    /// MEASURED, on <c>2026-09-20 19 14 56 (3).png</c> read at the size a two-page spread gives
    /// each page: the three columns of <c>これまで苦楽を共にしてきた仲間に対する態度か？</c> start
    /// 11px apart on a 14.6px pitch — 0.75 of a character. Judged on their top edges they were
    /// three separate groups, and because their padded boxes overlap, three translations were
    /// drawn on top of one another. The balloon is an oval: the columns at its edges are shorter
    /// and begin lower, by a fraction of their own length rather than by a fixed number of
    /// characters.
    /// </remarks>
    [Fact]
    public void MergeColumns_JoinsColumnsThatStartAtDifferentHeights()
    {
        var columns = new List<OcrTextBlock>
        {
            new("これまで苦楽を", new System.Windows.Rect(239, 154, 31, 102)),
            new("共にしてきた", new System.Windows.Rect(220, 165, 26, 81)),
            new("仲間に対する態度か？", new System.Windows.Rect(200, 153, 32, 144)),
        }.AsDetected();

        var merged = Assert.Single(VerticalColumnGrouping.MergeColumns(columns));

        Assert.Equal("これまで苦楽を共にしてきた仲間に対する態度か？", merged.Text);
    }

    /// <summary>
    /// Running alongside is not enough on its own — a gutter still separates two balloons.
    /// </summary>
    [Fact]
    public void MergeColumns_StillRefusesColumnsAcrossAGutter()
    {
        var columns = new List<OcrTextBlock>
        {
            new("これまで苦楽を", new System.Windows.Rect(239, 154, 31, 102)),
            new("別の吹き出し", new System.Windows.Rect(120, 165, 26, 81)),
        }.AsDetected();

        var result = VerticalColumnGrouping.MergeColumns(columns);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeColumns_DropsWideHorizontalTextBeforeItBridgesSeparateColumns()
    {
        var columns = new List<OcrTextBlock>
        {
            new("右", new System.Windows.Rect(80, 10, 10, 60)),
            new("橫排標題", new System.Windows.Rect(35, 8, 40, 27)),
            new("左", new System.Windows.Rect(20, 10, 10, 60)),
        };

        var result = VerticalColumnGrouping.MergeColumns(columns.AsDetected());

        Assert.Equal(2, result.Count);
        Assert.Equal(["右", "左"], result.Select(block => block.Text));
    }

    /// <summary>
    /// A detection box that swallowed two columns must not double the cell the translation is set
    /// in.
    /// </summary>
    /// <remarks>
    /// The numbers are one real group off <c>.ai/test-images/vertical-realtime-ja</c>: fourteen of
    /// the page's fifteen columns came back 22–26px wide, and the fifteenth was a single 46px box
    /// across two of them holding both columns' 22 characters. Sized on the box widths, the median
    /// of this group's two is that 46 and the balloon is drawn at twice the size of the text it
    /// replaces.
    /// </remarks>
    [Fact]
    public void MergeColumns_SizesTheCellFromTheAreaWhenOneBoxHoldsTwoColumns()
    {
        var columns = new List<OcrTextBlock>
        {
            new(new string('あ', 22), new System.Windows.Rect(57, 452, 46, 252)),
            new(new string('い', 8), new System.Windows.Rect(32, 453, 24, 170)),
        };

        var merged = Assert.Single(VerticalColumnGrouping.MergeColumns(columns.AsDetected()));

        Assert.InRange(merged.RenderGlyphHeight!.Value, 20, 26);
    }

    /// <summary>
    /// The other way a box can lie: far longer than the little that was read out of it. There the
    /// area rule is the one that overshoots, and the box's own width is what holds it down.
    /// </summary>
    [Fact]
    public void MergeColumns_KeepsTheBoxWidthWhenTheAreaWouldOvershoot()
    {
        var column = new OcrTextBlock("！", new System.Windows.Rect(10, 20, 8, 90));

        var merged = Assert.Single(VerticalColumnGrouping.MergeColumns([column.AsDetected()]));

        Assert.Equal(8, merged.RenderGlyphHeight!.Value, precision: 10);
    }

    /// <summary>
    /// Horizontal writing on a vertical page is kept and marked, not discarded.
    /// </summary>
    /// <remarks>
    /// MEASURED, on the 15 comic pages in <c>.ai/test-images/vertical-image-ja2</c>: the wide-box
    /// filter threw away 11 correctly-read blocks across 6 of them at 0.87 to 1.00 confidence — the
    /// name plates, a three-line narration box, a scene label, the chapter-end line. Keeping them
    /// out of the column merge is the part that was needed; dropping them from the output was a
    /// separate thing the same test did.
    /// </remarks>
    [Fact]
    public void Group_KeepsHorizontalTextAndMarksItAsRunningAcross()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("右", new System.Windows.Rect(80, 10, 10, 60)),
            new("オルン・ドゥーラ", new System.Windows.Rect(20, 100, 240, 36)),
            new("左", new System.Windows.Rect(20, 10, 10, 60)),
        }.AsDetected();

        var result = VerticalColumnGrouping.Group(blocks, frameWidth: 300);

        // The wide box still does not bridge the two columns.
        Assert.Equal(["右", "左", "オルン・ドゥーラ"], result.Select(block => block.Text));
        Assert.All(result.Take(2), column => Assert.False(column.RunsAcross));
        Assert.True(result[2].RunsAcross, "the name plate is expected to be marked as running across");
    }

    /// <summary>The flag says what the text does, not how wide its box happens to be.</summary>
    [Fact]
    public void Group_LeavesColumnGroupsUnmarked()
    {
        var columns = new List<OcrTextBlock>
        {
            new("右", new System.Windows.Rect(80, 10, 10, 60)),
            new("中", new System.Windows.Rect(68, 12, 10, 60)),
        }.AsDetected();

        var merged = Assert.Single(VerticalColumnGrouping.Group(columns, frameWidth: 300));

        Assert.False(merged.RunsAcross);
    }

    [Fact]
    public void MergeColumns_KeepsAOneCharacterWideDetection()
    {
        var column = new OcrTextBlock("！", new System.Windows.Rect(10, 20, 30, 15));

        var result = VerticalColumnGrouping.MergeColumns([column.AsDetected()]);

        var kept = Assert.Single(result);
        Assert.Equal(column.Text, kept.Text);
        Assert.Equal(column.Bounds, kept.Bounds);
    }

    /// <summary>
    /// Down the column, then one column left — and the columns actually used sit centred across the
    /// bubble rather than hard against its right edge.
    /// </summary>
    /// <remarks>
    /// Four columns of room, three rows deep, and five characters to place: two columns are filled
    /// and the fourth column of slack is shared, half a column either side. Hanging it all off one
    /// edge is what made every translation read as displaced towards the top right of the source it
    /// replaced.
    /// </remarks>
    [Fact]
    public void VerticalCells_RunDownThenMoveLeft_CentredAcrossTheBubble()
    {
        var cells = OverlayWindow.VerticalCells(
            "ABCDE", new System.Windows.Rect(10, 20, 40, 30), 10).ToList();

        Assert.Equal(new System.Windows.Rect(30, 20, 10, 10), cells[0].Cell);
        Assert.Equal(new System.Windows.Rect(30, 30, 10, 10), cells[1].Cell);
        Assert.Equal(new System.Windows.Rect(30, 40, 10, 10), cells[2].Cell);
        Assert.Equal(new System.Windows.Rect(20, 20, 10, 10), cells[3].Cell);
        Assert.Equal(new System.Windows.Rect(20, 30, 10, 10), cells[4].Cell);
    }

    /// <summary>
    /// A box cut to an exact number of cells holds that many columns, not one fewer.
    /// </summary>
    /// <remarks>
    /// MEASURED, on <c>.ai/test-images/vertical-image-ja/genshin-4koma-column-merge.png</c>: the
    /// left balloon comes back 21.9px per character with 27 characters to place over three columns,
    /// and the live overlay sizes its grid as exactly <c>columns * cellSize</c>. Dividing that back
    /// by the cell is not reliably the count that built it, so the third column measured away and
    /// five characters were dropped — silently, because a character with no cell is simply never
    /// drawn. The tolerance in <see cref="OverlayWindow.VerticalCells"/> is what holds it, and the
    /// expression here is the caller's own so the test cannot drift off the case it guards.
    /// </remarks>
    [Fact]
    public void VerticalCells_KeepEveryColumnOfAGridCutToSize()
    {
        const double cellSize = 21.9;
        const int columns = 3;
        const int rows = 11;
        var text = new string('あ', 27);

        var cells = OverlayWindow.VerticalCells(
            text,
            new System.Windows.Rect(0, 0, columns * cellSize, rows * cellSize),
            cellSize).ToList();

        Assert.Equal(text.Length, cells.Count);
        Assert.Equal(columns, cells.Select(cell => Math.Round(cell.Cell.Left, 3)).Distinct().Count());
    }

    /// <summary>A grid that fills its bubble is unmoved by the centring.</summary>
    [Fact]
    public void VerticalCells_FillingTheBubbleStayAgainstItsEdges()
    {
        var cells = OverlayWindow.VerticalCells(
            "ABCDEF", new System.Windows.Rect(10, 20, 20, 30), 10).ToList();

        Assert.Equal(new System.Windows.Rect(20, 20, 10, 10), cells[0].Cell);
        Assert.Equal(new System.Windows.Rect(10, 20, 10, 10), cells[3].Cell);
    }

    [Fact]
    public void VerticalGrid_KeepsEveryCharacterOfALongTranslation()
    {
        const string translated = "這是一段相當長的翻譯內容需要很多空間才放得下";
        var grid = OverlayWindow.FitVerticalGrid(44, 160, 18, translated.Length);

        var cells = OverlayWindow.VerticalCells(
            translated,
            new System.Windows.Rect(0, 0, 44, grid.Height),
            grid.CellSize);

        Assert.Equal(translated.Length, cells.Count());
        Assert.True(grid.CellSize >= 7);
    }

    [Fact]
    public void VerticalGlyph_UsesCellSizedLineBoxWithoutBaselineClipping()
    {
        OnStaThread(() =>
        {
            var glyph = new TextBlock { FontSize = 29.44 };
            var bounds = new System.Windows.Rect(12, 34, 32, 32);

            OverlayWindow.PositionVerticalGlyph(glyph, bounds);

            Assert.Equal(bounds.Width, glyph.Width);
            Assert.Equal(bounds.Height, glyph.Height);
            Assert.Equal(bounds.Height, glyph.LineHeight);
            Assert.Equal(
                System.Windows.LineStackingStrategy.BlockLineHeight,
                glyph.LineStackingStrategy);
            Assert.Equal(bounds.X, Canvas.GetLeft(glyph));
            Assert.Equal(bounds.Y, Canvas.GetTop(glyph));
        });
    }

    [Theory]
    [InlineData('「', true)]
    [InlineData('）', true)]
    [InlineData('—', true)]
    [InlineData('…', true)]
    [InlineData('ー', true)]
    [InlineData('。', false)]
    [InlineData('A', false)]
    [InlineData('漢', false)]
    public void VerticalGlyphRotation_MatchesTypographyRules(char glyph, bool expected)
    {
        Assert.Equal(expected, OverlayWindow.RotatesInVerticalText(glyph));
    }

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>
    /// Both coverage and layout boxes retain their original source coordinates.
    /// </summary>
    [Fact]
    public async Task VerticalRecognition_PreservesLayoutBoundsAsWellAsBounds()
    {
        using var source = new Bitmap(100, 60);
        // As the CJK path hands it over: Bounds pulled in onto the glyphs, LayoutBounds untouched.
        var recognized = new OcrTextBlock("縦書き", new System.Windows.Rect(22, 10, 8, 30))
        {
            LayoutBounds = new System.Windows.Rect(20, 10, 12, 30),
            LayoutScript = OcrLayoutScript.Cjk,
        };
        using var engine = new RecordingOcrEngine(recognized);

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "JA", CancellationToken.None);

        var block = Assert.Single(result);
        Assert.Equal(recognized.Bounds, block.Bounds);
        Assert.Equal(
            recognized.LayoutBounds,
            block.LayoutBounds);
        Assert.NotEqual(block.Bounds, block.LayoutBounds);

        // A vertical cell is measured across the column, not along its height.
        Assert.Equal(recognized.Bounds.Width, block.RenderGlyphHeight);
    }

    [Theory]
    // Vertical writing is not a script. A western title down the spine of a Japanese book is
    // still Latin, and the layout side must be told so by the text rather than by the rotation.
    [InlineData("縦書き", OcrLayoutScript.Cjk)]
    [InlineData("Vertigo", OcrLayoutScript.Latin)]
    [InlineData("BanG夢", OcrLayoutScript.Mixed)]
    public async Task VerticalText_LayoutScript_FollowsActualText_NotOrientation(
        string text, OcrLayoutScript expected)
    {
        using var source = new Bitmap(100, 60);
        using var engine = new RecordingOcrEngine(
            new OcrTextBlock(text, new System.Windows.Rect(20, 10, 10, 60)));

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "JA", CancellationToken.None);

        Assert.Equal(expected, Assert.Single(result).LayoutScript);
    }

    /// <summary>
    /// The same shape as the wide-horizontal-text case above, but with the two rectangles landing
    /// on opposite sides of the 1.4 candidate test: normalised, the strip looks narrow enough to be
    /// a column and bridges the two real ones into a single group.
    /// </summary>
    [Fact]
    public void MergeColumns_JudgesTheColumnShapeOnLayoutBounds()
    {
        var strip = new OcrTextBlock("橫排標題", new System.Windows.Rect(35, 8, 54, 40))
        {
            // 54 / 40 = 1.35, inside the 1.4 bar; the detector's own 66 / 40 = 1.65 is outside it.
            LayoutBounds = new System.Windows.Rect(35, 8, 66, 40),
        };
        var columns = new List<OcrTextBlock>
        {
            new OcrTextBlock("右", new System.Windows.Rect(80, 10, 10, 60)).AsDetected(),
            strip,
            new OcrTextBlock("左", new System.Windows.Rect(20, 10, 10, 60)).AsDetected(),
        };

        var result = VerticalColumnGrouping.MergeColumns(columns);

        Assert.Equal(2, result.Count);
        Assert.Equal(["右", "左"], result.Select(block => block.Text));
    }

    /// <summary>A merged column group carries layout metrics on, so nothing downstream sees a gap.</summary>
    [Fact]
    public void MergeColumns_CarriesLayoutMetricsOntoTheGroup()
    {
        var columns = new List<OcrTextBlock>
        {
            new OcrTextBlock("右", new System.Windows.Rect(80, 10, 10, 60)),
            new OcrTextBlock("中", new System.Windows.Rect(68, 12, 10, 60)),
        }.AsDetected();

        var merged = Assert.Single(VerticalColumnGrouping.MergeColumns(columns));

        Assert.Equal(OcrLayoutScript.Cjk, merged.LayoutScript);
        Assert.Equal(new System.Windows.Rect(68, 10, 22, 62), merged.LayoutBounds);
        Assert.NotNull(merged.LayoutGlyphHeight);
    }

    // Horizontal capture profiles must not change native vertical grouping.
    [Fact]
    public async Task VerticalGroupingAnswersOnGeometry_NotOnACaptureProfile()
    {
        var (previous, current) = OcrTextBlockGrouperTests.CentredBalloonPair();
        OcrTextBlock[] pair = [previous, current];

        // The two profiles really do answer this pair differently, which is what makes it worth
        // asking the vertical path about at all.
        Assert.Equal(2, OcrTextBlockGrouper.Group([.. pair], GroupingProfile.Interface).Count);
        Assert.Single(OcrTextBlockGrouper.Group([.. pair], GroupingProfile.General));

        // Turned into columns the pair is joined, and it is the geometry saying so: the two run
        // alongside each other for their whole length, 1.4 character widths apart, which is what
        // the two columns of one balloon look like — and the fixture is one balloon, "A BARBARIAN
        // A HEREDITARY TITLE!". It used to be refused, by a test on the columns' TOP EDGES that a
        // balloon's ragged tops fail; see SideBySideAlongTheColumn for the page that cost.
        Assert.Single(await GroupColumns(pair, gutter: 0));

        // A gutter between them is refused, with the profiles no more involved than before: the
        // distance test is what holds the edge of a balloon, and it still does.
        var apart = await GroupColumns(pair, gutter: 260);
        Assert.Equal(2, apart.Count);
        Assert.DoesNotContain(
            apart,
            block => block.Text.Contains("A BARBARIAN A") && block.Text.Contains("HEREDITARY TITLE!"));
    }

    /// <summary>
    /// Runs the vertical pipeline over the pair turned into columns, moved this far apart.
    /// </summary>
    private static async Task<List<OcrTextBlock>> GroupColumns(OcrTextBlock[] pair, double gutter)
    {
        // Big enough to hold the fixture's boxes: mapping back off the edge of the picture would
        // make the column merge judge rectangles that never existed.
        using var source = new Bitmap(1300, 1300);
        using var engine = new RecordingOcrEngine(pair.Select((block, index) => block with
        {
            Bounds = Shift(ToVerticalBounds(block.Bounds, source.Width), index * gutter),
            LayoutBounds = Shift(ToVerticalBounds(block.LayoutBounds, source.Width), index * gutter),
        }).ToArray());

        return await OcrService.RecognizeVerticalAsync(
            engine, source, "EN", CancellationToken.None);
    }

    private static System.Windows.Rect Shift(System.Windows.Rect rect, double dx) =>
        new(rect.X - dx, rect.Y, rect.Width, rect.Height);

    /// <summary>
    /// Neither vertical pass takes a profile, and this is the guard on it staying that way.
    /// </summary>
    /// <remarks>
    /// Both compare column against column. None of the thresholds a capture mode moves were
    /// measured on that geometry, so handing either of them one would be relaxing something nobody
    /// has measured — which is exactly the kind of change that reads as tidying up a signature. The
    /// absence of the parameter is what makes it impossible rather than merely unintended.
    /// </remarks>
    [Theory]
    [InlineData(typeof(OcrService), nameof(OcrService.RecognizeVerticalAsync))]
    [InlineData(typeof(VerticalColumnGrouping), nameof(VerticalColumnGrouping.MergeColumns))]
    public void TheVerticalPipeline_TakesNoProfile(Type owner, string method)
    {
        var parameters = owner
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(GroupingProfile));
    }

    private static System.Windows.Rect ToVerticalBounds(System.Windows.Rect r, int width) =>
        new(width - r.Bottom, r.X, r.Height, r.Width);

    private sealed class RecordingOcrEngine(params OcrTextBlock[] blocks) : IOcrEngine
    {
        public Size RecognizedSize { get; private set; }
        public bool VerticalText { get; private set; }

        public Task<List<OcrTextBlock>> RecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            CancellationToken cancellationToken = default, bool verticalText = false)
        {
            RecognizedSize = bitmap.Size;
            VerticalText = verticalText;
            return Task.FromResult(blocks.AsDetected());
        }

        public Task<List<OcrTextBlock>?> TryRecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            int? maxDetectSize = null,
            CancellationToken cancellationToken = default, bool verticalText = false) =>
            Task.FromResult<List<OcrTextBlock>?>(blocks.AsDetected());

        public void Dispose()
        {
        }
    }
}
