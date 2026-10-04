using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The rule that decides what a fresh reading is allowed to change, and the reason it exists: an
/// overlay showing the best reading of each sentence rather than the newest one, without rewriting
/// any sentence more often than the pass-level rule it replaces did.
/// </summary>
public class RealtimeReadingMergeTests
{
    // The reading that made issue #30: two sentences in frame, the second one finally read correctly
    // while the first wobbled. As one batch the pass scores lower than what is on screen, so the
    // correction was thrown away and "guitr" stayed up.
    private const string FirstLine = "The news did say they were building";
    private const string WrongSecondLine = "a facility to study that weird guitr.";
    private const string RightSecondLine = "a facility to study that weird guitar...";

    [Fact]
    public void ACorrectedSentenceReplacesTheWrongOneEvenWhenItsNeighbourWobbled()
    {
        var shown = new List<RenderedLine>
        {
            new(FirstLine, 0.98),
            new(WrongSecondLine, 0.96),
        };

        var merged = RealtimeReadingMerge.Merge(shown,
        [
            Read(FirstLine + "q", 0, 0.90),          // the same sentence, read worse
            Read(RightSecondLine, 1, 0.99),          // the correction
        ]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.Improved);
        Assert.Equal(1, merged.Kept);

        // The corrected sentence is up, and the neighbour kept the better of its own two readings
        // rather than being dragged down with it.
        Assert.Equal([FirstLine, RightSecondLine], merged.Blocks.Select(block => block.Text));
        Assert.Equal([0.98, 0.99], merged.Lines.Select(line => line.Confidence));
    }

    [Fact]
    public void ThePassLevelRuleThisReplacesWouldHaveThrownThatCorrectionAway()
    {
        // Not a test of the merge, but of why it had to change: the batch this pass read scores no
        // better than the batch on screen, so a single comparison of the two averages — which is
        // what the code did before — rejects the whole reading and the correction with it.
        var shown = new List<RenderedLine> { new(FirstLine, 0.98), new(WrongSecondLine, 0.96) };
        var read = new List<RenderedLine> { new(FirstLine + "q", 0.90), new(RightSecondLine, 0.99) };

        Assert.True(Weighted(read) <= Weighted(shown));
    }

