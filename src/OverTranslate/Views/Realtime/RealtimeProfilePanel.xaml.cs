using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NLog;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using static OverTranslate.Views.Realtime.RealtimeProfileDisplay;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brush = System.Windows.Media.Brush;
using Panel = System.Windows.Controls.Panel;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 模式, opened from the running capsule (翻譯中調整模式): which block, which mode it is on, and every
/// value of that mode — changed while the translation they are for is on the screen.
/// </summary>
/// <remarks>
/// <para>The same modes as the card on the page, read from and written to the same file through
/// <see cref="RealtimeProfileStore"/>; the session reads the changed blocks again once the modes have
/// rested (RealtimeSessionController.ProfilesSettle). A slider is only a change once it is let go,
/// as on the page: each change re-reads the blocks on that mode.</para>
///
/// <para>A window of its own beside the capsule, built the way 顯示外觀 is and for its reasons: it
/// never takes the foreground (pulling the focus off a full-screen game can minimise it), everything
/// on it is worked with the mouse, and as a visible topmost window of this process it is left out of
/// every frame and raised over the overlays by the session's re-assert. Nothing here is typed into, so
/// unlike 顯示外觀 it never asks for the keyboard either. The list of modes opens inside the panel
/// rather than in a popup, which would be one more window coming and going.</para>
///
/// <para>Every handler is guarded (<see cref="Guard"/>): a fault while tuning must cost the user the
/// press, not the application — an exception on the dispatcher outside a screenshot capture ends the
/// process (App.DispatcherUnhandledException).</para>
/// </remarks>
internal partial class RealtimeProfilePanel : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Between the capsule's edge and the panel's.
    private const double Gap = 6;

    // The tallest the panel's body gets on a screen with room to spare: every row, 進階 open, and
    // the list of modes open with a few custom ones — more than that scrolls. Less where the screen
    // beside the capsule has less room: see Place.
    private const double MaxBodyHeight = 600;

    // Below this the body is not worth showing at all; the panel then takes it and lets the screen
    // edge cut the rest, which only a capsule pushed into a corner of a very small screen meets.
    private const double MinBodyHeight = 160;

    // The most of the screen's height the body takes — see Place.
    private const double MaxScreenShare = 0.55;

    private System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private double _windowScale = 1.0;
    private bool _opensUp = true;
    private bool _placed;
    private bool _closing;

    // Which block is being tuned, by position: 區塊 1 is 0.
    private int _block;
    private bool _advanced;

    // Set while stored values are being put on the controls, so that is not taken for the user.
    private bool _showing;

    // Set while this panel writes the modes, so the change it raises is not taken for one made elsewhere.
    private bool _writing;

    // A slider being dragged: the panel is not shown again under it until it is let go.
    private Slider? _dragging;
    private bool _renderWhenReleased;

    private readonly Dictionary<ProfileParam, Row> _rows = [];

    // The change a preset's row was given, while the card offers to split.
    private Action<RealtimeProfile>? _forkChange;

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;
    private static RealtimeSessionController Session => RealtimeSessionController.Instance;

    /// <param name="bar">The capsule's visible surface, in physical pixels: the panel opens clear of it.</param>
    /// <param name="screen">The session's screen, which the panel has to stay on.</param>
    public RealtimeProfilePanel(System.Drawing.Rectangle bar, System.Drawing.Rectangle screen)
    {
        InitializeComponent();

        _bar = bar;
        _screen = screen;

        BuildRows();
        BlockSwitch.IndexChanged += (_, index) => Guard("block switch", () => SelectBlock(index));

        SettingsService.Instance.RealtimeProfilesChanged += OnProfilesChanged;
        Session.BlocksChanged += OnBlocksChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            SettingsService.Instance.RealtimeProfilesChanged -= OnProfilesChanged;
            Session.BlocksChanged -= OnBlocksChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            Session.UnmarkBlocks();
        };

        PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_renderWhenReleased) return;
            _renderWhenReleased = false;
            // After the release has been handled, so a slider let go writes before it is shown again.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Guard("deferred render", Render));
        };

        Render();
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => Guard("place", Place)), DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show could hand it the foreground: see the remarks.
        WindowStyles.ApplyNoActivate(this);

        // Opened at the bar, so the window starts on the monitor it will be placed on and the scale
        // read in Place is that monitor's.
        ScreenGeometry.MoveToPhysical(this, _bar.Left, _bar.Top);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(() => Guard("place", Place)), DispatcherPriority.Loaded);
    }

    /// <summary>The capsule moved — dragged, or its width changed — and the panel goes with it.</summary>
    public void UpdateAnchor(System.Drawing.Rectangle bar)
    {
        if (bar == _bar || _closing) return;
        _bar = bar;
        if (IsLoaded) Guard("place", Place);
    }

    /// <summary>Closes the panel; the blocks lose their names with it.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>
    /// Runs a handler so that a fault in it is logged and the panel shown again from the file, rather
    /// than taking the application down — see the remarks.
    /// </summary>
    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Realtime mode panel: {What} failed", what);
            try
            {
                _showing = false;
                if (!_closing) Render();
            }
            catch (Exception again)
            {
                Log.Error(again, "Realtime mode panel: could not be shown again; closing it");
                Dismiss();
            }
        }
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the panel for the session's screen and sets it against the capsule — above it, or below
    /// when there is more room there — its right edge on the capsule's, as 顯示外觀 is. The body is
    /// held to the room on that side, and scrolls beyond it.
    /// </summary>
    /// <remarks>
    /// The window is made as tall as the panel may get and never resizes after: rows coming and going
    /// (進階, 自動｜手動, the list of modes) only lay the panel out again inside it, against the
    /// capsule's side. Resizing the window with the panel showed a frame of the old layout in the new
    /// size before the new layout was drawn — 顯示外觀 met that with its pickers.
    /// </remarks>
    private void Place()
    {
        if (_closing) return;

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;

        // The capsule's own correction — see RealtimeControlWindow.ApplyScreenScale — so the panel
        // comes out the same physical size as the bar it opens from.
        double screenScale = ScreenGeometry.ScaleAt(_bar.Left, _bar.Top);
        double relative = screenScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);

        int Physical(double dip) => (int)Math.Round(dip * screenScale);
        double Dip(int physical) => physical / screenScale;

        double above = Dip(_bar.Top - _screen.Top);
        double below = Dip(_screen.Bottom - _bar.Bottom);
        // Decided once, on the first placement: a panel that flipped sides as a row opened would move
        // out from under the pointer that opened it.
        if (!_placed) _opensUp = above >= below;

        // What the chrome around the body takes: the header, the body's padding, the frame.
        var margin = RootChrome.Margin;
        const double header = 40, frame = 2;
        double room = (_opensUp ? above : below) - Gap - header - frame - Body.Padding.Bottom - 8;
        // And never more than a share of the screen, wherever the capsule is: the panel is beside the
        // picture it tunes, and every row open measured 594 DIP — three quarters of a 768-high screen
        // (the Win10 VM), with most of the game behind it. The rest scrolls.
        double share = Dip(_screen.Height) * MaxScreenShare;
        Body.MaxHeight = Math.Clamp(Math.Min(room, share), MinBodyHeight, MaxBodyHeight);
        UpdateLayout();

        double tallest = header + frame + Body.MaxHeight;
        RootChrome.VerticalAlignment = _opensUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Height = tallest * relative + margin.Top + margin.Bottom;

        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(tallest);
        int gap = Physical(Gap);

        int chromeTop = _opensUp
            ? Math.Max(_screen.Top, _bar.Top - gap - chromeHeight)
            : Math.Min(_bar.Bottom + gap, Math.Max(_screen.Top, _screen.Bottom - chromeHeight));
        int chromeLeft = Math.Clamp(
            _bar.Right - chromeWidth, _screen.Left, Math.Max(_screen.Left, _screen.Right - chromeWidth));

        // Cast away from the bar.
        Shadow.Direction = _opensUp ? 90 : 270;

        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));
        _placed = true;

        Opacity = 1;
    }

    // ── State ────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ProfileChoice> Choices() => RealtimeProfileEditing.Choices(Realtime.Profiles);

    private string ShownProfileId()
    {
        var blocks = Session.TranslatingBlocks;
        return blocks.Count == 0 ? RealtimePresets.StandardId : Realtime.KnownProfileId(blocks[_block].ProfileId);
    }

    private bool ShowsPreset => RealtimePresets.IsPreset(ShownProfileId());

    private void SelectBlock(int index)
    {
        if (_showing) return;
        _block = index;
        CancelFork();
        CloseModeList();
        Render();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (_writing) return;
        Dispatcher.BeginInvoke(() => Guard("modes changed elsewhere", RenderUnlessDragging));
    }

    private void OnBlocksChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Guard("blocks changed", RenderUnlessDragging));

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Guard("language changed", () =>
        {
            if (_closing) return;
            RebuildModeList();
            Render();
        }));

    private void RenderUnlessDragging()
    {
        if (_closing) return;
        if (_dragging is not null)
        {
            _renderWhenReleased = true;
            return;
        }
        Render();
    }

    /// <summary>
    /// Changes the mode the shown block is on and stores the list. On a preset, a change to
    /// <paramref name="param"/>'s row is offered on a mode split off it instead.
    /// </summary>
    private void Commit(ProfileParam param, Action<RealtimeProfile> change)
    {
        if (_showing) return;
        var id = ShownProfileId();
        if (RealtimePresets.IsPreset(id))
        {
            if (RealtimeProfileStore.WouldChangePreset(id, param, change)) OfferFork(param, change);
            else Render();
            return;
        }

        Write(() => RealtimeProfileStore.Change(id, change));
        Render();
    }

    private void Write(Action write)
    {
        _writing = true;
        try
        {
            write();
        }
        finally
        {
            _writing = false;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_closing) return;
        _showing = true;
        try
        {
            RenderCore();
        }
        finally
        {
            _showing = false;
        }
    }

    private void RenderCore()
    {
        var blocks = Session.TranslatingBlocks;
        if (blocks.Count == 0) return;
        _block = Math.Clamp(_block, 0, blocks.Count - 1);

        // The switch only where there is a choice of block.
        BlockSwitch.Visibility = blocks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (blocks.Count > 1)
        {
            string Name(int i) => LocalizationService.Format("S.Realtime.BlockNumber", i + 1);
            BlockSwitch.SetTexts(Name(0), Name(1), blocks.Count > 2 ? Name(2) : null);
            BlockSwitch.SelectedIndex = _block;
        }
        Session.MarkBlocks(_block);

        var id = ShownProfileId();
        var choice = Choices().FirstOrDefault(c => c.Id == id) ?? Choices()[0];
        bool preset = choice.IsPreset;
        ModeName.Text = choice.Name;
        PresetTag.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
        if (ModeListFrame.Visibility == Visibility.Visible) RebuildModeList();

        var sharing = Enumerable.Range(0, blocks.Count)
            .Where(i => i != _block && Realtime.KnownProfileId(blocks[i].ProfileId) == id)
            .Select(i => (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture))
            .ToList();
        SharedText.Text = sharing.Count == 0 ? "" : LocalizationService.Format("S.Realtime.ProfileSharedWith",
            string.Join(LocalizationService.Get("S.Realtime.BlockNumberSeparator"), sharing));
        SharedText.Visibility = sharing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        LockRow.Visibility = preset ? Visibility.Visible : Visibility.Collapsed;
        bool full = RealtimeProfileStore.IsFull;
        ForkBtn.IsEnabled = !full;
        ForkBtn.ToolTip = full ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles) : null;

        ResetBtn.Visibility = preset ? Visibility.Hidden : Visibility.Visible;
        ResetBtn.IsEnabled = !preset && choice.Modified > 0;
        ResetBtn.ToolTip = LocalizationService.Get("S.Realtime.ProfileResetAll");

        // A preset's rows are drawn in the secondary colour where the accent would be, as on the page.
        foreach (var host in new FrameworkElement[] { MainRows, AdvancedRows })
        {
            if (preset && TryFindResource("AppTextSecondary") is Brush grey) host.Resources["AppAccent"] = grey;
            else host.Resources.Remove("AppAccent");
        }

        var stored = preset
            ? RealtimePresets.Create(id)
            : RealtimeProfileEditing.WithoutTemplateValues(
                Realtime.Profiles.FirstOrDefault(profile => profile.Id == id) ?? RealtimePresets.Create(null));
        var filled = stored.Filled();
        bool vertical = blocks[_block].Orientation == Services.Realtime.RealtimeTextOrientation.Vertical;

        foreach (var row in _rows.Values)
        {
            row.Dot.Visibility = !preset && RealtimeProfileEditing.IsModified(stored, row.Param)
                ? Visibility.Visible
                : Visibility.Hidden;
            bool unused = vertical && RealtimeProfileEditing.IsHorizontalOnly(row.Param);
            row.Root.Opacity = unused ? 0.5 : 1;
            if (row.Note is { } note) note.Visibility = unused ? Visibility.Visible : Visibility.Collapsed;
            ShowValue(row, filled);
        }

        int advancedModified = preset ? 0 : RealtimeProfileEditing.ModifiedCount(stored, RealtimeProfileEditing.Advanced);
        AdvancedText.Text = !_advanced && advancedModified > 0
            ? LocalizationService.Format("S.Realtime.ProfileAdvancedModified", advancedModified)
            : LocalizationService.Get("S.Realtime.ProfileAdvanced");
        AdvancedChevron.Text = _advanced ? "" : "";
        AdvancedCard.Visibility = _advanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowValue(Row row, RealtimeProfile filled)
    {
        switch (row.Param)
        {
            case ProfileParam.MergeLevel:
                row.Slider!.Value = filled.MergeLevel ?? RealtimeMergeLevels.AutomaticMiddle;
                break;
            case ProfileParam.Align:
                row.Switch!.SelectedIndex = AlignIndex(filled.Align);
                return;
            case ProfileParam.FilterNumbersAndAbbreviations:
                row.Check!.IsChecked = filled.FilterNumbersAndAbbreviations != false;
                return;
            case ProfileParam.ClearAfterEmptyReads:
                row.Check!.IsChecked = filled.ClearAfterEmptyReads == RealtimeProfile.MinClearAfterEmptyReads;
                return;
            case ProfileParam.SettleSeconds:
            {
                // The slider holds the figure for 手動 under 自動 too, so turning back to 手動 shows it.
                row.Slider!.Value = Math.Round(filled.SettleSeconds!.Value / PollSeconds);
                bool automatic = filled.SettleAutomatic == true;
                row.Switch!.IsSecondSelected = !automatic;
                row.Slider.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
                break;
            }
            case ProfileParam.DetectorPercent:
            {
                row.Slider!.Value = filled.DetectorPercent!.Value;
                bool automatic = filled.DetectorAutomatic == true;
                row.Switch!.IsSecondSelected = !automatic;
                row.Slider.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
                break;
            }
            default:
                row.Slider!.Value = Convert.ToDouble(RealtimeProfileEditing.Value(filled, row.Param));
                break;
        }
        row.Value!.Text = Display(filled, row.Param);
    }

    // ── Building the rows ────────────────────────────────────────────────────────────────────────

    private void BuildRows()
    {
        Add(MainRows, SliderRow(ProfileParam.MergeLevel, RealtimeMergeLevels.Min, RealtimeMergeLevels.Max, 1));
        Add(MainRows, AlignRow());
        Add(MainRows, TickRow(ProfileParam.FilterNumbersAndAbbreviations));
        Add(MainRows, SliderRow(ProfileParam.SettleSeconds, 0, Math.Round(RealtimeProfile.MaxSettleSeconds / PollSeconds), 1, automatic: true));
        Add(MainRows, TickRow(ProfileParam.ClearAfterEmptyReads));
        Add(MainRows, SliderRow(ProfileParam.DetectorPercent, RealtimeProfile.MinDetectorPercent, RealtimeProfile.MaxDetectorPercent, 1, automatic: true));
        Add(AdvancedRows, SliderRow(ProfileParam.IgnoreSmallPercent, 0, RealtimeProfile.MaxIgnoreSmallPercent, 5));
        Add(AdvancedRows, SliderRow(ProfileParam.ConfidencePercent, RealtimeProfile.MinConfidencePercent, RealtimeProfile.MaxConfidencePercent, 1));

        // A rule between rows, never above the first: the card's own edge is the first row's top.
        void Add(Panel group, Row row)
        {
            if (group.Children.Count > 0)
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
                rule.SetResourceReference(Border.BackgroundProperty, "ToolbarMenuBorder");
                group.Children.Add(rule);
            }
            group.Children.Add(row.Root);
            _rows[row.Param] = row;
        }
    }

    /// <summary>The name with the 已修改 dot before it, and the line under it for a vertical block.</summary>
    private static Row NewRow(ProfileParam param, Grid top)
    {
        var root = new StackPanel();
        root.Children.Add(top);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AppAccent");
        // The dot is 已修改, whatever colour the preset's lock gives the accent.
        dot.ToolTip = LocalizationService.Get("S.Realtime.ProfileModified");

        var name = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.TextProperty, NameKey(param));
        name.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        name.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        var heading = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        heading.Children.Add(dot);
        heading.Children.Add(name);
        top.Children.Add(heading);

        var row = new Row(param, root, dot);
        if (RealtimeProfileEditing.IsHorizontalOnly(param))
        {
            var note = new TextBlock { FontSize = 11, Margin = new Thickness(12, 2, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfileHorizontalOnlyTip");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
            note.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            root.Children.Add(note);
            row.Note = note;
        }
        return row;
    }

    private static Grid TopGrid()
    {
        var top = new Grid { MinHeight = 28 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return top;
    }

    private static TextBlock ValueText()
    {
        var value = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        value.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        value.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        Grid.SetColumn(value, 1);
        return value;
    }

    /// <summary>
    /// Name and value on top, the slider the width of the row under them — with 自動｜手動 before it
    /// where the value can be left to 自動, and no slider under 自動.
    /// </summary>
    private Row SliderRow(ProfileParam param, double min, double max, double step, bool automatic = false)
    {
        var top = TopGrid();
        var row = NewRow(param, top);
        var value = ValueText();
        top.Children.Add(value);
        row.Value = value;

        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (automatic)
        {
            var mode = new SegmentedSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetIsSharedSizeScope(mode, true);
            mode.SetLabels("S.Realtime.ProfileAuto", "S.Realtime.ProfileManual");
            mode.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, NameKey(param));
            System.Windows.Automation.AutomationProperties.SetAutomationId(mode, "Panel" + param + "Mode");
            mode.SelectionChanged += (_, manual) => Guard("automatic switch", () =>
                Commit(param, profile => SetAutomatic(profile, param, !manual)));
            DockPanel.SetDock(mode, Dock.Left);
            line.Children.Add(mode);
            row.Switch = mode;
        }

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            SmallChange = step,
            LargeChange = step,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.SetResourceReference(StyleProperty, "AppSlider");
        slider.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, NameKey(param));
        System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "Panel" + param + "Slider");
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = slider));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Guard("slider let go", () =>
        {
            _dragging = null;
            double position = slider.Value;
            Commit(param, profile => SetNumber(profile, param, position));
        })));
        slider.ValueChanged += (_, _) => Guard("slider moved", () =>
        {
            // The reading follows the thumb the whole way; the value is written when it is let go,
            // or at once when it moved by a click on the track, which has no end to wait for.
            row.Value!.Text = SliderReading(param, slider.Value);
            if (_showing || ReferenceEquals(_dragging, slider)) return;
            double position = slider.Value;
            Commit(param, profile => SetNumber(profile, param, position));
        });
        line.Children.Add(slider);
        row.Slider = slider;
        row.Root.Children.Insert(1, line);
        return row;
    }

    /// <summary>譯文對齊: 自動｜置中｜靠左 on the name's line.</summary>
    private Row AlignRow()
    {
        var top = TopGrid();
        var row = NewRow(ProfileParam.Align, top);
        var mode = new SegmentedSwitch { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetIsSharedSizeScope(mode, true);
        mode.SetLabels("S.Realtime.ProfileAuto", "S.Realtime.ProfileAlignCenter", "S.Realtime.ProfileAlignLeft");
        mode.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.Realtime.ProfileAlign");
        System.Windows.Automation.AutomationProperties.SetAutomationId(mode, "PanelAlignMode");
        mode.IndexChanged += (_, index) => Guard("align switch", () =>
            Commit(ProfileParam.Align, profile => profile.Align = index switch
            {
                1 => RealtimeTextAligns.Center,
                2 => RealtimeTextAligns.Left,
                _ => RealtimeTextAligns.Auto,
            }));
        Grid.SetColumn(mode, 1);
        top.Children.Add(mode);
        row.Switch = mode;
        return row;
    }

    /// <summary>An on/off: the settings page's switch on the name's line.</summary>
    private Row TickRow(ProfileParam param)
    {
        var top = TopGrid();
        var row = NewRow(param, top);
        var tick = new CheckBox { Focusable = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        tick.SetResourceReference(StyleProperty, "ToggleSwitch");
        tick.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, NameKey(param));
        System.Windows.Automation.AutomationProperties.SetAutomationId(tick, "Panel" + param + "Check");
        bool filter = param == ProfileParam.FilterNumbersAndAbbreviations;
        RoutedEventHandler changed = (_, _) => Guard("switch", () =>
        {
            if (_showing) return;
            bool on = tick.IsChecked == true;
            if (filter) Commit(param, profile => profile.FilterNumbersAndAbbreviations = on);
            else Commit(param, profile => profile.ClearAfterEmptyReads = on
                ? RealtimeProfile.MinClearAfterEmptyReads
                : RealtimeProfile.MaxClearAfterEmptyReads);
        });
        tick.Checked += changed;
        tick.Unchecked += changed;
        Grid.SetColumn(tick, 1);
        top.Children.Add(tick);
        row.Check = tick;
        return row;
    }

    // ── The mode list ────────────────────────────────────────────────────────────────────────────

    private void ModeBtn_Click(object sender, RoutedEventArgs e) => Guard("mode list", () =>
    {
        if (ModeListFrame.Visibility == Visibility.Visible)
        {
            CloseModeList();
            return;
        }
        RebuildModeList();
        ModeListFrame.Visibility = Visibility.Visible;
    });

    private void CloseModeList() => ModeListFrame.Visibility = Visibility.Collapsed;

    /// <summary>The presets, then the user's own modes under a rule, the block's own marked.</summary>
    private void RebuildModeList()
    {
        ModeList.Children.Clear();
        var current = ShownProfileId();
        var choices = Choices();
        bool ruled = false;
        foreach (var choice in choices)
        {
            if (!choice.IsPreset && !ruled)
            {
                var rule = new Border { Height = 1, Margin = new Thickness(4, 4, 4, 4) };
                rule.SetResourceReference(Border.BackgroundProperty, "ToolbarMenuBorder");
                ModeList.Children.Add(rule);
                ruled = true;
            }
            var id = choice.Id;
            var item = new Button
            {
                Content = new TextBlock { Text = choice.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = id == current ? "Current" : null,
            };
            item.SetResourceReference(StyleProperty, "ModeItem");
            System.Windows.Automation.AutomationProperties.SetName(item, choice.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "PanelMode_" + (choice.IsPreset ? id : "custom" + ModeList.Children.Count));
            item.Click += (_, _) => Guard("mode picked", () =>
            {
                CloseModeList();
                Session.SetBlockProfile(_block, id);
                Render();
            });
            ModeList.Children.Add(item);
        }
    }

    // ── Header, 進階 ─────────────────────────────────────────────────────────────────────────────

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e) => Guard("advanced", () =>
    {
        _advanced = !_advanced;
        Render();
        if (_advanced)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AdvancedCard.BringIntoView());
    });

    /// <summary>還原為範本 asks first — see the confirming card in the markup.</summary>
    private void ResetBtn_Click(object sender, RoutedEventArgs e) => Guard("reset", () =>
    {
        var id = ShownProfileId();
        var choice = Choices().FirstOrDefault(c => c.Id == id);
        if (choice is null || choice.IsPreset || choice.Modified == 0) return;
        CancelFork();
        CloseModeList();
        ConfirmMessage.Text = LocalizationService.Format(
            "S.Realtime.ProfileResetConfirm", RealtimeProfileEditing.PresetName(choice.BasedOn));
        ConfirmLayer.Visibility = Visibility.Visible;
    });

    private void ConfirmReset_Click(object sender, RoutedEventArgs e) => Guard("reset confirmed", () =>
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        var id = ShownProfileId();
        if (RealtimePresets.IsPreset(id)) return;
        Write(() => RealtimeProfileStore.ResetAll(id));
        Render();
    });

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    // Presses inside a card must not reach the scrim behind it.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ── Split a preset ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A preset's row was given a change: the card offers it on a mode of the user's own, split off
    /// the preset — the page's card does the same (#417). The row shows the change in the preset's
    /// grey until the card is answered; 取消 puts it back.
    /// </summary>
    private void OfferFork(ProfileParam param, Action<RealtimeProfile> change)
    {
        var presetId = ShownProfileId();
        _forkChange = change;
        bool full = RealtimeProfileStore.IsFull;
        var trial = RealtimeProfile.CreateCustom(presetId, "");
        change(trial);
        ForkChangeText.Text = full
            ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles)
            : LocalizationService.Format("S.Realtime.ProfileForkChange",
                LocalizationService.Get(NameKey(param)),
                ValueName(RealtimePresets.Create(presetId).Filled(), param),
                ValueName(trial.Filled(), param));
        ForkCreateBtn.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        ForkCancelBtn.Content = LocalizationService.Get(full ? "S.Common.Close" : "S.Common.Cancel");
        CloseModeList();
        ForkLayer.Visibility = Visibility.Visible;
    }

    private void CancelFork()
    {
        if (ForkLayer.Visibility != Visibility.Visible) return;
        ForkLayer.Visibility = Visibility.Collapsed;
        _forkChange = null;
        Render();
    }

    private void ForkScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Guard("fork cancelled", CancelFork);

    private void ForkCancel_Click(object sender, RoutedEventArgs e) => Guard("fork cancelled", CancelFork);

    private void ForkCreate_Click(object sender, RoutedEventArgs e) => Guard("fork", () => Fork(_forkChange));

    private void ForkBtn_Click(object sender, RoutedEventArgs e) => Guard("fork", () => Fork(null));

    /// <summary>
    /// Makes a custom mode of the preset the block is on, with <paramref name="change"/> on it; the
    /// blocks on the preset move onto it (RealtimeProfileStore.Fork), so the panel now shows it.
    /// </summary>
    private void Fork(Action<RealtimeProfile>? change)
    {
        ForkLayer.Visibility = Visibility.Collapsed;
        _forkChange = null;
        var presetId = ShownProfileId();
        if (RealtimePresets.IsPreset(presetId)) Write(() => RealtimeProfileStore.Fork(presetId, change));
        Render();
    }

    // ── Parts ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One value's row and the parts of it that change.</summary>
    private sealed class Row(ProfileParam param, StackPanel root, FrameworkElement dot)
    {
        public ProfileParam Param { get; } = param;
        public StackPanel Root { get; } = root;
        public FrameworkElement Dot { get; } = dot;
        public TextBlock? Value { get; set; }
        public Slider? Slider { get; set; }
        public SegmentedSwitch? Switch { get; set; }
        public CheckBox? Check { get; set; }
        public TextBlock? Note { get; set; }
    }
}
