using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Lookup;
using Xunit;

namespace OverTranslate.Tests;

// What each dictionary engine puts on the wire and how it reads the answer, against canned answers.
// No network: the real endpoints were compared with GTranslate's by hand
// (.ai/translation-service-analysis/implementation/).
public class LookupProtocolTests
{
    // ---- Google -----------------------------------------------------------------------------

    private const string GoogleAnswer = """
        {"sentences":[{"trans":"跑步","orig":"run"},{"src_translit":"rən"}],
         "src":"en",
         "dict":[
           {"pos":"verb","entry":[{"word":"跑","reverse_translation":["run","go"],"score":0.5,"frequency":1},
                                  {"word":"","reverse_translation":["x"]},
                                  {"word":"經營","reverse_translation":["run"]}]},
           {"pos":"noun","entry":[{"word":"跑步","score":0.1}]}],
         "alternative_translations":[{"alternative":[{"word_postproc":"跑步","score":1000},{"word_postproc":"奔跑","score":20}]}],
         "definitions":[{"pos":"Verb","entry":[{"gloss":"move fast"},{"gloss":"move fast"},{"gloss":""}]},
                        {"pos":"noun","entry":[{"gloss":"an act of running"}]}],
         "synsets":[{"pos":"verb","entry":[{"synonym":["sprint","race"]},{"synonym":["race","dash"]}]}],
         "examples":{"example":[{"text":"a <b>run</b> in the park"}]}}
        """;

    [Fact]
    public async Task Google_AsksForTheDictionaryParts_AndReadsThem()
    {
        var handler = new Canned(_ => Json(GoogleAnswer));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("run", "zh-TW", "en");

        var sent = Assert.Single(handler.Requests);
        Assert.StartsWith("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-TW", sent.Uri);
        foreach (var part in new[] { "t", "bd", "at", "ex", "md", "ss" }) Assert.Contains($"&dt={part}&", sent.Uri + "&");
        Assert.Equal("q=run", sent.Body);

        Assert.Equal("run", result.Headword);
        Assert.Equal("rən", result.Pronunciation);

        var verb = result.Groups[0];
        Assert.Equal("verb", verb.PartOfSpeech);
        Assert.Equal(["跑", "經營"], verb.Entries.Select(e => e.Text));
        Assert.Equal(["run", "go"], verb.Entries[0].BackTranslations);
        Assert.Equal(0.5, verb.Entries[0].Confidence);
        Assert.Equal(1, verb.Entries[0].Frequency);
        Assert.Equal(["move fast"], verb.Definitions);          // matched regardless of case, repeats and blanks dropped
        Assert.Equal(["sprint", "race", "dash"], verb.Synonyms);

        Assert.Equal(["an act of running"], result.Groups[1].Definitions);
        Assert.Empty(result.Groups[1].Synonyms);

        // The other translations of the whole text, less those already listed.
        var others = result.Groups[2];
        Assert.Null(others.PartOfSpeech);
        Assert.Equal(["奔跑"], others.Entries.Select(e => e.Text));

        Assert.Equal("a <b>run</b> in the park", Assert.Single(result.Examples).Source);
    }

    [Fact]
    public async Task Google_AWordWithNoEntry_HasNoGroups_NotEvenTheOtherTranslations()
    {
        var handler = new Canned(_ => Json("""
            {"sentences":[{"trans":"xyzzyq","orig":"xyzzyq"}],"src":"en",
             "alternative_translations":[{"alternative":[{"word_postproc":"xyzzyq"}]}]}
            """));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("xyzzyq", "ja", "en");

        Assert.Empty(result.Groups);
        Assert.Null(result.Pronunciation);
        Assert.Empty(result.Examples);
    }

    [Fact]
    public async Task Google_ABlockedRequest_IsAnEngineFailure()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "ja", "en"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    // ---- Microsoft --------------------------------------------------------------------------

