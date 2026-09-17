using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Screen = System.Windows.Forms.Screen;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>One monitor, as offered in the screen picker.</summary>
public sealed record ScreenItem(
    string DeviceName, string Display, System.Drawing.Rectangle Bounds, bool IsPrimary)
    : ISearchableItem
{
    /// <summary>The label, which is the whole of what a monitor is known by here.</summary>
    public string SearchText => Display;
}

/// <summary>One open window, as offered in the source picker.</summary>
/// <remarks>
/// The handle is the value, and it is only good for as long as that window is open — which is why
/// the list is rebuilt on demand rather than kept, and why the controller checks the handle again
/// before it builds anything around it.
/// </remarks>
public sealed record WindowItem(IntPtr Hwnd, string Display, string Detail, string ProcessName)
    : ISearchableItem
{
    /// <summary>
    /// Title, detail line and process name together, because any of the three is a reasonable thing
    /// to look a window up by.
    /// </summary>
    /// <remarks>
    /// The process name especially: a browser tab's title is whatever page it happens to be on, and
    /// changes while the user is deciding, whereas "msedge" is what they would think to type. It is
    /// not shown in the list, which is exactly why it has to be listed here to be reachable.
    /// </remarks>
    public string SearchText => $"{Display} {Detail} {ProcessName}";
}

/// <summary>
/// Sets up a realtime session and then gets out of the way — everything after 選取翻譯區塊 happens on
/// the screen itself, driven by <see cref="RealtimeSessionController"/>.
/// </summary>
/// <remarks>
/// The page holds two kinds of value, and the line between them is the rule to keep:
///
/// <para><b>Parameters of one sitting</b> — capture source, screen and block count — are not read
/// from or written to the settings file. Reopening the window must not restore a screen that may
/// have been unplugged, or blocks that no longer frame the same content. The capture source is the
/// clearest case of the rule: 視窗擷取 is a live window handle, which does not survive the window
/// being closed, let alone the next launch, so storing the mode alone would reopen the page in a
/// state missing its own answer.</para>
///
/// <para><b>Translation preferences</b> — both languages and the service — are stored independently
/// from screenshot and text translation. Watching a game and translating a document are different
/// jobs with different latency and quality requirements, but choosing the same realtime pair and
/// service on every launch is needless repetition. The source language joined them late, and it is
/// the one with a cost: someone who watched Japanese yesterday and English today starts on the wrong
/// one, and realtime recognition gives no sign of a mismatch beyond reading badly. It is offered
/// back anyway because it is a language the user picked, not one guessed for them — 自動 is not in
/// this page's picker at all — and re-picking it every sitting is the repetition this group exists
/// to remove.</para>
///
/// <para><b>Appearance preferences</b> — the subtitle's two colours and how opaque its band is, in
/// 顯示外觀 — are stored and restored. A reader who needs yellow on dark blue needs it every session,
/// and re-picking it each time is the same failure as forgetting a theme. They live here rather than
/// in 設定 so the control sits beside what it changes.</para>
///
/// <para>Nothing on screen currently marks which card is which; 翻譯區塊 states that its value is not
/// kept, and 顯示外觀 says nothing either way. That is a deliberate trade for a shorter card, not an
/// oversight — if it turns out to confuse anyone, the fix is a line in 顯示外觀 saying its values are
/// kept, not removing the note from 翻譯區塊.</para>
///
/// <para><b>Kept for as long as this window is open</b> — the blocks the user drew, with the mode
/// chosen for each. Not a preference and not a parameter: it is the answer to "where is the text",
/// which is worth several minutes of dragging and is thrown away the moment the question changes.
/// It lives in <see cref="RealtimeSessionController"/> rather than here, and this page tells it to
/// forget in the two cases that change the question — the block count moving, and this window
/// closing.</para>
///
/// Anything added later belongs to one of these three groups; if it is not obvious which, it is a
/// preference only when the user would be annoyed to set it twice, and a parameter only when
/// offering it back could be wrong rather than merely stale.
/// </remarks>
public partial class RealtimePage : UserControl
{
    /// <summary>
    /// 繁體中文, because this page's own reason to exist is watching foreign content, and the reader
    /// is the person sitting here.
    /// </summary>
    private const string DefaultTargetLanguage = "ZH-HANT";

    /// <summary>
    /// Microsoft, chosen for this page rather than inherited: measured over 639 realtime passes it
    /// answered in 82ms at the median, which is what makes a subtitle appear to keep up. A service
    /// that reads more carefully but takes a second is the right default elsewhere and the wrong one
    /// here.
    /// </summary>
    private const TranslationProvider DefaultProvider = TranslationProvider.Microsoft;

    private const int MinBlocks = RealtimeSettings.MinBlockCount;
    private const int MaxBlocks = RealtimeSettings.MaxBlockCount;

    private int _blockCount = MinBlocks;

    private bool _windowsLoaded;

    // True while this page is filling the capture pickers from stored values rather than the user
    // choosing. Without it, restoring a monitor that has since been unplugged would write the
    // fallback straight back over the user's stored choice — the same failure the opacity slider's
    // flag exists to prevent, one card down.
    private bool _restoringSource;

    // True while this page is writing the opacity slider rather than the user, so that restoring a
    // stored value does not read as a change and write it straight back.
    private bool _syncingOpacity;

    // True between the thumb being picked up and put down. The preview follows every step of a drag;
    // the settings file waits for the end of it, because a drag across the track is one decision and
    // would otherwise be a hundred writes.
    private bool _draggingOpacity;

    // True while this page is putting the stored 顯示外觀 modes on their trays rather than the user
    // choosing one. Same failure as the two flags above, one control further on: checking a half
    // raises Checked, which would write back the value being restored.
    private bool _syncingAppearance;

