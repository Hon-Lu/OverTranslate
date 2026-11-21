using OverTranslate.Models;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services;

/// <summary>The glossary a request into one language is sent with.</summary>
/// <param name="Matcher">The entries, ready to be found in a text.</param>
/// <param name="Key">
/// Different whenever what would be sent could be different — another glossary, another language,
/// an entry edited — for keys of what was translated with it.
/// </param>
internal sealed record ActiveGlossary(GlossaryMatcher Matcher, string Key);

/// <summary>Which glossary, if any, a request is sent with — see <see cref="GlossarySettings"/>.</summary>
internal static class GlossaryRuntime
{
    private static readonly object Gate = new();
    private static GlossaryList? _lastList;
    private static ActiveGlossary? _last;

    /// <summary>
    /// The glossary in use when it is for <paramref name="targetLanguage"/> and has an entry; null
    /// otherwise — none chosen, or written for another language.
    /// </summary>
    /// <remarks>
    /// The language is that of the request, not the interface's or the main window's: 截圖翻譯 and
    /// 即時翻譯 can translate into different languages, and each request is matched on its own.
    /// Built once per list instance — the settings replace a list whenever it is edited, see
    /// <see cref="GlossarySettings"/> — so a screen translated a pass at a time does not sort a
    /// thousand entries every pass.
    /// </remarks>
    public static ActiveGlossary? For(GlossarySettings settings, string targetLanguage)
    {
        var list = settings.Active;
        if (list is null || list.Entries.Count == 0) return null;
        if (!string.Equals(list.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase)) return null;

        lock (Gate)
        {
            if (!ReferenceEquals(list, _lastList))
            {
                var terms = list.Entries.Select(entry => new GlossaryTerm(entry.Source, entry.Target)).ToList();
                _last = new ActiveGlossary(
                    new GlossaryMatcher(terms),
                    list.Id + "/" + list.TargetLanguage + "/" + GlossaryMatcher.Fingerprint(terms));
                _lastList = list;
            }

            return _last;
        }
    }

    /// <summary><paramref name="prompt"/> with the glossary for its request, or as it is when there is none.</summary>
    public static OpenAiPrompt With(OpenAiPrompt prompt, ActiveGlossary? glossary) =>
        glossary is null ? prompt : prompt with { Glossary = glossary.Matcher };
}