    private const string MicrosoftLookup = """
        [{"normalizedSource":"light","displaySource":"light","translations":[
          {"normalizedTarget":"光","displayTarget":"光","posTag":"NOUN","confidence":0.4,"prefixWord":"",
           "backTranslations":[{"normalizedText":"light","displayText":"light"},{"normalizedText":"glow","displayText":"glow"}]},
          {"normalizedTarget":"軽い","displayTarget":"軽い","posTag":"ADJ","confidence":0.3,"backTranslations":[]},
          {"normalizedTarget":"ライト","displayTarget":"ライト","posTag":"noun","confidence":0.2,"backTranslations":[]},
          {"normalizedTarget":"光","displayTarget":"光","posTag":"VERB","confidence":0.1,"backTranslations":[]}]}]
        """;

    [Fact]
    public async Task Microsoft_SignsBothRequests_AndPairsTheExamplesWithTheirTranslation()
    {
        var handler = new Canned(request => request.Uri.Contains("/dictionary/lookup")
            ? Json(MicrosoftLookup)
            : Json("""
                [{"normalizedSource":"light","normalizedTarget":"光","examples":[
                   {"sourcePrefix":"the ","sourceTerm":"light","sourceSuffix":" is on","targetPrefix":"","targetTerm":"光","targetSuffix":"がついている"}]}]
                """));
        var engine = new MicrosoftDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("light", "zh-CN", "en");

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.StartsWith("MSTranslatorAndroidApp::", r.Headers["X-MT-Signature"]));
        Assert.All(handler.Requests, r => Assert.Contains("&from=en&to=zh-Hans", r.Uri));
        Assert.Equal("""[{"Text":"light"}]""", handler.Requests[0].Body);

        // One pair per distinct translation, in the order found.
        var pairs = JsonDocument.Parse(handler.Requests[1].Body).RootElement.EnumerateArray()
            .Select(p => (p.GetProperty("Text").GetString(), p.GetProperty("Translation").GetString())).ToList();
        Assert.Equal([("light", "光"), ("light", "軽い"), ("light", "ライト")], pairs);

        // Grouped by part of speech, whatever case it is written in.
        Assert.Equal(["NOUN", "ADJ", "VERB"], result.Groups.Select(g => g.PartOfSpeech));
        Assert.Equal(["光", "ライト"], result.Groups[0].Entries.Select(e => e.Text));
        Assert.Equal(["light", "glow"], result.Groups[0].Entries[0].BackTranslations);

