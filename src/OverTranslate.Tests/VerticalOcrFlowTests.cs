using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class VerticalOcrFlowTests
{
    [Fact]
    public async Task Realtime_PreservesPixelsAndForwardsLimitAndCancellation()
    {
        using var bitmap = new Bitmap(80, 160);
        bitmap.SetPixel(7, 11, Color.Red);
        using var cancellation = new CancellationTokenSource();
        using var engine = new Engine();
        var result = await OcrService.TryRecognizeVerticalAsync(
            engine, bitmap, "JA", 96, cancellation.Token);
        Assert.Empty(result!);
        Assert.Same(bitmap, engine.Image);
        Assert.Equal(Color.Red.ToArgb(), engine.Image!.GetPixel(7, 11).ToArgb());
        Assert.True(engine.Vertical);
        Assert.Equal(96, engine.Limit);
        Assert.Equal(cancellation.Token, engine.Cancellation);
        Assert.Equal(1, engine.TryCalls);
    }

    [Fact]
    public async Task Realtime_BusyEngineRemainsNullAndDoesNotQueue()
    {
        using var bitmap = new Bitmap(80, 160);
        using var engine = new Engine { Busy = true };
        Assert.Null(await OcrService.TryRecognizeVerticalAsync(
            engine, bitmap, "JA", 96, CancellationToken.None));
        Assert.Equal(1, engine.TryCalls);
    }

    private sealed class Engine : IOcrEngine
    {
        public bool Busy { get; init; }
        public Bitmap? Image { get; private set; }
        public bool Vertical { get; private set; }
        public int? Limit { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public int TryCalls { get; private set; }

        public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string language,
            CancellationToken cancellationToken = default, bool verticalText = false) =>
            throw new InvalidOperationException("Realtime must not queue recognition.");

        public Task<List<OcrTextBlock>?> TryRecognizeAsync(Bitmap bitmap, string language,
            int? maxDetectSize = null, CancellationToken cancellationToken = default, bool verticalText = false)
        {
            TryCalls++;
            Image = bitmap;
            Vertical = verticalText;
            Limit = maxDetectSize;
            Cancellation = cancellationToken;
            return Task.FromResult<List<OcrTextBlock>?>(Busy ? null : []);
        }

        public void Dispose() { }
    }
}
