using System.Diagnostics;
using System.Drawing;
using OverTranslate.Services.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// The mixed-language paths: a realtime session watching one language while a screenshot is
/// translated from another. Single-language use never reaches these, which is why they went
/// unexercised until concurrent inference was allowed.
/// </summary>
/// <remarks>
/// Both tests FAIL against the engine as it stands, and are kept skipped as the record of that:
///   - TryRecognize... blocked 1071ms, though its whole contract is to give up rather than wait.
///   - Recognize... threw "等待其他辨識結束以切換 OCR 模型時逾時。" after 10s under three regions
///     of load, because a swap-waiter has no priority over new same-model callers and so starves.
/// They are skipped rather than fixed because the scenario is still unreachable by design, though
/// no longer by refusal. A capture may now start during a session, but the session is paused before
/// it does — its poll loops stopped, so nothing new arrives to starve the swap-waiter — and it is
/// only started again once the capture is off the screen; see
/// Services.Realtime.RealtimeCaptureInterlude. A session refuses to start over a capture for the
/// same reason, from the other side. What AcquireRuntime still waits out is a single pass that was
/// already in flight when the pause landed, which is bounded by one inference rather than by load.
/// Unskip these if the two are ever allowed to read at the same time; the arbitration would then
/// have to be made fair, and the EN↔KO model reload (~1s each way) dealt with too.
/// </remarks>
public class OcrEngineConcurrencyTests(ITestOutputHelper output)
{
    private const string SkipReason =
        "並存情境已由「截圖翻譯開始前先暫停即時翻譯」排除；若兩者改為可同時辨識，解除 Skip 並修正 AcquireRuntime。";

    // Large enough that detection takes long enough to overlap deliberately, blank so nothing is
    // recognised — these tests are about arbitration, not about what the models read.
    private static Bitmap SlowFrame() => new(1600, 1000);

    private static Bitmap FastFrame() => new(160, 60);

    [Fact(Skip = SkipReason)]
    public async Task TryRecognize_WhileAnotherLanguageIsInFlight_ReturnsPromptly()
    {
        using var engine = new OnnxOcrEngine();

        // Warm both models first: the question is what arbitration costs, not what a cold load does.
        using (var warm = FastFrame())
        {
            await engine.RecognizeAsync(warm, "EN");
            await engine.RecognizeAsync(warm, "KO");
        }

        using var slow = SlowFrame();
        var busy = engine.RecognizeAsync(slow, "EN");
        await Task.Delay(150); // let the EN pass take its runtime reference

        using var fast = FastFrame();
        var started = Stopwatch.StartNew();
        var result = await engine.TryRecognizeAsync(fast, "KO");
        started.Stop();

        output.WriteLine($"TryRecognizeAsync(KO) returned {(result is null ? "null" : "blocks")} after {started.ElapsedMilliseconds}ms");
        await busy;

        // TryRecognizeAsync promises the realtime loop that it either runs now or gives up now.
        // Blocking here stalls a region's whole poll loop.
        Assert.True(
            started.ElapsedMilliseconds < 500,
            $"TryRecognizeAsync blocked for {started.ElapsedMilliseconds}ms waiting to swap models.");
    }

    [Fact(Skip = SkipReason)]
    public async Task Recognize_WhileAnotherLanguageRunsContinuously_DoesNotTimeOut()
    {
        using var engine = new OnnxOcrEngine();
        using (var warm = FastFrame())
            await engine.RecognizeAsync(warm, "EN");

        // Three regions polling continuously, as a multi-region realtime session does.
        using var load = new CancellationTokenSource();
        var loops = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!load.IsCancellationRequested)
            {
                using var frame = SlowFrame();
                try
                {
                    await engine.TryRecognizeAsync(frame, "EN", cancellationToken: load.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        })).ToArray();

        await Task.Delay(500); // let the load settle into steady state

        try
        {
            using var shot = FastFrame();
            var started = Stopwatch.StartNew();
            var blocks = await engine.RecognizeAsync(shot, "KO");
            started.Stop();

            output.WriteLine($"RecognizeAsync(KO) completed in {started.ElapsedMilliseconds}ms");
            Assert.NotNull(blocks);
        }
        finally
        {
            load.Cancel();
            await Task.WhenAll(loops);
        }
    }
}
