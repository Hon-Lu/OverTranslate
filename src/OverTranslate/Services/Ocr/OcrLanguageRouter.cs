namespace OverTranslate.Services.Ocr;

internal static class OcrLanguageRouter
{
    public static string Normalize(string code) =>
        string.IsNullOrWhiteSpace(code) ? "EN" : code.Trim().ToUpperInvariant();

    public static bool UsesCjkOnnx(string code) => Normalize(code) is "ZH" or "ZH-HANT" or "JA" or "KO";

    public static bool UsesAutomaticLayout(string code) => Normalize(code) == "AUTO";

    public static bool IsSupported(string code) =>
        Normalize(code) is "AUTO" or "EN" or "ZH" or "ZH-HANT" or "JA" or "KO" or "RU" or "UK";

    public static string GetUnsupportedLanguageMessage(string code) =>
        LocalizationService.Format("S.Error.OcrUnsupportedLanguage", Normalize(code));
}
