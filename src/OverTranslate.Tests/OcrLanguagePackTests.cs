using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows.Controls;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using Xunit;
using ComboBox = System.Windows.Controls.ComboBox;

namespace OverTranslate.Tests;

/// <summary>
/// A source language that has to be downloaded first (#380): what the pack reports, and the rule that
/// a picker never holds a language that cannot be read yet.
/// </summary>
public sealed class OcrLanguagePackTests : IDisposable
{
    private static readonly byte[] Model = Enumerable.Range(0, 30_000).Select(i => (byte)(i * 11)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-lang-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Server(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(status == HttpStatusCode.OK
                ? new HttpResponseMessage(status) { Content = new ByteArrayContent(Model) }
                : new HttpResponseMessage(status));
    }

    private OcrLanguagePack Pack(HttpStatusCode status = HttpStatusCode.OK)
    {
        var manifest = new MangaModelManifest(
            "1",
            [new MangaModelSource("only", "https://example.test/{name}", null)],
            [new MangaModelFile("recognizer", "rec.onnx", Model.Length, Convert.ToHexString(SHA256.HashData(Model)).ToLowerInvariant())]);
        return new OcrLanguagePack("test", "S.OcrPack.CyrillicLanguages", ["RU"],
            new MangaModelStore(manifest, _root, new Server(status)) { RetryDelay = TimeSpan.Zero });
    }

    [Fact]
    public void NotDownloaded_OffersItsSize()
    {
        var pack = Pack();

        Assert.False(pack.IsReady);
        Assert.True(pack.ShowsRowHint);
        Assert.True(pack.ShowsDownloadMark);
        // 30 KB rounds up to the smallest size worth saying rather than to "0 MB".
        Assert.Equal(1, pack.Megabytes);
    }

    [Fact]
    public async Task Downloaded_IsReadyAndHasNothingToSayOnItsRow()
    {
        var pack = Pack();
        var heard = 0;
        pack.Changed += (_, _) => heard++;

        await pack.DownloadAsync();

        Assert.True(pack.IsReady);
        Assert.False(pack.ShowsRowHint);
        Assert.False(pack.ShowsDownloadMark);
        Assert.True(heard > 0);
        Assert.True(File.Exists(pack.PathOf("recognizer")));
    }

    [Fact]
    public async Task FailedDownload_SaysWhyUntilDismissed()
    {
        var pack = Pack(HttpStatusCode.NotFound);
        var heard = 0;
        pack.Changed += (_, _) => heard++;

        await pack.DownloadAsync();

        Assert.False(pack.IsReady);
        Assert.Equal(MangaDownloadFailure.Server, pack.Failure?.Kind);
        Assert.Equal(404, pack.Failure?.Status);
        Assert.NotNull(pack.FailureText);
        // A failure ends in the state it started from, so it has to be told apart by itself or a
        // card would go on asking as if nothing had happened.
        Assert.True(heard > 0);

        pack.ClearFailure();

        Assert.Null(pack.Failure);
        Assert.True(pack.ShowsDownloadMark);
    }

    [Fact]
    public async Task Deleted_GoesBackToNotDownloaded()
    {
        var pack = Pack();
        await pack.DownloadAsync();

        pack.Delete();

        Assert.False(pack.IsReady);
        Assert.True(pack.ShowsDownloadMark);
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("UK")]
    [InlineData("ru")]
    public void CyrillicLanguages_ShareOnePack(string code)
    {
        Assert.Equal("cyrillic", OnnxOcrEngine.GetModelKeyForLanguage(code));
        Assert.Same(OcrLanguagePacks.ForModelKey("cyrillic"), OcrLanguagePacks.ForCode(code));
        Assert.True(OcrLanguageRouter.IsSupported(code));
    }

    [Theory]
    [InlineData("KO")]
    [InlineData("ko")]
    public void Korean_IsAPackOfItsOwn(string code)
    {
        Assert.Equal("korean", OnnxOcrEngine.GetModelKeyForLanguage(code));
        Assert.Same(OcrLanguagePacks.ForModelKey("korean"), OcrLanguagePacks.ForCode(code));
        Assert.Equal(["KO"], OcrLanguagePacks.ForCode(code)!.Codes);
        Assert.True(OcrLanguageRouter.IsSupported(code));
    }

    // Someone updating from a version that shipped Korean, with it still chosen and nothing downloaded:
    // the screenshot falls back to 自動 and realtime, which has no 自動, to English.
    [Fact]
    public void RememberedKorean_WithoutThePack_FallsBack()
    {
        if (OcrLanguagePacks.ForCode("KO") is { IsReady: true }) return; // downloaded on this machine

        Assert.Equal(LanguageData.DefaultOcrSourceLanguage, LanguageData.GetValidOcrSourceCode("KO"));
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, LanguageData.GetValidRealtimeSourceCode("KO"));
    }

    // Only what the automatic model reads is in the installer.
    [Theory]
    [InlineData("EN")]
    [InlineData("JA")]
    [InlineData("AUTO")]
    [InlineData("ZH-HANT")]
    [InlineData("ZH")]
    public void InstalledLanguages_NeedNoPack(string code)
    {
        Assert.Null(OcrLanguagePacks.ForCode(code));
        Assert.True(OcrLanguagePacks.IsUsable(code));
    }

