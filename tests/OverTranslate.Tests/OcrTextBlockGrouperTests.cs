using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class OcrTextBlockGrouperTests
{
    [Fact]
    public void GroupsAdjacentAlignedLinesIntoSingleBlock()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家", new Rect(10, 40, 110, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal(2, grouped[0].Lines.Count);
        Assert.Equal("寧夏夜市美食攻略 食尚玩家", grouped[0].Text);
    }

    [Fact]
    public void KeepsDifferentColumnsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("LEFT", new Rect(10, 10, 100, 20)),
            new("RIGHT", new Rect(220, 38, 110, 20)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsDifferentFontSizesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 180, 28)),
            new("summary", new Rect(10, 48, 180, 16)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsTitleAndBodySeparateWhenHeightsAreCloseButDistinct()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 220, 28)),
            new("body text", new Rect(10, 44, 220, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void MergesAdjacentFragmentsOnTheSameLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏", new Rect(10, 10, 42, 24)),
            new("夜市", new Rect(58, 10, 42, 24)),
            new("攻略", new Rect(106, 10, 42, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("寧夏夜市攻略", grouped[0].Text);
    }

    [Fact]
    public void MergesPerWordBoxesOnOneLineWhenBoxTopsVaryFromAscendersAndDescenders()
    {
        // Real geometry: a single line the detector split into per-word boxes whose tops vary
        // (32..37) with ascenders/descenders. A Y-primary sort interleaves them, so the words must
        // be re-merged by reading order into one line instead of separate translations.
        var blocks = new List<OcrTextBlock>
        {
            new("Send", new Rect(0, 32, 132, 59)),
            new("and", new Rect(1082, 34, 100, 59)),
            new("terminal", new Rect(1475, 34, 246, 57)),
            new("this", new Rect(150, 35, 134, 57)),
            new("the", new Rect(632, 35, 101, 56)),
            new("background", new Rect(753, 35, 309, 58)),
            new("free", new Rect(1203, 35, 130, 58)),
            new("the", new Rect(1353, 35, 101, 56)),
            new("session", new Rect(302, 36, 221, 54)),
            new("to", new Rect(539, 37, 72, 54)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("Send this session to the background and free the terminal", grouped[0].Text);
    }

    [Fact]
    public void MergesHeadingWordBoxesThatOverlapHorizontallyFromUnclipExpansion()
    {
        // Real geometry from a large EN capture: the detector's unclip expansion enlarges the big
        // heading word-boxes until they overlap horizontally ("Translate" right edge 533 vs
        // "your website" left edge 515 → gap -18). They must still merge into one line instead of
        // being translated separately as "翻譯" + "你的 網站".
        var blocks = new List<OcrTextBlock>
        {
            new("Translate", new Rect(136, 68, 397, 99)),
            new("your website", new Rect(515, 71, 565, 102)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("Translate your website", grouped[0].Text);
    }

    [Fact]
    public void MergesShortMidLineWordThatIsFullyNestedInTheLine()
    {
        // Real geometry from a captured line: the short word "to" (h=25) is shorter than its
        // neighbours (h=31) so heightRatio is only ~0.81, but it sits on the same baseline (fully
        // nested, vertical overlap ~1.0) and must not be dropped out of the middle of the line.
        var blocks = new List<OcrTextBlock>
        {
            new("session", new Rect(120, 8, 95, 31)),
            new("to", new Rect(215, 11, 30, 25)),
            new("the", new Rect(250, 8, 46, 31)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("session to the", grouped[0].Text);
    }

    [Fact]
    public void KeepsTwoDistinctSameRowButtonsSeparate()
    {
        // Real geometry: two distinct buttons on the same row — "Download key-level usage report"
        // (h=32) and a shorter/taller, vertically offset "Create key" (h=38). heightRatio 0.84 and
        // vertical overlap 0.47 must keep them apart; over-merging them was a regression.
        var blocks = new List<OcrTextBlock>
        {
            new("Download key-level usage report", new Rect(1293, 321, 305, 32)),
            new("Create key", new Rect(1632, 298, 112, 38)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsSeparatePhrasesOnTheSameLineWhenGapIsLarge()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("左側", new Rect(10, 10, 42, 24)),
            new("右側", new Rect(140, 10, 42, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void GroupsWrappedSentenceWhenOpeningQuoteContinuesOnNextLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Poppin'Party×Roselia 合同ライブ「DREAMS", new Rect(10, 10, 330, 24)),
            new("GO ON」のセットリストプレイリストを公開！", new Rect(10, 40, 290, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
    }

    /// <summary>
    /// Similar widths and nothing else is not evidence of a wrap.
    /// </summary>
    /// <remarks>
    /// The 1.25 line advance here is this file's boilerplate — y=10 over y=40 at height 24, the
    /// same rectangles the test fourteen lines above uses to assert the opposite verdict, where the
    /// only difference is an unclosed 「 in the text. It is NOT a measured figure for how far apart
    /// independent lines sit, and it must not be read as a ceiling for any threshold: the measured
    /// figure for stacked independent rows is a settings panel's list at 1.47 to 1.58 line heights.
    /// What this test guards is the rule, not the number — similar widths alone stay apart, while
    /// the set-solid rule asks for tight leading and a shared edge on top of them.
    /// </remarks>
    [Fact]
    public void KeepsAlignedIndependentLinesSeparateWhenWidthsAreSimilar()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家最新整理", new Rect(10, 40, 210, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsNextSentenceSeparateAfterSentenceTerminator()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("第一句已經結束。", new Rect(10, 10, 220, 24)),
            new("第二句重新開始", new Rect(10, 40, 110, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void AWordWithNoDescenderIsStillPartOfItsLine()
    {
        // Measured: "Let's pay CiRCLE a visit on the way home." came back as these two boxes, a
        // height ratio of 0.58, 2px apart, the shorter one entirely inside the taller one's rows.
        // Split, "home" was translated on its own and drawn as a stray word beside a sentence with
        // its ending missing.
        var blocks = new List<OcrTextBlock>
        {
            new("Let's pay CiRCLE a visit on the way", new Rect(80, 78, 888, 88)),
            new("home.", new Rect(970, 106, 141, 51)),
        };

        var merged = Assert.Single(GroupDetected(blocks));
        Assert.Equal("Let's pay CiRCLE a visit on the way home.", merged.Text);
    }

    [Fact]
    public void StackedControlsOfSimilarHeightAreStillKeptApart()
    {
        // What the height guard was protecting, and why loosening it is safe: these overlap
        // vertically by about half, which the overlap test rejects on its own.
        var blocks = new List<OcrTextBlock>
        {
            new("Download report", new Rect(10, 10, 200, 32)),
            new("Create key", new Rect(215, 27, 160, 38)),
        };

        Assert.Equal(2, GroupDetected(blocks).Count);
    }

    [Fact]
    public void AMergedLineIsAsConfidentAsItsTextDeserves()
    {
        // Weighted by characters, not by fragment: a confidently-read line with a doubtful two-
        // character fragment beside it is still a confidently-read line, and a plain average would
        // make the score depend on where the detector happened to split it.
        var blocks = new List<OcrTextBlock>
        {
            new("Marina-san, are you okay?", new Rect(10, 10, 250, 24), Confidence: 1.0),
            new("!!", new Rect(266, 10, 20, 24), Confidence: 0.6),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.NotNull(merged.Confidence);
        Assert.InRange(merged.Confidence!.Value, 0.96, 1.0);
    }

    [Fact]
    public void ALineWithNoScoresCarriesNoConfidence()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("第一段文字", new Rect(10, 10, 100, 24)),
            new("第二段文字", new Rect(112, 10, 100, 24)),
        };

        var merged = Assert.Single(GroupDetected(blocks));
        Assert.Null(merged.Confidence);
    }

    /// <summary>
    /// The real bounds of a game panel whose description wraps onto a second line. The detector's
    /// unclip expansion leaves the two boxes overlapping by 3px, and while a `verticalGap &lt; 0`
    /// guard stood there they were translated apart — "…remaining in the" came back as 「每發子彈在」
    /// and "magazine" as 「雜誌」. See issue #74.
    /// </summary>
    [Fact]
    public void GroupsAWrappedLineWhoseBoxesOverlapByTheDetectorsExpansion()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Shots deal more damage for each bullet remaining in the", new Rect(700, 280, 570, 34)),
            new("magazine", new Rect(698, 311, 109, 31)),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(
            "Shots deal more damage for each bullet remaining in the magazine", merged.Text);
    }

    /// <summary>
    /// The real shape of the MAG SHOT panel: "magazine" has no ascender, so its box came back 26px
    /// under the 30px line it wraps from — 0.867, refused by 0.013 — while the letters themselves
    /// are all but the same size. Comparing the glyph heights the engine reports instead reads
    /// 0.937 and the line survives. See issue #79.
    /// </summary>
    [Fact]
    public void GroupsAWrappedLineWhoseBoxIsShortForWantOfAnAscender()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Shots deal more damage for each bullet remaining in the",
                new Rect(111, 78, 566, 30), RenderGlyphHeight: 16),
            new("magazine", new Rect(112, 108, 105, 26), RenderGlyphHeight: 15),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(
            "Shots deal more damage for each bullet remaining in the magazine", merged.Text);
    }

    /// <summary>
    /// And the case that keeps the swap honest: boxes near enough in height to pass the old test,
    /// holding text of visibly different sizes. A save-slot stamp over a character name did this —
    /// boxes 0.83 apart, letters 0.58. Reading the glyphs is what tells them apart.
    /// </summary>
    /// <remarks>
    /// The widths are what make the glyph heights come out at the measured 20 and 11.7: the engine
    /// does not measure ink, it estimates it from the average glyph pitch, so a box has to be as
    /// wide as the text in it was. They were 300 and 90 while this fixture set the glyph heights by
    /// hand, and those boxes yield 16.96 and 11.70 — a ratio of 0.98, which is to say the pair the
    /// test is built on would have merged in the app whatever this assertion said.
    /// </remarks>
    [Fact]
    public void KeepsLinesOfDifferentTextSizesApartEvenWhenTheirBoxesMatch()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("2026/07/24 23:29 AUTO SAVE", new Rect(40, 40, 354, 35), RenderGlyphHeight: 20),
            new("Narmaya", new Rect(41, 79, 63, 29), RenderGlyphHeight: 11.7),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// A stack of menu entries has the shape the width rule looks for — a longer label above a
    /// shorter one, aligned, evenly spaced — without being a sentence at all. These are the real
    /// bounds of 「キャラクター強化」over「所持品」in a game menu; joining them handed the translator
    /// one invented phrase and squeezed both labels into a single bubble. See issue #75.
    /// </summary>
    [Fact]
    public void KeepsStackedMenuLabelsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("キャラクター強化", new Rect(166, 476, 233, 36)),
            new("所持品", new Rect(165, 539, 94, 38)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// And what that must not cost: a real wrapped sentence, whose first line is long because it
    /// ran out of room. Real bounds from the same corpus as the menu above.
    /// </summary>
    /// <remarks>
    /// The boxes are the detector's, which is not what this fixture used to hold: it carried the
    /// normalised ones, 30 and 29 tall against the same 51px step, and those put the pair 1.73 line
    /// advances apart. The capture itself reads 1.33 — the normalisation had taken a quarter off
    /// the height and left the step, so the leading came back looking half again as loose as it is.
    /// Re-measured on the capture: gap 0.39 of a line, advance 1.33, heights 0.89 apart, first line
    /// 2.27 times the width of the second.
    /// </remarks>
    [Fact]
    public void StillGroupsASentenceLongEnoughToHaveWrapped()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("그랑 사이퍼의 갑판에 있는 연습용 더미를 조사하면", new Rect(355, 429, 563, 32.9)),
            new("플레이할 수 있습니다", new Rect(355, 475.4, 248, 37)),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// What the tolerance above must not let through: two boxes sharing a row overlap almost
    /// completely, and joining them as though the second wrapped from the first would read a button
    /// as the continuation of its neighbour. Far enough apart that the same-line merge does not take
    /// them first, so this reaches the next-line test the way the real ones did.
    /// </summary>
    [Fact]
    public void KeepsBoxesThatShareARowOutOfTheNextLineJoin()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("PICK UP", new Rect(10, 360, 67, 25)),
            new("HOLD TO SALVAGE", new Rect(20, 361, 160, 25)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    private static List<OcrTextBlock> GroupDetected(IReadOnlyList<OcrTextBlock> blocks) =>
        OcrTextBlockGrouper.Group(blocks.AsDetected(), GroupingProfile.Interface);

    /// <summary>
    /// Symptom B: two lines whose detection boxes are exactly the same size, one Latin and one CJK.
    /// </summary>
    /// <remarks>
    /// The size test used to compare normalised boxes, and a CJK one is pulled in to 0.82 of its
    /// detection box while a Latin one is left whole — so this pair read 0.820 against a 0.88 gate
    /// and was refused before anything about the text, the spacing or the alignment was consulted.
    /// No mixed-script pair could ever pass it.
    /// </remarks>
    [Fact]
    public void CrossScriptPair_WithEqualDetectionBoxes_IsNotRefusedBySizeTest()
    {
        var latin = new OcrTextBlock("OPTIONS", new Rect(10, 10, 240, 30)).AsDetected();
        var cjk = new OcrTextBlock("ゲーム設定", new Rect(10, 10, 240, 30)).AsDetected();

        Assert.Equal(OcrLayoutScript.Latin, latin.LayoutScript);
        Assert.Equal(OcrLayoutScript.Cjk, cjk.LayoutScript);
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(latin, cjk), precision: 9);

        // What it used to be: normalisation makes the same box 0.82 as tall on the CJK side.
        var normalizedCjk = OnnxOcrEngine.NormalizeBlocks([new("ゲーム設定", new Rect(10, 10, 240, 30))], useCjkRenderMetrics: true)[0];
        Assert.True(normalizedCjk.Bounds.Height / latin.Bounds.Height < 0.88);
    }

    /// <summary>
    /// A block that is itself both scripts has no single glyph body to measure, so it compares on
    /// the detection box like any other cross-script pairing.
    /// </summary>
    [Fact]
    public void MixedScriptBlock_UsesLayoutBoundsForSizeComparison()
    {
        var mixed = new OcrTextBlock("甲Glossaries", new Rect(10, 10, 240, 30)).AsDetected();
        var latin = new OcrTextBlock("Glossaries", new Rect(10, 50, 200, 30)).AsDetected();

        Assert.Equal(OcrLayoutScript.Mixed, mixed.LayoutScript);
        Assert.Null(mixed.LayoutGlyphHeight);
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(mixed, latin), precision: 9);
    }

    /// <summary>
    /// The Latin-dominant case the same rule covers: a line of Japanese prose carrying Western
    /// terms must not be sized as though every glyph in it were full-width.
    /// </summary>
    [Fact]
    public void MixedScriptBlock_DoesNotUseCjkGlyphMetricForLatinDominantText()
    {
        var box = new Rect(10, 10, 600, 30);
        var mixed = new OcrTextBlock(
            "2005 年から CSS, HTML, JavaScript のドキュメントを作成しています。", box).AsDetected();
        var cjk = new OcrTextBlock("技術文書を書いています", box).AsDetected();

        Assert.Null(mixed.LayoutGlyphHeight);
        Assert.NotNull(cjk.LayoutGlyphHeight);
        Assert.NotEqual(cjk.LayoutGlyphHeight, mixed.LayoutGlyphHeight);

        // Same box, so the comparison is 1.0 — not the CJK glyph estimate measured off one of them.
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(mixed, cjk), precision: 9);
    }

    /// <summary>
    /// 「OPTIONS／ゲーム設定」and 「Back／戻る」: each label is joined to its own translation
    /// candidate line, and the two pairs stay apart from each other.
    /// </summary>
    [Fact]
    public void Mixed_EnglishJapanese_DoesNotCrossMergeParagraphs()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("OPTIONS", new Rect(10, 10, 240, 30)),
            new("ゲーム設定", new Rect(10, 190, 240, 30)),
            new("Back", new Rect(10, 370, 140, 30)),
            new("戻る", new Rect(10, 550, 140, 30)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(4, grouped.Count);
    }

    /// <summary>
    /// The row rule measures on the detector's box, and this pair is where that changes the answer.
    /// </summary>
    /// <remarks>
    /// Two CJK words 16px apart, in boxes the engine has normalised from 40px down to 32.8. Against
    /// the detection height the space is 0.40 of a line and they are one line of text; against the
    /// normalised height it is 0.49 and they are two things side by side. Reading Bounds here would
    /// put the same picture on either side of the threshold depending on the source language the
    /// user picked, which is what the whole split of the metrics exists to stop.
    /// </remarks>
    [Fact]
    public void SameLineGap_IsMeasuredOnLayoutBounds_NotTheNormalisedBox()
    {
        OcrTextBlock Normalised(string text, double x) =>
            new OcrTextBlock(text, new Rect(x, 13.6, 40, 32.8))
            {
                LayoutBounds = new Rect(x, 10, 40, 40),
                LayoutScript = OcrLayoutScript.Cjk,
            };

        var previous = Normalised("今日", 10);
        var current = Normalised("天気", 66);

        Assert.Equal(16, current.LayoutBounds.X - previous.LayoutBounds.Right);
        Assert.True(16 / 40.0 < SameLineGapThreshold.Fallback);
        Assert.True(16 / 32.8 > SameLineGapThreshold.Fallback);

        var grouped = OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface);

        Assert.Equal("今日天気", Assert.Single(grouped).Text);
    }

    /// <summary>
    /// And the capture that says its own spacing: eight menu entries set well apart, with one pair
    /// of them closer than the fixed fallback would have allowed.
    /// </summary>
    [Fact]
    public void SameLineGaps_AreJudgedAgainstTheSpacingOfThisCapture()
    {
        // Word spaces of 0.1 of a line and item spaces of 0.6, which the estimator splits between.
        var boxes = new List<OcrTextBlock>();
        double x = 0;
        foreach (var (text, gap) in new (string, double)[]
        {
            ("Alpha", 0), ("beta", 4), ("Gamma", 24), ("delta", 4),
            ("Epsilon", 24), ("zeta", 4), ("Eta", 24), ("theta", 4),
        })
        {
            x += gap;
            boxes.Add(new OcrTextBlock(text, new Rect(x, 10, 60, 40)).AsDetected());
            x += 60;
        }

        var grouped = OcrTextBlockGrouper.Group(boxes, GroupingProfile.Interface);

        Assert.Equal(
            ["Alpha beta", "Gamma delta", "Epsilon zeta", "Eta theta"],
            grouped.Select(block => block.Text));
    }

    /// <summary>
    /// A checkbox entry is not the last line of the entry above it, however much shorter it is.
    /// </summary>
    /// <remarks>
    /// <para>Measured from region-panel-en, the corpus set that exists to catch exactly this: its
    /// entries were being joined on the width rule alone, while every pair in the corpus that
    /// really is one sentence wrapping stops at 1.33.</para>
    ///
    /// <para>The rectangles are the capture's own, scaled to whole numbers — blocks 17 and 18 of
    /// <c>game-menu-en.png</c> read <c>(84,378,307,18)</c> and <c>(83,406,202,20)</c>, which is 1.47
    /// line heights apart. The figure matters now in a way it did not when this was written: the
    /// general mode lets a wrapped line reach 1.45, so this pair clears it by 0.02 — the narrowest
    /// margin anywhere in that rule. It stays apart here because this runs on the interface profile,
    /// which does not take the relaxation, and that is the point of the profile.</para>
    /// </remarks>
    [Fact]
    public void AShorterLineSetTooFarBelow_IsNotTheEndOfTheParagraph()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Reveal all rooms before proceeding to next floor", new Rect(100, 100, 520, 30)),
            new("Allow automatic pomander use", new Rect(100, 144, 320, 30)),
        };

        // 44 / 30 = 1.47 line advances, past what a paragraph's own leading reaches.
        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// The leading is measured on the detector's box, and this pair is where that changes the
    /// answer.
    /// </summary>
    /// <remarks>
    /// A CJK pair stepped 44px apart in boxes the engine normalised from 34 down to 27.9. On the
    /// detection height that is 1.29 line advances and the second line ends the paragraph; on the
    /// normalised height it is 1.58 and it does not. Which one the rule reads decides the answer,
    /// and only one of them is the same on every source language.
    /// </remarks>
    [Fact]
    public void WrappedFinalLineLeading_IsMeasuredOnLayoutBounds_NotTheNormalisedBox()
    {
        OcrTextBlock Normalised(string text, double y, double width) =>
            new OcrTextBlock(text, new Rect(100, y + 3.05, width, 27.9))
            {
                LayoutBounds = new Rect(100, y, width, 34),
                LayoutScript = OcrLayoutScript.Cjk,
            };

        var previous = Normalised("彾匣內每發子彈的傷害會增加而且", 100, 460);
        var current = Normalised("更容易觸發", 144, 160);

        Assert.Equal(44 / 34.0, LineAdvanceOf(previous, current), precision: 6);
        Assert.True(44 / 34.0 < 1.38);
        Assert.True(44 / 27.9 > 1.38);

        var merged = Assert.Single(OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface));
        Assert.Equal(2, merged.Lines.Count);
    }

    private static double LineAdvanceOf(OcrTextBlock previous, OcrTextBlock current) =>
        (current.LayoutBounds.Y - previous.LayoutBounds.Y) /
        ((previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0);

    /// <summary>
    /// Centred speech is no longer refused as misaligned because its left edges disagree.
    /// </summary>
    /// <remarks>
    /// The real boxes from comic-en.png: "WHY ARE" centred over "YOU PICKING ON", 1.49 line heights
    /// apart on the left and 0.03 on the centre. Twenty-one of the ten comic pages' fifty-five
    /// verdicts were refused on the left edge like this one.
    /// </remarks>
    [Fact]
    public void CentredLines_AreNoLongerRefusedByTheAlignmentGate()
    {
        var previous = Line("WHY ARE", x: 320, y: 305, width: 201, height: 52);
        var current = Line("YOU PICKING ON", x: 244, y: 358, width: 356, height: 50);

        var verdict = NextLineVerdict(previous, current);

        Assert.NotEqual("alignment", verdict.Rule);
        Assert.True(verdict.LeftDelta > 1.2, $"left delta {verdict.LeftDelta:0.00} should be over the gate");
        Assert.Equal(verdict.CenterDelta, verdict.AlignmentDelta, precision: 6);
    }

    /// <summary>
    /// Body text set flush right is no longer refused because its left edges disagree either.
    /// </summary>
    /// <remarks>
    /// The stat panels in the comic corpus set their body flush right, so consecutive lines of one
    /// sentence read 6.35 line heights apart on the left and 0.00 on the right. Dropping the right
    /// edge from the comparison — which was considered — would leave those pairs permanently apart,
    /// and the hand-marked grouping says they belong together.
    /// </remarks>
    [Fact]
    public void FlushRightLines_AreNoLongerRefusedByTheAlignmentGate()
    {
        var previous = Line("EXPEDITION'S REAR, CONTRIBUTED TO", x: 100, y: 10, width: 600, height: 40);
        var current = Line("SLAYING BLUE MANE", x: 340, y: 56, width: 360, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.NotEqual("alignment", verdict.Rule);
        Assert.Equal(0, verdict.AlignmentDelta, precision: 6);
        Assert.True(verdict.LeftDelta > 1.2, $"left delta {verdict.LeftDelta:0.00} should be over the gate");
    }

    /// <summary>
    /// A short label over a body of text is still refused, because it is out of line on every edge.
    /// </summary>
    /// <remarks>
    /// This is what the alignment gate is really for, and the reason taking the smallest of three
    /// deltas is safe: the label/body pairs in the corpus read 3.18 / 6.33 / 9.49 line heights, so
    /// the smallest of them is still far outside a gate set at 1.2. Refusing on the left edge alone
    /// was never what kept them apart.
    /// </remarks>
    [Fact]
    public void ALabelOverABodyOfText_IsStillRefusedByTheAlignmentGate()
    {
        var previous = Line("MERIT", x: 100, y: 10, width: 120, height: 40);
        var current = Line("SERVED AS THE EXPEDITION'S", x: 300, y: 56, width: 400, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("alignment", verdict.Rule);
        Assert.False(verdict.Joined);
    }

    /// <summary>
    /// A line opening with a bullet is not a continuation, however it is spaced.
    /// </summary>
    /// <remarks>
    /// The spacing a news portal uses between headlines in a list is the spacing it uses inside a
    /// paragraph, so this pair is set as tightly as a real wrap and every geometric test passes it.
    /// Only the mark says otherwise.
    /// </remarks>
    [Fact]
    public void ALineOpeningWithABullet_IsNeverAContinuation()
    {
        var previous = Line("Government announces new transport policy", x: 100, y: 10, width: 600, height: 30);
        var current = Line("· Opposition responds within the hour", x: 100, y: 47, width: 520, height: 30);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("list bullet", verdict.Rule);
        Assert.False(verdict.Joined);
        Assert.True(verdict.LineAdvance < 1.38, $"advance {verdict.LineAdvance:0.00} is inside the wrap limit");
    }

    /// <summary>
    /// A colon joins the line below only when the two are set as closely as a wrapped sentence is.
    /// </summary>
    /// <remarks>
    /// The colon is read as a clause carrying on, which is one of its two meanings. The other is a
    /// form label over its value, and the corpus has one: a settings dialog's "Column type:" over
    /// "Standard", 1.79 line heights apart, joined into a single string for the translator.
    /// </remarks>
    [Fact]
    public void ALineEndingOnAColon_ContinuesOnlyWhenTheNextLineIsSetCloseUnderIt()
    {
        var label = Line("Column type:", x: 100, y: 10, width: 200, height: 30);
        var farBelow = Line("Standard", x: 100, y: 64, width: 160, height: 30);
        var setClose = Line("Standard", x: 100, y: 44, width: 160, height: 30);

        var refused = NextLineVerdict(label, farBelow);
        Assert.Equal("label colon", refused.Rule);
        Assert.False(refused.Joined);
        Assert.True(refused.LineAdvance > 1.38, $"advance {refused.LineAdvance:0.00} should be past the limit");

        var joined = NextLineVerdict(label, setClose);
        Assert.Equal("punctuation", joined.Rule);
        Assert.True(joined.Joined);
    }

    /// <summary>
    /// Lines in the middle of a paragraph join, where the width reading could never see them.
    /// </summary>
    /// <remarks>
    /// Every line inside a paragraph is about as wide as the one above it, and the rule that came
    /// before this one took similar widths as proof that nothing had wrapped. Thirteen refusals
    /// across the ten comic pages read that way, on pairs whose widths were within a fifth of each
    /// other. Here the widths are equal on purpose: if this test passes, it is not the shape test
    /// that passed it.
    /// </remarks>
    [Fact]
    public void ParagraphMiddleLines_OfEqualWidth_JoinOnTheSettingAlone()
    {
        var previous = Line("CHANCELLOR ARRANGED THE REWARDS", x: 100, y: 10, width: 500, height: 40);
        var current = Line("ACCORDING TO THE EXPLORERS MERITS", x: 100, y: 50, width: 500, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("set solid", verdict.Rule);
        Assert.True(verdict.Joined);
        Assert.Equal(1.00, verdict.WidthRatio, precision: 2);
    }

    /// <summary>
    /// Set solid needs the two lines to share an edge far more closely than the ordinary gate asks.
    /// </summary>
    /// <remarks>
    /// This is the one test standing between a stat panel's label and the body under it now that
    /// the width reading is no longer consulted. The pair here is 0.6 line heights out of line:
    /// inside the ordinary alignment gate at 1.2, and well outside the 0.35 this rule asks for. It
    /// must fall through to the older evidence rule and be refused there.
    /// </remarks>
    [Fact]
    public void LinesAtOneLeadingButOutOfLine_AreNotSetSolid()
    {
        var previous = Line("CHANCELLOR ARRANGED THE REWARDS", x: 100, y: 10, width: 500, height: 40);
        var current = Line("ACCORDING TO THE EXPLORERS MERITS", x: 124, y: 50, width: 500, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("no continuation evidence", verdict.Rule);
        Assert.False(verdict.Joined);
        Assert.InRange(verdict.AlignmentDelta, 0.35, 1.2);
    }

    /// <summary>
    /// A list set looser than text is set solid is still a list.
    /// </summary>
    /// <remarks>
    /// The settings panel's checkbox entries are the population this has to keep out: flush left,
    /// one text size, similar widths, and separated by nothing but their leading — 1.47 to 1.58
    /// line heights, against the 1.26 asked for here.
    /// </remarks>
    [Fact]
    public void ListEntriesSetLooserThanTheSolidLimit_AreNotSetSolid()
    {
        var previous = Line("Automatically navigate to coffers", x: 100, y: 10, width: 500, height: 40);
        var current = Line("Prioritize opening coffers over cairns", x: 100, y: 69, width: 500, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("no continuation evidence", verdict.Rule);
        Assert.True(verdict.LineAdvance > 1.40, $"advance {verdict.LineAdvance:0.00} should be past the solid limit");
    }

    /// <summary>
    /// A line too short to have run out of room is not set solid under the standard profile.
    /// </summary>
    /// <remarks>
    /// A speech bubble opens on one or two words, which is this exact shape and is not this case —
    /// waiving the length test is what the comic profile is for, and it is not waived here. Written
    /// as a test now so that the step which flips that flag has something that changes.
    /// </remarks>
    [Fact]
    public void AShortLineSetSolid_IsNotJoinedUnderTheStandardProfile()
    {
        var previous = Line("WHY ARE", x: 100, y: 10, width: 120, height: 40);
        var current = Line("YOU PICKING ON THE GOBLIN", x: 100, y: 50, width: 400, height: 40);

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface, decisions);
        var verdict = Assert.Single(decisions, decision => decision.Kind == "next");

        Assert.NotEqual("set solid", verdict.Rule);
        Assert.False(verdict.Joined);
    }

    /// <summary>
    /// A line continues the column it belongs to, not whichever group happened to open last.
    /// </summary>
    /// <remarks>
    /// The shape from the Japanese event page: a title wrapping onto a second line, with a heading
    /// from the next column sorted between the two halves because it starts a few pixels lower.
    /// Before every open group was asked, this pair was not refused — it was never put.
    /// </remarks>
    [Fact]
    public void AWrappedTitle_JoinsAcrossALineFromAnotherColumn()
    {
        var titleTop = Line("TVアニメ「バンドリ」放送記念フリーライブ", x: 1340, y: 612, width: 449, height: 32);
        var otherColumn = Line("RoseliaのRADIO SHOUT! -Prost-", x: 1912, y: 615, width: 366, height: 27);
        var titleBottom = Line("「新宿着陸計画」DAY2 チケット受付中", x: 1339, y: 650, width: 405, height: 33);

        var grouped = OcrTextBlockGrouper.Group(
            [titleTop, otherColumn, titleBottom], GroupingProfile.Interface);

        Assert.Equal(2, grouped.Count);
        Assert.Contains(grouped, block => block.Lines.Count == 2);
        Assert.Contains(grouped, block => block.Text == "RoseliaのRADIO SHOUT! -Prost-");
    }

    /// <summary>
    /// A third line standing in the column between two others stops them being neighbours.
    /// </summary>
    /// <remarks>
    /// The failure this guards is a news front page: headline, a small kicker, next headline, one
    /// column, all aligned. With every open group asked, the two headlines are within reach of each
    /// other and read as a plausible continuation; the kicker between them is what says they are
    /// not.
    /// </remarks>
    [Fact]
    public void ALineStandingBetweenTwoOthers_StopsThemBeingContinuations()
    {
        // The line in the middle is a small one — a kicker set over the headline under it. It has
        // to be small: two full-height lines cannot have a third of their own height between them
        // and still be within reach of each other, so the case this rule exists for is always an
        // intervening line shorter than the gap it sits in.
        var headline = Line("Council approves the new transport plan", x: 100, y: 0, width: 600, height: 40);
        var kicker = Line("TRANSPORT", x: 100, y: 42, width: 160, height: 12);
        var nextHeadline = Line("Ferry services resume", x: 100, y: 54, width: 400, height: 40);

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group(
            [headline, kicker, nextHeadline], GroupingProfile.Interface, decisions);

        // The pair that skips the standfirst is never judged at all: it is refused before any rule
        // is asked, which is what "nothing lies between" means.
        Assert.DoesNotContain(
            decisions,
            decision => decision.Kind == "next" &&
                        decision.Previous == headline.Text &&
                        decision.Current == nextHeadline.Text);
    }

    /// <summary>
    /// The same three lines, with the middle one moved out of the column, do reach each other.
    /// </summary>
    /// <remarks>
    /// The positive half of the pair above. Without it, the test above would still pass if the rule
    /// refused every distant pair for some other reason — say a reach filter set too tight — and
    /// nobody would know the column test was doing nothing.
    /// </remarks>
    [Fact]
    public void ALineBesideTheColumn_DoesNotStopTwoLinesBeingContinuations()
    {
        // The same three boxes as the test above, with the middle one moved out of the column.
        var first = Line("Council approves the new transport plan", x: 100, y: 0, width: 600, height: 40);
        var beside = Line("TRANSPORT", x: 900, y: 42, width: 160, height: 12);
        var second = Line("after a long delay", x: 100, y: 54, width: 400, height: 40);

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        var grouped = OcrTextBlockGrouper.Group(
            [first, beside, second], GroupingProfile.Interface, decisions);

        Assert.Contains(
            decisions,
            decision => decision.Kind == "next" &&
                        decision.Previous == first.Text &&
                        decision.Current == second.Text);
        Assert.Equal(2, grouped.Count);
        Assert.Contains(grouped, block => block.Lines.Count == 2);
    }

    /// <summary>
    /// A row of figures under a label is a row, however closely it is set.
    /// </summary>
    /// <remarks>
    /// The game character card from the corpus: a name and level over a hit-point count, set a line
    /// apart and sharing a right edge. Every geometric test passes it — the advance here is 1.00 —
    /// so only the content of the second line can say what it is.
    /// </remarks>
    [Fact]
    public void ALineOfNothingButFigures_IsNotAContinuation()
    {
        var previous = Line("Lvl 100 M. Lvl 50 Narmaya", x: 100, y: 10, width: 500, height: 40);
        var current = Line("57687 199730/199730", x: 200, y: 50, width: 400, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.Equal("numeric row", verdict.Rule);
        Assert.False(verdict.Joined);
        Assert.True(verdict.LineAdvance <= 1.20, $"advance {verdict.LineAdvance:0.00} is inside the solid limit");
    }

    /// <summary>
    /// A sentence that carries on with a figure in it is not a row of figures.
    /// </summary>
    /// <remarks>
    /// The reason the test above asks for no letters rather than for a leading digit. A release note
    /// wrapping onto "1.81 stabilizes the Error trait…" opens on a number and is still prose; the
    /// rule must not read the first character and stop.
    /// </remarks>
    [Fact]
    public void ASentenceContinuingOnAFigure_IsStillAContinuation()
    {
        var previous = Line("The Rust team is happy to announce a new version", x: 100, y: 10, width: 500, height: 40);
        var current = Line("1.81 stabilizes the Error trait in core", x: 100, y: 50, width: 500, height: 40);

        var verdict = NextLineVerdict(previous, current);

        Assert.NotEqual("numeric row", verdict.Rule);
        Assert.True(verdict.Joined);
    }

    /// <summary>
    /// The comic mode joins a bubble's short opening line to the line under it; the standard mode
    /// does not.
    /// </summary>
    /// <remarks>
    /// The real boxes from comic-en.png. "WHY ARE" is 201px over a 52px line — under four times its
    /// own height, so no length test can call it a wrap — and the line under it is set solid at
    /// 1.04 line advances with their centres a hundredth of a line apart. Everything except the
    /// length is already evidence; the mode is what says the length does not apply here.
    /// </remarks>
    [Fact]
    public void AShortBubbleOpening_JoinsInComicMode_AndNotInStandard()
    {
        var previous = Line("WHY ARE", x: 320, y: 305, width: 201, height: 52);
        var current = Line("YOU PICKING ON", x: 244, y: 358, width: 356, height: 50);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface).Count);

        var comic = OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General);
        Assert.Single(comic);
        Assert.Equal("WHY ARE YOU PICKING ON", comic[0].Text);
    }

    /// <summary>
    /// The comic mode waives the length test and nothing else: the geometry still has to hold.
    /// </summary>
    /// <remarks>
    /// The boundary the modes are allowed to move, stated as a test. A short line over one that is
    /// out of line with it — 0.6 line heights, inside the ordinary alignment gate and outside the
    /// set-solid one — must stay apart in both modes. If this ever passes in comic mode, a mode has
    /// started relaxing geometry rather than evidence.
    /// </remarks>
    [Fact]
    public void ComicMode_DoesNotWaiveTheSetSolidGeometry()
    {
        var previous = Line("WHY ARE", x: 320, y: 305, width: 201, height: 52);
        var current = Line("YOU PICKING ON", x: 275, y: 358, width: 356, height: 50);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General).Count);
    }

    /// <summary>
    /// Hand lettering's uneven size is accepted in the general mode, on the set-solid path only.
    /// </summary>
    /// <remarks>
    /// The pair from comic-en10.png, real geometry and real glyph heights: one sentence across two
    /// lines of the same balloon, whose glyphs measure 0.83 of each other because they were drawn
    /// by hand. The ordinary gate asks 0.88 and refuses it. The two lines are set solid — one
    /// leading, one edge — and the first is centred inside the second, which together are the only
    /// shape a mode may relax.
    /// </remarks>
    [Fact]
    public void UnevenHandLettering_JoinsInTheGeneralMode_OnTheSetSolidPath()
    {
        var (previous, current) = CentredBalloonPair();

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface).Count);
        Assert.Single(OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General));
    }

    /// <summary>
    /// The waiver reaches centred lines only. A stack of labels aligned down one edge is set solid
    /// by every other measure, and is refused.
    /// </summary>
    /// <remarks>
    /// This is what the waiver was costing before it had this condition: measured over the seven
    /// non-comic image sets it joined 75 such pairs, and the wrong ones are all this shape — a game
    /// panel's control labels, a HUD's readouts, stacked flush left. Sharing an edge is precisely
    /// what the set-solid geometry is looking for, so nothing earlier in the chain can tell them
    /// from a balloon; being centred is what a balloon does and a stack cannot.
    /// </remarks>
    [Fact]
    public void AFlushLeftStack_IsRefusedEvenInTheGeneralMode()
    {
        // The same leading, the same edge and the same glyph heights as the balloon above — only
        // the centring is gone, because the second line starts where the first one does.
        var previous = LineWithGlyphHeight("Move Camera", x: 305, y: 1045, width: 437, height: 66, glyph: 51.65);
        var current = LineWithGlyphHeight("Open Map (Hold)", x: 305, y: 1104, width: 529, height: 62, glyph: 42.98);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface).Count);
        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General).Count);
    }

    /// <summary>
    /// The centring is read whichever line is the narrower, because a balloon's lines take turns
    /// being the wide one.
    /// </summary>
    /// <remarks>
    /// <para>comic-en2.png, real boxes and real glyph heights: "YOU THINK THAT'D" over "HURT ME?",
    /// the wide line first. Only the second line is inset here, so a test that asks whether the
    /// FIRST line sits inside the second — the shape a balloon's opening line has, and the shape
    /// design.md §15.3 names — refuses it.</para>
    ///
    /// <para>This is the difference measured rather than argued: over the comic corpus, testing one
    /// direction keeps 12 of the 21 joins the waiver makes and testing the narrower line keeps 19,
    /// and the nine it loses are all balloon interiors like this one. Neither version admits the
    /// flush-left stacks above, which is what the condition is for.</para>
    /// </remarks>
    [Fact]
    public void ABalloonInteriorWithTheWideLineFirst_IsStillRead()
    {
        var previous = LineWithGlyphHeight("YOU THINK THAT'D", x: 204, y: 580, width: 429, height: 57, glyph: 39.84);
        var current = LineWithGlyphHeight("HURT ME?", x: 293, y: 634, width: 248, height: 60, glyph: 46.06);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface).Count);
        Assert.Single(OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General));
    }

    /// <summary>
    /// Two lines of one balloon that happen to be the same width are read on their centres, which
    /// is the one reading that survives their being the same width.
    /// </summary>
    /// <remarks>
    /// <para>The pair from comic-en.png, and what the inset reading cannot see. A letterer breaks a
    /// balloon to about one measure, so these two run past each other by nothing at all — 0.07 of a
    /// line height at their widest disagreement, flush as far as an inset can tell — and for a
    /// while that was recorded here as the accepted cost of deciding this on centring.</para>
    ///
    /// <para>It is not a cost, it is the inset going blind at the point the setting is most
    /// balanced: their centres are 0.05 of a line apart, which is the same claim the inset was
    /// standing in for. Seven pairs across the two comic sets sat in this gap.</para>
    /// </remarks>
    [Fact]
    public void TwoBalloonLinesOfEqualWidth_AreReadOnTheirCentres()
    {
        var previous = LineWithGlyphHeight("THAT GUY'S FAULT", x: 368, y: 725, width: 417, height: 58, glyph: 36.0);
        var current = LineWithGlyphHeight("YOU ENDED UP IN", x: 372, y: 780, width: 415, height: 58, glyph: 31.0);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface).Count);
        Assert.Single(OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General));
    }

    /// <summary>
    /// Two short readouts stacked in a column share a centre too, and are refused on their length.
    /// </summary>
    /// <remarks>
    /// <para>The real boxes of a driver overlay's 「RTX」over「VSR」, from the Korean subtitle
    /// captures — nine of them carry this pair. Their centres land on the same pixel, so reading
    /// the centres alone joins them, and the balloon above shows why the centres have to be read.
    /// What separates the two is that a balloon's lines are short because the balloon is narrow: the
    /// text as a whole still runs to the length that makes a line wrap, 14.3 line heights here
    /// against these two's 3.3.</para>
    ///
    /// <para>Without this guard the centring test moved 47 pair verdicts across the non-comic
    /// captures instead of five, the great majority of them wrong and this shape the commonest.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoShortReadoutsOnOneCentre_AreRefusedOnTheirLength()
    {
        var previous = LineWithGlyphHeight("RTX", x: 1826, y: 31, width: 36, height: 21, glyph: 10.5);
        var current = LineWithGlyphHeight("VSR", x: 1824, y: 47, width: 40, height: 25, glyph: 12.5);

        Assert.Equal(2, OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General).Count);
    }

    /// <summary>
    /// comic-en10.png: "A BARBARIAN A" centred over "HEREDITARY TITLE!", real boxes and real glyph
    /// heights. Shared with the vertical tests, which use it to establish that the two profiles
    /// really do disagree about something before asking whether vertical text hears them.
    /// </summary>
    internal static (OcrTextBlock Previous, OcrTextBlock Current) CentredBalloonPair() =>
        (LineWithGlyphHeight("A BARBARIAN A", x: 305, y: 1045, width: 437, height: 66, glyph: 51.65),
         LineWithGlyphHeight("HEREDITARY TITLE!", x: 263, y: 1104, width: 529, height: 62, glyph: 42.98));

    /// <summary>
    /// The relaxed size ratio reaches only pairs that are already set solid.
    /// </summary>
    /// <remarks>
    /// The same two lines, moved out of line with each other by 0.6 line heights — inside the
    /// ordinary alignment gate, outside the set-solid one. Comic mode must refuse this on the
    /// ordinary size ratio, because the pair never gets onto the path the mode is allowed to relax.
    /// This is the guard on "a profile lowers what it asks of set-solid lines, and nothing else".
    /// </remarks>
    [Fact]
    public void TheRelaxedSizeRatio_DoesNotReachPairsThatAreNotSetSolid()
    {
        var previous = LineWithGlyphHeight("THAT GUY'S FAULT", x: 368, y: 725, width: 417, height: 58, glyph: 36.0);
        var current = LineWithGlyphHeight("YOU ENDED UP IN", x: 407, y: 780, width: 415, height: 58, glyph: 31.0);

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General, decisions);
        var verdict = Assert.Single(decisions, decision => decision.Kind == "next");

        Assert.Equal("text size", verdict.Rule);
        Assert.InRange(verdict.AlignmentDelta, 0.35, 1.2);
    }

    /// <summary>
    /// A paragraph seam set looser than one line advance still joins, when the line above it was
    /// long enough to have wrapped.
    /// </summary>
    /// <remarks>
    /// The shape the relaxed limit exists for, and the reason the old one could not reach it: a
    /// paragraph's middle lines are equal in width, so the wrap-shape rule reads them as proof that
    /// nothing wrapped, and at 1.30 line heights they were past the set-solid limit as well. Six
    /// annotated web pages put 24 of these against 31 refusals, every one of the refusals wrong.
    /// The advance here is 1.30, inside the band between a paragraph's 1.45 ceiling and a list's
    /// 1.68 floor.
    /// </remarks>
    [Fact]
    public void AParagraphSeamPastOneLine_JoinsWhenTheLineAboveCouldHaveWrapped()
    {
        var previous = Line("open to global upstream and downstream partners", x: 100, y: 10, width: 500, height: 40);
        var current = Line("in OCR and document intelligence. The alliance", x: 100, y: 62, width: 500, height: 40);

        var verdict = GeneralVerdict(previous, current);

        Assert.Equal("set solid", verdict.Rule);
        Assert.True(verdict.Joined);
        Assert.Equal(1.30, verdict.LineAdvance, precision: 2);
    }

    /// <summary>
    /// A game menu's entries do not join at the leading a paragraph is allowed, because the entry
    /// above is too short to have run out of room.
    /// </summary>
    /// <remarks>
    /// <para>This is the pair that separates the two candidates measured for this rule, and the
    /// whole reason the relaxed limit is gated on the length test instead of replacing the
    /// constant. Moving the constant itself joined six of these across the corpus - "Continue" over
    /// "Game Options", "Game Options" over "Link Cygames ID", 「オプション」over 「Cygames ID連携」,
    /// a stat row over "Wish List", and two of a Japanese portal's shortcut labels - and the general
    /// mode is the default, so that is what a user gets when they capture a game menu.</para>
    ///
    /// <para><b>The geometry is the capture's own</b>, because the reason this pair reaches the
    /// leading test at all is subtle and a hand-written rectangle gets it wrong. The entries are
    /// centred, not flush left, so the general mode's waiver lets a short line be set solid at all -
    /// and once it does, the leading limit is the only thing left between them. Written flush left
    /// instead, the pair is refused by the length test whatever the limit says, and the test then
    /// passes for both candidates while appearing to tell them apart.</para>
    /// </remarks>
    [Fact]
    public void AMenuEntryStack_DoesNotJoinAtTheLeadingAParagraphIsAllowed()
    {
        // screen-panel-en-ja/OverTranslate_20260816_010258506.png, blocks 4 and 5.
        var previous = Line("Continue", x: 885, y: 762, width: 114, height: 39);
        var current = Line("Game Options", x: 856, y: 811, width: 172, height: 37);

        var verdict = GeneralVerdict(previous, current);

        Assert.NotEqual("set solid", verdict.Rule);
        Assert.False(verdict.Joined);
        Assert.Equal(1.29, verdict.LineAdvance, precision: 2);
        Assert.InRange(verdict.AlignmentDelta, 0, 0.35);
    }

    /// <summary>
    /// A short line above is still judged on the unrelaxed limit, on the one path that lets a short
    /// line be set solid at all.
    /// </summary>
    /// <remarks>
    /// <para>The other half of the branch. A short line above is refused outright under the
    /// interface profile, which does not waive the length test, so the limit it would have been
    /// judged on is never consulted there. The general mode waives that test for a narrower line
    /// centred inside a wider one - a speech bubble's opening words - and that is where a short
    /// line's limit becomes observable.</para>
    ///
    /// <para>Both pairs here are that shape. The first is 1.18 line heights apart and joins as it
    /// did before this rule existed; the second is 1.30, which the relaxed limit would admit and
    /// the unrelaxed one refuses. Without this, the branch could be rewritten to hand every pair
    /// the relaxed limit and only the menu test would notice.</para>
    /// </remarks>
    [Fact]
    public void AShortCentredLineAbove_IsStillJudgedOnTheUnrelaxedLimit()
    {
        var inside = GeneralVerdict(
            Line("WHY ARE", x: 200, y: 10, width: 120, height: 40),
            Line("YOU PICKING ON THE GOBLIN", x: 100, y: 57, width: 320, height: 40));
        var past = GeneralVerdict(
            Line("WHY ARE", x: 200, y: 10, width: 120, height: 40),
            Line("YOU PICKING ON THE GOBLIN", x: 100, y: 62, width: 320, height: 40));

        Assert.Equal(1.18, inside.LineAdvance, precision: 2);
        Assert.Equal("set solid", inside.Rule);
        Assert.True(inside.Joined);

        Assert.Equal(1.30, past.LineAdvance, precision: 2);
        Assert.NotEqual("set solid", past.Rule);
        Assert.False(past.Joined);
    }

    /// <summary>
    /// The same paragraph seam joins in the general mode and stays apart in the interface mode.
    /// </summary>
    /// <remarks>
    /// <para>This is the trade written as a test. The relaxed leading buys prose — six annotated web
    /// pages go from 140 groups to 113 — and charges 16 wrong joins for it, news headlines and wiki
    /// timeline entries strung together. That was accepted on the grounds that a user who meets
    /// those can switch to the interface mode, so the interface mode has to actually be an escape:
    /// measured with the relaxation applied to every mode, 15 of those 16 happened under it too,
    /// which is the argument failing rather than a number needing a nudge.</para>
    ///
    /// <para>One pair, 1.30 line heights apart, judged twice. Nothing about the geometry differs
    /// between the two calls — only which profile is asked.</para>
    /// </remarks>
    [Fact]
    public void AParagraphSeam_JoinsInTheGeneralMode_AndStaysApartInTheInterfaceMode()
    {
        var previous = Line("open to global upstream and downstream partners", x: 100, y: 10, width: 500, height: 40);
        var current = Line("in OCR and document intelligence. The alliance", x: 100, y: 62, width: 500, height: 40);

        var general = GeneralVerdict(previous, current);
        var iface = NextLineVerdict(previous, current);

        Assert.Equal(1.30, general.LineAdvance, precision: 2);
        Assert.Equal(general.LineAdvance, iface.LineAdvance, precision: 2);

        Assert.Equal("set solid", general.Rule);
        Assert.True(general.Joined);

        Assert.NotEqual("set solid", iface.Rule);
        Assert.False(iface.Joined);
    }

    /// <summary>
    /// The list entries the relaxed limit must not reach, at the leadings they were measured at.
    /// </summary>
    /// <remarks>
    /// <para>The gap the relaxed limit sits on the edge of is 0.23 line heights wide, which is
    /// narrow enough to need a guard rather than a note. These are the four pairs from the
    /// annotated pages that no bullet and no size difference protects - only their leading does -
    /// measured at 1.68, 1.72, 1.76 and 1.79. Each is a list entry under another list entry, long
    /// enough to have wrapped, so all four are judged on the relaxed limit and all four must still
    /// be refused by it.</para>
    ///
    /// <para>The corresponding corpus check is on the six annotated pages themselves: en1 G3-G7 and
    /// G12-G14, zh3 G4-G8 and G12-G14.</para>
    /// </remarks>
    [Theory]
    [InlineData(1.68)]
    [InlineData(1.72)]
    [InlineData(1.76)]
    [InlineData(1.79)]
    public void ListEntriesJustPastTheRelaxedLimit_StayApart(double advance)
    {
        var previous = Line("Genuinely embrace the open-source spirit and are", x: 100, y: 10, width: 500, height: 40);
        var current = Line("Have the willingness and capability to contribute", x: 100, y: 10 + advance * 40, width: 500, height: 40);

        var verdict = GeneralVerdict(previous, current);

        Assert.NotEqual("set solid", verdict.Rule);
        Assert.False(verdict.Joined);
        Assert.Equal(advance, verdict.LineAdvance, precision: 2);
    }

    /// <summary>
    /// A label ending in a colon is held to the unrelaxed leading in every mode, including the one
    /// that relaxes it for prose.
    /// </summary>
    /// <remarks>
    /// <para>A colon is two-faced: it ends a clause that carries on, and it ends a form's field
    /// name sitting above its value. The rule tells them apart by leading alone — a value set right
    /// under its label is a continuation, a value set further down is a separate field — so it
    /// reads the strict limit rather than the mode's, and that is deliberate. Relaxing it would
    /// give the general mode a wider window in which "Name:" swallows whatever is under it, and
    /// nothing in the layout says which of the two a given colon is.</para>
    ///
    /// <para>Written because the name no longer says so. <c>SolidLineAdvance</c> reads like "the
    /// set-solid limit", and the set-solid limit moved onto the profile in this step — so the
    /// obvious tidy-up is to make this rule read the profile too. The pair here is exactly the one
    /// that tidy-up would join: 1.30 line heights apart, which is inside the general mode's relaxed
    /// window and outside the strict one. The corpus will not catch it; this rule fires rarely
    /// enough that no image measured on this branch exercises it.</para>
    /// </remarks>
    [Fact]
    public void ALabelEndingInAColon_IsHeldToTheUnrelaxedLeading_InBothModes()
    {
        var previous = Line("Automatically navigate to the next objective:", x: 100, y: 10, width: 500, height: 40);
        var current = Line("Prioritize opening coffers over cairns", x: 100, y: 62, width: 500, height: 40);

        foreach (var verdict in new[] { GeneralVerdict(previous, current), NextLineVerdict(previous, current) })
        {
            Assert.Equal(1.30, verdict.LineAdvance, precision: 2);
            Assert.Equal("label colon", verdict.Rule);
            Assert.False(verdict.Joined);
        }
    }

    /// <summary>
    /// The same label, set right under its value, is still read as one clause carrying on.
    /// </summary>
    /// <remarks>
    /// The other side of the rule, so that the test above cannot be satisfied by refusing every
    /// colon. At 1.05 line heights the value is set under the label at a paragraph's own leading,
    /// which is the shape the rule is meant to admit.
    /// </remarks>
    [Fact]
    public void ALabelEndingInAColon_StillJoinsWhenTheValueIsSetRightUnderIt()
    {
        var previous = Line("Automatically navigate to the next objective:", x: 100, y: 10, width: 500, height: 40);
        var current = Line("Prioritize opening coffers over cairns", x: 100, y: 52, width: 500, height: 40);

        var verdict = GeneralVerdict(previous, current);

        Assert.Equal(1.05, verdict.LineAdvance, precision: 2);
        Assert.True(verdict.Joined);
    }

    /// <summary>
    /// A settings panel's checkbox entries stay apart in the general mode too, by 0.02 line
    /// heights.
    /// </summary>
    /// <remarks>
    /// <para>The narrowest margin in this rule, and the one worth a test of its own because the
    /// figure it is measured against was taken from a different population. The relaxed leading sits
    /// on a 0.23 gap — paragraph seams stop at 1.45, the bulleted lists on the same web pages start
    /// at 1.68 — but a game settings panel's checkbox entries are not those lists. They run 1.47 and
    /// 1.58, so the nearest of them clears the relaxed limit by 0.02.</para>
    ///
    /// <para>The interface mode is the answer to that and it has 0.27 to spare
    /// (<see cref="AShorterLineSetTooFarBelow_IsNotTheEndOfTheParagraph"/> is that pair on that
    /// profile). But the general mode is the default, so a user who frames a settings panel without
    /// switching modes is standing on the 0.02, and this is the test that goes red the day the
    /// relaxed limit is nudged up without anyone remembering why it could not be.</para>
    ///
    /// <para>Blocks 16-18 of <c>region-panel-en/game-menu-en.png</c>, at their own coordinates.</para>
    /// </remarks>
    [Fact]
    public void ASettingsPanelsEntries_StayApartInTheGeneralModeToo_ByTwoHundredthsOfALine()
    {
        var previous = Line("Reveal all rooms before proceeding to next floor", x: 84, y: 378, width: 307, height: 18);
        var current = Line("Allow automatic pomander use", x: 83, y: 406, width: 202, height: 20);

        var verdict = GeneralVerdict(previous, current);

        Assert.Equal(1.47, verdict.LineAdvance, precision: 2);
        Assert.NotEqual("set solid", verdict.Rule);
        Assert.False(verdict.Joined);

        // The margin itself, stated rather than implied: this is how much room there is.
        Assert.InRange(verdict.LineAdvance - GroupingProfile.General.SolidLineAdvanceWhenWrapped, 0.01, 0.03);
    }

    /// <summary>
    /// The trace reports the limit the pair was judged on, not whichever constant is named first.
    /// </summary>
    /// <remarks>
    /// Two set-solid limits now exist and which one applies is a property of the pair, so a trace
    /// printing one of them unconditionally would put a number beside a verdict that the rule never
    /// read. That is the failure this tool has already been rerun over twice.
    /// </remarks>
    [Fact]
    public void TheTrace_ReportsTheSolidLimitThePairWasJudgedOn()
    {
        var longAbove = Line("open to global upstream and downstream partners", x: 100, y: 10, width: 500, height: 40);
        var shortAbove = Line("Continue", x: 100, y: 10, width: 120, height: 40);
        var below = Line("in OCR and document intelligence. The alliance", x: 100, y: 62, width: 500, height: 40);

        Assert.Equal(1.45, GeneralVerdict(longAbove, below).SolidBar, precision: 2);
        Assert.Equal(1.20, GeneralVerdict(shortAbove, below).SolidBar, precision: 2);

        // The same wrapped line under the mode that does not take the relaxation.
        Assert.Equal(1.20, NextLineVerdict(longAbove, below).SolidBar, precision: 2);
    }

    /// <summary>One line whose glyph height is stated, for the tests about the size ratio.</summary>
    internal static OcrTextBlock LineWithGlyphHeight(
        string text, double x, double y, double width, double height, double glyph)
    {
        var bounds = new Rect(x, y, width, height);
        return new OcrTextBlock(
            text,
            bounds,
            LayoutScript: OcrLayoutScript.Latin,
            LayoutBounds: bounds,
            LayoutGlyphHeight: glyph);
    }

    /// <summary>One line with its layout geometry stated, so a fixture cannot be re-derived from its text.</summary>
    private static OcrTextBlock Line(string text, double x, double y, double width, double height)
    {
        var bounds = new Rect(x, y, width, height);
        return new OcrTextBlock(
            text,
            bounds,
            LayoutScript: OcrLayoutScript.Latin,
            LayoutBounds: bounds,
            // Stated rather than estimated from the letters: these fixtures are about alignment,
            // and a glyph height that moved with the wording would let the size gate answer first.
            LayoutGlyphHeight: height * 0.7);
    }

    private static OcrTextBlockGrouper.NextLineDecision NextLineVerdict(
        OcrTextBlock previous, OcrTextBlock current)
    {
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group([previous, current], GroupingProfile.Interface, decisions);
        return Assert.Single(decisions, decision => decision.Kind == "next");
    }

    /// <summary>The same verdict under the general profile, which waives the length test.</summary>
    private static OcrTextBlockGrouper.NextLineDecision GeneralVerdict(
        OcrTextBlock previous, OcrTextBlock current)
    {
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group([previous, current], GroupingProfile.General, decisions);
        return Assert.Single(decisions, decision => decision.Kind == "next");
    }

    /// <summary>
    /// The next-line diagnostic reports the misalignment on all three edges, not just the left one.
    /// </summary>
    /// <remarks>
    /// Centred speech is the case the left-edge figure cannot describe: each line starts at a
    /// different X because it holds a different number of letters, so the left delta grows with the
    /// difference in width while the centres stay on top of each other. The verdict here is still
    /// the one the current rules give — this test is about what the trace says, not about what was
    /// decided — because the threshold work that follows is done off these numbers and cannot start
    /// until they are visible.
    /// </remarks>
    [Fact]
    public void NextLineDiagnostic_ReportsCentreAndRightDeltas_NotOnlyTheLeftEdge()
    {
        // Same centre (200), same 40px line height, one line half as wide as the other.
        var blocks = new List<OcrTextBlock>
        {
            new("WHY ARE", new Rect(160, 10, 80, 40)),
            new("YOU PICKING ON", new Rect(120, 52, 160, 40)),
        }.AsDetected();

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, decisions);

        var next = Assert.Single(decisions, decision => decision.Kind == "next");
        Assert.Equal(1.00, next.LeftDelta, precision: 2);
        Assert.Equal(0.00, next.CenterDelta, precision: 2);
        Assert.Equal(1.00, next.RightDelta, precision: 2);
    }

    /// <summary>
    /// A same-line verdict leaves the vertical-only fields empty rather than filling them with
    /// whatever the horizontal case happens to have.
    /// </summary>
    /// <remarks>
    /// The two kinds share one record and several of its fields already mean different things
    /// (see <c>NextLineDecision</c>). The three added for the alignment work are not among them:
    /// pooling a row's numbers into a next-line distribution is how a corpus run gets thrown away,
    /// and it has happened twice.
    /// </remarks>
    [Fact]
    public void SameLineDiagnostic_LeavesTheVerticalOnlyFieldsAtZero()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Send", new Rect(10, 10, 60, 30)),
            new("to", new Rect(78, 12, 26, 26)),
        }.AsDetected();

        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, decisions);

        var row = Assert.Single(decisions, decision => decision.Kind == "row");
        Assert.Equal(0, row.CenterDelta);
        Assert.Equal(0, row.RightDelta);
        Assert.Equal(0, row.LeadingBar);
    }
}
