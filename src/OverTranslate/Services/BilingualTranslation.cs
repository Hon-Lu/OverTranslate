using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>What 雙語互譯 settled on: the answer to show and how it got there.</summary>
/// <param name="Blocks">The translation, as the engine returned it.</param>
/// <param name="DetectedLang">What the engine said about the text on the call that produced it.</param>
/// <param name="Target">The language <paramref name="Blocks"/> is in.</param>
/// <param name="Source">
/// The language the text is taken to be in, for the header; null when nothing could be told.
/// </param>
internal sealed record BilingualAnswer(
    List<TranslatedBlock> Blocks, string DetectedLang, string Target, string? Source);

/// <summary>
/// One 雙語互譯 translation from start to finish, shared by 取詞翻譯 and 替換翻譯.
/// </summary>
/// <remarks>
/// Three guesses meet here, and each can be wrong. The characters say which of the pair the text is
/// in (<see cref="BilingualDirection"/>); the engine then says what it read the text as, and that can
/// send it the other way (<see cref="BilingualPair.Correct"/>); and the engine's answer itself can be
/// the text handed back unchanged, which is what both of the others look like when they are wrong.
///
/// That last one is the tell this leans on, because it needs no guess at all (#339). Engines misread
/// single words all the time — 「вода」 as Macedonian, 「水」 as Chinese under 日文, 「Hund」 as
/// Norwegian — and an answer that is the text itself means only that the text was sent into the
/// language it is already in. So:
/// <list type="bullet">
/// <item>A correction whose answer is the text itself is not taken; the first answer stands.</item>
/// <item>An answer that is still the text itself is sent once into the other language of the pair,
/// which is where a same-script pair (德文 ⇄ 英文) lands when the characters could not tell and the
/// first language was the wrong one. That one request names its source rather than leaving it
/// automatic: the echo has just shown which language the text is in, and an engine left to guess
/// again reads 「chat」 or 「bonjour」 as English and hands them back a second time.</item>
/// </list>
/// Every language is asked for at most once, so a name or a number, which translates to itself
/// everywhere, costs one request more than it did and then stops.
///
/// In one place because both features promise the same thing, and a rule fixed in one of them and
/// not the other is the pair of features drifting apart that <see cref="BilingualPair"/> exists to
/// prevent.
/// </remarks>
internal static class BilingualTranslation
{
    /// <param name="translate">
    /// Sends the text from a source language into a target one; a null source means automatic.
    /// Both are target codes, as the pair is.
    /// </param>
    public static async Task<BilingualAnswer> TranslateAsync(
        string text, BilingualPair pair,
        Func<string?, string, Task<(List<TranslatedBlock> Blocks, string DetectedLang)>> translate)
    {
        var guessed = BilingualDirection.ResolveSource(text, pair.First, pair.Second);
        var target = pair.TargetFor(guessed);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };

        var (blocks, detected) = await translate(null, target);
        var answer = new BilingualAnswer(blocks, detected, target, guessed);

        var corrected = pair.Correct(detected, target, out var correctedSource);
        if (corrected is not null && tried.Add(corrected))
        {
            var (retried, retriedDetected) = await translate(null, corrected);
            if (BilingualPair.IsWorthKeeping(text, TextOf(retried)))
                answer = new BilingualAnswer(retried, retriedDetected, corrected, correctedSource ?? guessed);
        }
        else if (correctedSource is not null)
        {
            answer = answer with { Source = correctedSource };
        }

        if (BilingualPair.IsWorthKeeping(text, TextOf(answer.Blocks))) return answer;

        var other = pair.Other(answer.Target);
        if (other is null || !tried.Add(other)) return answer;

        var (alternative, alternativeDetected) = await translate(answer.Target, other);
        return BilingualPair.IsWorthKeeping(text, TextOf(alternative))
            ? new BilingualAnswer(alternative, alternativeDetected, other, answer.Target)
            : answer;
    }

    /// <summary>
    /// The source code to send for a language <see cref="TranslateAsync"/> names, or automatic for
    /// none — and for a language with no source code of its own, rather than fail the request.
    /// </summary>
    public static string SourceCode(string? language) =>
        language is null
            ? LanguageData.AutomaticSourceLanguage
            : LanguageData.MapTargetToSourceCode(language) ?? LanguageData.AutomaticSourceLanguage;

    private static string? TextOf(List<TranslatedBlock> blocks) => blocks.FirstOrDefault()?.TranslatedText;
}
