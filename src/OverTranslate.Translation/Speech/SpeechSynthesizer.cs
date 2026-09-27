using System.Diagnostics;
using NLog;

namespace OverTranslate.Translation.Speech;

/// <summary>Reads a text aloud with the first engine that has a voice for it and answers.</summary>
/// <remarks>
/// <para>One after another, never side by side: a press of the speaker is one sound, and asking
/// four engines at once for it would be four downloads to play one. The next engine is only asked
/// when the one before has no voice for the language, fails, or runs out its request timeout.</para>
///
/// <para>Logs engines, languages, sizes and timings, never the text — the same rule as
/// <see cref="BatchTranslator"/>, for the same reason.</para>
/// </remarks>
public sealed class SpeechSynthesizer(IReadOnlyList<ISpeechEngine> engines)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Whether any engine has a voice for <paramref name="language"/>.</summary>
    public bool Supports(string language) => engines.Any(engine => engine.Supports(language));

    /// <returns>The speech as MP3, and the name of the engine that produced it.</returns>
    /// <exception cref="TranslationEngineException">
    /// No engine has a voice for the language, or every one that has failed — the last failure.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the request in flight is aborted.</exception>
    public async Task<(byte[] Audio, string Engine)> SynthesizeAsync(
        string text, string language, CancellationToken cancellationToken = default)
    {
        TranslationEngineException? last = null;

        foreach (var engine in engines.Where(engine => engine.Supports(language)))
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var audio = await engine.SynthesizeAsync(text, language, cancellationToken);
                Log.Debug("朗讀：{Engine}，{Language}，{Characters} 字，{Bytes} bytes，{Elapsed} ms",
                    engine.Name, language, text.Length, audio.Length, Elapsed(started));
                return (audio, engine.Name);
            }
            catch (TranslationEngineException ex)
            {
                Log.Info("朗讀 {Engine} 失敗：{Language}，{Characters} 字，{Elapsed} ms，{Status}，{Reason}，改試下一個",
                    engine.Name, language, text.Length, Elapsed(started),
                    ex.StatusCode is { } status ? (int)status : "-", ex.Message);
                last = ex;
            }
        }

        throw last ?? new TranslationEngineException("Speech", $"no voice for {language}");
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
