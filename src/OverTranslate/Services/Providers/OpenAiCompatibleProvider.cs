using System.Net.Http;
using NLog;
using OverTranslate.Models;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.Providers;

/// <param name="Model">
/// The model to ask for. Required: empty is a configuration error rather than a fallback — see
/// <see cref="OpenAiCompatibleProvider.TranslateAsync"/>.
/// </param>
/// <param name="Template">
/// The user's own template, unfilled, or empty for the application's — see
/// <see cref="OpenAiModelProfile.Template"/>.
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
/// <param name="Batch">
/// Whether a screen goes as few requests as fit — see <see cref="AppSettings.OpenAiBatch"/>.
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
    string Template = "",
    bool SendTemperature = true,
    double Temperature = 0,
    bool SendTopP = false,
    double TopP = 0,
    bool SendSeed = false,
    int Seed = 0,
    string? ReasoningEffort = null,
    int TimeoutSeconds = OpenAiCompatibleProvider.DefaultTimeoutSeconds,
    bool Batch = false);

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
            profile.Template,
            profile.TemperatureEnabled,
            profile.Temperature,
            profile.TopPEnabled,
            profile.TopP,
            profile.SeedEnabled,
            profile.Seed,
            profile.ReasoningEffortEnabled ? profile.ReasoningEffort : null,
            TimeoutSecondsFor(settings.OpenAiTimeoutSeconds),
            settings.OpenAiBatch);
    }

    /// <summary>
    /// The setting the application ships with, as a profile like any other.
    /// </summary>
    /// <remarks>
    /// A whole profile rather than a special case threaded through the provider and the panel: the
    /// built-in row in 設定清單 shows a model, its parameters and its template — the application's —
    /// exactly as a saved row does, and everything that reads a setting reads one shape.
    ///
    /// Unnamed — the list localizes the name of this row, and a name stored here would be the wrong
    /// language the moment the interface is switched.
    ///
    /// It names <see cref="RecommendedModel"/> rather than leaving the model empty. That is the one
    /// model this application can honestly name: it is what the guide tells the user to pull, and the
    /// wording below is written for it. Naming it here is not the fallback that was removed — a
    /// fallback filled a box the user could not see, and this is on screen in 目前使用的設定 before a
    /// single line is translated, next to a list whose whole purpose is to replace it.
    ///
    /// Reasoning Effort is left off, as on any new profile: the recommended model does not think,
    /// and a field it does not need is one more thing a server can refuse.
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
        var prompt = BuildPrompt(sourceLang, targetLang, options.Template);
        Log.Info("OpenAI 相容翻譯：{Count} 個區塊，模型 \"{Model:l}\"，端點 {Endpoint}，{Mode:l}，{Way:l}",
            blocks.Count, model, endpoint, prompt.IsDefault ? "預設模板" : prompt.HasContext ? "自訂模板" : "自訂模板（無上下文）",
            options.Batch ? "批次" : "逐段");

        // The user's own words only at Debug, beside the text: what they wrote can quote what was on
        // screen, the same reason OnnxOcrEngine keeps the recognised text out of the shipped log.
        if (!prompt.IsDefault)
            Log.Debug("OpenAI 相容翻譯 template=\"{Template:l}\"", prompt.Template);

        IReadOnlyList<OpenAiChatOutcome> outcomes;
        try
        {
            outcomes = await _chat.TranslateAsync(
                blocks.Select(block => block.Text).ToList(),
                Request(options, endpoint, model, prompt),
                context is null ? null : new OpenAiChatContext(
                    context.History?.Select(Pair).ToList(),
                    context.Screen?.Select(line => new OpenAiScreenLine(line.Source, line.Translation)).ToList()),
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

    /// <summary>
    /// Has the server load the model before the first translation needs it — see
    /// <see cref="OpenAiChatTranslator.WarmUpAsync"/>.
    /// </summary>
    /// <remarks>
    /// Sent whatever the server is, on this machine or not: whether a model is already in memory is
    /// the server's business, and for one that keeps it there the request costs a token. Throws as
    /// the request did, unworded — nobody is shown a warm-up's failure, and the translation that
    /// follows meets the same cause and words it.
    /// </remarks>
    public async Task WarmUpAsync(string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        var options = _options();
        var model = options.Model.Trim();
        if (model.Length == 0) return;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _chat.WarmUpAsync(
            Request(options, BuildEndpoint(options.BaseUrl), model,
                BuildPrompt(sourceLang, targetLang, options.Template)),
            cancellationToken);
        Log.Info("OpenAI 相容暖機完成：{Elapsed} ms",
            (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static OpenAiChatRequest Request(
        OpenAiCompatibleOptions options, Uri endpoint, string model, OpenAiPrompt prompt) =>
        new(endpoint, model, options.ApiKey, prompt,
            options.SendTemperature ? options.Temperature : null,
            options.SendTopP ? options.TopP : null,
            options.SendSeed ? options.Seed : null,
            options.ReasoningEffort,
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            Batch: options.Batch);

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
    /// No built-in wording uses these. They stay because they are a written contract: a template the
    /// user wrote may use them.
    ///
    /// The names keep meaning only the name, which is also what they meant before the tags existed:
    /// a template someone wrote back then still reads the way they wrote it.
    /// </remarks>
    internal const string SourceCodePlaceholder = "{source_code}";

    /// <inheritdoc cref="SourceCodePlaceholder"/>
    internal const string TargetCodePlaceholder = "{target_code}";

    /// <summary>
    /// The template every request of one call is written from: the user's own, or the application's
    /// in the interface's language, its language placeholders filled in.
    /// </summary>
    /// <remarks>
    /// <para>The language the application's is written in follows the interface as the old built-in
    /// wording did: Traditional and Simplified Chinese for those two interfaces, English for every
    /// other. Every language name — the placeholders and the headings of the context alike — is written
    /// in that language too: an English sentence on a Japanese interface names its languages in
    /// English.</para>
    ///
    /// <para>A template that is nothing but whitespace is the application's. Normalised here as well
    /// as where it is saved, so a box saved before the editor did that reaches the model as the same
    /// string the settings panel shows.</para>
    /// </remarks>
    internal static OpenAiPrompt BuildPrompt(string sourceLang, string targetLang, string template)
    {
        var language = PromptLanguage();
        var english = language == OpenAiPromptLanguage.English;
        var automatic = LanguageData.IsAutomaticSource(sourceLang);
        var own = OpenAiSettings.NormaliseLineBreaks(template);
        var isDefault = own.Trim().Length == 0;

        return new OpenAiPrompt(
            language,
            Fill(isDefault ? OpenAiPrompt.DefaultTemplate(language) : own, sourceLang, targetLang, automatic, english),
            LanguageData.GetTargetDisplayName(targetLang, english),
            isDefault,
            automatic ? null : LanguageData.GetSourceDisplayName(sourceLang, english));
    }

    /// <summary>
    /// The application's template in the interface's language, unfilled: what the editor shows and
    /// compares against.
    /// </summary>
    internal static string DefaultTemplate() => OpenAiPrompt.DefaultTemplate(PromptLanguage());

    /// <summary>
    /// What a template box is saved as: empty when it is blank or reads the same as the application's
    /// template in the interface's language — so the application's keeps following the interface and
    /// its own revisions — and otherwise the text with its line breaks made bare, not trimmed.
    /// </summary>
    /// <remarks>
    /// Compared with line breaks made bare and the ends trimmed. Not trimmed when kept: a template
    /// with no <c>{text}</c> is followed straight by the text, so the blank line it ends with is part
    /// of it.
    /// </remarks>
    internal static string TemplateToStore(string text)
    {
        var bare = OpenAiSettings.NormaliseLineBreaks(text);
        var trimmed = bare.Trim();
        return trimmed.Length == 0 || trimmed == DefaultTemplate() ? "" : bare;
    }

    /// <summary>Which wording the interface language is served — see <see cref="BuildPrompt"/>.</summary>
    /// <remarks>
    /// Branching on <see cref="LocalizationService.Current"/> rather than reading the string
    /// dictionaries: the dictionaries fall back to zh-Hant whenever there is no Application to ask —
    /// see <see cref="LocalizationService.Get"/> — so a wording chosen that way would be the Chinese
    /// one in every test, and the per-language wording could not be tested at all. This is also the
    /// branch that comment in LocalizationService means by "the prompt templates".
    /// </remarks>
    internal static OpenAiPromptLanguage PromptLanguage() => LocalizationService.Current switch
    {
        LocalizationService.TraditionalChinese => OpenAiPromptLanguage.TraditionalChinese,
        LocalizationService.SimplifiedChinese => OpenAiPromptLanguage.SimplifiedChinese,
        _ => OpenAiPromptLanguage.English,
    };

    private static OpenAiTranslationPair Pair(TranslationPair pair) => new(pair.Source, pair.Translation);

    /// <summary>
    /// Substitutes the language placeholders, naming every language in the language the template's
    /// wording is chosen in — see <see cref="PromptLanguage"/> — so a filled template reads as one
    /// sentence.
    /// </summary>
    /// <remarks>
    /// An instruction written in Chinese that names its target language in English is a sentence in
    /// two languages, and the model this provider is written for is documented as wanting the
    /// language named the way the instruction around it is written. So the two Chinese interfaces
    /// name languages in their own words, and every other interface — whose wording is English —
    /// names them in English, not in the interface's language.
    ///
    /// What this costs is a template someone wrote in a language other than that one: their English
    /// sentence on a Chinese interface names 繁體中文 where it used to name Traditional Chinese.
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
    /// templates place the tag themselves.
    /// </remarks>
    private static string Fill(string template, string sourceLang, string targetLang, bool automatic, bool english)
    {
        // Nothing to name when the source is 自動. Only text the user wrote a source into reaches
        // this.
        //
        // Left in English while the names beside it follow the interface, because it is not a
        // language name: no built-in wording reaches it, and the template that does is one someone
        // wrote in a language this application cannot know. Changing it is a separate decision.
        var source = automatic
            ? "any language"
            : LanguageData.GetSourceDisplayName(sourceLang, english);

        var target = LanguageData.GetTargetDisplayName(targetLang, english);

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
