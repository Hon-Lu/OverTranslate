using System.Text.Json;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Google's dictionary: <c>translate_a/single</c>, the one-text endpoint the Web engine's
/// <c>translate_a/t</c> is the sibling of, asked for its dictionary part as well as the translation.
/// </summary>
/// <remarks>
/// <para>The request and the reading of the answer are GTranslate's
/// <c>GoogleTranslator.LookupDictionaryAsync</c> and <c>GoogleDictionaryParser</c>
/// (MIT, d4n3436/GTranslate), less what the dictionary card never showed (listed on
/// <see cref="DictionaryResult"/>, with how to ask for it again) and less the <c>tk</c> token:
/// measured 2026-09-27 with and without it, the answers were the same.</para>
///
/// <para>The parts asked for: <c>bd</c> translations by part of speech, and <c>t</c> with
/// <c>rm</c>, whose sentences carry the pronunciation (<c>src_translit</c>). GTranslate asked for
/// <c>t</c> without <c>rm</c>, and without it the reading is never there, so the dictionary card
/// never showed one. Measured 2026-09-27 on nine words: <c>rm</c> adds the reading for every
/// source language tried (食べる → Taberu, 电脑 → Diànnǎo, 사랑 → salang, дом → dom) and leaves
/// the entries exactly as they were.</para>
/// </remarks>
public sealed class GoogleDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    public override string Name => "GoogleDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?client=gtx&sl={Uri.EscapeDataString(sourceLanguage)}&tl={Uri.EscapeDataString(targetLanguage)}" +
                  "&dt=t&dt=bd&dt=rm&dj=1&source=input";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
        };

        using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
        return Read(document.RootElement, text);
    }

    internal static DictionaryResult Read(JsonElement root, string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"expected an object, found {root.ValueKind}");

        var groups = Items(root, "dict").Select(group => new DictionaryGroup(
            OptionalString(group, "pos"),
            Items(group, "entry")
                .Where(entry => OptionalString(entry, "word") is not null)
                .Select(entry => new DictionaryEntry(
                    OptionalString(entry, "word")!,
                    null,
                    Items(entry, "reverse_translation")
                        .Where(back => back.ValueKind == JsonValueKind.String)
                        .Select(back => back.GetString()!)
                        .ToList()))
                .ToList()))
            .ToList();

        var pronunciation = Items(root, "sentences")
            .Select(sentence => OptionalString(sentence, "src_translit"))
            .FirstOrDefault(reading => reading is not null);

        return new DictionaryResult(text, pronunciation, groups);
    }
}
