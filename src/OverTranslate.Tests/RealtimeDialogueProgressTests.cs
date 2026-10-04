using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeDialogueProgressTests
{
    [Theory]
    [InlineData(50, 0)]
    [InlineData(-50, 0)]
    [InlineData(0, 50)]
    public void MovingAndResizingFollowsCenterBeforeConfirmingSize(double dx, double dy)
    {
        var tracker = new DialogueReadingTracker();
        var initial = Read("Same dialogue.") with {
            SourceLineBounds = [new Rect(10, 10, 420, 30)],
            LayoutBounds = new Rect(10, 10, 420, 30), RenderGlyphHeight = 18 };
        var result = tracker.Merge([], [initial]);
        for (int step = 1; step <= 3; step++)
        {
            var raw = initial with { Bounds = new Rect(10 + dx * step, 10 + dy * step, 420, 38),
                SourceLineBounds = [new Rect(10 + dx * step, 10 + dy * step, 420, 38)],
                LayoutBounds = new Rect(10 + dx * step, 10 + dy * step, 420, 38), RenderGlyphHeight = 24 };
            result = tracker.Merge(result.Lines, [raw]);
            var displayed = result.Blocks[0];
            Assert.True(result.Repositioned);
            Assert.Equal(raw.Bounds.X + raw.Bounds.Width / 2, displayed.Bounds.X + displayed.Bounds.Width / 2);
            Assert.Equal(raw.Bounds.Y + raw.Bounds.Height / 2, displayed.Bounds.Y + displayed.Bounds.Height / 2);
            Assert.Equal(step == 1 ? 30 : 38, displayed.Bounds.Height);
            Assert.Equal(displayed.Bounds, Assert.Single(displayed.Lines));
            Assert.Equal(displayed.Bounds, displayed.LayoutBounds);
            Assert.Equal(step == 1 ? 18 : 24, displayed.RenderGlyphHeight);
        }
    }

    [Fact]
    public void CuteSubtitleLoggedBoxWobbleDoesNotRedrawStableWords()
    {
        var tracker = new DialogueReadingTracker();
        var boxes = new[] {
            new Rect(780, 121.4, 158, 37.3), new Rect(775, 121.5, 165, 38.9),
            new Rect(776, 119.3, 171, 40.4), new Rect(785, 121.2, 155, 36.6),
            new Rect(779, 105.4, 161, 67.2), new Rect(780, 123.1, 160, 37.8) };
        var result = tracker.Merge([], [new OcrTextBlock("Cute!", boxes[0], Confidence: 1)]);
        foreach (var box in boxes.Skip(1))
        {
            result = tracker.Merge(result.Lines, [new OcrTextBlock("Cute!", box, Confidence: 1)]);
            Assert.False(result.Changed);
        }
    }

    [Fact]
    public void MovingWithAlternatingSizesStillHasABoundedConfirmationBudget()
    {
        var tracker = new DialogueReadingTracker();
        var result = tracker.Merge([], [Read("Same dialogue.")]);
        int reads = 0;
        for (int poll = 0; poll < 20; poll++)
        {
            if (!tracker.TryTakeConfirmation()) continue;
            reads++;
            var raw = Read("Same dialogue.", x: 10 + reads * 50) with {
                Bounds = new Rect(10 + reads * 50, 10, 420, reads % 2 == 0 ? 58 : 38) };
            result = tracker.Merge(result.Lines, [raw]);
            Assert.True(result.Repositioned);
            Assert.Equal(raw.Bounds.X, result.Blocks[0].Bounds.X);
            Assert.Equal(30, result.Blocks[0].Bounds.Height);
        }
        Assert.Equal(DialogueReadingTracker.MaxConfirmationReads, reads);
        Assert.False(tracker.NeedsConfirmation);
    }

    [Fact]
    public void ExpiredTailNeedsFreshEvidenceAfterPixelChange()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("Here we go.")]);
        for (int i = 0; i < DialogueReadingTracker.MaxConfirmationReads; i++)
        {
            Assert.True(tracker.TryTakeConfirmation());
            tracker.Merge(first.Lines, [Read(i % 2 == 0 ? "Here we go.x" : "Here we go.y")]);
        }
        Assert.False(tracker.NeedsConfirmation);
        tracker.ObservePixelChange();
        var fresh = tracker.Merge(first.Lines, [Read("Here we go.x")]);
        Assert.False(fresh.Changed);
        Assert.Equal("Here we go.x", tracker.Merge(fresh.Lines, [Read("Here we go.x")]).Lines[0].Text);
    }

    [Fact]
    public void PersistentResizeIsConfirmedButDoesNotRenewOcrForever()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("Same dialogue.")]);
        var larger = new OcrTextBlock("Same dialogue.", new Rect(0, 0, 440, 50), Confidence: 0.98);
        var pending = tracker.Merge(first.Lines, [larger]);
        Assert.False(pending.Changed);
        Assert.Equal(first.Blocks[0].Bounds, pending.Blocks[0].Bounds);
        Assert.True(tracker.TryTakeConfirmation());
        var confirmed = tracker.Merge(pending.Lines, [larger]);
        Assert.True(confirmed.Repositioned);
        Assert.Equal(larger.Bounds, confirmed.Blocks[0].Bounds);
        Assert.False(tracker.NeedsConfirmation);
        Assert.False(tracker.Merge(confirmed.Lines, [larger]).Changed);
    }

    [Fact]
    public void NeighborChangeDoesNotPublishJitteringGeometryForHeldLine()
    {
        var tracker = new DialogueReadingTracker();
        var held = Read("Same dialogue.") with {
            SourceLineBounds = [new Rect(10, 10, 420, 30)], RenderGlyphHeight = 18 };
        var first = tracker.Merge([], [held, Read("Old neighbor.")]);
        var noisy = held with { Bounds = new Rect(8, 8, 428, 34),
            SourceLineBounds = [new Rect(8, 8, 428, 34)], RenderGlyphHeight = 23 };
        var changed = tracker.Merge(first.Lines, [noisy, Read("An entirely different sentence.")]);
        Assert.True(changed.Changed);
        Assert.Equal(held.Bounds, changed.Blocks[0].Bounds);
        Assert.Equal(held.SourceLineBounds, changed.Blocks[0].SourceLineBounds);
        Assert.Equal(held.RenderGlyphHeight, changed.Blocks[0].RenderGlyphHeight);
    }

    [Fact]
    public void NewSentenceDoesNotInheritTheOldPlacement()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("Same dialogue.")]);
        var next = Read("An entirely different sentence.", x: 200);
        var changed = tracker.Merge(first.Lines, [next]);
        Assert.True(changed.Changed);
        Assert.Equal(next.Bounds, changed.Blocks[0].Bounds);
    }

    private static OcrTextBlock Read(string text, double x = 10, double confidence = 0.98) =>
        new(text, new Rect(x, 10, 420, 30), Confidence: confidence);

    [Fact]
    public void CompletedTailMustNotRemainSuppressedForever()
    {
        var shown = new[] { new RenderedLine("We should leave this place right no", 0.99) };
        var complete = new[] { new OcrTextBlock("We should leave this place right now.", new Rect(10, 10, 420, 30), Confidence: 0.98) };
        ReadingMerge result = default;
        var tracker = new DialogueReadingTracker();
        for (int frame = 0; frame < 3; frame++)
        {
            result = tracker.Merge(shown, complete);
            shown = result.Lines.ToArray();
        }
        Assert.Equal(complete[0].Text, result.Blocks[0].Text);
    }

    [Fact]
    public void OneExtraReadSettlesThenIdleNeedsNoMoreOcr()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("A complete line.")]);
        Assert.True(tracker.NeedsConfirmation);
        var stable = tracker.Merge(first.Lines, [Read("A complete line.")]);
        Assert.False(stable.Changed);
        Assert.False(tracker.NeedsConfirmation);
    }

    [Fact]
    public void TransientSuffixIsNotAcceptedAndDoesNotKeepPolling()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("We should leave this place right now.")]);
        var noise = tracker.Merge(first.Lines, [Read("We should leave this place right now.x")]);
        Assert.False(noise.Changed);
        Assert.True(tracker.NeedsConfirmation);
        var stable = tracker.Merge(noise.Lines, [Read("We should leave this place right now.")]);
        Assert.False(stable.Changed);
        Assert.False(tracker.NeedsConfirmation);
    }

    [Fact]
    public void LowConfidenceSuffixAndDifferentPositionCannotConfirm()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("We should leave this place right no")]);
        var low = tracker.Merge(first.Lines, [Read("We should leave this place right now.", confidence: 0.5)]);
        Assert.Equal(first.Lines[0].Text, low.Lines[0].Text);
        var pending = tracker.Merge(low.Lines, [Read("We should leave this place right now.")]);
        var elsewhere = tracker.Merge(pending.Lines, [Read("We should leave this place right now.", x: 200)]);
        Assert.Equal(first.Lines[0].Text, elsewhere.Lines[0].Text);
    }

    [Fact]
    public void PositionChangeRefreshesWithoutChangingWordsButSmallJitterDoesNot()
    {
        var tracker = new DialogueReadingTracker();
        var first = tracker.Merge([], [Read("Same dialogue.")]);
        var jitter = tracker.Merge(first.Lines, [Read("Same dialogue.", x: 12)]);
        Assert.False(jitter.Changed);
        var moved = tracker.Merge(jitter.Lines, [Read("Same dialogue.", x: 40)]);
        Assert.True(moved.Repositioned);
        Assert.True(moved.Changed);
        Assert.Equal(first.Lines[0].Text, moved.Blocks[0].Text);
        Assert.Equal(40, moved.Blocks[0].Bounds.X);
    }

    [Fact]
    public void PanelKeepsItsOriginalConfidencePolicy()
    {
        var shown = new[] { new RenderedLine("We should leave this place right no", 0.99) };
        var merged = RealtimeReadingMerge.Merge(shown, [Read("We should leave this place right now.")]);
        Assert.False(merged.Changed);
    }
}
