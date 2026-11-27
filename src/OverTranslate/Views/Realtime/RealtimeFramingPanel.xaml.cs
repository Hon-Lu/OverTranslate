using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 翻譯區域預設, opened from the framing bar and from the running capsule: every framing kept for
/// the monitor or application being read, what each one is now, and 儲存目前框選 at the bottom.
/// </summary>
/// <remarks>
/// <para>A window of its own beside the bar, built the way 顯示外觀 and 模式 are and for their
/// reasons: <c>ShowActivated="False"</c> and a no-activate ex-style, because pulling the foreground
/// off a full-screen game can minimise it, and every row is picked with the mouse so nothing here
/// needs the keyboard. It is owned by the bar while framing, so it stays above the layer that covers
/// the whole screen.</para>
///
/// <para>What each row is comes from <see cref="RealtimeFramingStore.ListFor"/>: another framing is
/// pressed to switch to it; the one on the screen is ticked and pressing it does nothing; the one
/// applied and changed since says so and offers 還原 and 更新, and pressing the row itself does
/// nothing, so a stray press cannot throw the changes away; one that cannot be placed now is greyed
/// with the reason. The save row at the bottom says why when there is nothing it can save.</para>
///
/// <para>It closes on any of those actions, on its ✕, and when the bar puts it away — pressing the
/// bar's button again, opening 模式 or 顯示外觀, or leaving framing. It is not closed by a press
/// outside it: like the other panels over a session it stays until the user is done with it. While
/// it is open the blocks can still be moved, and the list follows — see <see cref="Rebuild"/>.</para>
/// </remarks>
internal partial class RealtimeFramingPanel : Window
{
    // Between the bar's edge and the panel's.
    private const double Gap = 6;

    // The panel's width: a thumbnail, one line of a name, and on a changed row its two buttons.
    // 340 rather than 300: a changed row's name shows 18 characters of Chinese and 25 of Russian
    // beside the two icons, against 15 and 19 at 300.
    private const double PanelWidth = 340;

    // How tall a row's thumbnail is drawn, in DIP.
    private const double ThumbnailHeight = 26;

    // The list's own limits, in DIP: enough for a few rows, never taller than the room beside the
    // bar allows — see Place.
    private const double MinListHeight = 96;
    private const double MaxListHeight = 320;

    // What the chrome around the list takes: the heading, the frame and the padding. The shadow's
    // margin is read off RootChrome itself, and the save row is measured.
    private const double Header = 38;
    private const double Frame = 2;

    private System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private double _windowScale = 1.0;
    private bool _opensUp = true;
    private bool _placed;
    private bool _closing;

