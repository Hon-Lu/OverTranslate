namespace OverTranslate.Translation;

/// <summary>A text cut into pieces short enough to send, and how to put their translations back.</summary>
/// <remarks>
/// The same cutting and joining the free engines go through (<see cref="TranslationRequestChunks"/>),
/// for a caller outside this library with a limit of its own.
/// </remarks>
public sealed class TextPieces
{
    private readonly List<TranslationRequestChunk> _chunks;

    private TextPieces(List<TranslationRequestChunk> chunks) => _chunks = chunks;

    /// <summary><paramref name="text"/> in pieces of at most <paramref name="limit"/> characters.</summary>
    public static TextPieces Cut(string text, int limit) => new(TranslationRequestChunks.Split(text, limit));

    public IReadOnlyList<string> Pieces => _chunks.Select(chunk => chunk.Text).ToList();

    /// <summary>One text from one translation per piece, in order.</summary>
    public string Join(IReadOnlyList<string> translated) => TranslationRequestChunks.Join(_chunks, translated);
}
