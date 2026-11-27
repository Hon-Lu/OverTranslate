using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The 模式 card on the 即時翻譯 page: the three presets, then the custom modes with the add as
/// their last place. A row opens <see cref="RealtimeProfileOverlay"/> on its mode.
/// </summary>
/// <remarks>
/// <para>Only the list lives on the page: what is looked at every time — languages, capture,
/// appearance — stays in view, and a mode's values, changed now and then, are one level down. The
/// list has no selected row, so it cannot read as choosing the mode in use; each block has its
/// own, which is what the hint under the heading says. Drawn again whenever the modes change,
/// wherever they were changed from.</para>
/// <para>Two lines a mode — the name, and under it a preset's summary or the preset a custom mode
/// is made from — because one line a mode put the names in a column whose width followed the
/// longest custom name, and the rows went ragged. Stacked, a name has the row's whole width and
/// nothing beside it has to line up with it. The icon square at the left gives the list the
/// column the eye runs down instead, and its tint tells the presets from the custom modes.</para>
/// <para>The presets and the custom modes are two groups, the add the custom group's last place:
/// beside the heading, as a line of text, it was missed; under a group of its own it reads as that
/// group's empty slot rather than as a step before 選取翻譯區塊. With no custom mode it is the whole
/// of the group.</para>
/// </remarks>
public partial class RealtimeProfilesCard : UserControl
{
    // The icons as line drawings on a 16 grid, stroked 1.2 wide (ProfileIconPath): 標準 a sparkle
    // for working it out on its own, 字幕・漫畫 a frame with two caption lines, 遊戲介面・聊天室
    // the panels of an interface, a custom mode the sliders of values set by hand. Drawn rather
    // than font glyphs so the four share one weight and look, and because the sparkle (F4A5) is
    // missing from Windows 10's Segoe MDL2 Assets (19045's segmdl2.ttf), which showed an empty
    // square there. The sparkle is a stroked star and a small filled one.
    private static readonly Geometry StandardStroke = Freeze(Geometry.Parse(
        "M7,1.8 L8.3,5.2 11.7,6.5 8.3,7.8 7,11.2 5.7,7.8 2.3,6.5 5.7,5.2 Z"));
    private static readonly Geometry StandardFill = Freeze(Geometry.Parse(
        "M12.3,10.2 L12.9,11.7 14.4,12.3 12.9,12.9 12.3,14.4 11.7,12.9 10.2,12.3 11.7,11.7 Z"));
    private static readonly Geometry SubtitleStroke = Freeze(Group(
        new RectangleGeometry(new Rect(1.8, 3, 12.4, 10), 2, 2),
        Geometry.Parse("M4.5,8.6 H11.5 M5.8,10.8 H10.2")));
    private static readonly Geometry InterfaceStroke = Freeze(Group(
        new RectangleGeometry(new Rect(2, 2, 5.2, 5.2), 1.2, 1.2),
        new RectangleGeometry(new Rect(8.8, 2, 5.2, 5.2), 1.2, 1.2),
        new RectangleGeometry(new Rect(2, 8.8, 12, 5.2), 1.2, 1.2)));
    private static readonly Geometry CustomStroke = Freeze(Group(
        Geometry.Parse("M2.5,4.5 H3.7 M7.3,4.5 H13.5 M2.5,11.5 H8.7 M12.3,11.5 H13.5"),
        new EllipseGeometry(new Point(5.5, 4.5), 1.8, 1.8),
        new EllipseGeometry(new Point(10.5, 11.5), 1.8, 1.8)));

    private static Geometry Group(params Geometry[] parts)
    {
        var group = new GeometryGroup();
        foreach (var part in parts)
            group.Children.Add(part);
        return group;
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // The custom modes listed last time, so a mode that is new since — made in the overlay, or
    // forked from a preset — can be shown and scrolled to. Null before the first render: nothing is
    // new then.
    private HashSet<string>? _listedCustomIds;

    // The mode made most recently on this visit to the page: listed first, folded or not — the list
    // has no selected row of its own, and a mode just made that landed among the folded rows would
    // look as if it had not been made. The folded list stays three rows; see CollapsibleRows.
    private string? _addedCustomId;

    // Whether the user unfolded the custom list. Not kept: leaving the page and coming back folds it.
    private bool _expanded;

    public RealtimeProfilesCard()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnChanged;
        SettingsService.Instance.RealtimeProfilesChanged += OnChanged;
        Unloaded += (_, _) =>
        {
            if (!_expanded && _addedCustomId is null) return;
            _expanded = false;
            _addedCustomId = null;
            RenderList();
        };
        Reload();
    }

    /// <summary>Re-reads the modes and lists them.</summary>
    public void Reload() => RenderList();

