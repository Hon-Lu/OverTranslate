using System.Drawing;

namespace OverTranslate.Services.Ocr;

internal interface IOcrEngine : IDisposable
{
    Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        bool verticalText = false);

    /// <summary>
    /// Recognises only if a slot is free right now, returning null instead of queueing.
    /// </summary>
    /// <remarks>
    /// For callers watching a live screen, where queueing is worse than skipping: by the time a
    /// queued pass reached the front the frame it was taken from would be several frames stale, and
    /// waiting for it would delay the frame that replaced it. A screenshot, which the user is
    /// waiting on and which will not come round again, uses <see cref="RecognizeAsync"/> instead.
    /// </remarks>
    /// <param name="maxDetectSize">
    /// Longest side to hand the text detector, or null for the default. Only ever downscales.
    /// </param>
    Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default,
        bool verticalText = false);

    /// <summary>
    /// A recognition slot taken now, for a caller that has to know it can read before it starts
    /// spending anything else; null when none is free.
    /// </summary>
    /// <remarks>
    /// For the manga path on a live screen, which reads part of a page with the manga models and
    /// part with this engine: finding the engine busy only after the manga models had read their
    /// part would throw that work away. An engine without slots of its own reads straight through.
    /// </remarks>
    IOcrSlot? TryTakeSlot() => new ThroughSlot(this);

    /// <summary>A recognition slot, waited for.</summary>
    Task<IOcrSlot> TakeSlotAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IOcrSlot>(new ThroughSlot(this));

    private sealed class ThroughSlot(IOcrEngine engine) : IOcrSlot
    {
        public Task<List<OcrTextBlock>> RecognizeAsync(
            Bitmap bitmap, string sourceLanguage, int? maxDetectSize, CancellationToken cancellationToken,
            bool verticalText) =>
            engine.RecognizeAsync(bitmap, sourceLanguage, cancellationToken, verticalText);

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// One of the engine's recognition slots, held until disposed; every read through it runs at once,
/// one after another, without asking for a slot again.
/// </summary>
internal interface IOcrSlot : IDisposable
{
    /// <param name="maxDetectSize">As <see cref="IOcrEngine.TryRecognizeAsync"/>; null is the screenshot size.</param>
    Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize, CancellationToken cancellationToken,
        bool verticalText);
}