    [Theory]
    [InlineData("cyrillic")]
    [InlineData("korean")]
    public void ShippedManifest_NamesTheFilesTheEngineAsksFor(string key)
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ocrmodels", $"lang-{key}.json")));

        Assert.NotNull(manifest.File("recognizer"));
        Assert.NotNull(manifest.File("dictionary"));
        Assert.Null(manifest.DirectMl);
        // A primary and a fallback, for every file.
        Assert.All(manifest.Files, file => Assert.Equal(2, manifest.SourcesOf(file).Count));
    }

    /// <summary>
    /// The Korean the tests read with is the one the pack downloads: the same bytes the manifest asks for,
    /// so a test passing on the copy says the same of the download.
    /// </summary>
    [Theory]
    [InlineData("recognizer", "rec.onnx")]
    [InlineData("dictionary", "dict.txt")]
    public void KoreanManifest_MatchesTheCopyTheTestsReadWith(string role, string copy)
    {
        var manifest = MangaModelManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ocrmodels", "lang-korean.json")));
        var file = manifest.File(role)!;
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ocrmodels", "onnx", "korean", copy));

        Assert.Equal(file.Size, bytes.Length);
        Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    // Korean keeps its place among the languages people already knew; the newer packs come last.
    [Fact]
    public void OcrSources_KeepKoreanWhereItWas()
    {
        var codes = LanguageData.OcrSourceLanguages.Select(language => language.Code).ToList();

        Assert.Equal(["AUTO", "EN", "JA", "KO", "ZH-HANT", "ZH", "RU", "UK"], codes);
    }

    [Theory]
    [InlineData("Сохранить", "изменения", "Сохранить изменения")]
    [InlineData("версия", "2", "версия 2")]
    [InlineData("настроек,", "вручную", "настроек, вручную")]
    [InlineData("Steam", "Настройки", "Steam Настройки")]
    public void JoinInlineText_SpacesCyrillicWords(string left, string right, string expected) =>
        Assert.Equal(expected, OcrTextBlockGrouper.JoinInlineText(left, right));

    [Theory]
    [InlineData("hello", "world", "hello world")]
    [InlineData("foo,", "bar", "foo,bar")]
    [InlineData("안녕", "하세요", "안녕하세요")]
    [InlineData("こんにちは", "世界", "こんにちは世界")]
    public void JoinInlineText_LeavesEverythingElseAsItWas(string left, string right, string expected) =>
        Assert.Equal(expected, OcrTextBlockGrouper.JoinInlineText(left, right));

    // The real pack, which is not downloaded on a machine running the tests with no assets.
    private static readonly LangItem Russian = LanguageData.OcrSourceLanguages.Single(language => language.Code == "RU");
    private static readonly LangItem Japanese = LanguageData.OcrSourceLanguages.Single(language => language.Code == "JA");

    private static (ComboBox Box, OcrSourcePicker Picker, List<string?> Acted) Picker()
    {
        var box = new ComboBox { ItemsSource = LanguageData.OcrSourceLanguages, SelectedValuePath = "Code" };
        var picker = new OcrSourcePicker(box, () => "AUTO");
        var acted = new List<string?>();
        box.SelectionChanged += (_, e) =>
        {
            if (!picker.Intercept(e)) acted.Add(box.SelectedValue as string);
        };
        return (box, picker, acted);
    }

    [Fact]
    public void Picker_NeverHoldsALanguageStillToBeDownloaded() => OnSta(() =>
    {
        if (Russian.Pack is { IsReady: true }) return; // downloaded on this machine
        var (box, picker, acted) = Picker();
        box.SelectedItem = Japanese;
        acted.Clear();

        box.SelectedItem = Russian;

        Assert.Equal("JA", box.SelectedValue);
        Assert.DoesNotContain("RU", acted);
        picker.Dispose();
    });

    // What a page does on the way back from the settings: rebinds its items, which empties the box, and
    // puts back the language it held. Asking about a download here left an empty box and a stray card.
    [Fact]
    public void Picker_GivenAGoneLanguageByCode_FallsBackAndTheOwnerHears() => OnSta(() =>
    {
        if (Russian.Pack is { IsReady: true }) return; // downloaded on this machine
        var (box, picker, acted) = Picker();

        box.SelectedItem = Russian;

        Assert.Equal("AUTO", box.SelectedValue);
        Assert.Equal(["AUTO"], acted);
        picker.Dispose();
    });

    // The realtime page is unloaded while the settings page deletes a pack, so it hears nothing.
    [Fact]
    public void Picker_HoldingALanguageWhosePackIsGone_FallsBackWhenItLooksAgain() => OnSta(() =>
    {
        if (Russian.Pack is { IsReady: true }) return; // downloaded on this machine
        var box = new ComboBox { ItemsSource = LanguageData.OcrSourceLanguages, SelectedValuePath = "Code", SelectedItem = Russian };
        var picker = new OcrSourcePicker(box, () => "EN");
        Assert.Equal("RU", box.SelectedValue);

        picker.Reconcile();

        Assert.Equal("EN", box.SelectedValue);
        picker.Dispose();
    });

    [Fact]
    public void Picker_PassesOrdinaryLanguagesThrough() => OnSta(() =>
    {
        var (box, picker, acted) = Picker();

        box.SelectedItem = Japanese;

        Assert.Equal("JA", box.SelectedValue);
        Assert.Equal(["JA"], acted);
        picker.Dispose();
    });

    private static void OnSta(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
