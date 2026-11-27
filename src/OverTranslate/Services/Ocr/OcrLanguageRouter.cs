namespace OverTranslate.Services.Ocr;

internal static class OcrLanguageRouter
{
    public static string Normalize(string code) =>
        string.IsNullOrWhiteSpace(code) ? "EN" : code.Trim().ToUpperInvariant();

    public static bool UsesCjkOnnx(string code) => Normalize(code) is "ZH" or "ZH-HANT" or "JA" or "KO";

    public static bool UsesAutomaticLayout(string code) => Normalize(code) == "AUTO";

    public static bool IsSupported(string code) =>
        Normalize(code) is "AUTO" or "EN" or "ZH" or "ZH-HANT" or "JA" or "KO" or "RU" or "UK"
            or "FR" or "DE" or "ES" or "IT" or "PT";

    /// <summary>Whether this language writes accented Latin letters, so a reading keeps them.</summary>
    public static bool KeepsLatinDiacritics(string code) =>
        Normalize(code) is "FR" or "DE" or "ES" or "IT" or "PT";

    /// <summary>
    /// Whether this source is written in Latin letters, so a translation into Chinese, Japanese or
    /// Korean is sized up from its glyph height the way an English one is.
    /// </summary>
    public static bool IsLatinScript(string code) => Normalize(code) == "EN" || KeepsLatinDiacritics(code);

    public static string GetUnsupportedLanguageMessage(string code) =>
        LocalizationService.Format("S.Error.OcrUnsupportedLanguage", Normalize(code));
}
