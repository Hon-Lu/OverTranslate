using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// One 即時翻譯 mode in a card over the shell — a preset read-only, a custom mode editable — and the
/// step that makes a new custom mode. Opened from the 模式 list on the 即時翻譯 page and from the edit
/// layer's 模式設定….
/// </summary>
/// <remarks>
/// <para>The frame is <see cref="Settings.ServiceSettingsOverlay"/>'s: a scrim over the whole window,
/// a card with a title and a close button, a body that scrolls, and the auto-save line at the foot.
/// Saved as it is changed, with no 儲存 button: <see cref="SettingsService.UpdateRealtimeProfiles"/>
/// tells a running session, which puts its blocks on the new values at once. That is why a slider
/// writes only when let go of (or, from the keyboard, once the keys have rested for
/// <see cref="KeyboardSettle"/>): every write that changes something makes a session read again.</para>
///
/// <para>A preset is shown with exactly the rows a custom mode has, locked (the user's call): looking
/// at a preset is looking at what a custom mode made from it starts as. Locked means not hit-testable,
/// out of the tab order, and the accent swapped for the secondary text colour inside the rows — not
/// the 0.45 fade the controls use when disabled, which would make the values hard to read.</para>
///
/// <para>What is shown for a value is the mode as it reads — <see cref="RealtimeProfile.Filled"/>. What
/// is written is only what differs from the preset — see
/// <see cref="RealtimeProfileEditing.WithoutTemplateValues"/> — so 已修改 means different.</para>
/// </remarks>
public partial class RealtimeProfileOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>How long arrow keys on a slider must rest before the value is written.</summary>
    private static readonly TimeSpan KeyboardSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>The card's widest: room for a name, 自動｜手動, a slot of <see cref="SlotWidth"/> and the values.</summary>
    private const double CardWidth = 800;

    /// <summary>The tallest the card gets while it shows a mode, so 進階 opening does not move it.</summary>
    private const double CardMaxModeHeight = 900;

    /// <summary>
    /// The widest the slot gets — the slider, or under 自動 the sentence saying what 自動 does. It
    /// shares what the row leaves with the name, so a long name (Russian) is not squeezed to a
    /// column narrower than its 僅適用於橫排 tag.
    /// </summary>
    private const double SlotWidth = 240;

    /// <summary>The narrowest the slot gets: a slider shorter than this is hard to place by eye.</summary>
    private const double SlotMinWidth = 150;

    /// <summary>
    /// The value column's least width: 依文字內容調整, the longest value, at the size it is shown in
    /// under 自動. Wider where a language's value is — the column is shared down the card, so the
    /// sliders of every row still end in one line.
    /// </summary>
    private const double ValueWidth = 112;

    private readonly Dictionary<ProfileParam, ParamRow> _rows = [];
    private readonly DispatcherTimer _settleTimer = new() { Interval = KeyboardSettle };
    private ParamRow? _pendingRow;

    private string _profileId = RealtimePresets.StandardId;
    private bool _creating;
    private bool _syncing;
    private bool _advancedExpanded;
    private string _templateId = RealtimePresets.StandardId;

    /// <summary>Raised once the card has gone, so the page behind it can take focus back.</summary>
    public event EventHandler? Closed;

    public RealtimeProfileOverlay()
    {
        InitializeComponent();
        BuildRows();

        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            FlushPending();
        };
        SizeChanged += (_, _) => FitCard();
    }

    // ── Open / close ────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="profileId"/>: a preset locked, a custom mode to edit.</summary>
    public void Open(string profileId)
    {
        _creating = false;
        _profileId = Realtime.KnownProfileId(profileId);
        _advancedExpanded = false;
        Render();
        Show();
        Focus();
    }

    /// <summary>The first step of a new custom mode: its name and the preset it starts from.</summary>
    public void OpenNew()
    {
        if (Realtime.Profiles.Count >= RealtimeSettings.MaxCustomProfiles) return;
        _creating = true;
        _templateId = RealtimePresets.StandardId;
        Render();
        NewNameBox.Text = SuggestedName();
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NewNameBox.Focus();
            NewNameBox.SelectAll();
        });
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Close()
    {
        if (!IsOpen) return;
        FlushPending();
        if (!_creating) CommitName(revertIfTaken: true);
        ConfirmLayer.Visibility = Visibility.Collapsed;
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

    private void Show()
    {
        bool wasOpen = IsOpen;
        Visibility = Visibility.Visible;
        FitCard();
        if (wasOpen) return;
        LocalizationService.LanguageChanged += OnLanguageChanged;

        // Cached as a bitmap for the length of the scale, as the other cards over the shell are, so
        // the text does not soften and re-sharpen on the way in — see AboutOverlay.Open.
        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // See ServiceSettingsOverlay.ReleaseAnimations.
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
    /// <see cref="CardWidth"/> wide where the window allows, and at most nine tenths of its height.
    /// A mode is shown at that height whatever its content, so opening 進階 or turning a value to
    /// 手動 scrolls the body rather than moving the card; the new step keeps to its content.
    /// </summary>
    private void FitCard()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Card.Width = Math.Max(320, Math.Min(CardWidth, ActualWidth - 32));
        Card.MaxHeight = ActualHeight * 0.9;
        Card.Height = _creating ? double.NaN : Math.Min(ActualHeight * 0.9, CardMaxModeHeight);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside a card must not bubble up to the scrim's dismiss handler.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ConfirmLayer.Visibility == Visibility.Visible) CloseConfirm();
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Render();

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    private static IReadOnlyList<ProfileChoice> Choices() => RealtimeProfileEditing.Choices(Realtime.Profiles);

    private ProfileChoice Shown() =>
        Choices().FirstOrDefault(choice => choice.Id == _profileId) ?? Choices()[0];

    private bool ShowsPreset => RealtimePresets.IsPreset(_profileId);

    /// <summary>The mode as stored: a preset's full copy, or the custom mode's entry.</summary>
    private RealtimeProfile Stored() =>
        ShowsPreset
            ? RealtimePresets.Create(_profileId)
            : Realtime.Profiles.FirstOrDefault(profile => profile.Id == _profileId) ?? RealtimePresets.Create(null);

    /// <summary>Changes the custom mode shown and stores the whole list — see the class remarks.</summary>
    private void Commit(Action<RealtimeProfile> change)
    {
        if (ShowsPreset || _creating) return;

        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        int index = list.FindIndex(profile => profile.Id == _profileId);
        if (index < 0) return;

        change(list[index]);
        list[index] = RealtimeProfileEditing.WithoutTemplateValues(list[index]);
        SettingsService.Instance.UpdateRealtimeProfiles(list);
        Render();
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        _syncing = true;
        try
        {
            if (_creating) RenderNew();
            else RenderMode();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RenderNew()
    {
        TitleText.Text = LocalizationService.Get("S.Realtime.ProfileNewTitle");
        PresetTag.Visibility = Visibility.Collapsed;
        NewPanel.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
        EditFooter.Visibility = Visibility.Collapsed;
        NewFooter.Visibility = Visibility.Visible;
        FootRule.Visibility = Visibility.Visible;

        TemplateRows.Children.Clear();
        foreach (var preset in Choices().Where(choice => choice.IsPreset))
        {
            var id = preset.Id;
            var name = new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold };
            var summary = new TextBlock { Text = RealtimeProfileEditing.PresetSummary(id), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            var content = new StackPanel();
            content.Children.Add(name);
            content.Children.Add(summary);

            var row = new RadioButton { GroupName = "RealtimeProfileTemplate", Content = content, IsChecked = id == _templateId };
            row.SetResourceReference(StyleProperty, "TemplateRow");
            System.Windows.Automation.AutomationProperties.SetName(row, preset.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(row, "ProfileTemplate_" + id);
            row.Checked += (_, _) => _templateId = id;
            row.MouseDoubleClick += (_, _) => Create();
            TemplateRows.Children.Add(row);
        }
        // The last row draws no line under itself: the frame's own edge is there.
        if (TemplateRows.Children.Count > 0 && TemplateRows.Children[^1] is RadioButton last)
            last.Loaded += (_, _) =>
            {
                if (last.Template?.FindName("Root", last) is Border root) root.BorderThickness = new Thickness(0);
            };
        FitCard();
    }

    private void RenderMode()
    {
        if (!ShowsPreset && Realtime.Profiles.All(profile => profile.Id != _profileId))
            _profileId = RealtimePresets.StandardId;

        var choice = Shown();
        bool preset = choice.IsPreset;
        TitleText.Text = choice.Name;
        PresetTag.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
        NewPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        NewFooter.Visibility = Visibility.Collapsed;
        // A preset has nothing to save and nothing to press: the way to change one is 新增 on the list.
        EditFooter.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;
        FootRule.Visibility = EditFooter.Visibility;
        ReadOnlyNote.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
        CustomHeader.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;

        if (!preset)
        {
            if (!NameBox.IsKeyboardFocusWithin) NameBox.Text = choice.Name;
            TemplateLineText.Text = RealtimeProfileEditing.TemplateLine(choice);
            ResetAllBtn.IsEnabled = choice.Modified > 0;
        }
        NameError.Text = "";

        bool live = RealtimeSessionController.Instance.IsTranslating;
        StatusText.Text = LocalizationService.Get(live ? "S.Realtime.ProfileLiveApply" : "S.Settings.AutoSaveHint");
        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        SaveGlyph.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        ApplyLock(preset);
        RenderRows(preset);
        FitCard();
    }

    /// <summary>
    /// A preset's rows: no pointer, no tab stop, and the accent inside them swapped for the secondary
    /// text colour — a slider's fill, the chosen half of 自動｜手動, a switch's track — at full strength.
    /// </summary>
    private void ApplyLock(bool locked)
    {
        foreach (var host in new FrameworkElement[] { MainGroups, AdvancedPanel })
        {
            host.IsHitTestVisible = !locked;
            KeyboardNavigation.SetTabNavigation(host, locked ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
            if (locked && TryFindResource("AppTextSecondary") is Brush grey) host.Resources["AppAccent"] = grey;
            else host.Resources.Remove("AppAccent");
        }
    }

    private void RenderRows(bool preset)
    {
        var stored = preset ? Stored() : RealtimeProfileEditing.WithoutTemplateValues(Stored());
        var filled = stored.Filled();
        var template = RealtimePresets.Create(filled.BasedOn);

        foreach (var row in _rows.Values)
        {
            bool modified = !preset && RealtimeProfileEditing.IsModified(stored, row.Param);
            // Its line is kept while hidden on a custom mode, so setting a value does not push the
            // rows below it down: beside a slider the row is about that tall anyway. Not on the tick's row,
            // which has no slider beside the name, so the kept line was a gap between the name and its
            // hint; there it comes and goes with the tick. A preset never shows it.
            row.Modified.Visibility = modified ? Visibility.Visible
                : preset || RealtimeProfileEditing.IsTick(row.Param) ? Visibility.Collapsed
                : Visibility.Hidden;
            row.Revert.ToolTip = RealtimeProfileEditing.IsTick(row.Param)
                ? LocalizationService.Get("S.Realtime.ProfileResetAll")
                : LocalizationService.Format("S.Realtime.ProfileRevert", TemplateDisplay(template, row.Param));
            ShowValue(row, filled);
        }

        int advancedModified = preset ? 0 : RealtimeProfileEditing.ModifiedCount(stored, RealtimeProfileEditing.Advanced);
        AdvancedText.Text = !_advancedExpanded && advancedModified > 0
            ? LocalizationService.Format("S.Realtime.ProfileAdvancedModified", advancedModified)
            : LocalizationService.Get("S.Realtime.ProfileAdvanced");
        AdvancedChevron.Text = _advancedExpanded ? "" : "";
        AdvancedPanel.Visibility = _advancedExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts one value of the mode on its row.</summary>
    private void ShowValue(ParamRow row, RealtimeProfile filled)
    {
        switch (row.Param)
        {
            case ProfileParam.MergeLevel:
            {
                int level = filled.MergeLevel ?? RealtimeMergeLevels.AutomaticMiddle;
                row.Slider!.Value = level;
                ShowMergeLevel(row, level);
                return;
            }
            case ProfileParam.ClearAfterEmptyReads:
                row.Check!.IsChecked = filled.ClearAfterEmptyReads == RealtimeProfile.MinClearAfterEmptyReads;
                return;
            case ProfileParam.FilterNumbersAndAbbreviations:
                row.Check!.IsChecked = filled.FilterNumbersAndAbbreviations != false;
                return;
            case ProfileParam.Align:
            {
                int index = AlignIndex(filled.Align);
                row.Switch!.SelectedIndex = index;
                // Hidden rather than collapsed, as 已修改 is: choosing 置中 or 靠左 does not move the
                // rows below.
                row.AutoTip!.Visibility = index == 0 ? Visibility.Visible : Visibility.Hidden;
                break;
            }
            // The slider holds the figure for 手動 under 自動 too, so turning back to 手動 shows it.
            case ProfileParam.SettleSeconds:
                row.Slider!.Value = Math.Round(filled.SettleSeconds!.Value / PollSeconds);
                ShowAutomatic(row, filled.SettleAutomatic == true);
                break;
            case ProfileParam.DetectorPercent:
                row.Slider!.Value = filled.DetectorPercent!.Value;
                ShowAutomatic(row, filled.DetectorAutomatic == true);
                break;
            default:
                row.Slider!.Value = Convert.ToDouble(RealtimeProfileEditing.Value(filled, row.Param));
                break;
        }
        row.Value.Text = Display(filled, row.Param);
    }

    /// <summary>
    /// 自動 or 手動 on a row that has both. Under 自動 there is no slider (the user found a greyed one
    /// beside a ticked 自動 odd): its slot says what 自動 does, and the value column says what it comes
    /// to now, as the row's headline under a small 自動.
    /// </summary>
    private static void ShowAutomatic(ParamRow row, bool automatic)
    {
        row.Switch!.IsSecondSelected = !automatic;
        row.Slider!.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        row.AutoTip!.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        row.Kicker!.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        row.Value.FontWeight = automatic ? FontWeights.SemiBold : FontWeights.Normal;
        row.Value.FontSize = automatic ? 13.5 : 13;
    }

    private static void ShowMergeLevel(ParamRow row, int level)
    {
        row.Value.Text = RealtimeProfileEditing.MergeLevelName(level);
        row.Description!.Text = RealtimeProfileEditing.MergeLevelDescription(level);
        row.Scale!.Current = level;
    }

    private static double PollSeconds => Services.Realtime.RealtimeRegionState.PollInterval.TotalSeconds;

    /// <summary>How a value reads on its row.</summary>
    private static string Display(RealtimeProfile filled, ProfileParam param) => param switch
    {
        ProfileParam.MergeLevel => RealtimeProfileEditing.MergeLevelName(filled.MergeLevel ?? RealtimeMergeLevels.AutomaticMiddle),
        ProfileParam.Align => RealtimeProfileEditing.AlignName(filled.Align),
        ProfileParam.SettleSeconds => filled.SettleAutomatic == true
            ? Services.Realtime.RealtimeActualValues.Of(filled).SettleSeconds is { } seconds
                ? SecondsText(seconds)
                : LocalizationService.Get("S.Realtime.ProfilePerGroup")
            : SecondsText(filled.SettleSeconds!.Value),
        ProfileParam.DetectorPercent => filled.DetectorAutomatic == true
            ? Services.Realtime.RealtimeActualValues.Of(filled).DetectorPercent is { } percent
                ? LocalizationService.Format("S.Realtime.ProfilePercent", percent)
                : LocalizationService.Get("S.Realtime.ProfilePerGroup")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.DetectorPercent),
        ProfileParam.IgnoreSmallPercent => filled.IgnoreSmallPercent is 0 or null
            ? LocalizationService.Get("S.Realtime.ProfileOff")
            : LocalizationService.Format("S.Realtime.ProfilePercent", filled.IgnoreSmallPercent),
        ProfileParam.ConfidencePercent => LocalizationService.Format("S.Realtime.ProfilePercent", filled.ConfidencePercent),
        _ => "",
    };

    /// <summary>
    /// The preset's value as the 還原 tip names it: 自動 where the preset leaves a value to 自動,
    /// rather than what 自動 happens to come to under this mode's 語句合併.
    /// </summary>
    private static string TemplateDisplay(RealtimeProfile template, ProfileParam param) => param switch
    {
        ProfileParam.SettleSeconds when template.SettleAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        ProfileParam.DetectorPercent when template.DetectorAutomatic == true => LocalizationService.Get("S.Realtime.ProfileAuto"),
        _ => Display(template, param),
    };

    private static string SecondsText(double seconds) =>
        seconds < 0.005 ? LocalizationService.Get("S.Realtime.ProfileSettleNone")
        : LocalizationService.Format("S.Realtime.ProfileSeconds", RealtimeProfileEditing.Seconds(seconds));

    /// <summary>What a slider's position reads as while it is being moved, before it is written.</summary>
    private static string SliderReading(ProfileParam param, double value) => param switch
    {
        ProfileParam.MergeLevel => RealtimeProfileEditing.MergeLevelName((int)Math.Round(value)),
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
        Add(LayoutGroup, BuildMergeRow());
        Add(LayoutGroup, BuildAlignRow());
        Add(FilterGroup, BuildRow(ProfileParam.FilterNumbersAndAbbreviations, "S.Realtime.ProfileFilterNumbers", "S.Realtime.ProfileFilterNumbersHint"));
        Add(TimingGroup, BuildRow(ProfileParam.SettleSeconds, "S.Realtime.ProfileSettle", "S.Realtime.ProfileSettleHint"));
        Add(TimingGroup, BuildRow(ProfileParam.ClearAfterEmptyReads, "S.Realtime.ProfileKeep", "S.Realtime.ProfileKeepHint"));
        Add(RecognitionGroup, BuildRow(ProfileParam.DetectorPercent, "S.Realtime.ProfileDetector", "S.Realtime.ProfileDetectorHint"));
        Add(AdvancedRecognitionGroup, BuildRow(ProfileParam.IgnoreSmallPercent, "S.Realtime.ProfileIgnoreSmall", "S.Realtime.ProfileIgnoreSmallHint"));
        Add(AdvancedRecognitionGroup, BuildRow(ProfileParam.ConfidencePercent, "S.Realtime.ProfileConfidence", "S.Realtime.ProfileConfidenceHint"));

        // A rule between rows, never above the first: the card's own edge is the first row's top.
        static void Add(Panel group, ParamRow row)
        {
            if (group.Children.Count > 0) group.Children.Add(Rule(new Thickness(0, 12, 0, 12)));
            group.Children.Add(row.Root);
        }
    }

    /// <summary>
    /// The name, its tag and, on the line under them, 已修改 with the way back to the preset's value.
    /// The name wraps rather than being cut: in Russian the controls beside it leave it a third of
    /// the row.
    /// </summary>
    private ParamRow NewRow(ProfileParam param, string nameKey, Grid top)
    {
        var root = new StackPanel();
        root.Children.Add(top);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.TextProperty, nameKey);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        name.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(name);
        if (RealtimeProfileEditing.IsHorizontalOnly(param))
        {
            var tag = TagChip("S.Realtime.ProfileHorizontalOnly");
            tag.SetResourceReference(ToolTipProperty, "S.Realtime.ProfileHorizontalOnlyTip");
            title.Children.Add(tag);
        }
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
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
        revert.Click += (_, _) =>
        {
            _settleTimer.Stop();
            _pendingRow = null;
            Commit(profile => RealtimeProfileEditing.Clear(profile, param));
        };

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

        var value = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        value.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        value.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        return new ParamRow(param, root, top, value, modified, revert);
    }

    private void AddHint(ParamRow row, string hintKey)
    {
        var hint = new TextBlock { LineHeight = 16, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(StyleProperty, "FieldHint");
        hint.SetResourceReference(TextBlock.TextProperty, hintKey);
        row.Root.Children.Add(hint);
        _rows[row.Param] = row;
    }

    /// <summary>
    /// 語句合併: the name and the step's name on top, the slider the full width of the row under
    /// them with its nine steps named, then what the step does and which way to move it.
    /// </summary>
    private ParamRow BuildMergeRow()
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = NewRow(ProfileParam.MergeLevel, "S.Realtime.ProfileMerge", top);
        row.Value.FontWeight = FontWeights.SemiBold;
        row.Value.TextWrapping = TextWrapping.NoWrap;
        row.Value.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(row.Value, 1);
        top.Children.Add(row.Value);

        var slider = NewSlider(row, RealtimeMergeLevels.Min, RealtimeMergeLevels.Max, 1, "S.Realtime.ProfileMerge", "MergeSlider");
        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        slider.Margin = new Thickness(0, 8, 0, 0);
        row.Root.Children.Add(slider);

        var scale = new MergeScale();
        row.Root.Children.Add(scale);
        row.Scale = scale;

        var description = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        description.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        description.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");
        row.Root.Children.Add(description);
        row.Description = description;

        AddHint(row, "S.Realtime.ProfileMergeHint");
        return row;
    }

    /// <summary>
    /// 譯文對齊: the name, and 自動｜置中｜靠左 starting where every 自動｜手動 starts; under 自動, the
    /// sentence saying what 自動 does on the line below. The value column is left empty: the switch
    /// shows the value, and the other two choices have nothing more to say.
    /// </summary>
    /// <remarks>
    /// Three parts are wider than 自動｜手動, so the switch runs on into the slider's slot rather than
    /// widening the card's shared switch column, which would narrow every slider by the difference.
    /// That leaves the slot no room for the sentence beside it, which goes under the row instead: in
    /// Russian, with 自動 as wide as По центру, a sentence in what was left of the slot ran to five
    /// lines and pressed the name's 僅適用於橫排 tag out of sight. The sentence spans no shared
    /// column — see <see cref="BuildRow"/>.
    /// </remarks>
    private ParamRow BuildAlignRow()
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProfileSwitch" });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = SlotMinWidth, MaxWidth = SlotWidth });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = ValueWidth, SharedSizeGroup = "ProfileValue" });
        var row = NewRow(ProfileParam.Align, "S.Realtime.ProfileAlign", top);

        // As tall as the rows with a slider, so the row does not shrink when 自動 is left.
        top.Children.Add(new Border { MinHeight = 40 });

        var mode = new SegmentedSwitch
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 12, 0),
        };
        // The three parts one width within this switch, as 自動｜手動's two are.
        Grid.SetIsSharedSizeScope(mode, true);
        mode.SetLabels("S.Realtime.ProfileAuto", "S.Realtime.ProfileAlignCenter", "S.Realtime.ProfileAlignLeft");
        mode.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.Realtime.ProfileAlign");
        System.Windows.Automation.AutomationProperties.SetAutomationId(mode, "AlignMode");
        mode.IndexChanged += (_, index) =>
        {
            if (_syncing) return;
            _settleTimer.Stop();
            _pendingRow = null;
            Commit(profile => profile.Align = index switch
            {
                1 => RealtimeTextAligns.Center,
                2 => RealtimeTextAligns.Left,
                _ => RealtimeTextAligns.Auto,
            });
        };
        Grid.SetColumn(mode, 1);
        Grid.SetColumnSpan(mode, 2);
        top.Children.Add(mode);
        row.Switch = mode;

        var tip = new TextBlock { FontSize = 11.5, LineHeight = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        tip.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        tip.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        tip.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfileAlignAutoTip");
        row.Root.Children.Add(tip);
        row.AutoTip = tip;

        _rows[row.Param] = row;
        return row;
    }

    private static int AlignIndex(string? align) => RealtimeTextAligns.Normalize(align) switch
    {
        RealtimeTextAligns.Center => 1,
        RealtimeTextAligns.Left => 2,
        _ => 0,
    };

    /// <summary>
    /// Every other value: name on the left; 自動｜手動 where the value can be left to 自動; a slot of
    /// <see cref="SlotWidth"/> for the slider; the value in a column of <see cref="ValueWidth"/> — or
    /// for an on/off (原文消失後立即清除譯文, 過濾數字與縮寫) a switch there instead.
    /// </summary>
    /// <remarks>
    /// The switch and value columns are shared down the card (the scope is EditPanel), so every
    /// slot starts and ends in one line; the name and the slot split what is left. Only fixed-size
    /// parts sit in the shared columns, and nothing that wraps spans one: a wrapping text across
    /// shared-size columns makes the shared sizes and the wrap feed each other, and layout never
    /// settles (measured on the page's old card: the window stopped responding).
    /// </remarks>
    private ParamRow BuildRow(ProfileParam param, string nameKey, string hintKey)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!RealtimeProfileEditing.IsTick(param))
        {
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProfileSwitch" });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = SlotMinWidth, MaxWidth = SlotWidth });
        }
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = ValueWidth, SharedSizeGroup = "ProfileValue" });
        var row = NewRow(param, nameKey, top);
        int valueColumn = top.ColumnDefinitions.Count - 1;

        if (RealtimeProfileEditing.IsTick(param))
        {
            // An on/off switch in the value column, the name across the rest: no 自動｜手動 and no slot
            // here. The settings page's switch rather than a tick box: the user asked for the two to
            // look alike. At 44 wide it fits the value column's least width, so the column does not grow.
            bool filter = param == ProfileParam.FilterNumbersAndAbbreviations;
            var tick = new CheckBox { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            tick.SetResourceReference(StyleProperty, "ToggleSwitch");
            tick.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);
            System.Windows.Automation.AutomationProperties.SetAutomationId(tick, filter ? "FilterCheck" : "KeepCheck");
            RoutedEventHandler changed = (_, _) =>
            {
                if (_syncing) return;
                bool on = tick.IsChecked == true;
                if (filter) Commit(profile => profile.FilterNumbersAndAbbreviations = on);
                else Commit(profile => profile.ClearAfterEmptyReads = on
                    ? RealtimeProfile.MinClearAfterEmptyReads
                    : RealtimeProfile.MaxClearAfterEmptyReads);
            };
            tick.Checked += changed;
            tick.Unchecked += changed;
            Grid.SetColumn(tick, valueColumn);
            top.Children.Add(tick);
            row.Check = tick;
            if (filter) row.Root.Children.Add(FilterTags(tick));
            AddHint(row, hintKey);
            return row;
        }

        var slot = new Grid { MinHeight = 40, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slot, 2);
        top.Children.Add(slot);

        Slider slider = param switch
        {
            ProfileParam.SettleSeconds => NewSlider(row, 0, Math.Round(RealtimeProfile.MaxSettleSeconds / PollSeconds), 1, nameKey, "SettleSlider"),
            ProfileParam.DetectorPercent => NewSlider(row, RealtimeProfile.MinDetectorPercent, RealtimeProfile.MaxDetectorPercent, 1, nameKey, "DetectorSlider"),
            ProfileParam.IgnoreSmallPercent => NewSlider(row, 0, RealtimeProfile.MaxIgnoreSmallPercent, 5, nameKey, "IgnoreSmallSlider"),
            _ => NewSlider(row, RealtimeProfile.MinConfidencePercent, RealtimeProfile.MaxConfidencePercent, 1, nameKey, "ConfidenceSlider"),
        };
        slot.Children.Add(slider);

        var values = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetColumn(values, valueColumn);
        top.Children.Add(values);

        if (param is ProfileParam.SettleSeconds or ProfileParam.DetectorPercent)
        {
            var mode = new SegmentedSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            // The two halves one width within this switch: 自動 and 手動 differ in length in most languages.
            Grid.SetIsSharedSizeScope(mode, true);
            mode.SetLabels("S.Realtime.ProfileAuto", "S.Realtime.ProfileManual");
            mode.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);
            System.Windows.Automation.AutomationProperties.SetAutomationId(mode, param == ProfileParam.SettleSeconds ? "SettleMode" : "DetectorMode");
            mode.SelectionChanged += (_, manual) =>
            {
                if (_syncing) return;
                _settleTimer.Stop();
                _pendingRow = null;
                Commit(profile => SetAutomatic(profile, param, !manual));
            };
            Grid.SetColumn(mode, 1);
            top.Children.Add(mode);
            row.Switch = mode;

            var tip = new TextBlock { FontSize = 11.5, LineHeight = 16, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            tip.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
            tip.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            tip.SetResourceReference(TextBlock.TextProperty, param == ProfileParam.SettleSeconds
                ? "S.Realtime.ProfileSettleAutoTip"
                : "S.Realtime.ProfileDetectorAutoTip");
            slot.Children.Add(tip);
            row.AutoTip = tip;

            var kicker = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right };
            kicker.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfileAuto");
            kicker.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
            kicker.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            values.Children.Add(kicker);
            row.Kicker = kicker;
        }
        values.Children.Add(row.Value);

        AddHint(row, hintKey);
        return row;
    }

    /// <summary>
    /// Turns a value to 自動 or 手動 and nothing else: the figure for 手動 stays as it was, so 手動
    /// comes back where the user left it — or, never moved, at the preset's figure (on 標準, about
    /// 0.15 seconds and 85%: see <see cref="RealtimePresets.Create"/>).
    /// </summary>
    private static void SetAutomatic(RealtimeProfile profile, ProfileParam param, bool automatic)
    {
        if (param == ProfileParam.SettleSeconds) profile.SettleAutomatic = automatic;
        else profile.DetectorAutomatic = automatic;
    }

    private Slider NewSlider(ParamRow row, double min, double max, double step, string nameKey, string automationId)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            SmallChange = step,
            LargeChange = step,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.SetResourceReference(StyleProperty, "AppSlider");
        slider.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);
        System.Windows.Automation.AutomationProperties.SetAutomationId(slider, automationId);

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
            // The reading follows the thumb the whole way; the value is written when it is let go.
            if (row.Param == ProfileParam.MergeLevel) ShowMergeLevel(row, (int)Math.Round(slider.Value));
            else row.Value.Text = SliderReading(row.Param, slider.Value);
            if (_syncing || dragging) return;
            // From the keyboard or a click on the track: written once the keys rest.
            _pendingRow = row;
            _settleTimer.Stop();
            _settleTimer.Start();
        };
        slider.LostKeyboardFocus += (_, _) => FlushPending();
        row.Slider = slider;
        return slider;
    }

    /// <summary>Writes the slider whose change was held back, if one was.</summary>
    private void FlushPending()
    {
        _settleTimer.Stop();
        if (_pendingRow is not { } row) return;
        _pendingRow = null;
        if (row.Slider is not { IsVisible: true } slider || ShowsPreset || _creating) return;

        double value = slider.Value;
        Commit(profile => SetNumber(profile, row.Param, value));
    }

    /// <summary>Writes a slider position as the value it stands for, in the user's units.</summary>
    private static void SetNumber(RealtimeProfile profile, ProfileParam param, double position)
    {
        int whole = (int)Math.Round(position);
        switch (param)
        {
            case ProfileParam.MergeLevel: profile.MergeLevel = whole; break;
            case ProfileParam.IgnoreSmallPercent: profile.IgnoreSmallPercent = whole; break;
            case ProfileParam.ConfidencePercent: profile.ConfidencePercent = whole; break;
            // A slider is only shown on 手動; the switch is written with the figure, as the file
            // keeps it whenever a figure is kept — see RealtimeProfile.SettleAutomatic.
            case ProfileParam.DetectorPercent:
                profile.DetectorAutomatic = false;
                profile.DetectorPercent = whole;
                break;
            case ProfileParam.SettleSeconds:
                profile.SettleAutomatic = false;
                profile.SettleSeconds = Math.Round(whole * PollSeconds, 2);
                break;
        }
    }

    private static Border Rule(Thickness margin)
    {
        var rule = new Border { Height = 1, Margin = margin };
        rule.SetResourceReference(Border.BackgroundProperty, "AppSubtleBorder");
        return rule;
    }

    /// <summary>
    /// What 過濾數字與縮寫 covers, as tags straight under its name and with no word before them: the
    /// name says 過濾 already. Tinted while the switch <paramref name="tick"/> is on and grey while it is not
    /// — see the FilterTag style — and wrapped onto a second line where the card is narrow.
    /// </summary>
    private static WrapPanel FilterTags(CheckBox tick)
    {
        // The bottom margin of each tag is the gap between wrapped lines; the panel gives it back.
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, -6) };
        foreach (var key in new[]
                 {
                     "S.Realtime.ProfileFilterTagNumbers", "S.Realtime.ProfileFilterTagNumberSymbols",
                     "S.Realtime.ProfileFilterTagSymbols", "S.Realtime.ProfileFilterTagAbbreviations",
                 })
        {
            var tag = new ContentControl { Tag = tick, Focusable = false, IsTabStop = false };
            tag.SetResourceReference(StyleProperty, "FilterTag");
            tag.SetResourceReference(ContentControl.ContentProperty, key);
            tags.Children.Add(tag);
        }
        return tags;
    }

    private static Border TagChip(string key)
    {
        var text = new TextBlock { FontSize = 11, FontWeight = FontWeights.Normal };
        text.SetResourceReference(TextBlock.TextProperty, key);
        text.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        var tag = new Border { Child = text };
        tag.SetResourceReference(StyleProperty, "ProfileTag");
        return tag;
    }

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        _advancedExpanded = !_advancedExpanded;
        Render();
        if (_advancedExpanded)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AdvancedPanel.BringIntoView());
    }

    // ── Name, reset, delete ─────────────────────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || ShowsPreset || _creating) return;
        var name = RealtimeProfileEditing.CleanName(NameBox.Text);
        NameError.Text = name.Length > 0 && RealtimeProfileEditing.NameTaken(name, _profileId, Choices())
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
        else if (e.Key == Key.Escape && NameBox.Text != Shown().Name)
        {
            // Escape puts a name being typed back first; with nothing typed it closes the card.
            e.Handled = true;
            RestoreName();
            NameBox.SelectAll();
        }
    }

    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitName(revertIfTaken: true);

    private void RestoreName()
    {
        _syncing = true;
        NameBox.Text = Shown().Name;
        _syncing = false;
        NameError.Text = "";
    }

    /// <summary>
    /// Stores what is in the name box: blank goes back to the old name without a word, a name another
    /// mode has is refused — kept in the box with the reason under it on Enter, put back on leaving.
    /// </summary>
    private void CommitName(bool revertIfTaken)
    {
        if (ShowsPreset || _creating || CustomHeader.Visibility != Visibility.Visible) return;

        var name = RealtimeProfileEditing.CleanName(NameBox.Text);
        if (name.Length == 0)
        {
            RestoreName();
            return;
        }
        if (RealtimeProfileEditing.NameTaken(name, _profileId, Choices()))
        {
            if (revertIfTaken) RestoreName();
            else NameError.Text = LocalizationService.Get("S.Realtime.ProfileNameTaken");
            return;
        }

        NameError.Text = "";
        var stored = Realtime.Profiles.FirstOrDefault(profile => profile.Id == _profileId);
        if (stored is null || stored.Name == name)
        {
            if (NameBox.Text != name) RestoreName();
            return;
        }
        Commit(profile => profile.Name = name);
    }

    private void ResetAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _settleTimer.Stop();
        _pendingRow = null;
        Commit(profile =>
        {
            foreach (var param in RealtimeProfileEditing.All)
                RealtimeProfileEditing.Clear(profile, param);
        });
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        ConfirmMessage.Text = LocalizationService.Format("S.Realtime.ProfileDeleteConfirm", Shown().Name);
        ConfirmLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ConfirmCancelBtn.Focus());
    }

    private void CloseConfirm()
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm();

    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseConfirm();

    /// <summary>
    /// Deletes the mode and closes the card. Blocks on it — and 新區塊 if it named it — go back to
    /// 標準, as a mode that names nothing always does.
    /// </summary>
    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        _settleTimer.Stop();
        _pendingRow = null;
        var deleted = _profileId;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        // Nothing of the name box may be written back on the way out.
        _syncing = true;
        NameBox.Text = Shown().Name;
        _syncing = false;
        _profileId = RealtimePresets.StandardId;
        SettingsService.Instance.UpdateRealtimeProfiles(Realtime.Profiles.Where(profile => profile.Id != deleted).ToList());
        Close();
    }

    // ── New ─────────────────────────────────────────────────────────────────────────────────────

    private static string SuggestedName() =>
        RealtimeProfileEditing.NewName(RealtimeProfileEditing.NextNumber(
            Choices().Select(choice => choice.Name), RealtimeProfileEditing.NewName));

    private void NewNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = RealtimeProfileEditing.CleanName(NewNameBox.Text);
        NewNameError.Text = name.Length > 0 && RealtimeProfileEditing.NameTaken(name, "", Choices())
            ? LocalizationService.Get("S.Realtime.ProfileNameTaken")
            : "";
    }

    private void NewNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Create();
    }

    private void NewCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Create_Click(object sender, RoutedEventArgs e) => Create();

    /// <summary>
    /// Makes the custom mode — every value following the preset picked — and turns the same card
    /// into its editor, so the next thing seen is what was just made.
    /// </summary>
    private void Create()
    {
        if (!_creating) return;
        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        if (list.Count >= RealtimeSettings.MaxCustomProfiles) return;

        var name = RealtimeProfileEditing.CleanName(NewNameBox.Text);
        if (name.Length == 0) name = SuggestedName();
        if (RealtimeProfileEditing.NameTaken(name, "", Choices()))
        {
            NewNameError.Text = LocalizationService.Get("S.Realtime.ProfileNameTaken");
            NewNameBox.Focus();
            NewNameBox.SelectAll();
            return;
        }

        var created = RealtimeProfile.CreateCustom(_templateId, name);
        list.Add(created);
        SettingsService.Instance.UpdateRealtimeProfiles(list);

        _creating = false;
        _profileId = created.Id;
        _advancedExpanded = false;
        NameBox.Text = name;
        Render();
        BodyScroller.ScrollToTop();
        Focus();

        // The body changes, the card stays: only its content cross-fades in.
        if (SystemParameters.ClientAreaAnimation)
            BodyScroller.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
    }

    // Round the template list to the inside of its border, so a filled row does not square off the
    // corners — the service panel's list does the same.
    private void TemplateList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        TemplateRows.Clip = new RectangleGeometry(new Rect(new Point(0, 0), new Size(Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2))), 9, 9);

    // ── Parts ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One value's row and the parts of it that change.</summary>
    private sealed class ParamRow(ProfileParam param, StackPanel root, Grid top, TextBlock value, StackPanel modified, Button revert)
    {
        public ProfileParam Param { get; } = param;
        public StackPanel Root { get; } = root;
        public Grid Top { get; } = top;
        public TextBlock Value { get; } = value;
        public StackPanel Modified { get; } = modified;
        public Button Revert { get; } = revert;
        public Slider? Slider { get; set; }
        public SegmentedSwitch? Switch { get; set; }
        public TextBlock? AutoTip { get; set; }
        public TextBlock? Kicker { get; set; }
        public CheckBox? Check { get; set; }
        public MergeScale? Scale { get; set; }
        public TextBlock? Description { get; set; }
    }

    /// <summary>
    /// The nine steps of 語句合併 named under its slider — 不合併, 逐項, 1–5, 整段, 合併 — with the
    /// five 自動判斷 steps bracketed and the step the slider is on picked out.
    /// </summary>
    /// <remarks>
    /// Each name is centred on its step, which a slider's thumb travels between half a thumb in from
    /// either end; the two end names are held to the ends instead. Where names would touch — the
    /// outer two pairs in Russian — the inner one gives way towards the middle, and the tick mark
    /// stays on the step.
    /// </remarks>
    internal sealed class MergeScale : Panel
    {
        private const double ThumbHalf = 8;
        private const double Gap = 6;
        private const int Steps = RealtimeMergeLevels.Max - RealtimeMergeLevels.Min + 1;

        private readonly TextBlock[] _labels = new TextBlock[Steps];
        private readonly Border[] _ticks = new Border[Steps];
        private readonly Border _band = new() { BorderThickness = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(0, 0, 5, 5), Height = 9 };
        private readonly Border _bandLabel = new() { Padding = new Thickness(4, 0, 4, 0) };
        private int _current = -1;

        public MergeScale()
        {
            Height = 46;
            Margin = new Thickness(0, 2, 0, 0);
            string[] keys =
            [
                "S.Realtime.ProfileMergeNone", "S.Realtime.ProfileMergeItems", "", "", "", "", "",
                "S.Realtime.ProfileMergePassage", "S.Realtime.ProfileMergeBlock",
            ];
            for (int i = 0; i < Steps; i++)
            {
                var label = new TextBlock { FontSize = 11 };
                label.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
                if (keys[i].Length > 0) label.SetResourceReference(TextBlock.TextProperty, keys[i]);
                else label.Text = (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _labels[i] = label;
                Children.Add(label);

                var tick = new Border { Width = 1, Height = 5 };
                tick.SetResourceReference(Border.BackgroundProperty, "AppInputBorder");
                _ticks[i] = tick;
                Children.Add(tick);
            }
            _band.SetResourceReference(Border.BorderBrushProperty, "AppInputBorder");
            Children.Add(_band);
            var bandText = new TextBlock { FontSize = 10.5 };
            bandText.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfileMergeAuto");
            bandText.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
            bandText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            _bandLabel.Child = bandText;
            _bandLabel.SetResourceReference(Border.BackgroundProperty, "AppCardBg");
            Children.Add(_bandLabel);
            Current = RealtimeMergeLevels.AutomaticMiddle;
        }

        /// <summary>The step the slider is on, named in the control colour and a step heavier.</summary>
        public int Current
        {
            get => _current;
            set
            {
                if (value == _current) return;
                _current = value;
                for (int i = 0; i < Steps; i++)
                {
                    bool on = i == value;
                    bool end = i is 0 or 1 or Steps - 2 or Steps - 1;
                    _labels[i].SetResourceReference(TextBlock.ForegroundProperty,
                        on ? "AppAccent" : end ? "AppTextPrimary" : "AppTextSecondary");
                    _labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                }
                InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(0, Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = finalSize.Width;
            double StepX(int i) => ThumbHalf + i * (width - 2 * ThumbHalf) / (Steps - 1);

            var left = new double[Steps];
            var size = _labels.Select(label => label.DesiredSize.Width).ToArray();
            for (int i = 0; i < Steps; i++) left[i] = StepX(i) - size[i] / 2;
            left[0] = 0;
            left[Steps - 1] = width - size[Steps - 1];
            for (int i = 1; i < Steps - 1; i++) left[i] = Math.Max(left[i], left[i - 1] + size[i - 1] + Gap);
            for (int i = Steps - 2; i >= 1; i--) left[i] = Math.Min(left[i], left[i + 1] - size[i] - Gap);

            for (int i = 0; i < Steps; i++)
            {
                _ticks[i].Arrange(new Rect(Math.Round(StepX(i)), 0, 1, 5));
                _labels[i].Arrange(new Rect(new Point(left[i], 7), _labels[i].DesiredSize));
            }

            double bandLeft = StepX(RealtimeMergeLevels.AutomaticLowest) - ThumbHalf;
            double bandRight = StepX(RealtimeMergeLevels.AutomaticHighest) + ThumbHalf;
            _band.Arrange(new Rect(bandLeft, 26, Math.Max(0, bandRight - bandLeft), 9));
            var bandSize = _bandLabel.DesiredSize;
            _bandLabel.Arrange(new Rect(new Point((bandLeft + bandRight - bandSize.Width) / 2, 29), bandSize));
            return finalSize;
        }
    }
}
