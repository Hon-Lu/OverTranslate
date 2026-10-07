using System.Runtime.InteropServices;
using System.Windows;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate.Views.QuickTranslate;

/// <summary>
/// 替換翻譯: the selected text, translated and put back in its place.
/// </summary>
/// <remarks>
/// The shortest path this application has. The other three translation surfaces all end with the
/// user reading an answer in a window of ours and deciding what to do with it; this one ends with
/// their own document changed and nothing of ours left on screen. It is for the case where the
/// answer is not something to read but something to write — a message being typed in another
/// language, a line in a document that has to end up translated.
///
/// Built out of the pieces 取詞翻譯 already proved: the selection is read by borrowing the copy
/// shortcut, and it is replaced by borrowing the paste one. See <see cref="SelectedTextReader"/> and
/// <see cref="SelectionReplacer"/> for why that is the only mechanism available.
///
/// Nothing at all happens when there is no selection, and deliberately nothing is said about it: the
/// user pressed a shortcut that acts on selected text without selecting any, and an error card over
/// their screen would make a non-event into an interruption. The log carries it instead, because the
/// other reason for an empty read is that the application refused the copy — which is a bug report
/// waiting to be made.
///
/// QuickTranslate, here and in the folder, the hint window, the settings and the S.QuickTranslate
/// string keys, is the name this feature had before it became 替換翻譯 — see
/// <see cref="QuickTranslateSettings"/> for why the code was not renamed with it.
/// </remarks>
internal static class QuickTranslateFlow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Monotonic id, so a slow translation cannot paste over a newer one's work.
    /// </summary>
    /// <remarks>
    /// Pressing the shortcut again means "this selection now". The run it interrupted is still
    /// waiting on a translation service, and left to finish it would paste its answer into whatever
    /// the user has selected since.
    /// </remarks>
    private static int _seq;

    /// <summary>Reads the selection, translates it, and pastes the result over it.</summary>
    public static async Task RunAsync()
    {
        var seq = ++_seq;

        // Before anything is shown. Putting a window on the screen takes the foreground away from
        // the application holding the selection, and the copy would then be sent to our own card.
        var source = await SelectedTextReader.ReadAsync();
        var foreground = GetForegroundWindow();

        if (source.Length == 0)
        {
            Log.Info("替換翻譯 did nothing: no selected text came back from the foreground window");
            return;
        }

        // Debug rather than Info: this is the user's own text, out of whatever they were reading or
        // writing. It belongs in a log only when they have turned 詳細資訊 on to report a problem.
        Log.Debug("替換翻譯 is translating a selection of {Length} characters: {Text}",
            source.Length, source);

        if (seq != _seq) return;

        var hint = QuickTranslateHintWindow.Summon();
        var settings = SettingsService.Instance.Current;

        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            hint.ReportFailure(LocalizationService.Get("S.Translation.MissingApiKey"));
            return;
        }

        try
        {
            var (results, _) = await AppServices.Translation.TranslateAsync(
                [new OcrTextBlock(source, new Rect())],
                LanguageData.GetValidSourceCode(settings.QuickTranslate.SourceLanguage),
                LanguageData.GetValidTargetCode(settings.QuickTranslate.TargetLanguage),
                settings.ApiKey);

            if (seq != _seq) return;

            var translation = results.FirstOrDefault()?.TranslatedText ?? "";
            if (translation.Length == 0)
            {
                // A provider that answers with nothing rather than failing. Pasting the empty string
                // would delete the user's selection, which is the one outcome worse than not working.
                Log.Info("替換翻譯 did not paste: the translation came back empty");
                hint.ReportFailure(LocalizationService.Get("S.QuickTranslate.EmptyResult"));
                return;
            }

            Log.Debug("替換翻譯 is pasting: {Text}", translation);

            var outcome = await SelectionReplacer.ReplaceSelectionAsync(translation, foreground);
            if (seq != _seq) return;

            switch (outcome)
            {
                case PasteOutcome.Pasted:
                    hint.ReportSuccess();
                    break;
                case PasteOutcome.FocusMoved:
                    hint.ReportFailure(LocalizationService.Get("S.QuickTranslate.FocusMoved"));
                    break;
                default:
                    hint.ReportFailure(LocalizationService.Get("S.QuickTranslate.ClipboardBusy"));
                    break;
            }
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;

            Log.Warn(ex, "替換翻譯 could not translate");
            hint.ReportFailure(LocalizationService.Format(
                "S.Translation.ProviderUnavailable",
                LanguageData.GetProviderDisplay(settings.Provider),
                ex.Message));
        }
    }

    /// <summary>
    /// The window the selection belongs to, so the paste can refuse to go anywhere else — see
    /// <see cref="SelectionReplacer.ReplaceSelectionAsync"/>.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
