namespace OverTranslate.Layout;

/// <summary>
/// Which target languages a vertical source is translated back into vertical writing for.
/// </summary>
/// <remarks>
/// <para>The 直排 switch says how the SOURCE runs. It used to say how the translation runs as well,
/// and for a target with no vertical tradition that meant one letter per cell down the balloon —
/// "Hello" as five stacked squares, which nobody can read (#315). Whether the translation goes
/// down or across is now the target's to decide, and the reading side — how the page is cut into
/// columns, where the colour is sampled, what is erased — stays the source's.</para>
///
/// <para>The rule is that a target which can be read downwards keeps the source's direction:
/// Japanese, both Chinese scripts and Korean. Everything else is set across.</para>
///
/// <para>An allow-list, not a deny-list, so that whatever a later release adds is set across
/// until someone decides otherwise: a new language cannot bring this bug back by being forgotten
/// here.</para>
/// </remarks>
internal static class VerticalTargetLanguages
{
    /// <summary>Whether a translation into <paramref name="targetLanguage"/> is set downwards.</summary>
    /// <param name="targetLanguage">A target code as the settings store it (JA, ZH-HANT…), in any case.</param>
    public static bool SetsDown(string? targetLanguage) =>
        targetLanguage?.Trim().ToUpperInvariant() switch
        {
            "JA" => true,
            "ZH-HANT" => true,
            // Both codes mean Simplified: LanguageData lists it as ZH for some engines and ZH-HANS for others.
            "ZH" => true,
            "ZH-HANS" => true,
            "KO" => true,
            _ => false,
        };
}
