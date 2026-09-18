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
    public async Task VerticalRecognition_RotatesAnticlockwiseAndMapsBoundsBack()
    {
        using var source = new Bitmap(100, 60);
        using var engine = new RecordingOcrEngine(
            new OcrTextBlock("縦", new System.Windows.Rect(10, 20, 30, 8), Confidence: 0.75));

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "JA", CancellationToken.None);

        Assert.Equal(new Size(60, 100), engine.RecognizedSize);
        var block = Assert.Single(result);
        Assert.Equal(new System.Windows.Rect(72, 10, 8, 30), block.Bounds);
        Assert.Equal(8, block.RenderGlyphHeight);
        Assert.Equal(0.75, block.Confidence);
        Assert.Equal(4, block.Lines.Count);
    }

    [Fact]
    public void MergeVerticalColumns_JoinsRightToLeftAndKeepsOtherGroupsSeparate()
    {
        var columns = new List<OcrTextBlock>
        {
            new("左", new System.Windows.Rect(56, 9, 10, 60), Confidence: 0.4),
            new("右", new System.Windows.Rect(80, 10, 10, 60), Confidence: 0.8),
            new("別", new System.Windows.Rect(20, 50, 10, 40), Confidence: 0.9),
            new("中", new System.Windows.Rect(68, 12, 10, 60), Confidence: 0.6),
        };

        var result = OcrService.MergeVerticalColumns(columns.AsDetected());

        Assert.Equal(2, result.Count);
        Assert.Equal("右中左", result[0].Text);
        Assert.Equal(new System.Windows.Rect(56, 9, 34, 63), result[0].Bounds);
        Assert.Equal(3, result[0].Lines.Count);
        Assert.Equal(0.6, result[0].Confidence!.Value, precision: 10);
        Assert.Equal("別", result[1].Text);
    }

    [Fact]
    public void MergeVerticalColumns_DropsWideHorizontalTextBeforeItBridgesSeparateColumns()
    {
        var columns = new List<OcrTextBlock>
        {
            new("右", new System.Windows.Rect(80, 10, 10, 60)),
            new("橫排標題", new System.Windows.Rect(35, 8, 40, 27)),
            new("左", new System.Windows.Rect(20, 10, 10, 60)),
        };

        var result = OcrService.MergeVerticalColumns(columns.AsDetected());

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
    public void MergeVerticalColumns_SizesTheCellFromTheAreaWhenOneBoxHoldsTwoColumns()
    {
        var columns = new List<OcrTextBlock>
        {
            new(new string('あ', 22), new System.Windows.Rect(57, 452, 46, 252)),
            new(new string('い', 8), new System.Windows.Rect(32, 453, 24, 170)),
        };

        var merged = Assert.Single(OcrService.MergeVerticalColumns(columns.AsDetected()));

        Assert.InRange(merged.RenderGlyphHeight!.Value, 20, 26);
    }

    /// <summary>
    /// The other way a box can lie: far longer than the little that was read out of it. There the
    /// area rule is the one that overshoots, and the box's own width is what holds it down.
    /// </summary>
    [Fact]
    public void MergeVerticalColumns_KeepsTheBoxWidthWhenTheAreaWouldOvershoot()
    {
        var column = new OcrTextBlock("！", new System.Windows.Rect(10, 20, 8, 90));

        var merged = Assert.Single(OcrService.MergeVerticalColumns([column.AsDetected()]));

        Assert.Equal(8, merged.RenderGlyphHeight!.Value, precision: 10);
    }

    [Fact]
    public void MergeVerticalColumns_KeepsAOneCharacterWideDetection()
    {
        var column = new OcrTextBlock("！", new System.Windows.Rect(10, 20, 30, 15));

        var result = OcrService.MergeVerticalColumns([column.AsDetected()]);

        var kept = Assert.Single(result);
        Assert.Equal(column.Text, kept.Text);
        Assert.Equal(column.Bounds, kept.Bounds);
    }

    [Fact]
    public void VerticalCells_RunDownThenMoveLeft()
    {
        var cells = OverlayWindow.VerticalCells(
            "ABCDE", new System.Windows.Rect(10, 20, 40, 30), 10).ToList();

        Assert.Equal(new System.Windows.Rect(40, 20, 10, 10), cells[0].Cell);
        Assert.Equal(new System.Windows.Rect(40, 30, 10, 10), cells[1].Cell);
        Assert.Equal(new System.Windows.Rect(40, 40, 10, 10), cells[2].Cell);
        Assert.Equal(new System.Windows.Rect(30, 20, 10, 10), cells[3].Cell);
        Assert.Equal(new System.Windows.Rect(30, 30, 10, 10), cells[4].Cell);
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
    /// The layout box has to turn with the picture, or the column merge below is comparing a
    /// rectangle in the rotated frame against ones that are not.
    /// </summary>
    [Fact]
    public async Task VerticalRecognition_MapsLayoutBoundsBackAsWellAsBounds()
    {
        using var source = new Bitmap(100, 60);
        // As the CJK path hands it over: Bounds pulled in onto the glyphs, LayoutBounds untouched.
        var recognized = new OcrTextBlock("縦書き", new System.Windows.Rect(10, 22, 30, 8))
        {
            LayoutBounds = new System.Windows.Rect(10, 20, 30, 12),
            LayoutScript = OcrLayoutScript.Cjk,
        };
        using var engine = new RecordingOcrEngine(recognized);

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "JA", CancellationToken.None);

        var block = Assert.Single(result);
        Assert.Equal(OcrService.MapVerticalBoundsBack(recognized.Bounds, source.Width), block.Bounds);
        Assert.Equal(
            OcrService.MapVerticalBoundsBack(recognized.LayoutBounds, source.Width),
            block.LayoutBounds);
        Assert.NotEqual(block.Bounds, block.LayoutBounds);

        // Render contract: the overlay's own numbers are still the rotated row height and the
        // mapped coverage box, untouched by any of the above.
        Assert.Equal(recognized.Bounds.Height, block.RenderGlyphHeight);
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
            new OcrTextBlock(text, new System.Windows.Rect(10, 20, 30, 10)));

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
    public void MergeVerticalColumns_JudgesTheColumnShapeOnLayoutBounds()
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

        var result = OcrService.MergeVerticalColumns(columns);

        Assert.Equal(2, result.Count);
        Assert.Equal(["右", "左"], result.Select(block => block.Text));
    }

    /// <summary>A merged column group carries layout metrics on, so nothing downstream sees a gap.</summary>
    [Fact]
    public void MergeVerticalColumns_CarriesLayoutMetricsOntoTheGroup()
    {
        var columns = new List<OcrTextBlock>
        {
            new OcrTextBlock("右", new System.Windows.Rect(80, 10, 10, 60)),
            new OcrTextBlock("中", new System.Windows.Rect(68, 12, 10, 60)),
        }.AsDetected();

        var merged = Assert.Single(OcrService.MergeVerticalColumns(columns));

        Assert.Equal(OcrLayoutScript.Cjk, merged.LayoutScript);
        Assert.Equal(new System.Windows.Rect(68, 10, 22, 62), merged.LayoutBounds);
        Assert.NotNull(merged.LayoutGlyphHeight);
    }

    // ---- design.md §8.5.1 #4 (v2.1, reversed): the capture mode reaches none of this ----

    /// <summary>
    /// A capture mode cannot change how vertical text is grouped, and the relaxed profile that
    /// would have changed it is here to prove the test is asking a real question.
    /// </summary>
    /// <remarks>
    /// <para>v1.1 wired the mode's profile into the first pass on the grounds that it is "the same
    /// horizontal grouper". It is the same grouper, and that turned out to be the wrong reason: the
    /// picture has been turned 270° before it gets there, so each column of the original arrives as
    /// a row, and the first pass's "does this line continue on the next one" is being asked of
    /// column against column — the very geometry the column merge was denied a profile over.</para>
    ///
    /// <para>The pair below is the set-solid one from <c>OcrTextBlockGrouperTests</c>, chosen
    /// because the two profiles are known to disagree about it. The first two assertions establish
    /// that disagreement rather than assume it: without them, a pipeline that ignored the profile
    /// and a pair that no profile would have joined look exactly alike, which is the mistake the
    /// harness made for two steps.</para>
    /// </remarks>
    [Fact]
    public async Task TheRelaxedProfile_DoesNotReachTheGroupingThatRunsOnTheRotatedFrame()
    {
        var (previous, current) = OcrTextBlockGrouperTests.CentredBalloonPair();
        OcrTextBlock[] pair = [previous, current];

        // The two profiles really do answer this pair differently.
        Assert.Equal(2, OcrTextBlockGrouper.Group([.. pair], GroupingProfile.Interface).Count);
        Assert.Single(OcrTextBlockGrouper.Group([.. pair], GroupingProfile.General));

        // Big enough to hold the fixture's boxes: mapping back off the edge of the picture would
        // make the column merge judge rectangles that never existed.
        using var source = new Bitmap(1300, 1300);
        using var engine = new RecordingOcrEngine(pair);

        var result = await OcrService.RecognizeVerticalAsync(
            engine, source, "EN", CancellationToken.None);

        // Two lines in, two blocks out: the first pass judged them on the conservative figures and
        // kept them apart. Had the relaxed profile reached it they would have been joined there and
        // come back as one.
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(
            result,
            block => block.Text.Contains("A BARBARIAN A") && block.Text.Contains("HEREDITARY TITLE!"));
    }

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
    [InlineData(nameof(OcrService.RecognizeVerticalAsync))]
    [InlineData(nameof(OcrService.MergeVerticalColumns))]
    public void TheVerticalPipeline_TakesNoProfile(string method)
    {
        var parameters = typeof(OcrService)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(GroupingProfile));
    }

    private sealed class RecordingOcrEngine(params OcrTextBlock[] blocks) : IOcrEngine
    {
        public Size RecognizedSize { get; private set; }

        public Task<List<OcrTextBlock>> RecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            CancellationToken cancellationToken = default)
        {
            RecognizedSize = bitmap.Size;
            return Task.FromResult(blocks.AsDetected());
        }

        public Task<List<OcrTextBlock>?> TryRecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            int? maxDetectSize = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<List<OcrTextBlock>?>(blocks.AsDetected());

        public void Dispose()
        {
        }
    }
}
