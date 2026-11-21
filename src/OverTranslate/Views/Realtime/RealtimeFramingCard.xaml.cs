using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The 翻譯區域預設 card on the 即時翻譯 page: every framing the user has kept, one row each,
/// with the one this target opens on selected, and each renamed in place or forgotten from its own
/// row.
/// </summary>
/// <remarks>
/// <para>Only the list lives here; the act of keeping a framing is on the floating bar, where the
/// blocks are — see <see cref="RealtimeSessionController.SaveFraming"/>. The card is where they are
/// looked at afterwards, which is also where a name can be given to one and where one of a target's
/// several is picked: doing so marks it in the file and tells the page, which turns its capture
/// source to that screen or window so the next 選取翻譯區塊 opens on it.</para>
///
/// <para>A row is a hit target of its own for the same errand, with the square at its left as the
/// visible control and a tab stop. The name and 不再保留 keep their own clicks: one is for typing
/// and the other for deleting, and a row that treated those as "pick me" would fight the pointer.</para>
/// </remarks>
public partial class RealtimeFramingCard : UserControl
{
    // The row's icon: two blocks, the wide one over the small one, drawn rather than a font glyph so
    // it matches the mode card's icons and has no font fallback to lose. Round joins because the
    // shape is small and a square corner reads as a stray pixel at 1.2 wide.
    private static readonly Geometry BlocksGlyph = Freeze(Geometry.Parse(
        "M1.8,2.4 H14.2 V7.4 H1.8 Z M1.8,9.9 H9.4 V13.6 H1.8 Z"));

    // The check a selected row's square carries instead: the square is the control, and what it says
    // has to be readable at 16px, so it is one bold stroke rather than a tinted state.
    private static readonly Geometry CheckGlyph = Freeze(Geometry.Parse("M3,8.6 L6.4,12 L13,4.6"));

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    // True while a rename is being written, so the settings event that follows does not commit the
    // same name again through the box it is re-rendering.
    private bool _committing;

