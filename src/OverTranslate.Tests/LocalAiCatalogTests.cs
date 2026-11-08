using System.IO;
using OverTranslate.Services.LocalAi;
using Xunit;

namespace OverTranslate.Tests;

public class LocalAiCatalogTests
{
    private static string ShippedPath()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "src", "OverTranslate", "localai", "models.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("models.json");
    }

    private static LocalAiCatalog Shipped() => LocalAiCatalog.Load(ShippedPath());

    [Fact]
    public void TheShippedListLoadsAndHasTheAgreedModels()
    {
        var catalog = Shipped();

        Assert.Equal(["hy-mt2-7b-ud-iq3-xxs", "gemma-4-e4b-qat-q4-0"], catalog.Recommended.Select(m => m.Id));
        Assert.Equal(7, catalog.Models.Count);
        Assert.DoesNotContain(catalog.Models, m => m.Name.Contains("TranslateGemma", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheOthersAreOrderedByVram()
    {
        var vram = Shipped().Others.Select(m => m.VramMiB).ToList();
        Assert.Equal(vram.OrderBy(v => v), vram);
    }

    [Fact]
    public void EveryModelUrlIsPinnedToACommit()
    {
        foreach (var model in Shipped().Models)
            Assert.Matches("/resolve/[0-9a-f]{40}/", model.Url);
    }

    [Fact]
    public void TheRuntimeHasTheOfficialReleaseFirstAndABackup()
    {
        var urls = Shipped().Runtime.Urls;
        Assert.StartsWith("https://github.com/ggml-org/llama.cpp/", urls[0]);
        Assert.Contains(urls, url => url.Contains("OverTranslate-assets"));
    }

    private const string Hash = "0146743e816210d5a9f12fa9f219bc3926134253f4c5205a6403c18b128feaae";

    private static string Json(string file = "a.gguf", string format = "gemma-4", string url = "https://x.test/a.gguf", string sha = Hash) => $$"""
        {"runtime":{"version":"b1","size":1,"sha256":"{{Hash}}","urls":["https://x.test/z.zip"]},
         "models":[{"id":"a","name":"A","shortName":"A","recommended":true,"repo":"r","file":"{{file}}","url":"{{url}}",
                    "size":1,"sha256":"{{sha}}","vramMiB":1,"format":"{{format}}","descriptionKey":"S.LocalAi.ModelDescHy7B"}]}
        """;

    [Fact]
    public void AWellFormedListParses() => Assert.Single(LocalAiCatalog.Parse(Json()).Models);

    [Theory]
    [InlineData("../evil.gguf", "gemma-4", "https://x.test/a.gguf", Hash)]
    [InlineData("a.gguf", "unknown-format", "https://x.test/a.gguf", Hash)]
    [InlineData("a.gguf", "gemma-4", "http://x.test/a.gguf", Hash)]
    [InlineData("a.gguf", "gemma-4", "https://x.test/a.gguf", "abc")]
    public void AMalformedEntryIsRefused(string file, string format, string url, string sha) =>
        Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Parse(Json(file, format, url, sha)));

    [Theory]
    [InlineData("gemma-4", "<|turn>user\nHI<turn|>\n<|turn>model\n")]
    [InlineData("hy-mt2-7b", "<|startoftext|>HI<|extra_0|>")]
    [InlineData("hy-mt2-1.8b", "<｜hy_begin▁of▁sentence｜><｜hy_User｜>HI<｜hy_Assistant｜>")]
    public void AFormatWrapsTheMessage(string format, string expected) =>
        Assert.Equal(expected, LocalAiPromptFormat.Wrap(format, "HI"));

    /// <summary>Braces in the text are the text's: only the template's own marker is filled in.</summary>
    [Fact]
    public void AMessageThatHoldsTheMarkerIsLeftAlone() =>
        Assert.Equal("<|startoftext|>{msg}<|extra_0|>", LocalAiPromptFormat.Wrap("hy-mt2-7b", "{msg}"));
}
