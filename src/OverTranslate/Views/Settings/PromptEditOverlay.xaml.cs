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
/// Writes one named prompt — the card that opens over
/// <see cref="ServiceSettingsOverlay"/> when a prompt is added or edited.
/// </summary>
/// <remarks>
/// The one surface in 設定 that does not save as it is typed. Everything behind it is a value that
/// takes effect the moment it changes; a prompt is prose, and half a sentence saved on the way to
/// the whole one is what the model would be sent on the next capture. So this card is confirmed,
/// and cancelling it leaves the stored prompt exactly as it was.
///
/// It writes the settings itself rather than handing a result back, because what it changes is a
/// list rather than a field: adding, renaming and deleting are three different edits to it, and
/// the panel behind only ever needs to be told that the list moved.
/// </remarks>
public partial class PromptEditOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>How many lines of prompt the box accepts.</summary>
    private const int PromptMaxLines = 200;

    /// <summary>True while the box is being cut back to the line limit, so its own edit is ignored.</summary>
    private bool _trimming;

    /// <summary>True while the card is being filled in, so initialization never trims or writes.</summary>
    private bool _loading;

    /// <summary>Which of the two lists this prompt belongs to.</summary>
    private bool _automatic;

    /// <summary>
    /// The preset being edited, or empty while a new one is being written.
    /// </summary>
    /// <remarks>
    /// The id rather than the object, so a save cannot write onto a preset that was removed from
    /// the list while this card was open — it looks the preset up again at the moment it commits.
    /// </remarks>
    private string _editingId = "";

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
    public void Open(bool automatic, OpenAiModelProfile? profile, string suggestedName)
    {
        _automatic = automatic;
        _editingId = profile?.Id ?? "";

        _loading = true;
        try
        {
            TitleText.Text = LocalizationService.Get(
                profile is null ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");
            ScopeHint.Text = LocalizationService.Get(
                automatic ? "S.Settings.PromptAutoScope" : "S.Settings.PromptExplicitScope");

            NameBox.Text = profile?.Name ?? suggestedName;

            // A new setting opens on the built-in wording rather than on empty boxes: it is the one
            // worked example of these parameters there is, and most edits are a sentence away from
            // it rather than a page of prose from nothing. Both pairs, because one saved setting
            // holds both.
            var built = OpenAiCompatibleProvider.BuiltInProfile();
            WritePair(AutoSystemBox, AutoUserBox, profile?.Auto ?? built.Auto);
            WritePair(ExplicitSystemBox, ExplicitUserBox, profile?.Explicit ?? built.Explicit);

            // The model opens on the built-in one like everything else on this card. A new setting is
            // a copy of what ships, and the reset beside this box puts the same name back — a box
            // that opened empty while its own reset filled it in would be two answers to one
            // question. Whoever is here for a different model is replacing a name either way.
            ModelBox.Text = profile?.Model ?? built.Model;

            var sampling = profile ?? built;
            TemperatureEnabledCheckBox.IsChecked = sampling.TemperatureEnabled;
            TemperatureBox.Text = FormatNumber(sampling.Temperature);
            TopPEnabledCheckBox.IsChecked = sampling.TopPEnabled;
            TopPBox.Text = FormatNumber(sampling.TopP);
            SeedEnabledCheckBox.IsChecked = sampling.SeedEnabled;
            SeedBox.Text = sampling.Seed.ToString(CultureInfo.InvariantCulture);

            if (automatic) PromptAutoTab.IsChecked = true;
            else PromptExplicitTab.IsChecked = true;

            // Nothing to delete while the setting does not exist yet.
            DeleteButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;

            // Left open by a card that was closed from inside the confirmation, otherwise.
            ConfirmLayer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }

        UpdateAdvancedChrome();
        UpdatePromptTabChrome();
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
    /// Only the labels this card composes in code — the title and the scope note. The boxes hold
    /// what the user typed, in whatever language they typed it.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        TitleText.Text = LocalizationService.Get(
            _editingId.Length == 0 ? "S.Settings.PromptAddTitle" : "S.Settings.PromptEditTitle");
        ScopeHint.Text = LocalizationService.Get(
            _automatic ? "S.Settings.PromptAutoScope" : "S.Settings.PromptExplicitScope");

        // Only while the question is on screen; it names the prompt, so it is composed rather than
        // bound and has to be written again in the new language.
        if (ConfirmLayer.Visibility == Visibility.Visible) WriteConfirmMessage();
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
            // under an unanswered confirmation would look like the delete had happened.
            if (ConfirmLayer.Visibility == Visibility.Visible) CloseConfirm();
            else Close();

            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    /// <summary>
    /// Swaps which of the two prompt pairs is on screen. Nothing else on the card moves: the model
    /// and the temperature belong to the setting, not to one of its two cases.
    /// </summary>
    private void PromptTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && _loading) return;

        _automatic = PromptAutoTab.IsChecked == true;
        UpdatePromptTabChrome();
    }

    private void UpdatePromptTabChrome()
    {
        AutoPromptPair.Visibility = _automatic ? Visibility.Visible : Visibility.Collapsed;
        ExplicitPromptPair.Visibility = _automatic ? Visibility.Collapsed : Visibility.Visible;

        // The sentence under the switch is what the switch changes, so it is written again here
        // rather than left as a general note about both cases.
        ScopeHint.Text = LocalizationService.Get(
            _automatic ? "S.Settings.PromptAutoScope" : "S.Settings.PromptExplicitScope");

        MovePromptSourceThumb(animate: !_loading);

        // Which pair is on screen decides which of the two messages the rule line carries.
        if (!_loading) UpdateChrome();

        // 自動 has no source language, so the two rows describing one would be listing parameters
        // that resolve to nothing. Hidden whole rather than left showing an empty example.
        var sourceRows = _automatic ? Visibility.Collapsed : Visibility.Visible;
        ParamRowSourceName.Visibility = sourceRows;
        ParamRowSourceCode.Visibility = sourceRows;
    }

    private void ModelBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    /// <remarks>
    /// The cap is per box rather than across the pair. Each is sent as one message, and a limit
    /// counted over both would make room in one half depend on what is in the other — which is
    /// invisible from the box being typed in.
    /// </remarks>
    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;

        // All four, not just the User halves: either one may be the empty one, because the card asks
        // for one of the two rather than for System with User optional.
        AutoSystemPlaceholder.Visibility = Placeholder(AutoSystemBox);
        AutoUserPlaceholder.Visibility = Placeholder(AutoUserBox);
        ExplicitSystemPlaceholder.Visibility = Placeholder(ExplicitSystemBox);
        ExplicitUserPlaceholder.Visibility = Placeholder(ExplicitUserBox);

        // The trim below raises this event again for its own edit.
        if (_trimming) return;

        if (!_loading) TrimToLineLimit(box);

        UpdateChrome();
    }

    /// <summary>Fills one pair's boxes from a stored pair.</summary>
    private static void WritePair(TextBox system, TextBox user, OpenAiPromptPair prompts)
    {
        system.Text = prompts.SystemPrompt;
        user.Text = prompts.UserPrompt;
    }

    /// <remarks>
    /// The built-in setting sends no system message, so this empties the box rather than filling it.
    /// It is the same action as the one on the User box — "put back what the app ships with" — and a
    /// button missing on one of four otherwise identical panels reads as an oversight.
    /// </remarks>
    private void AutoSystemDefault_Click(object sender, RoutedEventArgs e) =>
        Refill(AutoSystemBox, OpenAiCompatibleProvider.DefaultSystemPrompt);

    private void ExplicitSystemDefault_Click(object sender, RoutedEventArgs e) =>
        Refill(ExplicitSystemBox, OpenAiCompatibleProvider.DefaultSystemPrompt);

    private void AutoUserDefault_Click(object sender, RoutedEventArgs e) =>
        Refill(AutoUserBox, OpenAiCompatibleProvider.DefaultUserPrompt());

    private void ExplicitUserDefault_Click(object sender, RoutedEventArgs e) =>
        Refill(ExplicitUserBox, OpenAiCompatibleProvider.DefaultUserPrompt());

    /// <summary>Puts the built-in wording back in one box, to write over or to write from.</summary>
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
    /// Brings the save button in line with what is on screen: a prompt needs a name to be picked
    /// out of the list by and a sentence to send, and neither can be supplied later.
    /// </summary>
    private void UpdateChrome()
    {
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelPlaceholder.Visibility = ModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // A name to be picked out of the list by, a model to send to, and something to send. The
        // prompt is one of the two halves rather than both: which one a model wants its prompt in is
        // the model's business, and a card that demanded both would be asking the user to pad out a
        // message their model expects to be empty.
        // Each case has to have something to send, not just one of the two: both are used, and a
        // setting whose 指定 half is empty would send an empty prompt the moment someone picked a
        // source language. Checked across both pairs rather than the visible one, so the button does
        // not go live because the incomplete half happens to be off screen.
        var autoOk = HasPrompt(AutoSystemBox, AutoUserBox);
        var explicitOk = HasPrompt(ExplicitSystemBox, ExplicitUserBox);

        SaveButton.IsEnabled =
            NameBox.Text.Trim().Length > 0 &&
            ModelBox.Text.Trim().Length > 0 &&
            autoOk && explicitOk;

        UpdatePromptRule(autoOk, explicitOk);
    }

    /// <summary>
    /// Says which prompt is missing, or nothing at all while none is.
    /// </summary>
    /// <remarks>
    /// The pair on screen first: that is the one the reader can act on without moving. Naming the
    /// other tab is the case that would otherwise be unexplainable — the boxes in front of them are
    /// filled in, and 儲存 is off for a reason on a page they are not looking at.
    /// </remarks>
    private void UpdatePromptRule(bool autoOk, bool explicitOk)
    {
        var visibleOk = _automatic ? autoOk : explicitOk;
        var otherOk = _automatic ? explicitOk : autoOk;

        if (!visibleOk)
        {
            PromptRuleText.Text = LocalizationService.Get("S.Settings.PromptNeedOne");
            PromptRuleRow.Visibility = Visibility.Visible;
            return;
        }

        if (!otherOk)
        {
            PromptRuleText.Text = LocalizationService.Format(
                "S.Settings.PromptOtherEmpty",
                LocalizationService.Get(_automatic
                    ? "S.Settings.PromptSourceExplicit"
                    : "S.Settings.PromptSourceAuto"));
            PromptRuleRow.Visibility = Visibility.Visible;
            return;
        }

        PromptRuleRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Whether one case has a prompt at all — one of the two halves, not both.
    /// </summary>
    /// <remarks>
    /// Which half a model wants its prompt in is the model's business, and a card that demanded both
    /// would be asking the user to pad out a message their model expects to be empty.
    /// </remarks>
    private static Visibility Placeholder(TextBox box) =>
        box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static bool HasPrompt(TextBox system, TextBox user) =>
        system.Text.Trim().Length > 0 || user.Text.Trim().Length > 0;

    // ── The source-language switch ───────────────────────────────────────────

    /// <summary>
    /// The 偵錯工具 card's marker, so the same control does not travel at two speeds in one app.
    /// </summary>
    private static readonly Duration PromptSourceSlide = new(TimeSpan.FromMilliseconds(220));

    /// <summary>
    /// Puts the marker under the chosen half.
    /// </summary>
    /// <remarks>
    /// The travel is the marker's own width, because the two halves share a column size. Measured
    /// rather than fixed: the halves are sized to the longer of two labels, which is a different
    /// number in every language.
    /// </remarks>
    private void MovePromptSourceThumb(bool animate)
    {
        var target = _automatic ? 0 : PromptSourceThumb.ActualWidth;

        // Before the tray has been laid out there is no distance to travel and nothing to see; the
        // marker's own SizeChanged runs this again once the shared columns have their final width.
        if (!animate || PromptSourceThumb.ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            PromptSourceThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            PromptSourceThumbShift.X = target;
            return;
        }

        PromptSourceThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, PromptSourceSlide)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>
    /// The half's width is not known until the card has been opened once and laid out, so the marker
    /// is placed again whenever that number arrives or changes — without animating, since this is
    /// the switch being measured rather than the choice being changed.
    /// </summary>
    private void PromptSourceThumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MovePromptSourceThumb(animate: false);

    /// <summary>
    /// Drops anything past <see cref="PromptMaxLines"/> lines, silently.
    /// </summary>
    /// <remarks>
    /// A cap on the input rather than a check further in: the prompt is sent once per recognised
    /// block, so a pasted document is a real cost repeated a dozen times over, and the place to
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
    /// One handler for all three rows rather than one each: what they do is identical, and the
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

    /// <summary>
    /// Brings the three rows in line with what is on screen: nothing to type into while a parameter
    /// is not being sent, and its range greyed with it.
    /// </summary>
    private void UpdateAdvancedChrome()
    {
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
        if (name.Length == 0 || model.Length == 0 || !HasPrompt(AutoSystemBox, AutoUserBox) ||
            !HasPrompt(ExplicitSystemBox, ExplicitUserBox)) return;

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
        profile.TemperatureEnabled = TemperatureEnabledCheckBox.IsChecked == true;
        profile.Temperature = ReadNumber(TemperatureBox, MaxTemperature);
        profile.TopPEnabled = TopPEnabledCheckBox.IsChecked == true;
        profile.TopP = ReadNumber(TopPBox, MaxTopP);
        profile.SeedEnabled = SeedEnabledCheckBox.IsChecked == true;
        profile.Seed = ReadSeed();
        profile.Auto = ReadPair(AutoSystemBox, AutoUserBox);
        profile.Explicit = ReadPair(ExplicitSystemBox, ExplicitUserBox);

        Commit();
    }

    /// <summary>What one pair's boxes hold, in the form the settings file keeps.</summary>
    /// <remarks>
    /// Normalised but not trimmed. The blank line between the user prompt and the text being
    /// translated is the end of the wording rather than something the provider adds — see
    /// OpenAiChatTranslator.BuildMessages — so trimming here would delete the separator every
    /// time someone opened a setting and saved it again, and the symptom would be a model that
    /// gradually got worse with no edit anyone made on purpose.
    ///
    /// A box holding nothing but whitespace is still stored as empty: that is what decides whether
    /// the message is sent at all, and a space is not an instruction.
    /// </remarks>
    private static OpenAiPromptPair ReadPair(TextBox system, TextBox user) => new()
    {
        SystemPrompt = Stored(system),
        UserPrompt = Stored(user),
    };

    /// <inheritdoc cref="ReadPair"/>
    private static string Stored(TextBox box)
    {
        var text = OpenAiSettings.NormaliseLineBreaks(box.Text);
        return text.Trim().Length == 0 ? "" : text;
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
