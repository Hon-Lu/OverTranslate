using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 顯示外觀, opened from the running capsule (#355): the colours, the band, the border and the text
/// size, changed while the translation they are for is on the screen.
/// </summary>
/// <remarks>
/// <para>The same settings as the card on the page, read from and written to the same file, so the
/// page shows what was chosen here when the session ends and the next session starts with it. Every
/// change is written at once and announced through <see cref="AppearanceChanged"/>; the session
/// redraws its overlays with it. A colour or an opacity is only a change once it is let go — see
/// <see cref="ColorPicker"/> — because each one rebuilds every overlay on the screen.</para>
///
/// <para>A window of its own, like the language lists, and for the same reason: the capsule never
/// takes activation, and a Popup inside it would get no keyboard. Unlike the lists this one does not
/// take the foreground when it opens either — everything on it is worked with the mouse, and pulling
/// the focus off a full-screen game to show it could minimise the game. Only a click in one of the
/// two boxes that are typed into (the size and a colour's code) asks for the keyboard, and Enter or
/// Esc there hands it straight back.</para>
///
/// <para>It stays open until the 顯示外觀 button is pressed again, so the user can try one value after
/// another while watching the result. It follows the capsule when that is dragged, and closes with
/// it — see RealtimeControlWindow.</para>
///
/// <para>Nothing about the session's rules for its own windows is special-cased, for the reason the
/// language list gives: a visible, topmost, unowned window of this process is left out of every
/// frame and raised over the overlays by the session's once-a-second re-assert.</para>
/// </remarks>
internal partial class RealtimeAppearancePanel : Window
{
    // Between the capsule's edge and the panel's.
    private const double Gap = 6;

    private System.Drawing.Rectangle _bar;
    private readonly System.Drawing.Rectangle _screen;

    private double _windowScale = 1.0;
    private bool _opensUp = true;
    private bool _placed;
    private bool _closing;
    private IntPtr _previousForeground;

    // Set while stored values are being put on the controls, so that is not taken for the user.
    private bool _showing;
    private bool _draggingOpacity;

    // The colour picker that is open, and the swatch it was opened from; one at a time.
    private ColorPicker? _openPicker;
    private Button? _openSwatch;

    /// <param name="bar">The capsule's visible surface, in physical pixels: the panel opens clear of it.</param>
    /// <param name="screen">The session's screen, which the panel has to stay on.</param>
    public RealtimeAppearancePanel(System.Drawing.Rectangle bar, System.Drawing.Rectangle screen)
    {
        InitializeComponent();

        _bar = bar;
        _screen = screen;

        FontSizeMode.SetLabels("S.Realtime.ColorAuto", "S.Realtime.ColorFixed");
        TextColorMode.SetLabels("S.Realtime.ColorAuto", "S.Realtime.ColorFixed");
        ScrimColorMode.SetLabels("S.Realtime.ColorAuto", "S.Realtime.ColorFixed");
        BorderMode.SetLabels("S.Realtime.BorderRandom", "S.Realtime.BorderFixed");
        FontSizeStepper.Minimum = RealtimeSettings.MinFixedFontSize;
        FontSizeStepper.Maximum = RealtimeSettings.MaxFixedFontSize;

        ShowStored();

        // After the stored values are in, so putting them there is not read as the user choosing.
        FontSizeMode.SelectionChanged += (_, isFixed) => Persist(r => r.FontSizeMode =
            isFixed ? RealtimeFontSizeMode.Fixed : RealtimeFontSizeMode.Auto);
        FontSizeStepper.ValueCommitted += (_, size) => Persist(r => r.FixedFontSize = size);
        TextColorMode.SelectionChanged += (_, isFixed) => Persist(r => r.SampleSourceTextColor = !isFixed);
        ScrimColorMode.SelectionChanged += (_, isFixed) => Persist(r => r.NaturalBackgroundEnabled = !isFixed);
        BorderMode.SelectionChanged += (_, isFixed) => Persist(r => r.BorderColorMode =
            isFixed ? RealtimeBorderColorMode.Fixed : RealtimeBorderColorMode.Random);
        BorderToggle.Checked += (_, _) => Persist(r => r.BorderEnabled = true);
        BorderToggle.Unchecked += (_, _) => Persist(r => r.BorderEnabled = false);

        TextColorPicker.ColorCommitted += (_, color) => Persist(r => r.TextColor = RealtimeSubtitleColors.Format(color));
        ScrimColorPicker.ColorCommitted += (_, color) => Persist(r => r.ScrimColor = RealtimeSubtitleColors.Format(color));
        BorderColorPicker.ColorCommitted += (_, color) => Persist(r => r.BorderColor = RealtimeSubtitleColors.Format(color));

        // The slider follows the thumb on every step and is applied when it is let go — or at once,
        // when it moved by a click on the track, which has no end to wait for.
        OpacitySlider.ValueChanged += (_, _) =>
        {
            OpacityValue.Text = $"{CurrentOpacity}%";
            if (_showing || _draggingOpacity) return;
            Persist(r => r.ScrimOpacity = CurrentOpacity);
        };
        OpacitySlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _draggingOpacity = true));
        OpacitySlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _draggingOpacity = false;
            Persist(r => r.ScrimOpacity = CurrentOpacity);
        }));

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>Raised after a change is in the settings file, for the session to redraw with it.</summary>
    public event EventHandler? AppearanceChanged;

    private int CurrentOpacity => RealtimeSubtitleColors.ClampOpacity((int)Math.Round(OpacitySlider.Value));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Before Show could hand it the foreground: see the remarks for why it never takes it itself.
        WindowStyles.ApplyNoActivate(this);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(OnWindowPosChanging);

        // Opened at the bar, so the window starts on the monitor it will be placed on and the scale
        // read in Place is that monitor's.
        ScreenGeometry.MoveToPhysical(this, _bar.Left, _bar.Top);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>The capsule moved — dragged, or its width changed — and the panel goes with it.</summary>
    public void UpdateAnchor(System.Drawing.Rectangle bar)
    {
        if (bar == _bar || _closing) return;
        _bar = bar;
        if (IsLoaded) Place();
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the panel for the session's screen and sets it against the capsule — above it, or below
    /// when there is more room there — its right edge on the capsule's, which is the edge the capsule
    /// keeps still. Shown only once it is there.
    /// </summary>
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
        UpdateLayout();

        int Physical(double dip) => (int)Math.Round(dip * screenScale);

        var margin = RootChrome.Margin;
        int chromeWidth = Physical(Chrome.ActualWidth);
        int chromeHeight = Physical(Chrome.ActualHeight);
        int gap = Physical(Gap);

        // Decided once, on the first placement: a panel that flipped sides as a picker opened would
        // move out from under the pointer that opened it.
        if (!_placed) _opensUp = _bar.Top - _screen.Top >= _screen.Bottom - _bar.Bottom;

        int chromeTop = _opensUp
            ? Math.Max(_screen.Top, _bar.Top - gap - chromeHeight)
            : Math.Min(_bar.Bottom + gap, Math.Max(_screen.Top, _screen.Bottom - chromeHeight));
        int chromeLeft = Math.Clamp(
            _bar.Right - chromeWidth, _screen.Left, Math.Max(_screen.Left, _screen.Right - chromeWidth));

        // Cast away from the bar.
        Shadow.Direction = _opensUp ? 90 : 270;

        _placed = false;
        ScreenGeometry.MoveToPhysical(this, chromeLeft - Physical(margin.Left), chromeTop - Physical(margin.Top));
        _placed = true;

        Opacity = 1;
    }

    /// <summary>
    /// Keeps the edge next to the capsule still while the panel grows or shrinks — a picker opening
    /// or closing — so it grows away from the bar instead of over it, and stays on the screen.
    /// </summary>
    /// <remarks>
    /// Done as the size changes rather than after, for the reason the capsule pins its right edge
    /// this way: a correction after the resize is a second move, and the frame in between shows the
    /// panel already grown and not yet moved.
    /// </remarks>
    private IntPtr OnWindowPosChanging(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || !_placed || _closing) return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SwpNoSize) != 0) return IntPtr.Zero;

        var bounds = ScreenGeometry.PhysicalBounds(this);
        if (bounds.IsEmpty || pos.cy == bounds.Height) return IntPtr.Zero;

        var left = (pos.flags & SwpNoMove) == 0 ? pos.x : bounds.Left;
        var top = _opensUp ? bounds.Bottom - pos.cy : bounds.Top;
        pos.x = left;
        pos.y = Math.Clamp(top, _screen.Top, Math.Max(_screen.Top, _screen.Bottom - pos.cy));
        pos.flags &= ~SwpNoMove;
        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
        return IntPtr.Zero;
    }

    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Puts every stored value on its control without that reading as the user setting it.</summary>
    private void ShowStored()
    {
        var realtime = SettingsService.Instance.Current.Realtime;

        _showing = true;
        FontSizeMode.IsSecondSelected = realtime.FontSizeMode == RealtimeFontSizeMode.Fixed;
        FontSizeStepper.Value = realtime.FixedFontSize;
        TextColorMode.IsSecondSelected = !realtime.SampleSourceTextColor;
        ScrimColorMode.IsSecondSelected = !realtime.NaturalBackgroundEnabled;
        OpacitySlider.Value = RealtimeSubtitleColors.ClampOpacity(realtime.ScrimOpacity);
        BorderMode.IsSecondSelected = realtime.BorderColorMode == RealtimeBorderColorMode.Fixed;
        BorderToggle.IsChecked = realtime.BorderEnabled;
        _showing = false;

        Render();
    }

    private void Persist(Action<RealtimeSettings> apply)
    {
        if (_showing) return;

        var settings = SettingsService.Instance;
        apply(settings.Current.Realtime);
        settings.Save();

        Render();
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        ClosePicker();
        Persist(r => r.ResetAppearance());
        ShowStored();
    }

    /// <summary>
    /// What each row allows in its mode, and the colours on the swatches. A control that has nothing
    /// to set in the mode its row is in is greyed rather than hidden, so no row changes shape — and no
    /// button moves — as the modes are flipped.
    /// </summary>
    private void Render()
    {
        var realtime = SettingsService.Instance.Current.Realtime;

        var text = RealtimeSubtitleColors.Text(realtime.TextColor);
        var scrim = RealtimeSubtitleColors.Scrim(realtime.ScrimColor, 100);
        var border = RealtimeSubtitleColors.Border(realtime.BorderColor);

        FontSizeStepper.IsEnabled = realtime.FontSizeMode == RealtimeFontSizeMode.Fixed;
        FontSizeStepper.Opacity = FontSizeStepper.IsEnabled ? 1 : 0.45;

        ShowSwatch(TextColorBtn, TextColorChip, TextColorCode, text, enabled: !realtime.SampleSourceTextColor);
        ShowSwatch(ScrimColorBtn, ScrimColorChip, ScrimColorCode, scrim, enabled: !realtime.NaturalBackgroundEnabled);
        ShowSwatch(BorderColorBtn, BorderColorChip, null, border,
            enabled: realtime.BorderEnabled && realtime.BorderColorMode == RealtimeBorderColorMode.Fixed);

        OpacityRow.IsEnabled = !realtime.NaturalBackgroundEnabled;
        OpacityRow.Opacity = OpacityRow.IsEnabled ? 1 : 0.45;
        OpacityValue.Text = $"{CurrentOpacity}%";

        BorderMode.IsEnabled = realtime.BorderEnabled;
        BorderMode.Opacity = BorderMode.IsEnabled ? 1 : 0.45;

        // A picker whose row no longer takes a colour of ours — switched to 自動 or 隨機, or the
        // border turned off — has nothing left to set.
        if (_openSwatch is { IsEnabled: false }) ClosePicker();

        ResetBtn.IsEnabled = !realtime.HasDefaultAppearance(CurrentOpacity);
    }

    private static void ShowSwatch(Button button, Border chip, TextBlock? code, Color color, bool enabled)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        chip.Background = brush;
        if (code is not null) code.Text = RealtimeSubtitleColors.Format(color);
        button.IsEnabled = enabled;
    }

    // ── Colour pickers ───────────────────────────────────────────────────────────────────────────

    private void SwatchBtn_Click(object sender, RoutedEventArgs e)
    {
        var swatch = (Button)sender;
        var realtime = SettingsService.Instance.Current.Realtime;
        var (picker, color) = swatch == TextColorBtn ? (TextColorPicker, RealtimeSubtitleColors.Text(realtime.TextColor))
            : swatch == ScrimColorBtn ? (ScrimColorPicker, RealtimeSubtitleColors.Scrim(realtime.ScrimColor, 100))
            : (BorderColorPicker, RealtimeSubtitleColors.Border(realtime.BorderColor));

        // The swatch of the open picker closes it, as a menu button closes its menu.
        var reopening = ReferenceEquals(_openPicker, picker);
        ClosePicker();
        if (reopening) return;

        picker.Begin(Color.FromRgb(color.R, color.G, color.B));
        picker.Visibility = Visibility.Visible;
        swatch.Tag = "Open";
        _openPicker = picker;
        _openSwatch = swatch;
    }

    /// <summary>Folds the open picker away; its 返回 list goes with it — see ColorPickHistory.</summary>
    private void ClosePicker()
    {
        if (_openPicker is { } picker)
        {
            // Out of the box first, so a code typed and not yet entered is applied, not lost.
            if (picker.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            picker.Visibility = Visibility.Collapsed;
        }

        if (_openSwatch is { } swatch) swatch.Tag = null;
        _openPicker = null;
        _openSwatch = null;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press in one of the boxes that are typed into is the one thing here that wants the
    /// keyboard, so it is the one thing that takes the foreground.
    /// </summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsActive || !IsInTextBox(e.OriginalSource as DependencyObject)) return;
        TakeForeground();
    }

    /// <summary>
    /// Enter has applied what was typed and Esc has put it back; either way the user is done typing,
    /// and the keyboard goes back to whatever had it — usually the game.
    /// </summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || !IsActive) return;

        Keyboard.ClearFocus();
        GiveBackForeground();
    }

    private static bool IsInTextBox(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is TextBox) return true;
        return false;
    }

    /// <summary>Closes the panel, handing the keyboard back if a box had taken it.</summary>
    public void Dismiss()
    {
        if (_closing) return;

        // A code or a size typed and not yet entered is applied on the way out rather than lost.
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();

        _closing = true;
        if (IsActive) GiveBackForeground();
        Close();
    }

    // ── Foreground ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes this the window the keyboard talks to; see RealtimeLanguageMenu.TakeForeground for why
    /// the foreground thread's input queue is shared for the call.
    /// </summary>
    private void TakeForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var foreground = GetForegroundWindow();
        if (foreground != hwnd) _previousForeground = foreground;

        var owner = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);

        try
        {
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(self, owner, false);
        }
    }

    private void GiveBackForeground()
    {
        var previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (previous == IntPtr.Zero || !IsWindow(previous)) return;
        if (previous == new WindowInteropHelper(this).Handle) return;

        // Allowed without ceremony: this window holds the foreground, and the holder may pass it on.
        SetForegroundWindow(previous);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
