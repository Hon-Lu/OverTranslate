using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 過濾數字與縮寫 in capture translation: off by default and then exactly as before it existed;
/// on, the groups with nothing to translate are neither asked for nor drawn; turned over a capture
/// already on screen, only those groups are hidden, or asked for and shown.
/// </summary>
public class CaptureFilterNumbersTests
{
    private static readonly OcrTextBlock Title = new("Equipment: Iron Sword", new Rect(10, 10, 220, 24));
    private static readonly OcrTextBlock Health = new("HP 245/310", new Rect(10, 44, 110, 24));
    private static readonly OcrTextBlock Attack = new("ATK 132", new Rect(10, 78, 80, 24));
    private static readonly OcrTextBlock Gold = new("Gold 1,250", new Rect(130, 44, 100, 24));

    private static List<OcrTextBlock> Panel() => [Title, Health, Attack, Gold];

    private static TranslatedBlock Answer(OcrTextBlock block) =>
        new(block.Text, "譯:" + block.Text, block.Bounds, block.Lines, block.RenderGlyphHeight);

    /// <summary>An engine that answers every group with "譯:" and its text, and keeps what it was asked.</summary>
    private sealed class Engine
    {
        public List<List<OcrTextBlock>> Requests { get; } = [];

        public Task<List<TranslatedBlock>> Translate(List<OcrTextBlock> request)
        {
            Requests.Add(request);
            return Task.FromResult(request.Select(Answer).ToList());
        }
    }

    // ── The setting ──────────────────────────────────────────────────────────

    [Fact]
    public void OffByDefault() => Assert.False(new CaptureSettings().FilterNumbersAndAbbreviations);

    [Fact]
    public void AFileWrittenBeforeItExistedReadsAsOff()
    {
        Assert.False(SettingsService.Parse("""{"Capture":{"CloseAfterCopy":true}}""").Capture.FilterNumbersAndAbbreviations);
        Assert.False(SettingsService.Parse("""{"AutoTranslateAfterSelection":true}""").Capture.FilterNumbersAndAbbreviations);
        Assert.True(SettingsService.Parse("""{"Capture":{"FilterNumbersAndAbbreviations":true}}""").Capture.FilterNumbersAndAbbreviations);
    }

    // ── A translation ────────────────────────────────────────────────────────

    /// <summary>
    /// Off, the engine is handed the list as it was read — the same instance — and its answer comes
    /// back block for block, the same instances: nothing between them is any different from before.
    /// </summary>
    [Fact]
    public async Task Off_EveryGroupIsAskedForAndDrawnAsBefore()
    {
        var source = Panel();
        var engine = new Engine();
        var state = new CaptureFilteredTranslation(source, filter: false);

        var result = await state.TranslateAsync(engine.Translate);

        var request = Assert.Single(engine.Requests);
        Assert.Same(source, request);
        Assert.True(result.Taken);
        Assert.Equal(result.Answer, state.Blocks());
        Assert.All(state.Blocks().Zip(result.Answer), pair => Assert.Same(pair.Second, pair.First));
        Assert.DoesNotContain(state.Blocks(), block => block.Hidden);
    }

    [Fact]
    public async Task On_OnlyGroupsWithWordsAreAskedFor()
    {
        var engine = new Engine();
        var state = new CaptureFilteredTranslation(Panel(), filter: true);

        var result = await state.TranslateAsync(engine.Translate);

        Assert.Equal(new[] { Title.Text, Gold.Text }, Assert.Single(engine.Requests).Select(block => block.Text));
        Assert.Equal(new[] { 0, 3 }, result.Asked);
        var blocks = state.Blocks();
        Assert.Equal(new[] { false, true, true, false }, blocks.Select(block => block.Hidden));
        Assert.Equal(new[] { "譯:" + Title.Text, Health.Text, Attack.Text, "譯:" + Gold.Text },
            blocks.Select(block => block.TranslatedText));
        Assert.Equal(Panel().Select(block => block.Bounds), blocks.Select(block => block.Bounds));
    }

    [Fact]
    public async Task On_NothingIsAskedWhenEveryGroupIsLeftOut()
    {
        var engine = new Engine();
        var state = new CaptureFilteredTranslation([Health, Attack], filter: true);

        var result = await state.TranslateAsync(engine.Translate);

        Assert.Empty(engine.Requests);
        Assert.True(result.Taken);
        Assert.Empty(result.Asked);
        Assert.All(state.Blocks(), block => Assert.True(block.Hidden));
    }

