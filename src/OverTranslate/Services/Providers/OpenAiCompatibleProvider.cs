using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <param name="Model">
/// The model to ask for. Required: empty is a configuration error rather than a fallback — see
/// <see cref="OpenAiCompatibleProvider.TranslateAsync"/>.
/// </param>
/// <param name="PromptAuto">What to send when the source language is 自動.</param>
/// <param name="PromptExplicit">What to send when a source language has been chosen.</param>
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
    OpenAiPromptPair? PromptAuto = null,
    OpenAiPromptPair? PromptExplicit = null,
    bool SendTemperature = true,
    double Temperature = 0,
    bool SendTopP = false,
    double TopP = 0,
    bool SendSeed = false,
    int Seed = 0)
{
    /// <summary>
    /// The pair for one case. Empty rather than the built-in wording when nothing was supplied.
    /// </summary>
    /// <remarks>
    /// The built-in setting is resolved into these options rather than behind them — see the
    /// constructor, which asks <see cref="OpenAiSettings.SelectedProfile"/> first and falls back to
    /// <see cref="OpenAiCompatibleProvider.BuiltInProfile"/> once. So by the time a pair reaches
    /// here it is the whole answer: an empty half means "send no such message", not "substitute
    /// something". A profile with both halves empty is a shape the editor refuses to save.
    /// </remarks>
    public OpenAiPromptPair PromptsFor(bool automatic) =>
        (automatic ? PromptAuto : PromptExplicit) ?? new OpenAiPromptPair();
}

