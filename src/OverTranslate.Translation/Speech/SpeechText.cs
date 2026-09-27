namespace OverTranslate.Translation.Speech;

/// <summary>Cuts a text into pieces an engine will read, for the engines with a per-request limit.</summary>
internal static class SpeechText
{
    // Where a cut sounds least like one: the end of a sentence, then a pause, then a word break.
    private static readonly char[][] Breaks =
    [
        ['。', '！', '？', '!', '?', '.', '…', '；', ';'],
        ['，', '、', ',', '：', ':'],
        [' '],
    ];

    /// <remarks>
    /// Whitespace, line breaks included, becomes one space first — a voice reads a line break as
    /// nothing anyway. GTranslate cut at spaces only, so Chinese and Japanese, which have none, were
    /// cut wherever the limit fell, mid-word; here they are cut at their punctuation.
    /// </remarks>
    public static List<string> Split(string text, int limit)
    {
        var rest = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var pieces = new List<string>();

        while (rest.Length > limit)
        {
            var window = rest.AsSpan(0, limit);
            var cut = -1;
            foreach (var breaks in Breaks)
            {
                var at = window.LastIndexOfAny(breaks);
                // A cut in the first half would leave a stub and a second piece nearly as long.
                if (at >= limit / 2) { cut = at + 1; break; }
            }

            if (cut < 0)
            {
                cut = limit;
                if (char.IsHighSurrogate(rest[cut - 1])) cut--;   // never between the halves of one character
            }

            pieces.Add(rest[..cut].Trim());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0) pieces.Add(rest);
        return pieces;
    }
}
