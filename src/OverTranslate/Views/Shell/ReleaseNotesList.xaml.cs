using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using OverTranslate.Services;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Shell;

/// <summary>Which label the newest version listed carries.</summary>
public enum ReleaseNotesTag
{
    /// <summary>The newest release there is, which is what an update is offering.</summary>
    Latest,

    /// <summary>The version running, which is what the release history starts from.</summary>
    Current,
}

/// <summary>
/// The releases in the update window: one heading per version, with at most one open at a time.
/// </summary>
/// <remarks>
/// One open at a time is what keeps it clear which version a line belongs to. Run together, a quick
/// scroll carries the reader from one release into the next without them noticing, and the update
/// looks bigger than it is. The whole list scrolls as one, rather than the headings staying put and
/// the open one scrolling between them, because that left too little room once the download's
/// progress and warnings were up as well.
/// </remarks>
public partial class ReleaseNotesList : UserControl
{
    private const string ChevronClosed = "";
    private const string ChevronOpen = "";
    private const string ReleasesPage = "https://github.com/Hon-Lu/OverTranslate/releases";

    private readonly List<VersionItem> _items = [];
    private VersionItem? _open;

    public ReleaseNotesList()
    {
        InitializeComponent();
    }

    /// <summary>Lists <paramref name="feed"/>, starting with the newest version open.</summary>
    public void Show(ReleaseNotesFeed feed, ReleaseNotesTag tag)
    {
        var previouslyOpen = _open?.Entry.Version;
        var hadAny = _items.Count > 0;

        Items.Children.Clear();
        _items.Clear();
        _open = null;
        StickyRow.Visibility = Visibility.Collapsed;

        var culture = new CultureInfo(LocalizationService.Current);
        var severalVersions = feed.Shown.Count > 1;

        for (var i = 0; i < feed.Shown.Count; i++)
        {
            var item = BuildItem(feed.Shown[i], i == 0 ? tag : null, culture, withRow: severalVersions, isLast: i == feed.Shown.Count - 1);
            _items.Add(item);
            if (item.Row is not null) Items.Children.Add(item.Row);
            Items.Children.Add(item.Panel);
        }

        if (feed.Earlier > 0 || tag == ReleaseNotesTag.Current)
            Items.Children.Add(BuildEndRow(feed, tag));

        var toOpen = hadAny
            ? _items.FirstOrDefault(i => i.Entry.Version == previouslyOpen)
            : _items.FirstOrDefault();
        SetOpen(toOpen, scrollToIt: false);
    }

    private VersionItem BuildItem(ReleaseEntry entry, ReleaseNotesTag? tag, CultureInfo culture, bool withRow, bool isLast)
    {
        var sections = ReleaseNotesParser.Parse(entry.Body, LocalizationService.Current);
        var panel = BuildPanel(entry, sections, separated: withRow);

        var item = new VersionItem(entry, tag, culture, sections, panel);
        if (!withRow) return item;

        var row = new ToggleButton { Style = (Style)Resources["VersionRow"] };
        row.Click += (_, _) => SetOpen(item == _open ? null : item, scrollToIt: true);
        item.Row = row;
        return item;
    }

    private static UIElement RowContent(VersionItem item, bool open)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chevron = new TextBlock
        {
            Text = open ? ChevronOpen : ChevronClosed,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        chevron.SetResourceReference(ForegroundProperty, open ? "AppAccent" : "AppTextSecondary");
        top.Children.Add(chevron);

        var version = new TextBlock
        {
            Text = item.Entry.Version,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        version.SetResourceReference(ForegroundProperty, "AppTextPrimary");
        Grid.SetColumn(version, 1);
        top.Children.Add(version);

        if (item.Tag is { } tag)
        {
            var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.TextProperty, tag == ReleaseNotesTag.Latest ? "S.Update.LatestTag" : "S.Update.CurrentTag");
            label.SetResourceReference(ForegroundProperty, "AppAccent");

            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = label,
            };
            chip.SetResourceReference(Border.BackgroundProperty, open ? "AppCardBg" : "AppAccentSubtle");
            Grid.SetColumn(chip, 2);
            top.Children.Add(chip);
        }

