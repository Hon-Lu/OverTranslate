using System.Net;
using System.Net.Http;
using System.Text;
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
           {"pos":"verb","entry":[{"word":"跑","reverse_translation":["run","go"],"score":0.5},
                                  {"word":"","reverse_translation":["x"]},
                                  {"word":"經營","reverse_translation":["run"]}]},
           {"pos":"noun","entry":[{"word":"跑步"}]}]}
        """;

    [Fact]
    public async Task Google_AsksOnlyForWhatTheCardShows_AndReadsIt()
    {
        var handler = new Canned(_ => Json(GoogleAnswer));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("run", "zh-TW", "en");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://translate.googleapis.com/translate_a/single?client=dict-chrome-ex&sl=en&tl=zh-TW&dt=t&dt=bd&dt=rm&dj=1&source=input", sent.Uri);
        Assert.Equal("q=run", sent.Body);

        Assert.Equal("run", result.Headword);
        Assert.Equal("rən", result.Pronunciation);
        Assert.Equal(["verb", "noun"], result.Groups.Select(g => g.PartOfSpeech));
        Assert.Equal(["跑", "經營"], result.Groups[0].Entries.Select(e => e.Text));   // blank words dropped
        Assert.Equal(["run", "go"], result.Groups[0].Entries[0].BackTranslations);
        Assert.Empty(result.Groups[1].Entries[0].BackTranslations);
    }

    [Fact]
    public async Task Google_AWordWithNoEntry_HasNoGroups()
    {
        var handler = new Canned(_ => Json("""{"sentences":[{"trans":"xyzzyq","orig":"xyzzyq"}],"src":"en"}"""));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("xyzzyq", "ja", "en");

        Assert.Empty(result.Groups);
        Assert.Null(result.Pronunciation);
    }

    [Fact]
    public async Task Google_ABlockedRequest_IsAnEngineFailure_OnceEveryClientIsBlocked()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "ja", "en"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(["dict-chrome-ex", "gtx"], handler.Requests.Select(ClientOf));
    }

    // Google limits this endpoint per client name, so a 429 on one is asked again under the other —
    // and the one that answered is asked first from then on, rather than the limited one every time.
    [Fact]
    public async Task Google_A429_IsAskedAgainUnderTheOtherClient_WhichIsKept()
    {
        var handler = new Canned(sent => ClientOf(sent) == "dict-chrome-ex"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json(GoogleAnswer));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var first = await engine.LookupAsync("run", "zh-TW", "en");
        var second = await engine.LookupAsync("run", "zh-TW", "en");

        Assert.Equal(["dict-chrome-ex", "gtx", "gtx"], handler.Requests.Select(ClientOf));
        Assert.All(handler.Requests, sent => Assert.Equal("q=run", sent.Body));
        Assert.Equal(2, first.Groups.Count);
        Assert.Equal(2, second.Groups.Count);
    }

    // Only a 429 is a name being limited; anything else would fail the same way under either.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Google_OtherRefusals_AreNotAskedAgain(HttpStatusCode status)
    {
        var handler = new Canned(_ => new HttpResponseMessage(status));
        var engine = new GoogleDictionary(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.LookupAsync("run", "ja", "en"));
        Assert.Equal(status, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    private static string? ClientOf(SentRequest sent) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(sent.Uri).Query)["client"];

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
    public async Task Microsoft_SignsOneRequest_AndGroupsByPartOfSpeech()
    {
        var handler = new Canned(_ => Json(MicrosoftLookup));
        var engine = new MicrosoftDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("light", "zh-CN", "en");

        var sent = Assert.Single(handler.Requests);
        Assert.StartsWith("MSTranslatorAndroidApp::", sent.Headers["X-MT-Signature"]);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/dictionary/lookup?api-version=3.0&from=en&to=zh-Hans", sent.Uri);
        Assert.Equal("""[{"Text":"light"}]""", sent.Body);

        // Grouped by part of speech, whatever case it is written in.
        Assert.Equal("light", result.Headword);
        Assert.Equal(["NOUN", "ADJ", "VERB"], result.Groups.Select(g => g.PartOfSpeech));
        Assert.Equal(["光", "ライト"], result.Groups[0].Entries.Select(e => e.Text));
        Assert.Equal(["light", "glow"], result.Groups[0].Entries[0].BackTranslations);
    }

    [Fact]
    public async Task Microsoft_AWordWithNoTranslations_HasNoGroups()
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

    // ---- Youdao ------------------------------------------------------------------------------

    // Shapes taken from real answers, 2026-10-08, cut down to the parts read.
    private const string YoudaoEnglish = """
        {"ec":{"word":{"usphone":"rʌn","ukphone":"rʌn","return-phrase":"run",
          "trs":[{"pos":"v.","tran":"跑，奔跑；参加（赛跑）；管理，经营；运转；刊登；行驶；流动；掉色；变成；达到"},
                 {"pos":"n.","tran":"跑步，赛跑；旅程"},
                 {"tran":"【名】 （Run）（塞）鲁恩（人名）"}]}}}
        """;

    private const string YoudaoJapanese = """
        {"newjc":{"word":{"head":{"rs":"sēbu","pjm":"セーブ","hw":"セーブ"},
          "sense":[{"phrList":[{"jmsy":"【英】save；救，搭救，拯救。"}],"cx":"他动词・サ变/三类 名词"},
                   {"phrList":[{"jmsy":"電算保存"}]}]}},
         "jc":{"$ref":"$.newjc"}}
        """;

    private const string YoudaoHomonyms = """
        {"newjc":{"word":{"head":{"pjm":"やさしい","hw":"やさしい"},
          "homonymD":[{"head":{"hw":"優しい"},"sense":[{"phrList":[{"jmsy":"亲切的，富于同情心的。"}],"cx":"形容词"}]},
                      {"head":{"hw":"易しい"},"sense":[{"phrList":[{"jmsy":"容易。"},{"jmsy":"易懂，简单。"}],"cx":"形容词"}]}],
          "sense":[{"phrList":[{"jmsy":"容易；小心点；留神点；轻松点"}]}]}}}
        """;

    private const string YoudaoKorean = """
        {"kc":{"word":[{"trs":[{"pos":"名","tr":[{"exam":{"i":[]},"l":{"i":["爱，爱情。"]}},{"l":{"i":["爱好。"]}}]}],
                        "return-phrase":{"l":{"i":"사랑"}}},
                       {"trs":[{"pos":"名","tr":[{"l":{"i":["厢房。"]}}]}]}]}}
        """;

    private const string YoudaoFrench = """
        {"fc":{"word":[{"phone":"mɛzɔ̃","trs":[{"pos":"f.","tr":[{"l":{"i":["房屋，房子；住宅；家，家庭"]}}]},
                                               {"pos":"a.inv.","tr":[{"l":{"i":["家制的，自制的"]}}]}]}]}}
        """;

    [Fact]
    public async Task Youdao_SendsTheWordAndItsLanguage_AndReadsEnglish()
    {
        var handler = new Canned(_ => Json(YoudaoEnglish));
        var engine = new YoudaoDictionary(new HttpClient(handler));

        var result = await engine.LookupAsync("run", "zh-CN", "en");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4", sent.Uri);
        Assert.Equal("https://www.youdao.com/", sent.Headers["Referer"]);
        var form = Form(sent.Body);
        Assert.Equal(("run", "en", "web"), (form["q"], form["le"], form["client"]));

        Assert.Equal("/rʌn/", result.Pronunciation);
        Assert.Equal(["v.", "n."], result.Groups.Select(g => g.PartOfSpeech));   // the name with no pos left out
        Assert.Equal(YoudaoDictionary.MaxEntriesPerGroup, result.Groups[0].Entries.Count);
        Assert.Equal(["跑，奔跑", "参加（赛跑）", "管理，经营"], result.Groups[0].Entries.Take(3).Select(e => e.Text));
    }

    [Fact]
    public async Task Youdao_ReadsJapanese_WithTheKanaReading_AndASenseWithoutAPartOfSpeechKeptWithTheOneBefore()
    {
        var engine = new YoudaoDictionary(new HttpClient(new Canned(_ => Json(YoudaoJapanese))));

        var result = await engine.LookupAsync("セーブ", "zh-TW", "ja");

        Assert.Null(result.Pronunciation);   // the reading is the word itself
        var group = Assert.Single(result.Groups);
        Assert.Equal("他动词・サ变/三类 名词", group.PartOfSpeech);
        Assert.Equal(["【英】save", "救，搭救，拯救", "電算保存"], group.Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task Youdao_AWordWrittenMoreThanOneWay_IsReadFromItsHomonyms()
    {
        var engine = new YoudaoDictionary(new HttpClient(new Canned(_ => Json(YoudaoHomonyms))));

        var result = await engine.LookupAsync("やさしい", "zh-CN", "ja");

        var group = Assert.Single(result.Groups);
        Assert.Equal(["亲切的，富于同情心的", "容易", "易懂，简单"], group.Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task Youdao_ReadsKoreanAndFrench()
    {
        var korean = await new YoudaoDictionary(new HttpClient(new Canned(_ => Json(YoudaoKorean))))
            .LookupAsync("사랑", "zh-CN", "ko");
        var french = await new YoudaoDictionary(new HttpClient(new Canned(_ => Json(YoudaoFrench))))
            .LookupAsync("maison", "zh-CN", "fr");

        Assert.Equal(["爱，爱情", "爱好", "厢房"], Assert.Single(korean.Groups).Entries.Select(e => e.Text));
        Assert.Equal("/mɛzɔ̃/", french.Pronunciation);
        Assert.Equal(["f.", "a.inv."], french.Groups.Select(g => g.PartOfSpeech));
        Assert.Equal(["房屋，房子", "住宅", "家，家庭"], french.Groups[0].Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task Youdao_AWordItDoesNotKnow_HasNoGroups()
    {
        var engine = new YoudaoDictionary(new HttpClient(new Canned(_ => Json("""{"fanyi":{"tran":"x"},"le":"ja"}"""))));

        Assert.Empty((await engine.LookupAsync("ぬぬぬぬ", "zh-CN", "ja")).Groups);
    }

    // Chinese glosses only, and only for the four languages whose entries have parts of speech:
    // anything else hands the lookup on without a request.
    [Theory]
    [InlineData("run", "ja", "en")]
    [InlineData("Haus", "zh-CN", "de")]
    [InlineData("猫", "zh-CN", "zh-TW")]
    public async Task Youdao_APairItHasNoDictionaryFor_IsEmptyWithoutARequest(string word, string target, string source)
    {
        var handler = new Canned(_ => Json(YoudaoEnglish));

        var result = await new YoudaoDictionary(new HttpClient(handler)).LookupAsync(word, target, source);

        Assert.Empty(result.Groups);
        Assert.Empty(handler.Requests);
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
