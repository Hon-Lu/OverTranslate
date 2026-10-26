using OverTranslate.Services;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Layout;

/// <summary>
/// The bubbles of one capture translation, settled before the translation exists: where each one
/// goes and what colour it is, so the backdrop can be repaired while the text is still on its way.
/// </summary>
/// <remarks>
/// <para>Everything the overlay needs besides the text depends on the boxes and on nothing else.
/// Placement splits a group back onto its lines by geometry and only shares the text out between
/// them; the colours are read from the picture under the boxes; the backdrop is repaired under the
/// same boxes. So all of it is worked out once, as soon as recognition is done, on blocks that
/// still carry their source text where the translation will go — and the answer is dropped into
/// boxes that are already coloured, over a backdrop that is already repaired.</para>
///
/// <para>Placed one block at a time, which gives the same pieces as placing the whole list: every
/// branch of <see cref="OverlayPlacement.Place"/> maps each block to its own pieces and looks at
/// no other. That is what lets each block of the answer be matched to its own shapes.</para>
/// </remarks>
internal sealed class CaptureBubbleShapes
{
    private readonly CaptureLayoutMode _mode;
    private readonly bool _verticalText;
    private readonly string _targetLanguage;

    /// <summary>Each source block's pieces, placed and — once <see cref="Colour"/> has run — coloured.</summary>
    private readonly List<TranslatedBlock>[] _shapes;

    public CaptureBubbleShapes(
        IReadOnlyList<OcrTextBlock> source, CaptureLayoutMode mode, bool verticalText, string targetLanguage)
    {
        _mode = mode;
        _verticalText = verticalText;
        _targetLanguage = targetLanguage;
        _shapes = [.. source.Select(block => Place(Translated(block)))];
    }

    /// <summary>
    /// Every piece in drawing order, holding its source text: what the colours are sampled for and
    /// the backdrop is repaired under. The same boxes, in the same order, as the finished overlay.
    /// </summary>
    public IReadOnlyList<TranslatedBlock> Shapes => [.. _shapes.SelectMany(pieces => pieces)];

    /// <summary>Takes the coloured <see cref="Shapes"/> back, piece for piece.</summary>
    public void Colour(IReadOnlyList<TranslatedBlock> coloured)
    {
        if (coloured.Count != _shapes.Sum(pieces => pieces.Count))
            throw new ArgumentException("One colour per piece.", nameof(coloured));

        var next = 0;
        foreach (var pieces in _shapes)
            for (var i = 0; i < pieces.Count; i++)
                pieces[i] = coloured[next++];
    }

    /// <summary>
    /// The finished overlay from the provider's answer, in the colours already sampled; null when
    /// the answer does not line up block for block with what was read.
    /// </summary>
    public List<TranslatedBlock>? Final(IReadOnlyList<TranslatedBlock> translated)
    {
        if (translated.Count != _shapes.Length) return null;

        var blocks = new List<TranslatedBlock>();
        for (var i = 0; i < translated.Count; i++)
        {
            var pieces = Place(translated[i]);
            var shapes = _shapes[i];
            if (pieces.Count != shapes.Count) return null;

            blocks.AddRange(pieces.Select((piece, k) =>
                piece with { BackgroundColor = shapes[k].BackgroundColor, TextColor = shapes[k].TextColor }));
        }

        return blocks;
    }

    private List<TranslatedBlock> Place(TranslatedBlock block) =>
        OverlayPlacement.Place([block], _mode, _verticalText, _targetLanguage);

    /// <summary>A source block holding its own text, built the way the providers build theirs.</summary>
    private static TranslatedBlock Translated(OcrTextBlock block) =>
        new(block.Text, block.Text, block.Bounds, block.Lines, block.RenderGlyphHeight)
        {
            RunsAcross = block.RunsAcross,
            FontGlyphHeight = block.FontGlyphHeight,
            Tilt = block.Tilt,
        };
}
