// UseWindowsForms puts System.Drawing in the implicit usings, so this name collides
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Services;

/// <summary>
/// The font the screenshot and realtime overlays set a translation in, chosen by the language the
/// translation is in.
/// </summary>
/// <remarks>
/// The interface font follows the interface language (see LocalizationService's Fonts table), but
/// what the overlays draw is not interface text: it is the translation, and its language is the
/// target the user picked, which can be anything regardless of the interface. Both overlays used to
/// hard-code Microsoft JhengHei, so Japanese and Simplified Chinese translations came out with
/// Taiwanese punctuation — 。、， centred in the cell rather than sitting at the bottom left — and
/// with Taiwanese shapes for the Han characters the three languages share. A native reader notices
/// that at once, the same way they would in the interface.
///
/// Japanese is Yu Gothic UI rather than Yu Gothic. Measured with FormattedText at 20px SemiBold,
/// Yu Gothic's line height is 32.0 against JhengHei's 26.6, and a multi-line bubble sized for one
/// overflows in the other; the UI cut matches JhengHei's line height and runs about 11% narrower.
/// Meiryo is an optional Windows feature and is not on every machine, so it is not listed.
///
/// A target outside the three CJK scripts leads with Segoe UI and keeps a CJK family behind it for
/// what the translation leaves untranslated — a character name, a place — and that family follows
/// the interface, since a reader who chose a Japanese interface would rather see a stray kanji in
/// Japanese shapes. English keeps JhengHei there, as its interface font does.
///
/// Measuring and drawing have to ask for the same answer. Both overlays size a bubble from
/// FormattedText before they place the TextBlock in it, and a bubble measured in one family and
/// drawn in another wraps differently from what was measured.
/// </remarks>
public static class TranslatedTextFont
{
    private const string Japanese           = "Yu Gothic UI, Segoe UI, Sans-Serif";
    private const string Korean             = "Malgun Gothic, Segoe UI, Sans-Serif";
    private const string SimplifiedChinese  = "Microsoft YaHei, Segoe UI, Sans-Serif";
    private const string TraditionalChinese = "Microsoft JhengHei, Segoe UI, Sans-Serif";

    /// <summary>The comma-separated family list for a translation into <paramref name="targetLanguage"/>.</summary>
    /// <param name="targetLanguage">A target language code as the translation settings store it (JA, ZH-HANT, EN-US…), in any case.</param>
    /// <param name="uiLanguage">
    /// The interface language, which only matters for a non-CJK target. Defaults to
    /// <see cref="LocalizationService.Current"/>; a parameter so tests need not go through settings.
    /// </param>
    public static string FamilyList(string? targetLanguage, string? uiLanguage = null)
    {
        switch (targetLanguage?.Trim().ToUpperInvariant())
        {
            case "JA":
                return Japanese;
            case "KO":
                return Korean;
            // Both codes mean Simplified: LanguageData lists it as ZH for some engines and ZH-HANS for others.
            case "ZH":
            case "ZH-HANS":
                return SimplifiedChinese;
            case "ZH-HANT":
                return TraditionalChinese;
        }

        return $"Segoe UI, {FallbackCjkFamily(uiLanguage ?? LocalizationService.Current)}, Sans-Serif";
    }

    /// <summary>The <see cref="FamilyList"/> answer as a family WPF can draw with.</summary>
    public static FontFamily For(string? targetLanguage, string? uiLanguage = null) =>
        new(FamilyList(targetLanguage, uiLanguage));

    private static string FallbackCjkFamily(string uiLanguage)
    {
        if (uiLanguage.Equals(LocalizationService.SimplifiedChinese, StringComparison.OrdinalIgnoreCase))
            return "Microsoft YaHei";
        if (uiLanguage.Equals(LocalizationService.Japanese, StringComparison.OrdinalIgnoreCase))
            return "Yu Gothic UI";
        if (uiLanguage.Equals(LocalizationService.Korean, StringComparison.OrdinalIgnoreCase))
            return "Malgun Gothic";
        return "Microsoft JhengHei";
    }
}
