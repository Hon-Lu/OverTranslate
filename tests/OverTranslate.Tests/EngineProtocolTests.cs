using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Translation;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.Google;
using OverTranslate.Translation.Microsoft;
using Xunit;

namespace OverTranslate.Tests;

// What each engine puts on the wire and how it reads the answer, against canned responses shaped
// like the ones measured in .ai/translation-service-analysis/. No network: the real endpoints are
// checked by hand, and these pin what this side does with them.
public class EngineProtocolTests
{
    // ---- Packing ---------------------------------------------------------------------------

    [Fact]
    public void Plan_KeepsARequestUnderTheEnginesCharacterBudget()
    {
        var engine = new MicrosoftTranslator(new HttpClient(new Canned()));
        var texts = new[] { new string('a', 400), new string('b', 400), new string('c', 400) };

        Assert.Equal([[0, 1], [2]], engine.Plan(texts).Select(g => g.ToArray()));
    }

    [Fact]
    public void Plan_KeepsARequestUnderTheEnginesItemBudget()
    {
        var engine = new MicrosoftTranslator(new HttpClient(new Canned()));
        var texts = Enumerable.Range(0, 30).Select(i => $"line {i}").ToArray();

        var groups = engine.Plan(texts);

        Assert.Equal([25, 5], groups.Select(g => g.Count));
        Assert.Equal(Enumerable.Range(0, 30), groups.SelectMany(g => g));
    }

    [Fact]
    public void Plan_GivesBingOneTextPerGroup()
    {
        var engine = new BingTranslator(new HttpClient(new Canned()));

        Assert.Equal(3, engine.Plan(["a", "b", "c"]).Count);
    }

    [Fact]
    public async Task BlankTexts_AreNotSent_AndComeBackAsThemselves()
    {
        var handler = new Canned(_ => Json("""[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好"}]}]"""));
        var engine = new MicrosoftTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["", "Hello", "  "], "zh-TW");

