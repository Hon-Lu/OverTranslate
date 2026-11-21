using System.Text;

namespace OverTranslate.Translation.OpenAi;

/// <summary>One entry of the user's glossary: how a name or term in the source is to be translated.</summary>
public sealed record GlossaryTerm(string Source, string Target);

/// <summary>
/// The user's glossary as a request uses it: finds which entries a text contains, so that only those
/// are sent — see <see cref="OpenAiPrompt.Glossary"/>.
/// </summary>
/// <remarks>
/// <para>Only the entries a text contains are sent, never the whole list. Measured against sending
/// every entry, both reached every expected name on two models, but the whole list grew with the
/// glossary — two hundred entries is some 2,700 tokens, past what a local model's 4096 can hold with
/// a screen — and a glossary left switched on for another work had its names in every request.</para>
///
/// <para>Matching is literal. Longer entries first, and an entry overlapping one already found is
/// skipped, so リオン is not also read as リオ. An entry with a Latin letter in it matches as a whole
/// word and with its case, so Hope is not found in Hopeless and Snow not in snow; the rest match
/// anywhere, as CJK has no spaces to find words by. What still matches by accident —
/// "Hope you're well", リオデジャネイロ — is harmless to a model: measured, it was given the entry and
/// translated the sentence as meant every time (0 of 8), which is why the glossary needs no way to
/// switch entries off per feature.</para>
/// </remarks>
public sealed class GlossaryMatcher
{
    /// <summary>How many entries one request carries at most.</summary>
    public const int MaxTermsPerRequest = 20;

    /// <summary>
    /// How many characters the entries of one request may take in all, sources and targets together.
    /// </summary>
    /// <remarks>
    /// The request's budget counts characters — see <c>OpenAiChatTranslator.Message</c> — and the
    /// local model's whole context is 4096 with a piece of up to 1000 characters and room for its
    /// answer. Twenty short names come to a few hundred; this only stops twenty long entries crowding
    /// the text out.
    /// </remarks>
    public const int MaxTermCharactersPerRequest = 600;

    private readonly GlossaryTerm[] _longestFirst;
    private readonly Dictionary<GlossaryTerm, int> _order;
    private readonly bool[] _wholeWord;

    /// <param name="terms">The entries, in the user's order; blank ones are left out, the last of a repeated source kept.</param>
    public GlossaryMatcher(IEnumerable<GlossaryTerm> terms)
    {
        var bySource = new Dictionary<string, GlossaryTerm>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var term in terms)
        {
            var source = term.Source.Trim();
            var target = term.Target.Trim();
            if (source.Length == 0 || target.Length == 0) continue;
            if (!bySource.ContainsKey(source)) order.Add(source);
            bySource[source] = new GlossaryTerm(source, target);
        }

        var kept = order.Select(source => bySource[source]).ToList();
        _order = kept.Select((term, i) => (term, i)).ToDictionary(x => x.term, x => x.i);
        _longestFirst = [.. kept.OrderByDescending(term => term.Source.Length).ThenBy(term => _order[term])];
        _wholeWord = [.. _longestFirst.Select(term => term.Source.Any(IsLatinLetter))];
    }

    /// <summary>How many entries there are.</summary>
    public int Count => _longestFirst.Length;

    /// <summary>The entries <paramref name="text"/> contains, in the order they first appear in it.</summary>
    public IReadOnlyList<GlossaryTerm> Find(string text) => Find([text]);

    /// <summary>
    /// The entries any of <paramref name="texts"/> contains — what a batch carries — in the order
    /// they first appear, up to <see cref="MaxTermsPerRequest"/> and
    /// <see cref="MaxTermCharactersPerRequest"/>: past either, the ones found first are kept.
    /// </summary>
    public IReadOnlyList<GlossaryTerm> Find(IReadOnlyList<string> texts)
    {
        if (_longestFirst.Length == 0) return [];

        // (text index, position) of each entry's first match, so the union reads in text order.
        var found = new Dictionary<GlossaryTerm, (int Text, int At)>();
        for (var t = 0; t < texts.Count; t++)
        {
            var text = texts[t];
            if (string.IsNullOrEmpty(text)) continue;

            var taken = new List<(int Start, int End)>();
            for (var i = 0; i < _longestFirst.Length; i++)
            {
                var term = _longestFirst[i];
                for (var at = text.IndexOf(term.Source, StringComparison.Ordinal);
                     at >= 0;
                     at = text.IndexOf(term.Source, at + 1, StringComparison.Ordinal))
                {
                    var end = at + term.Source.Length;
                    if (_wholeWord[i] && !IsWordBoundary(text, at, end)) continue;
                    if (taken.Any(span => at < span.End && span.Start < end)) continue;

                    taken.Add((at, end));
                    if (!found.ContainsKey(term)) found[term] = (t, at);
                }
            }
        }

        var result = new List<GlossaryTerm>();
        var characters = 0;
        foreach (var term in found.OrderBy(x => x.Value.Text).ThenBy(x => x.Value.At).Select(x => x.Key))
        {
            var size = term.Source.Length + term.Target.Length;
            if (result.Count == MaxTermsPerRequest || characters + size > MaxTermCharactersPerRequest) break;
            result.Add(term);
            characters += size;
        }

        return result;
    }

    /// <summary>Whether the match at [<paramref name="start"/>, <paramref name="end"/>) has no Latin letter or digit touching it.</summary>
    private static bool IsWordBoundary(string text, int start, int end) =>
        (start == 0 || !IsWordCharacter(text[start - 1])) &&
        (end == text.Length || !IsWordCharacter(text[end]));

    private static bool IsWordCharacter(char c) => IsLatinLetter(c) || char.IsAsciiDigit(c);

    /// <summary>A letter of the Latin script, accented ones included.</summary>
    private static bool IsLatinLetter(char c) =>
        char.IsAsciiLetter(c) || (c >= 'À' && c <= 'ɏ' && char.IsLetter(c));

    /// <summary>A short fingerprint of the entries, for telling one glossary's contents from another's.</summary>
    public static string Fingerprint(IEnumerable<GlossaryTerm> terms)
    {
        var hash = new HashCode();
        var count = 0;
        foreach (var term in terms)
        {
            hash.Add(term.Source, StringComparer.Ordinal);
            hash.Add(term.Target, StringComparer.Ordinal);
            count++;
        }

        return new StringBuilder().Append(count).Append(':').Append(hash.ToHashCode().ToString("x8")).ToString();
    }
}
