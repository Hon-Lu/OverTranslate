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

// What each speech engine puts on the wire and how a service picks one of its engines, against canned answers.
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

    // #436: two pieces to a host that does not answer took two connection timeouts, one after the
    // other. The first failure ends the text and cancels the pieces still going.
    [Fact]
    public async Task APieceThatFails_CancelsTheOthers_AndIsTheFailure()
    {
        var handler = new FirstFailsRestHang();
        var engine = new GoogleWebSpeech(new HttpClient(handler));
        var text = string.Concat(Enumerable.Repeat("これは長い文章です。", 50));   // 500 characters, three pieces

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.SynthesizeAsync(text, "ja"));

        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(2, handler.Cancelled);
    }

    // #436: one long request ran past the request timeout; pieces, a few at a time, joined in order.
    [Fact]
    public async Task Microsoft_ReadsALongText_InPieces_AFewAtATime_JoinedInOrder()
    {
        var handler = new Pieces();
        var engine = new MicrosoftSpeech(new HttpClient(handler));
        var text = string.Concat(Enumerable.Range(1, 80).Select(i => $"S{i}. Some words to read aloud here. "));

        var audio = await engine.SynthesizeAsync(text, "en");

        Assert.True(handler.Count > 1);
        Assert.Equal(handler.Count, audio.Length);
        Assert.InRange(handler.Most, 2, MicrosoftVoices.Parallel);
        Assert.Equal(audio.OrderBy(b => b), audio);
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
        var narrow = new Fake("Narrow", "ja");
        var wide = new Fake("Wide", "ja", "sl");
        var synthesizer = new SpeechSynthesizer("Test", [narrow, wide]);

        var (_, engine) = await synthesizer.SynthesizeAsync("Dober dan", "sl");

        Assert.Equal("Wide", engine);
        Assert.Equal(0, narrow.Calls);
    }

    [Fact]
    public async Task Synthesizer_FallsBackWhenAnEngineFails()
    {
        var failing = new Fake("Rpc", "ja") { Fails = true };
        var synthesizer = new SpeechSynthesizer("Test", [failing, new Fake("Web", "ja")]);

        var (_, engine) = await synthesizer.SynthesizeAsync("こんにちは", "ja");

        Assert.Equal("Web", engine);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task Synthesizer_ALanguageNobodySpeaks_IsAFailure()
    {
        var synthesizer = new SpeechSynthesizer("Test", [new Fake("Rpc", "ja")]);

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(() => synthesizer.SynthesizeAsync("x", "xx"));
        Assert.Equal(SpeechFailure.Unsupported, ex.Reason);
    }

    // The old service knew fifteen languages and read the rest with an English voice. Microsoft and
    // Bing read every language the pickers offer; Google every one but Slovenian and Persian, which
    // both its addresses refuse.
    [Fact]
    public void EveryLanguageThePickersOffer_HasAVoice_ExceptTwoOnGoogle()
    {
        var http = new HttpClient(new Canned());
        var google = new SpeechSynthesizer("Google", [new GoogleRpcSpeech(http), new GoogleWebSpeech(http)]);
        var microsoft = new SpeechSynthesizer("Microsoft", [new MicrosoftSpeech(http)]);
        var bing = new SpeechSynthesizer("Bing", [new BingSpeech(http)]);

        var codes = LanguageData.SourceLanguages.Concat(LanguageData.TargetLanguages)
            .Select(language => language.Code)
            .Where(code => !LanguageData.IsAutomaticSource(code))
            .Select(EngineLanguage.ToEngine)
            .Distinct()
            .ToList();

        Assert.All(codes, code => Assert.True(microsoft.Supports(code), code));
        Assert.All(codes, code => Assert.True(bing.Supports(code), code));
        Assert.Equal(["fa", "sl"], codes.Where(code => !google.Supports(code)).Order());
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

    /// <summary>
    /// Microsoft's token, then speech that takes a moment: each piece answered with the number of
    /// its first sentence, counting how many were being answered at once.
    /// </summary>
    private sealed class Pieces : HttpMessageHandler
    {
        private int _running, _most, _count;

        public int Most => Volatile.Read(ref _most);
        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.ToString().Contains("/apps/endpoint"))
                return Json("""{"t":"header.eyJleHAiOjQxMDI0NDQ4MDB9.sig","r":"eastasia"}""");

            Interlocked.Increment(ref _count);
            var now = Interlocked.Increment(ref _running);
            lock (this) _most = Math.Max(_most, now);
            await Task.Delay(50, cancellationToken);
            Interlocked.Decrement(ref _running);

            var number = int.Parse(System.Text.RegularExpressions.Regex.Match(body, @"S(\d+)\.").Groups[1].Value);
            return Audio([(byte)number]);
        }
    }

    /// <summary>The first piece (idx=0) cannot connect; every other one waits until it is cancelled.</summary>
    private sealed class FirstFailsRestHang : HttpMessageHandler
    {
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Query.Contains("&idx=0&"))
            {
                await Task.Delay(50, cancellationToken);
                throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable");
            }

            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancelled); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>An engine with the voices it is told to have, that answers or fails on cue.</summary>
    private sealed class Fake(string name, params string[] languages) : ISpeechEngine
    {
        public string Name => name;
        public int Calls { get; private set; }
        public bool Fails { get; init; }

        public bool Supports(string language) => languages.Contains(language);

        public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            if (Fails) throw new TranslationEngineException(name, "down");
            return [0xFF];
        }
    }
}
