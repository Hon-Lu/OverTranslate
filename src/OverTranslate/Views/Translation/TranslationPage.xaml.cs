using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Translation;

public partial class TranslationPage : UserControl
{
    private const double PreferredDictionaryHeight = 290;
    private const double MinimumTranslatedResultHeight = 96;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly TranslationService _translationService = new();
    private readonly TtsService _tts = new();

    // Auto-translate: typing/edits restart this timer; it fires one translation once the user pauses.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private readonly DispatcherTimer _debounce;

    // True while we set the text/selectors programmatically, so those changes don't auto-translate.
    private bool _suppressAuto;
    // True while shared settings are being reflected into the selectors, so their change events do
    // not write the same values back to disk one field at a time.
    private bool _reloadingPreferences;
    // Monotonic id so a slow in-flight translation can't overwrite the result of a newer one.
    private int _seq;
    // Last input that translated successfully — lets us skip redundant identical re-translations.
    private (string Text, string Src, string Tgt, TranslationProvider Provider)? _lastDone;

    // The TTS button currently driving playback (so a second click stops instead of replaying).
    private Button? _ttsActiveBtn;

    /// <summary>
    /// The language the engine said the source was in, or empty.
    /// </summary>
    /// <remarks>
    /// 自動 is the default source language and the one most people never change, so without this the
    /// page does not know what it is looking at most of the time — which is why 朗讀原文 used to be
    /// switched off outright there. Every engine but one answers the question as a by-product of
    /// translating, and its answer is a better one than the picker can give.
    /// </remarks>
    private string _detectedLang = "";

    /// <summary>True while the engine and source language are the pair that has no voice coming.</summary>
    /// <remarks>
    /// Kept rather than recomputed, because the footer is settled from two directions: this is
    /// decided by <see cref="RenderSourceActions"/>, and the line it has to share is written by
    /// <see cref="SetStatus"/> and <see cref="SetTranslating"/>, neither of which has any business
    /// working out which engine is selected.
    /// </remarks>
    private bool _srcTtsBlocked;

