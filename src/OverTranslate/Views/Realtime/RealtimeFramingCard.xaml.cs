using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The 翻譯區域預設 card on the 即時翻譯 page: the presets of the screen or window chosen in 步驟 1／2,
/// with the one 開始 would put on it marked 選用中, and every other target's folded into one row.
/// </summary>
/// <remarks>
/// <para>Only the list lives here. Keeping a framing, and choosing which of a target's several it
/// opens on, are on the floating bar, where the blocks are — see
/// <see cref="RealtimeSessionController.SaveFraming"/>. The rows are not controls: a row that picked
/// itself on a click also turned the page's capture source, and the two lists then disagreed about
/// what "selected" meant. Here a preset is only renamed (✎)
/// or deleted (🗑, asked first). Both open over the window from <see cref="RealtimeFramingOverlay"/>.</para>
///
/// <para>The upper half follows 步驟 1／2 (<see cref="SetTarget"/>): named by the target, so 選用中
/// says whom it is chosen for, and swapped with a short fade when the choice changes while the lower
/// half stays where it is. Past three presets it folds as the 模式 card's custom list does, with the
/// one in use first. See <see cref="RealtimeFramingStore.GroupFor"/> for the split.</para>
///
/// <para>Renaming and deleting stay open while translating: they only touch the file, and a session
/// already running keeps the blocks it opened with.</para>
/// </remarks>
public partial class RealtimeFramingCard : UserControl
{
    // How tall a row's picture is drawn, in DIP.
    private const double ThumbnailHeight = 30;