    public RealtimeFramingCard()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnChanged;
        SettingsService.Instance.RealtimeFramingChanged += OnChanged;
        Render();
    }

    /// <summary>
    /// Raised with a framing the user picked, after it is marked in the settings file: the page turns
    /// its capture source to that preset's target and says whether it could.
    /// </summary>
    public event EventHandler<RealtimeFramingPreset>? Selected;

    /// <summary>Re-reads the kept framings and lists them, after the page itself was reloaded.</summary>
    public void Reload() => Render();

    /// <summary>Lets go of the static events, when the window holding the page goes.</summary>
    public void Teardown()
    {
        LocalizationService.LanguageChanged -= OnChanged;
        SettingsService.Instance.RealtimeFramingChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    private void Render()
    {
        var presets = Realtime.FramingPresets;

        ListHost.Children.Clear();
        foreach (var preset in presets) ListHost.Children.Add(Row(preset));

        // The last row drops its rule so it does not double the list's own border — but keeps the
        // accent edge a selected row carries.
        if (ListHost.Children.Count > 0)
        {
            var last = (Border)ListHost.Children[^1];
            last.BorderThickness = new Thickness(last.BorderThickness.Left, 0, 0, 0);
        }

        // Nothing kept: the list's frame would read as an empty box to fill in, and there is nothing
        // on this page to fill it with — the sentence says where the button is instead.
        ListBorder.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = presets.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// One kept framing: the pick square, its name over how many blocks and which application, and
    /// 不再保留 at the right.
    /// </summary>
    private Border Row(RealtimeFramingPreset preset)
    {
        var name = new TextBox
        {
            Text = RealtimeFramingDisplay.Name(preset),
            ToolTip = LocalizationService.Get("S.Realtime.FramingRename"),
        };
        name.SetResourceReference(StyleProperty, "FramingNameBox");
        AutomationProperties.SetName(name, LocalizationService.Get("S.Realtime.FramingName"));

        var detail = new TextBlock
        {
            Text = RealtimeFramingDisplay.Detail(preset),
            FontSize = 12,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        string id = preset.Id;
        name.GotKeyboardFocus += (_, _) => name.SelectAll();
        name.LostKeyboardFocus += (_, _) => Commit(name, id);
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit(name, id);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // Back to what is stored: a half-typed name is not a change.
                name.Text = ShownName(id, preset);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };

        var select = new Button
        {
            Content = preset.Selected ? Glyph(CheckGlyph, 1.7) : Glyph(BlocksGlyph, 1.2),
            ToolTip = LocalizationService.Format("S.Realtime.FramingUse", RealtimeFramingDisplay.Name(preset)),
        };
        select.SetResourceReference(StyleProperty, "FramingSelectButton");
        AutomationProperties.SetName(select, select.ToolTip as string ?? "");
        select.Click += (_, _) => Choose(preset);

        var forget = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
            },
            ToolTip = LocalizationService.Get("S.Realtime.FramingForget"),
        };
        forget.SetResourceReference(StyleProperty, "FramingForgetButton");
        AutomationProperties.SetName(forget, LocalizationService.Get("S.Realtime.FramingForget"));
        forget.Click += (_, _) => RealtimeFramingStore.Forget(id);

        var text = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(detail);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(select);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(forget, 2);
        grid.Children.Add(forget);

        var row = new Border { Child = grid };
        row.SetResourceReference(StyleProperty, preset.Selected ? "FramingRowSelected" : "FramingRow");
        // Clicking the row does what its square does, except on the things that have their own job:
        // the name, which is being typed into, 不再保留, and the square itself.
        row.MouseLeftButtonUp += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsWithin(source, name) || IsWithin(source, select) || IsWithin(source, forget)) return;

            Choose(preset);
        };
        return row;
    }

    /// <summary>The glyph inside a row's square, in the accent — the blocks' outline, or a check.</summary>
    private static FrameworkElement Glyph(Geometry data, double thickness)
    {
        var glyph = new Path
        {
            Data = data,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        glyph.SetResourceReference(Shape.StrokeProperty, "AppAccent");
        return glyph;
    }

    /// <summary>
    /// Marks <paramref name="preset"/> as its target's and tells the page, which turns its capture
    /// source to it. Written before the page is told so the list already shows the pick.
    /// </summary>
    private void Choose(RealtimeFramingPreset preset)
    {
        RealtimeFramingStore.Select(preset.Id);
        Selected?.Invoke(this, preset);
    }

    /// <summary>
    /// Writes what the box now says, or puts the target's own name back when the box only repeats it
    /// — see <see cref="RealtimeFramingDisplay.StoredName"/>.
    /// </summary>
    private void Commit(TextBox box, string id)
    {
        if (_committing) return;

        if (RealtimeFramingStore.ForId(id) is not { } preset) return;

        var stored = RealtimeFramingDisplay.StoredName(preset, box.Text);
        if (string.Equals(stored, preset.Name, StringComparison.Ordinal))
        {
            // Nothing was renamed: the box may still be showing a different spelling of the same
            // automatic name, so put back exactly what the list would draw.
            box.Text = RealtimeFramingDisplay.Name(preset);
            return;
        }

        _committing = true;
        try
        {
            RealtimeFramingStore.Rename(id, stored);
        }
        finally
        {
            _committing = false;
        }
    }

    private static string ShownName(string id, RealtimeFramingPreset fallback) =>
        RealtimeFramingStore.ForId(id) is { } preset
            ? RealtimeFramingDisplay.Name(preset)
            : RealtimeFramingDisplay.Name(fallback);

    /// <summary>Whether <paramref name="source"/> is <paramref name="container"/> or inside it.</summary>
    private static bool IsWithin(DependencyObject? source, DependencyObject container)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, container)) return true;

        return false;
    }

    // Round the list to the inside of its border, so a hovered row does not square off the corners.
    private void ListBorder_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ListBorder.Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 9, 9);

    /// <summary>
    /// Hands the wheel to the page once the list can go no further that way, or when it does not
    /// scroll at all.
    /// </summary>
    /// <remarks>
    /// A ScrollViewer marks every wheel it gets as handled, scrolled or not, so without this the
    /// page stopped dead whenever the pointer crossed the list.
    /// </remarks>
    private void ListScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        bool up = e.Delta > 0;
        bool canScroll = up ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight;
        if (canScroll) return;

        e.Handled = true;
        ListBorder.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = ListBorder,
        });
    }
}
