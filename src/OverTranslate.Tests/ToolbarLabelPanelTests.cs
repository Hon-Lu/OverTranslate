using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Controls;
using Xunit;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// Where the toolbars' label card and its arrow are laid out (#349).
/// </summary>
/// <remarks>
/// The first version placed the two from a size it measured by hand, and they came apart: a card
/// sized for the previous label, cut off at the popup's edge, its arrow somewhere else. These pin the
/// layout to the panel's own pass, against real elements. Positions are compared in the bar's units,
/// from its left edge — the panel's own start, <see cref="ToolbarLabelPanel.FrameLeft"/>, moves with
/// the card and the screen.
/// </remarks>
public class ToolbarLabelPanelTests
{
    private const double BarWidth = 600;

    [Theory]
    [InlineData(300)]   // the middle of the bar
    [InlineData(20)]    // the first button
    [InlineData(585)]   // the close button at the far end
    public void The_arrow_is_on_the_button_and_off_the_cards_corners(double anchor)
    {
        OnUiThread(() =>
        {
            var (panel, card, arrow) = Build(anchor, cardWidth: 220);

            AssertArrowOnButtonAndCardInside(panel, card, arrow, anchor);
        });
    }

    [Fact]
    public void A_card_in_the_middle_is_centred_on_its_button()
    {
        OnUiThread(() =>
        {
            var (panel, card, _) = Build(anchor: 300, cardWidth: 220);
            var box = Box(panel, card);
            Assert.Equal(300, panel.FrameLeft + (box.Left + box.Right) / 2, 3);
        });
    }

    [Fact]
    public void A_card_near_the_end_stays_inside_the_bar_while_its_arrow_allows()
    {
        OnUiThread(() =>
        {
            var (panel, card, _) = Build(anchor: 540, cardWidth: 220);
            Assert.Equal(BarWidth, panel.FrameLeft + Box(panel, card).Right, 3);
        });
    }

    [Fact]
    public void A_card_wider_than_the_bar_is_centred_on_it_and_drawn_whole()
    {
        // The realtime capsule collapsed to three buttons is narrower than most of its cards: the
        // popup used to be the bar plus a fixed margin, and cut the card off at its right.
        OnUiThread(() =>
        {
            var (panel, card, arrow) = Build(anchor: 60, cardWidth: 260, span: 130);

            var box = Box(panel, card);
            Assert.Equal(65, panel.FrameLeft + (box.Left + box.Right) / 2, 3);
            AssertArrowOnButtonAndCardInside(panel, card, arrow, anchor: 60);
        });
    }

    [Theory]
    [InlineData(570, 600, 0, 600)]       // the bar's last button, its right end against the screen's
    [InlineData(30, 600, 0, 600)]        // its first, its left end against the screen's
    [InlineData(100, 130, -270, 130)]    // a collapsed capsule in the right corner
    [InlineData(30, 130, 0, 400)]        // and in the left
    public void Against_the_screens_edge_the_popup_stops_there_and_the_arrow_stays_on_its_button(
        double anchor, double span, double screenLeft, double screenRight)
    {
        // A popup that runs off the screen is pushed back onto it whole, arrow and all: the frame has
        // to end at the screen, and the card has to fit inside it with its shadow.
        OnUiThread(() =>
        {
            var (panel, card, arrow) = Build(anchor, cardWidth: 220, span: span,
                screenLeft: screenLeft, screenRight: screenRight);

            Assert.True(panel.FrameLeft >= screenLeft, $"frame starts at {panel.FrameLeft}");
            Assert.True(panel.FrameLeft + panel.ActualWidth <= screenRight, $"frame ends at {panel.FrameLeft + panel.ActualWidth}");

            var box = Box(panel, card);
            Assert.InRange(panel.FrameLeft + box.Left, screenLeft + ToolbarLabelPanel.Edge, double.MaxValue);
            Assert.InRange(panel.FrameLeft + box.Right, double.MinValue, screenRight - ToolbarLabelPanel.Edge);
            AssertArrowOnButtonAndCardInside(panel, card, arrow, anchor);
        });
    }

