using System.Windows;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using OverTranslate.Views.Capture;

namespace OverTranslate.Services;

/// <summary>
/// What a translated capture has on screen: everything the bubbles, the copy and speak buttons, the
/// 過濾數字與縮寫 switch and the backup badge read from.
/// </summary>
/// <remarks>
/// Held as one value so that taking it off the screen and putting it back are each one step: the
/// pieces only mean anything together, and a restore that missed one of them would copy or read
/// aloud something other than what the bubbles show.
/// </remarks>
internal sealed record CaptureResult(
    List<OcrTextBlock> OcrBlocks,
    List<TranslatedBlock> TranslatedBlocks,
    List<TranslatedBlock> ColoredBlocks,
    CaptureBubbleBackdrop? Backdrop,
    bool VerticalText,
    string SourceLang,
    string TargetLang,
    CaptureFilteredTranslation? Filter,
    CaptureBubbleShapes? FilterShapes,
    TranslateRequest? FilterRequest,
    EngineUsage? EngineUsage)
{
    /// <summary>Nothing translated: what a capture holds before its first translation.</summary>
    public static CaptureResult Empty(string targetLang) =>
        new([], [], [], null, false, "", targetLang, null, null, null, null);

    public bool HasTranslation => ColoredBlocks.Count > 0;
}

/// <summary>
/// The capture as it was translated, the moment 編輯框選範圍 hands the box back — so that pressing
/// 翻譯 without having changed anything puts that translation straight back up instead of reading
/// and translating the same picture a second time.
/// </summary>
/// <remarks>
/// <para>Unchanged means every input the translation was made from is the same: the box to the
/// physical pixel, the two languages, the direction, and the service. A different box is a
/// different picture; anything else would make the engine say something different about the same
/// picture, which is what the user changed it for.</para>
///
/// <para>Kept until a translation actually runs, not until the box is first touched: a box dragged
/// away and back to the pixel it started on is the same box.</para>
/// </remarks>
internal sealed class CaptureReframe
{
    internal sealed record Inputs(
        Rect Selection, string SourceLang, string TargetLang, bool VerticalText, TranslationProvider Provider);

    private (Inputs Inputs, CaptureResult Result)? _held;

    /// <summary>Whether a translation is being held for the box being edited.</summary>
    public bool IsHolding => _held is not null;

    /// <summary>Holds <paramref name="result"/>, made from <paramref name="inputs"/>, while the box is edited.</summary>
    public void Hold(Inputs inputs, CaptureResult result) => _held = (inputs, result);

    /// <summary>
    /// The translation held for these inputs, given up in the same step; null — and the held one
    /// dropped — when anything differs, since a translation is about to replace it.
    /// </summary>
    public CaptureResult? Take(Inputs inputs)
    {
        var held = _held;
        _held = null;
        return held is { } h && h.Inputs == inputs ? h.Result : null;
    }

    /// <summary>Drops whatever is held: the session has moved on.</summary>
    public void Clear() => _held = null;
}