        if (item.Entry.Published is { } published)
        {
            var date = new TextBlock
            {
                Text = published.ToLocalTime().ToString("M", item.Culture),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            date.SetResourceReference(ForegroundProperty, "AppTextSecondary");
            Grid.SetColumn(date, 3);
            top.Children.Add(date);
        }

        if (open || item.Sections.Count == 0) return top;

        // A reason to open it: what the release is about, without opening it.
        var separator = LocalizationService.Get("S.Update.PeekSeparator");
        var peek = new TextBlock
        {
            Text = string.Join(separator, item.Sections.Where(s => s.Title.Length > 0).Select(s => s.Title)),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(20, 2, 0, 0),
        };
        peek.SetResourceReference(ForegroundProperty, "AppTextSecondary");

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(peek);
        return stack;
    }

    private Border BuildPanel(ReleaseEntry entry, IReadOnlyList<NotesSection> sections, bool separated)
    {
        var stack = new StackPanel();

        foreach (var section in sections)
        {
            if (section.Title.Length > 0)
                stack.Children.Add(BuildSectionTitle(section));

            foreach (var line in section.Lines)
                stack.Children.Add(BuildLine(line));
        }

        if (sections.Count == 0)
        {
            // Not in the shape the template produces, so all there is to offer is the page itself.
            var text = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0) };
            text.Inlines.Add(BuildLink("S.Update.NotesLink", entry.HtmlUrl));
            stack.Children.Add(text);
        }

        var panel = new Border
        {
            Padding = new Thickness(14, 0, 14, 14),
            BorderThickness = new Thickness(0, 0, 0, separated ? 1 : 0),
            Child = stack,
            Visibility = Visibility.Collapsed,
        };
        panel.SetResourceReference(Border.BorderBrushProperty, "AppBorder");
        return panel;
    }

    private static UIElement BuildSectionTitle(NotesSection section)
    {
        var title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 4),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        if (section.Glyph.Length > 0)
        {
            var glyph = new Run(section.Glyph + " ");
            glyph.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");
            title.Inlines.Add(glyph);
        }

        title.Inlines.Add(new Run(section.Title));
        return title;
    }

    private static UIElement BuildLine(NotesLine line)
    {
        var text = new TextBlock
        {
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, line.Level > 0 ? "AppTextSecondary" : "AppTextPrimary");

        foreach (var span in line.Spans)
        {
            var run = new Run(span.Text);
            if (span.Kind == NotesSpanKind.Bold) run.FontWeight = FontWeights.SemiBold;
            if (span.Kind == NotesSpanKind.Code) run.SetResourceReference(TextElement.BackgroundProperty, "AppSubtleBorder");
            text.Inlines.Add(run);
        }

        var row = new Grid { Margin = new Thickness(line.Level > 0 ? 14 : 0, 0, 0, 3) };
        if (!line.Bullet)
        {
            row.Children.Add(text);
            return row;
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 3,
            Height = 3,
            Margin = new Thickness(0, 8, 7, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        dot.SetResourceReference(Shape.FillProperty, "AppTextSecondary");
        row.Children.Add(dot);

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private UIElement BuildEndRow(ReleaseNotesFeed feed, ReleaseNotesTag tag)
    {
        var row = new Grid { Margin = new Thickness(14, 10, 14, 10) };

        var link = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };

        if (tag == ReleaseNotesTag.Current)
        {
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Inlines.Add(BuildLink("S.Update.AllVersionsLink", ReleasesPage));
        }
        else
        {
            var earlier = feed.TotalIsPartial ? $"{feed.Earlier}+" : feed.Earlier.ToString(CultureInfo.CurrentCulture);
            var count = new TextBlock
            {
                Text = LocalizationService.Format("S.Update.EarlierVersions", earlier),
                FontSize = 12,
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            row.Children.Add(count);
            link.Inlines.Add(BuildLink("S.Update.EarlierLink", ReleasesPage));
        }

        row.Children.Add(link);
        return row;
    }

    private Hyperlink BuildLink(string textKey, string url)
    {
        var run = new Run();
        run.SetResourceReference(Run.TextProperty, textKey);

        var link = new Hyperlink(run)
        {
            NavigateUri = new Uri(url),
            TextDecorations = null,
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "AppAccent");
        link.RequestNavigate += Link_RequestNavigate;
        return link;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>
    /// Opens <paramref name="item"/> and closes whatever else was open; null closes everything, which
    /// is how to see every version at once.
    /// </summary>
    private void SetOpen(VersionItem? item, bool scrollToIt)
    {
        _open = item;

        foreach (var each in _items)
        {
            var isOpen = each == item;
            each.Panel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

            if (each.Row is not null)
            {
                each.Row.IsChecked = isOpen;
                each.Row.Content = RowContent(each, isOpen);
            }
        }

        // A single version has no row to close it by, so it is simply always open.
        if (item is null && _items.Count == 1) _items[0].Panel.Visibility = Visibility.Visible;

        if (item?.Row is not null && scrollToIt)
        {
            // What closed above it has moved it, possibly out of sight, so it is brought to the top.
            Scroller.UpdateLayout();
            Scroller.ScrollToVerticalOffset(TopOf(item.Row) + Scroller.VerticalOffset);
        }

        UpdateSticky();
    }

    private double TopOf(FrameworkElement element) =>
        element.TransformToVisual(Scroller).Transform(new Point(0, 0)).Y;

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateSticky();

    /// <summary>
    /// Shows the open version's heading over the list while its own has scrolled away, and pushes it
    /// off the top as the end of that version's notes arrives.
    /// </summary>
    private void UpdateSticky()
    {
        if (_open?.Row is not { } row || !row.IsLoaded)
        {
            StickyRow.Visibility = Visibility.Collapsed;
            return;
        }

        var top = TopOf(row);
        var bottom = top + row.ActualHeight + _open.Panel.ActualHeight;
        var shown = top < 0 && bottom > 0;

        if (!shown)
        {
            StickyRow.Visibility = Visibility.Collapsed;
            return;
        }

        if (StickyRow.Tag != _open)
        {
            StickyRow.Tag = _open;
            StickyRow.Content = RowContent(_open, open: true);
        }

        StickyRow.Width = Scroller.ViewportWidth;
        StickyShift.Y = Math.Min(0, bottom - row.ActualHeight);
        StickyRow.Visibility = Visibility.Visible;
    }

    private void StickyRow_Click(object sender, RoutedEventArgs e)
    {
        // Pressing it toggled it; it only ever stands for the open version, so it stays lit.
        StickyRow.IsChecked = true;
        if (_open?.Row is { } row)
        {
            Scroller.ScrollToVerticalOffset(TopOf(row) + Scroller.VerticalOffset);
            SetOpen(null, scrollToIt: false);
        }
    }

    private void Clipped_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A Border's rounding does not clip what is inside it, and the lit row would square its corners.
        const double Radius = 7;
        Clipped.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);
        UpdateSticky();
    }

    private sealed class VersionItem(
        ReleaseEntry entry,
        ReleaseNotesTag? tag,
        CultureInfo culture,
        IReadOnlyList<NotesSection> sections,
        Border panel)
    {
        public ReleaseEntry Entry { get; } = entry;
        public ReleaseNotesTag? Tag { get; } = tag;
        public CultureInfo Culture { get; } = culture;
        public IReadOnlyList<NotesSection> Sections { get; } = sections;
        public Border Panel { get; } = panel;
        public ToggleButton? Row { get; set; }
    }
}
