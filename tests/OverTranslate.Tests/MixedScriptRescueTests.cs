using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverTranslate.Translation;
using OverTranslate.Translation.Google;
using Xunit;

namespace OverTranslate.Tests;

// 「角色名稱：艾莉絲 She has been waiting…」 into Chinese: Google (Web) and (RPC) hand it back as it
// was sent, and translate it given sl=en (measured 2026-09-28). The texts below are the ones
// measured; the answers are canned in the shapes those endpoints gave.
public class MixedScriptRescueTests
{
    private const string Mixed = "角色名稱：艾莉絲 She has been waiting for you since morning.";
    private const string Clause = "She has been waiting for you since morning";
    private const string Rescued = "角色名稱：艾莉絲 她從早上就在等你了。";

    // ---- Which texts count ---------------------------------------------------------------

    [Theory]
    [InlineData(Mixed, Clause)]
    [InlineData("提示：Press any key to continue", "Press any key to continue")]
    [InlineData("キャラ名：アリス She has been waiting.", "She has been waiting")]
    [InlineData("這個 API 要怎麼用", null)]
    [InlineData("Lv.5 艾莉絲", null)]
    [InlineData("123", null)]
    [InlineData("She has been waiting for you since morning.", null)]   // nothing to be mixed with
    [InlineData("中文", null)]
    public void LatinClause_IsASentenceBesideChineseJapaneseOrKorean(string text, string? expected)
    {
        Assert.Equal(expected, MixedScriptText.LatinClause(text));
    }

    [Fact]
    public void ATextLeftWhole_IsASuspect_WhateverTheEngineSaysItDetected()
    {
        // Web read the one as en and the other as zh-CN; both were left.
        Assert.NotNull(MixedScriptText.UntranslatedClause(Mixed, new(Mixed, "en"), "zh-TW"));
        Assert.NotNull(MixedScriptText.UntranslatedClause(
            "提示：Press any key to continue", new("提示：Press any key to continue", "zh-CN"), "zh-TW"));
    }

    // A simplified label comes back in traditional characters, and its sentence as it was.
    [Fact]
    public void ALabelOnlyConverted_IsASuspect_WhenReadAsTheTargetLanguage()
    {
        Assert.NotNull(MixedScriptText.UntranslatedClause(
            "角色名称：艾莉丝 She has been waiting for you since morning.",
            new("角色名稱：艾莉絲 She has been waiting for you since morning.", "zh-CN"), "zh-TW"));
    }

    // A Japanese label that was translated: asking again in English would lose it.
    [Fact]
    public void ALabelTranslatedFromAnotherLanguage_IsNotASuspect()
    {
        Assert.Null(MixedScriptText.UntranslatedClause(
            "アリス：She has been waiting for you since morning.",
            new("愛麗絲：She has been waiting for you since morning.", "ja"), "zh-TW"));
    }

    [Fact]
    public void ASentenceThatWasTranslated_IsNotASuspect()
    {
        Assert.Null(MixedScriptText.UntranslatedClause(Mixed, new(Rescued, "en"), "zh-TW"));
    }

    // ---- Google (Web) --------------------------------------------------------------------

    [Fact]
    public async Task GoogleWeb_AMixedTextLeftAsItWas_IsAskedAgainInItsSentencesLanguage()
    {
        var handler = new Web(probeLanguage: "en");
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["Hello", Mixed], "zh-TW");

        Assert.Equal(["你好", Rescued], answers.Select(a => a.Text));
        Assert.Equal("en", answers[1].DetectedLanguage);
        Assert.All(answers, a => Assert.False(a.Untranslated));

