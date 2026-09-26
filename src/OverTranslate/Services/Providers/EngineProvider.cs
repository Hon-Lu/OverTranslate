using OverTranslate.Engines;

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

        return (translated, DetectedLanguage.Vote(answers.Select(answer => answer.DetectedLanguage)));
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
