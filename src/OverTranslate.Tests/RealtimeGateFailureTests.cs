using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The gate's detection failing is a failed look at the region like any other: reported and waited
/// out, not the end of the region's loop.
/// </summary>
public class RealtimeGateFailureTests
{
    // Wide enough to be gated: RealtimeGate.WorthGating.
    private static readonly RealtimeRegion Wide =
        new(0, new Rectangle(0, 0, 1600, 300), RealtimeBlockMode.Panel, RealtimeTextOrientation.Horizontal);

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

    // What the session logs while body runs, as "Level|message".
    private static async Task<string[]> SessionLog(Func<Task> body)
    {
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ?? new NLog.Config.LoggingConfiguration();
        var rule = new NLog.Config.LoggingRule(typeof(RealtimeTranslationSession).FullName, NLog.LogLevel.Debug, target);
        config.LoggingRules.Add(rule);
        NLog.LogManager.Configuration = config;
        try
        {
            await body();
        }
        finally
        {
            config.LoggingRules.Remove(rule);
            NLog.LogManager.Configuration = config;
        }

        return [.. target.Logs];
    }

    [Fact]
    public async Task AGateThatThrows_LeavesTheRegionRunning_AndThePictureWaits()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        // A language the recogniser does not read makes every detection throw, the gate's included.
        // The first pass is asked for and skips the gate; the gate is reached once its wait is over.
        var log = await SessionLog(async () =>
        {
            session.Start([Wide], "XX", "ZH-HANT", Models.TranslationProvider.Google, new StillPicture(),
                readAtOnce: true);
            await Task.Delay(TimeSpan.FromSeconds(4));
            session.Stop(releaseRecogniser: true);
        });

        Assert.DoesNotContain(log, line => line.Contains("ended unexpectedly"));
        var failures = log.Count(line => line.StartsWith("Warn|") && line.Contains("Realtime pass failed"));
        // The read and then the gate; not one per poll — four seconds is over twenty-five of them.
        Assert.InRange(failures, 2, 4);
    }
}
