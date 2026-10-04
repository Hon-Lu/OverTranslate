using System.Windows;
using OverTranslate.Translation;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

// ResilientProvider's promises, pinned against engines that answer, fail, stall or hang on cue.
// None of this reaches the network.
public class ResilientProviderTests
{
    // When every engine fails, ResilientProvider hands back the original text so the rest of the
    // batch still renders. The realtime session must not mistake that stand-in for a translation — it
    // would cache it and draw the original text for that line until the session ended — so the block
    // says so.
    [Fact]
    public async Task EveryEngineFailing_MarksTheBlockUntranslated()
    {
        var provider = new ResilientProvider([Engine.Failing(), Engine.Failing()]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);
        Assert.Equal("Hello", blocks[0].TranslatedText);
    }

    // The unit is the primary's request. An engine that takes one text per request fails per text,
    // and only the text nobody answered is marked.
    [Fact]
    public async Task OnlyTheGroupNoEngineAnswered_IsMarkedUntranslated()
    {
        var provider = new ResilientProvider([Engine.OnePerRequest(text => text == "Hello" ? "你好" : null)]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello", "Broken"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.True(blocks[1].Untranslated);
    }

    // The font height a tilted line was measured with travels with its translation, for both the
    // chain and a single engine. Dropped here, the overlay would quietly size from the coverage
    // height again and draw the line ten times too large.
    [Fact]
    public async Task TheFontGlyphHeight_IsCarriedOntoTheTranslation()
    {
        List<OcrTextBlock> source =
            [new("TILTED", new Rect(0, 0, 300, 180), RenderGlyphHeight: 150) { FontGlyphHeight = 17 }];

        var (chained, _) = await new ResilientProvider([Engine.Answering(text => "傾斜")])
            .TranslateAsync(source, "EN", "ZH-HANT", "");
        var (single, _) = await new EngineProvider(Engine.Answering(text => "傾斜"))
            .TranslateAsync(source, "EN", "ZH-HANT", "");

        Assert.Equal(17, chained[0].FontGlyphHeight);
        Assert.Equal(150, chained[0].RenderGlyphHeight);
        Assert.Equal(17, single[0].FontGlyphHeight);
        Assert.Equal(150, single[0].RenderGlyphHeight);
    }

    // A text that translates to itself is still a translation, and must not be retried forever.
    [Fact]
    public async Task ATranslationIdenticalToTheSource_IsNotUntranslated()
    {
        var provider = new ResilientProvider([Engine.Answering(text => text)]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("OK"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
    }

    // An engine that answered but knows part of a text was left in the original says so, and the
    // block carries it like one nobody answered — without the rest of the group losing its answers.
    [Fact]
    public async Task AnAnswerTheEngineMarkedUntranslated_IsMarkedOnTheBlock()
    {
        var provider = new ResilientProvider([new Engine("Primary", (texts, _) =>
            Task.FromResult<IReadOnlyList<TextTranslation>>(
                [new("你好", "en"), new("提示：Press any key", "en") { Untranslated = true }]))]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello", "提示：Press any key"), "AUTO", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
        Assert.True(blocks[1].Untranslated);
        Assert.Equal("提示：Press any key", blocks[1].TranslatedText);
    }

    // The point of the whole change: a batch engine's screen is answered by one engine, all of it.
    [Fact]
    public async Task AScreenInOneRequest_IsServedByOneEngine()
    {
        var primary = Engine.Answering(text => "P:" + text);
        var provider = new ResilientProvider([primary, Engine.Answering(text => "B:" + text, "Backup")]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("a", "b", "c"), "EN", "ZH-HANT", "");

        Assert.All(blocks, block => Assert.StartsWith("P:", block.TranslatedText));
        Assert.Equal(1, primary.Calls);
        Assert.False(provider.LastUsage!.FallbackUsed);
    }

    // A failed request is far more often that request than the engine, so the user's engine is asked
    // again before anyone else is — which is what keeps the screen in the voice they chose.
    [Fact]
    public async Task AFailedPrimary_IsAskedAgainBeforeAnyBackup()
    {
        var calls = new List<string>();
        var provider = new ResilientProvider(
        [
            Engine.Answering(text => { lock (calls) calls.Add("primary"); return calls.Count > 1 ? "你好" : null; }),
            Engine.Answering(text => { lock (calls) calls.Add("backup"); return "備援"; }, "Backup"),
        ]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.Equal(["primary", "primary"], calls);
        Assert.False(provider.LastUsage!.FallbackUsed);
    }

    [Fact]
    public async Task APrimaryThatFailsTwice_HandsTheWholeGroupToTheBackup()
    {
        var provider = new ResilientProvider([Engine.Failing(), Engine.Answering(text => "B:" + text, "Backup")]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("a", "b"), "EN", "ZH-HANT", "");

        Assert.Equal(["B:a", "B:b"], blocks.Select(block => block.TranslatedText));
        Assert.True(provider.LastUsage!.FallbackUsed);
        Assert.Equal("Backup", provider.LastUsage.BackupEngine);
    }

    // Two engines behind one option — Web and RPC behind 「Google 翻譯 (標準)」 — are one voice to the
    // user, and the badge must not call one of them a backup.
    [Fact]
    public async Task AnEngineOfTheSameOption_IsNotCountedAsABackup()
    {
        var provider = new ResilientProvider(
            [Engine.Failing(), Engine.Answering(text => "R:" + text, "Rpc"), Engine.Answering(text => "M:" + text, "Microsoft")],
            optionName: name => name is "Failing" or "Rpc" ? "標準" : name);

        var (blocks, _) = await provider.TranslateAsync(Blocks("a"), "EN", "ZH-HANT", "");

        Assert.Equal("R:a", blocks[0].TranslatedText);
        Assert.False(provider.LastUsage!.FallbackUsed);
        Assert.Equal("標準", provider.LastUsage.Primary);
    }

    // Slow is not failing: a stalled primary gets a second request of its own at the hedge delay,
    // and a backup only a hedge delay after that.
    [Fact]
    public async Task ASlowPrimary_IsHedgedWithItselfFirst()
    {
        var calls = new List<string>();
        var primary = new Engine("Primary", (texts, call) =>
        {
            lock (calls) calls.Add("primary");
            return call == 1 ? Never() : Answer(texts, "P2:");
        });
        var backup = new Engine("Backup", (texts, _) => { lock (calls) calls.Add("backup"); return Answer(texts, "B:"); });
        var provider = new ResilientProvider([primary, backup],
            hedgeDelay: TimeSpan.FromMilliseconds(100), timeout: TimeSpan.FromSeconds(5));

        var (blocks, _) = await provider.TranslateAsync(Blocks("a"), "EN", "ZH-HANT", "");

        Assert.Equal("P2:a", blocks[0].TranslatedText);
        Assert.Equal(["primary", "primary"], calls);
    }

    // One figure and two that follow from it: the first backup goes up at twice the hedge, which is
    // also when a request stuck since the start gives up, and still has a whole hedge before the
    // deadline.
    [Fact]
    public void Timings_StayInProportion()
    {
        Assert.Equal(TranslationTiming.Hedge * 2, TranslationTiming.Request);
        Assert.Equal(TranslationTiming.Hedge * 3, TranslationTiming.Deadline);
    }

    // The slowest ordinary answer measured was 3.5 s (Google RPC). One that slow is waited for, not
    // asked for twice.
    [Fact]
    public async Task AnAnswerAtTheSlowEndOfNormal_IsNotSentTwice()
    {
        var primary = new Engine("Primary", async (texts, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3.6));
            return await Answer(texts, "P:");
        });
        var provider = new ResilientProvider([primary, Engine.Answering(text => "B:" + text, "Backup")]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("a"), "EN", "ZH-HANT", "");

        Assert.Equal("P:a", blocks[0].TranslatedText);
        Assert.Equal(1, primary.Calls);
    }

    // Errors from the free endpoints are mostly passing ones, so engines that all fail quickly get
    // one more round — starting from the user's own engine, not from whichever backup failed last.
    [Fact]
    public async Task EnginesThatAllFailOnce_AreRetriedFromThePrimary()
    {
        var calls = new List<string>();
        var provider = new ResilientProvider(
        [
            Engine.Answering(_ => { lock (calls) calls.Add("primary"); return calls.Count > 3 ? "你好" : null; }),
            Engine.Answering(_ => { lock (calls) calls.Add("backup"); return null; }, "Backup"),
        ]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.Equal(["primary", "primary", "backup", "primary"], calls);
    }

    [Fact]
    public async Task EnginesThatKeepFailing_AreRetriedOnlyOnce()
    {
        var engine = Engine.Failing();
        var provider = new ResilientProvider([engine]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);

        // Two steps (the primary, then the primary again), climbed twice.
        Assert.Equal(4, engine.Calls);
    }

    // A hang is not retried: the round that follows would only wait again, past the deadline.
    [Fact]
    public async Task AnEngineThatHangsPastTheDeadline_IsNotRetried()
    {
        var engine = new Engine("Primary", (_, _) => Never());
        var provider = new ResilientProvider([engine],
            hedgeDelay: TimeSpan.FromMilliseconds(50), timeout: TimeSpan.FromMilliseconds(300));

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);

        // The first request and its hedge; nothing after the deadline.
        Assert.Equal(2, engine.Calls);
    }

    // The requests that lost are aborted, not left to finish for nobody.
    [Fact]
    public async Task RequestsStillRunningWhenAGroupIsDecided_AreCancelled()
    {
        var stalled = new TaskCompletionSource<bool>();
        var primary = new Engine("Primary", (texts, call) => call == 1 ? Never() : Answer(texts, "P:"));
        primary.OnCancelled = () => stalled.TrySetResult(true);
        var provider = new ResilientProvider([primary],
            hedgeDelay: TimeSpan.FromMilliseconds(50), timeout: TimeSpan.FromSeconds(5));

        await provider.TranslateAsync(Blocks("a"), "EN", "ZH-HANT", "");

        Assert.True(await Task.WhenAny(stalled.Task, Task.Delay(2000)) == stalled.Task);
    }

    [Fact]
    public async Task EachGroupOfThePrimarysPlan_IsSentSeparately()
    {
        var seen = new List<string>();
        var primary = new Engine("Primary", (texts, _) =>
        {
            lock (seen) seen.Add(string.Join("+", texts));
            return Answer(texts, "");
        }, plan: texts => [[0, 1], [2]]);
        var provider = new ResilientProvider([primary]);

        await provider.TranslateAsync(Blocks("a", "b", "c"), "EN", "ZH-HANT", "");

        Assert.Equal(["a+b", "c"], seen.Order());
    }

    // Detected languages come back in the application's own codes, by majority.
    [Fact]
    public async Task DetectedLanguage_IsVotedAndMappedToTheApplicationsCodes()
    {
        var provider = new ResilientProvider([new Engine("Primary", (texts, _) =>
            Task.FromResult<IReadOnlyList<TextTranslation>>(
                [new("x", "ja"), new("y", "ja"), new("z", "zh-TW")]))]);

        var (_, detected) = await provider.TranslateAsync(Blocks("a", "b", "c"), "AUTO", "ZH-HANT", "");

        Assert.Equal("JA", detected);
    }

    private static List<OcrTextBlock> Blocks(params string[] texts) =>
        [.. texts.Select((text, i) => new OcrTextBlock(text, new Rect(0, i * 30, 100, 20)))];

    private static Task<IReadOnlyList<TextTranslation>> Answer(IReadOnlyList<string> texts, string prefix) =>
        Task.FromResult<IReadOnlyList<TextTranslation>>([.. texts.Select(text => new TextTranslation(prefix + text, "en"))]);

    private static Task<IReadOnlyList<TextTranslation>> Never() =>
        new TaskCompletionSource<IReadOnlyList<TextTranslation>>().Task;

    /// <summary>An engine whose every answer is decided by the test.</summary>
    private sealed class Engine(
        string name,
        Func<IReadOnlyList<string>, int, Task<IReadOnlyList<TextTranslation>>> answer,
        Func<IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<int>>>? plan = null) : ITextTranslator
    {
        private int _calls;

        public string Name => name;

        public int Calls => _calls;

        public Action? OnCancelled { get; set; }

        /// <summary>The whole list in one request, like every batch engine with a small screen.</summary>
        public static Engine Answering(Func<string, string?> answer, string name = "Primary") =>
            new(name, (texts, _) => AnswerAll(texts, answer));

        public static Engine Failing() => Answering(_ => null, "Failing");

        /// <summary>One text per request, like Bing.</summary>
        public static Engine OnePerRequest(Func<string, string?> answer) =>
            new("Primary", (texts, _) => AnswerAll(texts, answer),
                texts => [.. Enumerable.Range(0, texts.Count).Select(i => (IReadOnlyList<int>)[i])]);

        public IReadOnlyList<IReadOnlyList<int>> Plan(IReadOnlyList<string> texts) =>
            plan?.Invoke(texts) ?? [Enumerable.Range(0, texts.Count).ToList()];

        public async Task<IReadOnlyList<TextTranslation>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLanguage, string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            var pending = answer(texts, call);
            var cancelled = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() =>
            {
                OnCancelled?.Invoke();
                cancelled.TrySetResult();
            });

            if (await Task.WhenAny(pending, cancelled.Task) != pending)
                throw new OperationCanceledException(cancellationToken);

            return await pending;
        }

        private static Task<IReadOnlyList<TextTranslation>> AnswerAll(IReadOnlyList<string> texts, Func<string, string?> answer)
        {
            var answers = texts.Select(answer).ToList();
            return answers.Any(a => a is null)
                ? Task.FromException<IReadOnlyList<TextTranslation>>(new TranslationEngineException("Fake", "engine down"))
                : Task.FromResult<IReadOnlyList<TextTranslation>>([.. answers.Select(a => new TextTranslation(a!, "en"))]);
        }
    }
}