    public TranslationPage()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateNowAsync(); };

        var settings = SettingsService.Instance.Current;

        _suppressAuto = true;
        InitializeSelectors(settings.SourceLanguage, settings.TargetLanguage);
        _suppressAuto = false;

        _tts.StateChanged += OnTtsStateChanged;

        // Attach after initial values are set so initialization doesn't save or auto-translate
        SourceTextBox.TextChanged    += SourceTextBox_TextChanged;
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;

        RenderSourceActions();
    }

    /// <summary>
    /// Settles the two controls in the 原文 header against what is actually there to act on.
    /// </summary>
    /// <remarks>
    /// <para><b>Clear</b> exists only while there is text. It sits inboard of the speaker rather
    /// than outboard of it so that appearing and disappearing never shifts the speaker, which would
    /// otherwise move under the pointer of anyone reaching for it — and so the two panes' speakers
    /// stay on the same line across the divider.</para>
    ///
    /// <para><b>Speak</b> needs a language to read in, because there is no such thing as an
    /// automatic voice: <see cref="TtsService"/> maps 自動 onto Chinese, so English or Japanese
    /// source text would be read aloud in a Chinese voice. That used to happen silently, and the
    /// user was left to work out from the sound that a language picker three controls away was the
    /// cause.</para>
    ///
    /// <para>It used to be refused for the whole of 自動, which is the default and the setting most
    /// people never touch — so the button was off almost always, including for everyone whose engine
    /// had already said what the text was. It is the answer that decides now, not the picker:
    /// <see cref="EffectiveSourceLanguage"/>. What is left refused is the one case where no answer
    /// is ever coming, and the note names it rather than making anyone wait to find out.</para>
    ///
    /// <para>Not extended to "no text yet", which would also be a button that can do nothing: that
    /// state lasts a keystroke, and a speaker that greys out every time the box empties reads as
    /// flicker rather than as information.</para>
    /// </remarks>
    private void RenderSourceActions()
    {
        SrcClearBtn.Visibility = SourceTextBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The one state that never resolves, and the only one left that has to be refused outright.
        // Every other engine says what language the original was in as a by-product of translating,
        // so 自動 is a state the next result gets the page out of; an OpenAI-compatible server is
        // never asked, and cannot be — the prompt it is sent belongs to the user, so the reply
        // cannot be made to carry a second field. Waiting is not going to help there.
        var openAiAutomatic =
            SettingsService.Instance.Current.Provider == TranslationProvider.OpenAI &&
            LanguageData.IsAutomaticSource(SrcLangBox.SelectedValue as string);

        var available = !openAiAutomatic && EffectiveSourceLanguage().Length > 0;

        // Losing the language mid-sentence would otherwise leave the source playing with no way to
        // stop it, since the button that stops it is the one being disabled.
        if (!available && ReferenceEquals(_ttsActiveBtn, SrcTtsBtn)) _tts.Stop();

        SrcTtsBtn.IsEnabled = available;

        // The note at the foot of the page says the same sentence the greyed speaker's tooltip does.
        // A tooltip alone would have been found only by someone who already suspected there was
        // something to find, which is the wrong bar for the one explanation that exists.
        //
        // Only for the state that stays: an engine that simply has not answered yet is a second or
        // two, and a note appearing and going again on its own is noise rather than information.
        _srcTtsBlocked = openAiAutomatic;
        RenderFooter();

        // Read through the service rather than bound with DynamicResource, because which string
        // applies is a state and not a constant. Re-read on Reload, which is where a change of
        // interface language arrives.
        SrcTtsBtn.ToolTip = LocalizationService.Get(
            available ? "S.Translation.SpeakSource"
            : openAiAutomatic ? "S.Translation.SpeakSourceOpenAi"
            : "S.Translation.SpeakSourceUnknown");
    }

    /// <summary>
    /// What language the source text is actually in, or empty when nobody knows yet.
    /// </summary>
    /// <remarks>
    /// The picker's own value answers this except when it says 自動, and that is where
    /// <see cref="_detectedLang"/> takes over. The two callers are the two places 自動 is not an
    /// answer they can use: 朗讀原文 has to pick a voice, and <see cref="SwapBtn_Click"/> has to put
    /// something on the target side.
    /// </remarks>
    /// <inheritdoc cref="_detectedLang"/>
    private string EffectiveSourceLanguage()
    {
        var chosen = SrcLangBox.SelectedValue as string;
        if (!LanguageData.IsAutomaticSource(chosen)) return LanguageData.GetValidSourceCode(chosen);
        return _detectedLang;
    }

    /// <remarks>
    /// Cleared through the selection rather than by assigning Text, so it lands on the TextBox's
    /// own undo stack and Ctrl+Z puts the text back. Clearing is the one action on this page that
    /// destroys something the user typed, and a slip has to cost nothing — which is cheaper than a
    /// confirmation dialog and, unlike one, does not train people to click through.
    /// </remarks>
    private void SrcClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SourceTextBox.SelectAll();
        SourceTextBox.SelectedText = "";

        // They cleared it in order to type something else; leaving focus on a button that has just
        // vanished would make them click into the box first.
        SourceTextBox.Focus();
    }

    /// <summary>
    /// Re-reads the shared translation preferences after another page changes them, and the
    /// interface language along with them.
    /// </summary>
    public void Reload()
    {
        var settings = SettingsService.Instance.Current;
        var sourceLanguage = LanguageData.GetValidSourceCode(settings.SourceLanguage);
        var targetLanguage = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        var provider = settings.Provider;
        var selectionChanged =
            !Equals(SrcLangBox.SelectedValue, sourceLanguage) ||
            !Equals(TgtLangBox.SelectedValue, targetLanguage) ||
            !Equals(ProviderBox.SelectedValue, provider);

        _suppressAuto = true;
        _reloadingPreferences = true;
        try
        {
            // Rebound, not just re-selected. The item labels are read per draw but the containers
            // holding them are not rebuilt on their own, so a page that only re-selects goes on
            // showing the language it was built in — and this page, unlike its siblings, is kept
            // alive by the shell and reached again rather than recreated, so "built in" meant
            // "since the window opened". See LocalizationService.BindLocalizedItems.
            //
            // Inside the guarded block because clearing ItemsSource drops the selection, which
            // reaches the handlers as a change to nothing and would otherwise be saved as one.
            LocalizationService.BindLocalizedItems(SrcLangBox,  LanguageData.SourceLanguages);
            LocalizationService.BindLocalizedItems(TgtLangBox,  LanguageData.TargetLanguages);
            LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

            SrcLangBox.SelectedValue = sourceLanguage;
            TgtLangBox.SelectedValue = targetLanguage;
            ProviderBox.SelectedValue = provider;
            if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        }
        finally
        {
            _reloadingPreferences = false;
            _suppressAuto = false;
        }

        // Also where a change of interface language lands, which is what re-reads the speaker's
        // tooltip in the new language.
        RenderSourceActions();

        if (selectionChanged)
            RequestTranslate();
    }

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderSourceActions();
        RequestTranslate();
    }

    private void SrcLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingPreferences) SaveCurrentLanguageSelection();

        // The engine was asked about a pair that is no longer the one on screen.
        _detectedLang = "";
        RenderSourceActions();
        RequestTranslate();
    }

    // The target says nothing about what the source text is, so the detected language survives it.
    // Clearing it here would grey 朗讀原文 out and back for every change of target under 自動, which
    // is flicker rather than information — the same reason "no text yet" is not a state either.
    private void TgtLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingPreferences) SaveCurrentLanguageSelection();
        RequestTranslate();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        if (!_reloadingPreferences) SaveProviderSelection(provider);

        // The answer belongs to the engine that gave it, and the new one may not answer at all —
        // which is the whole of what RenderSourceActions has to settle here.
        _detectedLang = "";
        RenderSourceActions();
        RequestTranslate();
    }

    /// <summary>
    /// Schedules a debounced auto-translation. Programmatic edits are ignored — the screenshot flow
    /// fills both panes itself and then asks for the one translation it wants, see
    /// <see cref="SetContent"/> — and an empty source instantly clears the output.
    /// </summary>
    private void RequestTranslate()
    {
        if (_suppressAuto) return;

        _debounce.Stop();
        ClearDictionaryResult();
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _seq++;               // cancel any in-flight result
            TranslatedTextBox.Text = "";
            _lastDone = null;
            SetTranslating(false);
            SetStatus("", isError: false);
            ShowRetry(false);
            return;
        }
        _debounce.Start();
    }

    /// <remarks>
    /// 自動 cannot move to the target side — there is no such thing as translating into "whatever" —
    /// and it is the source language most of the time, so read through
    /// <see cref="EffectiveSourceLanguage"/>: the engine has already said what the original was, and
    /// that answer is what goes across. Without it this button read the picker's literal 自動, found
    /// nothing to map, and fell to the first entry in the list, which is right for English text and
    /// wrong for everything else.
    /// </remarks>
    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        // Before anything moves: the detected language describes the text that is about to stop
        // being the original, and the picker changes below clear it.
        var srcVal = EffectiveSourceLanguage();
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Swap texts programmatically (suppressed); the language changes below trigger one re-translate
        _suppressAuto = true;
        (SourceTextBox.Text, TranslatedTextBox.Text) = (TranslatedTextBox.Text, SourceTextBox.Text);
        _suppressAuto = false;

        // Swap target → source using explicit language mapping
        if (tgtVal != null)
        {
            var sourceCode = LanguageData.MapTargetToSourceCode(tgtVal);
            SrcLangBox.SelectedValue = sourceCode;
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Swap source → target. With 自動 and nothing detected — an empty box, or an engine that has
        // not answered — there is nothing to carry across, and English is the fallback: it is what
        // most of what people paste in is written in, and it is one click to correct.
        TgtLangBox.SelectedValue = LanguageData.MapSourceToTargetCode(
            srcVal.Length > 0 ? srcVal : "EN");
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;

        RequestTranslate(); // ensure the swapped direction is translated even if a language was unchanged
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e)
    {
        _lastDone = null;            // force a real re-translation even if input is unchanged
        _debounce.Stop();
        _ = TranslateNowAsync();
    }

    /// <summary>
    /// Translates the current source text with the chosen engine only (no hedge/fallback, per the
    /// manual page's design): a timeout/failure shows an error + retry rather than switching engines.
    /// Guarded by a sequence id so a stale result never overwrites a newer one.
    /// </summary>
    private async Task TranslateNowAsync()
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var apiKey = SettingsService.Instance.Current.ApiKey;
        if (_translationService.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus(LocalizationService.Get("S.Translation.MissingApiKey"), isError: true);
            ShowRetry(false);
            return;
        }

        var srcLang  = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        var tgtLang  = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        var provider = SettingsService.Instance.Current.Provider;

        var key = (text, srcLang, tgtLang, provider);
        if (_lastDone == key) return;   // identical to the last successful translation — skip

        var seq = ++_seq;
        ShowRetry(false);
        SetTranslating(true);

        try
        {
            var block = new OcrTextBlock(text, new Rect());
            var (results, detected) = await _translationService.TranslateAsync([block], srcLang, tgtLang, apiKey, resilient: false);
            if (seq != _seq) return;    // a newer request superseded this one — let it own the UI

            _detectedLang = detected ?? "";
            TranslatedTextBox.Text = results.FirstOrDefault()?.TranslatedText ?? "";
            _lastDone = key;
            SetTranslating(false);
            SetStatus("", isError: false);

            // The answer is what 朗讀原文 was waiting on under 自動, so the speaker comes back here
            // rather than on the next thing the user happens to touch.
            RenderSourceActions();

            var dictionarySource = LanguageData.IsAutomaticSource(srcLang) ? _detectedLang : srcLang;
            await LoadDictionaryAsync(seq, text, dictionarySource, tgtLang, provider);
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;
            SetTranslating(false);

            // This page sends to the chosen engine only (resilient: false), so any failure lands
            // here verbatim — and the free endpoints fail in ways nobody documents (an answer that
            // is not JSON, a 429 that is really a length limit). OverTranslate.Engines names the
            // engine and the kind of failure, never the text. Catch everything and lead with
            // a line that says what the user can actually do; keep the original text underneath
            // so the cause is still reportable.
            SetStatus(
                LocalizationService.Format(
                    "S.Translation.ProviderUnavailable",
                    LanguageData.GetProviderDisplay(provider), ex.Message),
                isError: true);
            ShowRetry(true);
        }
    }

    private async Task LoadDictionaryAsync(
        int seq, string text, string sourceLang, string targetLang, TranslationProvider provider)
    {
        if (sourceLang.Length == 0 || !DictionaryLookupEligibility.IsEligible(text)) return;

        DictionaryView.Clear();
        DictionaryLoadingBar.Visibility = Visibility.Visible;
        DictionaryHost.Visibility = Visibility.Visible;

        try
        {
            var result = await _translationService.LookupDictionaryAsync(
                text, sourceLang, targetLang, engine: provider);
            if (seq != _seq) return;

            DictionaryLoadingBar.Visibility = Visibility.Collapsed;
            DictionaryView.Show(result);
            if (result?.HasContent != true)
                DictionaryView.ShowMessage("S.Dictionary.NoResult");
            DictionaryHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Rich data is an enhancement to a translation that already succeeded. A dictionary
            // endpoint having a bad minute must not replace that answer with an error state.
            Log.Debug(ex, "Dictionary lookup did not complete");
            if (seq == _seq)
            {
                DictionaryLoadingBar.Visibility = Visibility.Collapsed;
                DictionaryView.ShowMessage("S.Dictionary.Unavailable");
                DictionaryHost.Visibility = Visibility.Visible;
            }
        }
    }

    private void ClearDictionaryResult()
    {
        DictionaryLoadingBar.Visibility = Visibility.Collapsed;
        DictionaryView.Clear();
        DictionaryHost.Visibility = Visibility.Collapsed;
    }

    private void TranslationResultLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        => DictionaryScroller.MaxHeight = CalculateDictionaryMaxHeight(e.NewSize.Height);

    internal static double CalculateDictionaryMaxHeight(double availableHeight)
        => Math.Min(
            PreferredDictionaryHeight,
            Math.Max(0, availableHeight - MinimumTranslatedResultHeight));

    // Toggles the in-flight indicator: an indeterminate bar over the output plus an accent status line,
    // so "translating" is obvious where the user is looking (the 譯文 panel), not just a grey footer note.
    private void SetTranslating(bool on)
    {
        TranslatingBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            StatusText.Text       = LocalizationService.Get("S.Translation.Translating");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppAccent");

            // 翻譯中 has taken the footer, so whatever was sharing it steps aside. Only on the way
            // in: switching off leaves the text for the caller to clear or replace, which is where
            // the note gets its line back.
            RenderFooter();
        }
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(SrcTtsBtn, SourceTextBox.Text, EffectiveSourceLanguage());

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(TgtTtsBtn, TranslatedTextBox.Text,
                                LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string));

    // Click the same button while it's speaking → stop. Click the other → switch playback to it.
    private async Task ToggleTtsAsync(Button btn, string text, string lang)
    {
        if (_tts.IsActive && _ttsActiveBtn == btn) { _tts.Stop(); return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        _ttsActiveBtn = btn;
        UpdateTtsIcons();           // show ⏹ immediately on click (don't wait for the fetch)
        try { await _tts.SpeakAsync(text, lang); }
        catch (Exception ex) { SetStatus(LocalizationService.Format("S.Translation.SpeakFailed", ex.Message), isError: true); }
    }

    // StateChanged only needs to handle "stopped/ended/failed" — start is reflected on click.
    private void OnTtsStateChanged(object? sender, EventArgs e)
    {
        if (!_tts.IsActive) { _ttsActiveBtn = null; UpdateTtsIcons(); }
    }

    /// <summary>Speaker when idle, stop while this button is the one playing.</summary>
    /// <remarks>
    /// Icon-font glyphs rather than the emoji these used to be, so that they share a vertical axis
    /// with the clear button beside them — see the comment on the 原文 header in the XAML.
    /// </remarks>
    private const string SpeakerGlyph = Views.Controls.TtsGlyphs.Speak;
    private const string StopGlyph = Views.Controls.TtsGlyphs.Stop;

    private void UpdateTtsIcons()
    {
        SrcTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, SrcTtsBtn) ? StopGlyph : SpeakerGlyph;
        TgtTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, TgtTtsBtn) ? StopGlyph : SpeakerGlyph;
    }

    /// <summary>
    /// Carries a screenshot result into the page and re-translates it once.
    /// </summary>
    /// <remarks>
    /// <para>Both panes are filled from the capture before anything is sent, so the window never
    /// opens empty: the translation that came with the result is what there is to read while the new
    /// one is on its way, and it is replaced in place when the answer arrives.</para>
    ///
    /// <para>Re-translated rather than shown as-is, because the two flows are not asking the same
    /// question. The overlay translates each OCR block on its own — it has to, since every answer is
    /// painted back over the box it came from — and what arrives here is those per-block answers
    /// glued together: sentences cut in half at a line end, and mixed-language lines each judged with
    /// only their own fragment for context. Sending the joined text as one request is the whole
    /// reason somebody opens the window after reading the overlay.</para>
    /// </remarks>
    public void SetContent(string sourceText, string translatedText, string srcLang, string tgtLang)
    {
        _suppressAuto = true;
        _debounce.Stop();
        _seq++;

        SourceTextBox.Text     = sourceText;
        TranslatedTextBox.Text = translatedText;
        SetTranslating(false);
        SetStatus("", isError: false);
        ShowRetry(false);

        SrcLangBox.SelectedValue = LanguageData.GetValidSourceCode(srcLang);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(tgtLang);

        // The carried translation is not one this page produced, so it does not count as one:
        // leaving it here would make the request below the "identical input" case and skip it.
        _lastDone = null;
        _suppressAuto = false;

        RenderSourceActions();

        // Straight to the request rather than through the debounce: nobody is typing, and the wait
        // would only hold back the answer the window was opened for.
        _ = TranslateNowAsync();
    }

    private void ShowRetry(bool visible)
        => RetryBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Releases the debounce timer and TTS playback when the shell window is destroyed.</summary>
    public void Teardown()
    {
        _debounce.Stop();
        _tts.Dispose();
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text       = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");

        RenderFooter();
    }

    /// <summary>
    /// Settles the single line at the foot of the page, which two things want.
    /// </summary>
    /// <remarks>
    /// The status text wins whenever it has anything to say. It is the answer to what the user just
    /// did — a translation running, an engine that failed — and it is gone again a moment later,
    /// while the note is a standing condition that will still be true once it goes. Covering the
    /// note for those few seconds costs nothing; making the reader find the status somewhere else,
    /// or read the two side by side, costs them the thing they were waiting for.
    ///
    /// One line rather than two rows, because the status line is empty almost all of the time. A row
    /// of its own would have the page grow the moment somebody picks OpenAI and shuffle again on
    /// every translation, which is movement in return for nothing.
    /// </remarks>
    private void RenderFooter()
    {
        SrcTtsNote.Visibility = _srcTtsBlocked && StatusText.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void InitializeSelectors(string sourceLang, string targetLang)
    {
        LocalizationService.BindLocalizedItems(SrcLangBox,  LanguageData.SourceLanguages);
        LocalizationService.BindLocalizedItems(TgtLangBox,  LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

        SrcLangBox.SelectedValue  = LanguageData.GetValidSourceCode(sourceLang);
        TgtLangBox.SelectedValue  = LanguageData.GetValidTargetCode(targetLang);
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
    }

    private void SaveCurrentLanguageSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        settings.TargetLanguage = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        SettingsService.Instance.Save();
    }

    private static void SaveProviderSelection(TranslationProvider provider)
    {
        var settings = SettingsService.Instance.Current;
        settings.Provider = provider;
        SettingsService.Instance.Save();
    }
}