    /// <summary>複製譯文: a group left out is copied as its original text, in its own place.</summary>
    [Fact]
    public async Task CopyingTheTranslationKeepsTheNumbersWhereTheyWere()
    {
        var state = new CaptureFilteredTranslation(Panel(), filter: true);
        await state.TranslateAsync(new Engine().Translate);

        Assert.Equal("譯:Equipment: Iron Sword HP 245/310 ATK 132 譯:Gold 1,250",
            string.Join(" ", state.Blocks().Select(block => block.TranslatedText)));
    }

    // ── Turning the switch over a capture on screen ─────────────────────────

    [Fact]
    public async Task TurnedOn_HidesWithoutAskingAgain()
    {
        var engine = new Engine();
        var state = new CaptureFilteredTranslation(Panel(), filter: false);
        await state.TranslateAsync(engine.Translate);
        var before = state.Blocks();

        state.SetFilter(true);

        Assert.False(state.NeedsAsking);
        var after = state.Blocks();
        Assert.Equal(new[] { false, true, true, false }, after.Select(block => block.Hidden));
        Assert.Same(before[0], after[0]);
        Assert.Same(before[3], after[3]);
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task TurnedOff_AsksOnlyForTheGroupsItHadLeftOut()
    {
        var engine = new Engine();
        var state = new CaptureFilteredTranslation(Panel(), filter: true);
        await state.TranslateAsync(engine.Translate);
        var before = state.Blocks();

        state.SetFilter(false);
        Assert.True(state.NeedsAsking);
        var result = await state.TranslateAsync(engine.Translate);

        Assert.Equal(2, engine.Requests.Count);
        Assert.Equal(new[] { Health.Text, Attack.Text }, engine.Requests[1].Select(block => block.Text));
        Assert.Equal(new[] { 1, 2 }, result.Asked);
        var after = state.Blocks();
        Assert.DoesNotContain(after, block => block.Hidden);
        Assert.Same(before[0], after[0]);
        Assert.Same(before[3], after[3]);
        Assert.Equal("譯:" + Health.Text, after[1].TranslatedText);
    }

    [Fact]
    public async Task TurnedBackAndForth_EachGroupIsAskedForOnce()
    {
        var engine = new Engine();
        var state = new CaptureFilteredTranslation(Panel(), filter: true);
        await state.TranslateAsync(engine.Translate);

        state.SetFilter(false);
        await state.TranslateAsync(engine.Translate);
        state.SetFilter(true);
        await state.TranslateAsync(engine.Translate);
        Assert.Equal(new[] { false, true, true, false }, state.Blocks().Select(block => block.Hidden));
        state.SetFilter(false);
        await state.TranslateAsync(engine.Translate);

        Assert.Equal(2, engine.Requests.Count);
        Assert.Equal(4, engine.Requests.Sum(request => request.Count));
        Assert.DoesNotContain(state.Blocks(), block => block.Hidden);
    }

    /// <summary>Turned off and on again while the answer is on its way: asked once, and drawn only when off.</summary>
    [Fact]
    public async Task TurnedWhileAsking_TheGroupsAreNotAskedForTwice()
    {
        var state = new CaptureFilteredTranslation(Panel(), filter: true);
        await state.TranslateAsync(new Engine().Translate);
        state.SetFilter(false);

        var pending = new TaskCompletionSource<List<TranslatedBlock>>();
        List<OcrTextBlock>? asked = null;
        var first = state.TranslateAsync(request => { asked = request; return pending.Task; });
        Assert.False(state.NeedsAsking);
        state.SetFilter(true);
        state.SetFilter(false);
        Assert.False(state.NeedsAsking);

        pending.SetResult([.. asked!.Select(Answer)]);
        Assert.True((await first).Taken);
        Assert.DoesNotContain(state.Blocks(), block => block.Hidden);
    }

    [Fact]
    public async Task AFailedOrMismatchedAnswerLeavesTheGroupsToBeAskedAgain()
    {
        var state = new CaptureFilteredTranslation(Panel(), filter: true);
        await state.TranslateAsync(new Engine().Translate);
        state.SetFilter(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.TranslateAsync(_ => throw new InvalidOperationException("engine down")));
        Assert.True(state.NeedsAsking);

        var mismatched = await state.TranslateAsync(request => Task.FromResult(new List<TranslatedBlock> { Answer(request[0]) }));
        Assert.False(mismatched.Taken);
        Assert.True(state.NeedsAsking);
        Assert.True(state.Blocks()[1].Hidden);
    }

    // ── The bubbles ─────────────────────────────────────────────────────────

    /// <summary>
    /// A group left out still has its place among the pieces, in the colours sampled for it, so
    /// every other bubble is cut and coloured the same whichever way the switch is.
    /// </summary>
    [Fact]
    public async Task TheBubblesAreTheSameShapesEitherWay()
    {
        var bubbles = new CaptureBubbleShapes(Panel(), CaptureLayoutMode.General, verticalText: false, "ZH-HANT");
        bubbles.Colour([.. bubbles.Shapes.Select((shape, i) => shape with
        {
            BackgroundColor = System.Windows.Media.Color.FromRgb((byte)i, 0, 0),
        })]);
        var off = new CaptureFilteredTranslation(Panel(), filter: false);
        var on = new CaptureFilteredTranslation(Panel(), filter: true);
        await off.TranslateAsync(new Engine().Translate);
        await on.TranslateAsync(new Engine().Translate);

        var drawnOff = bubbles.Final(off.Blocks())!;
        var drawnOn = bubbles.Final(on.Blocks())!;

        Assert.Equal(drawnOff.Select(block => block.Bounds), drawnOn.Select(block => block.Bounds));
        Assert.Equal(drawnOff.Select(block => block.BackgroundColor), drawnOn.Select(block => block.BackgroundColor));
        Assert.Equal(new[] { false, true, true, false }, drawnOn.Select(block => block.Hidden));
        Assert.Equal(drawnOff[0].TranslatedText, drawnOn[0].TranslatedText);
        Assert.Equal(drawnOff[3].TranslatedText, drawnOn[3].TranslatedText);
    }

    /// <summary>
    /// The overlay draws nothing for a hidden piece, and turning the switch hides or shows only the
    /// pieces it concerns: every other bubble stays where it was.
    /// </summary>
    [Fact]
    public void TheOverlayHidesAndShowsOnlyThePiecesTheSwitchConcerns() => OnSta(() =>
    {
        var shown = Panel().Select(Answer).ToList();
        var hidden = shown.Select((block, i) => i is 1 or 2 ? block with { Hidden = true } : block).ToList();
        var window = new OverlayWindow(hidden, [], 0, 0, 400, 300, "EN", "ZH-HANT", false);
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var texts = (Canvas)window.FindName("BubbleTextCanvas");
            window.UpdateBlocks(hidden, [], 0, 0, 400, 300, "EN", "ZH-HANT", false);
            Assert.Equal(new[] { shown[0].TranslatedText, shown[3].TranslatedText }, Written(texts));
            var places = Places(texts);

            // While the original is up there is nothing to fade: the pieces are simply there or not.
            window.SetBubblesVisible(false);
            window.ApplyHidden(shown);
            Assert.Equal(shown.Select(block => block.TranslatedText), Written(texts));
            Assert.All(texts.Children.Cast<UIElement>(), element => Assert.Equal(1, element.Opacity));
            Assert.Equal(places[0], Places(texts)[0]);
            Assert.Equal(places[1], Places(texts)[3]);

            window.ApplyHidden(hidden);
            Assert.Equal(new[] { shown[0].TranslatedText, shown[3].TranslatedText }, Written(texts));
            Assert.Equal(places, Places(texts));

            // On screen, what comes fades up from nothing and what stays is as it was.
            window.SetBubblesVisible(true);
            window.ApplyHidden(shown);
            Assert.Equal(shown.Select(block => block.TranslatedText), Written(texts));
            if (SystemParameters.ClientAreaAnimation)
                Assert.Equal(new[] { 1.0, 0.0, 0.0, 1.0 }, texts.Children.Cast<UIElement>().Select(element => element.Opacity));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// A hidden piece a drawn bubble would cover — a count inside a button — is drawn all the same,
    /// holding its own text; one beside a bubble is not.
    /// </summary>
    [Fact]
    public void AHiddenPieceUnderABubbleIsDrawnAllTheSame() => OnSta(() =>
    {
        var button = new TranslatedBlock("Skill Check", "技能確認", new Rect(10, 10, 140, 24));
        var inside = new TranslatedBlock("4", "4", new Rect(120, 12, 14, 20)) { Hidden = true };
        var beside = new TranslatedBlock("12", "12", new Rect(260, 12, 20, 20)) { Hidden = true };
        var window = new OverlayWindow([], [], 0, 0, 400, 300, "EN", "ZH-HANT", false);
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var texts = (Canvas)window.FindName("BubbleTextCanvas");
            window.UpdateBlocks([button, inside, beside], [], 0, 0, 400, 300, "EN", "ZH-HANT", false);

            Assert.Equal(new[] { "技能確認", "4" }, Written(texts));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The debug boxes mark a group left out in grey — the group box dashed, the line boxes thin and
    /// solid — and one legend says how many there are: with either kind of box on, never with both
    /// off, and nothing at all with the switch off.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TheDebugBoxesMarkTheGroupsLeftOut(bool groups, bool lines) => OnSta(() =>
    {
        var debug = SettingsService.Instance.Current.OcrDebug;
        var (oldGroups, oldLines, oldScope) = (debug.ShowGroupBoxes, debug.ShowLineBoxes, debug.ShowOnTranslation);
        debug.ShowGroupBoxes = groups;
        debug.ShowLineBoxes = lines;
        debug.ShowOnTranslation = true;
        var source = Panel();
        var window = new OverlayWindow([], [], 0, 0, 400, 300, "EN", "ZH-HANT", false);
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var canvas = (Canvas)window.FindName("DebugCanvas");
            window.ShowOcrDebug(source, 0, 0);
            var off = Snapshot(canvas);
            Assert.Empty(Legends(canvas));

            window.SetFilteredGroups(source, new HashSet<int> { 1, 2 });
            var boxes = canvas.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
            var strokes = boxes.Select(box => ((System.Windows.Media.SolidColorBrush)box.Stroke).Color.ToString()).ToList();
            if (groups)
                Assert.Equal(new[] { "#FFFFB454", "#FFB8BEC6", "#FFB8BEC6", "#FFFFB454" }, strokes.Take(4));
            if (lines)
            {
                // One line each, in group order, after the group boxes: grey for the two left out,
                // thin, solid, with the low fill every box has.
                var lineBoxes = boxes.Skip(groups ? 4 : 0).ToList();
                Assert.Equal(new[] { "#FF4DA3FF", "#FFB8BEC6", "#FFB8BEC6", "#FF4DA3FF" },
                    lineBoxes.Select(box => ((System.Windows.Media.SolidColorBrush)box.Stroke).Color.ToString()));
                Assert.All(lineBoxes, box => Assert.Equal(1, box.StrokeThickness));
                Assert.All(lineBoxes, box => Assert.Null(box.StrokeDashArray));
                Assert.Equal("#26B8BEC6", ((System.Windows.Media.SolidColorBrush)lineBoxes[1].Fill).Color.ToString());
            }
            Assert.DoesNotContain("#804DA3FF", strokes);

            var legends = Legends(canvas);
            Assert.Equal(groups || lines ? 1 : 0, legends.Count);
            if (groups || lines)
            {
                var row = (StackPanel)legends[0].Child;
                var sample = (System.Windows.Shapes.Rectangle)row.Children[0];
                Assert.Equal("#FFB8BEC6", ((System.Windows.Media.SolidColorBrush)sample.Stroke).Color.ToString());
                // The sample is the grey box that is on screen: dashed with the group boxes on.
                Assert.Equal(groups, sample.StrokeDashArray is { Count: > 0 });
                Assert.Equal(LocalizationService.Format("S.Debug.FilteredLegend", 2), ((TextBlock)row.Children[1]).Text);
            }

            // A reading other than the one judged is not marked; nor is anything with the switch off.
            window.ShowOcrDebug([.. source], 0, 0);
            Assert.Equal(off, Snapshot(canvas));
            window.ShowOcrDebug(source, 0, 0);
            window.SetFilteredGroups(source, new HashSet<int>());
            Assert.Equal(off, Snapshot(canvas));
        }
        finally
        {
            window.Close();
            (debug.ShowGroupBoxes, debug.ShowLineBoxes, debug.ShowOnTranslation) = (oldGroups, oldLines, oldScope);
        }
    });

    /// <summary>
    /// The legend goes outside the selection's top-left corner; below its bottom-left corner where
    /// the monitor's top edge or the toolbar is in the way; inside its top-left corner only when
    /// neither has room.
    /// </summary>
    [Fact]
    public void TheLegendGoesAboveThenBelowThenInside()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var legend = new Size(140, 26);

        Assert.Equal(new Point(100, 200 - 4 - 26),
            OverlayWindow.FilteredLegendPlace(new Rect(100, 200, 600, 400), legend, monitor, Rect.Empty, 4));
        // Against the monitor's top edge.
        Assert.Equal(new Point(100, 10 + 400 + 4),
            OverlayWindow.FilteredLegendPlace(new Rect(100, 10, 600, 400), legend, monitor, Rect.Empty, 4));
        // The toolbar above the selection, where it goes when there is no room under it.
        var bar = new Rect(80, 600, 900, 90);
        Assert.Equal(new Point(100, 700 + 300 + 4),
            OverlayWindow.FilteredLegendPlace(new Rect(100, 700, 600, 300), legend, monitor, bar, 4));
        // Neither: the selection fills the monitor.
        Assert.Equal(new Point(4, 4),
            OverlayWindow.FilteredLegendPlace(new Rect(0, 0, 1920, 1080), legend, monitor, Rect.Empty, 4));
        // Below the selection is the toolbar and above it is the monitor's edge: inside.
        Assert.Equal(new Point(104, 14),
            OverlayWindow.FilteredLegendPlace(new Rect(100, 10, 600, 400), legend, monitor, new Rect(90, 412, 800, 90), 4));
    }

    private static List<Border> Legends(Canvas canvas) => [.. canvas.Children.OfType<Border>()];

    private static List<string> Snapshot(Canvas canvas) =>
        [.. canvas.Children.Cast<FrameworkElement>().Select(e =>
            $"{e.GetType().Name} {Canvas.GetLeft(e)},{Canvas.GetTop(e)} {e.Width}x{e.Height} " +
            (e is System.Windows.Shapes.Rectangle r ? ((System.Windows.Media.SolidColorBrush)r.Stroke).Color.ToString() : ""))];

    private static List<string> Written(Canvas canvas) =>
        [.. canvas.Children.OfType<Border>().Select(border => ((TextBlock)border.Child).Text)];

    private static List<Point> Places(Canvas canvas) =>
        [.. canvas.Children.Cast<UIElement>().Select(element => new Point(Canvas.GetLeft(element), Canvas.GetTop(element)))];

    // ── The menu ────────────────────────────────────────────────────────────

    [Fact]
    public void TheMenuReadsTranslationThenScreenshotThenOther()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Capture", "ToolbarWindow.xaml"));
        var panel = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MorePanel").Elements().Single();
        var order = panel.Elements()
            .Where(e => e.Name.LocalName is "TextBlock" or "CheckBox")
            .Select(e => (string?)e.Attribute(x + "Name") ?? (string?)e.Attribute("Text"))
            .ToList();

        Assert.Equal(new[]
        {
            "{DynamicResource S.Toolbar.MoreTranslation}", "AutoTranslateSwitch", "FilterNumbersSwitch",
            "{DynamicResource S.Toolbar.MoreScreenshot}", "SaveScreenshotSwitch", "CloseAfterCopySwitch", "CopyOnCtrlCSwitch",
            "{DynamicResource S.Toolbar.MoreOther}", "ModelHintSwitch",
        }, order);
    }

    /// <summary>The info mark names itself and says what the switch does, and a press on it is its own.</summary>
    [Fact]
    public void TheInfoMarkIsNamedAndExplainsTheSwitch()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Capture", "ToolbarWindow.xaml"));
        var info = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "FilterNumbersInfo");

        Assert.Equal("{DynamicResource S.Toolbar.InfoIcon}", (string?)info.Attribute("AutomationProperties.Name"));
        Assert.Equal("{DynamicResource S.Realtime.ProfileFilterNumbersHint}", (string?)info.Attribute("AutomationProperties.HelpText"));
        Assert.NotNull(info.Attribute("PreviewMouseLeftButtonDown"));
        Assert.NotNull(info.Attribute("PreviewMouseLeftButtonUp"));
        var card = info.Elements().Single(e => e.Name.LocalName == "HoverToolTip.Content");
        Assert.Equal(new[]
        {
            "{DynamicResource S.Realtime.ProfileFilterTagNumbers}", "{DynamicResource S.Realtime.ProfileFilterTagNumberSymbols}",
            "{DynamicResource S.Realtime.ProfileFilterTagSymbols}", "{DynamicResource S.Realtime.ProfileFilterTagAbbreviations}",
        }, card.Descendants().Where(e => e.Name.LocalName == "ContentControl").Select(e => (string?)e.Attribute("Content")));
        Assert.Contains(card.Descendants(), e => (string?)e.Attribute("Text") == "{DynamicResource S.Realtime.ProfileFilterNumbersHint}");
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
