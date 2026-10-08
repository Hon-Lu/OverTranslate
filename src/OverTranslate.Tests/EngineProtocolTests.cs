using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Translation;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.DeepL;
using OverTranslate.Translation.Google;
using OverTranslate.Translation.Microsoft;
using OverTranslate.Translation.Tencent;
using OverTranslate.Translation.Youdao;
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

    // Measured 2026-09-28: this endpoint reports traditional Chinese as en; RPC and Chrome say
    // zh-TW for the same texts. Believed, 雙語互譯 turned round on every traditional Chinese word.
    [Theory]
    [InlineData("測試", "en", "zh-TW")]
    [InlineData("這個問題很難", "en", "zh-TW")]
    [InlineData("测试", "zh-CN", "zh-CN")]
    [InlineData("東京", "ja", "ja")]
    [InlineData("テスト", "en", "en")]          // kana: whatever this is, it is not Chinese
    [InlineData("這個 API 要怎麼用", "en", "en")] // a Latin letter: en may be true
    [InlineData("123", "en", "en")]
    [InlineData("test", "en", "en")]
    public async Task GoogleWeb_TraditionalChineseReportedAsEnglish_IsReadAsTraditionalChinese(
        string text, string reported, string expected)
    {
        var handler = new Canned(_ => Json(JsonSerializer.Serialize(new[] { new[] { "x", reported } })));
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync([text], "en"));

        Assert.Equal(expected, answer.DetectedLanguage);
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
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

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
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

        var answer = Assert.Single(await engine.TranslateAsync(["First line\nsame paragraph.\n\nSecond paragraph."], "zh-TW"));

        Assert.Equal("<First line same paragraph.>\n\n<Second paragraph.>", answer.Text);
    }

    [Fact]
    public async Task GoogleChrome_A5xxIsTriedOnceMoreOnTheRegionalHost()
    {
        var handler = new Canned(request => request.Uri.Contains("translate-pa.googleapis.com")
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Json("""[["好"],["en"]]"""));
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

        var answer = Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW"));

        Assert.Equal("好", answer.Text);
        Assert.Equal(["translate-pa.googleapis.com", "translate-pa.us.rep.googleapis.com"],
            handler.Requests.Select(r => new Uri(r.Uri).Host));
    }

    [Fact]
    public async Task GoogleChrome_ARefusalThatIsNotTheServers_IsNotRetriedElsewhere()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    // ---- Google (Chrome): the key ----------------------------------------------------------

    // Shaped like the real ones, made up: the loader names the main script in JavaScript-escaped
    // settings, and the main script holds two keys, only one of them next to translateHtml.
    //
    // Put together at run time because a key written out whole is one GitHub's secret scanning
    // reports, made up or not.
    private static readonly string StoredKey = FakeKey('S');
    private static readonly string HtmlKey   = FakeKey('H');
    private static readonly string OtherKey  = FakeKey('O');

    private static string FakeKey(char fill) => "AI" + "za" + new string(fill, 35);

    private const string Loader =
        """c._ctkk='x';c._pas='https://';c._pbi='';c._cac='';c._cam='';c._ctlu='',c._ps="\x22translate-pa.googleapis.com\x22,\x22https:\/\/translate.googleapis.com\/_\/translate_http\/_\/js\/k\\u003dtranslate_http.tr.en.abc.O\/m\\u003del_main\x22,\x22TE_20260929\x22";""";

    private const string MainScriptUrl =
        "https://translate.googleapis.com/_/translate_http/_/js/k=translate_http.tr.en.abc.O/m=el_main";

    private static readonly string MainScript =
        "_.n.Lc=function(a,b){this.h.A.send({display_language:b,key:\"" + OtherKey + "\"},a)};" +
        "d={host:b,path:\"/v1/translateHtml\",method:\"POST\",headers:{\"X-goog-api-key\":\"" + HtmlKey +
        "\",\"Content-Type\":\"application/json+protobuf\"}};";

    private static HttpResponseMessage RefusedKey() =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """[3,"API key not valid. Please pass a valid API key.",[["type.googleapis.com/google.rpc.ErrorInfo",["API_KEY_INVALID","googleapis.com"]]]]"""),
        };

    /// <summary>The scripts, and a translateHtml that answers only <paramref name="accepted"/>.</summary>
    private static Canned ChromeWithScripts(string accepted, string? mainScript = null) => new(request =>
        request.Uri == GoogleChromeKeySource.LoaderUrl ? Text(Loader) :
        request.Uri == MainScriptUrl ? Text(mainScript ?? MainScript) :
        request.Headers.GetValueOrDefault("X-Goog-API-Key") == accepted ? Json("""[["好"],["en"]]""") :
        RefusedKey());

    [Fact]
    public void GoogleChromeKeySource_FindsTheMainScriptInTheLoader()
    {
        Assert.Equal(MainScriptUrl, GoogleChromeKeySource.FindMainScript(Loader));
    }

    [Fact]
    public void GoogleChromeKeySource_PutsTheKeyNextToTranslateHtmlFirst()
    {
        // The other key comes first in the script and is refused by translateHtml with a 403, so
        // taking keys in the order they are written would pick the wrong one.
        Assert.Equal([HtmlKey, OtherKey], GoogleChromeKeySource.FindKeys(MainScript));
    }

    [Fact]
    public async Task GoogleChrome_AStoredKeyIsUsedWithoutLookingAnythingUp()
    {
        var handler = ChromeWithScripts(StoredKey);
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

        Assert.Equal("好", Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW")).Text);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(StoredKey, sent.Headers["X-Goog-API-Key"]);
    }

    [Fact]
    public async Task GoogleChrome_WithNoKey_LooksItUpAndKeepsIt()
    {
        var handler = ChromeWithScripts(HtmlKey);
        var kept = "";
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => kept, key => kept = key);

        Assert.Equal("好", Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW")).Text);
        Assert.Equal(HtmlKey, kept);

        // Kept, so the second screen goes straight to translateHtml.
        await engine.TranslateAsync(["OK again"], "zh-TW");
        Assert.Equal(
            [GoogleChromeKeySource.LoaderUrl, MainScriptUrl, "translateHtml", "translateHtml"],
            handler.Requests.Select(r => r.Uri.Contains("/v1/translateHtml") ? "translateHtml" : r.Uri));
    }

    [Fact]
    public async Task GoogleChrome_ARefusedKey_IsLookedUpAgainAndTheRequestSentOnceMore()
    {
        var handler = ChromeWithScripts(HtmlKey);
        var kept = StoredKey;
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => kept, key => kept = key);

        Assert.Equal("好", Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW")).Text);
        Assert.Equal(HtmlKey, kept);
        Assert.Equal(HtmlKey, handler.Requests[^1].Headers["X-Goog-API-Key"]);
    }

    [Fact]
    public async Task GoogleChrome_ABadRequestThatIsNotAboutTheKey_LooksNothingUp()
    {
        var handler = new Canned(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""[3,"Invalid JSON payload received."]"""),
        });
        var engine = new GoogleChromeTranslator(new HttpClient(handler), () => StoredKey);

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GoogleChrome_AFailedLookup_IsNotRepeatedOnEveryRequest()
    {
        // The scripts no longer have a key in them: what Google rewriting them would look like.
        var handler = ChromeWithScripts(HtmlKey, mainScript: "nothing here");
        var engine = new GoogleChromeTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));
        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["OK"], "zh-TW"));

        // Realtime translation sends a request every few seconds, and each lookup is 300 KB.
        Assert.Equal([GoogleChromeKeySource.LoaderUrl, MainScriptUrl], handler.Requests.Select(r => r.Uri));
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

    // ---- DeepL ---------------------------------------------------------------------------

    [Theory]
    [InlineData("abc:fx", "https://api-free.deepl.com/v2/translate")]
    [InlineData("abc", "https://api.deepl.com/v2/translate")]
    public async Task DeepL_SendsTheKeyToTheHostItBelongsTo(string key, string endpoint)
    {
        var handler = new Canned(_ => Json("""{"translations":[{"detected_source_language":"EN","text":"你好"}]}"""));
        var engine = new DeepLTranslator(new HttpClient(handler), key);

        await engine.TranslateAsync(["Hello"], "zh-TW");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, sent.Uri);
        Assert.Equal($"DeepL-Auth-Key {key}", sent.Headers["Authorization"]);
    }

    [Fact]
    public async Task DeepL_SendsEveryTextInOneRequest_AndReadsTheDetectedLanguageBack()
    {
        var handler = new Canned(_ => Json("""
            {"translations":[
              {"detected_source_language":"JA","text":"你好"},
              {"detected_source_language":"ZH","text":"世界"}]}
            """));
        var engine = new DeepLTranslator(new HttpClient(handler), "k");

        var answers = await engine.TranslateAsync(["こんにちは", "世界"], "zh-TW");

        Assert.Equal("text=%E3%81%93%E3%82%93%E3%81%AB%E3%81%A1%E3%81%AF&text=%E4%B8%96%E7%95%8C&target_lang=ZH-HANT",
            Assert.Single(handler.Bodies));
        Assert.Equal([("你好", "ja"), ("世界", "zh-CN")], answers.Select(a => (a.Text, a.DetectedLanguage)));
    }

    // The app offers American English and Brazilian Portuguese as targets; DeepL's source list has
    // one Chinese and no variants at all.
    [Theory]
    [InlineData("en", "zh-TW", "source_lang=ZH&target_lang=EN-US")]
    [InlineData("pt", "zh-CN", "source_lang=ZH&target_lang=PT-BR")]
    [InlineData("zh-CN", "no", "source_lang=NB&target_lang=ZH-HANS")]
    public async Task DeepL_WritesLanguagesTheWayItsApiWantsThem(string target, string source, string expected)
    {
        var handler = new Canned(_ => Json("""{"translations":[{"text":"x"}]}"""));
        var engine = new DeepLTranslator(new HttpClient(handler), "k");

        await engine.TranslateAsync(["a"], target, source);

        Assert.EndsWith(expected, Assert.Single(handler.Bodies));
    }

    [Fact]
    public void DeepL_PacksAtMostFiftyTextsARequest()
    {
        var engine = new DeepLTranslator(new HttpClient(new Canned()), "k");

        Assert.Equal([50, 10], engine.Plan(Enumerable.Range(0, 60).Select(i => $"line {i}").ToArray()).Select(g => g.Count));
    }

    [Fact]
    public async Task DeepL_ARefusedKey_IsAnEngineFailureWithItsStatus()
    {
        var engine = new DeepLTranslator(new HttpClient(new Canned(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), "k");

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["a"], "ja"));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    // ---- TranSmart -----------------------------------------------------------------------

    [Fact]
    public async Task TranSmart_SendsEveryTextInOneListAndMapsItsLanguageCodes()
    {
        var handler = new Canned(_ => Json("""{"header":{"ret_code":"succ"},"auto_translation":["一","二"],"src_lang":"ja","tgt_lang":"zh"}"""));
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["いち", "に"], "zh-CN", "ja");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://transmart.qq.com/api/imt", request.Uri);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(["いち", "に"], body.GetProperty("source").GetProperty("text_list").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("ja", body.GetProperty("source").GetProperty("lang").GetString());
        Assert.Equal("zh", body.GetProperty("target").GetProperty("lang").GetString());
        Assert.StartsWith("browser-chrome-", body.GetProperty("header").GetProperty("client_key").GetString());
        Assert.Equal(["一", "二"], answers.Select(a => a.Text));

        // Given, not detected: nothing to report.
        Assert.All(answers, a => Assert.Equal("", a.DetectedLanguage));
    }

    [Fact]
    public async Task TranSmart_ADetectedRequestIsSentOncePerScript_AndAnsweredInOrder()
    {
        var handler = new Canned(request =>
        {
            var texts = JsonDocument.Parse(request.Body).RootElement.GetProperty("source").GetProperty("text_list")
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            var language = texts[0].Any(c => c is >= '぀' and <= 'ヿ') ? "ja" : texts[0].Any(c => c >= '가') ? "ko" : "en";
            return Json(JsonSerializer.Serialize(new
            {
                header = new { ret_code = "succ" },
                auto_translation = texts.Select(t => $"<{t}>"),
                src_lang = language,
            }));
        });
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["こんにちは", "Hello", "おはよう", "안녕"], "zh-TW");

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(["<こんにちは>", "<Hello>", "<おはよう>", "<안녕>"], answers.Select(a => a.Text));
        Assert.Equal(["ja", "en", "ja", "ko"], answers.Select(a => a.DetectedLanguage));
        Assert.All(handler.Bodies, body => Assert.Contains("\"lang\":\"auto\"", body));
    }

    [Fact]
    public async Task TranSmart_BusyIsAskedAgain()
    {
        var calls = 0;
        var handler = new Canned(_ => Json(Interlocked.Increment(ref calls) == 1
            ? """{"header":{"ret_code":"busy"},"message":"Server is busy now, (10000), please retry later"}"""
            : """{"header":{"ret_code":"succ"},"auto_translation":["好"]}"""));
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync(["OK"], "zh-TW", "en"));

        Assert.Equal("好", answer.Text);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TranSmart_ARefusalInsideA200_IsAFailure()
    {
        var handler = new Canned(_ => Json("""{"header":{"ret_code":"outOfLimit"}}"""));
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["Hello"], "ja", "en"));

        Assert.Contains("outOfLimit", failure.Message);
        Assert.Single(handler.Requests);
    }

    // Refused before anything is sent, so the backups take over at once.
    [Theory]
    [InlineData("nl", "en")]
    [InlineData("zh-TW", "pl")]
    [InlineData("zh-TW", "ru")]
    [InlineData("zh-CN", "th")]
    [InlineData("sv", null)]
    public async Task TranSmart_APairItDoesNotHave_IsRefusedWithoutARequest(string target, string? source)
    {
        var handler = new Canned();
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["Hello"], target, source));

        Assert.Contains("unsupported", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("ja", "zh-TW", true)]
    [InlineData("ko", "zh-CN", true)]
    [InlineData(null, "zh-TW", true)]
    [InlineData(null, "nl", false)]
    [InlineData("nl", "zh-TW", false)]
    [InlineData("ru", "ja", true)]
    [InlineData("ru", "zh-TW", false)]
    [InlineData("zh-TW", "ru", true)]
    [InlineData("th", "zh-CN", false)]
    public void TranSmart_KnowsWhichPairsItHas(string? source, string target, bool expected) =>
        Assert.Equal(expected, TranSmartTranslator.Supports(source, target));

    [Fact]
    public async Task TranSmart_ALineBreakInsideATextIsJoined_AndABlankLineKept()
    {
        var handler = new Canned(request => Json(JsonSerializer.Serialize(new
        {
            header = new { ret_code = "succ" },
            auto_translation = JsonDocument.Parse(request.Body).RootElement.GetProperty("source").GetProperty("text_list")
                .EnumerateArray().Select(e => $"<{e.GetString()}>"),
        })));
        var engine = new TranSmartTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["おはよう\nございます", "First line\nwraps.\n\nSecond."], "zh-TW", "ja");

        Assert.Equal(["<おはようございます>", "<First line wraps.>\n\n<Second.>"], answers.Select(a => a.Text));
    }

    [Theory]
    [InlineData("おはよう\nございます", "おはようございます")]
    [InlineData("This sentence\r\n  wraps.", "This sentence wraps.")]
    [InlineData("「はい」\nOK", "「はい」 OK")]
    [InlineData("안녕하세요\n반갑습니다", "안녕하세요 반갑습니다")]
    [InlineData("  one line  ", "one line")]
    public void JoinLines_JoinsWithASpaceOnlyWhereTheScriptUsesOne(string paragraph, string expected) =>
        Assert.Equal(expected, BatchTranslator.JoinLines(paragraph));

    [Theory]
    [InlineData("装備", "裝備（裝備）", "zh-TW", "裝備")]
    [InlineData("攻撃力", "攻擊力（Attack Force）", "zh-TW", "攻擊力")]
    [InlineData("戻る", "Back（後退）", "zh-TW", "後退")]
    [InlineData("いいえ", "否（No）", "zh-TW", "否")]
    [InlineData("ロード", "Load（Load）", "zh-TW", "Load")]
    [InlineData("装備", "장비 (Equipment)", "ko", "장비")]
    [InlineData("閉じる", "닫기 (Close)", "ko", "닫기")]
    [InlineData("戻る", "Retour (Zurück)", "fr", "Retour (Zurück)")]
    [InlineData("体力（HP）", "體力（HP）", "zh-TW", "體力（HP）")]
    [InlineData("はい", "是的。", "zh-TW", "是的。")]
    public void TranSmart_TheGlossAfterAShortAnswerIsTakenOff(string piece, string answer, string target, string expected) =>
        Assert.Equal(expected, TranSmartTranslator.WithoutGloss(piece, answer, target));

    // ---- Youdao --------------------------------------------------------------------------

    // Shaped like the real page and script, made up. The key getter's key is put together at run
    // time, as FakeKey's are, so nothing here reads as a key copied from Youdao's page.
    private const string YoudaoPage =
        """<html><script src="https://shared.ydstatic.com/dict/translation-website/1.0.7/js/app.fake.js"></script></html>""";

    private static readonly string YoudaoGetterKey = "Getter" + new string('K', 10);

    private static readonly string YoudaoScript =
        """i={secretKey:"",decodeKey:"ydsecret://query/key/TEST",decodeIv:"ydsecret://query/iv/TEST",allowStroke:!1};""" +
        $$"""fetchTextTranslateSecretKey:async({commit:e},t)=>{const o="webfanyi-key-getter-2025",a="{{YoudaoGetterKey}}";return o}""";

    private const string YoudaoKey = """{"code":0,"data":{"secretKey":"SECRET","aesKey":"","aesIv":""}}""";

    /// <summary>A Youdao endpoint that answers each line it is sent with that line in brackets.</summary>
    private static Canned YoudaoEndpoint(
        Func<IReadOnlyList<string>, string>? type = null, Func<IReadOnlyList<string>, string>? answer = null) =>
        new(request =>
        {
            if (request.Uri == "https://fanyi.youdao.com/") return Text(YoudaoPage);
            if (request.Uri.EndsWith(".js")) return Text(YoudaoScript);
            if (request.Uri.StartsWith("https://dict.youdao.com/webtranslate/key")) return Json(YoudaoKey);

            var lines = YoudaoField(request.Body, "i").Split('\n');
            var json = answer?.Invoke(lines) ?? JsonSerializer.Serialize(new
            {
                code = 0,
                type = type?.Invoke(lines) ?? "ja2zh-CHT",
                translateResult = lines.Select(line => new[] { new { tgt = $"<{line}>", src = line } }),
            });
            return Text(YoudaoEncrypt(json));
        });

    private static string YoudaoField(string body, string name) => Uri.UnescapeDataString(
        body.Split('&').Single(field => field.StartsWith(name + "=")).Split('=', 2)[1].Replace('+', ' '));

    private static string YoudaoEncrypt(string json)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("ydsecret://query/key/TEST"));
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(json),
            System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("ydsecret://query/iv/TEST")));
        return Convert.ToBase64String(cipher).Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public async Task Youdao_FetchesItsCredentialsOnce_AndSendsTheTextsAsSignedLines()
    {
        var handler = YoudaoEndpoint();
        var engine = new YoudaoTranslator(new HttpClient(handler));

        var first = await engine.TranslateAsync(["早く逃げて！", "装備"], "zh-TW", "ja");
        await engine.TranslateAsync(["戻る"], "zh-TW", "ja");

        var key = Assert.Single(handler.Requests, r => r.Uri.StartsWith("https://dict.youdao.com/webtranslate/key"));
        Assert.Contains("keyid=webfanyi-key-getter-2025", key.Uri);
        var mysticTime = Uri.UnescapeDataString(key.Uri.Split('?')[1].Split('&').Single(f => f.StartsWith("mysticTime=")).Split('=')[1]);
        Assert.Contains($"sign={YoudaoSession.Sign(mysticTime, YoudaoGetterKey)}", key.Uri);
        var translate = handler.Requests.Where(r => r.Uri == "https://dict.youdao.com/webtranslate").ToList();
        Assert.Equal(2, translate.Count);
        Assert.Equal("早く逃げて！\n装備", YoudaoField(translate[0].Body, "i"));
        Assert.Equal("ja", YoudaoField(translate[0].Body, "from"));
        Assert.Equal("zh-CHT", YoudaoField(translate[0].Body, "to"));
        Assert.Equal("webfanyi", YoudaoField(translate[0].Body, "keyid"));
        Assert.Equal(YoudaoSession.Sign(YoudaoField(translate[0].Body, "mysticTime"), "SECRET"), YoudaoField(translate[0].Body, "sign"));
        Assert.Equal(["<早く逃げて！>", "<装備>"], first.Select(a => a.Text));
    }

    [Fact]
    public void Youdao_SignatureIsTheMd5OfTheTimeAndKey() =>
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(
                "client=fanyideskweb&mysticTime=1700000000000&product=webfanyi&key=SECRET"))).ToLowerInvariant(),
            YoudaoSession.Sign("1700000000000", "SECRET"));

    // A line break inside a text would come back as one entry more, and every answer after it
    // would belong to the text before.
    [Fact]
    public async Task Youdao_ALineBreakInsideATextIsJoinedBeforeItIsSent()
    {
        var handler = YoudaoEndpoint();
        var engine = new YoudaoTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["おはよう\nございます", "This sentence\nwraps.", "End"], "zh-TW", "ja");

        var sent = YoudaoField(handler.Requests.Single(r => r.Uri == "https://dict.youdao.com/webtranslate").Body, "i");
        Assert.Equal("おはようございます\nThis sentence wraps.\nEnd", sent);
        Assert.Equal(["<おはようございます>", "<This sentence wraps.>", "<End>"], answers.Select(a => a.Text));
    }

    [Fact]
    public async Task Youdao_ParagraphsAreSentAsLinesAndPutBackWithTheBlankLine()
    {
        var engine = new YoudaoTranslator(new HttpClient(YoudaoEndpoint()));

        var answer = Assert.Single(await engine.TranslateAsync(["First.\n\nSecond."], "zh-TW", "en"));

        Assert.Equal("<First.>\n\n<Second.>", answer.Text);
    }

    [Fact]
    public async Task Youdao_ALineAnsweredInSeveralSentences_IsOneTranslation()
    {
        var engine = new YoudaoTranslator(new HttpClient(YoudaoEndpoint(answer: _ =>
            """{"code":0,"type":"en2zh-CHT","translateResult":[[{"tgt":"這是第一句話。"},{"tgt":"這是第二個！\n"}]]}""")));

        var answer = Assert.Single(await engine.TranslateAsync(["This is the first. This is the second!"], "zh-TW"));

        Assert.Equal("這是第一句話。這是第二個！", answer.Text);
        Assert.Equal("en", answer.DetectedLanguage);
    }

    [Fact]
    public async Task Youdao_AnAnswerWithALineMoreOrLess_FailsTheRequest()
    {
        var engine = new YoudaoTranslator(new HttpClient(YoudaoEndpoint(answer: _ =>
            """{"code":0,"type":"ja2zh-CHT","translateResult":[[{"tgt":"一"}],[{"tgt":"二"}],[{"tgt":"三"}]]}""")));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["いち", "に"], "zh-TW", "ja"));
    }

    [Fact]
    public async Task Youdao_ADetectedRequestIsSentOncePerScript_AndReportsWhatEachWasReadAs()
    {
        var handler = YoudaoEndpoint(type: lines =>
            lines[0].Any(c => c >= '가') ? "ko2zh-CHS" : lines[0].Any(c => c > 0x2000) ? "ja2zh-CHS" : "en2zh-CHS");
        var engine = new YoudaoTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["안녕하세요", "Hello", "こんにちは"], "zh-CN");

        Assert.Equal(3, handler.Requests.Count(r => r.Uri == "https://dict.youdao.com/webtranslate"));
        Assert.Equal(["<안녕하세요>", "<Hello>", "<こんにちは>"], answers.Select(a => a.Text));
        Assert.Equal(["ko", "en", "ja"], answers.Select(a => a.DetectedLanguage));
        Assert.All(handler.Requests.Where(r => r.Uri == "https://dict.youdao.com/webtranslate"),
            r => Assert.Equal("zh-CHS", YoudaoField(r.Body, "to")));
    }

    [Fact]
    public async Task Youdao_ARefusalIsAskedAgainOnceWithFreshCredentials()
    {
        var refused = true;
        var inner = YoudaoEndpoint();
        var handler = new Canned(request =>
        {
            if (request.Uri == "https://dict.youdao.com/webtranslate" && refused)
            {
                refused = false;
                return Text(YoudaoEncrypt("""{"code":50}"""));
            }
            return inner.Answer(request);
        });
        var engine = new YoudaoTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync(["装備"], "zh-TW", "ja"));

        Assert.Equal("<装備>", answer.Text);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Contains("/webtranslate/key")));
    }

    [Fact]
    public async Task Youdao_AScriptWithoutTheKeyGetter_SaysSo()
    {
        var handler = new Canned(request => request.Uri.EndsWith(".js") ? Text("nothing here") : Text(YoudaoPage));
        var engine = new YoudaoTranslator(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.TranslateAsync(["装備"], "zh-TW", "ja"));

        Assert.Contains("key getter", failure.Message);
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

        /// <summary>What this handler answers, for another that answers most requests the same way.</summary>
        public HttpResponseMessage Answer(SentRequest request) => (answer ?? (_ => Json("[]")))(request);

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
