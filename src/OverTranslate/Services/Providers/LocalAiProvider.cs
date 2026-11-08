using System.Net.Http;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services.LocalAi;
using OverTranslate.Translation;
using OverTranslate.Translation.OpenAi;

namespace OverTranslate.Services.Providers;

/// <summary>The settings a translation is made with.</summary>
internal sealed record LocalAiOptions(bool Batch, string Style, int TimeoutSeconds);

/// <summary>
/// 本機 AI 翻譯: the same instruction and the same reading of the answer as the OpenAI-compatible
/// service, sent to a model this application runs itself.
/// </summary>
/// <remarks>
/// <para>Translation is <see cref="OpenAiChatTranslator"/>'s, whole: what goes in a request, what is
/// sent again. Only how a message reaches the model differs (<see cref="SendAsync"/>), wrapped in the
/// model's own markers and put through <c>/completion</c>.</para>
///
/// <para>The timeout counts one request and starts once the model is up: a large model takes longer
/// to load than anyone would set as a timeout, and that wait has a limit of its own
/// (<see cref="LocalAiServerProcess.StartLimit"/>).</para>
///
/// <para>A text too long for the model's context is cut into pieces that fit. Each piece is a text
/// as far as the translator is concerned, so each gets its own timeout and the translations of the
/// pieces before it, and they are joined back into the one block they came from.</para>
/// </remarks>
internal sealed class LocalAiProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient DefaultHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // The instruction takes about a hundred, and an answer two tokens or so a character; 4096 in all
    // — see OpenAiChatRequest.ContextTokens, which is what the translator cuts context against.
    internal const int MaxPieceCharacters = 1000;

    // A loop is what this stops: never an answer of a length the text could need.
    private const int MaxAnswerTokens = 3000;

    // Never sent anywhere: the translator wants an address, and this provider has its own route.
    private static readonly Uri NoEndpoint = new("http://127.0.0.1/");

    private readonly OpenAiChatTranslator _chat;
    private readonly ILocalAiBackend _backend;
    private readonly Func<LocalAiOptions> _options;
    private readonly HttpClient _http;

    internal LocalAiProvider(ILocalAiBackend? backend = null, Func<LocalAiOptions>? options = null, HttpClient? http = null)
    {
        _backend = backend ?? AppServices.LocalAi;
        _options = options ?? (() => FromSettings(SettingsService.Instance.Current.LocalAi));
        _http = http ?? DefaultHttp;
        _chat = new OpenAiChatTranslator(SendAsync);
    }

    internal static LocalAiOptions FromSettings(LocalAiSettings settings) => new(
        settings.Batch, settings.Style, OpenAiCompatibleProvider.TimeoutSecondsFor(settings.TimeoutSeconds));

    public bool RequiresApiKey => false;

    /// <summary>The longest answer to allow a message of this length: room for it, and no more.</summary>
    internal static int AnswerLimit(int messageLength) => Math.Min(MaxAnswerTokens, messageLength * 3 + 64);

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

        // Up front and outside the translator, which takes a failed request for that text's fault:
        // none of these is, and each is one sentence to the user rather than one per block.
        using (await _backend.ReadyAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        var options = _options();
        var prompt = OpenAiCompatibleProvider.BuildPrompt(sourceLang, targetLang, "", options.Batch, options.Style);
        var request = new OpenAiChatRequest(
            NoEndpoint, "", "", prompt,
            OpenAiModelProfile.DefaultTemperature,
            OpenAiModelProfile.DefaultTopP,
            OpenAiModelProfile.DefaultSeed,
            Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
            Batch: options.Batch);

        var cut = blocks.Select(block => TextPieces.Cut(block.Text, MaxPieceCharacters)).ToList();
        var pieces = cut.SelectMany(text => text.Pieces).ToList();

        Log.Info("本機 AI 翻譯：{Count} 個區塊（{Pieces} 段），{Way:l}", blocks.Count, pieces.Count, options.Batch ? "批次" : "逐段");

        IReadOnlyList<OpenAiChatOutcome> outcomes;
        try
        {
            outcomes = await _chat.TranslateAsync(
                pieces,
                request,
                context is null ? null : new OpenAiChatContext(
                    context.History?.Select(OpenAiCompatibleProvider.Pair).ToList(),
                    context.Screen?.Select(line => new OpenAiScreenLine(line.Source, line.Translation)).ToList()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiChatException ex)
        {
            throw Localized(ex, options.TimeoutSeconds);
        }
        catch (HttpRequestException ex)
        {
            // The server went away and did not come back.
            throw new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiStartFailed"), ex);
        }

        var results = new List<TranslatedBlock>(blocks.Count);
        var at = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var count = cut[i].Pieces.Count;
            var own = outcomes.Skip(at).Take(count).ToList();
            at += count;
            results.Add(OpenAiCompatibleProvider.ResultFor(
                blocks[i],
                // A block with a piece missing stands as its original: half a translation joined to
                // half the text would read as a finished one.
                own.All(outcome => outcome.Translated)
                    ? cut[i].Join(own.Select(outcome => outcome.Translation!).ToList())
                    : null));
        }

        var failed = outcomes.Count(outcome => !outcome.Translated);
        if (failed > 0)
            Log.Warn("本機 AI 翻譯：{Failed}/{Count} 段失敗，該區塊以原文代替", failed, pieces.Count);

        var detected = LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant();
        return (results, detected);
    }

    /// <summary>Starts the model, for the moment before a session's first translation.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        using (await _backend.ReadyAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// One message to the model. A server that dies partway through is waited for and asked again: it
    /// is brought back on its own, and the screen just says 翻譯中… for longer.
    /// </summary>
    private async Task<string> SendAsync(
        string message, OpenAiChatRequest request, int? maxTokens, CancellationToken token)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            LocalAiReady ready;
            try
            {
                ready = await _backend.ReadyAsync(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // No text could have caused this, so nothing else should be sent — which is what
                // the translator takes a failed connection to mean.
                throw new HttpRequestException(ex.Message, ex);
            }

            using (ready)
            {
                try
                {
                    return await LocalAiCompletion.SendAsync(
                        _http,
                        ready.Lease.Process,
                        LocalAiPromptFormat.Wrap(ready.Format, message),
                        maxTokens ?? AnswerLimit(message.Length),
                        request.Timeout!.Value,
                        token,
                        request.Temperature,
                        request.TopP,
                        request.Seed).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < attempts && !token.IsCancellationRequested)
                {
                    Log.Info("本機 AI 翻譯：連線中斷（{Message}），等待模型重新啟動", ex.Message);
                }
            }
        }
    }

    private static Exception Localized(OpenAiChatException ex, int timeoutSeconds) => ex.Failure switch
    {
        OpenAiChatFailure.TimedOut => new TimeoutException(LocalizationService.Format("S.Error.LocalAiTimeout", timeoutSeconds), ex),
        _ => new InvalidOperationException(LocalizationService.Get("S.Error.LocalAiNoTranslation"), ex),
    };
}