        var example = Assert.Single(result.Groups[0].Entries[0].Examples);
        Assert.Equal("the light is on", example.Source);
        Assert.Equal("光がついている", example.Translation);
        Assert.Empty(result.Groups[0].Entries[1].Examples);
    }

    [Fact]
    public async Task Microsoft_AWordWithNoTranslations_AsksForNoExamples()
    {
        var handler = new Canned(_ => Json("""[{"normalizedSource":"xyzzyq","displaySource":"xyzzyq","translations":[]}]"""));
        var engine = new MicrosoftDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("xyzzyq", "ja", "en");

        Assert.Single(handler.Requests);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Microsoft_APairWithNoDictionary_IsAnEngineFailure()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var engine = new MicrosoftDictionary(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "zh-TW", "en"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    // ---- Bing -------------------------------------------------------------------------------

    [Fact]
    public async Task Bing_SendsTheWordWithThePagesCredentials_AndKeepsTheTransliteration()
    {
        var handler = new Canned(request =>
        {
            if (request.Uri.EndsWith("/translator")) return Text(BingPage);
            return Json("""
                [{"normalizedSource":"book","displaySource":"book","translations":[
                  {"normalizedTarget":"책","displayTarget":"책","posTag":"NOUN","confidence":0.8,"transliteration":"chaeg",
                   "backTranslations":[{"normalizedText":"book","displayText":"book"},{"normalizedText":"book","displayText":"book"}]}]}]
                """);
        });
        var engine = new BingDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("book", "ko", "en");

        var lookup = Assert.Single(handler.Requests, r => r.Uri.Contains("/tlookupv3"));
        Assert.Contains("IG=ABCDEF0123&IID=translator.5077", lookup.Uri);
        var form = Form(lookup.Body);
        Assert.Equal("en", form["from"]);
        Assert.Equal("ko", form["to"]);
        Assert.Equal("book", form["text"]);
        Assert.Equal("TOKEN0123456789abcdef0123456789ab", form["token"]);
        Assert.Equal("1727400000000", form["key"]);

        var entry = Assert.Single(Assert.Single(result.Groups).Entries);
        Assert.Equal("책", entry.Text);
        Assert.Equal("chaeg", entry.Transliteration);
        Assert.Equal(["book"], entry.BackTranslations);
    }

    [Theory]
    [InlineData(400, false)]   // the request itself refused — the credentials are fine
    [InlineData(500, true)]    // anything else may be the credentials going stale
    public async Task Bing_ARefusal_IsAnEngineFailure_AndOnlyARefusalOfTheRequestKeepsTheCredentials(int status, bool refetched)
    {
        var handler = new Canned(request => request.Uri.EndsWith("/translator")
            ? Text(BingPage)
            : Json($$"""{"statusCode":{{status}}}"""));
        var engine = new BingDictionary(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "zh-TW", "en"));
        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "zh-TW", "en"));

        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
        Assert.Equal(refetched ? 2 : 1, handler.Requests.Count(r => r.Uri.EndsWith("/translator")));
    }

    // ---- The application's side -------------------------------------------------------------

    [Fact]
    public async Task Provider_SpeaksInEngineCodes_AndCreditsTheEntryToItsService()
    {
        var handler = new Canned(_ => Json(MicrosoftLookup.Replace("\"prefixWord\":\"\",", "")));
        var provider = new DictionaryLookupProvider(new MicrosoftDictionary(new HttpClient(handler)), "Microsoft");

        var result = await provider.LookupDictionaryAsync("light", "EN", "ZH-HANS");

        Assert.Contains("&from=en&to=zh-Hans", handler.Requests[0].Uri);
        Assert.NotNull(result);
        Assert.Equal("light", result.Source);
        Assert.Equal("Microsoft", result.Service);
        Assert.Equal(["NOUN", "ADJ", "VERB"], result.Groups.Select(g => g.PartOfSpeech));
    }

    [Fact]
    public async Task Provider_WithTheSourceLeftToDetection_AsksNothing()
    {
        var handler = new Canned(_ => Json(MicrosoftLookup));
        var provider = new DictionaryLookupProvider(new MicrosoftDictionary(new HttpClient(handler)), "Microsoft");

        Assert.Null(await provider.LookupDictionaryAsync("light", "AUTO", "JA"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Provider_AnEntryWithNothingToShow_IsNoResult()
    {
        var handler = new Canned(_ => Json("""[{"normalizedSource":"xyzzyq","translations":[]}]"""));
        var provider = new DictionaryLookupProvider(new MicrosoftDictionary(new HttpClient(handler)), "Microsoft");

        Assert.Null(await provider.LookupDictionaryAsync("xyzzyq", "EN", "JA"));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private const string BingPage =
        """<html><script>var params_AbusePreventionHelper = [1727400000000,"TOKEN0123456789abcdef0123456789ab",3600000];IG:"ABCDEF0123"</script><div data-iid="translator.5077"></div></html>""";

    private static Dictionary<string, string> Form(string body) => body.Split('&')
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed record SentRequest(string Uri, string Body, Dictionary<string, string> Headers);

    /// <summary>Answers every request from a function, remembering what was asked.</summary>
    private sealed class Canned(Func<SentRequest, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests { get { lock (_requests) return [.. _requests]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var sent = new SentRequest(request.RequestUri!.ToString(), body, headers);
            lock (_requests) _requests.Add(sent);
            return answer(sent);
        }
    }
}
