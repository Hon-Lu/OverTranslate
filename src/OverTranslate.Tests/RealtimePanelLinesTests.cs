using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimePanelLinesTests
{
    private static TranslatedBlock Block(string translation, params double[] widths) =>
        new("A complete source sentence.", translation, new Rect(0, 0, 400, 100),
            widths.Select((w, i) => new Rect(20 + i * 10, 10 + i * 30, w, 24)).ToArray());

    private static double Width(string text) => StringInfo.ParseCombiningCharacters(text).Length;

    [Theory]
    [InlineData("這是一段完整翻譯，應該依照原來的行框接續閱讀。")]
    [InlineData("The translated sentence has a different word order.")]
    [InlineData("👨‍👩‍👧‍👦 é 👍🏽 🇹🇼 👩‍💻 完整字元")]
    [InlineData("短")]
    [InlineData("")]
    public void PreservesEveryGraphemeAndOriginalRowGeometry(string translation)
    {
        var block = Block(translation, 200, 140, 180);
        var split = RealtimePanelLines.Split(block, Width);
        Assert.Equal(block.SourceLineBounds, split.Select(b => b.Bounds));
        Assert.All(split, b => Assert.Null(b.SourceLineBounds));
        Assert.All(split, b => Assert.Equal(block.OriginalText, b.OriginalText));
        static string Bare(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(Bare(translation), Bare(string.Concat(split.Select(b => b.TranslatedText))));
        var original = StringInfo.GetTextElementEnumerator(translation);
        var originalElements = new List<string>();
        while (original.MoveNext()) originalElements.Add(original.GetTextElement());
        foreach (var row in split)
        {
            var elements = StringInfo.GetTextElementEnumerator(row.TranslatedText);
            while (elements.MoveNext()) Assert.Contains(elements.GetTextElement(), originalElements);
        }
    }

    [Fact]
    public void DistributionUsesMeasuredWidthRatherThanCharacterCount()
    {
        var split = RealtimePanelLines.Split(Block("甲乙丙丁戊己", 7, 7),
            text => text == "甲" ? 6 : 1);
        Assert.Equal("甲乙", split[0].TranslatedText);
        Assert.Equal("丙丁戊己", split[1].TranslatedText);
    }

    [Fact]
    public void LatinWordsStayWhole()
    {
        const string translation = "The quick brown fox jumps over the lazy dog";
        var split = RealtimePanelLines.Split(Block(translation, 140, 200, 100), Width);
        foreach (var word in split.SelectMany(b => b.TranslatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            Assert.Contains(word, translation.Split(' '));
    }

    [Fact]
    public void ClosingPunctuationStaysWithPreviousText()
    {
        var split = RealtimePanelLines.Split(Block("甲乙，丙丁。", 100, 100), Width);
        Assert.DoesNotContain(split, b => b.TranslatedText.StartsWith('，') || b.TranslatedText.StartsWith('。'));
    }

    [Fact]
    public void InvalidRowsFallBackToTheWholeTranslation()
    {
        var block = Block("完整譯文", 0, 100);
        Assert.Same(block, Assert.Single(RealtimePanelLines.Split(block, Width)));
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.5, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    public void OnlyPanelReflowsAndCoversEvenEmptyRows(double dpi, bool longText) => OnSta(() =>
    {
        foreach (var mode in new[] { RealtimeBlockMode.Panel, RealtimeBlockMode.Subtitle })
        {
            var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 600, 300),
                _ => null, "EN", "ZH-HANT", "#FFFFFF", "#000000", 70, mode: mode);
            try
            {
                var type = typeof(RealtimeBlockWindow);
                void Set(string field, object value) =>
                    type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                var block = Block(longText ? string.Concat(Enumerable.Repeat("完整譯文需要縮小但不能遺失任何內容。", 8)) : "短", 200, 150, 180);
                Set("_dpiX", dpi);
                Set("_dpiY", dpi);
                Set("_lines", new List<TranslatedBlock> { block });
                type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                var backgrounds = (Canvas)window.FindName("ScrimCanvas");
                var texts = (Canvas)window.FindName("TextCanvas");
                Assert.Equal(mode == RealtimeBlockMode.Panel ? 3 : 1, texts.Children.Count);
                // One patch for the whole group, so joined rows read as one unit.
                var patch = Assert.IsType<Border>(Assert.Single(backgrounds.Children));
                if (mode != RealtimeBlockMode.Panel) continue;
                var covered = block.SourceLineBounds!
                    .Select(r => new Rect(r.X, r.Y, block.Bounds.Right - r.X, r.Height)).Aggregate(Rect.Union);
                Assert.Equal(covered.X / dpi, Canvas.GetLeft(patch), 6);
                Assert.Equal(covered.Y / dpi, Canvas.GetTop(patch), 6);
                Assert.Equal(covered.Width / dpi, patch.Width, 6);
                Assert.Equal(covered.Height / dpi, patch.Height, 6);
                var children = texts.Children.Cast<Border>()
                    .Select(b => (TextBlock)((Viewbox)b.Child).Child).ToArray();
                Assert.Single(children.Select(c => c.FontSize).Distinct());
                Assert.Equal(block.TranslatedText, string.Concat(children.Select(c => c.Text)));
                for (int i = 0; i < 3; i++)
                {
                    var border = Assert.IsType<Border>(texts.Children[i]);
                    var row = block.SourceLineBounds![i];
                    Assert.Equal(row.X / dpi, Canvas.GetLeft(border), 6);
                    Assert.Equal(row.Y / dpi, Canvas.GetTop(border), 6);
                    Assert.Equal((block.Bounds.Right - row.X) / dpi, border.Width, 6);
                    Assert.Equal(row.Height / dpi, border.Height, 6);
                    border.Measure(new Size(border.Width, border.Height));
                    border.Arrange(new Rect(0, 0, border.Width, border.Height));
                    var fitted = (Viewbox)border.Child;
                    Assert.True(fitted.ActualWidth <= border.Width + 0.01);
                    Assert.True(fitted.ActualHeight <= border.Height + 0.01);
                }
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void ShortTranslationUsesFirstRowWithoutArtificialBreaks()
    {
        var split = RealtimePanelLines.Split(Block("完整短句。", 100, 100, 100), Width);
        Assert.Equal("完整短句。", split[0].TranslatedText);
        Assert.All(split.Skip(1), row => Assert.Empty(row.TranslatedText));
    }

    [Fact]
    public void ShortInkTailCanUseParagraphWidthButStopsBeforeAnotherColumn()
    {
        var block = new TranslatedBlock("source", "譯文", new Rect(20, 0, 300, 60),
            [new Rect(20, 0, 300, 24), new Rect(30, 30, 20, 24)]);
        var neighbour = new TranslatedBlock("other", "other", new Rect(180, 30, 100, 24));
        Assert.Equal(new[] { 300.0, 148.0 }, RealtimePanelLines.AvailableWidths(block, [block, neighbour]));
        Assert.Equal(new[] { 300.0, 290.0 }, RealtimePanelLines.AvailableWidths(block, [block]));
    }

    [Fact]
    public void AvoidsAnIsolatedCjkGlyphOnTheLastUsedRow()
    {
        var split = RealtimePanelLines.Split(Block("甲乙丙丁戊己庚辛。", 8, 8), Width);
        Assert.Equal("甲乙丙丁", split[0].TranslatedText);
        Assert.Equal("戊己庚辛。", split[1].TranslatedText);
    }

    [Fact]
    public void LongSingleMessageDoesNotCoverTheNextMessage() => OnSta(() =>
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 400, 100),
            _ => null, "ZH-HANT", "EN", "#FFFFFF", "#000000", 100, mode: RealtimeBlockMode.Panel);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            var lines = new List<TranslatedBlock>
            {
                new("第一則", "This translated message is much longer than its short source.", new Rect(20, 10, 180, 20)),
                new("第二則", "Next message", new Rect(20, 40, 180, 20)),
            };
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var canvas = (Canvas)window.FindName("TextCanvas");
            var first = Assert.IsType<Border>(canvas.Children[0]);
            var second = Assert.IsType<Border>(canvas.Children[1]);
            Assert.True(Canvas.GetTop(first) + first.Height <= Canvas.GetTop(second));
            Assert.Equal(180, first.Width);
            var text = Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(first.Child).Child);
            Assert.Equal(lines[0].TranslatedText, text.Text);
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UsesAClauseBoundaryToAvoidAnOrphanWhenTheClauseFits()
    {
        var split = RealtimePanelLines.Split(Block("甲乙丙丁，戊己庚辛壬癸子丑。", 12, 12), Width);
        Assert.Equal("甲乙丙丁，", split[0].TranslatedText);
        Assert.Equal("戊己庚辛壬癸子丑。", split[1].TranslatedText);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
