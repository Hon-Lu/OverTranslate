namespace OverTranslate.Services;

/// <summary>
/// The two engines both translation features run on, owned for the life of the process.
/// </summary>
/// <remarks>
/// There must be exactly one of each. A second <see cref="OcrService"/> loads a second copy of the
/// ONNX runtime, and 截圖翻譯 and 即時翻譯 would then split the CPU between two engines instead of
/// queueing on one inference gate; a second <see cref="TranslationService"/> opens a second set of
/// HTTP handles for no gain.
///
/// They used to be fields on MainWindow, lent out through SharedOcrService / SharedTranslationService
/// and reached by casting <c>Application.Current.MainWindow</c>. That worked, but the dependency
/// appeared in no signature anywhere: a caller's own file gave no sign that it needed a particular
/// window to exist, and the one caller that handled the window being absent did so by quietly
/// building the second engine this type exists to prevent.
///
/// Never disposed, deliberately. They were not disposed as MainWindow fields either — the process
/// exiting is what releases them, and disposing an engine at shutdown would only race whatever pass
/// is still in flight. Making them static states that lifetime instead of leaving it implied.
/// </remarks>
internal static class AppServices
{
    /// <summary>
    /// The downloadable manga models for vertical Japanese: whether they are here, and fetching them.
    /// Declared before <see cref="Ocr"/>, which reads them.
    /// </summary>
    public static Services.Ocr.Manga.MangaModelStore MangaModels { get; } = Services.Ocr.Manga.MangaModelStore.CreateDefault();

    /// <summary>Recognition. Construction is cheap — the model itself is loaded on first use.</summary>
    public static OcrService Ocr { get; } = new(MangaModels);

    public static TranslationService Translation { get; } = new();
}
