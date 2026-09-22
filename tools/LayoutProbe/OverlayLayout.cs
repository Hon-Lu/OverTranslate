using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Views.Overlay;

namespace LayoutProbe;

/// <summary>
/// Drives a real <see cref="OverlayWindow"/> and reads back what it drew.
/// </summary>
internal static class OverlayLayout
{
    public static void Report()
    {
        Intent();
        Console.WriteLine();
        Room();
        Console.WriteLine();
        Vertical();
    }

    /// <summary>
    /// Two boxes, identical in every respect except the intent.
    /// </summary>
    /// <remarks>
    /// The control is the point. Same rectangle, same translation, same source text, so a
    /// difference between the two printed lines cannot have come from the geometry or from the
    /// wrapping — the intent is the only variable left. Both boxes are far taller than the
    /// translation needs, because that is the condition under which vertical alignment is visible
    /// at all: in a box the text fills, top and centre are the same place.
    /// </remarks>
    private static void Intent()
    {
        Console.WriteLine("intent — identical geometry and text, one Default and one GroupReflow");

        var (reflow, ordinary) = PairOfBoxes(ordinaryAt: new Rect(400, 40, 300, 126));
        Measure([reflow, ordinary], selectionWidth: 900, selectionHeight: 400);
    }

    /// <summary>
    /// The same pair, with empty canvas to the right of each.
    /// </summary>
    /// <remarks>
    /// "Does not expand rightwards" is invisible unless there is somewhere to expand to. The
    /// ordinary path widens a bubble toward free space when the translation does not fit its own
    /// box; the re-set group is held to the box it was read from, because to the right of a speech
    /// balloon is the drawing. So this fixture gives both of them room and a translation long
    /// enough to want it, and the two widths are the answer.
    /// </remarks>
    private static void Room()
    {
        Console.WriteLine("room — the same pair with free canvas to the right, and text that wants it");

        const string longTranslation =
            "這是一段刻意寫長的譯文，長到單行放不下，所以會去看右邊還有沒有空間可以借。";
        var (reflow, ordinary) = PairOfBoxes(ordinaryAt: new Rect(40, 260, 300, 126));
        Measure(
            [reflow with { TranslatedText = longTranslation },
             ordinary with { TranslatedText = longTranslation }],
            selectionWidth: 1400,
            selectionHeight: 600);
    }

    /// <summary>
    /// A vertical capture, which never reaches the code the two fixtures above exercise.
    /// </summary>
    /// <remarks>
    /// Its SourceLineBounds are character cells rather than lines of a paragraph — the shape
    /// <c>CombineColumns</c> produces for a lone column — because that is what the vertical
    /// renderer is actually handed.
    /// </remarks>
    private static void Vertical()
    {
        Console.WriteLine("vertical — a vertical capture, drawn by the other renderer");

        Rect[] cells = [new(80, 10, 24, 24), new(80, 34, 24, 24), new(80, 58, 24, 24)];
        var column = new TranslatedBlock(
            "縦書き",
            "直排的譯文",
            new Rect(80, 10, 24, 72),
            SourceLineBounds: cells,
            BackgroundColor: Colors.White,
            TextColor: Colors.Black);

        using var overlay = Show([column], 400, 300, verticalText: true);
        var canvas = (Canvas)overlay.Window.FindName("BubbleTextCanvas")!;

        Console.WriteLine($"  text elements: {canvas.Children.Count}");
        foreach (FrameworkElement child in canvas.Children)
        {
            Console.WriteLine(
                $"    {child.GetType().Name,-10} w={child.ActualWidth,6:0.0} h={child.ActualHeight,6:0.0} " +
                $"left={Canvas.GetLeft(child),8:0.0} top={Canvas.GetTop(child),7:0.0}");
        }
    }

    /// <summary>The re-set group and its control, differing only in intent and in where they sit.</summary>
    private static (TranslatedBlock Reflow, TranslatedBlock Ordinary) PairOfBoxes(Rect ordinaryAt)
    {
        Rect[] lines =
        [
            new(0, 0, 300, 30), new(0, 32, 300, 30), new(0, 64, 300, 30), new(0, 96, 300, 30),
        ];
        var reflow = new TranslatedBlock(
            "SOURCE TEXT THAT WAS FOUR LINES LONG",
            "短短的譯文",
            new Rect(40, 40, 300, 126),
            SourceLineBounds: lines,
            BackgroundColor: Colors.White,
            TextColor: Colors.Black,
            LayoutIntent: OverlayLayoutIntent.GroupReflow);

        return (reflow, reflow with { Bounds = ordinaryAt, LayoutIntent = OverlayLayoutIntent.Default });
    }

    private static void Measure(
        List<TranslatedBlock> blocks, double selectionWidth, double selectionHeight)
    {
        using var overlay = Show(blocks, selectionWidth, selectionHeight, verticalText: false);
        var canvas = (Canvas)overlay.Window.FindName("BubbleTextCanvas")!;

        Console.WriteLine($"  bubbles: {canvas.Children.Count}");
        for (var i = 0; i < canvas.Children.Count; i++)
        {
            var container = (Border)canvas.Children[i];
            var text = (TextBlock)container.Child!;
            text.UpdateLayout();

            // Where the text starts inside its own box. Top alignment leaves it at the padding;
            // centring leaves it at (box − padding − text) / 2 + padding.
            var textTop = text.TransformToAncestor(container).Transform(new Point(0, 0)).Y;

            Console.WriteLine(
                $"    [{i}] intent={blocks[i].LayoutIntent,-11} " +
                $"box w={container.Width,6:0.0} h={container.Height,6:0.0} " +
                $"left={Canvas.GetLeft(container),8:0.0} top={Canvas.GetTop(container),7:0.0} " +
                $"fontSize={text.FontSize,5:0.0} textH={text.ActualHeight,6:0.0} " +
                $"textTopInBox={textTop,6:0.0} valign={text.VerticalAlignment} " +
                $"talign={text.TextAlignment} wrap={text.TextWrapping}");
        }
    }

    private static ShownOverlay Show(
        List<TranslatedBlock> blocks, double selectionWidth, double selectionHeight, bool verticalText)
    {
        var window = new OverlayWindow(
            blocks, [], 0, 0, selectionWidth, selectionHeight, "EN", "ZH-TW", verticalText)
        {
            ShowInTaskbar = false,
        };

        // Shown, because the overlay builds its bubbles in Loaded — it needs a real presentation
        // source to read this monitor's DPI from before it can place anything.
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();

        return new ShownOverlay(window);
    }

    private readonly record struct ShownOverlay(OverlayWindow Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }
}
