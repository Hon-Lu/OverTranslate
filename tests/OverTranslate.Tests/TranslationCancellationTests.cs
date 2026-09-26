using System.Net.Http;
using System.Windows;
using OverTranslate.Engines;
using OverTranslate.Engines.Bing;
using OverTranslate.Engines.Google;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

// A cancelled batch must fail loudly. ResilientProvider's normal "every engine failed" path returns
// the ORIGINAL text as the translation so the rest of a batch still renders — if cancellation fell
// into that path, an abandoned capture would look like a successful translation of untranslated
// text. These tests pin the distinction. They never reach the network: cancellation is observed
// before any request is issued (and, unlike under GTranslate, would also abort one in flight).
public class TranslationCancellationTests
{
    private static readonly HttpClient Http = EngineHttp.CreateClient(TimeSpan.FromSeconds(5));

    private static List<OcrTextBlock> SampleBlocks() =>
    [
        new("Hello world", new Rect(0, 0, 100, 20)),
        new("Good morning", new Rect(0, 30, 100, 20)),
    ];

    [Fact]
    public async Task ResilientProvider_WhenCancelled_ThrowsInsteadOfFallingBackToOriginalText()
    {
        var provider = new ResilientProvider(
        [
            new BingTranslator(Http),
            new GoogleRpcTranslator(Http),
        ]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateAsync(SampleBlocks(), "EN", "ZH-HANT", "", cts.Token));
    }

    [Fact]
    public async Task EngineProvider_WhenCancelled_Throws()
    {
        var provider = new EngineProvider(new BingTranslator(Http));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateAsync(SampleBlocks(), "EN", "ZH-HANT", "", cts.Token));
    }

    [Fact]
    public async Task TranslationService_WhenCancelled_Throws()
    {
        var service = new TranslationService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TranslateAsync(SampleBlocks(), "EN", "ZH-HANT", "", cancellationToken: cts.Token));
    }

    // An empty batch short-circuits before the cancellation check; it must stay cheap and silent
    // rather than throwing, since nothing was ever going to be sent.
    [Fact]
    public async Task EmptyBatch_WithCancelledToken_ReturnsEmptyWithoutThrowing()
    {
        var provider = new EngineProvider(new BingTranslator(Http));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (blocks, detected) = await provider.TranslateAsync([], "EN", "ZH-HANT", "", cts.Token);

        Assert.Empty(blocks);
        Assert.Equal("", detected);
    }
}
