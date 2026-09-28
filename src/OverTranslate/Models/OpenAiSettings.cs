namespace OverTranslate.Models;

/// <summary>
/// The two messages one case sends: the system message and the user one.
/// </summary>
/// <remarks>
/// Both, rather than the single system prompt that shipped before, because the models this provider
/// is pointed at do not agree on where an instruction goes. A translation model is trained to be
/// told what to do in the user message with the text right under it, and sending that model a system
/// message it never saw in training changes what comes back; a general chat model wants the opposite.
/// Which half is used is the model's business, so the editor asks for one of the two rather than for
/// both — see <see cref="IsEmpty"/>, the one shape that is not a setting.
///
/// Kept as a pair rather than two properties on the profile so the two cases stay symmetrical: 自動
/// and 指定語言 each own one of these, and nothing has to remember which of four flat names goes
/// with which case.
/// </remarks>
public class OpenAiPromptPair
{
    /// <summary>
    /// The system message, or empty to send none at all.
    /// </summary>
    /// <remarks>
    /// Empty means the request carries no system message, not that it carries an empty one: an empty
    /// system message is still a turn the model has to account for, and the built-in setting is
    /// written for a model whose documented format has no system turn in it.
    /// </remarks>
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// What goes in front of the text to translate, in the user message, or empty to send the text
    /// on its own.
    /// </summary>
    /// <remarks>
    /// In front of rather than instead of: the text being translated is the rest of that same
    /// message — see <c>OpenAiChatTranslator.BuildMessages</c>. This is
    /// why the built-in wording ends in a colon.
    /// </remarks>
    public string UserPrompt { get; set; } = "";

    /// <summary>Whether this case would send no instruction at all.</summary>
    public bool IsEmpty => SystemPrompt.Trim().Length == 0 && UserPrompt.Trim().Length == 0;
}

/// <summary>
/// One named setting for an OpenAI-compatible model: which model, at what temperature, told what.
/// </summary>
/// <remarks>
/// The whole configuration rather than a prompt, which is the lesson of pointing this provider at
/// more than one local model. A prompt is written for a model — the format the model's own
/// documentation asks for — and the temperature that model wants goes with it. Keeping the three
/// apart meant that changing model was three edits in three places, and getting two of the three
/// right left a setup that quietly translated worse than it should.
///
/// So a profile is switched as a unit, which is also what makes the list worth having: 「Hy-MT2」
/// beside 「Qwen 測試」 says which model is in use, and two prompts that both open "Translate the
/// following" does not.
/// </remarks>
public class OpenAiModelProfile
{
    /// <summary>
    /// Identifies this profile for <see cref="OpenAiSettings.SelectedProfileId"/>.
    /// </summary>
    /// <remarks>
    /// A stored id rather than the position in the list, because deleting the first of three would
    /// otherwise silently move the selection onto a different profile. Assigned once when the
    /// profile is created — see <see cref="OpenAiSettings.NewId"/> — and never rewritten, so the
    /// selection survives a rename.
    /// </remarks>
    public string Id { get; set; } = "";

    /// <summary>What the list calls it. The user's own words, capped at a readable length.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The model to ask for, exactly as the server names it.
    /// </summary>
    /// <remarks>
    /// Required on a profile the user wrote: empty is a configuration error rather than a fallback,
    /// and the editor will not save without it — see
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.TranslateAsync"/>.
    /// </remarks>
    public string Model { get; set; } = "";

    /// <summary>
    /// Whether the request carries a temperature at all.
    /// </summary>
    /// <remarks>
    /// Every sampling parameter here is a pair — whether to send it, and what to send. Separate,
    /// because "no temperature" is not a number: the reasoning models on the hosted APIs reject the
    /// field outright rather than clamping it, so a request to them has to leave it out, and there is
    /// no value that means "never mind". Which parameters a server accepts at all is the server's
    /// business, so all three can be switched off one at a time.
    /// </remarks>
    public bool TemperatureEnabled { get; set; } = true;

    /// <summary>
    /// How much randomness the model is asked for, when <see cref="TemperatureEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Low rather than zero. This is translation, so the same line on screen should come back the
    /// same way twice — but a small local model at exactly 0 can fall into repeating itself and
    /// never come out, and a little slack is what the recommended model's own documentation asks
    /// for. <see cref="Seed"/> is what makes the result repeatable at a non-zero temperature.
    /// </remarks>
    public double Temperature { get; set; } = DefaultTemperature;

    /// <inheritdoc cref="TemperatureEnabled"/>
    public bool TopPEnabled { get; set; } = true;

    /// <summary>
    /// How much of the probability mass the model may pick from, when <see cref="TopPEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Narrows the field before the temperature chooses from it: the lower it is, the fewer
    /// candidates survive to be chosen between at all.
    /// </remarks>
    public double TopP { get; set; } = DefaultTopP;

