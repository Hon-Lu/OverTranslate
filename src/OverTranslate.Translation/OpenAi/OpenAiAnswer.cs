using System.Text.RegularExpressions;

namespace OverTranslate.Translation.OpenAi;

/// <summary>An answer to one text read back into its translation.</summary>
internal static class OpenAiAnswer
{
    private static readonly Regex OpeningTag = new(
        @"<t\s+id\s*=\s*[""']?(\d+)[""']?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClosingTag = new(
        @"</t\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Wrapped = new(
        @"^<t>(?<body>.*)</t>$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Fence = new(
        @"^```[a-zA-Z]*\n(.*?)\n?```$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Quotation marks a model can put around the whole of its answer, as pairs.</summary>
    private static readonly (char Open, char Close)[] Quotes = [('"', '"'), ('“', '”'), ('「', '」'), ('『', '』')];

    /// <summary>The translation in <paramref name="answer"/>, or null when there is none.</summary>
    /// <param name="source">The text that was asked for, to tell a quote it had from one the model added.</param>
    /// <remarks>
    /// <para>The whole answer, less its thinking, is the translation — asked for nothing else, a model
    /// mostly gives nothing else. What it does sometimes add is taken off: a code fence around all of
    /// it; a <c>&lt;t&gt;</c> around all of it, the tag the context gives a translation, when there is
    /// no other tag inside; a segment tag, a habit of the tagged format this replaced — the last one
    /// numbered 1, and none at all when every tag is numbered otherwise; and a pair of quotation marks around all of
    /// it, when the source has neither mark anywhere — a source that opens a title and leaves it
    /// open, 「アイライク ユアライク, is rightly answered with the title closed.</para>
    /// </remarks>
    public static string? Read(string answer, string source)
    {
        var text = OpenAiChatTranslator.StripThinking(answer);

        var fence = Fence.Match(text);
        if (fence.Success) text = fence.Groups[1].Value.Trim();

        var wrapped = Wrapped.Match(text);
        if (wrapped.Success && !OpenAiPrompt.LeaksTags("", wrapped.Groups["body"].Value))
            text = wrapped.Groups["body"].Value.Trim();

        if (OpeningTag.IsMatch(text))
        {
            text = LastFirstTag(text) ?? "";
            if (text.Length == 0) return null;
        }

        var trimmed = source.Trim();
        foreach (var (open, close) in Quotes)
        {
            if (text.Length >= 2 && text[0] == open && text[^1] == close
                && text.IndexOf(close, 1) == text.Length - 1
                && !trimmed.Contains(open) && !trimmed.Contains(close))
            {
                text = text[1..^1].Trim();
                break;
            }
        }

        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// The text of the last tag numbered 1, up to its closing tag — or, when the model never closed
    /// it, to the end of its line or the next tag.
    /// </summary>
    private static string? LastFirstTag(string text)
    {
        var openings = OpeningTag.Matches(text);
        for (var i = openings.Count - 1; i >= 0; i--)
        {
            if (openings[i].Groups[1].Value != "1") continue;

            var start = openings[i].Index + openings[i].Length;
            var next = i + 1 < openings.Count ? openings[i + 1].Index : text.Length;
            var closing = ClosingTag.Match(text, start);
            int end;
            if (closing.Success && closing.Index <= next)
                end = closing.Index;
            else
            {
                var lineEnd = text.IndexOf('\n', start);
                end = lineEnd >= 0 && lineEnd < next ? lineEnd : next;
            }

            return text[start..end].Trim();
        }

        return null;
    }
}
