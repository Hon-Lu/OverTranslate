using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views;
using OverTranslate.Views.Controls;
using Microsoft.Win32;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Settings persist the moment a control changes — there is no save button, so every handler
/// routes through <see cref="Persist"/>, which is inert while <see cref="_loading"/> is set.
/// </summary>
/// <remarks>
/// What a single translation service has to be told is not on this page: DeepL's key and OpenAI's
/// endpoint, model and prompt live in <see cref="ServiceSettingsOverlay"/>, reached from the tile
/// for that service. Everything here applies whichever service is chosen.
/// </remarks>
public partial class SettingsPage : UserControl
{
    private readonly DispatcherTimer _statusHold;

    /// <summary>
    /// The bundle written by the last press, so the row that appears afterwards can open the
    /// thing that was just sent. Null until the first export of this session.
    /// </summary>
    private string? _lastBundlePath;
    private readonly DispatcherTimer _hotkeyGamepadRecordTimer;
    private readonly ushort[] _recordGamepadButtons = new ushort[4];

    /// <summary>
    /// One editable shortcut: the controls that edit it and the settings it reads and writes.
    /// </summary>
    /// <param name="Action">
    /// Which shortcut this is, so the row can be matched against <see cref="HotkeyBindings.Resolve"/>
    /// — the one place that decides which of two shortcuts sharing a combination stays on.
    /// </param>
    /// <param name="AdvertisedInShell">
    /// Whether the shell's nav rail prints this combination beside a 快速工具 row, and so has to be
    /// told when it changes. True for the two shortcuts those rows name; false for the rest, which
    /// the interface names nowhere.
    /// </param>
    /// <remarks>
    /// There is no record button in the record because there is none on the page: the box is
    /// read-only and starts recording when it is clicked, so a button beside it would have been a
    /// second way to do the one thing clicking the box already does.
    /// </remarks>
    /// <param name="EnabledBox">
    /// The tick that turns this shortcut off, or null for the capture one, which has no tick at all
    /// and no setting behind it because that shortcut is the feature the application is for. Null
    /// here is what says "this row cannot be turned off".
    /// </param>
    /// <remarks>
    /// A row shadowed by a higher-priority shortcut says nothing about it. Priority still decides
    /// which of two shortcuts sharing a combination is registered — see
    /// <see cref="HotkeyBindings.Resolve"/>, and MainWindow logs the one it dropped — but the page
    /// does not carry a line for a state a user reaches only by having set the clash themselves.
    /// </remarks>
    private sealed record HotkeyField(
        HotkeyAction Action,
        string NameKey,
        TextBox Box,
        Func<AppSettings, string> Display,
        Func<AppSettings, ShortcutTrigger> Trigger,
        Action<AppSettings, ShortcutTrigger, string> Apply,
        bool AdvertisedInShell,
        // Fully qualified: WinForms is also referenced here and has its own CheckBox.
        System.Windows.Controls.CheckBox? EnabledBox = null,
        Action<AppSettings, bool>? SetEnabled = null);

    private HotkeyField[] _hotkeyFields = [];

