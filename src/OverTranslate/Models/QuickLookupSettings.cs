namespace OverTranslate.Models;

/// <summary>
/// Everything 取詞翻譯 keeps between lookups, under one key.
/// </summary>
public class QuickLookupSettings
{
    /// <summary>
    /// Whether each successful translation replaces the clipboard contents. Off by default because
    /// copying without an explicit gesture is useful only when the reader has chosen that workflow.
    /// </summary>
    public bool AutoCopyTranslation { get; set; } = false;

    /// <summary>
    /// Whether the popup replaces its detailed result with the compact status-and-preview view.
    /// False keeps the complete translation, actions and dictionary visible on a first run.
    /// </summary>
    public bool ResultsCollapsed { get; set; } = false;

    /// <summary>
    /// Whether the popup picks the direction itself out of <see cref="BilingualFirstLanguage"/> and
    /// <see cref="BilingualSecondLanguage"/> instead of translating along the fixed pair the header
    /// pickers hold. Off by default: the fixed pair is what someone who only reads needs, and this
    /// is for the other half of the job — writing back in the other language.
    /// </summary>
    public bool BilingualEnabled { get; set; } = false;

    /// <summary>What the pair is before anyone has said otherwise.</summary>
    /// <remarks>
    /// The interface ships in Traditional Chinese and most of what its readers point this at is
    /// English, so this is the pair the feature was asked for — and both halves are one pick away
    /// from being something else.
    /// </remarks>
    public const string DefaultFirstLanguage = "ZH-HANT";

    /// <inheritdoc cref="DefaultFirstLanguage"/>
    public const string DefaultSecondLanguage = "EN-US";

    /// <summary>
    /// The two languages 雙語互譯 moves between. Target codes rather than source ones, because both
    /// of them have to be somewhere a translation can land — 自動 is not one of the two.
    ///
    /// The first is also the fallback: text in neither language is translated into it.
    /// </summary>
    public string BilingualFirstLanguage { get; set; } = DefaultFirstLanguage;

    /// <inheritdoc cref="BilingualFirstLanguage"/>
    public string BilingualSecondLanguage { get; set; } = DefaultSecondLanguage;
}
