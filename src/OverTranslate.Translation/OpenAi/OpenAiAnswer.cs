using System.Text.RegularExpressions;

namespace OverTranslate.Translation.OpenAi;

/// <summary>
/// An answer read back into one translation per segment.
/// </summary>
/// <param name="Translations">One per segment, in order; null where the answer had none.</param>
/// <param name="Complete">Whether the ids were exactly 1 to n, each once.</param>
/// <param name="Tagged">Whether the answer had any segment tag in it at all.</param>
internal sealed record OpenAiAnswer(IReadOnlyList<string?> Translations, bool Complete, bool Tagged)
{
    private static readonly Regex OpeningTag = new(
        @"<t\s+id\s*=\s*[""']?(\d+)[""']?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClosingTag = new(
        @"</t\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Fence = new(
        @"```[a-zA-Z]*\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Reads <paramref name="answer"/> for <paramref name="count"/> segments.</summary>
    /// <remarks>
    /// <para>The tags are cut into runs — a new run starts wherever an id is not greater than the one
    /// before it — and the last run whose ids are exactly 1 to n is the answer. A model that echoes
    /// the texts, or the first half of its own answer, before the answer proper is read correctly
    /// that way; when no run is complete, the last one is still read, for what it has.</para>
    ///
    /// <para>A tag the model never closed ends at the end of its line, or at the next tag. Small
    /// models drop closing tags, and the line is where one segment ends anyway.</para>
    ///
    /// <para>Thinking and a code fence around the answer are taken off first.</para>
    /// </remarks>
    public static OpenAiAnswer Read(string answer, int count)
    {
        var text = WithoutFence(OpenAiChatTranslator.StripThinking(answer)).Trim();
        var tags = Tags(text);

        var runs = new List<List<(int Id, string Text)>>();
        foreach (var tag in tags)
        {
            if (runs.Count == 0 || tag.Id <= runs[^1][^1].Id) runs.Add([]);
            runs[^1].Add(tag);
        }

        var complete = runs.FindLastIndex(run => IsExactly(run, count));
        var chosen = complete >= 0 ? runs[complete] : runs.Count > 0 ? runs[^1] : [];

        var translations = new string?[count];
        foreach (var (id, translation) in chosen)
            if (id >= 1 && id <= count) translations[id - 1] = translation;

        return new OpenAiAnswer(translations, complete >= 0, tags.Count > 0);
    }

    /// <summary>Whether a run's ids are exactly 1 to <paramref name="count"/>, each once.</summary>
    private static bool IsExactly(List<(int Id, string Text)> run, int count) =>
        run.Count == count && run.Select(tag => tag.Id).Order().SequenceEqual(Enumerable.Range(1, count));

    /// <summary>Every segment tag in order, closed or not.</summary>
    private static List<(int Id, string Text)> Tags(string text)
    {
        var tags = new List<(int, string)>();
        var openings = OpeningTag.Matches(text);
        for (var i = 0; i < openings.Count; i++)
        {
            var start = openings[i].Index + openings[i].Length;
            var next = i + 1 < openings.Count ? openings[i + 1].Index : text.Length;
            if (!int.TryParse(openings[i].Groups[1].ValueSpan, out var id)) continue;

            var closing = ClosingTag.Match(text, start);
            int end;
            if (closing.Success && closing.Index <= next)
                end = closing.Index;
            else
            {
                var lineEnd = text.IndexOf('\n', start);
                end = lineEnd >= 0 && lineEnd < next ? lineEnd : next;
            }

            tags.Add((id, text[start..end].Trim()));
        }

        return tags;
    }

    /// <summary>The first fenced block's body, or the text as it is when it has none.</summary>
    private static string WithoutFence(string text)
    {
        var fence = Fence.Match(text);
        return fence.Success ? fence.Groups[1].Value : text;
    }
}