    [Fact]
    public void ASentenceNothingOnScreenAnswersToIsShownHoweverItScored()
    {
        // The confidence rule only ever settles an argument between two readings of one sentence. A
        // new line has no argument to lose, and holding it back for scoring low would be the failure
        // this whole feature exists to prevent.
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine("Something else entirely was said here", 0.99)],
            [Read("A completely different line of dialogue", 0, 0.42)]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.Added);
        Assert.Equal(1, merged.Dropped);
        Assert.Equal("A completely different line of dialogue", merged.Blocks[0].Text);
    }

    [Fact]
    public void ReadingTheSameWordsAgainChangesNothing()
    {
        var shown = new List<RenderedLine> { new(FirstLine, 0.94), new(RightSecondLine, 0.94) };

        var merged = RealtimeReadingMerge.Merge(shown,
            [Read(FirstLine, 0, 0.97), Read(RightSecondLine, 1, 0.97)]);

        // Same words, so nothing is redrawn — and the score stays at what was actually rendered, so
        // the line cannot drift away one tolerated character at a time on the back of a higher one.
        Assert.False(merged.Changed);
        Assert.Equal(2, merged.Kept);
        Assert.Equal([0.94, 0.94], merged.Lines.Select(line => line.Confidence));
    }

    [Fact]
    public void ALineDroppedByThisPassIsNotHeldOnScreenByItsNeighbour()
    {
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine(FirstLine, 0.98), new RenderedLine(RightSecondLine, 0.98)],
            [Read(FirstLine, 0, 0.90)]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.Dropped);
        Assert.Single(merged.Blocks);
    }

    [Fact]
    public void TwoLinesReadAsOneAreNotMistakenForAReReadingOfEither()
    {
        // The detector joining a two-line subtitle into one box is a real change of what is on
        // screen, not one sentence read differently — pairing it with either half would hold the
        // other half's translation up beside it.
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine(FirstLine, 0.98), new RenderedLine(RightSecondLine, 0.98)],
            [Read(FirstLine + " " + RightSecondLine, 0, 0.95)]);

        Assert.Equal(1, merged.Added);
        Assert.Equal(2, merged.Dropped);
        Assert.Equal(0, merged.Kept);
    }

    [Fact]
    public void ASentenceThatMovedDownTheFrameIsStillTheSameSentence()
    {
        // A new line arriving above pushes everything below it down. Pairing by position alone would
        // call the moved line new and the new line a re-reading of it, translating both again.
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine(FirstLine, 0.98)],
            [Read("Somebody else started speaking", 0, 0.95), Read(FirstLine + "n", 1, 0.80)]);

        Assert.Equal(1, merged.Added);
        Assert.Equal(1, merged.Kept);
        Assert.Equal(0, merged.Dropped);
        Assert.Equal(FirstLine, merged.Blocks[1].Text);
    }

    [Fact]
    public void TheSameSentenceTwiceOnScreenKeepsItsOwnReadingEach()
    {
        // Exact matches are paired before similar ones, so a duplicated line cannot have its partner
        // consumed by the copy beside it.
        var shown = new List<RenderedLine>
        {
            new("Are you seriously telling me that", 0.99),
            new("Are you seriously telling me that", 0.70),
        };

        var merged = RealtimeReadingMerge.Merge(shown,
        [
            Read("Are you seriousIy telling me that", 0, 0.80),   // a re-reading of the first
            Read("Are you seriously telling me that", 1, 0.75),   // exactly the second
        ]);

        Assert.Equal(0, merged.Dropped);
        Assert.Equal(2, merged.Kept);

        // The exact reading paired with the copy it matches exactly, leaving the misread one arguing
        // with the 0.99 line — which it loses. Had it paired the other way round, 0.80 would have
        // beaten 0.70 and put "seriousIy" on screen.
        Assert.All(merged.Blocks, block =>
            Assert.Equal("Are you seriously telling me that", block.Text));
    }

    [Fact]
    public void ReadingsThatOnlyWobbleAtTheSameScoreNeverRewriteAnything()
    {
        // The reason the rule this replaces existed at all: a line rewritten every time recognition
        // wobbles reads far worse than one wrong character. Recognition of an unchanged subtitle
        // wanders by a character or two at a score that says nothing about which reading is right,
        // and none of that may reach the screen.
        var random = new Random(30);
        var shown = new List<RenderedLine> { new(FirstLine, 0.97), new(RightSecondLine, 0.97) };
        var rewrites = 0;

        for (var poll = 0; poll < 200; poll++)
        {
            // Within MinConfidenceGain of what is up, either side of it — which is where the score
            // stops telling a better reading from a worse one.
            var merged = RealtimeReadingMerge.Merge(shown,
            [
                Read(Wobble(FirstLine, random), 0, 0.97 + (random.NextDouble() - 0.5) * 0.04),
                Read(Wobble(RightSecondLine, random), 1, 0.97 + (random.NextDouble() - 0.5) * 0.04),
            ]);

            for (var i = 0; i < merged.Lines.Count; i++)
                if (merged.Lines[i].Text != shown[i].Text)
                    rewrites++;

            shown = [.. merged.Lines];
        }

        Assert.Equal(0, rewrites);
    }

    [Fact]
    public void ASentenceCanOnlyEverBeRewrittenByAScoreThatClimbs()
    {
        // What bounds the flicker: every rewrite of one sentence has to clear the last one by
        // MinConfidenceGain, so a line cannot be redrawn more than a handful of times however long
        // it stays up, and each redraw is a step towards the best reading rather than the newest.
        var random = new Random(30);
        var shown = new List<RenderedLine> { new(FirstLine, 0.80) };
        var scores = new List<double>();

        for (var poll = 0; poll < 200; poll++)
        {
            var merged = RealtimeReadingMerge.Merge(shown,
                [Read(Wobble(FirstLine, random), 0, 0.80 + random.NextDouble() * 0.20)]);

            if (merged.Lines[0].Text != shown[0].Text) scores.Add(merged.Lines[0].Confidence);
            shown = [.. merged.Lines];
        }

        Assert.NotEmpty(scores);
        Assert.Equal(scores.OrderBy(score => score), scores);
        for (var i = 1; i < scores.Count; i++)
            Assert.True(scores[i] > scores[i - 1] + RealtimeReadingMerge.MinConfidenceGain);

        // 200 polls of a sentence being re-read, and the reader saw it change this few times.
        Assert.True(scores.Count <= 10, $"one sentence was rewritten {scores.Count} times");
    }

    [Fact]
    public void ABetterScoreThatIsOnlyBetterByNoiseIsNotEnough()
    {
        // Where the engine stops discriminating: measured over five sessions, readings 0.01 apart
        // step backwards about as often as forwards ("Where have you gone?!" at 0.98 replaced by
        // "Where have vou gone?!" at 1.00).
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine("Where have you gone?!", 0.98)],
            [Read("Where have vou gone?!", 0, 0.99)]);

        Assert.False(merged.Changed);
        Assert.Equal("Where have you gone?!", merged.Blocks[0].Text);
    }

    [Fact]
    public void ALineCaughtMidAnimationIsReplacedByTheFinishedOne()
    {
        // A visual novel typing its line out, read when about four fifths of it was on screen. The
        // truncated reading is not a bad one — the recogniser was right about every character that
        // existed — so it scores at least as well as the finished line does, and the confidence rule
        // alone would leave the reader without the end of the sentence for as long as it is up.
        const string TypedSoFar = "I never thought I would see this place again";
        const string Finished = "I never thought I would see this place again in my life.";

        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine(TypedSoFar, 0.99)],
            [Read(Finished, 0, 0.94)]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.Improved);
        Assert.Equal(Finished, merged.Blocks[0].Text);
        Assert.Equal(0.94, merged.Lines[0].Confidence);
    }

    [Fact]
    public void AHalfDrawnLastGlyphDoesNotStopTheFinishedLineLanding()
    {
        // The character the animation was part way through is read as something else, so the shown
        // line is not literally a prefix of the finished one. That is the ordinary case rather than
        // an edge one, which is why the front of the line is compared with the usual tolerance.
        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine("You should have told me about it a lot soc", 0.97)],
            [Read("You should have told me about it a lot sooner than this.", 0, 0.95)]);

        Assert.Equal(1, merged.Improved);
        Assert.Equal("You should have told me about it a lot sooner than this.", merged.Blocks[0].Text);
    }

    [Fact]
    public void ALineThatMerelyWobbledAtItsEndIsStillJudgedOnItsScore()
    {
        // The rule above must not become "anything longer wins", or every trailing wobble rewrites
        // the line and the churn MinConfidenceGain exists to stop comes straight back. Three extra
        // characters in thirty-seven is noise, so this goes to the score — which it happens to win,
        // as it did before this rule existed.
        Assert.False(TextSimilarity.IsContinuationOf(RightSecondLine, WrongSecondLine));

        var merged = RealtimeReadingMerge.Merge(
            [new RenderedLine(WrongSecondLine, 0.96)],
            [Read(RightSecondLine, 0, 0.99)]);

        Assert.Equal(1, merged.Improved);

        // The same wobble at a score that has not climbed stays off the screen, which is the half
        // that would have been lost had growth been read into it.
        var wobbled = RealtimeReadingMerge.Merge(
            [new RenderedLine(WrongSecondLine, 0.96)],
            [Read(RightSecondLine, 0, 0.96)]);

        Assert.False(wobbled.Changed);
        Assert.Equal(WrongSecondLine, wobbled.Blocks[0].Text);
    }

    [Fact]
    public void AnAnimationIsFollowedToItsEndInAFewSteps()
    {
        // A line typing itself out, read every other poll as the session actually reads it — so each
        // read finds a chunk more of the sentence than the last one did. What the reader sees is the
        // overlay following the line in, rather than stopping wherever the first read happened to
        // catch it.
        const string Line = "So this is what you meant when you said it would be difficult.";
        var shown = new List<RenderedLine>();
        var redraws = 0;

        for (var drawn = 12; drawn <= Line.Length; drawn += 12)
        {
            var merged = RealtimeReadingMerge.Merge(shown, [Read(Line[..drawn], 0, 0.98)]);
            if (merged.Changed) redraws++;
            shown = [.. merged.Lines];
        }

        // And the last read of all, once the animation has stopped and the frame settles.
        var final = RealtimeReadingMerge.Merge(shown, [Read(Line, 0, 0.98)]);
        if (final.Changed) redraws++;

        // Everything on screen is the real sentence, from its start, and it got there in a handful
        // of steps — not one per chunk, because each has to clear MinGrowthRatio on the last.
        Assert.StartsWith(final.Lines[0].Text, Line);
        Assert.True(redraws <= 6, $"the line was redrawn {redraws} times while animating in");

        // What is deliberately not claimed: an exact finish. The animation's last chunk was two
        // characters, and two characters appearing at the end of a line is indistinguishable from
        // recognition noise — see TextSimilarity.MinGrowthChars for why that is the side to lose on.
        // The complaint this rule answers is the other end of this scale: without it the overlay
        // would have stopped at the first read that paired at all.
        var missing = Line.Length - final.Lines[0].Text.Length;
        Assert.InRange(missing, 0, TextSimilarity.MinGrowthChars - 1);
    }

    /// <summary>One character read wrong, now and then — what recognition noise looks like.</summary>
    private static string Wobble(string text, Random random)
    {
        if (random.Next(3) == 0) return text;

        var at = random.Next(text.Length);
        return text[..at] + "l" + text[(at + 1)..];
    }

    private static double Weighted(List<RenderedLine> lines)
    {
        double weighted = 0, weight = 0;
        foreach (var line in lines)
        {
            var characters = Math.Max(1, line.Text.Trim().Length);
            weighted += line.Confidence * characters;
            weight += characters;
        }

        return weight > 0 ? weighted / weight : 0;
    }

    private static OcrTextBlock Read(string text, int line, double confidence) =>
        new(text, new Rect(10, 40 + line * 30, 300, 24), Confidence: confidence);
}
