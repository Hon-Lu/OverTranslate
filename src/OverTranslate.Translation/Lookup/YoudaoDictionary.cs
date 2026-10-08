using System.Text.Json;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Youdao's dictionary: <c>jsonapi_s</c>, what www.youdao.com's word pages read, with no key.
/// </summary>
/// <remarks>
/// <para>Its glosses are Chinese and only Chinese — simplified, whatever was asked — so it answers
/// for a Chinese target and has nothing for any other. For traditional Chinese the simplified gloss
/// is shown as it is: the card is a dictionary, not a translation, and a user who picked 有道 knows
/// whose dictionary it is.</para>
///
/// <para>One request, with every dictionary Youdao has for the word in it, each under its own name.
/// Measured 2026-10-08, the four that carry a part of speech — which the card needs, it groups by
/// it — are the source languages listed in <see cref="Sources"/>. German, Spanish, Russian and
/// Portuguese answer too, but only as <c>multle</c>, numbered glosses with no part of speech; Italian
/// and Chinese have no bilingual entry at all. Those are not asked, and the card falls through to
/// the next engine.</para>
///
/// <para>A word Youdao does not know comes back without the dictionary (Japanese and Korean put a
/// machine translation, <c>fanyi</c>, in its place), which is read as an entry with no groups.</para>
/// </remarks>
public sealed class YoudaoDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Endpoint = "https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4";

    /// <summary>The source languages with a dictionary that has parts of speech, and where each answer keeps it.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["en"] = "ec",
        ["ja"] = "newjc",
        ["ko"] = "kc",
        ["fr"] = "fc",
    };

    /// <summary>The most translations shown under one part of speech.</summary>
    /// <remarks>
    /// Youdao's entries are a printed dictionary's: 「run」 as a verb is thirty-odd senses. The card
    /// is a glance beside a translation, so the first few — the commonest, as it orders them — are
    /// what is kept.
    /// </remarks>
    internal const int MaxEntriesPerGroup = 8;

    public override string Name => "YoudaoDictionary";

    /// <summary>Whether this dictionary can answer the pair at all, before anything is sent.</summary>
    internal static bool Answers(string sourceLanguage, string targetLanguage) =>
        targetLanguage is "zh-CN" or "zh-TW" && Sources.ContainsKey(sourceLanguage);

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        // Nothing to ask for: an empty entry hands the lookup to the next engine without a request.
        if (!Answers(sourceLanguage, targetLanguage)) return new DictionaryResult(text, null, []);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"]       = text,
                ["le"]      = sourceLanguage,
                ["t"]       = "0",
                ["client"]  = "web",
                ["keyfrom"] = "webdict",
            }),
        };
        request.Headers.Referrer = new Uri("https://www.youdao.com/");

        using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(Sources[sourceLanguage], out var dictionary) ||
            dictionary.ValueKind != JsonValueKind.Object)
            return new DictionaryResult(text, null, []);

        return sourceLanguage switch
        {
            "en" => ReadEnglish(dictionary, text),
            "ja" => ReadJapanese(dictionary, text),
            _    => ReadKoreanOrFrench(dictionary, text),
        };
    }

    /// <summary><c>ec.word</c>: <c>trs[]</c> of <c>pos</c> and <c>tran</c>, the senses of one part of speech in one string.</summary>
    internal static DictionaryResult ReadEnglish(JsonElement ec, string text)
    {
        var word = FirstWord(ec);
        var groups = new Groups();
        foreach (var tr in Items(word, "trs"))
            groups.Add(OptionalString(tr, "pos"), Senses(OptionalString(tr, "tran")));

        var phonetic = OptionalString(word, "usphone") ?? OptionalString(word, "ukphone");
        return new DictionaryResult(
            OptionalString(word, "return-phrase") ?? text,
            phonetic is null ? null : $"/{phonetic}/",
            groups.ToList());
    }

    /// <summary>
    /// <c>newjc.word</c>: <c>head</c> with the reading, and <c>sense[]</c> of <c>cx</c>, the part of
    /// speech, and <c>phrList[].jmsy</c>, one sense each.
    /// </summary>
    /// <remarks>
    /// <para>A kana word that is written more than one way — やさしい, 優しい and 易しい — has the
    /// entries under <c>homonymD</c>, and beside them a <c>sense</c> list that is the two run together
    /// with a long machine-made list of near-synonyms; the homonyms are what is read.</para>
    ///
    /// <para>A sense with no <c>cx</c> is the part of speech before it continuing: セーブ's
    /// 「電算保存」 follows the senses of the verb.</para>
    /// </remarks>
    internal static DictionaryResult ReadJapanese(JsonElement newjc, string text)
    {
        var word = FirstWord(newjc);
        var homonyms = Items(word, "homonymD").ToList();

        var groups = new Groups();
        foreach (var entry in homonyms.Count > 0 ? homonyms : [word])
        {
            string? partOfSpeech = null;
            foreach (var sense in Items(entry, "sense"))
            {
                partOfSpeech = OptionalString(sense, "cx") ?? partOfSpeech;
                groups.Add(partOfSpeech, Items(sense, "phrList").SelectMany(phrase => Senses(OptionalString(phrase, "jmsy"))));
            }
        }

        var head = word.ValueKind == JsonValueKind.Object && word.TryGetProperty("head", out var h) ? h : default;
        var headword = OptionalString(head, "hw") ?? text;
        var reading = OptionalString(head, "pjm");
        return new DictionaryResult(headword, reading == headword ? null : reading, groups.ToList());
    }

    /// <summary>
    /// <c>kc.word[]</c> and <c>fc.word[]</c>, the same shape: each word's <c>trs[]</c> of <c>pos</c> and
    /// <c>tr[].l.i</c>, the gloss as a list of strings.
    /// </summary>
    internal static DictionaryResult ReadKoreanOrFrench(JsonElement dictionary, string text)
    {
        var groups = new Groups();
        string? phone = null;
        foreach (var word in Items(dictionary, "word"))
        {
            phone ??= OptionalString(word, "phone");
            foreach (var tr in Items(word, "trs"))
            {
                groups.Add(OptionalString(tr, "pos"), Items(tr, "tr").SelectMany(gloss =>
                    gloss.TryGetProperty("l", out var l) && l.TryGetProperty("i", out var i)
                        ? Senses(i.ValueKind == JsonValueKind.Array
                            ? string.Concat(i.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()))
                            : i.ValueKind == JsonValueKind.String ? i.GetString() : null)
                        : []));
            }
        }

        return new DictionaryResult(text, phone is null ? null : $"/{phone}/", groups.ToList());
    }

    /// <summary>The word an answer is about: an object in some answers, the first of a list in others.</summary>
    private static JsonElement FirstWord(JsonElement dictionary) =>
        dictionary.TryGetProperty("word", out var word)
            ? word.ValueKind == JsonValueKind.Array ? word.EnumerateArray().FirstOrDefault() : word
            : default;

    /// <summary>One gloss as its senses: Youdao separates senses with 「；」 and synonyms within one with 「，」.</summary>
    private static IEnumerable<string> Senses(string? gloss) =>
        gloss is null
            ? []
            : gloss.Split('；', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(sense => sense.TrimEnd('。', ' ', '\n'))
                .Where(sense => sense.Length > 0);

    /// <summary>Translations gathered by part of speech, in the order each first appears.</summary>
    /// <remarks>Without a part of speech there is no group to put them in: the card shows none such.</remarks>
    private sealed class Groups
    {
        private readonly List<(string PartOfSpeech, List<string> Entries)> _groups = [];

        public void Add(string? partOfSpeech, IEnumerable<string> senses)
        {
            if (string.IsNullOrWhiteSpace(partOfSpeech)) return;

            var group = _groups.FirstOrDefault(g => g.PartOfSpeech == partOfSpeech);
            if (group.Entries is null)
            {
                group = (partOfSpeech, []);
                _groups.Add(group);
            }

            foreach (var sense in senses)
                if (group.Entries.Count < MaxEntriesPerGroup && !group.Entries.Contains(sense))
                    group.Entries.Add(sense);
        }

        public List<DictionaryGroup> ToList() => _groups
            .Where(g => g.Entries.Count > 0)
            .Select(g => new DictionaryGroup(g.PartOfSpeech, g.Entries.Select(e => new DictionaryEntry(e, null, [])).ToList()))
            .ToList();
    }
}