    [Fact]
    public void A_new_label_is_laid_out_at_its_own_size()
    {
        // The bug the panel exists to prevent: the card's size has to be the one for the text it
        // has now, not the one it had for the last button.
        OnUiThread(() =>
        {
            var text = new TextBlock { Text = "設定", FontSize = 12 };
            var card = new Border { Padding = new Thickness(12, 7, 12, 8), Child = text };
            var (panel, _, arrow) = Build(anchor: 585, card: card);

            double narrow = card.ActualWidth;
            text.Text = "自動翻譯、截圖後自動關閉、儲存截圖等設定，再加上一句讓它更長";
            panel.UpdateLayout();
            Assert.True(card.ActualWidth > narrow + 50);

            double arrowCentre = ArrowCentre(panel, arrow);
            var box = Box(panel, card);
            Assert.InRange(arrowCentre, box.Left + 18, box.Right - 18);
        });
    }

    [Fact]
    public void Above_puts_the_arrow_under_the_card_and_turns_it_over()
    {
        OnUiThread(() =>
        {
            var (panel, card, arrow) = Build(anchor: 300, cardWidth: 220, above: true);

            var cardBox = Box(panel, card);
            var arrowBox = Box(panel, arrow);
            Assert.Equal(cardBox.Bottom - 1, arrowBox.Top, 3);
            Assert.Equal(-1, ((ScaleTransform)arrow.RenderTransform).ScaleY);
        });
    }

    [Fact]
    public void Below_puts_the_arrow_over_the_card_pointing_up()
    {
        OnUiThread(() =>
        {
            var (panel, card, arrow) = Build(anchor: 300, cardWidth: 220);

            Assert.Equal(Box(panel, card).Top + 1, Box(panel, arrow).Bottom, 3);
            Assert.Same(Transform.Identity, arrow.RenderTransform);
        });
    }

    private static void AssertArrowOnButtonAndCardInside(
        ToolbarLabelPanel panel, FrameworkElement card, FrameworkElement arrow, double anchor)
    {
        double arrowCentre = ArrowCentre(panel, arrow);
        Assert.Equal(anchor, panel.FrameLeft + arrowCentre, 3);

        var cardBox = Box(panel, card);
        Assert.True(arrowCentre - cardBox.Left >= 18, $"arrow {arrowCentre} too near the card's left {cardBox.Left}");
        Assert.True(cardBox.Right - arrowCentre >= 18, $"arrow {arrowCentre} too near the card's right {cardBox.Right}");

        // Never out of the popup.
        Assert.True(cardBox.Left >= 0, $"card starts at {cardBox.Left}");
        Assert.True(cardBox.Right <= panel.ActualWidth, $"card ends at {cardBox.Right} of {panel.ActualWidth}");
    }

    private static (ToolbarLabelPanel Panel, FrameworkElement Card, FrameworkElement Arrow) Build(
        double anchor, double cardWidth = 0, bool above = false, FrameworkElement? card = null,
        double span = BarWidth,
        double screenLeft = double.NegativeInfinity, double screenRight = double.PositiveInfinity)
    {
        card ??= new Border { Width = cardWidth, Height = 40 };
        var arrow = new Canvas { Width = 12, Height = 7 };
        var panel = new ToolbarLabelPanel
        {
            Span = span, Anchor = anchor, Above = above, ScreenLeft = screenLeft, ScreenRight = screenRight,
        };
        panel.Children.Add(card);
        panel.Children.Add(arrow);

        // The root of a real presentation source, never shown, so the layout system runs as it does
        // in the popup — including a change deep in the card reaching the panel on the next pass.
        // Never shown: no WS_VISIBLE. Disposed when the test's thread ends.
        var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("label")
        {
            Width = 800,
            Height = 200,
            WindowStyle = 0,
        })
        {
            RootVisual = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        Sources.Value!.Add(source);
        panel.UpdateLayout();
        return (panel, card, arrow);
    }

    // The sources a test opened, closed with its thread.
    private static readonly ThreadLocal<List<System.Windows.Interop.HwndSource>> Sources = new(() => new());

    private static Rect Box(Visual panel, FrameworkElement child) =>
        child.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));

    private static double ArrowCentre(Visual panel, FrameworkElement arrow)
    {
        var box = Box(panel, arrow);
        return (box.Left + box.Right) / 2;
    }

    /// <summary>WPF elements may only be built on an STA thread, and xunit runs its tests on MTA ones.</summary>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var source in Sources.Value!) source.Dispose();
                Sources.Value!.Clear();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
