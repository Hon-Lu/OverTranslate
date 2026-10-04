using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using Xunit;

namespace OverTranslate.Tests;

// What each speech engine puts on the wire and how the fallback picks one, against canned answers.
// No network: the real endpoints were checked by hand (.ai/translation-service-analysis/implementation/).
public class SpeechProtocolTests
{
    // ---- Cutting long texts ----------------------------------------------------------------

    [Fact]
    public void Split_CutsChineseAtItsPunctuation_NotMidWord()
    {
        var text = string.Concat(Enumerable.Repeat("今天天氣很好，我們去散步吧。", 20));

        var pieces = SpeechText.Split(text, 200);

        Assert.All(pieces, piece => Assert.True(piece.Length <= 200));
        Assert.All(pieces[..^1], piece => Assert.EndsWith("。", piece));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public void Split_CutsEnglishAtASpace_AndFoldsLineBreaks()
    {
        var text = string.Join("\n", Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 10));

        var pieces = SpeechText.Split(text, 200);

        Assert.All(pieces, piece => Assert.True(piece.Length <= 200));
        Assert.DoesNotContain(pieces, piece => piece.Contains('\n'));
        Assert.Equal(text.Replace('\n', ' '), string.Join(' ', pieces));
    }

    [Fact]
    public void Split_ATextWithNowhereToCut_IsCutAtTheLimit()
    {
        var pieces = SpeechText.Split(new string('あ', 450), 200);

        Assert.Equal([200, 200, 50], pieces.Select(p => p.Length));
    }

    // ---- Google -----------------------------------------------------------------------------

    [Fact]
    public async Task GoogleWeb_AsksForEachPieceAndJoinsThemInOrder()
    {
        var handler = new Canned(request =>
        {
            var index = request.Uri.Split("&idx=")[1].Split('&')[0];
            return Audio([0xFF, byte.Parse(index)]);
        });
        var engine = new GoogleWebSpeech(new HttpClient(handler));
        var text = string.Concat(Enumerable.Repeat("これは長い文章のテストです。", 30));

        var audio = await engine.SynthesizeAsync(text, "ja");

        var total = handler.Requests.Count;
        Assert.True(total > 1);
        Assert.All(handler.Requests, r => Assert.Contains("tl=ja", r.Uri));
        Assert.All(handler.Requests, r => Assert.Contains("client=tw-ob", r.Uri));
        Assert.All(handler.Requests, r => Assert.Contains($"total={total}", r.Uri));
        Assert.Equal(Enumerable.Range(0, total).SelectMany(i => new byte[] { 0xFF, (byte)i }), audio);
    }

    [Fact]
    public async Task GoogleRpc_SendsTheSpeechCall_AndDecodesTheAudio()
    {
        var handler = new Canned(_ => Json(RpcEnvelope(Convert.ToBase64String([0xFF, 0xF3, 1, 2]))));
        var engine = new GoogleRpcSpeech(new HttpClient(handler));

        var audio = await engine.SynthesizeAsync("你好", "zh-TW");

        var sent = Assert.Single(handler.Requests);
        Assert.Contains("rpcids=jQ1olc", sent.Uri);
        var call = JsonDocument.Parse(Uri.UnescapeDataString(sent.Body["f.req=".Length..])).RootElement[0][0];
        Assert.Equal("jQ1olc", call[0].GetString());
        Assert.Equal("你好", JsonDocument.Parse(call[1].GetString()!).RootElement[0].GetString());
        Assert.Equal("zh-TW", JsonDocument.Parse(call[1].GetString()!).RootElement[1].GetString());
        Assert.Equal([0xFF, 0xF3, 1, 2], audio);
    }

    [Fact]
    public async Task GoogleRpc_ARefusedCall_IsAnEngineFailure()
    {
        var handler = new Canned(_ => Json(")]}'\n\n" + """[["wrb.fr","jQ1olc",null,null,null,[13],"generic"]]"""));
        var engine = new GoogleRpcSpeech(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.SynthesizeAsync("你好", "zh-TW"));
    }

    // ---- Microsoft and Bing -----------------------------------------------------------------

    [Fact]
    public async Task Microsoft_SignsForAToken_ThenReadsInTheTaiwaneseVoice()
    {
        var handler = new Canned(request => request.Uri.Contains("/apps/endpoint")
            ? Json("""{"t":"header.eyJleHAiOjQxMDI0NDQ4MDB9.sig","r":"eastasia"}""")
            : Audio([0xFF, 0xF3]));
        var engine = new MicrosoftSpeech(new HttpClient(handler));

        await engine.SynthesizeAsync("你好 & <再見>", "zh-TW");
        await engine.SynthesizeAsync("第二句", "zh-TW");

        var token = Assert.Single(handler.Requests, r => r.Uri.Contains("/apps/endpoint"));
        Assert.StartsWith("MSTranslatorAndroidApp::", token.Headers["X-MT-Signature"]);

        var speech = handler.Requests.Where(r => r.Uri.Contains("tts.speech")).ToList();
        Assert.Equal(2, speech.Count);
        Assert.StartsWith("https://eastasia.tts.speech.microsoft.com/", speech[0].Uri);
        Assert.Equal("Bearer header.eyJleHAiOjQxMDI0NDQ4MDB9.sig", speech[0].Headers["Authorization"]);
        Assert.Contains("name='zh-TW-HsiaoChenNeural'", speech[0].Body);
        Assert.Contains("你好 &amp; &lt;再見&gt;", speech[0].Body);
    }

