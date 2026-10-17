using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A failure being retried stays the session's failure until every region that failed has got
/// through again, and goes when the session stops or pauses. See RealtimeFailureBoard.
/// </summary>
public class RealtimeFailureBoardTests
{
    private const RealtimeFailureStage Read = RealtimeFailureStage.Read;
    private const RealtimeFailureStage Translate = RealtimeFailureStage.Translate;

    [Fact]
    public void A_failure_is_the_message_and_saying_it_again_changes_nothing()
    {
        var board = new RealtimeFailureBoard();

        Assert.True(board.Report(0, Translate, "down"));
        Assert.False(board.Report(1, Translate, "down"));
        Assert.Equal("down", board.Message);

        // The latest one is what is said.
        Assert.True(board.Report(0, Read, "no model"));
        Assert.Equal("no model", board.Message);
    }

    [Fact]
    public void It_stays_until_every_region_that_failed_has_got_through()
    {
        var board = new RealtimeFailureBoard();
        board.Report(0, Translate, "down");
        board.Report(1, Translate, "down");

        Assert.False(board.Recovered(0, Translate));
        Assert.Equal("down", board.Message);

        Assert.True(board.Recovered(1, Translate));
        Assert.Null(board.Message);
    }

    [Fact]
    public void A_read_getting_through_does_not_end_a_translation_failure()
    {
        var board = new RealtimeFailureBoard();
        board.Report(0, Translate, "down");

        Assert.False(board.Recovered(0, Read));
        Assert.Equal("down", board.Message);
    }

    [Fact]
    public void A_region_that_never_failed_getting_through_changes_nothing()
    {
        var board = new RealtimeFailureBoard();

        Assert.False(board.Recovered(0, Read));
        Assert.Null(board.Message);
    }

    [Fact]
    public void A_stopped_region_and_a_stopped_session_take_it_down()
    {
        var board = new RealtimeFailureBoard();
        board.Report(0, Read, "no model");
        board.Report(1, Translate, "down");

        Assert.False(board.Forget(0));
        Assert.True(board.Forget(1));
        Assert.Null(board.Message);

        board.Report(2, Read, "no model");
        Assert.True(board.Clear());
        Assert.False(board.Clear());
        Assert.Null(board.Message);
    }

    // ── The session ──────────────────────────────────────────────────────────

    private sealed class StillPicture : IRealtimeCaptureBackend
    {
        public string Name => "still";

        public Bitmap? GrabRegion(Rectangle screenBounds)
        {
            var frame = new Bitmap(screenBounds.Width, screenBounds.Height);
            using var g = Graphics.FromImage(frame);
            g.Clear(Color.White);
            return frame;
        }

        public CaptureMiss DescribeMiss(Rectangle screenBounds) => CaptureMiss.Waiting;
        public string DescribeActivity() => "";
        public string DescribeSteps() => "";

        public void Dispose()
        {
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
    }

    [Fact]
    public async Task A_session_keeps_a_failed_read_up_and_its_pause_and_stop_take_it_down()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        int changes = 0;
        session.FailureChanged += (_, _) => Interlocked.Increment(ref changes);
        var region = new RealtimeRegion(
            0, new Rectangle(0, 0, 400, 200), RealtimeBlockMode.Panel, RealtimeTextOrientation.Horizontal);

        try
        {
            // A language the recogniser does not read: every pass fails, and is retried.
            session.Start([region], "XX", "ZH-HANT", Models.TranslationProvider.Google, new StillPicture(),
                readAtOnce: true);
            await WaitUntil(() => session.CurrentFailure is not null);
            Assert.NotNull(session.CurrentFailure);
            // Said once, not at every failed pass.
            await Task.Delay(1500);
            Assert.Equal(1, Volatile.Read(ref changes));

            session.Pause();
            Assert.Null(session.CurrentFailure);
            Assert.Equal(2, Volatile.Read(ref changes));

            session.Resume();
            await WaitUntil(() => session.CurrentFailure is not null);
            Assert.NotNull(session.CurrentFailure);
        }
        finally
        {
            session.Stop(releaseRecogniser: true);
        }

        Assert.Null(session.CurrentFailure);
    }
}