        Assert.Equal(3, handler.Sent.Count);
        Assert.Equal("auto", handler.Sent[1].Source);
        Assert.Equal(new[] { Clause }, handler.Sent[1].Texts);
        Assert.Equal("en", handler.Sent[2].Source);
        Assert.Equal(new[] { Mixed }, handler.Sent[2].Texts);
    }

    // Latin script is not always English.
    [Fact]
    public async Task GoogleWeb_TheSentencesLanguage_IsWhatTheProbeSays()
    {
        var handler = new Web(probeLanguage: "fr");
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        await engine.TranslateAsync([Mixed], "zh-TW");

        Assert.Equal("fr", handler.Sent[^1].Source);
    }

    [Theory]
    [InlineData("這個 API 要怎麼用")]
    [InlineData("Lv.5 艾莉絲")]
    [InlineData("123")]
    public async Task GoogleWeb_ATextThatIsRightAsItIs_IsSentOnce(string text)
    {
        var handler = new Web(probeLanguage: "en");
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync([text], "zh-TW"));

        Assert.Single(handler.Sent);
        Assert.Equal(text, answer.Text);
        Assert.False(answer.Untranslated);
    }

    [Fact]
    public async Task GoogleWeb_ASentenceAlreadyInTheTargetLanguage_IsNotAskedAgain()
    {
        var handler = new Web(probeLanguage: "zh-TW");
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync([Mixed], "zh-TW"));

        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal(Mixed, answer.Text);
        Assert.False(answer.Untranslated);
    }

    [Fact]
    public async Task GoogleWeb_AGivenSourceLanguage_IsNotSecondGuessed()
    {
        var handler = new Web(probeLanguage: "en");
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        await engine.TranslateAsync([Mixed], "zh-TW", "zh-TW");

        Assert.Single(handler.Sent);
    }

    // The first answer was good for the rest of the request; the one it could not put right keeps
    // what it had and says so, so a cache does not keep it.
    [Fact]
    public async Task GoogleWeb_ARescueThatFails_KeepsTheFirstAnswerAndMarksIt()
    {
        var handler = new Web(probeLanguage: "en", failGivenSource: true);
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["Hello", Mixed], "zh-TW");

        Assert.Equal(["你好", Mixed], answers.Select(a => a.Text));
        Assert.False(answers[0].Untranslated);
        Assert.True(answers[1].Untranslated);
    }

    // What the engine answers with the language given is its answer, even unchanged; marking it
    // would have it asked for forever.
    [Fact]
    public async Task GoogleWeb_ARescueThatStillComesBackAsItWas_IsTakenAsItStands()
    {
        var handler = new Web(probeLanguage: "en", rescueAnswer: Mixed);
        var engine = new GoogleWebTranslator(new HttpClient(handler));

        var answer = Assert.Single(await engine.TranslateAsync([Mixed], "zh-TW"));

        Assert.Equal(Mixed, answer.Text);
        Assert.False(answer.Untranslated);
    }

    // ---- Google (RPC) --------------------------------------------------------------------

    // RPC reports the text as zh-TW, which is the target: what it detected is no help either.
    [Fact]
    public async Task GoogleRpc_AMixedTextLeftAsItWas_IsAskedAgainInItsSentencesLanguage()
    {
        var sent = new List<(string Source, string Text)>();
        var handler = new Handler(request =>
        {
            var calls = RpcCalls(request.Body);
            lock (sent) sent.AddRange(calls.Select(c => (c.Source, c.Text)));

            return Text(")]}'\n\n" + JsonSerializer.Serialize(calls.Select((call, i) => new object?[]
            {
                "wrb.fr", "MkEWBc",
                call switch
                {
                    { Source: "en" } => RpcData(Rescued, "en"),
                    { Text: Clause } => RpcData("她從早上就在等你了", "en"),
                    _ => RpcData(call.Text == "Hello" ? "你好" : call.Text, "zh-TW"),
                },
                null, null, null, (i + 1).ToString(),
            })));
        });
        var engine = new GoogleRpcTranslator(new HttpClient(handler));

        var answers = await engine.TranslateAsync(["Hello", Mixed], "zh-TW");

        Assert.Equal(["你好", Rescued], answers.Select(a => a.Text));
        Assert.Equal([("auto", "Hello"), ("auto", Mixed), ("auto", Clause), ("en", Mixed)], sent);
    }

    // ---- Helpers -------------------------------------------------------------------------

    /// <summary>
    /// Google (Web) as measured: the mixed text comes back as it was with sl=auto, the sentence on its
    /// own is detected as <paramref name="probeLanguage"/>, and with a language given the text is translated.
    /// </summary>
    private sealed class Web(string probeLanguage, bool failGivenSource = false, string rescueAnswer = Rescued)
        : HttpMessageHandler
    {
        private readonly List<(string Source, string[] Texts)> _sent = [];

        public IReadOnlyList<(string Source, string[] Texts)> Sent { get { lock (_sent) return [.. _sent]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var source = uri.Split("sl=")[1].Split('&')[0];
            var texts = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&').Select(field => Uri.UnescapeDataString(field["q=".Length..].Replace('+', ' '))).ToArray();
            lock (_sent) _sent.Add((source, texts));

            if (source != "auto")
            {
                return failGivenSource
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json(JsonSerializer.Serialize(texts.Select(t => t == Mixed ? rescueAnswer : t)));
            }

            return Json(JsonSerializer.Serialize(texts.Select(t => t switch
            {
                "Hello" => new[] { "你好", "en" },
                Clause => ["她從早上就在等你了", probeLanguage],
                _ => [t, "en"],
            })));
        }
    }

    private sealed class Handler(Func<(string Uri, string Body), HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
    }

    /// <summary>The text and source language of each call in a batchexecute envelope.</summary>
    private static List<(string Text, string Source)> RpcCalls(string body)
    {
        var request = Uri.UnescapeDataString(body["f.req=".Length..].Replace('+', ' '));
        using var envelope = JsonDocument.Parse(request);
        return envelope.RootElement[0].EnumerateArray().Select(call =>
        {
            using var inner = JsonDocument.Parse(call[1].GetString()!);
            var args = inner.RootElement[0];
            return (args[0].GetString()!, args[1].GetString()!);
        }).ToList();
    }

    private static string RpcData(string translation, string detected) => JsonSerializer.Serialize(new object?[]
    {
        null,
        new object?[]
        {
            new object?[] { new object?[] { translation } },
            "zh-TW", 1, detected,
        },
    });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
}