    public RealtimePage()
    {
        InitializeComponent();

        BindPickers();

        ApplyPageDefaults();
        ApplyCaptureMode();

        // Attached after the initial value is set, so initialisation does not fire the handler
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;
        TgtLangBox.SelectionChanged += TgtLangBox_SelectionChanged;
        SrcLangBox.SelectionChanged += SrcLangBox_SelectionChanged;

        // The slider's own drag events, which it forwards from its thumb rather than surfacing as
        // properties of its own.
        ScrimOpacitySlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _draggingOpacity = true));
        ScrimOpacitySlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _draggingOpacity = false;
                PersistScrimOpacity();
            }));

        // Set before the handlers go on, for the reason the boxes above give: restoring a stored
        // value must not read as the user reaching for the switch and write it straight back.
        ShowAppearanceModes(SettingsService.Instance.Current.Realtime);
        TextColorAutoRadio.Checked += TextColorMode_Changed;
        TextColorFixedRadio.Checked += TextColorMode_Changed;
        ScrimColorAutoRadio.Checked += ScrimColorMode_Changed;
        ScrimColorFixedRadio.Checked += ScrimColorMode_Changed;
        BorderToggle.Checked += BorderToggle_Toggled;
        BorderToggle.Unchecked += BorderToggle_Toggled;
        BorderRandomRadio.Checked += BorderMode_Changed;
        BorderFixedRadio.Checked += BorderMode_Changed;

        SyncScrimOpacity();
        RenderColours();
        RenderPauseHint();

        RealtimeSessionController.Instance.StateChanged += OnSessionStateChanged;

        // Not just the stepper: the start button has to begin unavailable too, because 原文語言
        // starts unset and RenderState is what ties the two together.
        RenderState();
    }

    /// <summary>
    /// Applies this page's independent translation preferences and per-session defaults.
    /// </summary>
    /// <remarks>
    /// 原文語言 comes back only if the user has ever set it. Empty is the state that asks the question
    /// rather than answering it badly: realtime recognition gets one look at a frame and no retry, so
    /// there is no silent automatic mode to fall back on here — which is why 自動 is cleared rather
    /// than offered. <see cref="LanguageData.GetValidOcrSourceCode"/> answers 自動 for anything it
    /// cannot place, so that one branch covers a never-set value, a hand-edited one and a code
    /// retired since.
    /// </remarks>
    private void ApplyPageDefaults()
    {
        var settings = SettingsService.Instance.Current;
        // Never null any more: the picker used to be left blank so that only a language the user
        // chose was ever used, and the shortcut has no page on which to ask. Anything unusable —
        // blank from an older settings file, 自動 from a hand-edited one — resolves to the default.
        SrcLangBox.SelectedValue =
            LanguageData.GetValidRealtimeSourceCode(settings.Realtime.SourceLanguage);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(
            settings.Realtime.TargetLanguage);
        if (TgtLangBox.SelectedValue == null)
            TgtLangBox.SelectedValue = DefaultTargetLanguage;

        ProviderBox.SelectedValue = settings.Realtime.Provider;
        if (ProviderBox.SelectedValue == null)
            ProviderBox.SelectedValue = DefaultProvider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
        RenderProviderHint();

        // Clamped rather than trusted: this is a settings-file value and the stepper's range is the
        // one that has to hold. RenderBlockCount runs later, from Reload.
        _blockCount = Math.Clamp(settings.Realtime.BlockCount, MinBlocks, MaxBlocks);

        // Before the restore below, which needs to know whether the saved mode is one this machine
        // can still honour.
        ApplyCaptureCapability();

        // Restored, unlike the screen and the window it implies, because it is the one part of the
        // arrangement that is a habit rather than a fact about this sitting: someone who watches a
        // game watches a game. Set while _restoringSource holds, so the radio's own handler does not
        // read it as the user reaching for the control and write it straight back.
        _restoringSource = true;
        if (settings.Realtime.CaptureMode is RealtimeCaptureMode.Window
            || !WgcCapability.SupportsScreenMode)
            WindowModeRadio.IsChecked = true;
        else
            ScreenModeRadio.IsChecked = true;
        _restoringSource = false;
    }

    /// <summary>
    /// Offers only the capture modes this machine can actually run.
    /// </summary>
    /// <remarks>
    /// The answer is static — see <see cref="WgcCapability"/>, where it is read once at launch — so
    /// this is a fact about the machine settled before the user has chosen anything, and the honest
    /// place to spend it is here rather than at 開始翻譯. A mode that is offered, framed over, and
    /// then refused costs the user the whole set-up to learn something the program knew when the
    /// page opened.
    ///
    /// Disabled and explained rather than hidden. A tile that is simply missing reads as the user
    /// failing to find it — against a guide or a screenshot showing two — while a greyed one with
    /// the reason underneath answers the question they are about to ask. The reason goes in a notice
    /// across the bottom of the step rather than inside the tile, because a disabled tile dims its
    /// own text along with everything else in it.
    ///
    /// The restore above coerces a saved 螢幕擷取 to 視窗擷取 on these machines, and deliberately
    /// does not write that back: it runs under <see cref="_restoringSource"/>, so the preference
    /// survives in the settings file and comes back if the machine ever gains the API.
    /// </remarks>
    private void ApplyCaptureCapability()
    {
        var screen = WgcCapability.SupportsScreenMode;

        ScreenModeRadio.IsEnabled = screen;
        ScreenModeNotice.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;

        // Nothing to steer to when even window capture is missing: the feature does not exist on
        // this machine, which RenderState says once in the footer rather than twice in the tiles.
        WindowModeRadio.IsEnabled = WgcCapability.SupportsWindowMode;
    }

    /// <summary>
    /// Re-reads what may have changed while the user was elsewhere: the attached monitors, and
    /// whether a session is running.
    /// </summary>
    /// <summary>
    /// Fills the three language and provider pickers.
    /// </summary>
    /// <remarks>
    /// Re-run on every <see cref="Reload"/> rather than only at construction, because the item
    /// labels come from the string dictionary and the shell keeps this page for its lifetime —
    /// built once, it would still be showing the language the app started in. The selections are
    /// carried across by hand, because rebinding the items drops them — and dropping them here would
    /// read as this page forgetting what the user chose the moment they changed the interface
    /// language.
    /// </remarks>
    private void BindPickers()
    {
        var source   = SrcLangBox.SelectedValue;
        var target   = TgtLangBox.SelectedValue;
        var provider = ProviderBox.SelectedValue;

        LocalizationService.BindLocalizedItems(
            SrcLangBox,
            LanguageData.OcrSourceLanguages
                .Where(language => !LanguageData.IsAutomaticSource(language.Code))
                .ToList());
        LocalizationService.BindLocalizedItems(TgtLangBox,  LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);

        SrcLangBox.SelectedValue  = source;
        TgtLangBox.SelectedValue  = target;
        ProviderBox.SelectedValue = provider;
    }

    public void Reload()
    {
        BindPickers();
        LoadScreens();
        // Both halves: the mode's own explanation comes from the string dictionary, and the window
        // list is a snapshot of a desktop the user has been away from since it was taken.
        ApplyCaptureMode();
        if (_windowsLoaded) LoadWindows();
        SyncScrimOpacity();
        RenderColours();
        RenderPauseHint();
        RenderState();
    }

    /// <summary>
    /// Names the capture shortcut in 運作方式, or drops the line when there is none to name.
    /// </summary>
    /// <remarks>
    /// Re-read on every <see cref="Reload"/> rather than only at construction: 設定 is where that
    /// shortcut is changed, and the user reaches this page again through the same nav rail — so the
    /// line would otherwise go on naming the key they just replaced.
    /// </remarks>
    private void RenderPauseHint()
    {
        PauseHint.Text = RealtimePauseHint.ForSettingsPage(RealtimePauseHint.CurrentHotkey);
        PauseHint.Visibility = PauseHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Detaches from the controller when the shell window is destroyed.</summary>
    /// <remarks>
    /// The kept block layout goes with the window, which is the whole of its scope: it exists so
    /// that ending a session to change something on this page does not cost the user their blocks,
    /// and closing the window is the end of that errand. A session already running is left alone —
    /// it owns its own blocks, and closing this window has never been a request to stop it.
    /// </remarks>
    public void Teardown()
    {
        RealtimeSessionController.Instance.StateChanged -= OnSessionStateChanged;
        RealtimeSessionController.Instance.ForgetBlocks();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is TranslationProvider provider)
            SaveTranslationPreference(settings => settings.Realtime.Provider = provider);
        RenderProviderHint();
    }

    /// <remarks>
    /// Only a real selection is written, never the momentary null that rebinding the items produces
    /// — see <see cref="BindPickers"/>. Without that guard, changing the interface language would
    /// blank this preference on its way past.
    /// </remarks>
    private void SrcLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SrcLangBox.SelectedValue is string sourceLanguage)
            SaveTranslationPreference(settings =>
                settings.Realtime.SourceLanguage = LanguageData.GetValidOcrSourceCode(sourceLanguage));

        RenderState();
    }

    private void TgtLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TgtLangBox.SelectedValue is not string targetLanguage) return;
        SaveTranslationPreference(settings =>
            settings.Realtime.TargetLanguage = LanguageData.GetValidTargetCode(targetLanguage));
    }

    private static void SaveTranslationPreference(Action<AppSettings> apply)
    {
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
    }

    /// <summary>
    /// The service's own note, plus the one thing that would otherwise only surface as a failure
    /// mid-session: a key-based service with no key saved. Said here, before the screen is handed
    /// over, rather than as an error on a floating bar the user is no longer looking at.
    /// </summary>
    private void RenderProviderHint()
    {
        var item = ProviderBox.SelectedItem as ProviderItem;
        var missingKey = item?.RequiresApiKey == true &&
                         string.IsNullOrWhiteSpace(SettingsService.Instance.Current.ApiKey);

        ProviderHint.Text = missingKey
            ? LocalizationService.Get("S.Realtime.NoApiKey")
            : item?.Hint ?? "";
        ProviderHint.Foreground = missingKey
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }

    // ── 擷取來源 ───────────────────────────────────────────────────────────────

    /// <summary>Whether the user has asked for one window rather than the whole screen.</summary>
    private bool WindowMode => WindowModeRadio.IsChecked == true;

    private void CaptureModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the card exists.
        if (WindowPickerPanel is null) return;

        if (!_restoringSource)
            SaveTranslationPreference(settings => settings.Realtime.CaptureMode =
                WindowMode ? RealtimeCaptureMode.Window : RealtimeCaptureMode.Screen);

        ApplyCaptureMode();
    }

    /// <summary>
    /// Shows the half of the card the chosen mode needs, and hides the screen picker when it is no
    /// longer the user's to answer.
    /// </summary>
    /// <remarks>
    /// The window list is built the first time 視窗擷取 is chosen rather than when the page loads:
    /// enumerating every open window costs a pass over the desktop, and the common case — a user who
    /// wants the whole screen — never needs it. After that it is only rebuilt on request, because a
    /// list that reshuffles itself while the user is reading it is worse than one that is a few
    /// seconds old.
    /// </remarks>
    private void ApplyCaptureMode()
    {
        var window = WindowMode;

        WindowPickerPanel.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        ScreenPanel.Visibility = window ? Visibility.Collapsed : Visibility.Visible;

        if (!window || _windowsLoaded) return;

        _windowsLoaded = true;
        LoadWindows();
    }

    private void RefreshWindowsBtn_Click(object sender, RoutedEventArgs e) => LoadWindows();

    /// <summary>
    /// Asks Windows what is open, keeping the user's pick if it is still there.
    /// </summary>
    private void LoadWindows()
    {
        var previous = (WindowBox.SelectedItem as WindowItem)?.Hwnd ?? IntPtr.Zero;

        var found = CaptureWindowList.Enumerate();
        var items = found
            .Select(window => new WindowItem(
                window.Hwnd,
                window.Title,
                window.ProcessName.Length > 0
                    ? LocalizationService.Format("S.Realtime.CaptureWindowDetail", window.ProcessName)
                    : "",
                window.ProcessName))
            .ToList();

        WindowBox.ItemsSource = items;

        _restoringSource = true;
        if (previous != IntPtr.Zero)
        {
            // Refreshing the list, not arriving at it. Matched by handle: the same window is the
            // same window whatever it has retitled itself to since.
            WindowBox.SelectedItem = items.FirstOrDefault(item => item.Hwnd == previous);
        }
        else
        {
            // First look this sitting, so the stored choice gets to speak. Nothing is written back
            // when it fails to match — the user is left on 請選擇視窗 with their stored window still
            // remembered, and only their next pick replaces it. A game that is not running yet is
            // not a decision to forget it.
            var realtime = SettingsService.Instance.Current.Realtime;
            var stored = CaptureWindowList.FindStored(
                found, realtime.CaptureWindowProcess, realtime.CaptureWindowTitle);

            WindowBox.SelectedItem = stored is { } match
                ? items.FirstOrDefault(item => item.Hwnd == match.Hwnd)
                : null;
        }
        _restoringSource = false;

        RenderWindowChoice(items.Count);
    }

    private void WindowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowHint is null) return;

        if (!_restoringSource && WindowBox.SelectedItem is WindowItem picked)
        {
            SaveTranslationPreference(settings =>
            {
                settings.Realtime.CaptureWindowProcess = ProcessNameOf(picked.Hwnd);
                settings.Realtime.CaptureWindowTitle = picked.Display;
            });
        }

        RenderWindowChoice((WindowBox.ItemsSource as IReadOnlyList<WindowItem>)?.Count ?? 0);
    }

    /// <summary>
    /// The application a listed window belongs to, taken from the list rather than asked again.
    /// </summary>
    private string ProcessNameOf(IntPtr hwnd) =>
        (WindowBox.ItemsSource as IReadOnlyList<WindowItem>)
            ?.FirstOrDefault(item => item.Hwnd == hwnd)?.ProcessName ?? "";

    /// <summary>
    /// The line under the picker: which monitor the framing will happen on, or why there is nothing
    /// to choose.
    /// </summary>
    /// <remarks>
    /// Naming the monitor matters more here than it looks. The screen picker is gone in this mode,
    /// so the one thing the user cannot otherwise tell — which screen is about to be covered by a
    /// full-screen framing layer — has to be said out loud, and it is decided by a window they may
    /// have dragged somewhere since they last looked.
    /// </remarks>
    /// <summary>Shows the prompt only while the field is both unanswered and not being answered.</summary>
    /// <remarks>
    /// The prompt is a TextBlock laid over the picker rather than anything the picker owns, so it
    /// knows nothing about the picker being open — and the open picker is a search field the user is
    /// typing into. Left alone the prompt sits on top of what they type.
    /// </remarks>
    private void SyncWindowPlaceholder() =>
        WindowPlaceholder.Visibility =
            WindowBox.SelectedItem is null && !WindowBox.IsDropDownOpen
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void WindowBox_DropDownStateChanged(object sender, EventArgs e) => SyncWindowPlaceholder();

    private void RenderWindowChoice(int count)
    {
        SyncWindowPlaceholder();

        if (count == 0)
        {
            WindowHint.Text = LocalizationService.Get("S.Realtime.CaptureWindowNone");
            return;
        }

        if (WindowBox.SelectedItem is not WindowItem item)
        {
            WindowHint.Text = LocalizationService.Get("S.Realtime.CaptureWindowHint");
            return;
        }

        if (ScreenOf(item.Hwnd) is not { } screen)
        {
            // Closed while the list was sitting open. Said here rather than saved for 開始翻譯,
            // because the user is looking at this line right now.
            WindowHint.Text = LocalizationService.Get("S.Realtime.WindowCaptureGone");
            return;
        }

        var line = LocalizationService.Format("S.Realtime.CaptureWindowOnScreen", ScreenLabel(screen));
        WindowHint.Text = item.Detail.Length > 0 ? $"{line} {item.Detail}" : line;
    }

    /// <summary>The monitor a window sits on, or null when it has gone since the list was built.</summary>
    private static Screen? ScreenOf(IntPtr hwnd)
    {
        try
        {
            return CaptureWindowList.StillAvailable(hwnd) ? Screen.FromHandle(hwnd) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The same wording the screen picker uses, so the two name a monitor identically.</summary>
    private static string ScreenLabel(Screen screen)
    {
        var screens = Screen.AllScreens;
        var index = Array.FindIndex(screens, candidate => candidate.DeviceName == screen.DeviceName);

        return LocalizationService.Format(
            screen.Primary ? "S.Realtime.ScreenItemPrimary" : "S.Realtime.ScreenItem",
            index + 1, screen.Bounds.Width, screen.Bounds.Height);
    }

    private void LoadScreens()
    {
        var stored = SettingsService.Instance.Current.Realtime.CaptureScreenDeviceName;
        var previous = ScreenBox.SelectedValue as string ?? (stored.Length > 0 ? stored : null);

        var screens = Screen.AllScreens;
        var items = screens
            .Select((screen, index) => new ScreenItem(
                screen.DeviceName,
                LocalizationService.Format(
                    screen.Primary ? "S.Realtime.ScreenItemPrimary" : "S.Realtime.ScreenItem",
                    index + 1, screen.Bounds.Width, screen.Bounds.Height),
                screen.Bounds,
                screen.Primary))
            .ToList();

        ScreenBox.ItemsSource = items;

        // Three cases, and they want different answers. The pick still being there is the easy one.
        // A stored monitor that is not attached any more falls back to the primary: the user did
        // choose once, so this is a substitute for their answer rather than a first guess, and the
        // primary is the one screen that is always there. Nothing stored at all falls back to the
        // monitor this window is on — the one they are looking at, and not necessarily the primary.
        _restoringSource = true;
        if (previous != null && items.Any(item => item.DeviceName == previous))
            ScreenBox.SelectedValue = previous;
        else if (stored.Length > 0)
            ScreenBox.SelectedValue = items.FirstOrDefault(item => item.IsPrimary)?.DeviceName
                ?? items.FirstOrDefault()?.DeviceName;
        else
            ScreenBox.SelectedValue = CurrentScreenDeviceName(items);
        _restoringSource = false;
    }

    /// <remarks>
    /// Only a pick the user made is written — see <see cref="LoadScreens"/> for why a monitor that
    /// has been unplugged must not overwrite the one they chose.
    /// </remarks>
    private void ScreenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSource || ScreenBox.SelectedValue is not string deviceName) return;

        SaveTranslationPreference(settings => settings.Realtime.CaptureScreenDeviceName = deviceName);
    }

    private string? CurrentScreenDeviceName(IReadOnlyList<ScreenItem> items)
    {
        try
        {
            if (Window.GetWindow(this) is { } window)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    var deviceName = Screen.FromHandle(handle).DeviceName;
                    if (items.Any(item => item.DeviceName == deviceName))
                        return deviceName;
                }
            }
        }
        catch (Exception)
        {
            // Window not yet sourced, or a monitor removed mid-call — the primary below is a fine
            // answer either way.
        }

        return items.FirstOrDefault(item => item.IsPrimary)?.DeviceName
            ?? items.FirstOrDefault()?.DeviceName;
    }

    private void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        var controller = RealtimeSessionController.Instance;
        if (controller.IsActive)
        {
            controller.Stop();
            return;
        }

        // The other half of the same rule the capture side enforces: one OCR engine, one pool of
        // inference slots, and neither feature is any use with the other competing for them. This
        // way round is the less likely of the two — a capture covers the screen, so its selection
        // layer is usually over this button — but the toolbar phase leaves the screen usable.
        if (System.Windows.Application.Current.MainWindow is MainWindow { IsCapturing: true })
        {
            SetStatus(LocalizationService.Get("S.Realtime.CaptureInProgress"), isError: true);
            return;
        }

        if (SrcLangBox.SelectedValue is not string sourceLanguage)
        {
            SetStatus(LocalizationService.Get("S.Realtime.ChooseSourceFirst"), isError: true);
            return;
        }

        // The two modes need different things to be true before there is a session to start, and
        // both failures are the user's to fix on this page rather than something to discover on a
        // screen that has just gone black.
        System.Drawing.Rectangle framingBounds;
        var sourceWindow = IntPtr.Zero;

        if (WindowMode)
        {
            if (WindowBox.SelectedItem is not WindowItem window)
            {
                SetStatus(LocalizationService.Get("S.Realtime.ChooseWindowFirst"), isError: true);
                return;
            }

            // Checked here and again in the controller, and neither is redundant: this one can still
            // tell the user to refresh the list, while the later one is the last word before pixels
            // are read.
            if (ScreenOf(window.Hwnd) is not { } windowScreen)
            {
                SetStatus(LocalizationService.Get("S.Realtime.WindowCaptureGone"), isError: true);
                LoadWindows();
                return;
            }

            // The blocks have to be drawn over the window, so the framing layer belongs on whatever
            // monitor that window is on — not on whichever one the hidden picker last held.
            framingBounds = windowScreen.Bounds;
            sourceWindow = window.Hwnd;
        }
        else
        {
            if (ScreenBox.SelectedItem is not ScreenItem screen)
            {
                SetStatus(LocalizationService.Get("S.Realtime.NoScreens"), isError: true);
                return;
            }

            framingBounds = screen.Bounds;
        }

        var settings = SettingsService.Instance.Current;
        var request = new RealtimeStartRequest(
            framingBounds,
            _blockCount,
            LanguageData.GetValidOcrSourceCode(sourceLanguage),
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string),
            ProviderBox.SelectedValue as TranslationProvider? ?? DefaultProvider,
            settings.Realtime.TextColor,
            settings.Realtime.ScrimColor,
            settings.Realtime.ScrimOpacity,
            settings.Realtime.NaturalBackgroundEnabled,
            settings.Realtime.SampleSourceTextColor,
            WindowMode ? RealtimeCaptureMode.Window : RealtimeCaptureMode.Screen,
            sourceWindow,
            settings.Realtime.BorderEnabled,
            settings.Realtime.BorderColorMode == RealtimeBorderColorMode.Fixed
                ? settings.Realtime.BorderColor
                : null);

        // The shell is handed over to be hidden: it is almost certainly sitting on the screen the
        // user is about to frame blocks on, and it comes back when the session ends.
        controller.Start(request, Window.GetWindow(this));
    }

    // ── 顯示外觀 ───────────────────────────────────────────────────────────────

    private void TextColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.Realtime.TextColor,
            picked => Persist(s => s.Realtime.TextColor = picked));

    private void ScrimColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.Realtime.ScrimColor,
            picked => Persist(s => s.Realtime.ScrimColor = picked));

    /// <summary>
    /// Everything on the card, back to what it ships as: both colours and the mode each is in, the
    /// weight of the band, and the border with its own two values.
    /// </summary>
    /// <remarks>
    /// One button for the card rather than one per group. There were three before, each sitting
    /// inside a group and undoing only that group, which left the reader to work out how much a
    /// reset covered from where the button happened to be — and it split 底色 from 底色不透明度 across
    /// two of them, although nobody wants one put back without the other.
    ///
    /// The defaults are read off a fresh <see cref="RealtimeSettings"/> rather than written out
    /// again here, so that a default changed in one place does not leave this button restoring the
    /// old one.
    /// </remarks>
    private void ResetAppearanceBtn_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new RealtimeSettings();
        Persist(s =>
        {
            var realtime = s.Realtime;
            realtime.SampleSourceTextColor = defaults.SampleSourceTextColor;
            realtime.TextColor = defaults.TextColor;
            realtime.NaturalBackgroundEnabled = defaults.NaturalBackgroundEnabled;
            realtime.ScrimColor = defaults.ScrimColor;
            realtime.ScrimOpacity = defaults.ScrimOpacity;
            realtime.BorderEnabled = defaults.BorderEnabled;
            realtime.BorderColorMode = defaults.BorderColorMode;
            realtime.BorderColor = defaults.BorderColor;
        });

        // The trays and the slider are the controls here that hold their own value rather than
        // reading it back from the settings on every render, so they have to be told.
        ShowAppearanceModes(SettingsService.Instance.Current.Realtime);
        SyncScrimOpacity();
        RenderColours();
    }

    /// <summary>
    /// Puts the stored modes on the three trays and the 邊框 switch without that reading as the user
    /// setting them.
    /// </summary>
    /// <remarks>
    /// Guarded rather than attached-later, because the reset needs the same thing once the handlers
    /// are already on: checking a half raises Checked, which would write the value being restored
    /// straight back, one control at a time.
    /// </remarks>
    private void ShowAppearanceModes(RealtimeSettings stored)
    {
        _syncingAppearance = true;
        (stored.SampleSourceTextColor ? TextColorAutoRadio : TextColorFixedRadio).IsChecked = true;
        (stored.NaturalBackgroundEnabled ? ScrimColorAutoRadio : ScrimColorFixedRadio).IsChecked = true;
        (stored.BorderColorMode == RealtimeBorderColorMode.Fixed
            ? BorderFixedRadio
            : BorderRandomRadio).IsChecked = true;
        BorderToggle.IsChecked = stored.BorderEnabled;
        _syncingAppearance = false;
    }

    /// <summary>
    /// 文字顏色 → 自動／固定, which is the same value the 進階選項 switch used to hold.
    /// </summary>
    /// <remarks>
    /// Written the moment the half is chosen — one press is one decision, so there is nothing to
    /// hold back the way a drag across the opacity track has. The marker moves either way, restored
    /// or chosen: the guard is about what gets written, not about where the tray is drawn.
    /// </remarks>
    private void TextColorMode_Changed(object sender, RoutedEventArgs e)
    {
        MoveTextColorModeThumb(animate: true);
        if (_syncingAppearance) return;
        Persist(s => s.Realtime.SampleSourceTextColor = TextColorAutoRadio.IsChecked == true);
    }

    /// <inheritdoc cref="TextColorMode_Changed"/>
    private void ScrimColorMode_Changed(object sender, RoutedEventArgs e)
    {
        MoveScrimColorModeThumb(animate: true);
        if (_syncingAppearance) return;
        Persist(s => s.Realtime.NaturalBackgroundEnabled = ScrimColorAutoRadio.IsChecked == true);
    }

    /// <summary>顯示外觀 → 邊框. Written on the flip, like the two trays above.</summary>
    private void BorderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAppearance) return;
        Persist(s => s.Realtime.BorderEnabled = BorderToggle.IsChecked == true);
    }

    /// <inheritdoc cref="TextColorMode_Changed"/>
    private void BorderMode_Changed(object sender, RoutedEventArgs e)
    {
        MoveBorderModeThumb(animate: true);
        if (_syncingAppearance) return;
        Persist(s => s.Realtime.BorderColorMode = ReferenceEquals(sender, BorderFixedRadio)
            ? RealtimeBorderColorMode.Fixed
            : RealtimeBorderColorMode.Random);
    }

    private void BorderColorBtn_Click(object sender, RoutedEventArgs e) =>
        PickColour(
            SettingsService.Instance.Current.Realtime.BorderColor,
            picked => Persist(s => s.Realtime.BorderColor = picked));

    /// <summary>
    /// The half's width is not known until the tray is laid out, so the marker is placed again
    /// whenever it gets one — which for 邊框 is the first time the switch is turned on.
    /// </summary>
    private void TextColorModeThumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MoveTextColorModeThumb(animate: false);

    /// <inheritdoc cref="TextColorModeThumb_SizeChanged"/>
    private void ScrimColorModeThumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MoveScrimColorModeThumb(animate: false);

    /// <inheritdoc cref="TextColorModeThumb_SizeChanged"/>
    private void BorderModeThumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MoveBorderModeThumb(animate: false);

    private void MoveTextColorModeThumb(bool animate) =>
        MoveModeThumb(
            TextColorModeThumb, TextColorModeThumbShift, TextColorFixedRadio.IsChecked == true, animate);

    private void MoveScrimColorModeThumb(bool animate) =>
        MoveModeThumb(
            ScrimColorModeThumb, ScrimColorModeThumbShift, ScrimColorFixedRadio.IsChecked == true, animate);

    private void MoveBorderModeThumb(bool animate) =>
        MoveModeThumb(
            BorderModeThumb, BorderModeThumbShift, BorderFixedRadio.IsChecked == true, animate);

    /// <summary>Slides the marker under the chosen half — the same tray as 設定 → 偵錯工具.</summary>
    /// <remarks>
    /// The marker is the width of one half and starts under the first, so the second is exactly one
    /// of its own widths to the right. Three trays share this rather than one each: they are the
    /// same control asked three times, and a copy per tray is three places for the easing to drift.
    /// </remarks>
    private static void MoveModeThumb(
        Border thumb, System.Windows.Media.TranslateTransform shift, bool toSecond, bool animate)
    {
        var target = toSecond ? thumb.ActualWidth : 0;

        if (!animate || thumb.ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            shift.X = target;
            return;
        }

        shift.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });
    }

    /// <summary>
    /// Follows the thumb: the label and the preview on every step, the settings file at the end of a
    /// drag — or immediately, when the value moved by keyboard or by a click on the track, neither of
    /// which has an end to wait for.
    /// </summary>
    private void ScrimOpacitySlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingOpacity) return;

        // Persist renders on its way through, so only the held-back case has to render for itself.
        if (_draggingOpacity) RenderColours();
        else PersistScrimOpacity();
    }

    /// <summary>Puts the stored opacity on the slider without that reading as the user setting it.</summary>
    private void SyncScrimOpacity()
    {
        _syncingOpacity = true;
        ScrimOpacitySlider.Value = RealtimeSubtitleColors.ClampOpacity(
            SettingsService.Instance.Current.Realtime.ScrimOpacity);
        _syncingOpacity = false;
    }

    private void PersistScrimOpacity() =>
        Persist(s => s.Realtime.ScrimOpacity = CurrentScrimOpacity);

    /// <summary>
    /// What the slider is showing, which is what the preview draws — not what is stored, because
    /// mid-drag those are deliberately different.
    /// </summary>
    private int CurrentScrimOpacity =>
        RealtimeSubtitleColors.ClampOpacity((int)Math.Round(ScrimOpacitySlider.Value));

    /// <summary>
    /// Opens the system colour picker on the current value and reports what came back.
    /// </summary>
    /// <remarks>
    /// The native dialog, not one of ours: it already carries a full picker, the custom-colour slots
    /// and every keyboard convention, and none of that is worth rebuilding for two fields. It has no
    /// alpha channel, which suits a setting that has no alpha to offer — the scrim's is fixed, see
    /// <see cref="RealtimeSubtitleColors"/>.
    /// </remarks>
    private void PickColour(string current, Action<string> onPicked)
    {
        var start = RealtimeSubtitleColors.Text(current);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            // Opens on what is in effect, so a small adjustment starts from the current colour
            // rather than from black.
            Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B),
            FullOpen = true,
            AnyColor = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        onPicked(RealtimeSubtitleColors.Format(
            System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
    }

    private void Persist(Action<AppSettings> apply)
    {
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        RenderColours();
    }

    /// <summary>
    /// Paints the two swatches, their hex labels, the opacity reading and the preview.
    /// </summary>
    /// <remarks>
    /// The colours come from what is stored; the opacity comes from the slider, which during a drag
    /// is deliberately ahead of it — see <see cref="ScrimOpacitySlider_ValueChanged"/>.
    /// </remarks>
    private void RenderColours()
    {
        var settings = SettingsService.Instance.Current;
        var realtime = settings.Realtime;
        var opacity = CurrentScrimOpacity;
        var text = RealtimeSubtitleColors.Text(realtime.TextColor);
        var scrim = RealtimeSubtitleColors.Scrim(realtime.ScrimColor, opacity);

        ScrimOpacityValue.Text = $"{opacity}%";

        // The swatch shows the colour the user picked, so it is drawn opaque even for the scrim —
        // the preview below is where its translucency is on display.
        TextColorSwatch.Background = new SolidColorBrush(text);
        ScrimColorSwatch.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(scrim.R, scrim.G, scrim.B));

        TextColorValue.Text = RealtimeSubtitleColors.Format(text);
        ScrimColorValue.Text = RealtimeSubtitleColors.Format(scrim);

        PreviewText.Foreground = new SolidColorBrush(text);

        // The backdrop changes with the mode — see the preview markup for why one cannot serve both.
        PreviewChecker.Visibility = realtime.NaturalBackgroundEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewScene.Visibility = realtime.NaturalBackgroundEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        // No band at all in 自動, because that is what a session draws: the source line is erased and
        // the picture is repaired where it was, and the translation goes back on top of the repair.
        // Painting the stored colour here instead would promise a strip that never appears.
        PreviewScrim.Background = realtime.NaturalBackgroundEnabled
            ? null
            : new SolidColorBrush(scrim);

        // Greyed rather than hidden in 自動: the row would change shape with the mode, and the colour
        // is still what a sampled line falls back to when the sample comes back unusable.
        TextColorBtn.IsEnabled = !realtime.SampleSourceTextColor;
        ScrimColorBtn.IsEnabled = !realtime.NaturalBackgroundEnabled;

        // The slider says how much of the colour above it reaches the screen, so in 自動 it has
        // nothing to weigh. Dimmed by hand as well as disabled: a TextBlock inside a switched-off
        // panel is still drawn at full strength, which would leave the label looking live over a
        // greyed track.
        ScrimOpacityRow.IsEnabled = !realtime.NaturalBackgroundEnabled;
        ScrimOpacityRow.Opacity = realtime.NaturalBackgroundEnabled ? 0.45 : 1.0;

        // Only while it is about something on screen — see the note itself for what it is for.
        AutoModeNotice.Visibility =
            realtime.SampleSourceTextColor || realtime.NaturalBackgroundEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

        // The border follows the same stored values the session will be started with. 隨機 has no
        // one colour to show, so the preview draws it in the theme's accent, which sits with the page.
        var fixedBorder = RealtimeSubtitleColors.Border(realtime.BorderColor);
        var fixedBorderMode = realtime.BorderColorMode == RealtimeBorderColorMode.Fixed;
        BorderModeTray.Visibility = realtime.BorderEnabled ? Visibility.Visible : Visibility.Collapsed;
        BorderColorBtn.Visibility = realtime.BorderEnabled && fixedBorderMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        BorderColorSwatch.Background = new SolidColorBrush(fixedBorder);
        BorderColorValue.Text = RealtimeSubtitleColors.Format(fixedBorder);
        if (!realtime.BorderEnabled)
            PreviewScrim.BorderBrush = null;
        else if (fixedBorderMode)
            PreviewScrim.BorderBrush = new SolidColorBrush(fixedBorder);
        else
            PreviewScrim.SetResourceReference(Border.BorderBrushProperty, "AppAccent");
        PreviewScrim.BorderThickness = new Thickness(
            realtime.BorderEnabled ? RealtimeSubtitleColors.BorderThickness : 0);

        ResetAppearanceBtn.IsEnabled = !IsDefaultAppearance(realtime, opacity);
    }

    /// <summary>
    /// Whether every value on the card is still what it ships as, which is what decides the reset.
    /// </summary>
    /// <remarks>
    /// A reset that would do nothing still looks like it might, and the user has to press it to find
    /// out. The opacity is passed in rather than read from the settings: mid-drag the slider is
    /// deliberately ahead of what is stored, and a button that comes back to life a beat late reads
    /// as having missed the drag.
    /// </remarks>
    private static bool IsDefaultAppearance(RealtimeSettings realtime, int opacity)
    {
        var defaults = new RealtimeSettings();

        return realtime.SampleSourceTextColor == defaults.SampleSourceTextColor
            && realtime.NaturalBackgroundEnabled == defaults.NaturalBackgroundEnabled
            && realtime.BorderEnabled == defaults.BorderEnabled
            && realtime.BorderColorMode == defaults.BorderColorMode
            && opacity == defaults.ScrimOpacity
            && string.Equals(realtime.TextColor, defaults.TextColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(realtime.ScrimColor, defaults.ScrimColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(realtime.BorderColor, defaults.BorderColor, StringComparison.OrdinalIgnoreCase);
    }

    private void BlockCountDown_Click(object sender, RoutedEventArgs e) => StepBlockCount(-1);

    private void BlockCountUp_Click(object sender, RoutedEventArgs e) => StepBlockCount(+1);

    private void StepBlockCount(int delta)
    {
        var next = Math.Clamp(_blockCount + delta, MinBlocks, MaxBlocks);
        if (next == _blockCount) return;

        _blockCount = next;

        // Kept, unlike the rest of a sitting: the shortcut has to offer the same number of blocks as
        // this stepper and cannot read it off a page that is not open. See AppSettings.
        SaveTranslationPreference(settings => settings.Realtime.BlockCount = next);

        // The blocks kept from the last sitting were drawn to fill a different count, so they are
        // no longer an answer to the question being asked. Dropped as the count moves rather than
        // checked when they are offered back, so the user sees the rule they were given.
        RealtimeSessionController.Instance.ForgetBlocks();

        RenderBlockCount();
    }

    /// <summary>
    /// Shows the count and switches off whichever button would do nothing.
    /// </summary>
    /// <remarks>
    /// Disabled rather than silently ignored at the ends: a button that still looks pressable and
    /// then does nothing reads as the application having missed the click.
    /// </remarks>
    private void RenderBlockCount()
    {
        var active = RealtimeSessionController.Instance.IsActive;

        BlockCountText.Text = _blockCount.ToString();
        BlockCountDown.IsEnabled = !active && _blockCount > MinBlocks;
        BlockCountUp.IsEnabled = !active && _blockCount < MaxBlocks;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RenderState);

    private void RenderState()
    {
        bool active = RealtimeSessionController.Instance.IsActive;

        PrimaryBtn.Content = LocalizationService.Get(
            active ? "S.Realtime.StopSession" : "S.Realtime.SelectBlocks");

        // Locked rather than hidden while running: the user can see what the session was started
        // with, and changing any of it would mean rebuilding every block anyway.
        SrcLangBox.IsEnabled = !active;
        TgtLangBox.IsEnabled = !active;
        ProviderBox.IsEnabled = !active;
        ScreenBox.IsEnabled = !active;
        RenderBlockCount();

        // Nothing can start without a source language, so the button says so by being unavailable
        // rather than by refusing after the fact, and the field keeps its prompt until answered.
        var hasSource = SrcLangBox.SelectedValue is string;

        // The same rule one step further out: with no capture at all there is no mode to run, and
        // the whole feature is unavailable rather than one half of it. Said here, once, instead of
        // greying both tiles and leaving the user to work out what the two of them have in common.
        var capturable = WgcCapability.SupportsWindowMode || WgcCapability.SupportsScreenMode;

        PrimaryBtn.IsEnabled = active || (hasSource && capturable);
        SrcLangPlaceholder.Visibility = hasSource ? Visibility.Collapsed : Visibility.Visible;

        if (!active && !capturable)
        {
            SetStatus(LocalizationService.Get("S.Realtime.RealtimeUnsupported"), isError: true);
            return;
        }

        SetStatus(
            active ? LocalizationService.Get("S.Realtime.ActiveHint") : "",
            isError: false);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("AppError")
            : (System.Windows.Media.Brush)FindResource("AppTextSecondary");
    }
}
