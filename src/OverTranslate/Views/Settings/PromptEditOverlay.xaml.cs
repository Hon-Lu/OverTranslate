using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Settings;

/// <summary>
/// Writes one named model setting — the card that opens over
/// <see cref="ServiceSettingsOverlay"/> when a setting is added or edited.
/// </summary>
/// <remarks>
/// The one surface in 設定 that does not save as it is typed. Everything behind it is a value that
/// takes effect the moment it changes; the template is prose, and half a sentence saved
/// on the way to the whole one is what the model would be sent on the next capture. So this card is
/// confirmed, and cancelling it leaves the stored setting exactly as it was.
///
/// It writes the settings itself rather than handing a result back, because what it changes is a
/// list rather than a field: adding, renaming and deleting are three different edits to it, and
/// the panel behind only ever needs to be told that the list moved.
/// </remarks>
public partial class PromptEditOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>How many lines the template box accepts.</summary>
    private const int PromptMaxLines = 200;

    /// <summary>True while the box is being cut back to the line limit, so its own edit is ignored.</summary>
    private bool _trimming;

    /// <summary>True while the card is being filled in, so initialization never trims or writes.</summary>
    private bool _loading;

    /// <summary>
    /// The preset being edited, or empty while a new one is being written.
    /// </summary>
    /// <remarks>
    /// The id rather than the object, so a save cannot write onto a preset that was removed from
    /// the list while this card was open — it looks the preset up again at the moment it commits.
    /// </remarks>
    private string _editingId = "";

    /// <summary>Whether the setting on this card sends as a batch — the tray beside the template.</summary>
    private bool _batch;

    /// <summary>Raised once the stored prompt library has changed, before the card closes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once the card has gone, saved or not, so the panel behind can take focus back.</summary>
    public event EventHandler? Closed;

    public PromptEditOverlay()
    {
        InitializeComponent();

        // On the control rather than checked on save: a name too long to fit the row it is drawn in
        // should stop being typed, not be silently shortened once it is too late to see.
        NameBox.MaxLength = OpenAiSettings.MaxNameLength;
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    /// <param name="profile">The setting to edit, or null to write a new one.</param>
    /// <param name="suggestedName">
    /// What to call a new setting until the user renames it. Ignored when editing.
    /// </param>
    public void Open(OpenAiModelProfile? profile, string suggestedName)
    {
        _editingId = profile?.Id ?? "";

        _loading = true;
        try
        {
            TitleText.Text = LocalizationService.Get(
                profile is null ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");

            NameBox.Text = profile?.Name ?? suggestedName;

            // An empty template is the application's for the way the setting sends, and opens as its
            // text in the interface's language rather than as an empty box: what is sent is what is
            // on screen.
            var built = OpenAiCompatibleProvider.BuiltInProfile();
            _batch = (profile ?? built).Batch;
            SegmentRadio.IsChecked = !_batch;
            BatchRadio.IsChecked = _batch;
            ShowModeHint();
            _defaultTemplate = OpenAiCompatibleProvider.DefaultTemplate(_batch);
            var template = profile?.Template ?? built.Template;
            TemplateBox.Text = template.Trim().Length == 0
                ? _defaultTemplate
                : OpenAiSettings.NormaliseLineBreaks(template);

            // The model opens on the built-in one like everything else on this card. A new setting is
            // a copy of what ships, and the reset beside this box puts the same name back — a box
            // that opened empty while its own reset filled it in would be two answers to one
            // question. Whoever is here for a different model is replacing a name either way.
            ModelBox.Text = profile?.Model ?? built.Model;

            var sampling = profile ?? built;
            ReasoningEffortEnabledCheckBox.IsChecked = sampling.ReasoningEffortEnabled;
            WriteReasoningEfforts(sampling.ReasoningEffort);
            TemperatureEnabledCheckBox.IsChecked = sampling.TemperatureEnabled;
            TemperatureBox.Text = FormatNumber(sampling.Temperature);
            TopPEnabledCheckBox.IsChecked = sampling.TopPEnabled;
            TopPBox.Text = FormatNumber(sampling.TopP);
            SeedEnabledCheckBox.IsChecked = sampling.SeedEnabled;
            SeedBox.Text = sampling.Seed.ToString(CultureInfo.InvariantCulture);

            // Nothing to delete while the setting does not exist yet.
            DeleteButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;

            // Left open by a card that was closed from inside the confirmation, otherwise.
            ConfirmLayer.Visibility = Visibility.Collapsed;
            ModeConfirmLayer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }

        UpdateAdvancedChrome();
        UpdateChrome();

        Visibility = Visibility.Visible;

        LocalizationService.LanguageChanged += OnLanguageChanged;

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

        // The name is what a new prompt is missing, and selected rather than merely focused so the
        // suggested one is replaced by typing instead of typed around.
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Close()
    {
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

    // See ServiceSettingsOverlay.ReleaseAnimations: HoldEnd would keep these properties under the
    // animation clock, and the composition layer with them, long after the move is over.
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

    /// <summary>
    /// The labels this card composes in code — the title — and the template box while it still
    /// reads as the default: that one is the application's text, which follows the interface. The
    /// other boxes hold what the user typed, in whatever language they typed it.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        TitleText.Text = LocalizationService.Get(
            _editingId.Length == 0 ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");

        // Assigned rather than put in through Refill: nothing of the user's is replaced, and the
        // undo history of a box that was never written in is nothing to keep.
        var wasDefault = IsDefault(TemplateBox.Text);
        _defaultTemplate = OpenAiCompatibleProvider.DefaultTemplate(_batch);
        if (wasDefault) TemplateBox.Text = _defaultTemplate;
        UpdateChrome();

        // Only while the question is on screen; it names the prompt, so it is composed rather than
        // bound and has to be written again in the new language.
        if (ConfirmLayer.Visibility == Visibility.Visible) WriteConfirmMessage();
        if (ModeConfirmLayer.Visibility == Visibility.Visible) WriteModeConfirm();
    }

    /// <summary>
    /// Swallows the click without dismissing. Handled so it does not reach the panel behind, whose
    /// own scrim does close on a click.
    /// </summary>
    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled, or the panel behind this one would take the same Escape and close as well.
        if (e.Key == Key.Escape)
        {
            // The question first: Escape answers the thing on top, and dismissing the card out from
            // under an unanswered confirmation would look like the delete had happened. On the
            // switch's question it is the safe answer, keeping the box as it is.
            if (ConfirmLayer.Visibility == Visibility.Visible) CloseConfirm();
            else if (ModeConfirmLayer.Visibility == Visibility.Visible) CloseModeConfirm();
            else Close();

            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    private void ModelBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The trim below raises this event again for its own edit.
        if (_trimming) return;

        if (!_loading) TrimToLineLimit(TemplateBox);
        UpdateChrome();
    }

    /// <summary>
    /// The application's template for the way the setting sends, in the interface's language, as
    /// the box was last given it — what the box is compared against to tell the default from the
    /// user's own.
    /// </summary>
    private string _defaultTemplate = "";

    /// <summary>Whether the box reads as the default, which is what saving it as empty means.</summary>
    /// <remarks>The same comparison the save makes — see <see cref="OpenAiCompatibleProvider.TemplateToStore"/>.</remarks>
    private bool IsDefault(string text)
    {
        var trimmed = OpenAiSettings.NormaliseLineBreaks(text).Trim();
        return trimmed.Length == 0 || trimmed == _defaultTemplate;
    }

    /// <summary>Puts the application's template for the way the setting sends back, undoably.</summary>
    private void TemplateResetButton_Click(object sender, RoutedEventArgs e)
    {
        Refill(TemplateBox, _defaultTemplate);
        UpdateChrome();
    }

    // ── 翻譯方式 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches the way the setting sends, and the template with it while the box is the default:
    /// the other way's default goes in without a question. A template of the user's own is asked
    /// about — see <see cref="OpenAiCompatibleProvider.TemplateOnSwitch"/>.
    /// </summary>
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // Ahead of the guard: the marker and the hint follow the choice when it is loaded too.
        MoveModeThumb(animate: !_loading);
        ShowModeHint();
        if (_loading) return;

        var toBatch = ReferenceEquals(sender, BatchRadio);
        if (toBatch == _batch) return;

        var next = OpenAiCompatibleProvider.TemplateOnSwitch(TemplateBox.Text, toBatch);
        _batch = toBatch;
        _defaultTemplate = OpenAiCompatibleProvider.DefaultTemplate(_batch);

        if (next is not null)
        {
            // Assigned rather than put in through Refill, as on a language change: nothing of the
            // user's is replaced.
            TemplateBox.Text = next;
            UpdateChrome();
            return;
        }

        UpdateChrome();
        WriteModeConfirm();
        ModeConfirmLayer.Visibility = Visibility.Visible;
        ModeKeepButton.Focus();
    }

    /// <summary>The question, naming the way switched to.</summary>
    private void WriteModeConfirm() =>
        ModeConfirmMessage.Text = LocalizationService.Get(
            _batch ? "S.Settings.PromptModeSwitchToBatch" : "S.Settings.PromptModeSwitchToSegment");

    private void CloseModeConfirm() => ModeConfirmLayer.Visibility = Visibility.Collapsed;

    /// <summary>Leaves the box as the user wrote it; the way stays switched.</summary>
    private void ModeKeepButton_Click(object sender, RoutedEventArgs e) => CloseModeConfirm();

    /// <summary>Puts the new way's default in, undoably: Ctrl+Z brings the user's text back.</summary>
    private void ModeUseDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        CloseModeConfirm();
        Refill(TemplateBox, OpenAiCompatibleProvider.TemplateAfterAnswer(TemplateBox.Text, _batch, useDefault: true));
        UpdateChrome();
    }

    /// <summary>Shows what the chosen way does, whatever the question about the template is answered.</summary>
    private void ShowModeHint()
    {
        var batch = BatchRadio.IsChecked == true;
        ModeSegmentHint.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
        ModeBatchHint.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts the marker under the chosen half, at the speed of the other trays.</summary>
    private void MoveModeThumb(bool animate)
    {
        var target = BatchRadio.IsChecked == true ? ModeThumb.ActualWidth : 0;
        if (!animate || ModeThumb.ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            ModeThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            ModeThumbShift.X = target;
            return;
        }

        ModeThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Places the marker again once the halves have a width, without animating.</summary>
    private void ModeThumb_SizeChanged(object sender, SizeChangedEventArgs e) => MoveModeThumb(animate: false);

    private void ParamsToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = ParamsContent.Visibility != Visibility.Visible;
        ParamsContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ParamsChevronRotation.Angle = open ? 180 : 0;
    }

    /// <summary>Puts the text in the box, to write over or to write from.</summary>
    /// <remarks>
    /// Through the selection rather than by assigning Text, which would throw away the undo history:
    /// this replaces something the user wrote, and Ctrl+Z getting it back is what makes a
    /// confirmation unnecessary.
    /// </remarks>
    private static void Refill(TextBox box, string text)
    {
        box.SelectAll();
        box.SelectedText = text;
        box.Focus();
    }

    /// <summary>Puts the model the guide recommends in the box.</summary>
    /// <remarks>
    /// A suggestion rather than a fallback, which is the difference that matters here: leaving the
    /// box empty is still a configuration error and still refuses to send, and this name only ever
    /// arrives because somebody asked for it. It is the one the Ollama guide tells the user to pull.
    /// </remarks>
    private void ModelResetButton_Click(object sender, RoutedEventArgs e)
    {
        Refill(ModelBox, OpenAiCompatibleProvider.RecommendedModel);
        UpdateChrome();
    }

    /// <summary>
    /// Brings the save button in line with what is on screen: a setting needs a name to be picked
    /// out of the list by and a model to send to, and neither can be supplied later.
    /// </summary>
    /// <remarks>
    /// The template is not asked for. Empty is the application's template, which is a complete
    /// request — not a setting someone forgot to finish.
    /// </remarks>
    private void UpdateChrome()
    {
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelPlaceholder.Visibility = ModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        TemplateResetButton.IsEnabled = !IsDefault(TemplateBox.Text);

        SaveButton.IsEnabled = NameBox.Text.Trim().Length > 0 && ModelBox.Text.Trim().Length > 0;
    }

    /// <summary>
    /// Drops anything past <see cref="PromptMaxLines"/> lines, silently.
    /// </summary>
    /// <remarks>
    /// A cap on the input rather than a check further in: the instruction goes into every request,
    /// so a pasted document is a real cost repeated on each one, and the place to
    /// stop it is where it arrives. Nothing is said about it — the box visibly refuses to grow,
    /// which is the whole message, and a warning about a limit nobody reaches by writing an
    /// instruction would only be in the way.
    ///
    /// Removed through the selection so the paste stays undoable; the trim is then simply applied
    /// again if the undone text is still too long.
    /// </remarks>
    private void TrimToLineLimit(TextBox box)
    {
        var overflow = LineLimitOverflowIndex(box.Text, PromptMaxLines);
        if (overflow < 0) return;

        _trimming = true;
        try
        {
            box.Select(overflow, box.Text.Length - overflow);
            box.SelectedText = "";
            box.CaretIndex = overflow;
        }
        finally
        {
            _trimming = false;
        }
    }

    /// <summary>
    /// Where the text passes <paramref name="maxLines"/> lines, or -1 when it does not.
    /// </summary>
    /// <remarks>
    /// Hard line breaks only. <see cref="TextBox.LineCount"/> counts the lines actually drawn, so
    /// with wrapping on it would make the cap depend on how wide the window happens to be.
    /// </remarks>
    internal static int LineLimitOverflowIndex(string text, int maxLines)
    {
        var index = -1;
        for (var line = 0; line < maxLines; line++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0) return -1;
        }

        // Cut before the break that would have started the next line, and before the carriage
        // return in front of it, so the kept text does not end on a half of a CRLF pair.
        return index > 0 && text[index - 1] == '\r' ? index - 1 : index;
    }



    // ── Advanced fold ───────────────────────────────────────────────

    /// <summary>How long the advanced section takes to open or close.</summary>
    private static readonly Duration AdvancedDuration = TimeSpan.FromMilliseconds(180);

    private bool _advancedExpanded;

    /// <summary>
    /// Which open/close is current, so a run that is replaced part way through does not then finish
    /// and hand the section a height belonging to the state it was leaving.
    /// </summary>
    private int _advancedTransition;

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e) =>
        SetAdvancedExpanded(!_advancedExpanded);

    /// <remarks>
    /// The height is animated from the content's measured height rather than from a number written
    /// here, and handed back to Auto once open: the sentences inside are localized and wrap against
    /// the card's width, so today's measurement is not tomorrow's.
    /// </remarks>
    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        var transition = ++_advancedTransition;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        AdvancedChevronRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(expanded ? 180 : 0, AdvancedDuration) { EasingFunction = ease });

        // Enabled for the whole of the opening move, and only switched off once the closing one has
        // finished - closed, its content has to be out of the tab order as well as out of sight,
        // which a zero height alone would not manage.
        if (expanded) AdvancedHost.IsEnabled = true;

        var to = 0d;
        if (expanded)
        {
            AdvancedContent.Measure(new Size(
                AdvancedHost.ActualWidth > 0 ? AdvancedHost.ActualWidth : double.PositiveInfinity,
                double.PositiveInfinity));
            to = AdvancedContent.DesiredSize.Height;
        }

        var height = new DoubleAnimation(AdvancedHost.ActualHeight, to, AdvancedDuration)
        {
            EasingFunction = ease
        };
        height.Completed += (_, _) =>
        {
            if (transition != _advancedTransition) return;

            AdvancedHost.BeginAnimation(HeightProperty, null);
            if (_advancedExpanded)
            {
                AdvancedHost.Height = double.NaN;
            }
            else
            {
                AdvancedHost.Height = 0;
                AdvancedHost.IsEnabled = false;
            }
        };

        AdvancedHost.BeginAnimation(HeightProperty, height);
        AdvancedHost.BeginAnimation(
            OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, AdvancedDuration) { EasingFunction = ease });
    }

    // ── Advanced parameters ──────────────────────────────────────────────────

    /// <summary>Widest value any of these APIs accepts; the field is clamped to it.</summary>
    /// <remarks>
    /// Clamped rather than refused, because a number outside the range is a typo rather than a
    /// decision — and a card that refused to save over one would leave the reader hunting for it
    /// behind a fold.
    /// </remarks>
    private const double MaxTemperature = 2;

    /// <inheritdoc cref="MaxTemperature"/>
    private const double MaxTopP = 1;

    /// <remarks>
    /// One handler for all four rows rather than one each: what they do is identical, and the
    /// difference between them is which box is disabled, which is read off the sender's own row.
    ///
    /// Moved here from the panel with the model name they belong beside. They were in an 進階 fold at
    /// the top of the page, which made them look like preferences of the application's — they are
    /// numbers a model's own documentation asks for, and they change when the model does.
    /// </remarks>
    private void AdvancedParam_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateAdvancedChrome();
        UpdateChrome();
    }

    /// <inheritdoc cref="AdvancedParam_Toggled"/>
    private void AdvancedParam_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAdvancedChrome();
        UpdateChrome();
    }

    /// <summary>
    /// Puts the box back into the shape the value is stored in once the caret leaves, so a half-typed
    /// "1." is not what the card carries away.
    /// </summary>
    private void TemperatureBox_LostFocus(object sender, RoutedEventArgs e) =>
        Settle(TemperatureBox, FormatNumber(ReadNumber(TemperatureBox, MaxTemperature)));

    /// <inheritdoc cref="TemperatureBox_LostFocus"/>
    private void TopPBox_LostFocus(object sender, RoutedEventArgs e) =>
        Settle(TopPBox, FormatNumber(ReadNumber(TopPBox, MaxTopP)));

    /// <inheritdoc cref="TemperatureBox_LostFocus"/>
    private void SeedBox_LostFocus(object sender, RoutedEventArgs e) =>
        Settle(SeedBox, ReadSeed().ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc cref="TemperatureBox_LostFocus"/>
    private void Settle(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
        UpdateAdvancedChrome();
    }

    /// <summary>What is in one box, clamped, or 0 when it is not a number at all.</summary>
    /// <remarks>
    /// Both cultures are tried, in that order. The value is stored and sent as invariant, but the
    /// separator someone types is the one their keyboard and their locale give them.
    /// </remarks>
    private static double ReadNumber(TextBox box, double max)
    {
        var text = box.Text.Trim();
        if (text.Length == 0) return 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return 0;

        return Math.Clamp(value, 0, max);
    }

    /// <summary>What is in the seed box, or 0 when it is not a whole number at all.</summary>
    /// <remarks>
    /// Parsed as a long and clamped down, so pasting something longer than an int is a number at the
    /// end of the range rather than an overflow. Negative seeds are not offered: the servers this
    /// talks to disagree about them, and there is nothing a negative one can express that a positive
    /// one cannot.
    /// </remarks>
    private int ReadSeed()
    {
        var text = SeedBox.Text.Trim();
        if (text.Length == 0) return 0;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return 0;

        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One entry of the Reasoning Effort list, as the shared combo box template draws it.</summary>
    public sealed record ReasoningEffortOption(string Display)
    {
        /// <summary>The value itself: what a screen reader names the entry by.</summary>
        public override string ToString() => Display;
    }

    /// <summary>
    /// Fills the Reasoning Effort list and picks <paramref name="stored"/> in it.
    /// </summary>
    /// <remarks>
    /// A value that is not one of the five — written into the settings file by hand — is added to
    /// the end of the list for this card rather than replaced. It is sent as it is, so the card shows
    /// what is sent, and saving without touching the row keeps it.
    /// </remarks>
    private void WriteReasoningEfforts(string stored)
    {
        var options = OpenAiModelProfile.ReasoningEfforts.Select(value => new ReasoningEffortOption(value)).ToList();
        var selected = options.FindIndex(option => option.Display == stored);
        if (selected < 0 && stored.Trim().Length > 0)
        {
            options.Add(new ReasoningEffortOption(stored));
            selected = options.Count - 1;
        }

        ReasoningEffortBox.ItemsSource = options;
        ReasoningEffortBox.SelectedIndex = Math.Max(selected, 0);
    }

    /// <summary>What the Reasoning Effort list has picked.</summary>
    private string ReadReasoningEffort() =>
        (ReasoningEffortBox.SelectedItem as ReasoningEffortOption)?.Display
        ?? OpenAiModelProfile.DefaultReasoningEffort;

    /// <summary>
    /// Brings the four rows in line with what is on screen: nothing to type into while a parameter
    /// is not being sent, and its range greyed with it.
    /// </summary>
    private void UpdateAdvancedChrome()
    {
        // The list keeps its value while switched off, the way the boxes below keep theirs: turning
        // it on is then the one click that sends the pre-filled none.
        ReasoningEffortBox.IsEnabled = ReasoningEffortEnabledCheckBox.IsChecked == true;

        Row(TemperatureEnabledCheckBox, TemperatureBox, TemperatureRangeHint);
        Row(TopPEnabledCheckBox, TopPBox, TopPRangeHint);
        Row(SeedEnabledCheckBox, SeedBox, SeedRangeHint);

        static void Row(CheckBox enabled, TextBox box, TextBlock range)
        {
            var on = enabled.IsChecked == true;
            box.IsEnabled = on;
            range.Opacity = on ? 1 : 0.45;
        }
    }

    // ── Committing ───────────────────────────────────────────────────────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        if (name.Length == 0 || model.Length == 0) return;

        var openAi = SettingsService.Instance.Current.OpenAi;

        // Looked up again rather than held: the list is the stored one, and this card has been open
        // for as long as someone took to write a paragraph.
        var profile = openAi.Profiles.FirstOrDefault(p => p.Id == _editingId);
        if (profile is null)
        {
            // The panel does not offer to add past the cap, so this only fires if the list filled up
            // behind this card. Closing without writing is the honest answer.
            if (openAi.Profiles.Count >= OpenAiSettings.MaxProfiles)
            {
                Close();
                return;
            }

            profile = new OpenAiModelProfile { Id = OpenAiSettings.NewId() };
            openAi.Profiles.Add(profile);

            // A setting someone just wrote is a setting they want used. Adding one and then having
            // to pick it in the list would make the first half of that gesture do nothing.
            openAi.SelectedProfileId = profile.Id;
        }

        profile.Name = name;
        profile.Model = model;
        profile.ReasoningEffortEnabled = ReasoningEffortEnabledCheckBox.IsChecked == true;
        profile.ReasoningEffort = ReadReasoningEffort();
        profile.TemperatureEnabled = TemperatureEnabledCheckBox.IsChecked == true;
        profile.Temperature = ReadNumber(TemperatureBox, MaxTemperature);
        profile.TopPEnabled = TopPEnabledCheckBox.IsChecked == true;
        profile.TopP = ReadNumber(TopPBox, MaxTopP);
        profile.SeedEnabled = SeedEnabledCheckBox.IsChecked == true;
        profile.Seed = ReadSeed();
        profile.Batch = _batch;
        profile.Template = OpenAiCompatibleProvider.TemplateToStore(TemplateBox.Text, _batch);

        Commit();
    }

    /// <summary>
    /// Asks before deleting — the one confirmation in the application.
    /// </summary>
    /// <remarks>
    /// Every other destructive gesture here is undoable (Ctrl+Z in a box) or costs a setting that is
    /// one click to restore. This one throws away prose someone wrote, with nothing to get it back,
    /// from a button that shares a row with 取消 and 儲存.
    /// </remarks>
    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        WriteConfirmMessage();
        ConfirmLayer.Visibility = Visibility.Visible;

        // The safe half takes focus, so Enter or Space arriving from the click that opened this
        // lands on 取消 rather than on the button that deletes.
        ConfirmCancelButton.Focus();
    }

    /// <summary>Names the prompt in the question, so the reader is not asked about "the prompt".</summary>
    private void WriteConfirmMessage()
    {
        // The box rather than the stored preset: a rename typed but not yet saved is still what the
        // user is looking at, and asking about the old name would be asking about something else.
        ConfirmMessage.Text = LocalizationService.Format(
            "S.Settings.PromptDeleteConfirm", NameBox.Text.Trim());
    }

    private void CloseConfirm() => ConfirmLayer.Visibility = Visibility.Collapsed;

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e) => CloseConfirm();

    /// <summary>Swallows the click. Dismissing on it would answer the question by accident.</summary>
    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirm();

        var openAi = SettingsService.Instance.Current.OpenAi;
        openAi.Profiles.RemoveAll(p => p.Id == _editingId);

        // Back to the built-in setting rather than to whichever profile happens to be next in the
        // list: the provider resolves an id that names nothing the same way, and a silent move onto
        // a neighbouring model would change what gets sent without saying so.
        if (openAi.SelectedProfileId == _editingId) openAi.SelectedProfileId = "";

        Commit();
    }

    private void Commit()
    {
        SettingsService.Instance.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