    /// <param name="bar">The framing bar's visible surface, in physical pixels: the panel opens clear of it.</param>
    /// <param name="screen">The session's screen, which the panel has to stay on.</param>
    /// <param name="list">The rows and the save row, as they are now.</param>
    /// <param name="target">What the session reads, which a greyed row's reason names the size of.</param>
    public RealtimeFramingPanel(
        System.Drawing.Rectangle bar,
        System.Drawing.Rectangle screen,
        RealtimeFramingStore.FramingList list,
        RealtimeFramingStore.Target target)
    {
        InitializeComponent();

        _bar = bar;
        _screen = screen;

        Chrome.Width = PanelWidth;
        Build(list, target);

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>Raised with the id of a framing pressed to put it on the screen. The list closes with the pick.</summary>
    public event EventHandler<string>? Picked;

    /// <summary>Raised when 儲存目前框選 is pressed.</summary>
    public event EventHandler? SaveRequested;

    /// <summary>Raised with the id of the changed framing whose 更新 was pressed.</summary>
    public event EventHandler<string>? UpdateRequested;

    /// <summary>Raised with the id of the changed framing whose 還原 was pressed.</summary>
    public event EventHandler<string>? RestoreRequested;

    /// <summary>Raised when 不套用 is pressed.</summary>
    public event EventHandler? SkipRequested;

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
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>The bar moved — dragged, or its width changed — and the panel goes with it.</summary>
    public void UpdateAnchor(System.Drawing.Rectangle bar)
    {
        if (bar == _bar || _closing) return;
        _bar = bar;
        if (IsLoaded) Place();
    }

    /// <summary>
    /// Redraws the rows for blocks that changed while the list was open — a block moved, added or
    /// removed on the layer — so a tick, 已修改 or 與「A」相同 is never one press out of date.
    /// </summary>
    public void Rebuild(RealtimeFramingStore.FramingList list, RealtimeFramingStore.Target target)
    {
        if (_closing) return;
        Build(list, target);
        if (IsLoaded) Place();
    }

    /// <summary>Closes the panel; the bar hears about it and lets go of its button.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Build(RealtimeFramingStore.FramingList list, RealtimeFramingStore.Target target)
    {
        RowsHost.Children.Clear();
        bool any = list.Rows.Count > 0;
        TargetText.Text = RealtimeFramingDisplay.Caption(target);
        TargetText.ToolTip = TargetText.Text;

        // 不套用 heads the list only when there is something not to apply: with nothing kept for this
        // monitor or application the target opens empty anyway, and a lone ticked 不套用 over an
        // empty list would read as a choice the user had made.
        if (any) RowsHost.Children.Add(SkipRow(list.Skipped, list.Save != RealtimeFramingStore.SaveState.NoBlocks));
        foreach (var row in list.Rows) RowsHost.Children.Add(Row(row, target));

        RowsScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SaveRule.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SaveHost.Content = SaveRow(list);
    }

    /// <summary>One framing, drawn for what it is now — see the remarks.</summary>
    private FrameworkElement Row(RealtimeFramingStore.FramingRow row, RealtimeFramingStore.Target target)
    {
        var preset = row.Preset;
        var name = RealtimeFramingDisplay.Name(preset);
        bool unusable = row.State == RealtimeFramingStore.RowState.Unusable;

        var title = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The second line: 已修改 ahead of the count on a changed framing, so the name keeps the
        // whole first line; the reason on one that cannot be placed.
        var detail = new TextBlock
        {
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        if (row.State == RealtimeFramingStore.RowState.Modified)
        {
            var modified = new System.Windows.Documents.Run(LocalizationService.Get("S.Realtime.FramingModified"));
            modified.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AppAccent");
            detail.Inlines.Add(modified);
            detail.Inlines.Add(new System.Windows.Documents.Run(" · " + RealtimeFramingDisplay.BlockCount(preset)));
        }
        else
        {
            detail.Text = unusable
                ? RealtimeFramingDisplay.Unusable(row.Why, target)
                : RealtimeFramingDisplay.BlockCount(preset);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);

        var thumbnail = RealtimeFramingThumbnail.Create(preset, ThumbnailHeight);
        thumbnail.Margin = new Thickness(0, 0, 10, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(thumbnail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (Trailing(row) is { } trailing)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        if (unusable)
        {
            // Greyed as a whole but for the reason, which is the one thing on the row to read.
            thumbnail.Opacity = 0.45;
            title.Opacity = 0.55;
        }

        FrameworkElement element;
        if (row.State == RealtimeFramingStore.RowState.Ready)
        {
            var button = new Button { Content = grid };
            button.SetResourceReference(StyleProperty, "FramingMenuRow");
            string id = preset.Id;
            button.Click += (_, _) => Picked?.Invoke(this, id);
            element = button;
        }
        else
        {
            var border = new Border { Child = grid };
            border.SetResourceReference(StyleProperty, "FramingStaticRow");
            element = border;
        }

        // The whole name, which the row cuts with …; a greyed row's reason too, which it may cut.
        element.ToolTip = unusable ? $"{name}\n{detail.Text}" : name;
        ToolTipService.SetShowOnDisabled(element, true);
        AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>
    /// 不套用, at the top of the list: a framing with no blocks, which pressing clears the screen to and
    /// the target opens on from then on. Ticked when it is the choice, as a framing's row is, and
    /// still pressable while blocks have been drawn over it since.
    /// </summary>
    private FrameworkElement SkipRow(bool chosen, bool blocksOnScreen)
    {
        // An empty frame where a framing's thumbnail is: no blocks.
        var empty = new Border
        {
            Width = 46,
            Height = ThumbnailHeight,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        empty.SetResourceReference(Border.BorderBrushProperty, "AppTextSecondary");

        var label = new TextBlock
        {
            Text = LocalizationService.Get("S.Realtime.FramingNone"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(empty);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (chosen)
        {
            var check = Tick();
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        FrameworkElement element;
        if (chosen && !blocksOnScreen)
        {
            var border = new Border { Child = grid };
            border.SetResourceReference(StyleProperty, "FramingStaticRow");
            element = border;
        }
        else
        {
            var button = new Button { Content = grid };
            button.SetResourceReference(StyleProperty, "FramingMenuRow");
            button.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
            element = button;
        }

        AutomationProperties.SetAutomationId(element, "FramingNone");
        AutomationProperties.SetName(element, label.Text);
        return element;
    }

    /// <summary>The tick on the row this target opens on.</summary>
    private static TextBlock Tick()
    {
        var check = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AppAccent");
        return check;
    }

    /// <summary>What sits at a row's right: the tick on the framing on the screen, 還原 and 更新 on a changed one.</summary>
    private FrameworkElement? Trailing(RealtimeFramingStore.FramingRow row)
    {
        switch (row.State)
        {
            case RealtimeFramingStore.RowState.Current:
                return Tick();

            case RealtimeFramingStore.RowState.Modified:
            {
                string id = row.Preset.Id;
                var restore = CapsuleButton("FramingRestoreGlyph", "S.Realtime.FramingRestore", primary: false,
                    corners: new CornerRadius(6, 0, 0, 6));
                restore.Click += (_, _) => RestoreRequested?.Invoke(this, id);

                var update = CapsuleButton("FramingUpdateGlyph", "S.Realtime.FramingUpdate", primary: true,
                    corners: new CornerRadius(0, 6, 6, 0));
                update.Click += (_, _) => UpdateRequested?.Invoke(this, id);

                var rule = new Border { Width = 1 };
                rule.SetResourceReference(Border.BackgroundProperty, "AppButtonBorder");

                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(restore);
                buttons.Children.Add(rule);
                buttons.Children.Add(update);

                var capsule = new Border { Child = buttons };
                capsule.SetResourceReference(StyleProperty, "FramingCapsule");
                return capsule;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 還原 or 更新 on a changed framing's row: one half of the capsule, a glyph with the word as its
    /// tooltip and its name.
    /// </summary>
    /// <remarks>
    /// <para>Drawn rather than taken from the icon font (see FramingRestoreGlyph), the same on
    /// Windows 10 and 11. Save rather than Sync: what 更新 does is write the blocks into the preset,
    /// and Sync reads as fetching something. 更新's mark takes the accent, the row's main action;
    /// 還原's the text colour.</para>
    ///
    /// <para>The 16 grid is drawn whole and scaled to 14 DIP by a Viewbox, stroke and all: the line
    /// comes out about 1.2 wide, the weight of the bar's own drawn marks beside it, and the two
    /// marks keep their places on the grid instead of each being stretched to its own bounds.</para>
    /// </remarks>
    private static Button CapsuleButton(string geometryKey, string labelKey, bool primary, CornerRadius corners)
    {
        var label = LocalizationService.Get(labelKey);
        var path = new System.Windows.Shapes.Path
        {
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, geometryKey);
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, primary ? "AppAccent" : "AppTextPrimary");

        var icon = new Viewbox
        {
            Width = 14,
            Height = 14,
            Child = new Grid { Width = 16, Height = 16, Children = { path } },
            SnapsToDevicePixels = true,
        };

        var button = new Button { Content = icon, ToolTip = label, Tag = corners };
        button.SetResourceReference(StyleProperty, "FramingCapsuleButton");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, primary ? "FramingUpdate" : "FramingRestore");
        return button;
    }

    /// <summary>
    /// 儲存目前框選: pressable when the blocks on the screen can be kept as a new framing, and
    /// otherwise greyed with what stands in the way under it.
    /// </summary>
    private FrameworkElement SaveRow(RealtimeFramingStore.FramingList list)
    {
        var plus = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "AppAccent");

        var label = new TextBlock
        {
            Text = LocalizationService.Get("S.Realtime.FramingSaveCurrent"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        string? reason = list.Save switch
        {
            RealtimeFramingStore.SaveState.Same when list.SameAs is { } same =>
                LocalizationService.Format("S.Realtime.FramingSameAs", RealtimeFramingDisplay.MessageName(same)),
            RealtimeFramingStore.SaveState.Full =>
                LocalizationService.Format("S.Realtime.FramingLimitReached", RealtimeSettings.MaxFramingPresets),
            RealtimeFramingStore.SaveState.NoBlocks => LocalizationService.Get("S.Realtime.NeedOneBlock"),
            _ => null,
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(label);
        if (reason is not null)
        {
            var why = new TextBlock
            {
                Text = reason,
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            why.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            text.Children.Add(why);
        }

        var line = new DockPanel();
        DockPanel.SetDock(plus, Dock.Left);
        line.Children.Add(plus);
        line.Children.Add(text);

        if (reason is null)
        {
            var button = new Button { Content = line, MinHeight = 36 };
            button.SetResourceReference(StyleProperty, "FramingMenuRow");
            AutomationProperties.SetAutomationId(button, "FramingSaveCurrent");
            AutomationProperties.SetName(button, label.Text);
            button.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
            return button;
        }

        plus.Opacity = 0.45;
        label.Opacity = 0.55;
        var border = new Border { Child = line, MinHeight = 36 };
        border.SetResourceReference(StyleProperty, "FramingStaticRow");
        AutomationProperties.SetAutomationId(border, "FramingSaveCurrent");
        AutomationProperties.SetName(border, $"{label.Text} — {reason}");
        return border;
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the panel against the bar — above it, or below when there is more room there — and shows
    /// it once it is there. The list is capped to the room on that side, and never made taller than
    /// <see cref="MaxListHeight"/>.
    /// </summary>
    private void Place()
    {
        if (_closing) return;

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            _windowScale = target.TransformToDevice.M11;

        // The bar's own correction — see RealtimeControlWindow.ApplyScreenScale — so the panel comes
        // out the same physical size as the bar it opens from.
        double screenScale = ScreenGeometry.ScaleAt(_bar.Left, _bar.Top);
        double relative = screenScale / _windowScale;
        RootChrome.LayoutTransform = Math.Abs(relative - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(relative, relative);

        int Physical(double dip) => (int)Math.Round(dip * screenScale);
        double Dip(int physical) => physical / screenScale;

        double above = Dip(_bar.Top - _screen.Top);
        double below = Dip(_screen.Bottom - _bar.Bottom);

        // Decided once, on the first placement: a panel that flipped sides as the list changed would
        // move out from under the pointer that opened it.
        if (!_placed) _opensUp = above >= below;

        // The save row is always there and does not scroll, so the rows get what it leaves.
        UpdateLayout();
        double footer = SaveHost.ActualHeight +
            (SaveRule.Visibility == Visibility.Visible
                ? SaveRule.ActualHeight + SaveRule.Margin.Top + SaveRule.Margin.Bottom
                : 0);

        var margin = RootChrome.Margin;
        double room = (_opensUp ? above : below) - Gap - Header - Frame - footer - margin.Bottom;
        RowsScroll.MaxHeight = Math.Clamp(room, MinListHeight, MaxListHeight);

        RootChrome.VerticalAlignment = _opensUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        UpdateLayout();

        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(Chrome.ActualHeight);
        int gap = Physical(Gap);

        int chromeTop = _opensUp
            ? Math.Max(_screen.Top, _bar.Top - gap - chromeHeight)
            : Math.Min(_bar.Bottom + gap, Math.Max(_screen.Top, _screen.Bottom - chromeHeight));
        int chromeLeft = Math.Clamp(
            _bar.Left, _screen.Left, Math.Max(_screen.Left, _screen.Right - chromeWidth));

        // Cast away from the bar.
        Shadow.Direction = _opensUp ? 90 : 270;

        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));
        _placed = true;

        Opacity = 1;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Dismiss();
}
