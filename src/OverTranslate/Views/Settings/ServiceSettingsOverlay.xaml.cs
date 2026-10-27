using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Views.Controls;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;
using RadioButton = System.Windows.Controls.RadioButton;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Modal panel holding everything one translation service has to be told — a key for DeepL, an
/// endpoint and a prompt for OpenAI. Drawn on top of the shell rather than in a window of its own,
/// like <see cref="Shell.AboutOverlay"/>, so the app keeps a single visible surface.
/// </summary>
/// <remarks>
/// These settings persist the moment a control changes, the same contract the settings page keeps,
/// so every handler routes through <see cref="Persist"/> and is inert while <see cref="_loading"/>
/// is set. There is no OK button to press and nothing is discarded on close.
/// </remarks>
public partial class ServiceSettingsOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    // Typing shouldn't hit the disk on every keystroke; the value is written once typing pauses.
    private static readonly TimeSpan EditDebounce = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _apiKeyDebounce;
    private readonly DispatcherTimer _openAiSettingsDebounce;

    /// <summary>True while the controls are being populated, so initialization never writes back.</summary>
    private bool _loading;

    /// <summary>Which service is on screen. Decides which panel is shown and what the title says.</summary>
    private TranslationProvider _provider = TranslationProvider.DeepL;

    /// <summary>Raised once the panel has been dismissed, so the page behind it can re-read what changed.</summary>
    public event EventHandler? Closed;

    public ServiceSettingsOverlay()
    {
        InitializeComponent();

        _apiKeyDebounce = new DispatcherTimer { Interval = EditDebounce };
        _apiKeyDebounce.Tick += (_, _) =>
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Secret.Trim());
        };

        _openAiSettingsDebounce = new DispatcherTimer { Interval = EditDebounce };
        _openAiSettingsDebounce.Tick += (_, _) =>
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Secret.Trim();
                if (TryReadTimeout(out var timeout)) s.OpenAiTimeoutSeconds = timeout;
            });
        };

        // The prompt card writes the settings itself — it is the one surface here that commits on
        // a button rather than as it is typed — so all this side has to do is re-read the list.
        PromptEditor.Changed += (_, _) => LoadPromptLibrary(SettingsService.Instance.Current);

        // Focus comes back so Escape closes this panel again: it was on the card, which has gone.
        PromptEditor.Closed += (_, _) => Focus();
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open(TranslationProvider provider)
    {
        _provider = provider;
        LoadSettings();

        Visibility = Visibility.Visible;

        // The panel is only listening while it is on screen, and the settings page behind it
        // re-reads the same strings on its own.
        LocalizationService.LanguageChanged += OnLanguageChanged;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated,
        // then ramps snapping back on over roughly a second once the motion stops — see
        // AboutOverlay.Open for why the card is cached as a bitmap for the length of the scale.
        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // Focus lets the control receive Escape without the page underneath stealing it
        Focus();
    }

    public void Close()
    {
        // Nothing may be left sitting in a timer: the page behind this one re-reads the stored
        // settings the moment it is told the panel closed, and a pending edit would be invisible
        // to it until the timer happened to fire.
        FlushPendingEdits();

        LocalizationService.LanguageChanged -= OnLanguageChanged;

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();
            Closed?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // DoubleAnimation defaults to FillBehavior.HoldEnd, so the animated properties stay under the
    // animation clock's control long after the animation has visually finished. Handing them back
    // to their owners drops the intermediate composition layer as soon as the transition is over.
    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;

        Card.CacheMode = null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the card must not bubble up to the scrim's dismiss handler
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            TitleText.Text = LocalizationService.Format(
                "S.Settings.ServiceDialogTitle", LanguageData.GetProviderDisplay(_provider));

            var deepL = _provider == TranslationProvider.DeepL;
            DeepLPanel.Visibility = deepL ? Visibility.Visible : Visibility.Collapsed;
            OpenAiPanel.Visibility = deepL ? Visibility.Collapsed : Visibility.Visible;

            // The card is sized for the wider of the two panels, and DeepL is one field. OpenAI's
            // width is set by the prompt library rather than by the fields above it: the list and
            // the prompt it resolves to sit side by side, and the prompt is a paragraph.
            Card.Width = deepL ? 460 : 760;

            ApiKeyBox.Secret = s.ApiKey;

            OpenAiBaseUrlBox.Text = s.OpenAiBaseUrl;
            OpenAiApiKeyBox.Secret = s.OpenAiApiKey;
            OpenAiTimeoutBox.Text = s.OpenAiTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
            LoadPromptLibrary(s);

            // Set here rather than in XAML because the guide has a copy per interface language, and
            // LoadSettings is what runs again when that language changes — see OnLanguageChanged.
            OllamaGuideLink.NavigateUri = new Uri(DocumentationLinks.OllamaGuide);

            UpdateOpenAiFieldChrome();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Re-renders the text this panel composes in code: the title,
    /// the built-in row's name and the label on the add row.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => LoadSettings();

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
    }

    /// <summary>Writes out whatever is still waiting on a debounce timer.</summary>
    private void FlushPendingEdits()
    {
        // Before the debounce below reads it: a number too small that is still being edited when
        // the panel closes is raised to the minimum rather than dropped.
        RaiseTimeoutToMinimum();

        if (_apiKeyDebounce.IsEnabled)
        {
            _apiKeyDebounce.Stop();
            Persist(s => s.ApiKey = ApiKeyBox.Secret.Trim());
        }

        if (_openAiSettingsDebounce.IsEnabled)
        {
            _openAiSettingsDebounce.Stop();
            Persist(s =>
            {
                s.OpenAiBaseUrl = OpenAiBaseUrlBox.Text.Trim();
                s.OpenAiApiKey = OpenAiApiKeyBox.Secret.Trim();
                if (TryReadTimeout(out var timeout)) s.OpenAiTimeoutSeconds = timeout;
            });
        }
    }

    // ── DeepL ────────────────────────────────────────────────────────────────

    private void ApiKeyBox_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _apiKeyDebounce.Stop();
        _apiKeyDebounce.Start();
    }

    // ── OpenAI fields ────────────────────────────────────────────────────────

    private void OpenAiSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Unconditionally: the placeholders answer what an empty box will do, and waiting for the
        // debounce would leave them a beat behind the typing.
        UpdateOpenAiFieldChrome();

        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    /// <summary>
    /// Shows what each empty box falls back to, in place of the empty box.
    /// </summary>
    /// <remarks>
    /// The model box is the one with nothing to fall back to, so it says so rather than naming a
    /// model: a name shown the way the address below shows its default reads as "leave this and it
    /// still works", which is the one thing that is not true of this field.
    /// </remarks>
    private void UpdateOpenAiFieldChrome()
    {
        OpenAiBaseUrlPlaceholder.Text = OpenAiCompatibleProvider.DefaultBaseUrl;
        OpenAiBaseUrlPlaceholder.Visibility =
            OpenAiBaseUrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        OpenAiTimeoutPlaceholder.Text =
            OpenAiCompatibleProvider.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        OpenAiTimeoutPlaceholder.Visibility =
            OpenAiTimeoutBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenAiTimeoutError.Text = TryReadTimeout(out _)
            ? ""
            : LocalizationService.Format("S.Settings.OpenAiTimeoutMin", OpenAiCompatibleProvider.MinTimeoutSeconds);

    }

    // ── OpenAI timeout ───────────────────────────────────────────────────────

    /// <summary>
    /// The timeout box as a setting: true for empty (null, the default) or a whole number at or
    /// above the minimum; false for a number still too small.
    /// </summary>
    private bool TryReadTimeout(out int? seconds)
    {
        seconds = null;
        var text = OpenAiTimeoutBox.Text;
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < OpenAiCompatibleProvider.MinTimeoutSeconds)
            return false;
        seconds = value;
        return true;
    }

    /// <summary>
    /// Replaces a number under the minimum with the minimum. Only once the box is left: while it is
    /// being typed in, "3" is on its way to "30", and correcting it then would fight the typing.
    /// </summary>
    private void RaiseTimeoutToMinimum()
    {
        if (!TryReadTimeout(out _))
            OpenAiTimeoutBox.Text = OpenAiCompatibleProvider.MinTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void OpenAiTimeoutBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !IsDigits(e.Text);

    // Space never reaches PreviewTextInput, so it is stopped here.
    private void OpenAiTimeoutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private void OpenAiTimeoutBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is not string pasted || !IsDigits(pasted))
            e.CancelCommand();
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9');

    private void OpenAiTimeoutBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OpenAiSetting_TextChanged(sender, e);

    private void OpenAiTimeoutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        RaiseTimeoutToMinimum();

    private void OpenAiSecret_SecretChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _openAiSettingsDebounce.Stop();
        _openAiSettingsDebounce.Start();
    }

    // ── Prompt library ───────────────────────────────────────────────────────

    /// <summary>One row of the prompt list, as the markup draws it.</summary>
    /// <remarks>
    /// A row rather than the stored preset itself, because the built-in prompt is a row too and has
    /// no preset behind it: it is the entry with the empty id, which is what the settings file means
    /// by "nothing picked". Properties rather than a record so the markup can bind them by name, and
    /// Visibility rather than the bool it comes from so no converter is needed for either.
    /// </remarks>
    public sealed class PromptPresetRow
    {
        /// <summary>The preset's id, or empty for the built-in row.</summary>
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public bool IsSelected { get; init; }

        /// <summary>The tag after the name: how this setting sends what is on screen.</summary>
        public string ModeLabel { get; init; } = "";

        public bool IsBuiltIn => Id.Length == 0;

        /// <inheritdoc cref="IsBuiltIn"/>
        public Visibility ActionsVisibility => IsBuiltIn ? Visibility.Collapsed : Visibility.Visible;

        /// <inheritdoc cref="IsBuiltIn"/>
        public Visibility BuiltInBadgeVisibility => IsBuiltIn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the list. Also the language-change path, since the built-in row's name and the label
    /// on the add row are localized.
    /// </summary>
    /// <remarks>
    /// Rebuilt whole on every change rather than kept in sync: the list is at most six short rows,
    /// and the alternative is change notification on a collection that only ever changes because
    /// this panel changed it.
    /// </remarks>
    private void LoadPromptLibrary(AppSettings s)
    {
        var profiles = s.OpenAi.Profiles;
        var selectedId = s.OpenAi.SelectedProfileId;

        // An id naming a profile that is no longer there comes up as the built-in row, which is what
        // the provider uses in the same situation. Not corrected in the file here — checking that
        // row writes it back, and nothing reads the stale value in between.
        if (selectedId.Length > 0 && profiles.All(p => p.Id != selectedId)) selectedId = "";

        var rows = new List<PromptPresetRow>
        {
            new()
            {
                Name = LocalizationService.Get("S.Settings.PromptDefaultName"),
                IsSelected = selectedId.Length == 0,
                ModeLabel = ModeLabel(OpenAiCompatibleProvider.BuiltInProfile().Template),
            },
        };

        rows.AddRange(profiles.Select(p => new PromptPresetRow
        {
            Id = p.Id,
            Name = p.Name,
            IsSelected = p.Id == selectedId,
            ModeLabel = ModeLabel(p.Template),
        }));

        PromptPresetList.ItemsSource = rows;

        UpdatePromptChrome();
    }

    /// <summary>Picks the prompt this case sends.</summary>
    /// <remarks>
    /// Also the path a rebuilt list takes when the stored selection comes up checked, so it writes
    /// only when the value actually moved — which is why it needs no guard of its own. The rows are
    /// realized during layout, i.e. after <see cref="_loading"/> has been put back, so a guard on
    /// that flag would not have caught them anyway.
    /// </remarks>
    private void PromptPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton row || row.Tag is not string id) return;

        var openAi = SettingsService.Instance.Current.OpenAi;
        if (openAi.SelectedProfileId != id)
        {
            openAi.SelectedProfileId = id;
            SettingsService.Instance.Save();
        }

        UpdatePromptChrome();
    }

    private void PromptAddButton_Click(object sender, RoutedEventArgs e)
    {
        var profiles = SettingsService.Instance.Current.OpenAi.Profiles;
        if (profiles.Count >= OpenAiSettings.MaxProfiles) return;

        PromptEditor.Open(profile: null, suggestedName: SuggestProfileName(profiles));
    }

    private void PromptEditPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;

        var profile = SettingsService.Instance.Current.OpenAi.Profiles
            .FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        PromptEditor.Open(profile, suggestedName: "");
    }

    /// <summary>
    /// Copies this version's built-in setting into the list as a profile of the user's own.
    /// </summary>
    /// <remarks>
    /// Straight into the same list the Add button writes to, counting against the same cap: the
    /// built-in row is not a slot of its own, and a copy of it is an ordinary setting once it is
    /// made — editable, renameable, deletable. Anything else would mean a second kind of row whose
    /// only difference is where it came from.
    ///
    /// It copies <see cref="OpenAiCompatibleProvider.BuiltInProfile"/> rather than whatever row is
    /// checked. The card beside it is about one thing — the built-in wording being rewritten by
    /// updates — and a copy of an already-saved profile would be a duplicate nobody asked for, under
    /// a name that claims to be the shipped default.
    ///
    /// The selection is deliberately left alone. The new row is byte for byte what is already in use,
    /// so switching to it changes nothing today, and moving someone off the row they picked is not
    /// what a button called 複製 says it will do. It matters after the next update, and by then the
    /// row is in the list to pick.
    /// </remarks>
    private void ProfileSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var openAi = SettingsService.Instance.Current.OpenAi;
        if (openAi.Profiles.Count >= OpenAiSettings.MaxProfiles) return;

        // A fresh object every call, so naming it here cannot write through to anything shared.
        var snapshot = OpenAiCompatibleProvider.BuiltInProfile();
        snapshot.Id = OpenAiSettings.NewId();
        snapshot.Name = SnapshotName(openAi.Profiles);

        openAi.Profiles.Add(snapshot);
        SettingsService.Instance.Save();

        LoadPromptLibrary(SettingsService.Instance.Current);
    }

    /// <summary>
    /// What a copy of the built-in setting is called: the shipped name with this version behind it.
    /// </summary>
    /// <remarks>
    /// The version rather than a number, because that is the thing the copy is preserving — two rows
    /// called 系統預設 v2.2.0 and 系統預設 v2.3.0 say which wording each one holds, which is the
    /// whole question the card raises.
    ///
    /// Pressing twice on one version is allowed rather than blocked, so the suffix exists: someone
    /// who wants a second copy to edit down should not have to rename the first one to get it. The
    /// brackets are half-width in every language — they are punctuation around a number here, not
    /// prose, and the full-width pair the CJK locales would otherwise want sets a visible gap either
    /// side of a name that is already mostly Latin.
    ///
    /// Three digits, so a build number never reaches the name: the file is stamped 2.2.0 and what
    /// distinguishes one built-in wording from the next is the release, not the build it came from.
    /// </remarks>
    private static string SnapshotName(List<OpenAiModelProfile> profiles)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var baseName = LocalizationService.Format("S.Settings.ProfileSnapshotName", version);

        if (!Taken(baseName)) return baseName;

        // Bounded by the cap: this runs only with a free slot, so at most MaxProfiles names can be
        // in the way and one of the numbers tried here is always free.
        for (var n = 1; n <= OpenAiSettings.MaxProfiles; n++)
        {
            var name = $"{baseName} ({n})";
            if (!Taken(name)) return name;
        }

        return baseName;

        bool Taken(string name) => profiles.Any(
            p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// A name for a new prompt that nothing in the list is already called.
    /// </summary>
    /// <remarks>
    /// Numbered from the first free number rather than from the count, so deleting the first of two
    /// and adding another does not suggest the name the remaining one already has. The user is
    /// expected to replace it — it opens selected — and it exists so that saving without thinking
    /// about a name still leaves a list that can be read.
    /// </remarks>
    private static string SuggestProfileName(List<OpenAiModelProfile> profiles)
    {
        for (var n = 1; n <= OpenAiSettings.MaxProfiles + 1; n++)
        {
            var name = LocalizationService.Format("S.Settings.PromptNewName", n);
            if (profiles.All(p => !string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return name;
        }

        // Unreachable while the cap holds: the loop tries one more number than there are slots.
        return LocalizationService.Format("S.Settings.PromptNewName", profiles.Count + 1);
    }

    /// <summary>
    /// Brings the note, the add row and the preview in line with what is on screen.
    /// </summary>
    private void UpdatePromptChrome()
    {
        var openAi = SettingsService.Instance.Current.OpenAi;

        var count = openAi.Profiles.Count;
        PromptAddButton.IsEnabled = count < OpenAiSettings.MaxProfiles;
        PromptAddText.Text = LocalizationService.Format(
            "S.Settings.ProfileAdd", count, OpenAiSettings.MaxProfiles);

        // The copy writes into the same list, so it stops offering on the same cap the Add row does.
        // Greyed rather than hidden: the card's sentence still applies at five settings, and a button
        // that vanishes reads as a feature that went away rather than as a list that is full.
        ProfileSnapshotButton.IsEnabled = count < OpenAiSettings.MaxProfiles;

        // One profile answers every question this pane asks, including which model and which
        // temperature: that is what makes it a 模型設定 rather than a prompt. The built-in one stands
        // in when nothing is picked, and it is a profile of the same shape — see
        // OpenAiCompatibleProvider.BuiltInProfile — so nothing here is a special case.
        var profile = openAi.SelectedProfile() ?? OpenAiCompatibleProvider.BuiltInProfile();

        var model = profile.Model.Trim();
        ProfileModelText.Text = model.Length > 0
            ? model
            : LocalizationService.Get("S.Settings.ModelRequired");
        ProfileModelText.Opacity = model.Length > 0 ? 1 : 0.55;

        // A parameter that is switched off is drawn as 不傳送 rather than left blank or hidden: the
        // reason a model behaves the way it does is as often a field that was left out as a value
        // that was sent, and a row that disappears when it is off cannot be read as either.
        ProfileReasoningEffortText.Text = profile.ReasoningEffortEnabled
            ? profile.ReasoningEffort
            : Off;
        ProfileTemperatureText.Text = Sampling(profile.TemperatureEnabled, profile.Temperature);
        ProfileTopPText.Text = Sampling(profile.TopPEnabled, profile.TopP);
        ProfileSeedText.Text = Sampling(profile.SeedEnabled, profile.Seed);

        // An em dash rather than a phrase for a parameter that is switched off. The four cards are
        // read as a block of values, and a sentence in one of them is longer than the card, pushes
        // the label into ellipsis and stops the block scanning as four of the same thing. A dash is
        // the conventional "no value here", and the same mark in any language.
        static string Sampling(bool enabled, double value) => enabled
            ? value.ToString("0.##", CultureInfo.InvariantCulture)
            : Off;

        // Interim, until the template editor replaces this panel.
        ProfileModeText.Text = ModeLabel(profile.Template);
        if (profile.Template.Trim().Length > 0)
            WriteTemplate(profile.Template);
        else
            WriteExtraInstructions("");
    }

    /// <summary>What a setting's mode tag says, in the list and over the preview alike.</summary>
    private static string ModeLabel(string template) => LocalizationService.Get(
        template.Trim().Length > 0 ? "S.Settings.PromptModePerSegment" : "S.Settings.PromptModeBatch");

    /// <inheritdoc cref="UpdatePromptChrome"/>
    private const string Off = "\u2014";

    /// <summary>
    /// Puts the model name on the clipboard, which is the one value on this pane someone needs
    /// somewhere else — in a terminal, next to `ollama list`.
    /// </summary>
    /// <remarks>
    /// The glyph answers rather than a toast: this panel has no status line of its own, and a message
    /// that has to appear somewhere would have to be dismissed from somewhere. A tick where the copy
    /// icon was is feedback in the place the click happened, and it goes back on its own.
    ///
    /// Nothing is said when the clipboard refuses — another application can hold it open — because
    /// the icon staying as it was is already the honest answer: nothing was copied. The alternative
    /// is an error dialog over a settings panel for a value the reader can select and copy by hand.
    /// </remarks>
    private void ProfileModelCopy_Click(object sender, RoutedEventArgs e)
    {
        var model = ProfileModelText.Text.Trim();
        if (model.Length == 0) return;

        try
        {
            Clipboard.SetText(model);
        }
        catch (Exception)
        {
            return;
        }

        ProfileModelCopyButton.Content = "\uE73E";

        _copyFeedback?.Stop();
        _copyFeedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _copyFeedback.Tick += (_, _) =>
        {
            _copyFeedback?.Stop();
            _copyFeedback = null;
            ProfileModelCopyButton.Content = "\uE8C8";
        };
        _copyFeedback.Start();
    }

    /// <summary>Puts the copy icon back after the tick. Null while no tick is showing.</summary>
    private DispatcherTimer? _copyFeedback;

    /// <summary>
    /// Fills the extra-instructions panel, or shows the line that says only the built-in rules are
    /// sent.
    /// </summary>
    /// <remarks>
    /// Plain text: the placeholders a carried-over prompt may still hold are filled in on the way
    /// out, but they are no longer something this panel teaches, so they are not picked out either.
    /// Normalised first, so a box saved before the editor did that does not draw a CRLF as two
    /// breaks.
    /// </remarks>
    private void WriteExtraInstructions(string text)
    {
        var hasText = text.Trim().Length > 0;

        PromptPanelTitle.Text = LocalizationService.Get("S.Settings.ExtraInstructions");
        ExtraInstructionsText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        ExtraInstructionsEmpty.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ExtraInstructionsText.Text = hasText ? OpenAiSettings.NormaliseLineBreaks(text) : "";
    }

    /// <summary>Fills the same panel with the user's own template.</summary>
    private void WriteTemplate(string template)
    {
        PromptPanelTitle.Text = LocalizationService.Get("S.Settings.PromptModeCustom");
        ExtraInstructionsEmpty.Visibility = Visibility.Collapsed;
        ExtraInstructionsText.Visibility = Visibility.Visible;
        ExtraInstructionsText.Text = OpenAiSettings.NormaliseLineBreaks(template).TrimEnd();
    }

    private void OllamaGuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
