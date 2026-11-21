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
    // Points both Segoe Fluent Icons and Windows 10's Segoe MDL2 Assets have: 標準 the gears of
    // working it out on its own, 字幕・漫畫 the caption frame, 遊戲介面・聊天室 the panels of an
    // interface; a custom mode the sliders of values set by hand. Not the sparkle (F4A5), which
    // reads better as "automatic" but is missing from Windows 10's MDL2 (checked against 19045's
    // segmdl2.ttf), so the row would show an empty square there.
    private const string StandardGlyph = "";
    private const string SubtitleGlyph = "";
    private const string InterfaceGlyph = "";
    private const string CustomGlyph = "";

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // How many custom rows show before the list scrolls, and how much of the next one shows under
    // them: a row cut through the middle says there is more to scroll to, where four whole rows
    // and the list's border would read as all there is.
    private const int VisibleCustomRows = 4;
    private const double NextRowPeek = 0.5;

    // The custom modes listed last time, so a mode that is new since — made in the overlay, or
    // forked from a preset — can be scrolled to. Null before the first render: nothing is new then.
    private HashSet<string>? _listedCustomIds;

    public RealtimeProfilesCard()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnChanged;
        SettingsService.Instance.RealtimeProfilesChanged += OnChanged;
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
        Fill(CustomHost, customs);
        CustomList.Visibility = CustomHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var ids = customs.Select(choice => choice.Id).ToHashSet();
        var added = _listedCustomIds is null ? null : customs.LastOrDefault(choice => !_listedCustomIds.Contains(choice.Id))?.Id;
        _listedCustomIds = ids;
        // After layout, so the rows have their heights: the cap is measured from them, and a new
        // row can only be scrolled to once it has a place.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            FitCustomScroll();
            if (added is not null)
                CustomHost.Children.OfType<Button>()
                    .FirstOrDefault(row => System.Windows.Automation.AutomationProperties.GetAutomationId(row) == "ProfileRow_" + added)
                    ?.BringIntoView();
        });

        int count = Realtime.Profiles.Count;
        int max = RealtimeSettings.MaxCustomProfiles;
        bool full = count >= max;
        CustomCount.Text = $"{count}/{max}";
        AddButtonText.Text = full
            ? LocalizationService.Format("S.Realtime.ProfileLimit", max)
            : LocalizationService.Get("S.Realtime.ProfileAdd");
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
        var glyph = new TextBlock
        {
            Text = choice.IsPreset ? PresetGlyph(choice.Id) : CustomGlyph,
            FontFamily = IconFont,
            FontSize = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, choice.IsPreset ? "AppAccent" : "AppTextSecondary");
        var tile = new Border { Child = glyph };
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

    private static string PresetGlyph(string id) => id switch
    {
        RealtimePresets.SubtitleId => SubtitleGlyph,
        RealtimePresets.InterfaceId => InterfaceGlyph,
        _ => StandardGlyph,
    };

    private Shell.ShellWindow? Shell() => Window.GetWindow(this) as Shell.ShellWindow;

    private void AddButton_Click(object sender, RoutedEventArgs e) => Shell()?.OpenNewRealtimeProfile();

    // Round each list to the inside of its border, so a hovered row does not square off the corners.
    // The custom list's clip is on its scroller, the part that stays put, not on the rows that move.
    private void ListHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);

    private void CustomScroll_SizeChanged(object sender, SizeChangedEventArgs e) => ListHost_SizeChanged(sender, e);

    // A row's height changes with the width (a long template line wraps) and with the language.
    private void CustomHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitCustomScroll();

    /// <summary>
    /// Caps the custom list at <see cref="VisibleCustomRows"/> rows and part of the next, measured
    /// from the rows as laid out, or lets it take its full height when there are no more than that.
    /// </summary>
    /// <remarks>
    /// Measured rather than a fixed number of DIP: a row is one line of name and one of template in
    /// most languages, but the line wraps in a narrow window or a long Russian template, and a
    /// fixed cap would then show three and a bit rows in one place and four and a bit in another.
    /// About 250 DIP, so the list and the add under it still fit the page at the smallest window.
    /// </remarks>
    private void FitCustomScroll()
    {
        var rows = CustomHost.Children.OfType<FrameworkElement>().ToList();
        if (rows.Count <= VisibleCustomRows || rows.Any(row => row.ActualHeight <= 0))
        {
            CustomScroll.MaxHeight = double.PositiveInfinity;
            return;
        }
        CustomScroll.MaxHeight = Math.Ceiling(rows.Take(VisibleCustomRows).Sum(row => row.ActualHeight)
                                              + rows[VisibleCustomRows].ActualHeight * NextRowPeek);
    }

    /// <summary>
    /// Hands the wheel to the page once the custom list can go no further that way, or when it
    /// does not scroll at all.
    /// </summary>
    /// <remarks>
    /// A ScrollViewer marks every wheel it gets as handled, scrolled or not, so without this the
    /// page stopped dead whenever the pointer crossed the list: at its end, or for good when the
    /// list was short enough not to scroll.
    /// </remarks>
    private void CustomScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        bool up = e.Delta > 0;
        bool canScroll = up ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight;
        if (canScroll) return;

        e.Handled = true;
        var forward = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = scroll,
        };
        CustomList.RaiseEvent(forward);
    }
}
