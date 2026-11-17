using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The 模式 card on the 即時翻譯 page: the list of modes on the left, the values of the one selected
/// on the right — read-only for a preset, editable for a custom mode.
/// </summary>
/// <remarks>
/// <para>Saved as it is changed, like the rest of this page, with no 儲存 button, and never locked
/// while a session runs: <see cref="SettingsService.UpdateRealtimeProfiles"/> tells a running session,
/// which puts its blocks on the new values at once. That is also why a slider writes only when it is
/// let go of (or, from the keyboard, once the keys have rested for <see cref="KeyboardSettle"/>): every
/// write that changes something makes a running session read every block again.</para>
///
/// <para>What is shown for a value is the mode as it reads — <see cref="RealtimeProfile.Filled"/> —
/// so a value out of range in the file is shown held to the range and the file is left as it is.
/// What is written is only what differs from the preset — see
/// <see cref="RealtimeProfileEditing.WithoutTemplateValues"/> — so 已修改 means different.</para>
///
/// <para>The value rows are built in code from one recipe (<see cref="BuildRow"/>) rather than
/// written out ten times: every row is the same grid of name, tags, control and reading, and the
/// differences are which control and how its reading is worded.</para>
/// </remarks>
public partial class RealtimeProfilesCard : UserControl
{
    /// <summary>How long arrow keys on a slider must rest before the value is written.</summary>
    private static readonly TimeSpan KeyboardSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Narrower than this, the list goes above the values instead of beside them: the values need
    /// about 480 for a name, a slider and its reading on one line. A floor only — wider where the
    /// controls need it, see <see cref="UpdateColumns"/>.
    /// </summary>
    private const double TwoColumnMinWidth = 700;

    /// <summary>
    /// The room a row keeps between its name and the controls on its right; less than this and the
    /// names go above the controls — see <see cref="ApplyStacking"/>.
    /// </summary>
    private const double NameGap = 8;

    /// <summary>The margin either way of the stacking threshold, as for the columns.</summary>
    private const double StackingHysteresis = 24;

    private readonly Dictionary<ProfileParam, ParamRow> _rows = [];
    private readonly DispatcherTimer _settleTimer = new() { Interval = KeyboardSettle };
    private ParamRow? _pendingRow;

    // The last value a 自動 slider was set to by hand, per mode and value, so ticking 自動 and
    // unticking it again gives back what the user had rather than the automatic value.
    private readonly Dictionary<(string, ProfileParam), double> _lastManual = [];

    private string _selectedId = RealtimePresets.StandardId;
    private bool _loaded;
    private bool _syncing;
    private bool _advancedExpanded;
    private bool _oneColumn;
    private bool _stacked;
    private string _nameShown = "";

    public RealtimeProfilesCard()
    {
        InitializeComponent();
        BuildRows();
        // The room the rows are given, which a row that does not fit is wider than.
        Detail.SizeChanged += (_, _) =>
        {
            ApplyStacking();
            UpdateColumns(Body.ActualWidth);
        };

        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            FlushPending();
        };

        LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => FlushPending();

