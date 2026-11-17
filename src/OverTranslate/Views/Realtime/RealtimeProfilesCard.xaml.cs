using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The 模式 card on the 即時翻譯 page: the three presets and the custom modes, one row each, and the
/// row that adds one. A row opens <see cref="RealtimeProfileOverlay"/> on its mode.
/// </summary>
/// <remarks>
/// Only the list lives on the page: what is looked at every time — languages, capture, appearance —
/// stays in view, and a mode's values, changed now and then, are one level down. The list has no
/// selected row, so it cannot read as choosing the mode in use; each block has its own. Drawn again
/// whenever the modes change, wherever they were changed from.
/// </remarks>
public partial class RealtimeProfilesCard : UserControl
{
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
        ListHost.Children.Clear();
        foreach (var choice in RealtimeProfileEditing.Choices(Realtime.Profiles))
            ListHost.Children.Add(ListRow(choice));

        int count = Realtime.Profiles.Count;
        bool full = count >= RealtimeSettings.MaxCustomProfiles;
        AddRowText.Text = LocalizationService.Format("S.Realtime.ProfileAdd", count, RealtimeSettings.MaxCustomProfiles);
        AddRow.IsEnabled = !full;
        AddRow.ToolTip = full ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles) : null;
    }

    /// <summary>
    /// The name — a preset's with 預設 after it — over one line: a preset's summary, or the preset a
    /// custom mode is made from and how many values it changed; then ›.
    /// </summary>
    private Button ListRow(ProfileChoice choice)
    {
        var name = new TextBlock { Text = choice.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(name);
        if (choice.IsPreset)
        {
            var tagText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Normal };
            tagText.SetResourceReference(TextBlock.TextProperty, "S.Realtime.ProfilePresetTag");
            tagText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            var tag = new Border { Child = tagText };
            tag.SetResourceReference(StyleProperty, "ProfileTag");
            line1.Children.Add(tag);
        }
        else
        {
            name.ToolTip = choice.Name;
        }

        var line2 = new TextBlock
        {
            Text = choice.IsPreset ? RealtimeProfileEditing.PresetSummary(choice.Id) : RealtimeProfileEditing.TemplateLine(choice),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        line2.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var text = new StackPanel();
        text.Children.Add(line1);
        text.Children.Add(line2);

        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var row = new Button { Content = grid };
        row.SetResourceReference(StyleProperty, "ProfileListRow");
        System.Windows.Automation.AutomationProperties.SetName(row, choice.Name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(row, "ProfileRow_" + choice.Id);
        var id = choice.Id;
        row.Click += (_, _) => Shell()?.OpenRealtimeProfile(id);
        return row;
    }

    private Shell.ShellWindow? Shell() => Window.GetWindow(this) as Shell.ShellWindow;

    private void AddRow_Click(object sender, RoutedEventArgs e) => Shell()?.OpenNewRealtimeProfile();

    // Round the list to the inside of its border, so a filled row does not square off the corners.
    private void ListBody_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);
}
