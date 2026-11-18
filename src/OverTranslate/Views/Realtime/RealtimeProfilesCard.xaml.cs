using System.Globalization;
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
/// The 模式 card on the 即時翻譯 page: the three presets and the custom modes, one line each, and
/// the add beside the heading. A row opens <see cref="RealtimeProfileOverlay"/> on its mode.
/// </summary>
/// <remarks>
/// <para>Only the list lives on the page: what is looked at every time — languages, capture,
/// appearance — stays in view, and a mode's values, changed now and then, are one level down. The
/// list has no selected row, so it cannot read as choosing the mode in use; each block has its
/// own. Drawn again whenever the modes change, wherever they were changed from.</para>
/// <para>One line a mode — the name, its description in grey beside it, › — because the card sits
/// between 顯示外觀 and 選取翻譯區塊, and as a list of two-line rows with a 預設 tag on three of
/// them it read as a step to be worked through before starting, when most people never need it.
/// The names are set in the ordinary weight so they read as a column of entries, not headings,
/// and on a tinted band (ProfileNameBand) so that column is told from the descriptions at a glance:
/// as plain text side by side the two read as one run of words.</para>
/// </remarks>
public partial class RealtimeProfilesCard : UserControl
{
    // The band's own padding either side of the name (ProfileNameBand), the room between the band
    // and the description, and the share of the row a custom name's band may take before the name
    // is cut: past that a long name would push its description into a sliver.
    private const double BandPadding = 16 + 12;
    private const double DescriptionGap = 14;
    private const double CustomNameShare = 0.4;

    private double _presetNameWidth;

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
        _presetNameWidth = PresetNameWidth(choices);

        ListHost.Children.Clear();
        foreach (var choice in choices)
            ListHost.Children.Add(ListRow(choice));
        if (ListHost.Children.Count > 0)
            ((Button)ListHost.Children[^1]).BorderThickness = new Thickness(0);
        CapCustomNames(ListHost.ActualWidth);

        int count = Realtime.Profiles.Count;
        bool full = count >= RealtimeSettings.MaxCustomProfiles;
        AddButtonText.Text = LocalizationService.Format("S.Realtime.ProfileAdd", count, RealtimeSettings.MaxCustomProfiles);
        AddButton.IsEnabled = !full;
        AddButton.ToolTip = full ? LocalizationService.Format("S.Realtime.ProfileLimit", RealtimeSettings.MaxCustomProfiles) : null;
    }

    /// <summary>
    /// The width of the presets' name column: the widest of their three names in the font in
    /// effect, so their descriptions start on one line down the list.
    /// </summary>
    /// <remarks>
    /// Measured rather than shared through a SharedSizeGroup: a shared column would also take in
    /// the custom names, and one long one would push every description across. Measured again on
    /// every render, which a change of language always causes, because the names and the font
    /// (AppFont is swapped per language before LanguageChanged) both change with it.
    /// </remarks>
    private double PresetNameWidth(IReadOnlyList<ProfileChoice> choices)
    {
        var family = TryFindResource("AppFont") as FontFamily ?? new FontFamily("Segoe UI");
        var size = TryFindResource("AppFontSize") is double s ? s : 13;
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double widest = 0;
        foreach (var choice in choices.Where(choice => choice.IsPreset))
        {
            var text = new FormattedText(choice.Name, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                typeface, size, System.Windows.Media.Brushes.Black, dip);
            widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
        }
        // A pixel or two over the measure, so rounding never trims a preset's own name.
        return Math.Ceiling(widest) + 2;
    }

    /// <summary>
    /// The name, then its description in grey — a preset's summary, or the preset a custom mode is
    /// made from and how many values it changed — then ›.
    /// </summary>
    /// <remarks>
    /// A custom name takes at least the presets' column, so a short one keeps the descriptions in
    /// line, and at most <see cref="CustomNameShare"/> of the row, past which it is cut with the
    /// whole of it on the tooltip. The description wraps rather than being cut: it is the one
    /// thing the row says about the mode.
    /// </remarks>
    private Button ListRow(ProfileChoice choice)
    {
        var name = new TextBlock
        {
            Text = choice.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (choice.IsPreset)
        {
            name.Width = _presetNameWidth;
        }
        else
        {
            name.MinWidth = _presetNameWidth;
            name.ToolTip = choice.Name;
            name.Tag = CustomNameTag;
        }

        var description = new TextBlock
        {
            Text = choice.IsPreset ? RealtimeProfileEditing.PresetSummary(choice.Id) : RealtimeProfileEditing.TemplateLine(choice),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(DescriptionGap, 8, 0, 8),
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var band = new Border { Child = name };
        band.SetResourceReference(StyleProperty, "ProfileNameBand");

        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(band);
        Grid.SetColumn(description, 1);
        grid.Children.Add(description);
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

    private static readonly object CustomNameTag = new();

    // The cap on a custom name follows the list's width; the band's padding is inside it.
    private void CapCustomNames(double listWidth)
    {
        if (listWidth <= 0) return;
        foreach (var row in ListHost.Children.OfType<Button>())
        {
            if (row.Content is Grid { Children: [Border { Child: TextBlock name }, ..] } && ReferenceEquals(name.Tag, CustomNameTag))
                name.MaxWidth = Math.Max(name.MinWidth, listWidth * CustomNameShare - BandPadding);
        }
    }

    private Shell.ShellWindow? Shell() => Window.GetWindow(this) as Shell.ShellWindow;

    private void AddButton_Click(object sender, RoutedEventArgs e) => Shell()?.OpenNewRealtimeProfile();

    // Round the list to the inside of its border, so a filled row does not square off the corners;
    // and keep the custom names to their share of the new width.
    private void ListHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);
        if (e.WidthChanged)
            CapCustomNames(e.NewSize.Width);
    }
}
