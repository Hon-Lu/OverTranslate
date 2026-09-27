using NLog;
using OverTranslate.Translation;

namespace OverTranslate.Services.Providers;

/// <summary>
/// One free engine on its own: no retry, no fallback, and a failure goes straight to the caller.
/// </summary>
/// <remarks>
/// What 文字翻譯 asks for (<c>resilient: false</c>). It shows one text and a retry button, and an
/// answer from an engine the user did not pick, presented as though it were theirs, is worse there
/// than an error they can act on.
/// </remarks>
public sealed class EngineProvider(ITextTranslator engine) : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => engine.Name;

    public bool RequiresApiKey => false;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");

        var answers = await engine.TranslateAsync(
            blocks.Select(block => block.Text).ToList(),
            EngineLanguage.ToEngine(targetLang), EngineLanguage.SourceToEngine(sourceLang),
            cancellationToken);

        var translated = blocks.Select((block, i) =>
            new TranslatedBlock(block.Text, answers[i].Text, block.Bounds, block.Lines, block.RenderGlyphHeight)
                { RunsAcross = block.RunsAcross }).ToList();

        for (var i = 0; i < blocks.Count; i++)
            TranslatedTextLog.Write(Log, i, engine.Name, blocks[i].Text, answers[i].Text);

        return (translated, DetectedLanguage.Vote(answers.Select(answer => answer.DetectedLanguage)));
    }
}

/// <summary>Each block's text and what it came back as, for the verbose log only.</summary>
/// <remarks>
/// <para>The half of a report the log could not otherwise show. OCR already writes what it read at
/// Debug; without the answer beside it, a line that "was not translated" cannot be told apart
/// between an engine handing the text back unchanged, an engine dropping a name, an empty answer
/// and the overlay failing to draw — and sending the text again rarely reproduces any of them.</para>
///
/// <para>Here in the application, not in OverTranslate.Translation, which promises never to log
/// text at all. Debug only, like the OCR text it pairs with: this is whatever was on the user's
/// screen, and it is written only when they turned 記錄詳細資訊 on. Each line carries both sides so
/// it stands on its own, the way the OpenAI-compatible provider's does.</para>
/// </remarks>
internal static class TranslatedTextLog
{
    public static void Write(Logger log, int index, string engine, string source, string translation)
    {
        if (log.IsDebugEnabled)
            log.Debug("翻譯結果 index={Index} engine={Engine} in=\"{In}\" out=\"{Out}\"",
                index, engine, source, translation);
    }
}

/// <summary>The language a list of texts is in, by majority.</summary>
internal static class DetectedLanguage
{
    /// <remarks>
    /// A screen can mix languages, and the callers that want one answer — 文字翻譯's speaker and
    /// dictionary, quick lookup's bilingual mode — send one text anyway, where the vote is unanimous.
    /// </remarks>
    public static string Vote(IEnumerable<string> detected)
    {
        var votes = detected
            .Select(EngineLanguage.FromEngine)
            .Where(code => code.Length > 0)
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Code: group.Key, Count: group.Count()))
            .ToList();

        return votes.Count > 0 ? votes.MaxBy(vote => vote.Count).Code : "";
    }
}
