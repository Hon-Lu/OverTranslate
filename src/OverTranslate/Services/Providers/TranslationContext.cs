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
/// <param name="SegmentTranslated">
/// Told each time a block's translation is known or changes, with its index in the blocks and the
/// translation, before the call returns: a provider that streams says so as each one arrives, and
/// may say it again for the same block when the whole answer reads it differently. The result the
/// call returns is still the whole answer. Called from whichever thread the answer arrived on, and
/// possibly from several at once.
/// </param>
/// <remarks>
/// Optional all through, and ignored by every provider but the OpenAI-compatible one: an engine
/// translates each text on its own and has nowhere to put the rest.
/// </remarks>
public sealed record TranslationContext(
    IReadOnlyList<TranslationPair>? History = null,
    IReadOnlyList<TranslationPair>? Reference = null,
    Action<int, string>? SegmentTranslated = null);
