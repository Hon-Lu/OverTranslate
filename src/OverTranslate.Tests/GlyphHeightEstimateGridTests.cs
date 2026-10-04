using System.Globalization;
using System.Text;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// Pins what the glyph height estimate returns, on both sides that read it, over a grid chosen to
/// sit on each of its branches.
/// </summary>
/// <remarks>
/// <para>The estimate is shared: the layout side reads it through <c>LayoutGlyphHeightFor</c> and
/// the overlay side through the render normalisation, and both come out of the same
/// <c>EstimateGlyphHeight</c>. So a change made for one of them — including a change that only
/// means to observe it — is a change to the other, and "the grouping came out the same" does not
/// say the overlay did.</para>
///
/// <para>Written as one dump rather than a case per branch on purpose: what has to be proved is
/// that nothing moved anywhere, and a list of separate assertions proves it only where somebody
/// thought to assert. The rows are the branch boundaries — a width either side of twice the box
/// height, the glyph counts either side of the pitch clamp, both scripts, and a box small enough to
/// reach the floor.</para>
/// </remarks>
public class GlyphHeightEstimateGridTests(ITestOutputHelper output)
{
    /// <summary>
    /// The rows, shared with the trace tests so that both are asked about the same boxes.
    /// </summary>
    internal static readonly (string Text, Rect Box)[] Grid =
    [
        // The web-v3 pair this grid was written for: two lines of one paragraph, one on each side
        // of the pitch branch.
        ("cost.", new Rect(0, 0, 62, 31)),
        ("cost.", new Rect(0, 0, 61, 31)),
        ("cost.", new Rect(0, 0, 63, 31)),
        ("continues to use PP-DocLayoutV3 for layout analysis", new Rect(0, 0, 1737, 33)),
        // The measured short-line cases ShortTextGlyphHeight was built on.
        ("YA", new Rect(0, 0, 40, 95)),
        ("Wow", new Rect(0, 0, 60, 95)),
        ("Hello there friend", new Rect(0, 0, 300, 86)),
        // Either side of the four-glyph clamp, at one width.
        ("abc", new Rect(0, 0, 200, 40)),
        ("abcd", new Rect(0, 0, 200, 40)),
        // Four glyphs but not twice as wide as tall: the clamp refuses, so the short-line
        // correction takes it on the Latin side; CJK keeps its box on both.
        ("TOO.", new Rect(0, 0, 55, 32)),
        ("日本語です", new Rect(0, 0, 80, 50)),
        // CJK takes the other pitch coefficient and no short-line correction.
        ("設定", new Rect(0, 0, 80, 40)),
        ("日本語のテキストです", new Rect(0, 0, 400, 40)),
        // Mixed gets no layout estimate at all, and still renders.
        ("BanG Dream! アニメ", new Rect(0, 0, 300, 30)),
        // The floor, and a box with no height.
        ("test", new Rect(0, 0, 4, 2)),
        ("test", new Rect(0, 0, 40, 0)),
    ];

    [Fact]
    public void TheEstimateIsUnchangedOnBothSidesThatReadIt()
    {
        var dump = Dump();
        output.WriteLine(dump);

        Assert.Equal(Expected, dump);
    }

    private static string Dump()
    {
        var builder = new StringBuilder();

        foreach (var (text, box) in Grid)
        {
            var script = LayoutScriptDetection.For(text);
            var layout = OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text);

            var latin = OnnxOcrEngine.NormalizeBlocks([new OcrTextBlock(text, box)], useCjkRenderMetrics: false)[0];
            var cjk = OnnxOcrEngine.NormalizeBlocks([new OcrTextBlock(text, box)], useCjkRenderMetrics: true)[0];

            builder.Append(CultureInfo.InvariantCulture, $"{text} | {box.Width:0.0000}x{box.Height:0.0000} | {script}");
            builder.Append(CultureInfo.InvariantCulture, $" | layout={Number(layout)}");
            builder.Append(CultureInfo.InvariantCulture, $" | renderLatin={Number(latin.RenderGlyphHeight)} {Box(latin.Bounds)}");
            builder.Append(CultureInfo.InvariantCulture, $" | renderCjk={Number(cjk.RenderGlyphHeight)} {Box(cjk.Bounds)}");
            builder.Append(LineBreak);
        }

        return builder.ToString();

        static string Number(double? value) =>
            value is { } number ? number.ToString("0.0000", CultureInfo.InvariantCulture) : "null";