        Assert.Equal(["", "你好", "  "], answers.Select(a => a.Text));
        Assert.Single(handler.Bodies);
        Assert.Equal("""[{"Text":"Hello"}]""", handler.Bodies[0]);
    }

    [Fact]
    public async Task ATextOverTheBudget_IsCutSentInPiecesAndJoined()
    {
        var handler = new Canned(request =>
        {
            var texts = JsonDocument.Parse(request.Body).RootElement.EnumerateArray()
                .Select(e => e.GetProperty("Text").GetString()!).ToList();
            return Json(JsonSerializer.Serialize(texts.Select(t => new
            {
                translations = new[] { new { text = $"[{t.Length}]" } },
            })));
        });
        var engine = new MicrosoftTranslator(new HttpClient(handler));
        var sentence = "The traveler walked along the quiet road and thought about home. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 30)).Trim();

        var answer = Assert.Single(await engine.TranslateAsync([text], "zh-TW"));

        Assert.True(handler.Bodies.Count > 1);
        Assert.All(handler.Bodies, body => Assert.True(
            JsonDocument.Parse(body).RootElement.EnumerateArray().Sum(e => e.GetProperty("Text").GetString()!.Length) <= 1000));
        var piecesSent = handler.Bodies.Sum(body => JsonDocument.Parse(body).RootElement.GetArrayLength());
        Assert.Equal(piecesSent, answer.Text.Split(' ').Length);
    }

    [Fact]
    public async Task AnAnswerCountThatDoesNotMatch_FailsTheRequest()
    {
        var handler = new Canned(_ => Json("""[{"translations":[{"text":"一"}]}]"""));
        var engine = new MicrosoftTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["one", "two"], "zh-TW"));
    }

    [Fact]
    public async Task AnHttpError_CarriesItsStatus()
    {
        var engine = new MicrosoftTranslator(new HttpClient(new Canned(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["one"], "zh-TW"));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
    }

    // ---- Microsoft -----------------------------------------------------------------------

    [Fact]
    public async Task Microsoft_SendsOneSignedArrayAndMapsItsLanguageCodes()
    {
        var handler = new Canned(_ => Json("""
            [{"detectedLanguage":{"language":"zh-Hans"},"translations":[{"text":"一"}]},
             {"detectedLanguage":{"language":"nb"},"translations":[{"text":"二"}]}]
            """));
        var engine = new MicrosoftTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["one", "two"], "zh-TW");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hant", request.Uri);
        Assert.StartsWith("MSTranslatorAndroidApp::", request.Headers["X-MT-Signature"]);
        Assert.Equal(["zh-CN", "no"], answers.Select(a => a.DetectedLanguage));
    }

    [Fact]
    public void Microsoft_SignatureIsBoundToTheUrl()
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var nonce = Guid.Parse("0123456789abcdef0123456789abcdef");

        var a = MicrosoftTranslator.Sign("api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=ja", now, nonce);
        var b = MicrosoftTranslator.Sign("api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hant", now, nonce);

        Assert.NotEqual(a, b);
        Assert.EndsWith("::Sun, 27 Sep 2026 12:00:00GMT::0123456789abcdef0123456789abcdef", a);
    }

    // ---- Google (Web) --------------------------------------------------------------------

    [Fact]
    public async Task GoogleWeb_SendsEveryTextAsItsOwnFieldAndReadsBothAnswerShapes()
    {
        var handler = new Canned(request => Json(request.Uri.Contains("sl=auto")
            ? """[["你好","en"],["謝謝","iw"]]"""
            : """["你好","謝謝"]"""));
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var detected = await engine.TranslateAsync(["Hello", "Thanks"], "zh-TW");
        var given = await engine.TranslateAsync(["Hello", "Thanks"], "zh-TW", "en");

        Assert.Equal("q=Hello&q=Thanks", handler.Bodies[0]);
        Assert.Equal(["en", "he"], detected.Select(a => a.DetectedLanguage));
        Assert.Equal(["你好", "謝謝"], given.Select(a => a.Text));
        Assert.All(given, a => Assert.Equal("", a.DetectedLanguage));
    }

    // ---- Google (RPC) --------------------------------------------------------------------

    [Fact]
    public async Task GoogleRpc_MatchesAnswersByTagNotByOrder()
    {
        var handler = new Canned(_ => Text(")]}'\n\n" + JsonSerializer.Serialize(new object?[]
        {
            new object?[] { "wrb.fr", "MkEWBc", RpcData("二", "ko"), null, null, null, "2" },
            new object?[] { "wrb.fr", "MkEWBc", RpcData("一", "ja"), null, null, null, "1" },
            new object?[] { "di", 42 },
        })));
        var engine = new GoogleRpcTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["いち", "둘"], "zh-TW");

        Assert.Single(handler.Requests);
        Assert.Equal(["一", "二"], answers.Select(a => a.Text));
        Assert.Equal(["ja", "ko"], answers.Select(a => a.DetectedLanguage));
    }

    [Fact]
    public async Task GoogleRpc_ACallTheServerRefused_IsAskedAgainOnItsOwn()
    {
        var calls = 0;
        var handler = new Canned(_ => Text(")]}'\n\n" + JsonSerializer.Serialize(Interlocked.Increment(ref calls) == 1
            ? new object?[]
            {
                new object?[] { "wrb.fr", "MkEWBc", RpcData("一", "ja"), null, null, null, "1" },
                new object?[] { "wrb.fr", "MkEWBc", null, null, null, new[] { 13 }, "2" },
            }
            : new object?[] { new object?[] { "wrb.fr", "MkEWBc", RpcData("二", "ja"), null, null, null, "1" } })));
        var engine = new GoogleRpcTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["いち", "に"], "zh-TW");

        Assert.Equal(["一", "二"], answers.Select(a => a.Text));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(Uri.EscapeDataString("いち"), handler.Bodies[1]);
    }

    [Fact]
    public async Task GoogleRpc_ACallRefusedTwice_FailsTheRequest()
    {
        var handler = new Canned(_ => Text(")]}'\n\n" + JsonSerializer.Serialize(new object?[]
        {
            new object?[] { "wrb.fr", "MkEWBc", null, null, null, new[] { 13 }, "1" },
        })));
        var engine = new GoogleRpcTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["に"], "zh-TW"));
    }

    // Seen twice from 「Google (Web)」: one line of a batch answered with nothing. Shown, it is an
    // empty box; so it is asked again, and only it.
    [Fact]
    public async Task AnEmptyAnswerToATextWithWords_IsAskedAgain()
    {
        var calls = 0;
        var handler = new Canned(_ => Json(Interlocked.Increment(ref calls) == 1
            ? """[["一","en"],["","en"]]"""
            : """[["二","en"]]"""));
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["one", "two"], "zh-TW");

        Assert.Equal(["一", "二"], answers.Select(a => a.Text));
        Assert.Equal("q=two", handler.Bodies[1]);
    }

    [Theory]
    [InlineData(new[] { "連線遺失。", "重試。" }, "連線遺失。重試。")]
    [InlineData(new[] { "Connection lost.", "Retrying." }, "Connection lost. Retrying.")]
    [InlineData(new[] { "연결이 끊겼습니다.", "다시 시도합니다." }, "연결이 끊겼습니다. 다시 시도합니다.")]
    public void GoogleRpc_JoinsSentencesWithASpaceOnlyWhereTheLanguageUsesOne(string[] sentences, string expected)
    {
        Assert.Equal(expected, GoogleRpcTranslator.JoinSentences(sentences));
    }

    // ---- Google (Chrome) -----------------------------------------------------------------

    [Fact]
    public async Task GoogleChrome_EscapesMarkupOnTheWayOutAndDecodesItOnTheWayBack()
    {
        var handler = new Canned(_ => Json("""[["按 &lt;A&gt; 鍵並「儲存」"],["en"]]"""));
        var engine = new GoogleChromeTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync(["Press <A> & \"save\""], "zh-TW"));

        Assert.Contains("Press &lt;A&gt; &amp; &quot;save&quot;", JsonDocument.Parse(handler.Bodies[0]).RootElement[0][0][0].GetString());
        Assert.Equal("按 <A> 鍵並「儲存」", answer.Text);
        Assert.Equal("en", answer.DetectedLanguage);
    }

    [Fact]
    public async Task GoogleChrome_KeepsTheBlankLineBetweenParagraphs()
    {
        var handler = new Canned(request =>
        {
            var sent = JsonDocument.Parse(request.Body).RootElement[0][0].EnumerateArray().Select(e => e.GetString()!).ToList();
            return Json(JsonSerializer.Serialize(new object[] { sent.Select(s => $"<{s}>").ToArray() }));
        });
        var engine = new GoogleChromeTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync(["First line\nsame paragraph.\n\nSecond paragraph."], "zh-TW"));

        Assert.Equal("<First line same paragraph.>\n\n<Second paragraph.>", answer.Text);
    }

    [Fact]
    public async Task GoogleChrome_A5xxIsTriedOnceMoreOnTheRegionalHost()
    {
        var handler = new Canned(request => request.Uri.Contains("translate-pa.googleapis.com")
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Json("""[["好"],["en"]]"""));
        var engine = new GoogleChromeTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW"));

        Assert.Equal("好", answer.Text);
        Assert.Equal(["translate-pa.googleapis.com", "translate-pa.us.rep.googleapis.com"],
            handler.Requests.Select(r => new Uri(r.Uri).Host));
    }

    [Fact]
    public async Task GoogleChrome_ARefusalThatIsNotTheServers_IsNotRetriedElsewhere()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var engine = new GoogleChromeTranslator(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    // ---- Bing ----------------------------------------------------------------------------

    private const string BingPage =
        """<html><script>var params_AbusePreventionHelper = [1727400000000,"TOKEN0123456789abcdef0123456789ab",3600000];IG:"ABCDEF0123"</script><div data-iid="translator.5077"></div></html>""";

    [Fact]
    public async Task Bing_FetchesItsCredentialsOnceForEveryText()
    {
        var handler = new Canned(request => request.Uri.EndsWith("/translator")
            ? Text(BingPage)
            : Json("""[{"detectedLanguage":{"language":"ja"},"translations":[{"text":"快逃！"}],"usedLLM":true}]"""));
        var engine = new BingTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["早く逃げて！", "逃げて", "走れ"], "zh-TW");

        Assert.Equal(1, handler.Requests.Count(r => r.Uri.EndsWith("/translator")));
        Assert.Equal(3, handler.Requests.Count(r => r.Uri.Contains("/ttranslatev3")));
        Assert.All(answers, a => Assert.Equal("ja", a.DetectedLanguage));
        Assert.Contains(handler.Bodies, body => body.Contains("to=zh-Hant") && body.Contains("key=1727400000000"));
    }

    // Bing reports its errors inside a 200.
    [Fact]
    public async Task Bing_AnErrorInsideA200_IsAFailure()
    {
        var handler = new Canned(request => request.Uri.EndsWith("/translator")
            ? Text(BingPage)
            : Json("""{"statusCode":400,"errorMessage":""}"""));
        var engine = new BingTranslator(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["x"], "zh-TW"));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
    }

    [Fact]
    public async Task Bing_CredentialsRefusedForAnythingButTheText_AreFetchedAgain()
    {
        var refused = true;
        var handler = new Canned(request =>
        {
            if (request.Uri.EndsWith("/translator")) return Text(BingPage);
            if (refused) { refused = false; return Json("""{"statusCode":205}"""); }
            return Json("""[{"translations":[{"text":"好"}]}]""");
        });
        var engine = new BingTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));
        await engine.TranslateAsync(["OK"], "zh-TW");

        Assert.Equal(2, handler.Requests.Count(r => r.Uri.EndsWith("/translator")));
    }

    // ---- Transport -----------------------------------------------------------------------

    // Concurrent requests share one connection per host instead of one each; see EngineHttp.
    [Fact]
    public async Task EveryRequest_IsOfferedHttp2()
    {
        var inner = new Canned(_ => Json("[]"));
        var client = new HttpClient(new EngineHttp.PreferHttp2Handler(inner));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/") { Version = HttpVersion.Version11 };
        await client.SendAsync(request);

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(HttpVersion.Version20, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string RpcData(string translation, string detected) => JsonSerializer.Serialize(new object?[]
    {
        null,
        new object?[]
        {
            new object?[] { new object?[] { null, null, null, null, null, new object?[] { new object?[] { translation } } } },
            "zh-TW", 1, "auto",
        },
        detected,
    });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed record SentRequest(
        string Uri, string Body, Dictionary<string, string> Headers, Version Version, HttpVersionPolicy Policy);

    /// <summary>Answers every request from a function, remembering what was asked.</summary>
    private sealed class Canned(Func<SentRequest, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests { get { lock (_requests) return [.. _requests]; } }

        public IReadOnlyList<string> Bodies => Requests.Where(r => r.Body.Length > 0).Select(r => r.Body).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var sent = new SentRequest(request.RequestUri!.ToString(), body, headers, request.Version, request.VersionPolicy);
            lock (_requests) _requests.Add(sent);
            return (answer ?? (_ => Json("[]")))(sent);
        }
    }
}