        Reload();
    }

    /// <summary>Re-reads the modes and shows them, keeping the selection where it still exists.</summary>
    /// <remarks>The first time, the selection is the mode the last block was set to.</remarks>
    public void Reload()
    {
        var realtime = SettingsService.Instance.Current.Realtime;
        if (!_loaded)
        {
            _selectedId = realtime.NewBlockProfileOrStandard();
            _loaded = true;
        }
        _selectedId = realtime.KnownProfileId(_selectedId);
        Render();
    }

    /// <summary>Selects <paramref name="profileId"/> and scrolls the card into view — 管理模式….</summary>
    public void ShowProfile(string profileId)
    {
        FlushPending();
        _selectedId = SettingsService.Instance.Current.Realtime.KnownProfileId(profileId);
        CloseTransient();
        Render();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BringIntoView());
    }

    /// <summary>Lets go of the static event, when the window holding the page goes.</summary>
    public void Teardown()
    {
        FlushPending();
        LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Render();
        // The names are a different length now; the width they are measured against has not moved.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ApplyStacking();
            UpdateColumns(Body.ActualWidth);
        });
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    private static IReadOnlyList<ProfileChoice> Choices() => RealtimeProfileEditing.Choices(Realtime.Profiles);

    private ProfileChoice SelectedChoice() =>
        Choices().FirstOrDefault(choice => choice.Id == _selectedId) ?? Choices()[0];

    /// <summary>The selected mode as stored: a preset's full copy, or the custom mode's entry.</summary>
    private RealtimeProfile SelectedStored() =>
        RealtimePresets.IsPreset(_selectedId)
            ? RealtimePresets.Create(_selectedId)
            : Realtime.Profiles.FirstOrDefault(profile => profile.Id == _selectedId) ?? RealtimePresets.Create(null);

    /// <summary>
    /// Changes the selected custom mode and stores the whole list — see the class remarks for what
    /// is kept of the change.
    /// </summary>
    private void Commit(Action<RealtimeProfile> change)
    {
        if (RealtimePresets.IsPreset(_selectedId)) return;

        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        int index = list.FindIndex(profile => profile.Id == _selectedId);
        if (index < 0) return;

        change(list[index]);
        list[index] = RealtimeProfileEditing.WithoutTemplateValues(list[index]);
        SettingsService.Instance.UpdateRealtimeProfiles(list);
        Render();
    }

    private void Select(string id)
    {
        if (id == _selectedId) return;
        FlushPending();
        CommitName(revertIfTaken: true);
        _selectedId = id;
        CloseTransient();
        Render();
    }

    // The two things that open on the card and belong to the mode they were opened on.
    private void CloseTransient()
    {
        DeleteConfirm.Visibility = Visibility.Collapsed;
        CloseTemplateChooser(focusAdd: false);
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        _syncing = true;
        try
        {
            RenderList();
            RenderHeader();
            RenderRows();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RenderList()
    {
        var choices = Choices();
        ListHost.Children.Clear();

        bool separated = false;
        foreach (var choice in choices)
        {
            if (!choice.IsPreset && !separated)
            {
                ListHost.Children.Add(Rule(new Thickness(4, 6, 4, 6)));
                separated = true;
            }
            ListHost.Children.Add(ListRow(choice));
        }

        int count = Realtime.Profiles.Count;
        bool full = count >= RealtimeSettings.MaxCustomProfiles;
        AddRowText.Text = LocalizationService.Format("S.Realtime.ProfileAdd", count, RealtimeSettings.MaxCustomProfiles);
        AddRow.IsEnabled = !full;
        AddRow.ToolTip = full ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles) : null;
        if (full) CloseTemplateChooser(focusAdd: false);
    }

    private RadioButton ListRow(ProfileChoice choice)
    {
        var name = new TextBlock
        {
            Text = choice.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        FrameworkElement content;
        if (choice.IsPreset)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(name);
            var tag = TagChip("S.Realtime.ProfilePresetTag");
            Grid.SetColumn(tag, 1);
            grid.Children.Add(tag);
            content = grid;
        }
        else
        {
            var stack = new StackPanel();
            stack.Children.Add(name);
            var template = new TextBlock
            {
                Text = LocalizationService.Format("S.Realtime.ProfileTemplateOf", RealtimeProfileEditing.PresetName(choice.BasedOn)),
                FontWeight = FontWeights.Normal,
                Margin = new Thickness(0, 1, 0, 0),
            };
            template.SetResourceReference(StyleProperty, "FieldHint");
            stack.Children.Add(template);
            content = stack;
            name.ToolTip = choice.Name;
        }

        var row = new RadioButton
        {
            GroupName = "RealtimeProfileList",
            Content = content,
            IsChecked = choice.Id == _selectedId,
            Margin = new Thickness(0, 0, 0, 2),
        };
        row.SetResourceReference(StyleProperty, "ProfileListRow");
        System.Windows.Automation.AutomationProperties.SetName(row, choice.Name);
        row.Checked += (_, _) =>
        {
            if (!_syncing) Select(choice.Id);
        };
        return row;
    }

    private void RenderHeader()
    {
        var choice = SelectedChoice();
        bool preset = choice.IsPreset;
        CustomHeader.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;
        PresetHeader.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;

        bool full = Realtime.Profiles.Count >= RealtimeSettings.MaxCustomProfiles;
        if (preset)
        {
            PresetName.Text = choice.Name;
            CreateFromBtn.IsEnabled = !full;
            CreateFromBtn.ToolTip = full ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles) : null;
            HeaderNote.Text = LocalizationService.Get("S.Realtime.ProfilePresetReadOnly");
            NameError.Text = "";
        }
        else
        {
            if (!NameBox.IsKeyboardFocusWithin || _nameShown != choice.Id)
            {
                NameBox.Text = choice.Name;
                NameError.Text = "";
            }
            _nameShown = choice.Id;
            ResetAllBtn.IsEnabled = RealtimeProfileEditing.ModifiedCount(SelectedStored(), RealtimeProfileEditing.All) > 0;
            HeaderNote.Text = LocalizationService.Format("S.Realtime.ProfileBasedOn", RealtimeProfileEditing.PresetName(choice.BasedOn));
        }
    }

    private void RenderRows()
    {
        bool preset = RealtimePresets.IsPreset(_selectedId);
        var stored = preset ? SelectedStored() : RealtimeProfileEditing.WithoutTemplateValues(SelectedStored());
        var filled = stored.Filled();
        var template = RealtimePresets.Create(filled.BasedOn);

        foreach (var row in _rows.Values)
        {
            var param = row.Param;
            bool enabled = !preset;
            bool modified = !preset && RealtimeProfileEditing.IsSet(stored, param);

            // Its line under the name is kept while hidden on a custom mode, so that setting a value
            // does not push the rows below it down. A preset never shows it.
            row.Modified.Visibility = modified ? Visibility.Visible : preset ? Visibility.Collapsed : Visibility.Hidden;
            // A tick has no reading of its own to put back; 還原為範本 says it.
            row.Revert.ToolTip = param == ProfileParam.ClearAfterEmptyReads
                ? LocalizationService.Get("S.Realtime.ProfileResetAll")
                : LocalizationService.Format("S.Realtime.ProfileRevert", Display(template, param));

            foreach (var control in row.Controls) control.IsEnabled = enabled;
            // The trays, the switch and the tick have no greyed look of their own; the slider has.
            foreach (var control in row.Fades) control.Opacity = enabled ? 1.0 : 0.45;
            row.Name.SetResourceReference(TextBlock.ForegroundProperty, enabled || preset ? "AppTextPrimary" : "AppTextSecondary");
            row.Value.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "AppTextPrimary" : "AppTextSecondary");

            ShowValue(row, filled);
        }

        int advancedModified = preset ? 0 : RealtimeProfileEditing.ModifiedCount(stored, RealtimeProfileEditing.Advanced);
        AdvancedText.Text = !_advancedExpanded && advancedModified > 0
            ? LocalizationService.Format("S.Realtime.ProfileAdvancedModified", advancedModified)
            : LocalizationService.Get("S.Realtime.ProfileAdvanced");
        AdvancedChevron.Text = _advancedExpanded ? "" : "";
        AdvancedPanel.Visibility = _advancedExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts one value of the selected mode on its row.</summary>
    private void ShowValue(ParamRow row, RealtimeProfile filled)
    {
        switch (row.Param)
        {
            case ProfileParam.ClearAfterEmptyReads:
                row.Toggle!.IsChecked = ClearsAtOnce(filled);
                break;
            case ProfileParam.SettleSeconds:
            {
                bool automatic = filled.SettleSeconds < 0;
                double seconds = automatic ? RealtimeProfileEditing.AutomaticSettleSeconds(filled) : filled.SettleSeconds!.Value;
                row.Auto!.IsChecked = automatic;
                row.Slider!.Value = Math.Round(seconds / PollSeconds);
                if (automatic) row.Slider.IsEnabled = false;
                break;
            }
            case ProfileParam.DetectorPercent:
            {
                bool automatic = filled.DetectorPercent < 0;
                row.Auto!.IsChecked = automatic;
                row.Slider!.Value = automatic ? RealtimeProfileEditing.AutomaticDetectorPercent(filled) : filled.DetectorPercent!.Value;
                if (automatic) row.Slider.IsEnabled = false;
                break;
            }
            default:
                row.Slider!.Value = Convert.ToDouble(RealtimeProfileEditing.Value(filled, row.Param));
                break;
        }
        row.Value.Text = Display(filled, row.Param);
    }

    private static double PollSeconds => Services.Realtime.RealtimeRegionState.PollInterval.TotalSeconds;

    /// <summary>原文消失後立即清除譯文: ticked on a mode that clears on the first empty reading.</summary>
    private static bool ClearsAtOnce(RealtimeProfile filled) =>
        filled.ClearAfterEmptyReads == RealtimeProfile.MinClearAfterEmptyReads;

    /// <summary>How a value reads on its row and on the 還原 tooltip.</summary>
    private static string Display(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.MergeLevel => LevelName(filled.MergeLevel ?? RealtimeMergeLevels.AutomaticMiddle),
        ProfileParam.SettleSeconds => SecondsText(filled.SettleSeconds ?? RealtimePresets.Automatic),
        ProfileParam.DetectorPercent => filled.DetectorPercent is < 0 or null
            ? LocalizationService.Get("S.Realtime.ProfileAuto")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.DetectorPercent),
        ProfileParam.IgnoreSmallPercent => filled.IgnoreSmallPercent is 0 or null
            ? LocalizationService.Get("S.Realtime.ProfileOff")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.IgnoreSmallPercent),
        ProfileParam.ConfidencePercent => LocalizationService.Format("S.Realtime.ProfilePercent", filled.ConfidencePercent),
        _ => "",
    };

    // Stand-in until the page is rebuilt around 合併程度: the ends read as the step beside them.
    private static string LevelName(int level) => level switch
    {
        <= RealtimeMergeLevels.Items => LocalizationService.Get("S.Realtime.ProfileLayoutItems"),
        >= RealtimeMergeLevels.Passage => LocalizationService.Get("S.Realtime.ProfileLayoutPassage"),
        _ => Grade(RealtimeMergeLevels.Tendency(level)),
    };

    private static string SecondsText(double seconds) =>
        seconds < 0 ? LocalizationService.Get("S.Realtime.ProfileAuto")
        : seconds < 0.005 ? LocalizationService.Get("S.Realtime.ProfileSettleNone")
        : LocalizationService.Format("S.Realtime.ProfileSeconds", RealtimeProfileEditing.Seconds(seconds));

    private static string Grade(int step) => step switch
    {
        1 => LocalizationService.Get("S.Realtime.ProfileGradeLow"),
        2 => LocalizationService.Get("S.Realtime.ProfileGradeLower"),
        4 => LocalizationService.Get("S.Realtime.ProfileGradeHigher"),
        5 => LocalizationService.Get("S.Realtime.ProfileGradeHigh"),
        _ => LocalizationService.Get("S.Realtime.ProfileGradeMid"),
    };

    /// <summary>What a slider's position reads as while it is being moved, before it is written.</summary>
    private static string SliderReading(ProfileParam param, double value) => param switch
    {
        ProfileParam.MergeLevel => LevelName((int)Math.Round(value)),
        ProfileParam.SettleSeconds => SecondsText(Math.Round(value * PollSeconds, 2)),
        ProfileParam.DetectorPercent => LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        ProfileParam.IgnoreSmallPercent => value < 0.5
            ? LocalizationService.Get("S.Realtime.ProfileOff")
            : LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        ProfileParam.ConfidencePercent => LocalizationService.Format("S.Realtime.ProfilePercent", (int)value),
        _ => "",
    };

    // ── Building the rows ───────────────────────────────────────────────────────────────────────

    private void BuildRows()
    {
        Add(LayoutGroup, BuildRow(ProfileParam.MergeLevel, "S.Realtime.ProfileLayout", "S.Realtime.ProfileLayoutHint"));
        Add(TimingGroup, BuildRow(ProfileParam.SettleSeconds, "S.Realtime.ProfileSettle", "S.Realtime.ProfileSettleHint"));
        Add(TimingGroup, BuildRow(ProfileParam.ClearAfterEmptyReads, "S.Realtime.ProfileKeep", "S.Realtime.ProfileKeepHint"));
        Add(RecognitionGroup, BuildRow(ProfileParam.DetectorPercent, "S.Realtime.ProfileDetector", "S.Realtime.ProfileDetectorHint"));
        Add(AdvancedRecognitionGroup, BuildRow(ProfileParam.IgnoreSmallPercent, "S.Realtime.ProfileIgnoreSmall", "S.Realtime.ProfileIgnoreSmallHint"));
        Add(AdvancedRecognitionGroup, BuildRow(ProfileParam.ConfidencePercent, "S.Realtime.ProfileConfidence", "S.Realtime.ProfileConfidenceHint"));

        // A rule between rows, never above the first: the group's header is the first row's top.
        static void Add(System.Windows.Controls.Panel group, ParamRow row)
        {
            if (group.Children.Count > 1) group.Children.Add(Rule(new Thickness(0, 10, 0, 10)));
            group.Children.Add(row.Root);
        }
    }

    /// <summary>
    /// One value's row: name, its tags and 已修改 on the left; 自動, the control and its reading
    /// on the right, in columns shared down the card; the line on which way to move it under them;
    /// and, while it has no effect, why.
    /// </summary>
    private ParamRow BuildRow(ProfileParam param, string nameKey, string hintKey)
    {
        // The shared columns hold only fixed-size parts. The wrapping lines under them are outside
        // this grid: a wrapping text spanning shared-size columns makes the shared sizes and the
        // wrap feed each other, and layout never settles (measured: the window stopped responding).
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProfileAuto" });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProfileControl" });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProfileValue" });
        var root = new StackPanel();
        root.Children.Add(top);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        name.SetResourceReference(TextBlock.TextProperty, nameKey);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");

        // The name and its tag on one line, 已修改 always on the line under them: beside the name it
        // fitted in rows without a tag and wrapped under in rows with one, so it moved row to row.
        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(name);
        if (RealtimeProfileEditing.IsHorizontalOnly(param))
        {
            var tag = TagChip("S.Realtime.ProfileHorizontalOnly");
            tag.SetResourceReference(ToolTipProperty, "S.Realtime.ProfileHorizontalOnlyTip");
            title.Children.Add(tag);
        }
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(title);

        var revert = new Button { Width = 24, Height = 24, Margin = new Thickness(2, 0, 0, 0) };
        revert.SetResourceReference(StyleProperty, "IconButton");
        revert.Content = new TextBlock
        {
            Text = "",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
        };
        System.Windows.Automation.AutomationProperties.SetName(revert, LocalizationService.Get("S.Realtime.ProfileResetAll"));
        revert.Click += (_, _) => Commit(profile => RealtimeProfileEditing.Clear(profile, param));

        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AppAccent");
        var modifiedText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        modifiedText.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfileModified");
        modifiedText.SetResourceReference(TextBlock.ForegroundProperty, "AppAccent");
        modifiedText.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        var modified = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        modified.Children.Add(dot);
        modified.Children.Add(modifiedText);
        modified.Children.Add(revert);
        heading.Children.Add(modified);
        top.Children.Add(heading);

        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, MinWidth = 76, Margin = new Thickness(12, 0, 2, 0) };
        value.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        value.FontSize = 13;

        var row = new ParamRow(param, root, top, name, value, modified, revert) { Heading = heading };

        switch (param)
        {
            case ProfileParam.MergeLevel:
                BuildSlider(row, RealtimeMergeLevels.Min, RealtimeMergeLevels.Max, 1, nameKey);
                break;
            case ProfileParam.SettleSeconds:
                BuildAuto(row, null);
                BuildSlider(row, 0, Math.Round(RealtimeProfile.MaxSettleSeconds / PollSeconds), 1, nameKey);
                break;
            case ProfileParam.ClearAfterEmptyReads:
            {
                var tick = new CheckBox { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
                tick.SetResourceReference(StyleProperty, "ModernCheckBox");
                tick.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);
                RoutedEventHandler changed = (_, _) =>
                {
                    if (!_syncing) Commit(profile => profile.ClearAfterEmptyReads = tick.IsChecked == true
                        ? RealtimeProfile.MinClearAfterEmptyReads
                        : RealtimeProfile.MaxClearAfterEmptyReads);
                };
                tick.Checked += changed;
                tick.Unchecked += changed;
                Grid.SetColumn(tick, 2);
                top.Children.Add(tick);
                row.Toggle = tick;
                row.Controls.Add(tick);
                row.Fades.Add(tick);
                break;
            }
            case ProfileParam.DetectorPercent:
                BuildAuto(row, "S.Realtime.ProfileDetectorAutoTip");
                BuildSlider(row, RealtimeProfile.MinDetectorPercent, RealtimeProfile.MaxDetectorPercent, 1, nameKey);
                break;
            case ProfileParam.IgnoreSmallPercent:
                BuildSlider(row, 0, RealtimeProfile.MaxIgnoreSmallPercent, 5, nameKey);
                break;
            case ProfileParam.ConfidencePercent:
                BuildSlider(row, RealtimeProfile.MinConfidencePercent, RealtimeProfile.MaxConfidencePercent, 1, nameKey);
                break;
        }

        if (row.Slider is not null)
        {
            Grid.SetColumn(value, 3);
            top.Children.Add(value);
        }

        var hint = new TextBlock { LineHeight = 16, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(StyleProperty, "FieldHint");
        hint.SetResourceReference(TextBlock.TextProperty, hintKey);
        root.Children.Add(hint);

        _rows[param] = row;
        return row;
    }

    /// <summary>
    /// The 自動 tick in front of a slider: ticked, the slider rests on what 自動 stands for and is
    /// greyed; unticked, the slider's position is written as the value — the last one set by hand on
    /// this mode if there was one.
    /// </summary>
    private void BuildAuto(ParamRow row, string? tipKey)
    {
        var auto = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        auto.SetResourceReference(StyleProperty, "ModernCheckBox");
        auto.SetResourceReference(ContentProperty, "S.Realtime.ProfileAuto");
        if (tipKey is not null) auto.SetResourceReference(ToolTipProperty, tipKey);
        auto.Checked += (_, _) =>
        {
            if (_syncing) return;
            FlushPending();
            Commit(profile => SetNumber(profile, row.Param, RealtimePresets.Automatic));
        };
        auto.Unchecked += (_, _) =>
        {
            if (_syncing) return;
            var slider = row.Slider!;
            if (_lastManual.TryGetValue((_selectedId, row.Param), out var manual)) slider.Value = manual;
            Commit(profile => SetNumber(profile, row.Param, slider.Value));
        };
        Grid.SetColumn(auto, 1);
        row.Top.Children.Add(auto);
        row.Auto = auto;
        row.Controls.Add(auto);
        row.Fades.Add(auto);
    }

    private void BuildSlider(ParamRow row, double min, double max, double tick, string nameKey,
        string? lowKey = null, string? highKey = null)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            SmallChange = tick,
            LargeChange = tick,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        slider.SetResourceReference(StyleProperty, "AppSlider");
        slider.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);

        bool dragging = false;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            _settleTimer.Stop();
            _pendingRow = row;
            FlushPending();
        }));
        slider.ValueChanged += (_, _) =>
        {
            row.Value.Text = SliderReading(row.Param, slider.Value);
            if (_syncing || dragging) return;
            // From the keyboard or a click on the track: written once the keys rest.
            _pendingRow = row;
            _settleTimer.Stop();
            _settleTimer.Start();
        };
        slider.LostKeyboardFocus += (_, _) => FlushPending();

        FrameworkElement control = slider;
        if (lowKey is not null && highKey is not null)
        {
            // The two ends named under the track, for the one slider whose direction is not obvious
            // from its name: 合併傾向 runs from reading items apart to reading one passage.
            var ends = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            var low = new TextBlock { HorizontalAlignment = HorizontalAlignment.Left };
            var high = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (end, key) in new[] { (low, lowKey), (high, highKey) })
            {
                end.SetResourceReference(StyleProperty, "AnnotationSliderEnd");
                end.SetResourceReference(TextBlock.TextProperty, key);
                ends.Children.Add(end);
            }
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Width = 212 };
            slider.Margin = new Thickness(12, 0, 0, 0);
            stack.Children.Add(slider);
            stack.Children.Add(ends);
            control = stack;
        }

        Grid.SetColumn(control, 2);
        row.Top.Children.Add(control);
        row.Slider = slider;
        row.Controls.Add(control);
    }

    /// <summary>Writes the slider whose change was held back, if one was.</summary>
    private void FlushPending()
    {
        _settleTimer.Stop();
        if (_pendingRow is not { } row) return;
        _pendingRow = null;
        if (row.Slider is not { IsEnabled: true } slider || RealtimePresets.IsPreset(_selectedId)) return;

        double value = slider.Value;
        if (row.Auto is not null) _lastManual[(_selectedId, row.Param)] = value;
        Commit(profile => SetNumber(profile, row.Param, value));
    }

    /// <summary>Writes a slider position as the value it stands for, in the user's units.</summary>
    private static void SetNumber(RealtimeProfile profile, ProfileParam param, double position)
    {
        int whole = (int)Math.Round(position);
        bool automatic = position < 0;
        switch (param)
        {
            case ProfileParam.MergeLevel: profile.MergeLevel = whole; break;
            case ProfileParam.IgnoreSmallPercent: profile.IgnoreSmallPercent = whole; break;
            case ProfileParam.ConfidencePercent: profile.ConfidencePercent = whole; break;
            case ProfileParam.DetectorPercent:
                profile.DetectorPercent = automatic ? RealtimePresets.Automatic : whole;
                break;
            case ProfileParam.SettleSeconds:
                profile.SettleSeconds = automatic ? RealtimePresets.Automatic : Math.Round(whole * PollSeconds, 2);
                break;
        }
    }

    private static Border Rule(Thickness margin)
    {
        var rule = new Border { Height = 1, Margin = margin };
        rule.SetResourceReference(Border.BackgroundProperty, "AppSubtleBorder");
        return rule;
    }

    private static Border TagChip(string key)
    {
        var text = new TextBlock { FontSize = 11, FontWeight = FontWeights.Normal };
        text.SetResourceReference(TextBlock.TextProperty, key);
        text.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        var tag = new Border { Child = text };
        tag.SetResourceReference(StyleProperty, "ProfileTag");
        return tag;
    }

    // ── Header: name, reset, delete, create ─────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || RealtimePresets.IsPreset(_selectedId)) return;
        var name = RealtimeProfileEditing.CleanName(NameBox.Text);
        NameError.Text = name.Length > 0 && RealtimeProfileEditing.NameTaken(name, _selectedId, Choices())
            ? LocalizationService.Get("S.Realtime.ProfileNameTaken")
            : "";
    }

    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitName(revertIfTaken: false);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _syncing = true;
            NameBox.Text = SelectedChoice().Name;
            _syncing = false;
            NameError.Text = "";
            NameBox.SelectAll();
        }
    }

    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitName(revertIfTaken: true);

    /// <summary>
    /// Stores what is in the name box: blank goes back to the old name without a word, a name
    /// another mode has is refused — kept in the box with the reason under it on Enter, put back on
    /// leaving the box.
    /// </summary>
    private void CommitName(bool revertIfTaken)
    {
        if (RealtimePresets.IsPreset(_selectedId) || CustomHeader.Visibility != Visibility.Visible) return;

        var current = SelectedChoice();
        var name = RealtimeProfileEditing.CleanName(NameBox.Text);
        void Restore()
        {
            _syncing = true;
            NameBox.Text = current.Name;
            _syncing = false;
            NameError.Text = "";
        }

        if (name.Length == 0)
        {
            Restore();
            return;
        }
        if (RealtimeProfileEditing.NameTaken(name, _selectedId, Choices()))
        {
            if (revertIfTaken) Restore();
            else NameError.Text = LocalizationService.Get("S.Realtime.ProfileNameTaken");
            return;
        }

        NameError.Text = "";
        var stored = Realtime.Profiles.FirstOrDefault(profile => profile.Id == _selectedId);
        if (stored is null || stored.Name == name)
        {
            if (NameBox.Text != name) Restore();
            return;
        }
        Commit(profile => profile.Name = name);
    }

    private void ResetAllBtn_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        _lastManual.Remove((_selectedId, ProfileParam.SettleSeconds));
        _lastManual.Remove((_selectedId, ProfileParam.DetectorPercent));
        Commit(profile =>
        {
            foreach (var param in RealtimeProfileEditing.All)
                RealtimeProfileEditing.Clear(profile, param);
        });
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        // Blocks on it go back to 標準 as it goes, as a mode that names nothing always does.
        DeleteConfirmText.Text = LocalizationService.Format("S.Realtime.ProfileDeleteConfirm", SelectedChoice().Name);
        DeleteConfirm.Visibility = Visibility.Visible;
        // On 取消, so an Enter that was meant for something else does not delete.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => DeleteCancelBtn.Focus());
    }

    private void DeleteCancelBtn_Click(object sender, RoutedEventArgs e) =>
        DeleteConfirm.Visibility = Visibility.Collapsed;

    private void DeleteConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        _settleTimer.Stop();
        _pendingRow = null;
        var before = Realtime.Profiles.ToList();
        var next = RealtimeProfileEditing.SelectionAfterDelete(before, _selectedId);
        var deleted = _selectedId;

        DeleteConfirm.Visibility = Visibility.Collapsed;
        _selectedId = next;
        SettingsService.Instance.UpdateRealtimeProfiles(before.Where(profile => profile.Id != deleted));
        Render();
    }

    private void Detail_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DeleteConfirm.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            DeleteConfirm.Visibility = Visibility.Collapsed;
        }
    }

    private void CreateFromBtn_Click(object sender, RoutedEventArgs e) => Create(_selectedId);

    // ── Adding ──────────────────────────────────────────────────────────────────────────────────

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        TemplateButtons.Children.Clear();
        Button? focus = null;
        string current = SelectedChoice().BasedOn;
        foreach (var preset in Choices().Where(choice => choice.IsPreset))
        {
            var button = new Button
            {
                Content = preset.Name,
                ToolTip = RealtimeProfileEditing.PresetSummary(preset.Id),
                Padding = new Thickness(22, 0, 10, 0),
            };
            button.SetResourceReference(StyleProperty, "ProfileAddRow");
            button.SetResourceReference(ForegroundProperty, "AppTextPrimary");
            var id = preset.Id;
            button.Click += (_, _) => Create(id);
            TemplateButtons.Children.Add(button);
            if (preset.Id == current) focus = button;
        }

        AddRow.Visibility = Visibility.Collapsed;
        TemplateChooser.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => focus?.Focus());
    }

    private void TemplateCancel_Click(object sender, RoutedEventArgs e) => CloseTemplateChooser(focusAdd: true);

    private void TemplateChooser_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseTemplateChooser(focusAdd: true);
    }

    private void CloseTemplateChooser(bool focusAdd)
    {
        if (TemplateChooser.Visibility != Visibility.Visible) return;
        TemplateChooser.Visibility = Visibility.Collapsed;
        AddRow.Visibility = Visibility.Visible;
        if (focusAdd) AddRow.Focus();
    }

    /// <summary>
    /// Makes a custom mode on <paramref name="presetId"/> — 自訂模式 N, every value following the
    /// preset — selects it and puts the cursor in its name, ready to be typed over.
    /// </summary>
    private void Create(string presetId)
    {
        FlushPending();
        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        if (list.Count >= RealtimeSettings.MaxCustomProfiles) return;

        var names = Choices().Select(choice => choice.Name);
        var name = RealtimeProfileEditing.NewName(RealtimeProfileEditing.NextNumber(names, RealtimeProfileEditing.NewName));
        var created = RealtimeProfile.CreateCustom(presetId, name);
        list.Add(created);

        CloseTemplateChooser(focusAdd: false);
        DeleteConfirm.Visibility = Visibility.Collapsed;
        SettingsService.Instance.UpdateRealtimeProfiles(list);
        _selectedId = created.Id;
        Render();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    // ── Layout and 進階 ──────────────────────────────────────────────────────────────────────────

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        _advancedExpanded = !_advancedExpanded;
        _syncing = true;
        try { RenderRows(); }
        finally { _syncing = false; }
    }

    // With a margin either way, so a scroll bar coming and going across the threshold cannot flip
    // the layout back and forth.
    private void Body_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns(e.NewSize.Width);

    /// <summary>
    /// One column below <see cref="TwoColumnMinWidth"/>, and also wherever the values beside the
    /// list would be narrower than their controls.
    /// </summary>
    /// <remarks>
    /// The second half is the one that bites: the three-way trays are as wide as their words, and in
    /// Russian 分行處理's and 讀取方式's ran off the card's edge at a width the fixed threshold still
    /// called wide enough (measured on Windows 10 at 125%, the main window 1152 wide). The controls'
    /// columns are shared down the card, so their widths are the widest row's — the same figure for
    /// every layout, which is what keeps this from feeding back into itself.
    /// </remarks>
    private void UpdateColumns(double width)
    {
        if (width <= 0) return;
        double needed = TwoColumnMinWidth;
        if (RowInset() is { } inset)
            needed = Math.Max(needed, ListColumn.ActualWidth + GapColumn.ActualWidth + inset + ControlsWidth());
        ApplyColumns(_oneColumn ? width < needed + 24 : width < needed);
    }

    /// <summary>
    /// The group's padding either side of a row, from where the row starts — not from its width: a
    /// row whose controls do not fit is laid out wider than the room it was given. Null before the
    /// rows are laid out.
    /// </summary>
    private double? RowInset()
    {
        var top = _rows[ProfileParam.MergeLevel].Top;
        if (top.ActualWidth <= 0 || Detail.ActualWidth <= 0 || !top.IsDescendantOf(Detail)) return null;
        return 2 * top.TranslatePoint(new System.Windows.Point(0, 0), Detail).X;
    }

    /// <summary>
    /// The right-hand columns every row shares — 自動, the control and its reading — as wide as
    /// their widest content, which they are measured at whatever room is left.
    /// </summary>
    private double ControlsWidth()
    {
        var widest = new double[_rows[ProfileParam.MergeLevel].Top.ColumnDefinitions.Count];
        foreach (var row in _rows.Values)
            foreach (UIElement child in row.Top.Children)
            {
                int column = Grid.GetColumn(child);
                if (column >= 1 && column < widest.Length && child.Visibility != Visibility.Collapsed)
                    widest[column] = Math.Max(widest[column], child.DesiredSize.Width);
            }
        return widest.Sum();
    }

    /// <summary>
    /// Beside each other while the card is wide enough for both, the list above the values when it
    /// is not — the same fall-back the 設定 page's sections make.
    /// </summary>
    private void ApplyColumns(bool oneColumn)
    {
        if (oneColumn == _oneColumn) return;
        _oneColumn = oneColumn;

        Grid.SetColumnSpan(ListBorder, oneColumn ? 3 : 1);
        Grid.SetRow(Detail, oneColumn ? 1 : 0);
        Grid.SetColumn(Detail, oneColumn ? 0 : 2);
        Grid.SetColumnSpan(Detail, oneColumn ? 3 : 1);
        Detail.Margin = new Thickness(0, oneColumn ? 4 : 0, 0, 0);
    }

    /// <summary>
    /// Each value's name beside its controls while the longest name fits there, on a line of its own
    /// above them when it does not — see <see cref="HeadingWidth"/>.
    /// </summary>
    /// <remarks>
    /// The name's column takes what the controls leave, and a name is one line that does not wrap,
    /// so on a narrow window the names were cut to their first two or three characters (measured on
    /// Windows 10 at 125%, the main window 1152 wide: two columns, 525 for the values, and the
    /// controls of 讀取方式 alone taking 490 of it). How much the controls take depends on the
    /// language and the font, so the threshold is measured rather than fixed: the widest name plus
    /// the right-hand columns, which are shared down the card and so the same for every row.
    ///
    /// The name moves out of the row's grid rather than spanning its columns: a wrapping panel across
    /// shared-size columns is what kept the page's layout from ever settling — see
    /// <see cref="BuildRow"/>.
    /// </remarks>
    private void ApplyStacking()
    {
        if (RowInset() is not { } inset) return;
        double width = Detail.ActualWidth - inset;
        double controls = ControlsWidth();
        double names = 0;
        foreach (var row in _rows.Values) names = Math.Max(names, HeadingWidth(row));
        double needed = names + NameGap + controls;
        bool stacked = _stacked ? width < needed + StackingHysteresis : width < needed;
        if (stacked == _stacked) return;
        _stacked = stacked;

        foreach (var row in _rows.Values)
        {
            if (stacked)
            {
                row.Top.Children.Remove(row.Heading);
                row.Root.Children.Insert(0, row.Heading);
                row.Heading.Margin = new Thickness(0, 0, 0, 6);
            }
            else
            {
                row.Root.Children.Remove(row.Heading);
                row.Top.Children.Insert(0, row.Heading);
                row.Heading.Margin = new Thickness(0);
            }
        }
    }

    /// <summary>The name alone: 僅適用於橫排 may wrap under it, and 已修改 has its own line.</summary>
    /// <remarks>
    /// Measured: counting the tag in, or 已修改 too, put every name above its controls at the
    /// default window width (1280) — 沒有標點也合併 and its tag do not fit beside the trays there —
    /// and the card became far looser for it.
    /// </remarks>
    private static double HeadingWidth(ParamRow row) =>
        string.IsNullOrEmpty(row.Name.Text) ? 0 : TextWidth(row.Name);

    private static double TextWidth(TextBlock text) =>
        new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>One value's row and the parts of it that change.</summary>
    private sealed class ParamRow(
        ProfileParam param, StackPanel root, Grid top, TextBlock name, TextBlock value, StackPanel modified, Button revert)
    {
        public ProfileParam Param { get; } = param;
        public StackPanel Root { get; } = root;
        public Grid Top { get; } = top;
        public TextBlock Name { get; } = name;
        public TextBlock Value { get; } = value;
        public StackPanel Modified { get; } = modified;
        public Button Revert { get; } = revert;
        public FrameworkElement Heading { get; init; } = new StackPanel();
        public List<UIElement> Controls { get; } = [];
        public List<UIElement> Fades { get; } = [];
        public Slider? Slider { get; set; }
        public CheckBox? Auto { get; set; }
        public CheckBox? Toggle { get; set; }
    }
}
