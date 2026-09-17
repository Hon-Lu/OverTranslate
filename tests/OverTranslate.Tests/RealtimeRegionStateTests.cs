using System.Drawing;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeRegionStateTests
{
    [Fact]
    public void SamplingFasterDoesNotSearchOrRescanFaster()
    {
        // The thresholds are counted in polls and derived from times, so that the poll interval
        // decides only how quickly a change is NOTICED. Whoever moves the interval next will move
        // these counts with it; this checks that what the counts are worth in milliseconds stays
        // where it was argued for — a scan of a region with no known text every ~500ms, and a full
        // re-examination of a watched region every ~1s.
        var search = (RealtimeRegionState.MaxUnsettledPolls + 1) * RealtimeRegionState.PollInterval;
        var rescan = RealtimeRegionState.FullRescanPolls * RealtimeRegionState.PollInterval;

        Assert.InRange(search, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600));
        Assert.InRange(rescan, TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(1150));
    }

    [Fact]
    public void PollingIsNeverFasterThanFramesAreReadBack()
    {
        // Both capture backends throttle the one expensive step — the GPU-to-CPU readback — so a
        // poll taken sooner than that sees pixels it has already seen. Two identical samples read as
        // "the picture has settled", which silently skips the wait in MaxTextUnsettledPolls: the
        // loop would still look settled and would simply have stopped waiting for anything. Nothing
        // about that failure is visible from the outside, so it is asserted here instead.
        Assert.True(
            RealtimeRegionState.PollInterval >= WgcMonitorCaptureBackend.MaxFrameAge,
            $"poll {RealtimeRegionState.PollInterval.TotalMilliseconds}ms is faster than the monitor " +
            $"backend's {WgcMonitorCaptureBackend.MaxFrameAge.TotalMilliseconds}ms readback");
        Assert.True(
            RealtimeRegionState.PollInterval >= WgcWindowCaptureBackend.MaxFrameAge,
            $"poll {RealtimeRegionState.PollInterval.TotalMilliseconds}ms is faster than the window " +
            $"backend's {WgcWindowCaptureBackend.MaxFrameAge.TotalMilliseconds}ms readback");
    }

    [Fact]
    public void AlternatingFalseTailsCannotForceOcrForever()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        OverTranslate.Services.OcrTextBlock Read(string text) => new(text, new System.Windows.Rect(0, 0, 100, 20), Confidence: 0.98);
        var first = state.Dialogue.Merge([], [Read("Here we go.")]);
        state.MarkRendered(OneLine, frame.Capture, first.Lines);
        int forced = 0;
        for (int poll = 0; poll < 20; poll++)
        {
            if (!state.Observe(frame.Capture, dialogue: true)) continue;
            forced++;
            var merged = state.Dialogue.Merge(state.RenderedLines, [Read(poll % 2 == 0 ? "Here we go.x" : "Here we go.y")]);
            state.MarkRendered(OneLine, frame.Capture, merged.Lines);
        }
        Assert.InRange(forced, 1, 3);
        Assert.False(state.Dialogue.NeedsConfirmation);
        Assert.Equal("Here we go.", state.RenderedText);
        frame.ChangeText();
        Assert.True(state.Observe(frame.Capture, dialogue: true));
        var pending = state.Dialogue.Merge(state.RenderedLines, [Read("Here we go.z")]);
        state.MarkRendered(OneLine, frame.Capture, pending.Lines);
        Assert.True(state.Dialogue.NeedsConfirmation);
        Assert.True(state.Observe(frame.Capture, dialogue: true));
        var confirmed = state.Dialogue.Merge(state.RenderedLines, [Read("Here we go.z")]);
        Assert.Equal("Here we go.z", confirmed.Lines[0].Text);
    }

    [Fact]
    public void BusyRecognizerDoesNotSpendConfirmationBudget()
    {
        var tracker = new DialogueReadingTracker();
        tracker.Merge([], [new OverTranslate.Services.OcrTextBlock("Here we go.", new System.Windows.Rect(0, 0, 100, 20))]);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Assert.True(tracker.TryTakeConfirmation());
            tracker.RecognitionUnavailable();
        }
        for (int attempt = 0; attempt < DialogueReadingTracker.MaxConfirmationReads; attempt++)
            Assert.True(tracker.TryTakeConfirmation());
        Assert.False(tracker.TryTakeConfirmation());
    }

    [Fact]
    public void DialogueReadsKnownTextChangeOnTheFirstPoll()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");
        frame.ChangeText();
        Assert.True(state.Observe(frame.Capture, dialogue: true));
    }

    [Fact]
    public void DialogueConfirmationCanReadAnUnchangedFingerprintOnce()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        var read = new[] { new OverTranslate.Services.OcrTextBlock("Complete subtitle.", new System.Windows.Rect(0, 0, 100, 20)) };
        var first = state.Dialogue.Merge([], read);
        state.MarkRendered(OneLine, frame.Capture, first.Lines);
        Assert.True(state.Observe(frame.Capture, dialogue: true));
        var stable = state.Dialogue.Merge(state.RenderedLines, read);
        state.MarkRendered(OneLine, frame.Capture, stable.Lines);
        Assert.False(state.Observe(frame.Capture, dialogue: true));
    }

    private static readonly List<Rectangle> OneLine = [new Rectangle(10, 40, 300, 20)];

    [Fact]
    public void StillContentIsRecognisedOnceItHasSettled()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        Assert.False(state.Observe(frame.Capture));   // first sighting — give it a poll to settle
        Assert.True(state.Observe(frame.Capture));    // held still, read it
    }

    [Fact]
    public void MovingContentIsRecognisedEvenThoughItNeverSettles()
    {
        // Before any text is known there is nothing to watch but the whole region, so a video still
        // reports a change on every poll. It has to be read anyway, or a session over moving content
        // would never get started.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxUnsettledPolls,
                "a region over constantly changing content was never recognised");
        }

        Assert.Equal(RealtimeRegionState.MaxUnsettledPolls, polls);
    }

    [Fact]
    public void BackgroundMotionAroundUnchangedTextIsIgnored()
    {
        // The whole point of watching strips: a subtitle sitting still over a playing video must not
        // be re-recognised on every poll just because the picture behind it moved.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        for (int i = 0; i < RealtimeRegionState.FullRescanPolls - 1; i++)
        {
            frame.MoveBackground();
            Assert.False(state.Observe(frame.Capture));
        }
    }

    [Fact]
    public void ChangedTextIsRecognisedWithinTwoPolls()
    {
        // The latency the reader actually feels. Two polls, not the six a region without known text
        // is allowed.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        frame.ChangeText();
        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxTextUnsettledPolls,
                "a changed line of text took too long to be recognised");
        }
    }

    [Fact]
    public void TextAppearingOutsideTheWatchedStripsIsCaughtByTheFullRescan()
    {
        // Nothing short of recognition can tell new text from background motion, so this is the one
        // case that still costs a periodic pass.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        frame.MoveBackground();

        for (int i = 0; i < RealtimeRegionState.FullRescanPolls - 1; i++)
            Assert.False(state.Observe(frame.Capture));

        Assert.True(state.Observe(frame.Capture));
    }

    [Fact]
    public void ACompletelyStillRegionIsNeverRecognisedTwice()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        // The idle path: nothing moved anywhere, so not even the full rescan may trigger a pass.
        for (int i = 0; i < RealtimeRegionState.FullRescanPolls * 3; i++)
            Assert.False(state.Observe(frame.Capture));
    }

    [Fact]
    public void OneEmptyPassDoesNotClearTheOverlayOrForgetWhereTheTextWas()
    {
        // Recognition drops a line it had a moment ago often enough. Acting on the first empty pass
        // makes the translation blink out and come straight back.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        state.MarkRendered([], frame.Capture, "");

        Assert.False(state.ShouldClearOverlay);
        Assert.True(state.IsWatchingText);
    }

    [Fact]
    public void EmptyPassesInARowClearTheOverlayAndGoBackToWatchingTheWholeRegion()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        for (int i = 0; i < RealtimeRegionState.EmptyPassesBeforeClearing; i++)
            state.MarkRendered([], frame.Capture, "");

        Assert.True(state.ShouldClearOverlay);
        Assert.False(state.IsWatchingText);

        // Back on the whole-region path, so a background change is a change again.
        frame.MoveBackground();
        Assert.False(state.Observe(frame.Capture));
        Assert.True(state.Observe(frame.Capture));
    }

    [Fact]
    public void FindingTextAgainCancelsThePendingClear()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");
        state.MarkRendered([], frame.Capture, "");

        state.MarkRendered(OneLine, frame.Capture, "hello again");

        Assert.False(state.ShouldClearOverlay);
        Assert.True(state.IsWatchingText);
    }

    [Fact]
    public void ARegionHuntingForTextScansOftenEnoughToCatchAShortLine()
    {
        // A line that shows for a second and a half has to be caught inside its own lifetime. With a
        // 250ms poll this is the search rate over live content, so it has to stay well under that.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
        }

        Assert.True(polls <= 2, $"a region with no known text only scanned every {polls + 1} polls");
    }

    [Fact]
    public void TheSearchRateNeverSlowsDownHoweverLongTheRegionStaysEmpty()
    {
        // Easing off after a quiet spell would save real work, but the moment it eased off is the
        // moment a short line could slip through between scans — and the user cannot tell that from
        // the feature not working.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();

        for (int i = 0; i < 40; i++)
            state.MarkRendered([], frame.Capture, "");

        // Moved before the first poll: MarkRendered has just recorded the current picture, so a poll
        // taken against it would only report that nothing has happened yet.
        frame.MoveBackground();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            frame.MoveBackground();
            polls++;
        }

        Assert.Equal(RealtimeRegionState.MaxUnsettledPolls, polls);
    }

    [Fact]
    public void RenderedTextIsWhatWasLastMarked()
    {
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        Assert.Equal("", state.RenderedText);

        state.MarkRendered(OneLine, frame.Capture, "hello");
        Assert.Equal("hello", state.RenderedText);
    }

    [Fact]
    public void HowWellTheShownLineWasReadIsRemembered()
    {
        // What a later reading of the same sentence is compared against before it may replace it.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        Assert.Equal(0, state.RenderedConfidence);

        state.MarkRendered(OneLine, frame.Capture, "hello", 0.93);
        Assert.Equal(0.93, state.RenderedConfidence);

        // Nothing on screen means nothing to beat, so the next reading wins whatever it scored.
        state.Invalidate();
        Assert.Equal(0, state.RenderedConfidence);
    }

    [Fact]
    public void InvalidatingMakesAnUnchangedRegionWorthReadingAgain()
    {
        // A translation that failed after the pass had been recorded. Nothing on screen has changed,
        // so without this the region would compare itself against a record of the very frame it is
        // looking at and never read it again — the line would simply never appear.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        Assert.False(state.Observe(frame.Capture));

        state.Invalidate();

        int polls = 0;
        while (!state.Observe(frame.Capture))
        {
            polls++;
            Assert.True(polls <= RealtimeRegionState.MaxTextUnsettledPolls,
                "an invalidated region was not read again");
        }
    }

    [Fact]
    public void InvalidatingForgetsTheRenderedTextButNotWhereItWas()
    {
        // The text never reached the screen, so claiming it is rendered would suppress the retry as
        // "the same words we already show". Where the words are has not stopped being true, though,
        // and giving up the strips would put the region back to searching its whole area.
        var state = new RealtimeRegionState();
        var frame = new FakeFrame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        state.Invalidate();

        Assert.Equal("", state.RenderedText);
        Assert.True(state.IsWatchingText);
    }

    /// <summary>
    /// Stands in for a captured frame: one brightness for the whole region, another for the text
    /// strips, so a test can move the background and the text independently. Each step is well over
    /// the fingerprint's tolerance, so a move here is unambiguously a change.
    /// </summary>
    private sealed class FakeFrame
    {
        private byte _background = 20;
        private byte _text = 200;

        public void MoveBackground() => _background += 40;
        public void ChangeText() => _text -= 40;

        public FrameFingerprint Capture(IReadOnlyList<Rectangle>? bands)
        {
            var cells = new byte[100];
            Array.Fill(cells, bands is null ? _background : _text);
            return new FrameFingerprint(cells);
        }
    }
}