    private static readonly Duration TurnDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(150));

    // The screen or window 步驟 1／2 has chosen; null while 視窗擷取 has no window.
    private RealtimeFramingStore.Target? _target;

    // Whether 其他螢幕或視窗 is open. Not kept: leaving the page and coming back closes it.
    private bool _othersOpen;

    // Whether the chosen target's own list is unfolded. Not kept either, and folded again when
    // 步驟 1／2 turns to another target.
    private bool _currentExpanded;

    public RealtimeFramingCard()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnChanged;
        SettingsService.Instance.RealtimeFramingChanged += OnChanged;
        Unloaded += (_, _) =>
        {
            if (!_othersOpen && !_currentExpanded) return;
            _othersOpen = false;
            _currentExpanded = false;
            Render();
        };
        Render();
    }

    /// <summary>Re-reads the kept framings and lists them, after the page itself was reloaded.</summary>
    public void Reload() => Render();

    /// <summary>
    /// The screen or window 步驟 1／2 has chosen now — null while 視窗擷取 has none, or it has
    /// gone. The upper half is redrawn for it, fading in when it is another target than before.
    /// </summary>
    internal void SetTarget(RealtimeFramingStore.Target? target)
    {
        if (Nullable.Equals(_target, target)) return;

        bool another = _target?.Key != target?.Key || _target?.WindowTitle != target?.WindowTitle;
        _target = target;
        if (another) _currentExpanded = false;
        Render();
        if (another) FadeInCurrent();
    }

    /// <summary>Lets go of the static events, when the window holding the page goes.</summary>
    public void Teardown()
    {
        LocalizationService.LanguageChanged -= OnChanged;
        SettingsService.Instance.RealtimeFramingChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    private RealtimeFramingStore.CardGroups Groups() =>
        RealtimeFramingStore.GroupFor(Realtime.FramingPresets, _target, Realtime.FramingSkippedTargets);

    private void Render()
    {
        var presets = Realtime.FramingPresets;
        var groups = Groups();

        // The upper half: the chosen target's presets, or the one sentence when it has none. With no
        // target and nothing kept anywhere, only the sentence.
        bool hasTarget = _target is not null;
        CurrentPanel.Visibility = hasTarget || presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CurrentCaption.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        CurrentCaption.Text = _target is { } target ? RealtimeFramingDisplay.Caption(target) : "";

        // Past three, folded: the one in use is already first (GroupFor), so it is always in view.
        var (shown, hidden) = CollapsibleRows.Shown(groups.Current.Count, null, _currentExpanded);
        Fill(ListHost, shown.Select(index => groups.Current[index])
            .Select(preset => Row(preset, preset.Id == groups.OpensOn?.Id, other: false)));
        if (CollapsibleRows.HasMoreRow(groups.Current.Count, hidden, _currentExpanded))
        {
            ListHost.Children.Add(CollapsibleRows.MoreRow(hidden, _currentExpanded, "S.Realtime.ShowMoreFramings",
                "FramingPresetMore", () => { _currentExpanded = !_currentExpanded; Render(); }));
        }
        ListBorder.Visibility = groups.Current.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyBox.Visibility = groups.Current.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // The hint explains a list; with nothing kept at all, the sentence in its place says it all.
        HintText.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The lower half: one row for every other target's presets, open or not.
        OthersPanel.Visibility = groups.Others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OthersPanel.Margin = new Thickness(0, CurrentPanel.Visibility == Visibility.Visible ? 10 : 0, 0, 0);
        OthersCount.Text = LocalizationService.Format("S.Realtime.FramingOthersCount", groups.Others.Count);
        AutomationProperties.SetName(OthersToggle,
            $"{LocalizationService.Get("S.Realtime.FramingOthers")} {OthersCount.Text}");
        AutomationProperties.SetItemStatus(OthersToggle, _othersOpen ? "expanded" : "collapsed");

        bool open = _othersOpen && groups.Others.Count > 0;
        Fill(OthersHost, open ? groups.Others.Select(preset => Row(preset, inUse: false, other: true)) : []);
        OthersListBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Turn(open, animate: false);
    }

    /// <summary>Puts <paramref name="rows"/> in <paramref name="host"/>, the last without its rule.</summary>
    /// <remarks>Before the 顯示其餘 row is added, which brings its own rule.</remarks>
    private static void Fill(StackPanel host, IEnumerable<Border> rows)
    {
        host.Children.Clear();
        foreach (var row in rows) host.Children.Add(row);

        // The last row drops its rule so it does not double the list's own border.
        if (host.Children.Count > 0)
            ((Border)host.Children[^1]).BorderThickness = new Thickness(0);
    }

    /// <summary>
    /// One kept framing: its picture, its name (and 選用中) over its size and blocks — with the
    /// target's name first under 其他螢幕或視窗 — then ✎ and 🗑.
    /// </summary>
    private Border Row(RealtimeFramingPreset preset, bool inUse, bool other)
    {
        var name = RealtimeFramingDisplay.Name(preset);

        var thumbnail = new Border
        {
            Child = RealtimeFramingThumbnail.Create(preset, ThumbnailHeight),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };

        // The name gives way, the badge does not: a star column in a left-aligned grid sizes to the
        // name until the row runs out, then cuts the name and keeps 選用中 whole beside it.
        var title = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        OverTranslate.Controls.TrimmedToolTip.SetIsEnabled(title, true);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        title.SetResourceReference(TextBlock.FontSizeProperty, "AppFontSize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        var nameLine = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameLine.Children.Add(title);
        if (inUse)
        {
            var badgeText = new TextBlock
            {
                Text = LocalizationService.Get("S.Realtime.FramingInUse"),
                FontSize = 11.5,
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "AppAccent");
            var badge = new Border { Child = badgeText };
            badge.SetResourceReference(StyleProperty, "FramingInUseBadge");
            AutomationProperties.SetAutomationId(badge, "FramingInUse");
            Grid.SetColumn(badge, 1);
            nameLine.Children.Add(badge);
        }

        var detail = new TextBlock
        {
            Text = other ? RealtimeFramingDisplay.OtherDetail(preset) : RealtimeFramingDisplay.SizeAndBlocks(preset),
            FontSize = 12,
            LineHeight = 16,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        detail.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        OverTranslate.Controls.TrimmedToolTip.SetIsEnabled(detail, true);

        var text = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        text.Children.Add(nameLine);
        text.Children.Add(detail);

        string id = preset.Id;
        var edit = IconButton("FramingEditGlyph", "S.Realtime.FramingEdit", "FramingEdit_" + id);
        edit.Click += (_, _) => (Window.GetWindow(this) as Shell.ShellWindow)?.OpenRealtimeFramingEdit(id);
        var delete = IconButton("FramingDeleteGlyph", "S.Realtime.FramingDelete", "FramingDelete_" + id);
        delete.Margin = new Thickness(2, 0, 0, 0);
        delete.Click += (_, _) => (Window.GetWindow(this) as Shell.ShellWindow)?.OpenRealtimeFramingDelete(id);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(thumbnail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        edit.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(edit, 2);
        grid.Children.Add(edit);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        var row = new Border { Child = grid };
        row.SetResourceReference(StyleProperty, "FramingRow");
        AutomationProperties.SetName(row, inUse ? $"{name} {LocalizationService.Get("S.Realtime.FramingInUse")}" : name);
        AutomationProperties.SetAutomationId(row, "FramingRow_" + id);
        return row;
    }

    /// <summary>✎ or 🗑: a mark drawn on a 16 grid, named and tipped with what it does.</summary>
    private static Button IconButton(string geometryKey, string labelKey, string automationId)
    {
        var label = LocalizationService.Get(labelKey);

        var path = new System.Windows.Shapes.Path
        {
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, geometryKey);

        var button = new Button
        {
            Content = new Grid { Width = 16, Height = 16, Children = { path }, SnapsToDevicePixels = true },
            ToolTip = label,
        };
        // The mark takes the button's colour, which the style turns up under the pointer.
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        button.SetResourceReference(StyleProperty, "FramingRowIconButton");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    // ── 其他螢幕或視窗 ──────────────────────────────────────────────────────────────────────

    private void OthersToggle_Click(object sender, RoutedEventArgs e)
    {
        _othersOpen = !_othersOpen;
        // Turned first: the redraw sets the chevron straight to its angle unless it is turning.
        Turn(_othersOpen, animate: true);
        Render();
    }

    /// <summary>Turns the chevron down or up, over a moment unless animations are off.</summary>
    private void Turn(bool open, bool animate)
    {
        double angle = open ? 180 : 0;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            if (OthersChevronTurn.HasAnimatedProperties && !animate) return;
            OthersChevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            OthersChevronTurn.Angle = angle;
            return;
        }

        var turn = new DoubleAnimation
        {
            To = angle,
            Duration = TurnDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        turn.Completed += (_, _) =>
        {
            OthersChevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            OthersChevronTurn.Angle = angle;
        };
        OthersChevronTurn.BeginAnimation(RotateTransform.AngleProperty, turn);
    }

    /// <summary>The upper half arriving for another target: a short fade in, or straight in.</summary>
    private void FadeInCurrent()
    {
        if (!SystemParameters.ClientAreaAnimation || !IsLoaded) return;

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            CurrentPanel.BeginAnimation(OpacityProperty, null);
            CurrentPanel.Opacity = 1;
        };
        CurrentPanel.BeginAnimation(OpacityProperty, fade);
    }

    // ── Clipping ────────────────────────────────────────────────────────────────────────────

    // Round the list to the inside of its border, so nothing inside it squares off the corners.
    private void ListBorder_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ListBorder.Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);

    // Round the list under 其他螢幕或視窗 the same way.
    private void OthersListBorder_SizeChanged(object sender, SizeChangedEventArgs e) =>
        OthersListBorder.Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);
}
