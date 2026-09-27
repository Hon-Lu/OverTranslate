using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Translation.Google;

/// <summary>
/// 「Google 翻譯 (RPC)」: the call translate.google.com makes for itself, several to a request.
/// </summary>
/// <remarks>
/// <para><c>batchexecute</c> is Google's envelope for its web apps' internal calls, and it takes a
/// list of them. GTranslate put one translation (<c>MkEWBc</c>) in each envelope; this puts one per
/// text in the same envelope, each tagged with its position, and matches the answers back by that
/// tag — they come back in whatever order the server finished them. Fourteen test sentences came
/// back identical to GTranslate's one-at-a-time answers, and 200 in one envelope was accepted.</para>
///
/// <para>The request format is GTranslate's (<c>GoogleTranslator2</c>, MIT); so is reading the
/// answer out of the nested arrays, which is where all of this endpoint's fragility lives. Every
/// position read below is one Google can move without notice, and when it does the whole request
/// fails as unreadable rather than returning something misplaced.</para>
/// </remarks>
public sealed class GoogleRpcTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Endpoint =
        "https://translate.google.com/_/TranslateWebserverUi/data/batchexecute?rpcids=" + RpcId;
    private const string RpcId = "MkEWBc";

    public override string Name => "Google (RPC)";

    protected override int MaxItemsPerRequest => 50;

    protected override int MaxCharactersPerRequest => 5000;

    /// <remarks>The same detector as 「Google 翻譯 (Web)」, and the same sentence left.</remarks>
    protected override bool RescuesMixedScript => true;

    /// <remarks>
    /// A text of several sentences comes back as a list of them, and the blank line between two
    /// paragraphs is not in it. Sent apart, they come back apart and are put back with it.
    /// </remarks>
    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) =>
        SplitParagraphs(text, joinLines: false);

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var calls = pieces.Select((piece, i) => new object?[]
        {
            RpcId,
            JsonSerializer.Serialize(new object[]
            {
                new object[] { piece, sourceLanguage ?? "auto", targetLanguage, 1 },
                Array.Empty<object>(),
            }),
            null,

            // The tag each answer comes back with. Positions start at 1 because "generic", which is
            // what a single call is tagged with, is the only other thing that has been seen here.
            (i + 1).ToString(CultureInfo.InvariantCulture),
        }).ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(
                [new KeyValuePair<string, string>("f.req", JsonSerializer.Serialize(new object[] { calls }))]),
        };

        var body = await ReadAsync(request, cancellationToken);

        // The answer opens with )]}' to stop it being run as a script; the JSON starts after it.
        var start = body.IndexOf('[');
        if (start < 0) throw new TranslationEngineException(Name, "unexpected answer");
        using var document = JsonDocument.Parse(body.AsMemory(start));

        var answers = new TextTranslation?[pieces.Count];
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 7 ||
                entry[0].ValueKind != JsonValueKind.String || entry[0].GetString() != "wrb.fr")
                continue;   // bookkeeping entries ("di", "af.httprm") ride along with the answers

            var tag = entry[6].ValueKind == JsonValueKind.String ? entry[6].GetString() : null;
            if (!int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var position) ||
                position < 1 || position > pieces.Count)
                throw new TranslationEngineException(Name, "answer with an unknown tag");

            // A call the server refused comes back with no data and an error code further along —
            // [13], INTERNAL, at random: one or two calls in a hundred, however many share the
            // envelope, one call alone included (measured 2026-09-27). It is left unanswered and
            // asked again on its own rather than failing the texts that were answered.
            if (entry[2].ValueKind != JsonValueKind.String)
                continue;

            answers[position - 1] = Read(entry[2].GetString()!, sourceLanguage);
        }

        return answers;
    }

    /// <summary>One call's answer, which is itself JSON inside a string.</summary>
    private static TextTranslation Read(string data, string? sourceLanguage)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var result = root[1][0][0];

        // Longer texts come back as a list of sentence pieces in [5]; short ones, links and
        // gender-specific answers as one string in [0].
        string translation;
        if (result.GetArrayLength() > 5 && result[5].ValueKind == JsonValueKind.Array)
        {
            translation = JoinSentences(result[5].EnumerateArray()
                .Select(sentence => RequireString(sentence[0])));
        }
        else
        {
            translation = RequireString(result[0]);
        }

        var detected = "";
        if (sourceLanguage is null)
        {
            var reported = root[1][3].ValueKind == JsonValueKind.String ? root[1][3].GetString() : null;
            if (reported == "auto" && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                reported = root[2].GetString();
            if (!string.IsNullOrEmpty(reported) && reported != "auto")
                detected = LanguageCodes.FromGoogle(reported);
        }

        return new TextTranslation(translation, detected);
    }

    /// <summary>
    /// Puts the sentence pieces back together, with a space between them only where the language
    /// uses one.
    /// </summary>
    /// <remarks>
    /// GTranslate joined them with a space every time, which is right for English and left
    /// 「連線遺失。 重試。」 in Chinese — a gap the original never had and nobody would write.
    /// </remarks>
    internal static string JoinSentences(IEnumerable<string> sentences)
    {
        var joined = new StringBuilder();
        foreach (var sentence in sentences)
        {
            var part = sentence.Trim();
            if (part.Length == 0) continue;

            if (joined.Length > 0 && !IsCloseSet(joined[^1]) && !IsCloseSet(part[0]))
                joined.Append(' ');

            joined.Append(part);
        }

        return joined.ToString();
    }

    /// <summary>Whether a character belongs to text set without spaces between sentences.</summary>
    /// <remarks>
    /// Not Hangul: Korean puts a space between sentences like English does, which is why this is
    /// not <see cref="TranslationRequestChunks"/>' list.
    /// </remarks>
    private static bool IsCloseSet(char character) =>
        character is >= '⺀' and <= '鿿'    // radicals, kana, bopomofo, Han
            or >= '豈' and <= '﫿'          // compatibility ideographs
            or >= '＀' and <= '￯';         // full-width forms and CJK punctuation
}