        static string Box(Rect box) => string.Create(
            CultureInfo.InvariantCulture,
            $"{box.X:0.0000},{box.Y:0.0000},{box.Width:0.0000},{box.Height:0.0000}");
    }

    // Written out rather than Environment.NewLine so the expected dump below means the same thing
    // wherever the tests run.
    private const string LineBreak = "\n";

    private static readonly string[] ExpectedRows =
    [
        "cost. | 62.0000x31.0000 | Latin | layout=15.5000 | renderLatin=15.5000 0.0000,0.0000,62.0000,31.0000 | renderCjk=null 0.0000,2.7900,62.0000,25.4200",
        "cost. | 61.0000x31.0000 | Latin | layout=15.5000 | renderLatin=15.5000 0.0000,0.0000,61.0000,31.0000 | renderCjk=null 0.0000,2.7900,61.0000,25.4200",
        "cost. | 63.0000x31.0000 | Latin | layout=16.3800 | renderLatin=16.3800 0.0000,0.0000,63.0000,31.0000 | renderCjk=null 0.0000,8.0660,63.0000,14.8680",
        "continues to use PP-DocLayoutV3 for layout analysis | 1737.0000x33.0000 | Latin | layout=27.0600 | renderLatin=27.0600 0.0000,0.0000,1737.0000,33.0000 | renderCjk=null 0.0000,2.9700,1737.0000,27.0600",
        "YA | 40.0000x95.0000 | Latin | layout=47.5000 | renderLatin=47.5000 0.0000,0.0000,40.0000,95.0000 | renderCjk=null 0.0000,8.5500,40.0000,77.9000",
        "Wow | 60.0000x95.0000 | Latin | layout=47.5000 | renderLatin=47.5000 0.0000,0.0000,60.0000,95.0000 | renderCjk=null 0.0000,8.5500,60.0000,77.9000",
        "Hello there friend | 300.0000x86.0000 | Latin | layout=24.3750 | renderLatin=24.3750 0.0000,0.0000,300.0000,86.0000 | renderCjk=null 0.0000,31.9375,300.0000,22.1250",
        "abc | 200.0000x40.0000 | Latin | layout=20.0000 | renderLatin=20.0000 0.0000,0.0000,200.0000,40.0000 | renderCjk=null 0.0000,3.6000,200.0000,32.8000",
        "abcd | 200.0000x40.0000 | Latin | layout=32.8000 | renderLatin=32.8000 0.0000,0.0000,200.0000,40.0000 | renderCjk=null 0.0000,3.6000,200.0000,32.8000",
        "TOO. | 55.0000x32.0000 | Latin | layout=16.0000 | renderLatin=16.0000 0.0000,0.0000,55.0000,32.0000 | renderCjk=null 0.0000,2.8800,55.0000,26.2400",
        "日本語です | 80.0000x50.0000 | Cjk | layout=41.0000 | renderLatin=25.0000 0.0000,0.0000,80.0000,50.0000 | renderCjk=null 0.0000,4.5000,80.0000,41.0000",
        "設定 | 80.0000x40.0000 | Cjk | layout=32.8000 | renderLatin=20.0000 0.0000,0.0000,80.0000,40.0000 | renderCjk=null 0.0000,3.6000,80.0000,32.8000",
        "日本語のテキストです | 400.0000x40.0000 | Cjk | layout=32.8000 | renderLatin=32.8000 0.0000,0.0000,400.0000,40.0000 | renderCjk=null 0.0000,3.6000,400.0000,32.8000",
        "BanG Dream! アニメ | 300.0000x30.0000 | Mixed | layout=null | renderLatin=24.6000 0.0000,0.0000,300.0000,30.0000 | renderCjk=null 0.0000,2.7000,300.0000,24.6000",
        "test | 4.0000x2.0000 | Latin | layout=1.0000 | renderLatin=1.0000 0.0000,0.0000,4.0000,2.0000 | renderCjk=null 0.0000,0.1800,4.0000,1.6400",
        "test | 40.0000x0.0000 | Latin | layout=1.0000 | renderLatin=1.0000 0.0000,0.0000,40.0000,0.0000 | renderCjk=null 0.0000,-0.5000,40.0000,1.0000",
    ];

    private static readonly string Expected =
        string.Concat(ExpectedRows.Select(row => row + LineBreak));
}
