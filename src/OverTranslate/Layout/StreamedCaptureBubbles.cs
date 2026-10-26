using OverTranslate.Services;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Layout;

/// <summary>
/// The bubbles of one capture translation while its translations are still arriving: where each
/// one goes and what colour it is, settled before the first translation exists, and the text put
/// into them one block at a time.
/// </summary>
/// <remarks>
/// <para>Everything the overlay needs besides the text depends on the boxes and on nothing else.
/// Placement splits a group back onto its lines by geometry and only shares the text out between
/// them; the colours are read from the picture under the boxes; the backdrop is repaired under the
/// same boxes. So all of it is worked out once, as soon as recognition is done, on blocks that
/// still carry their source text where the translation will go — and a translation that arrives is
/// dropped into boxes that are already coloured, rather than waiting for a pass over the crop.</para>
///
/// <para>Placed one block at a time, which gives the same pieces as placing the whole list: every
/// branch of <see cref="OverlayPlacement.Place"/> maps each block to its own pieces and looks at
/// no other. That is what lets block <c>i</c> be re-placed on its own when its translation comes.</para>
///
/// <para>A block whose translation has not arrived is still in the list the overlay is handed,
/// marked pending: not drawn, but there to be asked about by its neighbours, so a bubble does not
/// grow into room that a later one is about to take.</para>
///
/// <para>Arrivals may come from any thread; everything else is the UI thread's.</para>
/// </remarks>
internal sealed class StreamedCaptureBubbles
{
    private readonly IReadOnlyList<OcrTextBlock> _source;
    private readonly CaptureLayoutMode _mode;
    private readonly bool _verticalText;
    private readonly string _targetLanguage;

    /// <summary>Each source block's pieces, placed and — once <see cref="Colour"/> has run — coloured.</summary>
    private readonly List<TranslatedBlock>[] _shapes;

    /// <summary>Each block's pieces with its translation in them, or null until it arrives.</summary>
    private readonly List<TranslatedBlock>?[] _arrived;

    /// <summary>The text each arrived block was last placed with, to tell a repeat from a change.</summary>
    private readonly string?[] _texts;

    private readonly object _incomingLock = new();
    private readonly Dictionary<int, string> _incoming = [];
    private volatile bool _streamed;

    public StreamedCaptureBubbles(
        IReadOnlyList<OcrTextBlock> source, CaptureLayoutMode mode, bool verticalText, string targetLanguage)
    {
        _source = source;
        _mode = mode;
        _verticalText = verticalText;
        _targetLanguage = targetLanguage;
        _shapes = [.. source.Select(block => Place(Translated(block, block.Text)))];
        _arrived = new List<TranslatedBlock>?[source.Count];
        _texts = new string?[source.Count];
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
    /// Notes a translation for block <paramref name="index"/>, from whichever thread it arrived on.
    /// Nothing is placed until <see cref="TakeArrivals"/>; a later arrival for the same block
    /// replaces an earlier one still waiting.
    /// </summary>
    public void Arrive(int index, string translation)
    {
        lock (_incomingLock) _incoming[index] = translation;
        _streamed = true;
    }

    /// <summary>
    /// Whether any translation arrived before the whole answer. An engine that answers all at once
    /// never says anything until it is done, and its bubbles appear as they always did, without
    /// fading in — see <see cref="Final"/>.
    /// </summary>
    public bool Streamed => _streamed;

    /// <summary>
    /// Places what arrived since the last call, and says which blocks changed and which of those
    /// are on screen for the first time.
    /// </summary>
    public (bool Changed, IReadOnlyList<int> FirstShown) TakeArrivals()
    {
        KeyValuePair<int, string>[] incoming;
        lock (_incomingLock)
        {
            incoming = [.. _incoming];
            _incoming.Clear();
        }

        var changed = false;
        var firstShown = new List<int>();
        foreach (var (index, text) in incoming)
        {
            if (index < 0 || index >= _source.Count || string.IsNullOrWhiteSpace(text)) continue;
            if (_texts[index] == text) continue;

            if (_arrived[index] is null) firstShown.Add(index);
            _texts[index] = text;
            _arrived[index] = Coloured(index, Place(Translated(_source[index], text)));
            changed = true;
        }

        return (changed, firstShown);
    }

    /// <summary>Whether block <paramref name="index"/> is on screen with a translation.</summary>
    public bool IsShown(int index) => _arrived[index] is not null;

    /// <summary>
    /// The list the overlay draws: arrived blocks with their translation, the rest pending.
    /// </summary>
    /// <param name="firstShown">Blocks whose pieces are to fade in, from <see cref="TakeArrivals"/>.</param>
    public (List<TranslatedBlock> Blocks, IReadOnlyCollection<TranslatedBlock> Pending, IReadOnlyCollection<TranslatedBlock> Arriving)
        Current(IReadOnlyCollection<int> firstShown)
    {
        var blocks = new List<TranslatedBlock>();
        var pending = new HashSet<TranslatedBlock>(ReferenceEqualityComparer.Instance);
        var arriving = new HashSet<TranslatedBlock>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _source.Count; i++)
        {
            var pieces = _arrived[i] ?? _shapes[i];
            blocks.AddRange(pieces);
            if (_arrived[i] is null) pending.UnionWith(pieces);
            else if (firstShown.Contains(i)) arriving.UnionWith(pieces);
        }

        return (blocks, pending, arriving);
    }

    /// <summary>
    /// The finished overlay from the provider's answer, in the colours already sampled; null when
    /// the answer does not line up block for block with what was read.
    /// </summary>
    /// <remarks>
    /// <para>The answer is what is drawn, not what streamed: a provider may read the whole answer
    /// differently from a piece of it, and a block that failed comes back as its source text.</para>
    ///
    /// <para>What fades in is what streamed blocks had not shown yet. When nothing streamed at all,
    /// nothing fades: the whole overlay appears at once, as it did before anything streamed.</para>
    /// </remarks>
    public (List<TranslatedBlock> Blocks, IReadOnlyCollection<TranslatedBlock> Arriving)? Final(
        IReadOnlyList<TranslatedBlock> translated)
    {
        if (translated.Count != _source.Count) return null;

        var blocks = new List<TranslatedBlock>();
        var arriving = new HashSet<TranslatedBlock>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < translated.Count; i++)
        {
            var pieces = Coloured(i, Place(translated[i]));
            if (pieces.Count != _shapes[i].Count) return null;

            blocks.AddRange(pieces);
            if (Streamed && !IsShown(i)) arriving.UnionWith(pieces);
        }

        return (blocks, arriving);
    }

    private List<TranslatedBlock> Place(TranslatedBlock block) =>
        OverlayPlacement.Place([block], _mode, _verticalText, _targetLanguage);

    /// <summary>Block <paramref name="index"/>'s pieces in the colours its shape was given.</summary>
    private List<TranslatedBlock> Coloured(int index, List<TranslatedBlock> pieces)
    {
        var shapes = _shapes[index];
        return [.. pieces.Select((piece, i) => i < shapes.Count
            ? piece with { BackgroundColor = shapes[i].BackgroundColor, TextColor = shapes[i].TextColor }
            : piece)];
    }

    /// <summary>A source block holding <paramref name="text"/>, built the way the providers build theirs.</summary>
    private static TranslatedBlock Translated(OcrTextBlock block, string text) =>
        new(block.Text, text, block.Bounds, block.Lines, block.RenderGlyphHeight)
        {
            RunsAcross = block.RunsAcross,
            FontGlyphHeight = block.FontGlyphHeight,
            Tilt = block.Tilt,
        };
}
