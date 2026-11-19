using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// A hidden line is left off the overlay and nothing else about the overlay changes: the lines
/// beside it are drawn where and how they were, and a change in it alone draws nothing again.
/// </summary>
public class RealtimeHiddenLinesDrawingTests
{
    private static readonly System.Drawing.Rectangle Block = new(0, 0, 400, 120);

    // A label and its value on one row, the label's translation longer than the label: how much
    // room it may take is decided by the value beside it.
    private static TranslatedBlock Label(string translated = "攻擊力攻擊力攻擊力") =>
        new("攻撃力", translated, new Rect(20, 30, 60, 24)) { ReadAs = RealtimeBlockMode.Panel };

    private static TranslatedBlock Value(string text, bool hidden) =>
        new(text, text, new Rect(100, 30, 50, 24)) { ReadAs = RealtimeBlockMode.Panel, Hidden = hidden };

    private readonly record struct Drawn(int Boxes, IReadOnlyList<(double Left, double Top, double Width, double Height, double Size, string Text)> Texts);

    private static Drawn Draw(params TranslatedBlock[][] passes) =>
        OnStaThread(() =>
        {
            var window = NewWindow();
            try
            {
                foreach (var lines in passes) window.SetLines(lines);
                return Measure(window);
            }
            finally
            {
                window.Close();
            }
        });

    private static Drawn DrawIn(RealtimeBlockMode mode, TranslatedBlock[] lines) =>
        OnStaThread(() =>
        {
            var window = NewWindow(mode);
            try
            {
                window.SetLines(lines);
                return Measure(window);
            }
            finally
            {
                window.Close();
            }
        });

    private static RealtimeBlockWindow NewWindow(RealtimeBlockMode mode = RealtimeBlockMode.Panel)
    {
        var window = new RealtimeBlockWindow(
            0, Block, _ => null, "JA", "ZH-TW",
            RealtimeSubtitleColors.DefaultText,
            RealtimeSubtitleColors.DefaultScrim,
            RealtimeSubtitleColors.DefaultScrimOpacity,
            mode: mode);
        typeof(RealtimeBlockWindow)
            .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        return window;
    }

    private static Drawn Measure(RealtimeBlockWindow window)
    {
        var canvas = (Canvas)window.FindName("TextCanvas");
        var texts = canvas.Children.OfType<Border>()
            .Select(border =>
            {
                var text = FindText(border);
                return (Canvas.GetLeft(border), Canvas.GetTop(border), border.Width, border.Height, text.FontSize, text.Text);
            })
            .ToList();
        return new Drawn(canvas.Children.Count, texts);
    }

    private static TextBlock FindText(System.Windows.DependencyObject element) =>
        TryFind(element) ?? throw new InvalidOperationException("no text in " + element.GetType().Name);