/// <summary>
/// Translates through the OpenAI-compatible Chat Completions contract. Each OCR block is an
/// independent request so its bounds and ordering stay aligned with the existing provider model.
/// </summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const int MaxConcurrentRequests = 8;
    private static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Func<OpenAiCompatibleOptions> _options;

    public OpenAiCompatibleProvider(
        HttpClient? http = null,
        Func<OpenAiCompatibleOptions>? options = null)
    {
        _http = http ?? DefaultHttp;
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
            profile.Auto,
            profile.Explicit,
            profile.TemperatureEnabled,
            profile.Temperature,
            profile.TopPEnabled,
            profile.TopP,
            profile.SeedEnabled,
            profile.Seed);
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
        CancellationToken cancellationToken = default)
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

        var configuredApiKey = options.ApiKey.Trim();

        // Counts and configuration only, so this stays in the shipped log: it is what tells a report
        // of "nothing was translated" apart from a request that never left, and names the model the
        // answer came from — with a local server the model is the variable that explains the output.
        Log.Info("OpenAI 相容翻譯：{Count} 個區塊，模型 \"{Model}\"，端點 {Endpoint}",
            blocks.Count, model, endpoint);

        // Built once for the batch: every block is sent the same instruction, and the user may have
        // written this one themselves, so it is worth resolving in one place rather than per request.
        var prompts = BuildPrompts(sourceLang, targetLang, options.PromptsFor(
            LanguageData.IsAutomaticSource(sourceLang)));

        // The prompt and the text itself only at Debug: the text is whatever was on the user's
        // screen, the same reason OnnxOcrEngine keeps the recognised text out of the shipped log.
        Log.Debug("OpenAI 相容翻譯 system=\"{System}\" user=\"{User}\"",
            prompts.System, prompts.User);

        var translations = new string[blocks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, blocks.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentRequests,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                translations[index] = await TranslateOneAsync(
                    blocks[index].Text,
                    prompts,
                    configuredApiKey,
                    endpoint,
                    model,
                    Sampling.From(options),
                    token);

                // Both sides of one block on one line: a block that came back still in its own
                // language is the shape this provider fails in, and that is only visible by reading
                // the request against the reply.
                if (Log.IsDebugEnabled)
                    Log.Debug("OpenAI 相容翻譯 index={Index} in=\"{In}\" out=\"{Out}\"",
                        index, blocks[index].Text, translations[index]);
            });

        var results = new List<TranslatedBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            results.Add(new TranslatedBlock(
                block.Text,
                translations[i],
                block.Bounds,
                block.Lines,
                block.RenderGlyphHeight) { RunsAcross = block.RunsAcross });
        }

        var detected = LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant();
        return (results, detected);
    }

    private async Task<string> TranslateOneAsync(
        string text,
        (string System, string User) prompts,
        string apiKey,
        Uri endpoint,
        string model,
        Sampling sampling,
        CancellationToken cancellationToken)
    {
        // A dictionary rather than an anonymous type because the sampling fields are conditional: a
        // server that refuses one of them refuses every value of it, so the only way to say nothing
        // is to send no such field.
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(prompts, text),
        };
        if (sampling.Temperature is { } temperature) payload["temperature"] = temperature;
        if (sampling.TopP is { } topP) payload["top_p"] = topP;
        if (sampling.Seed is { } seed) payload["seed"] = seed;
        payload["stream"] = false;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var trimmedKey = apiKey.Trim();
        if (trimmedKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                LocalizationService.Format(
                    "S.Error.OpenAiHttp", (int)response.StatusCode, ReadError(json)),
                null,
                response.StatusCode);

        string content;
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            content = ReadContent(message);
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiUnparsable"), ex);
        }

        var translated = StripThinking(content);
        if (translated.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiNoTranslation"));
        return translated;
    }

    /// <summary>
    /// The sampling parameters for one request: each one a value to send, or null to send no such
    /// field.
    /// </summary>
    /// <remarks>
    /// Three nullables together rather than three arguments, so that adding a fourth parameter is an
    /// edit to one type instead of to every signature between the options and the payload.
    /// </remarks>
    internal readonly record struct Sampling(double? Temperature, double? TopP, int? Seed)
    {
        public static Sampling From(OpenAiCompatibleOptions options) => new(
            options.SendTemperature ? options.Temperature : null,
            options.SendTopP ? options.TopP : null,
            options.SendSeed ? options.Seed : null);
    }

    /// <summary>
    /// The server asked when the settings page's address box is left empty: a local Ollama on its
    /// own default port, which is what the setup guide this page links to leaves running.
    /// </summary>
    internal const string DefaultBaseUrl = "http://localhost:11434/v1";

    internal static Uri BuildEndpoint(string baseUrl)
    {
        baseUrl = baseUrl.Trim();
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiBadUrl"));

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path;
            return builder.Uri;
        }

        if (path.Length == 0)
            path = "/v1";
        builder.Path = $"{path}/chat/completions";
        return builder.Uri;
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
    /// The two messages one batch sends, with the language placeholders filled in.
    /// </summary>
    /// <param name="prompts">
    /// The pair for the case in hand, already resolved against the built-in setting — see
    /// <see cref="OpenAiCompatibleOptions.PromptsFor"/>.
    /// </param>
    /// <remarks>
    /// Either half may come back empty, and empty means the request carries no such message. Which
    /// of the two a model wants is the model's business: the recommended one is trained on the
    /// instruction sitting in the user turn immediately above the text, and has no system turn in
    /// its documented format at all.
    ///
    /// Not trimmed, only normalised. What a prompt ends with is part of the prompt: the user half is
    /// followed immediately by the text being translated — see <see cref="BuildMessages"/> — so the
    /// blank line between the two lives at the end of the wording, where whoever wrote it can see it
    /// and a model that wants no blank line can be told so by deleting it.
    ///
    /// A half that is nothing but whitespace is still empty, though. A box that looks empty and a
    /// box that is empty have to mean the same thing, or a stray space is sent as an instruction.
    /// </remarks>
    internal static (string System, string User) BuildPrompts(
        string sourceLang,
        string targetLang,
        OpenAiPromptPair prompts)
    {
        var automatic = LanguageData.IsAutomaticSource(sourceLang);

        // Normalised here as well as where it is saved, so a prompt written before the editor did
        // that reaches the model as the same string the settings panel shows it as — see
        // OpenAiSettings.NormaliseLineBreaks. The built-in wordings are already bare LFs.
        string One(string template)
        {
            var text = OpenAiSettings.NormaliseLineBreaks(template);
            return text.Trim().Length == 0 ? "" : Fill(text, sourceLang, targetLang, automatic);
        }

        return (One(prompts.SystemPrompt), One(prompts.UserPrompt));
    }

    /// <summary>
    /// The messages for one request: the instruction, and the text to translate under it.
    /// </summary>
    /// <remarks>
    /// The user prompt goes in front of the text in the same message rather than in a message of its
    /// own. That is the format the recommended model documents — an instruction, a blank line, then
    /// the segment — and a model trained that way reads two separate user turns as a conversation it
    /// is being asked to continue rather than as a job.
    ///
    /// Joined with nothing at all: the separator belongs to the wording, which is why the built-in
    /// one ends in a colon and two line feeds. A separator added here would be this application
    /// deciding the shape of somebody else's documented prompt format, and it could not be turned
    /// off — the model that wants its segment on the very next character would have no way to say
    /// so. See <see cref="DefaultUserPrompt"/>.
    ///
    /// A system message only when there is one to send. An empty system turn is not nothing — it is
    /// a turn — and the setting this ships with deliberately has none.
    ///
    /// Every line break that leaves here is a bare \n, the text being translated included. The
    /// prompts were normalised upstream in <see cref="BuildPrompts"/>; the text was not, and it is
    /// the half that arrives from outside this application — a block the OCR joined, or a line a
    /// capture carried a \r into. Sending one message written two ways means the same screen reaches
    /// the model as two different strings depending on where its line breaks came from.
    /// </remarks>
    internal static object[] BuildMessages((string System, string User) prompts, string text)
    {
        text = OpenAiSettings.NormaliseLineBreaks(text);

        var messages = new List<object>(2);
        if (prompts.System.Length > 0)
            messages.Add(new { role = "system", content = prompts.System });

        messages.Add(new
        {
            role = "user",
            content = prompts.User + text,
        });

        return [.. messages];
    }

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
    /// 日本語 and 한국어 are served the English wording on purpose: the model's own documentation
    /// publishes a prompt for Chinese and for English and none for these two, and inventing a
    /// translation of it would be guessing at a format on the user's behalf. The language the prompt
    /// names still follows the interface — see <see cref="Fill"/> — so a Japanese interface sends an
    /// English sentence naming 日本語, which is the owner's decision and not an oversight.
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

        // English is also the default arm, which is what a language this build has no dictionary for
        // would take — unreachable in practice, since LocalizationService.Current resolves the stored
        // code against the same table first, and present because a switch has to be exhaustive.
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

    internal static string StripThinking(string value) => ThinkingBlock.Replace(value, "").Trim();

    private static string ReadContent(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray().Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : ""));
        }

        return "";
    }

    private static string ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString() ?? LocalizationService.Get("S.Error.UnknownError");
        }
        catch (JsonException)
        {
            // Non-JSON proxies and local servers are common; return a bounded response below.
        }

        var compact = json.Trim();
        if (compact.Length == 0) return LocalizationService.Get("S.Error.NoErrorContent");
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }
}