    /// <summary>The field waiting for a key, or null. At most one at a time.</summary>
    private HotkeyField? _recording;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        // In the order the page lists them, which is not the order HotkeyBindings resolves them in:
        // there, position is priority and the newest shortcut goes last, while here it is what a
        // reader meets first. Nothing depends on this order — rows are looked up by action.
        _hotkeyFields =
        [
            new HotkeyField(
                HotkeyAction.Capture,
                "S.Settings.CaptureHotkey",
                HotkeyBox,
                s => s.HotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.Capture),
                ApplyCaptureTrigger,
                AdvertisedInShell: true),
            new HotkeyField(
                HotkeyAction.QuickLookup,
                "S.Settings.QuickLookupHotkey",
                QuickLookupHotkeyBox,
                s => s.QuickLookupHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.QuickLookup),
                ApplyQuickLookupTrigger,
                AdvertisedInShell: true,
                QuickLookupHotkeyEnabledCheckBox,
                (s, on) => s.QuickLookupHotkeyEnabled = on),
            new HotkeyField(
                HotkeyAction.QuickTranslate,
                "S.Settings.QuickTranslateHotkey",
                QuickTranslateHotkeyBox,
                s => s.QuickTranslateHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.QuickTranslate),
                ApplyQuickTranslateTrigger,
                AdvertisedInShell: false,
                QuickTranslateHotkeyEnabledCheckBox,
                (s, on) => s.QuickTranslateHotkeyEnabled = on),
            new HotkeyField(
                HotkeyAction.TranslationWindow,
                "S.Settings.WindowHotkey",
                WindowHotkeyBox,
                s => s.TranslationWindowHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.TranslationWindow),
                ApplyWindowTrigger,
                AdvertisedInShell: false,
                WindowHotkeyEnabledCheckBox,
                (s, on) => s.TranslationWindowHotkeyEnabled = on),
            new HotkeyField(
                HotkeyAction.RealtimePause,
                "S.Settings.RealtimePauseHotkey",
                RealtimePauseHotkeyBox,
                s => s.RealtimePauseHotkeyDisplay,
                s => HotkeyBindings.TriggerFor(s, HotkeyAction.RealtimePause),
                ApplyRealtimePauseTrigger,
                AdvertisedInShell: false,
                RealtimePauseHotkeyEnabledCheckBox,
                (s, on) => s.RealtimePauseHotkeyEnabled = on),
        ];

        _hotkeyGamepadRecordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _hotkeyGamepadRecordTimer.Tick += (_, _) => PollRecordingGamepad();

        _statusHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _statusHold.Tick += (_, _) => { _statusHold.Stop(); FadeStatusOut(); };

        // Paired with Loaded rather than subscribed once: the shell keeps one instance of this
        // page for its lifetime and swaps it in and out of the content host, so unsubscribing on
        // Unloaded without re-subscribing on Loaded would leave it deaf from the first time the
        // user navigated away. A static event holding an instance handler also has to be let go
        // of at some point, which rules out subscribing only once.
        Loaded   += (_, _) =>
        {
            LocalizationService.LanguageChanged += OnLanguageChanged;
            SettingsService.Instance.OcrDebugChanged += OnOcrDebugChanged;
            SettingsService.Instance.CaptureOptionsChanged += OnCaptureOptionsChanged;
            OnOcrDebugChanged(this, EventArgs.Empty);
            OnCaptureOptionsChanged(this, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            SettingsService.Instance.OcrDebugChanged -= OnOcrDebugChanged;
            SettingsService.Instance.CaptureOptionsChanged -= OnCaptureOptionsChanged;
            StopRecording();
        };

        ApplyDiagnosticUploadAvailability();
        LoadSettings();
    }

    /// <summary>
    /// Re-reads the stored settings, so a change made elsewhere — a key typed into the service
    /// panel, a shortcut rebound — is on screen when this page is next shown.
    /// </summary>
    public void Reload() => LoadSettings();

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            foreach (var field in _hotkeyFields) field.Box.Text = field.Display(s);

            // The ticks, then the "a shortcut above took this" lines the ticks can change. The
            // availability pass is explicit because the toggle handler ignores changes made while
            // loading, so nothing else would grey these rows out on the way in.
            foreach (var binding in HotkeyBindings.Resolve(s))
                if (FieldFor(binding.Action)?.EnabledBox is { } box)
                    box.IsChecked = binding.Enabled;

            foreach (var field in _hotkeyFields) ApplyHotkeyRowAvailability(field);

            LightThemeRadio.IsChecked = s.Theme != ThemeService.Dark;
            DarkThemeRadio.IsChecked  = s.Theme == ThemeService.Dark;

            // LocalizationService.Current, not s.UiLanguage: an unset preference is showing the
            // system default right now, and the picker has to agree with what is on screen.
            UiLanguageBox.ItemsSource = LocalizationService.Options;
            UiLanguageBox.SelectedValue = LocalizationService.Current;
            if (UiLanguageBox.SelectedValue == null) UiLanguageBox.SelectedIndex = 0;

            QuickTranslateSourceBox.ItemsSource = LanguageData.SourceLanguages;
            QuickTranslateTargetBox.ItemsSource = LanguageData.TargetLanguages;
            QuickTranslateSourceBox.SelectedValue = LanguageData.GetValidSourceCode(s.QuickTranslate.SourceLanguage);
            QuickTranslateTargetBox.SelectedValue = LanguageData.GetValidTargetCode(s.QuickTranslate.TargetLanguage);
            QuickTranslateSourceBox.Items.Refresh();
            QuickTranslateTargetBox.Items.Refresh();

            StartupCheckBox.IsChecked = StartupService.IsEnabled;

            AutoTranslateCheckBox.IsChecked = s.AutoTranslateAfterSelection;

            SaveScreenshotCheckBox.IsChecked = s.SaveScreenshotToDisk;
            ScreenshotPathBox.Text = ScreenshotSaveService.ResolveDirectory(s.ScreenshotSavePath);

            VerboseLoggingCheckBox.IsChecked = s.VerboseLogging;

            OcrLineBoxesCheckBox.IsChecked = s.OcrDebug.ShowLineBoxes;
            TextGroupBoxesCheckBox.IsChecked = s.OcrDebug.ShowGroupBoxes;
            DebugSourceOnlyRadio.IsChecked = !s.OcrDebug.ShowOnTranslation;
            DebugSourceAndTranslationRadio.IsChecked = s.OcrDebug.ShowOnTranslation;

            RefreshServiceTiles();
            UpdateScreenshotPathVisibility();
            UpdateDebugScopeAvailability();
            UpdateVerboseLoggingAvailability();
            CollapseDebugTools();
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private int _quickTranslateFoldVersion;

    private void QuickTranslateSettings_Toggled(object sender, RoutedEventArgs e)
    {
        if (QuickTranslateSettingsFold is null) return;

        var expanded = QuickTranslateSettingsToggle.IsChecked == true;
        var version = ++_quickTranslateFoldVersion;
        var fold = QuickTranslateSettingsFold;
        var body = QuickTranslateSettingsBody;
        var fromHeight = fold.Visibility == Visibility.Collapsed ? 0 : fold.ActualHeight;
        if (expanded) fold.Visibility = Visibility.Visible;

        void Finish()
        {
            fold.BeginAnimation(HeightProperty, null);
            body.BeginAnimation(OpacityProperty, null);
            fold.Height = expanded ? double.NaN : 0;
            body.Opacity = expanded ? 1 : 0;
            fold.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            Finish();
            return;
        }

        // Measure the body at the available width, including wrapped hints and its top margin.
        // Return to Auto after opening so resizing and language changes can reflow naturally.
        var width = ((FrameworkElement)fold.Parent).ActualWidth;
        body.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        var target = expanded ? body.DesiredSize.Height : 0;
        fold.Height = fromHeight;
        var duration = TimeSpan.FromMilliseconds(expanded ? 200 : 160);
        var height = new DoubleAnimation(target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        height.Completed += (_, _) =>
        {
            if (version == _quickTranslateFoldVersion) Finish();
        };
        fold.BeginAnimation(HeightProperty, height);
        body.BeginAnimation(OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, duration));
    }

    private void QuickTranslateLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QuickTranslateSourceBox.SelectedValue is not string source ||
            QuickTranslateTargetBox.SelectedValue is not string target) return;

        Persist(s =>
        {
            s.QuickTranslate.SourceLanguage = LanguageData.GetValidSourceCode(source);
            s.QuickTranslate.TargetLanguage = LanguageData.GetValidTargetCode(target);
        });
    }

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        FlashSaved();
    }

    private void FlashSaved() => FlashSuccess(LocalizationService.Get("S.Settings.Saved"));

    /// <summary>
    /// The same line the auto-save confirmation uses, for the handful of actions that finish with
    /// something to say other than 已儲存 — an export naming the file it wrote, so far.
    /// </summary>
    private void FlashSuccess(string message)
    {
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppSuccess");

        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1;
        AutoSaveHint.Opacity = 0;

        _statusHold.Stop();
        _statusHold.Start();
    }

    private void FadeStatusOut()
    {
        var fade = new DoubleAnimation
        {
            From = 1, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300))
        };
        fade.Completed += (_, _) => AutoSaveHint.Opacity = 1;
        StatusText.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// The status line for something that has started and has not finished. Neither colour fits:
    /// green would be a claim, red an accusation, and the fade the success path uses would take the
    /// line away while the thing it describes is still running — so this one holds until whoever
    /// started it replaces it.
    /// </summary>
    private void ShowProgress(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppTextSecondary");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    /// <summary>
    /// For an outcome that is neither. Red would say the press failed when the file it produced is
    /// sitting on the disk and is the whole point; green would say it went through when it did not.
    /// Held rather than faded, for the same reason the error line is: there is something left to do.
    /// </summary>
    private void ShowWarning(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppWarning");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    private void ShowError(string message)
    {
        _statusHold.Stop();
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text       = message;
        StatusText.Foreground = (Brush)FindResource("AppError");
        StatusText.Opacity    = 1;
        AutoSaveHint.Opacity  = 0;
    }

    // ── Field handlers ───────────────────────────────────────────────────────

    // ── Translation services ─────────────────────────────────────────────────

    /// <summary>Which service a tile's button configures.</summary>
    private TranslationProvider? ProviderOf(object sender) => sender switch
    {
        _ when ReferenceEquals(sender, DeepLConfigureBtn)  => TranslationProvider.DeepL,
        _ when ReferenceEquals(sender, OpenAiConfigureBtn) => TranslationProvider.OpenAI,
        _ => null,
    };

    private void ConfigureService_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderOf(sender) is not { } provider) return;

        // The panel belongs to the shell rather than to this page so that it covers the nav rail
        // too: a modal the user can navigate out from behind is not one.
        if (Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.OpenServiceSettings(provider);
    }

    /// <summary>
    /// Brings the two tiles in line with what is stored: what each service still needs before it
    /// can translate.
    /// </summary>
    /// <remarks>
    /// Public because the shell calls it when the settings panel is dismissed: what was typed in
    /// there is exactly what these tiles report.
    /// </remarks>
    public void RefreshServiceTiles()
    {
        var s = SettingsService.Instance.Current;

        // DeepL cannot translate a word without a key, so an empty one is something still to do.
        WriteServiceBadge(DeepLBadge, DeepLBadgeText, configured: s.ApiKey.Trim().Length > 0, required: true);

        // OpenAI has a working endpoint, model and prompt built in — it is aimed at a local model —
        // so nothing here is missing, only either left as shipped or changed.
        var openAiTouched =
            s.OpenAiBaseUrl.Trim().Length > 0 ||
            s.OpenAiApiKey.Trim().Length > 0 ||
            s.OpenAi.Profiles.Count > 0;
        WriteServiceBadge(OpenAiBadge, OpenAiBadgeText, configured: openAiTouched, required: false);
    }

    /// <param name="required">
    /// Whether the service refuses to run until it is configured. Only that case is worth a warning
    /// colour; a service that works as shipped is reporting a choice, not a gap.
    /// </param>
    private void WriteServiceBadge(Border badge, TextBlock text, bool configured, bool required)
    {
        var key = configured
            ? "S.Settings.ServiceReady"
            : required ? "S.Settings.ServiceNeedsSetup" : "S.Settings.ServiceDefault";

        text.Text = LocalizationService.Get(key);

        // SetResourceReference rather than a resolved brush: FindResource hands back the brush the
        // theme in force at the time holds, and a tile written once would then keep the dark
        // theme's fill after the user switched to the light one.
        var warn = !configured && required;
        badge.SetResourceReference(Border.BackgroundProperty, warn ? "AppWarningSubtle" : "AppAccentSubtle");
        text.SetResourceReference(TextBlock.ForegroundProperty, warn ? "AppWarning" : "AppAccent");
    }

    // ── General ──────────────────────────────────────────────────────────────

    private void AutoTranslate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.UpdateCaptureOptions(autoTranslate: AutoTranslateCheckBox.IsChecked == true);
        FlashSaved();
    }

    // Startup lives in the registry rather than the settings file, so it saves on its own path
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            StartupService.Set(StartupCheckBox.IsChecked == true);
            FlashSaved();
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.StartupFailed", ex.Message));
        }
    }

    // Takes effect on the next line written, not on the next launch — the level is applied before it
    // is stored, so a user who is mid-reproduction can tick the box and keep going.
    private void VerboseLogging_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool verbose = VerboseLoggingCheckBox.IsChecked == true;
        LogLevelService.Apply(verbose);
        Persist(s => s.VerboseLogging = verbose);
    }

    /// <summary>How long the 偵錯工具 fold takes, and the shape of it.</summary>
    /// <remarks>
    /// Eased out and not elastic: nothing threw this open, so an overshoot would be motion the
    /// gesture did not ask for. 0.3s is long enough to be followed and short enough that a second
    /// press never has to wait for a first.
    /// </remarks>
    private static readonly Duration DebugFoldDuration = new(TimeSpan.FromMilliseconds(300));

    private static readonly IEasingFunction DebugFoldEasing =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>How far the fold's content sits above its resting place before it opens.</summary>
    private const double DebugFoldRise = -8;

    private bool _debugToolsExpanded;

    /// <summary>
    /// Shuts the 偵錯工具 card, which is how every visit to this page starts.
    /// </summary>
    /// <remarks>
    /// Called from the load rather than left to the markup's initial state, because the page is
    /// built once and shown again: without this, a card opened in one visit is still open in the
    /// next. The fold is the one thing on this page that is not remembered — see AppSettings.
    ///
    /// Without animating, because this is not the card closing — it is the card being found shut,
    /// and a page that plays its animations on arrival looks like it is still loading.
    /// </remarks>
    private void CollapseDebugTools()
    {
        DebugToolsToggle.IsChecked = false;
        SetDebugToolsExpanded(false, animate: false);
    }

    private void DebugToolsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        SetDebugToolsExpanded(DebugToolsToggle.IsChecked == true, animate: true);
    }

    /// <summary>
    /// Opens or shuts the fold, carrying the card's height, the content and the chevron together.
    /// </summary>
    /// <remarks>
    /// <para>Every animation is written with a target and no start, which is what makes the fold
    /// reversible: WPF runs a To-only animation from whatever is on screen at that instant, so a
    /// fold caught halfway open turns round from halfway rather than snapping to its full height
    /// and closing from there. Pressing the header repeatedly does what pressing it looks like it
    /// should do.</para>
    ///
    /// <para>The height is animated on the clipping Border rather than left to Auto, because Auto
    /// cannot be animated — so it is pinned to its measured height for the duration and handed back
    /// to Auto at the end, where the content is free to reflow again (a longer translation, a
    /// wrapped line) without this having fixed a stale number to it.</para>
    /// </remarks>
    private void SetDebugToolsExpanded(bool expanded, bool animate)
    {
        _debugToolsExpanded = expanded;
        if (expanded) DebugToolsFold.Visibility = Visibility.Visible;

        // The Windows setting for animations inside a window. Off means shown the answer rather
        // than the motion — the fold still works, it just arrives.
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            StopDebugFoldAnimations();
            DebugToolsFold.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            DebugToolsFold.Height = expanded ? double.NaN : 0;
            DebugToolsBody.Opacity = expanded ? 1 : 0;
            DebugToolsBodyRise.Y = expanded ? 0 : DebugFoldRise;
            DebugToolsChevronAngle.Angle = expanded ? 180 : 0;
            return;
        }

        // Off Auto before animating, at the height it is showing right now.
        var target = expanded ? MeasuredDebugFoldHeight() : 0;
        DebugToolsFold.Height = DebugToolsFold.ActualHeight;
        if (expanded) ScrollDebugToolsIntoView(target);

        var height = DebugFoldAnimation(target);
        height.Completed += (_, _) =>
        {
            // A later press owns the fold now, and its own Completed will finish the job.
            if (_debugToolsExpanded != expanded) return;

            DebugToolsFold.BeginAnimation(HeightProperty, null);
            DebugToolsFold.Height = expanded ? double.NaN : 0;
            if (!expanded) DebugToolsFold.Visibility = Visibility.Collapsed;
        };

        DebugToolsFold.BeginAnimation(HeightProperty, height);
        DebugToolsBody.BeginAnimation(OpacityProperty, DebugFoldAnimation(expanded ? 1 : 0));
        DebugToolsBodyRise.BeginAnimation(
            TranslateTransform.YProperty, DebugFoldAnimation(expanded ? 0 : DebugFoldRise));
        DebugToolsChevronAngle.BeginAnimation(
            RotateTransform.AngleProperty, DebugFoldAnimation(expanded ? 180 : 0));
    }

    private static DoubleAnimation DebugFoldAnimation(double to) =>
        new(to, DebugFoldDuration) { EasingFunction = DebugFoldEasing };

    private void StopDebugFoldAnimations()
    {
        DebugToolsFold.BeginAnimation(HeightProperty, null);
        DebugToolsBody.BeginAnimation(OpacityProperty, null);
        DebugToolsBodyRise.BeginAnimation(TranslateTransform.YProperty, null);
        DebugToolsChevronAngle.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>
    /// Scrolls so the whole card is in view once it has finished opening, rather than leaving the
    /// user to find the rest of what they just opened.
    /// </summary>
    /// <remarks>
    /// <para>Aimed at where the card's bottom edge <em>will</em> be — its height now plus the fold
    /// it is about to grow — because scrolling to where it is today would stop short by exactly the
    /// part that is being revealed. The viewer clamps whatever it cannot reach yet and catches up
    /// frame by frame as the fold opens, so the two movements finish together.</para>
    ///
    /// <para>Does nothing when the card already fits, which is the common case on a tall window.
    /// Scrolling a page that did not need scrolling is worse than not scrolling one that did.</para>
    /// </remarks>
    private void ScrollDebugToolsIntoView(double foldHeight)
    {
        if (PresentationSource.FromVisual(DebugToolsCard) is null) return;

        var content = (Visual)CardsScroll.Content;
        var top = DebugToolsCard.TransformToAncestor(content).Transform(new System.Windows.Point(0, 0)).Y;
        var bottom = top + DebugToolsCard.ActualHeight + foldHeight;

        var offset = Math.Max(0, bottom - CardsScroll.ViewportHeight);
        if (offset <= CardsScroll.VerticalOffset) return;

        SmoothScroll.To(CardsScroll, offset, DebugFoldDuration, DebugFoldEasing);
    }

    /// <summary>How tall the fold's content wants to be at the width the card gives it.</summary>
    private double MeasuredDebugFoldHeight()
    {
        var width = DebugToolsFold.ActualWidth > 0 ? DebugToolsFold.ActualWidth : double.PositiveInfinity;
        DebugToolsBody.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        return DebugToolsBody.DesiredSize.Height;
    }

    private void OcrDebugBoxes_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool lines = OcrLineBoxesCheckBox.IsChecked == true;
        bool groups = TextGroupBoxesCheckBox.IsChecked == true;
        SettingsService.Instance.UpdateOcrDebug(lines, groups);
        FlashSaved();
    }

    private void OnOcrDebugChanged(object? sender, EventArgs e)
    {
        var loading = _loading;
        _loading = true;
        try
        {
            OcrLineBoxesCheckBox.IsChecked = SettingsService.Instance.Current.OcrDebug.ShowLineBoxes;
            TextGroupBoxesCheckBox.IsChecked = SettingsService.Instance.Current.OcrDebug.ShowGroupBoxes;
            DebugSourceOnlyRadio.IsChecked = !SettingsService.Instance.Current.OcrDebug.ShowOnTranslation;
            DebugSourceAndTranslationRadio.IsChecked = SettingsService.Instance.Current.OcrDebug.ShowOnTranslation;
        }
        finally { _loading = loading; }
        UpdateDebugScopeAvailability();
    }

    /// <summary>
    /// 框線顯示範圍 only says where the boxes are drawn, so with neither box ticked it has nothing
    /// to act on and is disabled rather than left to be chosen for no effect.
    /// </summary>
    private void UpdateDebugScopeAvailability() =>
        DebugScopeRow.IsEnabled = OcrLineBoxesCheckBox.IsChecked == true || TextGroupBoxesCheckBox.IsChecked == true;

    private void OcrDebugScope_Changed(object sender, RoutedEventArgs e)
    {
        // Ahead of the guard, because the marker has to follow the choice however it was made —
        // including the write coming back from the capture toolbar, which arrives with _loading set.
        MoveDebugScopeThumb(animate: true);

        if (_loading) return;
        SettingsService.Instance.UpdateOcrDebug(showOnTranslation: ReferenceEquals(sender, DebugSourceAndTranslationRadio));
        FlashSaved();
    }

    /// <summary>
    /// Puts the marker under the chosen half of the 框線顯示範圍 switch.
    /// </summary>
    /// <remarks>
    /// The travel is the marker's own width, because the two halves share a column size — see the
    /// tray's ColumnDefinitions. Measured rather than fixed: the halves are sized to the longer of
    /// two labels, which is a different number in every language.
    /// </remarks>
    private void MoveDebugScopeThumb(bool animate)
    {
        var target = DebugSourceAndTranslationRadio.IsChecked == true
            ? DebugScopeThumb.ActualWidth
            : 0;

        // Before the tray has been laid out there is no distance to travel and nothing to see; the
        // marker's own SizeChanged runs this again once the shared columns have their final width.
        if (!animate || DebugScopeThumb.ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            DebugScopeThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            DebugScopeThumbShift.X = target;
            return;
        }

        DebugScopeThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, DebugScopeSlideDuration) { EasingFunction = DebugFoldEasing });
    }

    /// <summary>
    /// The capture toolbar's trays, so the same control does not travel at two speeds in one app.
    /// Shorter than the fold: this is a marker crossing a gap, not a card opening.
    /// </summary>
    private static readonly Duration DebugScopeSlideDuration = new(TimeSpan.FromMilliseconds(220));

    /// <summary>
    /// The half's width is not known until the card has been opened once and laid out, so the marker
    /// is placed again whenever that number arrives or changes — without animating, since this is
    /// the switch being measured rather than the choice being changed.
    /// </summary>
    private void DebugScopeThumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MoveDebugScopeThumb(animate: false);

    /// <summary>
    /// An environment variable outranks this setting, so when one is set the checkbox says so rather
    /// than accepting clicks that change nothing.
    /// </summary>
    private void UpdateVerboseLoggingAvailability()
    {
        if (!LogLevelService.IsOverriddenByEnvironment) return;

        VerboseLoggingCheckBox.IsEnabled = false;
        VerboseLoggingHint.Text = LocalizationService.Get("S.Settings.LoggingEnvOverride");
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = DarkThemeRadio.IsChecked == true ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(theme);
        Persist(s => s.Theme = theme);
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (UiLanguageBox.SelectedValue as string is not { } language) return;

        // Persist first: the swap re-runs LoadSettings through LanguageChanged, and both that and
        // LocalizationService.Current read the stored value back.
        Persist(s => s.UiLanguage = language);
        LocalizationService.Apply(language);

        // Persist already flashed the confirmation, but it did so in the outgoing language and
        // LoadSettings does not touch the status line. Say it again in the new one.
        FlashSaved();
    }

    /// <summary>
    /// Re-renders the text on this page that DynamicResource cannot reach.
    /// </summary>
    /// <remarks>
    /// Four things on this page are composed in code and so hold a string from the language that
    /// was in effect when they were built: the provider list and its hint, the pickers' language
    /// labels, the service tiles' state words, and the environment-override notice. LoadSettings
    /// rebuilds all of them, and is already guarded against writing back.
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs e) => LoadSettings();

    private void SaveScreenshotCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateScreenshotPathVisibility();
        if (_loading) return;
        SettingsService.Instance.UpdateCaptureOptions(saveScreenshot: SaveScreenshotCheckBox.IsChecked == true);
        FlashSaved();
    }

    /// <summary>
    /// The capture toolbar's 顯示更多 panel sets the same two switches, and this page may be open
    /// behind the capture while it does.
    /// </summary>
    private void OnCaptureOptionsChanged(object? sender, EventArgs e)
    {
        var loading = _loading;
        _loading = true;
        try
        {
            AutoTranslateCheckBox.IsChecked = SettingsService.Instance.Current.AutoTranslateAfterSelection;
            SaveScreenshotCheckBox.IsChecked = SettingsService.Instance.Current.SaveScreenshotToDisk;
        }
        finally { _loading = loading; }
    }

    private void UpdateScreenshotPathVisibility()
    {
        ScreenshotPathRow.Visibility = SaveScreenshotCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScreenshotPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The box is read-only and acts as a button: clicking anywhere in it opens the folder picker.
        e.Handled = true;

        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("S.Settings.FolderPickerTitle"),
            InitialDirectory = ScreenshotPathBox.Text
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ScreenshotPathBox.Text = dialog.FolderName;
        // Store "" when the folder matches the default, so the setting follows the system
        // Pictures folder instead of freezing today's expanded path.
        Persist(s => s.ScreenshotSavePath = string.Equals(
            dialog.FolderName, ScreenshotSaveService.DefaultDirectory, StringComparison.OrdinalIgnoreCase)
            ? ""
            : dialog.FolderName);
    }

    private void OpenScreenshotFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScreenshotSaveService.OpenFolder(ScreenshotPathBox.Text);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    /// <remarks>
    /// The diagnostics folder rather than the log folder, because the export is what a person coming
    /// off this card is looking for — the raw logs are inside the zip anyway, and this is where the
    /// zips accumulate. That is also why the button no longer says "log folder": a button that opens
    /// one folder while naming another is worse than either name on its own.
    /// </remarks>
    private void OpenDiagnosticsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticBundleService.OpenExportFolder();
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    /// <summary>
    /// Collects the bundle and stops there, leaving what happens to it to the row that appears
    /// underneath.
    /// </summary>
    /// <remarks>
    /// Collecting and sending used to be one press, on the grounds that nobody collects a bundle
    /// for its own sake. True, and it left no room for the half a bundle cannot carry: what the
    /// user was trying to do when it went wrong. Splitting them buys the moment in between, which
    /// is where <see cref="DiagnosticNoteOverlay"/> now asks for exactly that.
    ///
    /// Off the UI thread because the bundle copies and compresses every log file there is, which on
    /// a machine that has filled its five archives is a dozen megabytes — not long, but long enough
    /// to freeze the window on a slow disk, and freezing while collecting a bug report is its own
    /// bug report. The button is disabled meanwhile so the same zip cannot be started twice.
    /// </remarks>
    private async void ExportDiagnosticsBtn_Click(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsBtn.IsEnabled = false;

        // A code from an earlier press describes an earlier upload. Leaving it on screen through
        // the next one invites it to be copied as though it were the new one.
        DiagnosticsResultPanel.Visibility = Visibility.Collapsed;

        try
        {
            ShowProgress(LocalizationService.Get("S.Settings.DiagnosticsExporting"));

            var path = await Task.Run(() => DiagnosticBundleService.Export());
            _lastBundlePath = path;

            ShowExported();
            FlashSuccess(LocalizationService.Get("S.Settings.DiagnosticsExported"));

            // Only where there is nowhere to send it. With an endpoint the panel that just appeared
            // is the next step and says so, and an Explorer window arriving over it is a second
            // instruction contradicting the first. Without one, this is still #126: the file exists
            // to be dragged into a forum post, and Explorer opens with it already selected.
            if (!DiagnosticUploadService.IsConfigured)
                DiagnosticBundleService.Reveal(path);
        }
        catch (Exception ex)
        {
            // If the collection failed there is no file to fall back to, which is why this one
            // names the error.
            ShowError(LocalizationService.Format("S.Settings.DiagnosticsFailed", ex.Message));
        }
        finally
        {
            ExportDiagnosticsBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Opens the panel that asks what went wrong and sends the bundle with the answer attached.
    /// </summary>
    /// <remarks>
    /// The panel belongs to the shell rather than to this page, like the service one: its scrim has
    /// to cover the nav rail, and an upload in flight is exactly the moment the user must not be
    /// able to navigate away from the thing that will tell them how it went.
    /// </remarks>
    private void UploadDiagnosticsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBundlePath is null) return;

        if (Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.OpenDiagnosticNote(_lastBundlePath);
    }

    /// <summary>
    /// Brings the result panel in line with what became of the bundle, once the note panel is gone.
    /// </summary>
    /// <remarks>
    /// Public because the shell owns the note panel and hands its outcome back here — the same
    /// arrangement <see cref="RefreshServiceTiles"/> has with the service panel.
    ///
    /// Dismissing without sending leaves the panel exactly as the export left it: the bundle is
    /// still there, the Upload button is still there, and nothing has happened that the page has
    /// anything to say about.
    /// </remarks>
    public void ReportDiagnosticUpload(DiagnosticNoteResult result)
    {
        if (result.Code is { } code)
        {
            ShowUploaded(code);
            FlashSuccess(LocalizationService.Format("S.Settings.DiagnosticsUploaded", code));
            return;
        }

        if (result.AttemptFailed)
        {
            ShowNotUploaded();
            ShowWarning(LocalizationService.Get("S.Settings.DiagnosticsUploadFailed"));
        }
    }

    /// <summary>
    /// The panel as it looks when a bundle has been written and has not been sent anywhere.
    /// </summary>
    /// <remarks>
    /// Upload leads because it is the reason the export was pressed, and it is the only thing here
    /// that has not happened yet. It is absent entirely in a build with no endpoint, where the
    /// panel is a receipt rather than a step.
    /// </remarks>
    private void ShowExported()
    {
        DiagnosticsResultGlyph.Text = CompletedGlyph;
        DiagnosticsResultTitle.SetResourceReference(
            TextBlock.TextProperty, "S.Settings.DiagnosticsExportedTitle");
        DiagnosticsResultHint.SetResourceReference(
            TextBlock.TextProperty,
            DiagnosticUploadService.IsConfigured
                ? "S.Settings.DiagnosticsExportedHint"
                : "S.Settings.DiagnosticsNoEndpointHint");

        DiagnosticsCodeText.Visibility = Visibility.Collapsed;
        CopyDiagnosticsCodeBtn.Visibility = Visibility.Collapsed;
        UploadDiagnosticsBtn.Visibility =
            DiagnosticUploadService.IsConfigured ? Visibility.Visible : Visibility.Collapsed;

        ShowDiagnosticsResultPanel();
    }

    /// <summary>The panel as it looks when a bundle went up and came back with a code.</summary>
    private void ShowUploaded(string code)
    {
        DiagnosticsResultGlyph.Text = CompletedGlyph;
        DiagnosticsResultTitle.SetResourceReference(
            TextBlock.TextProperty, "S.Settings.DiagnosticsCodeLabel");
        DiagnosticsResultHint.SetResourceReference(
            TextBlock.TextProperty, "S.Settings.DiagnosticsUploadedHint");

        DiagnosticsCodeText.Text = code;
        DiagnosticsCodeText.Visibility = Visibility.Visible;
        CopyDiagnosticsCodeBtn.Visibility = Visibility.Visible;

        // It has been sent. Leaving the button there invites a second copy of the same bundle,
        // which arrives at the other end as a second report from someone who only had one problem.
        UploadDiagnosticsBtn.Visibility = Visibility.Collapsed;

        ShowDiagnosticsResultPanel();
    }

    /// <summary>
    /// The panel as it looks when the bundle was written but did not leave the machine.
    /// </summary>
    /// <remarks>
    /// The same panel rather than a different one, because the thing it is built around — Open file,
    /// pointing at the zip that exists either way — is what the user needs in both cases. What goes
    /// away is everything that would be a lie here: there is no code to copy, and nothing was
    /// uploaded for the thirty-day line to be about.
    ///
    /// One wording for every reason an upload can fail. Whether it was the network, the size or a
    /// refusal, the next move is the same: report it by hand and attach the file. Telling the four
    /// apart would offer a distinction the user cannot act on.
    ///
    /// The thirty-day line stays, unlike everything else that goes: it is about the copy on disk,
    /// which is kept for the same thirty days either way.
    /// </remarks>
    private void ShowNotUploaded()
    {
        DiagnosticsResultGlyph.Text = FailedGlyph;
        DiagnosticsResultTitle.SetResourceReference(
            TextBlock.TextProperty, "S.Settings.DiagnosticsUploadFailedTitle");
        DiagnosticsResultHint.SetResourceReference(
            TextBlock.TextProperty, "S.Settings.DiagnosticsNotUploadedHint");

        DiagnosticsCodeText.Visibility = Visibility.Collapsed;
        CopyDiagnosticsCodeBtn.Visibility = Visibility.Collapsed;

        // Stays, as the retry. The reasons an upload fails are mostly the ones that pass on their
        // own — a network coming back, a rate limit expiring — and the bundle it would send is
        // still sitting on the disk.
        UploadDiagnosticsBtn.Visibility = Visibility.Visible;

        ShowDiagnosticsResultPanel();
    }

    /// <summary>
    /// Shows the result panel, and scrolls to it when it has landed past the bottom of the page.
    /// </summary>
    /// <remarks>
    /// The panel appears at the foot of the last card on a page that scrolls, so on anything short
    /// of a tall window it arrives off screen. Without this the press looks like it did nothing:
    /// the status line flashes and fades, and everything it produced — the code, the upload button,
    /// the file — is somewhere the user has been given no reason to look.
    ///
    /// After a layout pass, because the panel became visible a moment ago and has no height yet;
    /// scrolling to a zero-height element lands in the wrong place.
    /// </remarks>
    private void ShowDiagnosticsResultPanel()
    {
        DiagnosticsResultPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(RevealDiagnosticsResult), DispatcherPriority.Loaded);
    }

    /// <summary>Room left under the panel once it has been scrolled to, so it is not flush.</summary>
    private const double RevealPadding = 12;

    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(280));

    /// <remarks>
    /// Animated rather than jumped, and that is the point rather than decoration: movement is what
    /// says something appeared down here, where an instant re-position is indistinguishable from
    /// the page having always looked that way — which is the thing this is trying to fix. Windows'
    /// animation setting is the local reduced-motion preference, and with it off the scroll goes
    /// straight there.
    /// </remarks>
    private void RevealDiagnosticsResult()
    {
        if (CardsScroll.Content is not FrameworkElement content) return;

        // False if the user has navigated off the page while the export was running, in which case
        // there is nothing on screen to scroll and no ancestor to measure against.
        if (!DiagnosticsResultPanel.IsVisible) return;

        var top = DiagnosticsResultPanel
            .TransformToAncestor(content)
            .Transform(new System.Windows.Point(0, 0)).Y;

        var target = Math.Min(
            top + DiagnosticsResultPanel.ActualHeight + RevealPadding - CardsScroll.ViewportHeight,
            CardsScroll.ScrollableHeight);

        // Only when it is genuinely out of sight. Moving the page under the eyes of someone who can
        // already see the panel is the same interruption for none of the benefit.
        if (target <= CardsScroll.VerticalOffset) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            CardsScroll.ScrollToVerticalOffset(target);
            return;
        }

        CardsScroll.BeginAnimation(ScrollOffsetProperty, new DoubleAnimation
        {
            From = CardsScroll.VerticalOffset,
            To = target,
            Duration = RevealDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },

            // Held rather than released at the end, unlike every other animation on this page:
            // releasing hands the property back to its default of zero, and the callback below
            // would scroll the page to the top the moment the movement finished.
        });
    }

    /// <summary>
    /// What the scroll animation drives. <see cref="ScrollViewer.VerticalOffset"/> is read-only, so
    /// the animation moves this instead and every tick scrolls by hand.
    /// </summary>
    private static readonly DependencyProperty ScrollOffsetProperty =
        DependencyProperty.RegisterAttached(
            "ScrollOffset",
            typeof(double),
            typeof(SettingsPage),
            new PropertyMetadata(0.0, (d, e) =>
                ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    /// <summary>Segoe MDL2 Completed, the tick in a circle.</summary>
    private const string CompletedGlyph = "\uE930";

    /// <summary>Segoe MDL2 Important, the exclamation mark in a circle.</summary>
    private const string FailedGlyph = "\uE7BA";

    /// <summary>
    /// Points the card's explanation at whichever of the two stories is true for this build: with an
    /// endpoint compiled in there is somewhere to send the export afterwards; without one, nothing
    /// ever leaves the machine. The button itself says the same thing in both — it only exports.
    /// </summary>
    /// <remarks>
    /// A resource reference rather than an assignment, so it survives a language change — the page
    /// rebuilds itself on one, and a plain Text assignment would come back in the old language.
    ///
    /// A build with no endpoint is a supported state, not a broken one: it is what every build was
    /// until the worker was deployed, and what a user gets by pointing OVERTRANSLATE_DIAG_ENDPOINT
    /// at something that is not an address.
    /// </remarks>
    private void ApplyDiagnosticUploadAvailability()
    {
        var configured = DiagnosticUploadService.IsConfigured;

        DiagnosticsHintText.SetResourceReference(
            TextBlock.TextProperty,
            configured ? "S.Settings.DiagnosticsUploadHint" : "S.Settings.DiagnosticsHint");
    }

    /// <remarks>
    /// The clipboard is a shared, lockable resource, and the one thing that must not happen is the
    /// user walking away believing they have the code. On failure the code stays on screen and the
    /// line says to read it from there.
    /// </remarks>
    private void CopyDiagnosticsCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = DiagnosticsCodeText.Text;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            Clipboard.SetText(code);
            FlashSuccess(LocalizationService.Get("S.Settings.DiagnosticsCodeCopied"));
        }
        catch (Exception)
        {
            ShowError(LocalizationService.Get("S.Settings.CopyFailed"));
        }
    }

    /// <remarks>
    /// Opens the zip rather than selecting it, because the question this button answers is "what did
    /// I just send" — and Explorer shows the three files inside a zip it opens.
    /// </remarks>
    private void OpenDiagnosticsBundleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBundlePath is null) return;

        try
        {
            DiagnosticBundleService.Open(_lastBundlePath);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    /// <summary>The field this box edits, or null for anything else.</summary>
    private HotkeyField? FieldOf(object sender) =>
        _hotkeyFields.FirstOrDefault(field => ReferenceEquals(field.Box, sender));

    /// <summary>The row that edits one action.</summary>
    private HotkeyField? FieldFor(HotkeyAction action) =>
        _hotkeyFields.FirstOrDefault(field => field.Action == action);

    /// <summary>
    /// Greys out the box and the record button of a shortcut that is switched off.
    /// </summary>
    /// <remarks>
    /// The tick is the whole row's switch, so leaving the rest of the row live invites the user to
    /// record a combination for a shortcut that will not be registered — an edit that appears to work
    /// and changes nothing.
    ///
    /// Only the tick does this. A row shadowed by a higher-priority shortcut stays editable on
    /// purpose: re-recording it onto a free combination is exactly how that is fixed, and disabling
    /// the row would take the fix away along with the problem.
    /// </remarks>
    private static void ApplyHotkeyRowAvailability(HotkeyField field)
    {
        // No tick means the row cannot be switched off at all — the capture shortcut.
        var on = field.EnabledBox is not { } box || box.IsChecked == true;

        field.Box.IsEnabled = on;

        // Describe the next action on load and after each toggle.
        if (field.EnabledBox is { } toggle)
        {
            toggle.SetResourceReference(ToolTipProperty,
                on ? "S.Settings.HotkeyDisable" : "S.Settings.HotkeyEnable");
            System.Windows.Automation.AutomationProperties.SetName(toggle, LocalizationService.Get(field.NameKey));
        }
    }

    private void HotkeyEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var field = _hotkeyFields.FirstOrDefault(f => ReferenceEquals(f.EnabledBox, sender));
        if (field?.SetEnabled is not { } setEnabled || field.EnabledBox is not { } box) return;

        // Switching a row off while it is waiting for a key would leave it recording into a control
        // about to be disabled, and the tick would look like it had done nothing.
        if (ReferenceEquals(_recording, field)) StopRecording();

        Persist(s => setEnabled(s, box.IsChecked == true));
        ApplyHotkeyRowAvailability(field);

        // The global hooks are bound from these settings, so the tick means nothing until they are
        // rebound — without this the shortcut keeps working until the application is restarted, and
        // a switch that takes effect later is worse than no switch.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // A row the rail advertises drops its combination when it is switched off, so the rail is
        // as wrong after a tick as it is after a re-record.
        if (field.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHints();
    }

    private static void ApplyCaptureTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.HotkeyInputKind = trigger.Kind;
        s.HotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.HotkeyModifiers = trigger.Modifiers;
            s.HotkeyVirtualKey = trigger.VirtualKey;
            s.HotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.HotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyWindowTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.TranslationWindowHotkeyInputKind = trigger.Kind;
        s.TranslationWindowHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.TranslationWindowHotkeyModifiers = trigger.Modifiers;
            s.TranslationWindowHotkeyVirtualKey = trigger.VirtualKey;
            s.TranslationWindowHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.TranslationWindowHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyRealtimePauseTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.RealtimePauseHotkeyInputKind = trigger.Kind;
        s.RealtimePauseHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.RealtimePauseHotkeyModifiers = trigger.Modifiers;
            s.RealtimePauseHotkeyVirtualKey = trigger.VirtualKey;
            s.RealtimePauseHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.RealtimePauseHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyQuickLookupTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.QuickLookupHotkeyInputKind = trigger.Kind;
        s.QuickLookupHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.QuickLookupHotkeyModifiers = trigger.Modifiers;
            s.QuickLookupHotkeyVirtualKey = trigger.VirtualKey;
            s.QuickLookupHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.QuickLookupHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private static void ApplyQuickTranslateTrigger(AppSettings s, ShortcutTrigger trigger, string display)
    {
        s.QuickTranslateHotkeyInputKind = trigger.Kind;
        s.QuickTranslateHotkeyDisplay = display;
        if (trigger.Kind == ShortcutInputKind.Keyboard)
        {
            s.QuickTranslateHotkeyModifiers = trigger.Modifiers;
            s.QuickTranslateHotkeyVirtualKey = trigger.VirtualKey;
            s.QuickTranslateHotkeyGamepadButton = GamepadShortcutButton.None;
        }
        else if (trigger.Kind == ShortcutInputKind.Gamepad)
        {
            s.QuickTranslateHotkeyGamepadButton = trigger.GamepadButton;
        }
    }

    private void StartRecording(HotkeyField field)
    {
        // Only one at a time: two boxes both asking to be pressed would leave the next key press
        // ambiguous to the user long before it was ambiguous to the code.
        StopRecording();

        _recording = field;
        field.Box.Text = LocalizationService.Get("S.Settings.HotkeyPromptAnyInput");
        field.Box.Focus();

        for (int i = 0; i < _recordGamepadButtons.Length; i++)
            _recordGamepadButtons[i] = GamepadInput.TryGetButtons(i, out var buttons) ? buttons : (ushort)0;
        _hotkeyGamepadRecordTimer.Start();
    }

    /// <summary>Ends recording and puts the stored trigger back in the box.</summary>
    /// <remarks>
    /// Reads the setting rather than remembering what was there, so this also serves as the way
    /// back after a successful capture: the new value has been persisted by then, and restoring
    /// from the settings shows it.
    /// </remarks>
    private void StopRecording()
    {
        _hotkeyGamepadRecordTimer.Stop();
        if (_recording is not { } field) return;

        field.Box.Text = field.Display(SettingsService.Instance.Current);
        _recording = null;

        // The box asks to be pressed by being focused, so it has to stop being focused once it has
        // been: a box left carrying the focus ring after the shortcut is taken reads as still
        // waiting for one. Only when the focus is still here — the path in from LostFocus runs
        // after the user has already put it somewhere else, and clearing then would take it back.
        if (field.Box.IsKeyboardFocused) Keyboard.ClearFocus();
    }

    /// <summary>
    /// Every click on a focused box toggles recording — start, then stop, then start again.
    /// </summary>
    /// <remarks>
    /// GotFocus can only answer the first click, because the box stays focused after it. Without
    /// this the box would record once and then ignore every further click until focus had left and
    /// come back, which is the state a user lands in the moment they change their mind.
    ///
    /// The focusing click is left alone so it is not handled twice: GotFocus starts the recording
    /// that click asked for.
    /// </remarks>
    private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FieldOf(sender) is not { } field || !field.Box.IsFocused) return;

        e.Handled = true;
        if (ReferenceEquals(_recording, field)) StopRecording();
        else StartRecording(field);
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (FieldOf(sender) is { } field && !ReferenceEquals(_recording, field))
            StartRecording(field);
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (FieldOf(sender) is not { } field || !ReferenceEquals(_recording, field)) return;

        // Keep recording while focus moves inside this settings page. This is what lets the user
        // press a mouse button anywhere on the page after starting to record instead of having to
        // aim at the read-only box itself.
        if (Keyboard.FocusedElement is DependencyObject focused && IsDescendantOfThisPage(focused)) return;

        StopRecording();
    }

    private bool IsDescendantOfThisPage(DependencyObject child)
    {
        for (DependencyObject? current = child; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } recording || !ReferenceEquals(recording.Box, sender)) return;
        e.Handled = true;

        bool isSystemKey = e.Key == Key.System;
        var key = isSystemKey ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt  || key == Key.RightAlt  ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        uint mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl))  mods |= GlobalHotkey.MOD_CONTROL;
        if (isSystemKey || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mods |= GlobalHotkey.MOD_ALT;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods |= GlobalHotkey.MOD_SHIFT;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        var trigger = ShortcutTrigger.Keyboard(mods, vk);

        // A bare key is not merely watched, it is taken from every other application — so only the
        // keys that can afford to be taken are offered. See HotkeyBindings.IsBindable.
        if (!HotkeyBindings.IsBindable(trigger))
        {
            ShowError(LocalizationService.Get("S.Settings.HotkeyNeedsModifier"));
            StopRecording();
            return;
        }

        var prefix = GlobalHotkey.ModifiersToString(mods);
        var display = string.IsNullOrEmpty(prefix) ? key.ToString() : $"{prefix}+{key}";
        CommitShortcut(recording, trigger, display);
    }

    /// <remarks>
    /// The left and right buttons are not offered, and cannot be: left is how the box is clicked to
    /// start recording in the first place, and a shortcut on either would fire on every click the
    /// user makes anywhere. That leaves middle and the two side buttons — matching what
    /// <see cref="GlobalAuxiliaryHotkeys"/> watches for.
    ///
    /// Handled is set so the press is not also acted on as a press: XButton1 and XButton2 are the
    /// browser's Back and Forward, which WPF routes to navigation.
    /// </remarks>
    private void SettingsPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_recording is not { } recording) return;

        var kind = e.ChangedButton switch
        {
            MouseButton.Middle => ShortcutInputKind.MouseMiddle,
            MouseButton.XButton1 => ShortcutInputKind.MouseX1,
            MouseButton.XButton2 => ShortcutInputKind.MouseX2,
            _ => ShortcutInputKind.Keyboard,
        };

        if (kind == ShortcutInputKind.Keyboard) return;

        e.Handled = true;
        CommitShortcut(
            recording,
            ShortcutTrigger.Mouse(kind),
            LocalizationService.Get(MouseButtonNameKey(kind)));
    }

    /// <summary>The string naming one mouse button in the shortcut box.</summary>
    private static string MouseButtonNameKey(ShortcutInputKind kind) => kind switch
    {
        ShortcutInputKind.MouseX1 => "S.Settings.MouseX1",
        ShortcutInputKind.MouseX2 => "S.Settings.MouseX2",
        _ => "S.Settings.MouseMiddle",
    };

    private void PollRecordingGamepad()
    {
        if (_recording is not { } recording)
        {
            _hotkeyGamepadRecordTimer.Stop();
            return;
        }

        for (int i = 0; i < _recordGamepadButtons.Length; i++)
        {
            if (!GamepadInput.TryGetButtons(i, out var current))
            {
                _recordGamepadButtons[i] = 0;
                continue;
            }

            ushort pressed = (ushort)(current & ~_recordGamepadButtons[i]);
            _recordGamepadButtons[i] = current;
            var button = GamepadInput.FirstButton(pressed);
            if (button == GamepadShortcutButton.None) continue;

            var display = LocalizationService.Format(
                "S.Settings.GamepadButton",
                GamepadInput.ButtonName(button));
            CommitShortcut(recording, ShortcutTrigger.Gamepad(button), display);
            return;
        }
    }

    /// <remarks>
    /// Windows keys a registration by window and combination, so the second shortcut to claim one is
    /// simply refused — RegisterHotKey returns false and nothing else happens. Left to itself that
    /// reads as a shortcut that stopped working for no reason, so the clash is refused here, where
    /// there is something to say about it. The mouse and controller buttons are not registered with
    /// Windows at all, but they go through the same refusal so that one button cannot silently do two
    /// different things.
    ///
    /// A shortcut that is switched off does not hold its trigger — same rule as
    /// <see cref="HotkeyBindings"/>, so what the page refuses and what actually gets registered
    /// agree.
    /// </remarks>
    private void CommitShortcut(HotkeyField recording, ShortcutTrigger trigger, string display)
    {
        var settings = SettingsService.Instance.Current;
        var enabled = HotkeyBindings.Resolve(settings)
            .Where(binding => binding.Enabled)
            .Select(binding => binding.Action)
            .ToHashSet();

        var taken = _hotkeyFields.FirstOrDefault(
            field => !ReferenceEquals(field, recording) &&
                     enabled.Contains(field.Action) &&
                     field.Trigger(settings) == trigger);

        if (taken is not null)
        {
            ShowError(LocalizationService.Format(
                "S.Settings.HotkeyTaken", display, LocalizationService.Get(taken.NameKey)));
            StopRecording();
            return;
        }

        Persist(s => recording.Apply(s, trigger, display));

        // After the write, so the box picks the new trigger up out of the settings.
        StopRecording();

        // The global hook holds the old trigger until it is rebound.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ReRegisterHotkey();

        // The nav rail advertises these beside 截圖翻譯 and 取詞翻譯 and is on screen right now, so it
        // has to be told; nothing else re-reads them until the shell is next shown or activated. The
        // other shortcuts are advertised nowhere, so there is nothing to refresh.
        if (recording.AdvertisedInShell && Window.GetWindow(this) is Shell.ShellWindow shell)
            shell.RefreshHotkeyHints();
    }
}
