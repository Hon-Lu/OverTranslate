using System.Reflection;
using System.Windows;
using GTranslate.Results;
using GTranslate.Translators;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

// When every engine fails, ResilientProvider hands back the original text so the rest of the batch
// still renders. The realtime session must not mistake that stand-in for a translation — it would
// cache it and draw the original text for that line until the session ended — so the block says so.
public class ResilientProviderUntranslatedTests
{
    [Fact]
    public async Task EveryEngineFailing_MarksTheBlockUntranslated()
    {
        var provider = new ResilientProvider([Engine(_ => null), Engine(_ => null)]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);
        Assert.Equal("Hello", blocks[0].TranslatedText);
    }

    [Fact]
    public async Task OnlyTheBlockNoEngineAnswered_IsMarkedUntranslated()
    {
        var provider = new ResilientProvider([Engine(text => text == "Hello" ? "你好" : null)]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello", "Broken"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.True(blocks[1].Untranslated);
    }

    // A text that translates to itself is still a translation, and must not be retried forever.
    [Fact]
    public async Task ATranslationIdenticalToTheSource_IsNotUntranslated()
    {
        var provider = new ResilientProvider([Engine(text => text)]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("OK"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
    }

    // Errors from the free endpoints are mostly passing ones, so engines that all fail quickly get
    // one more round — starting from the user's own engine, not from whichever backup failed last.
    [Fact]
    public async Task EnginesThatAllFailOnce_AreRetriedFromThePrimary()
    {
        var calls = new List<string>();
        var provider = new ResilientProvider(
        [
            Engine(_ => { calls.Add("primary"); return calls.Count > 2 ? "你好" : null; }),
            Engine(_ => { calls.Add("backup"); return null; }),
        ]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.False(blocks[0].Untranslated);
        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.Equal(["primary", "backup", "primary"], calls);
    }

    [Fact]
    public async Task EnginesThatKeepFailing_AreRetriedOnlyOnce()
    {
        var calls = 0;
        var provider = new ResilientProvider([Engine(_ => { Interlocked.Increment(ref calls); return null; })]);

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);
        Assert.Equal(2, calls);
    }

    // A hang is not retried: the round that follows would only wait again, past the deadline.
    [Fact]
    public async Task AnEngineThatHangsPastTheDeadline_IsNotRetried()
    {
        var calls = 0;
        var translator = DispatchProxy.Create<ITranslator, FakeTranslator>();
        ((FakeTranslator)(object)translator).Hang = () => Interlocked.Increment(ref calls);
        var provider = new ResilientProvider(
            [new GTranslateProvider(translator)],
            hedgeDelay: TimeSpan.FromMilliseconds(50),
            timeout: TimeSpan.FromMilliseconds(200));

        var (blocks, _) = await provider.TranslateAsync(Blocks("Hello"), "EN", "ZH-HANT", "");

        Assert.True(blocks[0].Untranslated);
        Assert.Equal(1, calls);
    }

    private static List<OcrTextBlock> Blocks(params string[] texts) =>
        [.. texts.Select((text, i) => new OcrTextBlock(text, new Rect(0, i * 30, 100, 20)))];

    // null from the answer means the engine fails for that text.
    private static GTranslateProvider Engine(Func<string, string?> answer)
    {
        var translator = DispatchProxy.Create<ITranslator, FakeTranslator>();
        ((FakeTranslator)(object)translator).Answer = answer;
        return new GTranslateProvider(translator);
    }

    public class FakeTranslator : DispatchProxy
    {
        public Func<string, string?> Answer { get; set; } = _ => null;

        // Set to make every call count itself and then never answer.
        public Action? Hang { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Name" => "Fake",
            "TranslateAsync" => Translate((string)args![0]!),
            _ => throw new NotSupportedException(method?.Name),
        };

        private Task<ITranslationResult> Translate(string text)
        {
            if (Hang is not null)
            {
                Hang();
                return new TaskCompletionSource<ITranslationResult>().Task;
            }

            return Answer(text) is { } translation
                ? Task.FromResult(FakeResult.Of(translation))
                : Task.FromException<ITranslationResult>(new HttpRequestException("engine down"));
        }
    }

    public class FakeResult : DispatchProxy
    {
        private string _translation = "";

        public static ITranslationResult Of(string translation)
        {
            var result = DispatchProxy.Create<ITranslationResult, FakeResult>();
            ((FakeResult)(object)result)._translation = translation;
            return result;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Translation" => _translation,
            _ => null,
        };
    }
}