    [Fact]
    public void Microsoft_ReadsTheExpiryFromTheToken()
    {
        // {"exp":4102444800} — 2100-01-01.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), MicrosoftSpeech.ExpiryOf("h.eyJleHAiOjQxMDI0NDQ4MDB9.s"));
        Assert.True(MicrosoftSpeech.ExpiryOf("not a token") > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Bing_UsesThePagesCredentials_AndFetchesThemAgainAfterARefusal()
    {
        var refused = true;
        var handler = new Canned(request =>
        {
            if (request.Uri.EndsWith("/translator")) return Text(BingPage);
            if (refused) { refused = false; return Json("""{"statusCode":205}"""); }
            return Audio([0xFF, 0xF3]);
        });
        var engine = new BingSpeech(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslationEngineException>(() => engine.SynthesizeAsync("Hej", "sv"));
        var audio = await engine.SynthesizeAsync("Hej", "sv");

        Assert.Equal([0xFF, 0xF3], audio);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.EndsWith("/translator")));
        var speech = handler.Requests.Last();
        Assert.Contains("/tfettts?isVertical=1&IG=ABCDEF0123&IID=translator.5077", speech.Uri);
        Assert.Contains("token=TOKEN0123456789abcdef0123456789ab", speech.Body);
        Assert.Contains(Uri.EscapeDataString("name='sv-SE-SofieNeural'"), speech.Body.Replace("+", "%20"));
    }

    // ---- Picking an engine ------------------------------------------------------------------

    [Fact]
    public async Task Synthesizer_SkipsEnginesWithNoVoice_WithoutAskingThem()
    {
        var google = new Fake("Google", "ja");
        var microsoft = new Fake("Microsoft", "ja", "sl");
        var synthesizer = new SpeechSynthesizer([google, microsoft]);

        var (_, engine) = await synthesizer.SynthesizeAsync("Dober dan", "sl");

        Assert.Equal("Microsoft", engine);
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task Synthesizer_FallsBackWhenAnEngineFails()
    {
        var failing = new Fake("Google", "ja") { Fails = true };
        var synthesizer = new SpeechSynthesizer([failing, new Fake("Microsoft", "ja")]);

        var (_, engine) = await synthesizer.SynthesizeAsync("こんにちは", "ja");

        Assert.Equal("Microsoft", engine);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task Synthesizer_ALanguageNobodySpeaks_IsAFailure()
    {
        var synthesizer = new SpeechSynthesizer([new Fake("Google", "ja")]);

        await Assert.ThrowsAsync<TranslationEngineException>(() => synthesizer.SynthesizeAsync("x", "xx"));
    }

    // Stopping has to abort the download, and must not be mistaken for a failure that moves on to
    // the next engine.
    [Fact]
    public async Task Synthesizer_Cancelled_StopsAndAsksNobodyElse()
    {
        var hanging = new Fake("Google", "ja") { Hangs = true };
        var next = new Fake("Microsoft", "ja");
        var synthesizer = new SpeechSynthesizer([hanging, next]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synthesizer.SynthesizeAsync("こんにちは", "ja", cts.Token));

        Assert.Equal(0, next.Calls);
    }

    // The old service knew fifteen languages and read the rest with an English voice.
    [Fact]
    public void EveryLanguageThePickersOffer_HasAVoice()
    {
        var http = new HttpClient(new Canned());
        var synthesizer = new SpeechSynthesizer(
            [new GoogleRpcSpeech(http), new GoogleWebSpeech(http), new MicrosoftSpeech(http), new BingSpeech(http)]);

        var codes = LanguageData.SourceLanguages.Concat(LanguageData.TargetLanguages)
            .Select(language => language.Code)
            .Where(code => !LanguageData.IsAutomaticSource(code))
            .Distinct();

        Assert.All(codes, code => Assert.True(synthesizer.Supports(EngineLanguage.ToEngine(code)), code));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private const string BingPage =
        """<html><script>var params_AbusePreventionHelper = [1727400000000,"TOKEN0123456789abcdef0123456789ab",3600000];IG:"ABCDEF0123"</script><div data-iid="translator.5077"></div></html>""";

    private static string RpcEnvelope(string base64) =>
        ")]}'\n\n" + JsonSerializer.Serialize(new object?[]
        {
            new object?[] { "wrb.fr", "jQ1olc", JsonSerializer.Serialize(new object[] { base64 }), null, null, null, "generic" },
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Audio(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed record SentRequest(string Uri, string Body, Dictionary<string, string> Headers);

    /// <summary>Answers every request from a function, remembering what was asked.</summary>
    private sealed class Canned(Func<SentRequest, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests { get { lock (_requests) return [.. _requests]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var sent = new SentRequest(request.RequestUri!.ToString(), body, headers);
            lock (_requests) _requests.Add(sent);
            return (answer ?? (_ => Audio([0xFF])))(sent);
        }
    }

    /// <summary>An engine with the voices it is told to have, that answers, fails or hangs on cue.</summary>
    private sealed class Fake(string name, params string[] languages) : ISpeechEngine
    {
        public string Name => name;
        public int Calls { get; private set; }
        public bool Fails { get; init; }
        public bool Hangs { get; init; }

        public bool Supports(string language) => languages.Contains(language);

        public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Hangs) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fails) throw new TranslationEngineException(name, "down");
            return [0xFF];
        }
    }
}