    /// <summary>Lets go of the static events, when the window holding the page goes.</summary>
    public void Teardown()
    {
        LocalizationService.LanguageChanged -= OnChanged;
        SettingsService.Instance.RealtimeProfilesChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => RenderList();

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    private void RenderList()
    {
        var choices = RealtimeProfileEditing.Choices(Realtime.Profiles);
        Fill(PresetHost, choices.Where(choice => choice.IsPreset));
        var customs = choices.Where(choice => !choice.IsPreset).ToList();

        var ids = customs.Select(choice => choice.Id).ToHashSet();
        var added = _listedCustomIds is null ? null : customs.LastOrDefault(choice => !_listedCustomIds.Contains(choice.Id))?.Id;
        _listedCustomIds = ids;
        if (added is not null) _addedCustomId = added;

        int pinned = customs.FindIndex(choice => choice.Id == _addedCustomId);
        var (shown, hidden) = CollapsibleRows.Shown(customs.Count, pinned >= 0 ? pinned : null, _expanded);
        Fill(CustomHost, shown.Select(index => customs[index]));
        // 顯示其餘 N 個 / 收起 as the list's last row; ＋ 新增自訂模式 stays the list's one button,
        // under it.
        if (CollapsibleRows.HasMoreRow(customs.Count, hidden, _expanded))
        {
            CustomHost.Children.Add(CollapsibleRows.MoreRow(hidden, _expanded, "S.Realtime.ShowMoreProfiles",
                "ProfileCustomMore", () => { _expanded = !_expanded; RenderList(); }));
        }
        CustomList.Visibility = CustomHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // After layout, so a new row has a place to be scrolled to.
        if (added is not null)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                CustomHost.Children.OfType<Button>()
                    .FirstOrDefault(row => System.Windows.Automation.AutomationProperties.GetAutomationId(row) == "ProfileRow_" + added)
                    ?.BringIntoView());
        }

        int count = Realtime.Profiles.Count;
        int max = RealtimeSettings.MaxCustomProfiles;
        bool full = count >= max;
        CustomCount.Text = $"{count}/{max}";
        AddButtonText.Text = full
            ? LocalizationService.Format("S.Realtime.ProfileLimit", max)
            : LocalizationService.Get("S.Realtime.ProfileAdd");
        // At the cap the text is the limit, not an action, so the + goes with it.
        AddButtonGlyph.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        AddButton.IsEnabled = !full;
        System.Windows.Automation.AutomationProperties.SetName(AddButton, AddButtonText.Text);
    }

    private void Fill(StackPanel host,IEnumerable<ProfileChoice> choices)
    {
        host.Children.Clear();
        foreach (var choice in choices)
            host.Children.Add(ListRow(choice));
        if (host.Children.Count > 0)
            ((Button)host.Children[^1]).BorderThickness = new Thickness(0);
    }

    /// <summary>
    /// The icon square, the name, under it a preset's summary or the preset a custom mode is made
    /// from and how many values it changed, then ›.
    /// </summary>
    /// <remarks>
    /// The name keeps to one line, cut with the whole of it on the tooltip; the line under it
    /// wraps rather than being cut: it is the one thing the row says about the mode.
    /// </remarks>
    private Button ListRow(ProfileChoice choice)
    {
        var tile = new Border { Child = Icon(choice) };
        tile.SetResourceReference(StyleProperty, choice.IsPreset ? "ProfileIconTile" : "ProfileIconTileCustom");

        var name = new TextBlock
        {
            Text = choice.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (!choice.IsPreset)
            name.ToolTip = choice.Name;

        var description = new TextBlock
        {
            Text = choice.IsPreset ? RealtimeProfileEditing.PresetSummary(choice.Id) : RealtimeProfileEditing.TemplateLine(choice),
            FontSize = 12,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(description);

        // Only the colour answers the pointer: a glyph moved on hover is blurred for a moment
        // while WPF re-renders the text at its new position.
        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
        };
        chevron.SetResourceReference(StyleProperty, "ProfileChevron");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(tile);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var row = new Button { Content = grid };
        row.SetResourceReference(StyleProperty, "ProfileListRow");
        System.Windows.Automation.AutomationProperties.SetName(row, choice.Name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(row, "ProfileRow_" + choice.Id);
        var id = choice.Id;
        row.Click += (_, _) => Shell()?.OpenRealtimeProfile(id);
        return row;
    }

    /// <summary>The mode's icon, in the accent for a preset and the secondary text colour for a custom mode.</summary>
    private static FrameworkElement Icon(ProfileChoice choice)
    {
        string brush = choice.IsPreset ? "AppAccent" : "AppTextSecondary";
        var (stroke, fill) = !choice.IsPreset ? (CustomStroke, null)
            : choice.Id switch
            {
                RealtimePresets.SubtitleId => (SubtitleStroke, null),
                RealtimePresets.InterfaceId => (InterfaceStroke, null),
                _ => (StandardStroke, (Geometry?)StandardFill),
            };

        var icon = new Grid();
        var outline = new System.Windows.Shapes.Path { Data = stroke };
        outline.SetResourceReference(StyleProperty, "ProfileIconPath");
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        icon.Children.Add(outline);
        if (fill is not null)
        {
            var solid = new System.Windows.Shapes.Path { Data = fill };
            solid.SetResourceReference(StyleProperty, "ProfileIconPath");
            solid.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
            icon.Children.Add(solid);
        }
        return icon;
    }

    private Shell.ShellWindow? Shell() => Window.GetWindow(this) as Shell.ShellWindow;

    private void AddButton_Click(object sender, RoutedEventArgs e) => Shell()?.OpenNewRealtimeProfile();

    // Round each list to the inside of its border, so a hovered row does not square off the corners.
    private void ListHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);

}
