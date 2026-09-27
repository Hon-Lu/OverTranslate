using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// The last recognition of a capture, kept so that translating the same crop again does not read it
/// again.
/// </summary>
/// <remarks>
/// <para>Recognition is the slowest step of a capture, and the commonest second press of 翻譯 is
/// the same picture into another language — nothing recognition sees has changed. So a result is
/// handed back when everything recognition is given is what it was given last time: the same crop,
/// the same language, the same orientation and the same grouping.</para>
///
/// <para>The crop is told by where it was cut from, not by the bitmap it was cut into. A capture
/// window holds one screenshot for its whole life, so the same window and the same region are the
/// same pixels. The bitmap would not do: 複製原文 hands the frame back to the user when it is done,
/// and the next press of 翻譯 cuts a new bitmap out of the very same region.</para>
/// </remarks>
internal sealed class CaptureOcrReuse
{
    private sealed record Entry(
        object Capture, Rect Region, string Language, bool VerticalText, CaptureLayoutMode LayoutMode,
        List<OcrTextBlock> Blocks);

    private Entry? _last;

    /// <param name="capture">What holds the screenshot the crop was cut from.</param>
    /// <param name="region">Where in it the crop was cut.</param>
    /// <param name="layoutMode">The mode as recognition receives it, after the application's policy.</param>
    public bool TryGet(
        object capture, Rect region, string sourceLanguage, bool verticalText, CaptureLayoutMode layoutMode,
        out List<OcrTextBlock> blocks)
    {
        if (_last is { } last &&
            ReferenceEquals(last.Capture, capture) &&
            last.Region == region &&
            last.Language == OcrLanguageRouter.Normalize(sourceLanguage) &&
            last.VerticalText == verticalText &&

            // Vertical text has one grouping profile of its own and never looks at the mode.
            (verticalText || last.LayoutMode == layoutMode))
        {
            blocks = last.Blocks;
            return true;
        }

        blocks = [];
        return false;
    }

    /// <remarks>
    /// Nothing found is not kept: the frame goes back to the user to be redrawn, and whatever comes
    /// next is recognised afresh anyway.
    /// </remarks>
    public void Remember(
        object capture, Rect region, string sourceLanguage, bool verticalText, CaptureLayoutMode layoutMode,
        List<OcrTextBlock> blocks)
    {
        _last = blocks.Count == 0
            ? null
            : new Entry(capture, region, OcrLanguageRouter.Normalize(sourceLanguage), verticalText, layoutMode, blocks);
    }

    public void Clear() => _last = null;
}
