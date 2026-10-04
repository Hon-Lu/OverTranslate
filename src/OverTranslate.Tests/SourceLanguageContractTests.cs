using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using RapidOcrNet;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The contract the whole layout-metric split exists for: one recognition model and one raw
/// detection must produce the same geometry and the same grouping under 自動, 英文, 日文 and 中文.
/// </summary>
/// <remarks>
/// This drives <see cref="OnnxOcrEngine.ApplyBlockFilters"/> — the real chain the engine runs
/// between the library's raw blocks and the caller's — rather than calling its stages in order.
/// That is deliberate. Both of the language dependencies this work removed were rules that looked
/// language-neutral in isolation: the lone-ideograph cleanup was routed per language by its caller,
/// and BoxShapeNoise read a Bounds that normalisation had already rewritten per script. Tests that
/// covered those rules one at a time stayed green through both. A test that lists the stages has
/// the same blind spot for whatever is added next; one that calls the chain does not.
///
/// No model is loaded. The fixtures are detector output, which is what the contract is about: the
/// same raw detection, read four ways.
/// </remarks>
public class SourceLanguageContractTests
{
    /// <summary>Every source language the router accepts for this path, automatic included.</summary>
    private static readonly string[] SourceLanguages = ["AUTO", "EN", "JA", "ZH"];

    /// <summary>
    /// A capture holding one of everything that has previously been read differently per language.
    /// </summary>
    /// <remarks>
    /// Each entry earns its place:
    ///
    ///   闇 in a 74x40 box   — BoxShapeNoise. Untouched the ratio is 1.85 and it is kept; through
    ///                         the CJK render normalisation the box is pulled to 0.82 of its height
    ///                         and the ratio becomes 2.26, over the threshold. This is the block
    ///                         that used to exist under 英文 and not under 日文.
    ///   田Projects          — the icon cleanup, which used to run on Latin and automatic only.
    ///   本Wikiについて       — the same cleanup's remainder gate: a heading, not noise, on all four.
    ///   攻                  — a lone ideograph, kept, and Cjk to the layout side on all four.
    ///   Confirm / Back      — two UI labels one line apart horizontally that must stay apart.
    ///   the wrapped pair    — a sentence continuing onto a second line, which must join.
    ///   Send / to           — a cross-script-free short word beside a long one, the size test's
    ///                         own trap when it compared normalised heights.
    /// </remarks>
    private static TextBlock[] Capture() =>
    [
        Detected("Confirm", 40, 30, 96, 26),
        Detected("Back", 260, 30, 62, 26),
        Detected("The quick brown fox jumps over", 40, 90, 420, 28),
        Detected("the lazy dog.", 40, 126, 170, 28),
        Detected("Send", 40, 190, 74, 31),
        Detected("to", 128, 195, 30, 25),
        Detected("田Projects", 40, 250, 132, 30),
        Detected("本Wikiについて", 240, 250, 168, 30),
        Detected("攻", 460, 250, 34, 30),
        Detected("闇", 540, 245, 74, 40),
        Detected("ゲーム設定を変更します", 40, 320, 300, 30),
        Detected("設定はいつでも戻せます", 40, 358, 300, 30),
    ];

    /// <summary>Both capture modes. The contract has to hold inside each of them, separately.</summary>
    /// <remarks>
    /// A mode changes how far the thresholds are relaxed. If it were ever allowed to change them
    /// per language as well, this contract would come apart in one mode while staying green in the
    /// other — so the modes are a dimension of the contract, not a thing tested beside it.
    /// </remarks>
    public static TheoryData<CaptureLayoutMode> LayoutModes() =>
        new() { CaptureLayoutMode.Interface, CaptureLayoutMode.General };

    [Theory]
    [MemberData(nameof(LayoutModes))]
    public void Auto_English_Japanese_Chinese_ProduceSameGrouping_ForAMixedCapture(
        CaptureLayoutMode mode)
    {
        var results = SourceLanguages.ToDictionary(
            language => language, language => GroupAs(language, Capture(), mode));

        var reference = results["AUTO"];
        foreach (var language in SourceLanguages)
        {
            var actual = results[language];

            Assert.Equal(reference.Count, actual.Count);
            Assert.Equal(
                reference.Select(block => block.Text),
                actual.Select(block => block.Text));

            // Bounds is deliberately NOT compared: it carries the render normalisation, which is
            // allowed to differ per language and is the whole reason LayoutBounds exists.
            Assert.Equal(
                reference.Select(block => block.LayoutBounds),
                actual.Select(block => block.LayoutBounds));
            Assert.Equal(
                reference.Select(block => block.LayoutScript),
                actual.Select(block => block.LayoutScript));
        }
    }

    /// <summary>
    /// The same contract on a capture whose text is entirely Japanese, where the CJK render
    /// normalisation applies to every block rather than a few.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutModes))]
    public void Auto_English_Japanese_Chinese_ProduceSameGrouping_ForAJapaneseParagraph(
        CaptureLayoutMode mode)
    {
        TextBlock[] Paragraph() =>
        [
            Detected("吾輩は猫である。名前はまだ無い。", 40, 40, 460, 30),
            Detected("どこで生まれたか頓と見当がつかぬ。", 40, 78, 490, 30),
            Detected("決定", 40, 200, 68, 30),
            Detected("もどる", 200, 200, 96, 30),
        ];

        var texts = SourceLanguages
            .Select(language => GroupAs(language, Paragraph(), mode)
                .Select(block => block.Text).ToList())
            .ToList();

        Assert.All(texts, actual => Assert.Equal(texts[0], actual));
    }

    /// <summary>
    /// A block that exists under one source language and not under another is the failure this
    /// whole change was written for, so it gets its own assertion rather than being folded into
    /// the text comparison above.
    /// </summary>
    [Fact]
    public void TheSameRawDetection_KeepsTheSameBlocks_OnEverySourceLanguage()
    {
        var counts = SourceLanguages
            .Select(language => OnnxOcrEngine.ApplyBlockFilters(
                Capture(),
                language,
                OcrLanguageRouter.UsesCjkOnnx(language),
                OcrLanguageRouter.UsesAutomaticLayout(language)).Count)
            .ToList();

        Assert.All(counts, count => Assert.Equal(counts[0], count));

        // And the borderline box is one of the survivors, so the assertion above is not passing
        // because everything was dropped everywhere.
        var kept = OnnxOcrEngine.ApplyBlockFilters(
            Capture(), "JA", useCjkRenderMetrics: true, usesAutomaticLayout: false);
        Assert.Contains(kept, block => block.Text == "闇");
    }

    private static List<OcrTextBlock> GroupAs(
        string language, TextBlock[] capture, CaptureLayoutMode mode) =>
        OcrTextBlockGrouper.Group(
            OnnxOcrEngine.ApplyBlockFilters(
                capture,
                language,
                OcrLanguageRouter.UsesCjkOnnx(language),
                OcrLanguageRouter.UsesAutomaticLayout(language)),
            GroupingProfile.For(mode));

    /// <summary>One detector box, in the shape the library hands over.</summary>
    private static TextBlock Detected(string text, int x, int y, int width, int height) =>
        new()
        {
            BoxPoints =
            [
                new SKPointI(x, y),
                new SKPointI(x + width, y),
                new SKPointI(x + width, y + height),
                new SKPointI(x, y + height),
            ],
            BoxScore = 0.9f,
            Text = text,
            Chars = [.. text.Select(c => c.ToString())],
            CharScores = [.. text.Select(_ => 0.99f)],
        };
}