    /// <inheritdoc cref="TemperatureEnabled"/>
    public bool SeedEnabled { get; set; } = true;

    /// <summary>
    /// The number that makes generation repeatable, when <see cref="SeedEnabled"/>.
    /// </summary>
    /// <remarks>
    /// With the model, the prompts and the other parameters unchanged, the same seed usually gives
    /// the same output. That is what lets the temperature sit above zero without the same screen
    /// being translated differently each time it is captured.
    /// </remarks>
    public int Seed { get; set; } = DefaultSeed;

    /// <summary>The values a new setting starts on, and the ones the built-in setting sends.</summary>
    /// <remarks>
    /// Named rather than written twice: they are both the initialisers above and what
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.BuiltInProfile"/> ships, and a new
    /// setting that opened on different numbers from the one it was copied from would be a
    /// difference nobody chose.
    /// </remarks>
    public const double DefaultTemperature = 0.7;

    /// <inheritdoc cref="DefaultTemperature"/>
    public const double DefaultTopP = 0.6;

    /// <inheritdoc cref="DefaultTemperature"/>
    public const int DefaultSeed = 42;

    /// <summary>What this profile sends when the source language is 自動.</summary>
    public OpenAiPromptPair Auto { get; set; } = new();

    /// <summary>What this profile sends when a source language has been chosen.</summary>
    public OpenAiPromptPair Explicit { get; set; } = new();

    /// <summary>The pair for one case.</summary>
    public OpenAiPromptPair PromptsFor(bool automatic) => automatic ? Auto : Explicit;
}

/// <summary>
/// Everything the OpenAI-compatible provider keeps that is not one value on one line.
/// </summary>
/// <remarks>
/// A group of its own, following <see cref="RealtimeSettings"/>: the profiles are a list of objects
/// rather than a value, and a list belongs under the feature that owns it rather than flat beside
/// the endpoint. The connection — <see cref="AppSettings.OpenAiBaseUrl"/> and
/// <see cref="AppSettings.OpenAiApiKey"/> — deliberately stays where it shipped, and deliberately
/// stays out of the profiles: it is the server, and switching which model to ask that server for is
/// not switching servers.
///
/// The keys that did ship and are gone — <c>OpenAiPromptAuto</c>, <c>OpenAiPromptExplicit</c>,
/// <c>AutoPrompts</c>, <c>ExplicitPrompts</c>, <c>OpenAiModel</c> and the flat temperature pair —
/// were dropped rather than migrated, on the owner's call. A stored prompt has no name and no model
/// beside it, and carrying one into this list would mean inventing both.
/// </remarks>
public class OpenAiSettings
{
    /// <summary>
    /// How many profiles the user may keep.
    /// </summary>
    /// <remarks>
    /// Five, on top of the built-in one that is always there. A cap rather than an open list because
    /// this is a settings panel and not a library: the list is picked from in place, without a
    /// scroller of its own. Nothing breaks at six — the cap is there to keep the panel legible, so
    /// the Add button simply stops offering.
    /// </remarks>
    public const int MaxProfiles = 5;

    /// <summary>How long a profile name may be. Enough for a phrase, not for a sentence.</summary>
    public const int MaxNameLength = 40;

    /// <summary>
    /// The same text with every line break written as a bare <c>\n</c>.
    /// </summary>
    /// <remarks>
    /// A WPF TextBox inserts <c>\r\n</c> when Return is pressed, while the built-in wordings and any
    /// text loaded into the box from them use bare <c>\n</c>. A prompt someone edits therefore ends
    /// up with both, and a mixed template is not just untidy: the preview splits prose on <c>\n</c>
    /// to place its line breaks, so a <c>\r</c> left at the end of a piece renders as a second break
    /// and the paragraph gap doubles on screen. It also means two prompts that read identically are
    /// sent to the model as different strings.
    ///
    /// Normalised where the text is committed rather than where it is drawn, so the settings file
    /// holds one form. The preview normalises too, because presets saved before this did not.
    /// </remarks>
    public static string NormaliseLineBreaks(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>The user's own model settings, in the order the list shows them.</summary>
    public List<OpenAiModelProfile> Profiles { get; set; } = [];

    /// <summary>
    /// Which profile is in use, by <see cref="OpenAiModelProfile.Id"/>, or empty for the built-in
    /// one.
    /// </summary>
    /// <remarks>
    /// Empty is both the default and the fallback: an id naming a profile that has since been
    /// deleted resolves to the built-in setting rather than to nothing, so a settings file edited by
    /// hand cannot leave the provider with no model and no instruction.
    /// </remarks>
    public string SelectedProfileId { get; set; } = "";

    /// <summary>The selected profile, or null when the built-in setting is in use.</summary>
    public OpenAiModelProfile? SelectedProfile()
    {
        if (SelectedProfileId.Length == 0) return null;

        return Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
    }

    /// <summary>A fresh <see cref="OpenAiModelProfile.Id"/>, unique against everything stored.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
