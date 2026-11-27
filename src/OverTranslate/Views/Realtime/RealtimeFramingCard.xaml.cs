using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    // How tall a row's thumbnail is drawn, in DIP.
    private const double ThumbnailHeight = 24;

    // True while a rename is being written, so the settings event that follows does not commit the
    // same name again through the box it is re-rendering.
    private bool _committing;

    // True while a session runs: picking a framing turns the capture source, which a running session
    // cannot follow, so the rows cannot be picked. Renaming and deleting stay open.
    private bool _selectionLocked;

    // Whether the user unfolded the list. Not kept: leaving the page and coming back folds it again.
    private bool _expanded;

    public RealtimeFramingCard()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnChanged;
        SettingsService.Instance.RealtimeFramingChanged += OnChanged;
        Unloaded += (_, _) =>
        {
            if (!_expanded) return;
            _expanded = false;
            Render();
        };
        Render();
    }

    /// <summary>
    /// Raised with a framing the user picked, after it is marked in the settings file: the page turns
    /// its capture source to that preset's target and says whether it could.
    /// </summary>
    public event EventHandler<RealtimeFramingPreset>? Selected;

    /// <summary>Re-reads the kept framings and lists them, after the page itself was reloaded.</summary>
    public void Reload() => Render();

    /// <summary>
    /// Locks picking a framing while a session runs, as the page locks its languages, service and
    /// screen; renaming and deleting stay open. Picking used to go through and move the tick while
    /// nothing on the screen changed.
    /// </summary>
    public void SetSelectionLocked(bool locked)
    {
        if (_selectionLocked == locked) return;
        _selectionLocked = locked;

        // A rename in progress keeps its box: redrawing the rows would throw the typing away.
        if (ListHost.IsKeyboardFocusWithin) return;
        Render();
    }

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
        var skipped = Realtime.FramingSkippedTargets;

        // Folded past a few rows, with every preset a target opens on still in view.
        var keep = presets.Select((preset, index) => (preset, index))
            .Where(item => RealtimeFramingStore.OpensOn(item.preset, skipped))
            .Select(item => item.index);
        var (shown, hidden) = CollapsibleRows.Shown(presets.Count, keep, _expanded);

        ListHost.Children.Clear();
        foreach (var index in shown) ListHost.Children.Add(Row(presets[index]));

        // The last row drops its rule so it does not double the list's own border — but keeps the
        // accent edge a selected row carries.
        if (ListHost.Children.Count > 0)
        {
            var last = (Border)ListHost.Children[^1];
            last.BorderThickness = new Thickness(last.BorderThickness.Left, 0, 0, 0);
        }

        // 顯示其餘 N 組 / 收起 as the list's last row, after the rule fix above: it brings its own.
        if (CollapsibleRows.HasMoreRow(presets.Count, hidden, _expanded))
        {
            ListHost.Children.Add(CollapsibleRows.MoreRow(hidden, _expanded, "S.Realtime.ShowMoreFramings",
                "FramingPresetMore", () => { _expanded = !_expanded; Render(); }));
        }

        // Nothing kept: the list's frame would read as an empty box to fill in, and there is nothing
        // on this page to fill it with — the sentence says where the button is instead.
        // The hint explains a list; with no list, the one sentence in its place says it all.
        ListBorder.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var nameCell = NameCell(name);

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
            Content = RealtimeFramingThumbnail.Create(preset, ThumbnailHeight),
            ToolTip = LocalizationService.Format("S.Realtime.FramingUse", RealtimeFramingDisplay.Name(preset)),
            IsEnabled = !_selectionLocked,
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
        text.Children.Add(nameCell);
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
        // A target the user chose 不套用 for has no row marked: it opens on none of them.
        row.SetResourceReference(StyleProperty,
            RealtimeFramingStore.OpensOn(preset, Realtime.FramingSkippedTargets) ? "FramingRowSelected" : "FramingRow");
        // Clicking the row does what its square does, except on the things that have their own job:
        // the name, which is being typed into, 不再保留, and the square itself.
        row.MouseLeftButtonUp += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsWithin(source, nameCell) || IsWithin(source, select) || IsWithin(source, forget)) return;

            Choose(preset);
        };
        return row;
    }

    /// <summary>
    /// The name, edited in place: one line cut with … while it is only being read, the whole name
    /// in the box once it is clicked into, and an underline with a pencil while the pointer is on it
    /// — the box has no frame, so without those nothing says it can be typed into.
    /// </summary>
    /// <remarks>
    /// A text box cannot cut its own text with …, so a text block that can lies over it while it is
    /// not being edited, and the box's own text is drawn transparent underneath; the block takes no
    /// presses, so a click lands in the box. The cell sizes to the name and gives way to the row's
    /// width (a star column in a left-aligned grid), so the pencil sits right after the name rather
    /// than at the far side of the row.
    /// </remarks>
    private static FrameworkElement NameCell(TextBox box)
    {
        box.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;

        var shown = new TextBlock
        {
            Text = box.Text,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            // The box's own padding and border, so the two lines of text sit on each other.
            Margin = new Thickness(0, 1, 0, 3),
        };
        shown.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        shown.SetResourceReference(TextBlock.FontSizeProperty, "AppFontSize");
        shown.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimary");

        var pencil = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
        };
        pencil.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");

        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(shown);

        var cell = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.Children.Add(field);
        Grid.SetColumn(pencil, 1);
        cell.Children.Add(pencil);

        void Update()
        {
            bool editing = box.IsKeyboardFocusWithin;
            shown.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            if (editing) box.ClearValue(ForegroundProperty);
            else box.Foreground = System.Windows.Media.Brushes.Transparent;
            pencil.Visibility = !editing && box.IsMouseOver ? Visibility.Visible : Visibility.Hidden;
        }

        box.TextChanged += (_, _) => { if (!box.IsKeyboardFocusWithin) shown.Text = box.Text; };
        box.IsKeyboardFocusWithinChanged += (_, _) => { shown.Text = box.Text; Update(); };
        box.MouseEnter += (_, _) => Update();
        box.MouseLeave += (_, _) => Update();
        Update();
        return cell;
    }

    /// <summary>
    /// Marks <paramref name="preset"/> as its target's and tells the page, which turns its capture
    /// source to it. Written before the page is told so the list already shows the pick. Nothing
    /// while a session runs — see <see cref="SetSelectionLocked"/>.
    /// </summary>
    private void Choose(RealtimeFramingPreset preset)
    {
        if (_selectionLocked) return;

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

}
