using System.Net.Http;
using NLog;
using OverTranslate.Models;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.Providers;

/// <param name="Model">
/// The model to ask for. Required: empty is a configuration error rather than a fallback — see
/// <see cref="OpenAiCompatibleProvider.TranslateAsync"/>.
/// </param>
/// <param name="ExtraInstructions">
/// What the user adds to the built-in rules, unfilled — see <see cref="OpenAiModelProfile.ExtraInstructions"/>.
/// </param>
/// <param name="SendTemperature">
/// Whether the request carries a temperature at all. Off leaves the field out entirely rather than
/// sending a default: a server that rejects the field rejects any value in it, so there is no number
/// that means "never mind". <paramref name="SendTopP"/> and <paramref name="SendSeed"/> are the same
/// switch for their own parameter.
/// </param>
/// <param name="SendTopP">
/// <inheritdoc cref="SendTemperature" path="/summary"/>
/// </param>
/// <param name="SendSeed">
/// <inheritdoc cref="SendTemperature" path="/summary"/>
/// </param>
/// <param name="ReasoningEffort">
/// The <c>reasoning_effort</c> to send, or null to send none — the switch and the value in one,
/// since it has no default worth a separate flag.
/// </param>
/// <remarks>
/// The two newer switches default to off while the temperature defaults to on. Not a judgement about
/// the parameters: every caller that translates builds these from a profile — see
/// <see cref="OpenAiCompatibleProvider.FromSettings"/> — and sets all six explicitly. The defaults
/// only serve the callers that construct options by hand to ask something else, and those send the
/// smallest request that still carries what they came to test.
/// </remarks>
public sealed record OpenAiCompatibleOptions(
    string BaseUrl,
    string Model,
    string ApiKey = "",
    string ExtraInstructions = "",
    bool SendTemperature = true,
    double Temperature = 0,
    bool SendTopP = false,
    double TopP = 0,
    bool SendSeed = false,
    int Seed = 0,
    string? ReasoningEffort = null,
    int TimeoutSeconds = OpenAiCompatibleProvider.DefaultTimeoutSeconds);

