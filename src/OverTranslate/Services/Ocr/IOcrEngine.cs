using System.Drawing;

namespace OverTranslate.Services.Ocr;

internal interface IOcrEngine : IDisposable
{
    Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default, bool verticalText = false);

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
        CancellationToken cancellationToken = default, bool verticalText = false);
}