    private static TextBlock? TryFind(System.Windows.DependencyObject element)
    {
        if (element is TextBlock text) return text;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(element).OfType<System.Windows.DependencyObject>())
            if (TryFind(child) is { } found) return found;
        return null;
    }

    [Fact]
    public void AHiddenLineIsNotDrawn()
    {
        var drawn = Draw([Label(), Value("1200", hidden: true)]);

        var only = Assert.Single(drawn.Texts);
        Assert.Equal("攻擊力攻擊力攻擊力", only.Text);
    }

    [Fact]
    public void TheLineBesideItIsDrawnAsIfItWereStillThere()
    {
        var shown = Draw([Label(), Value("1200", hidden: false)]);
        var hidden = Draw([Label(), Value("1200", hidden: true)]);

        Assert.Equal(2, shown.Boxes);
        Assert.Equal(shown.Texts[0], Assert.Single(hidden.Texts));
    }

    [Fact]
    public void AHiddenLineSomethingDrawnWouldCoverIsDrawnAsBefore()
    {
        // Read as a subtitle, the label's band is as wide as its translation and reaches over the
        // value beside it. Drawn, the value's own box went over the band; left out, the band would
        // hide the number it was meant to leave on show. So it is drawn, exactly as before.
        TranslatedBlock Subtitle(TranslatedBlock line) => line with { ReadAs = RealtimeBlockMode.Subtitle };
        var label = Subtitle(Label());

        var shown = DrawIn(RealtimeBlockMode.Subtitle, [label, Subtitle(Value("1200", hidden: false))]);
        var hidden = DrawIn(RealtimeBlockMode.Subtitle, [label, Subtitle(Value("1200", hidden: true))]);
        Assert.Equal(2, shown.Boxes);
        Assert.Equal(shown.Texts, hidden.Texts);

        // Out of the band's reach it is left out.
        var far = Subtitle(Value("1200", hidden: true)) with { Bounds = new Rect(330, 30, 50, 24) };
        var apart = DrawIn(RealtimeBlockMode.Subtitle, [label, far]);
        Assert.Equal("攻擊力攻擊力攻擊力", Assert.Single(apart.Texts).Text);
    }

    [Fact]
    public void AHiddenLineTheRowAboveOnlyReachesTheEdgeOfIsLeftOut()
    {
        // A name over its HP, the two rows read with boxes a couple of pixels into each other, so
        // the name's box reaches into the top of the HP row: the figures under it can still be
        // read, so they are left out like every other in the column, not drawn for a margin.
        var name = new TranslatedBlock("名前", "名前名前", new Rect(20, 30, 120, 24)) { ReadAs = RealtimeBlockMode.Panel };
        var hp = new TranslatedBlock("HP 210/300", "HP 210/300", new Rect(20, 52, 120, 24)) { ReadAs = RealtimeBlockMode.Panel, Hidden = true };

        var shown = Draw([name, hp with { Hidden = false }]);
        var above = shown.Texts.Single(text => text.Text == "名前名前");
        Assert.True(above.Top + above.Height > hp.Bounds.Top + 0.5, "the name's box no longer reaches into the row below");

        var drawn = Draw([name, hp]);
        Assert.Equal("名前名前", Assert.Single(drawn.Texts).Text);
    }

    [Theory]
    // Down into the row from above, across all of it: half its height is where its letters are.
    [InlineData(0, 0, 400, 30 + 11.9, false)]
    [InlineData(0, 0, 400, 30 + 12, true)]
    // Up into it from below, the same.
    [InlineData(0, 54 - 11.9, 400, 100, false)]
    [InlineData(0, 54 - 12, 400, 100, true)]
    // Across it on its own row, as a subtitle band does: the same length again, about a letter.
    [InlineData(0, 30, 100 + 11.9, 24, false)]
    [InlineData(0, 30, 100 + 12, 24, true)]
    // A corner, deep enough one way and not the other.
    [InlineData(0, 0, 100 + 6, 54, false)]
    public void ARowIsHiddenOnceSomethingReachesHalfItsHeightIntoIt(double left, double top, double width, double height, bool hides)
    {
        var row = new Rect(100, 30, 50, 24);
        Assert.Equal(hides, RealtimeBlockWindow.HidesRow(new Rect(left, top, width, height), row));
    }

    [Fact]
    public void AChangeInAHiddenLineAloneDrawsNothingAgain()
    {
        OnStaThread(() =>
        {
            var window = NewWindow();
            try
            {
                window.SetLines([Label(), Value("12:30", hidden: true)]);
                var before = ((Canvas)window.FindName("TextCanvas")).Children[0];

                // A timer ticking over: the label is not drawn again.
                window.SetLines([Label(), Value("12:31", hidden: true)]);
                Assert.Same(before, ((Canvas)window.FindName("TextCanvas")).Children[0]);

                // The label changing is drawn, as it always was.
                window.SetLines([Label("防禦力防禦力防禦力"), Value("12:31", hidden: true)]);
                var after = ((Canvas)window.FindName("TextCanvas")).Children[0];
                Assert.NotSame(before, after);
                Assert.Equal("防禦力防禦力防禦力", FindText(after).Text);

                // And a line going from hidden to shown is drawn.
                window.SetLines([Label("防禦力防禦力防禦力"), Value("12:31", hidden: false)]);
                Assert.Equal(2, ((Canvas)window.FindName("TextCanvas")).Children.Count);
                return 0;
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