/// <summary>
/// 「OpenAI 相容」 as the application asks for it: the user's setting, the instruction written from
/// it, and the chat requests themselves sent by <see cref="OpenAiChatTranslator"/>.
/// </summary>
/// <remarks>
/// What stays here is what needs the application: the settings and the built-in profile, the prompt
/// templates (language names follow the interface), and the words a failure reaches the user in.
/// What goes over the wire and how an answer is read is the library's.
/// </remarks>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Its own client rather than EngineHttp's: a local model can take tens of seconds to load on the
    // first request, far past what the free engines are given. No timeout of the client's own — each
    // request carries the user's, see TimeoutSeconds.
    private static readonly HttpClient DefaultHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How long one request may take when the timeout box is left empty.</summary>
    internal const int DefaultTimeoutSeconds = 60;

    /// <summary>
    /// The shortest timeout the box accepts. Below this a local model that is still loading on the
    /// first request is cut off every time.
    /// </summary>
    internal const int MinTimeoutSeconds = 10;

    /// <summary>The timeout a saved setting stands for: empty is the default, too short is the minimum.</summary>
    internal static int TimeoutSecondsFor(int? saved) =>
        saved is { } seconds ? Math.Max(MinTimeoutSeconds, seconds) : DefaultTimeoutSeconds;

    private readonly OpenAiChatTranslator _chat;
    private readonly Func<OpenAiCompatibleOptions> _options;

    public OpenAiCompatibleProvider(
        HttpClient? http = null,
        Func<OpenAiCompatibleOptions>? options = null)
    {
        _chat = new OpenAiChatTranslator(http ?? DefaultHttp);
        _options = options ?? (() => FromSettings(SettingsService.Instance.Current));
    }

    /// <summary>
    /// The model the Ollama guide tells the user to install, offered by the reset beside the model
    /// box. Not a default: an empty box is still a configuration error and still refuses to send.
    /// </summary>
    /// <remarks>
    /// A suggestion arrives because somebody asked for it, which is the whole difference from the
    /// fallback that used to live here. A fallback means a translation quietly comes from a model
    /// nobody chose; this only ever fills a box the user is looking at.
    ///
    /// Must name the same build as docs/guides/OLLAMA_GUIDE.*.md. The two go out of step the moment
    /// one is edited alone, and the symptom is a user following the guide and then being offered a
    /// different name by the app.
    /// </remarks>
    internal const string RecommendedModel = "hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS";

    /// <summary>
    /// The setting in use: the one the user picked, or the built-in one.
    /// </summary>
    /// <remarks>
    /// Read at the moment of translating rather than held, so a profile edited in the settings panel
    /// takes effect on the next capture — and so the built-in wording follows the interface language
    /// as it is switched, since <see cref="BuiltInProfile"/> is rebuilt on each call.
    /// </remarks>
    internal static OpenAiCompatibleOptions FromSettings(AppSettings settings)
    {
        var profile = settings.OpenAi.SelectedProfile() ?? BuiltInProfile();

        return new OpenAiCompatibleOptions(
            settings.OpenAiBaseUrl,
            profile.Model,
            settings.OpenAiApiKey,
            profile.ExtraInstructions,
            profile.TemperatureEnabled,
            profile.Temperature,
            profile.TopPEnabled,
            profile.TopP,
            profile.SeedEnabled,
            profile.Seed,
            profile.ReasoningEffortEnabled ? profile.ReasoningEffort : null,
            TimeoutSecondsFor(settings.OpenAiTimeoutSeconds));
    }

    /// <summary>
    /// The setting the application ships with, as a profile like any other.
    /// </summary>
    /// <remarks>
    /// A whole profile rather than a special case threaded through the provider and the panel: the
    /// built-in row in 設定清單 shows a model, a temperature and two prompt pairs exactly as a saved
    /// row does, and everything that reads a setting reads one shape.
    ///
    /// Unnamed — the list localizes the name of this row, and a name stored here would be the wrong
    /// language the moment the interface is switched.
    ///
    /// It names <see cref="RecommendedModel"/> rather than leaving the model empty. That is the one
    /// model this application can honestly name: it is what the guide tells the user to pull, and the
    /// wording below is written for it. Naming it here is not the fallback that was removed — a
    /// fallback filled a box the user could not see, and this is on screen in 目前使用的設定 before a
    /// single line is translated, next to a list whose whole purpose is to replace it.
    /// </remarks>
    internal static OpenAiModelProfile BuiltInProfile() => new()
    {
        Model = RecommendedModel,

        // The initialisers on the profile itself, named rather than repeated — see
        // OpenAiSettings.DefaultTemperature. A new setting is a copy of this one, so the two have to
        // be the same numbers or "add" would quietly change what a model is sent.
        TemperatureEnabled = true,
        Temperature = OpenAiModelProfile.DefaultTemperature,
        TopPEnabled = true,
        TopP = OpenAiModelProfile.DefaultTopP,
        SeedEnabled = true,
        Seed = OpenAiModelProfile.DefaultSeed,
        Auto = new OpenAiPromptPair
        {
            SystemPrompt = DefaultSystemPrompt,
            UserPrompt = DefaultUserPrompt(),
        },
        Explicit = new OpenAiPromptPair
        {
            SystemPrompt = DefaultSystemPrompt,
            UserPrompt = DefaultUserPrompt(),
        },
    };

    // Local OpenAI-compatible servers commonly accept an empty or dummy key. Endpoint and model
    // validation happens when translating, where the UI can show an actionable error.
    public bool RequiresApiKey => false;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken cancellationToken = default,
        TranslationContext? context = null)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options();
        var endpoint = BuildEndpoint(options.BaseUrl);
        var model = options.Model.Trim();

        // No fallback model. Which model is loaded decides what the translation reads like, and this
        // provider talks to whatever the user pointed it at — a name picked here would be a guess
        // about someone else's Ollama, and a wrong guess fails as "model not found" from the server
        // rather than as the one thing the user still has to fill in.
        if (model.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiNoModel"));

        // Counts and configuration only, so this stays in the shipped log: it is what tells a report
        // of "nothing was translated" apart from a request that never left, and names the model the
        // answer came from — with a local server the model is the variable that explains the output.
        Log.Info("OpenAI 相容翻譯：{Count} 個區塊，模型 \"{Model}\"，端點 {Endpoint}",
            blocks.Count, model, endpoint);

        var prompt = BuildPrompt(sourceLang, targetLang, options.ExtraInstructions);

        // The user's own words only at Debug, beside the text: what they wrote can quote what was on
        // screen, the same reason OnnxOcrEngine keeps the recognised text out of the shipped log.
        Log.Debug("OpenAI 相容翻譯 extra=\"{Extra}\"", prompt.ExtraInstructions);

        IReadOnlyList<OpenAiChatOutcome> outcomes;
        try
        {
            outcomes = await _chat.TranslateAsync(
                blocks.Select(block => block.Text).ToList(),
                new OpenAiChatRequest(
                    endpoint, model, options.ApiKey, prompt,
                    LanguageData.GetModelLanguageTag(targetLang),
                    options.SendTemperature ? options.Temperature : null,
                    options.SendTopP ? options.TopP : null,
                    options.SendSeed ? options.Seed : null,
                    options.ReasoningEffort,
                    TimeSpan.FromSeconds(options.TimeoutSeconds)),
                context is null ? null : new OpenAiChatContext(
                    context.History?.Select(Pair).ToList(),
                    context.Reference?.Select(Pair).ToList(),
                    context.SegmentTranslated),
                cancellationToken);
        }
        catch (OpenAiChatException ex)
        {
            throw Localized(ex, options.TimeoutSeconds);
        }

        // Only some blocks failed — every one failing was thrown above, in the user's words. Kinds
        // and counts only, never the text, so this stays in the shipped log: it is what explains a
        // screen where a few blocks were left in the original.
        var failures = outcomes.Where(outcome => !outcome.Translated).ToList();
        if (failures.Count > 0)
            Log.Warn("OpenAI 相容翻譯：{Failed}/{Count} 個區塊失敗，以原文代替（{Kinds}）",
                failures.Count, blocks.Count, string.Join("、", failures
                    .GroupBy(FailureKind)
                    .Select(group => $"{group.Key}×{group.Count()}")));

        // Both sides of one block on one line: a block that came back still in its own language is
        // the shape this provider fails in, and that is only visible by reading the request against
        // the reply.
        if (Log.IsDebugEnabled)
            for (var i = 0; i < blocks.Count; i++)
                Log.Debug("OpenAI 相容翻譯 index={Index} in=\"{In}\" out=\"{Out}\"",
                    i, blocks[i].Text, outcomes[i].Translation ?? $"<{FailureKind(outcomes[i])}>");

        var results = new List<TranslatedBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            // A block whose request failed: the original stands in, and says so — as for the engines
            // behind ResilientProvider, so the overlay and the realtime cache treat both the same.
            var block = blocks[i];
            results.Add(new TranslatedBlock(
                block.Text,
                outcomes[i].Translation ?? block.Text,
                block.Bounds,
                block.Lines,
                block.RenderGlyphHeight)
                {
                    RunsAcross = block.RunsAcross,
                    FontGlyphHeight = block.FontGlyphHeight,
                    Tilt = block.Tilt,
                    Untranslated = !outcomes[i].Translated,
                });
        }

        var detected = LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant();
        return (results, detected);
    }

    /// <summary>How one block failed, for the log: never the server's words, which can quote the text.</summary>
    private static string FailureKind(OpenAiChatOutcome outcome) => outcome.Error switch
    {
        null => "NotSent",
        OpenAiChatException { StatusCode: { } status } chat => $"{chat.Failure}({(int)status})",
        OpenAiChatException chat => chat.Failure.ToString(),
        var other => other.GetType().Name,
    };

    /// <summary>
    /// A failure the server answered with, in the words the capture toolbar's notice and the translation
    /// window's status line show — both put <see cref="Exception.Message"/> on screen as it is.
    /// </summary>
    private static Exception Localized(OpenAiChatException ex, int timeoutSeconds) => ex.Failure switch
    {
        // A TimeoutException, not the cancellation it started as: the capture and the realtime
        // loop both treat a cancellation as the user leaving and stay silent, which is how a server
        // that never answered used to look like nothing had happened at all.
        OpenAiChatFailure.TimedOut => new TimeoutException(
            LocalizationService.Format("S.Error.OpenAiTimeout", timeoutSeconds), ex),
        OpenAiChatFailure.Rejected => new HttpRequestException(
            LocalizationService.Format("S.Error.OpenAiHttp", (int)ex.StatusCode!, ex.ServerMessage switch
            {
                null => LocalizationService.Get("S.Error.UnknownError"),
                "" => LocalizationService.Get("S.Error.NoErrorContent"),
                var message => message,
            }),
            ex,
            ex.StatusCode),
        OpenAiChatFailure.Unparsable => new InvalidOperationException(
            LocalizationService.Get("S.Error.OpenAiUnparsable"), ex),
        _ => new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiNoTranslation"), ex),
    };

    /// <summary>
    /// The server asked when the settings page's address box is left empty: a local Ollama on its
    /// own default port, which is what the setup guide this page links to leaves running.
    /// </summary>
    internal const string DefaultBaseUrl = "http://localhost:11434/v1";

    /// <summary>
    /// The address the requests go to: <see cref="DefaultBaseUrl"/> for an empty box, otherwise
    /// whatever <see cref="OpenAiChatEndpoint.Resolve"/> makes of it.
    /// </summary>
    /// <remarks>Checked before anything is sent, so a mistyped address is named as that.</remarks>
    internal static Uri BuildEndpoint(string baseUrl)
    {
        baseUrl = baseUrl.Trim();
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;

        return OpenAiChatEndpoint.Resolve(baseUrl)
            ?? throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiBadUrl"));
    }

    /// <summary>The placeholder a template uses for the language being translated out of.</summary>
    internal const string SourcePlaceholder = "{source_name}";

    /// <summary>The placeholder a template uses for the language being translated into.</summary>
    internal const string TargetPlaceholder = "{target_name}";

    /// <summary>
    /// What the name placeholders were called before the tags gained their own.
    /// </summary>
    /// <remarks>
    /// Still substituted, and deliberately not advertised in the settings hint: a template someone
    /// wrote before this change is sitting in their settings file, and dropping these would send the
    /// model a literal "{source}" instead of a language. Nothing writes them any more, so the pair
    /// only ever shrinks.
    /// </remarks>
    internal const string LegacySourcePlaceholder = "{source}";

    /// <inheritdoc cref="LegacySourcePlaceholder"/>
    internal const string LegacyTargetPlaceholder = "{target}";

    /// <summary>
    /// The placeholders for the language tags — <c>ja</c> rather than <c>Japanese</c>.
    /// </summary>
    /// <remarks>
    /// Separate from the name rather than fused into it, so a template can put the two wherever its
    /// model expects them: TranslateGemma wanted "Japanese (ja)", another model may want the tag
    /// alone, and this application cannot know which.
    ///
    /// No built-in wording uses these any more — see <see cref="DefaultUserPrompt"/>, which
    /// names the target language and nothing else. They stay because they are a written contract:
    /// templates in users' settings files use them, the settings panel advertises them, and the
    /// model that wanted a tag is one someone may still be pointing this at.
    ///
    /// The names keep meaning only the name, which is also what they meant before the tags existed:
    /// a template someone wrote back then still reads the way they wrote it.
    /// </remarks>
    internal const string SourceCodePlaceholder = "{source_code}";

    /// <inheritdoc cref="SourceCodePlaceholder"/>
    internal const string TargetCodePlaceholder = "{target_code}";

    /// <summary>
    /// The instruction every request of one call is written from: the wording for the interface
    /// language, the target language named as the interface names it, and the user's own rules.
    /// </summary>
    /// <remarks>
    /// <para>One instruction for every model and every caller, which replaces the prompts a profile
    /// used to hold — see <see cref="OpenAiPrompt"/> for what it says and why. The language it is
    /// written in follows the interface as the old built-in wording did: Traditional and Simplified
    /// Chinese for those two interfaces, English for every other.</para>
    ///
    /// <para>The user's rules are filled in like the old templates were, because what was carried
    /// over from them still says <c>{target_name}</c> — see <see cref="OpenAiPromptMigration"/>.
    /// Normalised here as well as where it is saved, so a box saved before the editor did that
    /// reaches the model as the same string the settings panel shows.</para>
    /// </remarks>
    internal static OpenAiPrompt BuildPrompt(string sourceLang, string targetLang, string extraInstructions)
    {
        var extra = OpenAiSettings.NormaliseLineBreaks(extraInstructions).Trim();
        if (extra.Length > 0)
            extra = Fill(extra, sourceLang, targetLang, LanguageData.IsAutomaticSource(sourceLang));

        return new OpenAiPrompt(PromptLanguage(), LanguageData.GetTargetDisplayName(targetLang), extra);
    }

    /// <summary>Which wording the interface language is served — see <see cref="BuildPrompt"/>.</summary>
    /// <remarks>
    /// Branching on <see cref="LocalizationService.Current"/> rather than reading the string
    /// dictionaries, for the reason <see cref="DefaultUserPrompt"/> gives.
    /// </remarks>
    internal static OpenAiPromptLanguage PromptLanguage() => LocalizationService.Current switch
    {
        LocalizationService.TraditionalChinese => OpenAiPromptLanguage.TraditionalChinese,
        LocalizationService.SimplifiedChinese => OpenAiPromptLanguage.SimplifiedChinese,
        _ => OpenAiPromptLanguage.English,
    };

    private static OpenAiTranslationPair Pair(TranslationPair pair) => new(pair.Source, pair.Translation);

    /// <summary>
    /// The system message the application ships with: none.
    /// </summary>
    /// <remarks>
    /// A named constant rather than an empty string written into three places, because it is a
    /// decision rather than an absence. The recommended model is a translation model whose published
    /// format is a single user turn; the system message it never saw in training is a variable that
    /// changes the output for no stated reason. A general chat model pointed at this provider wants
    /// one, which is what the box in the editor is for.
    /// </remarks>
    internal const string DefaultSystemPrompt = "";

    /// <summary>
    /// The user message the application ships with, unfilled — the form the settings panel shows, so
    /// the placeholders are visible rather than described in prose elsewhere.
    /// </summary>
    /// <remarks>
    /// One wording per interface language, which is a reversal: a single English wording shipped
    /// before this, on the reasoning that the string is read by a model rather than by a person. What
    /// changed is the model it is written for. The recommended model follows an instruction in the
    /// language the instruction is written in, and the interface language is the closest thing this
    /// application has to the language its user thinks in.
    ///
    /// Every interface other than the two Chinese ones is served the English wording on purpose: the
    /// model's own documentation publishes a prompt for Chinese and for English and none for any
    /// other language, and inventing a translation of it would be guessing at a format on the user's
    /// behalf. The language the prompt names still follows the interface — see <see cref="Fill"/> —
    /// so a Japanese interface sends an English sentence naming 日本語, which is the owner's decision
    /// and not an oversight.
    ///
    /// Branching on <see cref="LocalizationService.Current"/> rather than reading the string
    /// dictionaries, even though these are translations of one sentence: the dictionaries fall back
    /// to zh-Hant whenever there is no Application to ask — see <see cref="LocalizationService.Get"/>
    /// — so a prompt served that way would be the Chinese one in every test, and the per-language
    /// wording could not be tested at all. This is also the branch that comment in LocalizationService
    /// means by "the prompt templates".
    ///
    /// Written verbatim from what the owner supplied, and deliberately not reflowed: the shape of a
    /// prompt is part of it, and a wording nobody re-measured after editing is a new variable on a
    /// working path. That includes the two line feeds it ends with — the text being translated
    /// starts at the character after them, so trimming the end of this string closes up the blank
    /// line the model was trained to see and runs the instruction into the first line on screen.
    ///
    /// The same sentence in both cases. 自動 and 指定語言 are two settings, two halves of the library
    /// and two arguments, but this model is told what to translate into and detects the rest — so
    /// naming the source language would be an instruction with nothing behind it.
    /// </remarks>
    internal static string DefaultUserPrompt() => LocalizationService.Current switch
    {
        LocalizationService.TraditionalChinese =>
            $"將以下文本翻譯為{TargetPlaceholder}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n",

        LocalizationService.SimplifiedChinese =>
            $"将以下文本翻译为{TargetPlaceholder}，注意只需要输出翻译后的结果，不要额外解释：\n\n",

        // English, and every other interface language.
        _ =>
            $"Translate the following text into {TargetPlaceholder}. Note that you should only " +
            "output the translated result without any additional explanation:\n\n",
    };

    /// <summary>
    /// Substitutes the language placeholders, naming every language in the interface's own language
    /// so a filled built-in template reads as one sentence.
    /// </summary>
    /// <remarks>
    /// The names followed English regardless of the interface before the built-in wording did, and
    /// changed with it: an instruction written in Chinese that names its target language in English
    /// is a sentence in two languages, and the model this provider is written for is documented as
    /// wanting the language named the way the instruction around it is written. One placeholder
    /// still resolves one way — that way is now "the interface's language" rather than "English".
    ///
    /// What this costs is a template someone wrote in a language other than the interface's: their
    /// English sentence on a Chinese interface now names 繁體中文 where it used to name Traditional
    /// Chinese. The parameter table in the prompt editor shows the example in the interface language
    /// for that reason — it is the one place the rule is visible before a prompt is sent.
    ///
    /// The language tags beside the names are unaffected: those have only ever been the model's own
    /// spelling and have no localised form to choose between.
    /// </remarks>
    /// <remarks>
    /// The code placeholders are replaced before the name ones purely for readability; the two sets
    /// cannot collide, because <c>{source}</c> is not a prefix of <c>{source_code}</c> once the
    /// closing brace is counted.
    ///
    /// 自動 has no tag to give, so <see cref="SourceCodePlaceholder"/> empties out there. A template
    /// that wrote its own brackets around it is left with an empty pair, which is the cost of letting
    /// templates place the tag themselves — the built-in automatic template names no source at all
    /// and so never shows it.
    /// </remarks>
    private static string Fill(string template, string sourceLang, string targetLang, bool automatic)
    {
        // Nothing to name when the source is 自動. The built-in automatic template names no source
        // at all, so this only ever reaches a template the user wrote one into themselves.
        //
        // Left in English while the names beside it follow the interface, because it is not a
        // language name: no built-in wording reaches it, and the template that does is one someone
        // wrote in a language this application cannot know. Changing it is a separate decision.
        var source = automatic
            ? "any language"
            : LanguageData.GetSourceDisplayName(sourceLang);

        var target = LanguageData.GetTargetDisplayName(targetLang);

        return template
            .Replace(SourceCodePlaceholder, automatic ? "" : LanguageData.GetModelLanguageTag(sourceLang),
                StringComparison.OrdinalIgnoreCase)
            .Replace(TargetCodePlaceholder, LanguageData.GetModelLanguageTag(targetLang),
                StringComparison.OrdinalIgnoreCase)
            .Replace(SourcePlaceholder, source, StringComparison.OrdinalIgnoreCase)
            .Replace(TargetPlaceholder, target, StringComparison.OrdinalIgnoreCase)
            .Replace(LegacySourcePlaceholder, source, StringComparison.OrdinalIgnoreCase)
            .Replace(LegacyTargetPlaceholder, target, StringComparison.OrdinalIgnoreCase);
    }
}
