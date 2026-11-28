using OverTranslate.Services;

namespace OverTranslate.Views.Capture;

/// <summary>
/// Which of the bar's buttons the label card names, and what it says about each — the card itself
/// is <see cref="Controls.ToolbarLabel"/>.
/// </summary>
/// <remarks>
/// The card grows away from the selection — under the bar when the bar is under the selection, over
/// it when the bar sits above — so it never lands on the text being read.
/// </remarks>
public partial class ToolbarWindow
{
    // Set where the bar is placed: whether it ended up above the selection.
    private bool _labelsPreferAbove;

    private void InitializeLabels()
    {
        Labels.Attach(this, BarSurface);
        Labels.PrefersAbove = () => _labelsPreferAbove;

        Labels.Add(TtsBtn, () => LocalizationService.Get(_isSpeaking ? "S.Toolbar.SpeakStopLabel" : "S.Toolbar.Speak"));
        Labels.Add(SwapBtn, () => LocalizationService.Get("S.Toolbar.Swap"));
        // The name is on the half that is not chosen and gone from the one that is, so the card is
        // where the chosen half says what it is — and the hints the picker's list used to carry.
        Labels.Add(HorizontalSeg,
            () => LocalizationService.Get("S.Toolbar.DirectionHorizontal"),
            () => LocalizationService.Get("S.Toolbar.DirectionHorizontalHint"));
        Labels.Add(VerticalSeg,
            () => LocalizationService.Get("S.Toolbar.DirectionVertical"),
            () => LocalizationService.Get("S.Toolbar.DirectionVerticalHint"));
        Labels.Add(EditSelectionBtn,
            () => LocalizationService.Get("S.Toolbar.EditSelection"),
            () => LocalizationService.Get("S.Toolbar.EditSelectionHint"));
        Labels.Add(ToggleBtn,
            () => LocalizationService.Get(_bubblesVisible ? "S.Toolbar.ShowSource" : "S.Toolbar.ShowTranslation"),
            () => LocalizationService.Get(_bubblesVisible ? "S.Toolbar.ShowSourceHint" : "S.Toolbar.ShowTranslationHint"));
        Labels.Add(CopyTextBtn,
            () => LocalizationService.Get(CopiesTranslation() ? "S.Toolbar.CopyTranslation" : "S.Toolbar.CopyText"),
            () => LocalizationService.Get(CopiesTranslation() ? "S.Toolbar.CopyTranslationHint" : "S.Toolbar.CopyTextHint"));
        Labels.Add(AnnotateBtn, () => LocalizationService.Get("S.Toolbar.Annotate"));
        // Ctrl+C on the card while Ctrl+C 複製截圖 is on (#445): the one place that says the key now
        // takes a screenshot, so whoever turned it on and forgot is told where they are looking.
        // Off, no line at all — not 未設定, which would offer a shortcut nobody asked for. Read at each
        // showing, so the switch flipped in the menu is what the next card says.
        Labels.Add(CopyShotBtn,
            () => LocalizationService.Get("S.Toolbar.Screenshot"),
            () => LocalizationService.Get("S.Toolbar.ScreenshotHint"),
            () => SettingsService.Instance.Current.Capture.CopyOnCtrlC ? CopyShortcutKeys : null);
        Labels.Add(OpenWindowBtn, () => LocalizationService.Get("S.Toolbar.OpenWindow"));
        Labels.Add(MoreBtn,
            () => LocalizationService.Get("S.Shell.Settings"),
            () => LocalizationService.Get("S.Toolbar.SettingsHint"));
        Labels.Add(CloseBtn, () => LocalizationService.Get("S.Toolbar.Close"));
    }

    private static readonly string[] CopyShortcutKeys = ["Ctrl", "C"];

    private bool CopiesTranslation() =>
        ResolveCopyTextKind(_hasTranslated, _bubblesVisible) == CopyTextKind.Translation;
}
