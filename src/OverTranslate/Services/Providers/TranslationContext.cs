namespace OverTranslate.Services.Providers;

/// <summary>A line already translated: what was on screen, and what it became.</summary>
public sealed record TranslationPair(string Source, string Translation);

/// <summary>
/// What a caller knows about a translation beyond its blocks, for a provider that can use it.
/// </summary>
/// <param name="History">
/// Earlier lines of the same session, oldest first: what a model reads to keep names and subjects
/// the way they were. Never the blocks being translated.
/// </param>
/// <param name="Reference">
/// Lines on the same screen that are already translated and are not being sent: the screen around
/// the new lines, to be read and not translated.
/// </param>
/// <remarks>
/// Optional all through, and ignored by every provider but the OpenAI-compatible one: an engine
/// translates each text on its own and has nowhere to put the rest.
/// </remarks>
public sealed record TranslationContext(
    IReadOnlyList<TranslationPair>? History = null,
    IReadOnlyList<TranslationPair>? Reference = null);
