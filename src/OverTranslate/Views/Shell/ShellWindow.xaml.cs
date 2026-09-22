using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Views.Realtime;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Translation;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Shell;

public enum ShellPage
{
    Translation,
    Realtime,
    Settings
}

/// <summary>
/// The application's single operable window: a left nav rail plus a content area that swaps
/// between pages. Every entry point (tray icon, tray menu, capture toolbar, second launch)
/// funnels through <see cref="ShowOrActivate"/> so only one of these ever exists.
/// </summary>
public partial class ShellWindow : Window
{
    private static ShellWindow? _instance;

    /// <summary>
    /// The open shell, or null when it is closed to the tray.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ShowOrActivate"/> this never creates one. A shortcut that starts 即時翻譯
    /// needs to hand the shell over to be hidden when it happens to be open, and must not conjure a
    /// window to hide when it is not — the session is about to cover the screen either way.
    /// Reliable because <see cref="OnClosed"/> clears it.
    /// </remarks>
    public static ShellWindow? Current => _instance;

    /// <summary>
    /// The shell's dimensions from the last time it was open, for as long as the process lives.
    /// </summary>
    /// <remarks>
    /// In memory rather than in the settings file, on purpose. Closing this window is the ordinary
    /// way to get it off the screen — the app goes on running in the tray — so reopening it during
    /// the same sitting should give back the window the user had, not the one the app ships with.
    /// Surviving a restart is a different question, and the answer there is the designed default:
    /// centred, at a size picked to fit the content.
    /// </remarks>
    private static Size? _lastSize;

    /// <inheritdoc cref="_lastSize"/>
    private static WindowState _lastWindowState = WindowState.Normal;

    /// <inheritdoc cref="_lastSize"/>
    private static bool _lastRailCollapsed;

    /// <summary>
    /// Whether the pin was on the last time the shell was open, for as long as the process lives.
    /// </summary>
    /// <remarks>
    /// Deliberately not in the settings file, and this is the whole of the pin's memory: it is a
    /// choice about the sitting the user is in — reading something beside a window they are typing
    /// into — not a preference about the application, so a restart starts unpinned. Remembered
    /// across a close and reopen for the same reason the size and the page are: closing this window
    /// is how it is put away, and a user who pinned it should not have to pin it again every time
    /// it comes back within the same sitting.
    /// </remarks>
    private static bool _lastPinned;

    /// <summary>
    /// The page the shell was last showing, so opening it again lands where the user left off
    /// rather than always on 文字翻譯.
    /// </summary>
    /// <inheritdoc cref="_lastSize"/>
    private static ShellPage _lastPage = ShellPage.Translation;

    private static readonly Duration IndicatorDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration ContentDuration   = new(TimeSpan.FromMilliseconds(120));

    // Token for the in-flight content transition; see AnimateContentIn.
    private object? _contentTransition;

    private readonly TranslationPage _translationPage = new();
    private readonly RealtimePage    _realtimePage    = new();
    private readonly SettingsPage    _settingsPage    = new();

    private ShellPage? _current;

    public TranslationPage TranslationPage => _translationPage;

    /// <summary>
    /// Shows the shell on <paramref name="page"/>, creating it if needed, and returns the
    /// live instance so callers can push content into a page.
    /// </summary>
    /// <param name="page">
    /// Where to land, or null for wherever the shell was last left — which is what a plain
    /// "open the window" means. Callers with something to say pass the page that says it: the
    /// tray's 設定 item, and the capture flow handing its result to 文字翻譯.
    /// </param>
    public static ShellWindow ShowOrActivate(ShellPage? page = null)
    {
        // Read before the window is built: constructing one mounts a page, which writes _lastPage,
        // so asking for it after would only ever get back the page the constructor chose.
        var target = page ?? _lastPage;

        if (_instance == null)
        {
            _instance = new ShellWindow();
            _instance.Show();
        }
        else if (_instance.WindowState == WindowState.Minimized)
        {
            _instance.WindowState = WindowState.Normal;
        }

        _instance.Navigate(target);
        _instance.RefreshHotkeyHints();
        _instance.RefreshQuickToolAvailability();

        var shell = _instance;
        shell.Dispatcher.BeginInvoke(shell.Activate, DispatcherPriority.ApplicationIdle);
        return shell;
    }

    public ShellWindow()
    {
        InitializeComponent();

        BrandIcon.Source = AppIconService.CreateMainIcon();
        var compactIcon = AppIconService.CreateCompactIcon();
        Icon = compactIcon;
        TitleIcon.Source = compactIcon;
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";

        _instance = this;
        MatchBrandIconToText();
        RefreshHotkeyHints();

        // Subscribed once here rather than per open, so the handler is not stacked up by a user who
        // opens the service panel more than once.
        ServiceSettings.Closed += (_, _) => _settingsPage.RefreshServiceTiles();
        DiagnosticNote.Closed += (_, result) => _settingsPage.ReportDiagnosticUpload(result);

        // Subscribed rather than refreshed on show: a session ending brings this window back with
        // Show(), not through ShowOrActivate, so nothing else would clear the disabled state and
        // the button would stay greyed out for as long as the shell stayed open.
        Realtime.RealtimeSessionController.Instance.StateChanged += OnRealtimeStateChanged;
        RefreshQuickToolAvailability();

        // Same reasoning, and one more: the periodic check runs whether or not this window exists,
        // so the rail has to be able to gain the row while the user is sitting in front of it.
        UpdateNotifier.AvailabilityChanged += OnUpdateAvailabilityChanged;
        RefreshUpdateAvailability();

        // The rail's composed strings — the update row's version and the 快速工具 rows'
        // blocked-by-realtime tooltips — are set from code, so DynamicResource does not reach them
        // and they would keep the language they were built in. The settings page that changes the
        // language lives inside this window, so they are always on screen when it happens.
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;

        // The system title bar is gone, so what it used to do for itself is done here: maximising
        // to the work area rather than over it, rounding the window and colouring its outer edge,
        // and saying which of maximise or restore this window is currently offering.
        WindowFrame.Attach(this);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
        StateChanged += (_, _) => RefreshMaximizeButton();
        RefreshMaximizeButton();

        RestoreSessionLayout();

        // Nav_Checked drives navigation, so this also renders the initial page
        TranslationNav.IsChecked = true;
    }

    /// <inheritdoc cref="_lastSize"/>
    private void RestoreSessionLayout()
    {
        if (_lastSize is { } size)
        {
            Width  = size.Width;
            Height = size.Height;
        }

        if (_lastWindowState == WindowState.Maximized) WindowState = WindowState.Maximized;

        _railCollapsed = _lastRailCollapsed;
        SetRailWidth(_railCollapsed ? 0 : RailWidth);
        RefreshSidebarToggle();

        SetPinned(_lastPinned);
    }

    // The window's outer edge is the compositor's, not this window's, so it is the one colour in
    // the application that has to be handed over again rather than re-resolved.
    private void OnThemeChanged(object? sender, EventArgs e) => WindowFrame.ApplyAppearance(this);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshQuickToolAvailability();
        RefreshUpdateAvailability();
        RefreshSidebarToggle();
        RefreshPinButton();
    }

    /// <summary>
    /// Caps the brand mark to the height of the wordmark and version beside it.
    /// </summary>
    /// <remarks>
    /// A size-changed handler rather than a binding on ActualHeight. The grid's first column is
    /// Auto, so its width comes from the image's desired width, which under a binding is still the
    /// source bitmap's own until the binding has resolved — by which point the column has claimed
    /// the whole rail and squeezed out the text the height was to be measured from. Measuring after
    /// the text has been laid out has no such ordering to lose.
    ///
    /// Only on a height change. Setting the image's width resizes the Auto column, which resizes
    /// the starred column beside it, which raises this event again — reacting to that would loop.
    /// The badge is deliberately not part of the measurement: it is on its own row, so a release
    /// appearing never resizes the mark.
    /// </remarks>
    private void MatchBrandIconToText()
    {
        BrandText.SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged) return;
            BrandIcon.Width = BrandIcon.Height = e.NewSize.Height;
        };
    }

    /// <summary>
    /// Re-reads the shortcuts into the 快速工具 rows' trailing hints. Called on construction and on
    /// every ShowOrActivate, and directly by <see cref="Settings.SettingsPage"/> the moment a new
    /// shortcut is recorded — the rail is visible the whole time the user is on 設定, so waiting
    /// for the next navigation would leave the wrong shortcut on screen next to the button.
    /// </summary>
    public void RefreshHotkeyHints()
    {
        var settings = SettingsService.Instance.Current;

        // Blank rather than a "未設定" placeholder: the label already says what the button does,
        // and a slot that only ever holds a shortcut should be empty when there is none.
        CaptureHotkeyText.Text = string.IsNullOrWhiteSpace(settings.HotkeyDisplay)
            ? ""
            : settings.HotkeyDisplay;

        // Also blank when the shortcut is switched off in 設定: this row is not registered then, so
        // printing the combination would advertise a key press that does nothing. The button
        // itself stays — it is a way in of its own, not a reminder of the shortcut.
        QuickLookupHotkeyText.Text =
            settings.QuickLookupHotkeyEnabled && !string.IsNullOrWhiteSpace(settings.QuickLookupHotkeyDisplay)
                ? settings.QuickLookupHotkeyDisplay
                : "";
    }

    /// <summary>
    /// Greys out the rail's 快速工具 rows a realtime session is in the way of, and says why.
    /// </summary>
    /// <remarks>
    /// The two rows answer to different rules now. 截圖翻譯 runs during a session — it stands the
    /// session down for its own duration, see Services.Realtime.RealtimeCaptureInterlude — and is
    /// refused only while blocks are being framed, where there is nothing running to stand down and
    /// the screenshot would be of the edit layer. 取詞翻譯 is refused for the whole of a session, on
    /// a rule about the screen rather than the engine: see MainWindow.RefuseWhileRealtimeRuns.
    ///
    /// This is the half the user sees; each feature enforces its own rule when pressed. The shell is
    /// hidden for the duration of a session, so it matters in one specific way in: a user who opens
    /// 設定 from the tray mid-session gets the rail, and its actions with it. That is also why the
    /// framing state is a backstop rather than something met — the edit layer is topmost and covers
    /// the screen, so the rail is behind it while blocks are being drawn.
    /// </remarks>
    private void OnRealtimeStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshQuickToolAvailability);

    public void RefreshQuickToolAvailability()
    {
        var controller = Realtime.RealtimeSessionController.Instance;
        var running = controller.IsActive;
        var framing = running && !controller.IsTranslating;

        CaptureBtn.IsEnabled = !framing;
        CaptureBtn.ToolTip = framing
            ? LocalizationService.Get("S.Shell.CaptureBlockedByRealtimeFraming")
            : null;

        // 取詞翻譯 is not merely refused during a session — a session already dismisses the popup
        // when it starts (MainWindow.OnRealtimeStateChanged), so a row that still looked available
        // would open a window that closes itself.
        QuickLookupBtn.IsEnabled = !running;
        QuickLookupBtn.ToolTip = running
            ? LocalizationService.Get("S.Shell.QuickLookupBlockedByRealtime")
            : null;
    }

    private void OnUpdateAvailabilityChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshUpdateAvailability);

    /// <summary>
    /// Shows or hides the rail's update badge to match what <see cref="UpdateNotifier"/> has found.
    /// </summary>
    /// <remarks>
    /// Unaffected by 跳過此版本 by design — that choice silences the startup dialog, and taking the
    /// badge away with it would leave a user who skipped a release with no way back to it short of
    /// reinstalling.
    /// </remarks>
    private void RefreshUpdateAvailability()
    {
        var update = UpdateNotifier.Available;
        if (update is null)
        {
            UpdateBtn.Visibility = Visibility.Collapsed;
            return;
        }

        // The version is on the badge itself rather than behind a hover: it is the one piece of
        // information that tells the user whether this is the release they already decided about.
        // 「至」carries the relationship to the version directly above — this is where that number
        // goes, not merely that a number exists. "v" matches that label's own formatting.
        UpdateBtnText.Text = LocalizationService.Format("S.Shell.UpdateAvailable", update.LatestVersion);
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        var update = UpdateNotifier.Available;
        if (update is null) return;
        UpdateWindow.ShowOrActivate(update);
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Puts the right one of maximise and restore on the middle caption button.
    /// </summary>
    /// <remarks>
    /// Driven from StateChanged rather than only from the button's own click: double-clicking the
    /// title bar and Aero Snap both maximise this window without going anywhere near it.
    /// </remarks>
    private void RefreshMaximizeButton() =>
        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "" : "";

    /// <summary>The rail's width whenever it is showing.</summary>
    /// <remarks>
    /// One width rather than a range. The rail holds a fixed set of rows whose widest line is a
    /// 快速工具 label and its shortcut, so there is one width that fits them — every other width is
    /// either wasting the page's or trimming the rail's own labels. What a user dragging the rail
    /// narrow actually wants is the width back, and the toggle in the title bar gives them all of
    /// it in one press.
    /// </remarks>
    private const double RailWidth = 248;

    private static readonly Duration RailDuration = new(TimeSpan.FromMilliseconds(320));

    /// <summary>
    /// The rail column's width, as something that can be animated.
    /// </summary>
    /// <remarks>
    /// GridLength is not a double and WPF ships no animation for it, so a column driven directly
    /// can only jump. Animating a plain double here and writing the column from it is what lets the
    /// rail settle into place instead.
    /// </remarks>
    private static readonly DependencyProperty RailWidthProperty = DependencyProperty.Register(
        "RailWidth", typeof(double), typeof(ShellWindow),
        new PropertyMetadata(0.0, (d, e) => ((ShellWindow)d).ApplyRailWidth((double)e.NewValue)));

    /// <summary>
    /// The rail's width as it has just been written, animation included.
    /// </summary>
    /// <remarks>
    /// Not ActualWidth: the column's own Width is what the animation writes, and it is read here
    /// before the layout pass that would make ActualWidth agree — so a toggle pressed mid-slide
    /// would otherwise carry on from the width the rail had a frame ago and jump.
    /// </remarks>
    private double CurrentRailWidth => SidebarColumn.Width.IsAbsolute
        ? SidebarColumn.Width.Value
        : SidebarColumn.ActualWidth;

    private void ApplyRailWidth(double width)
    {
        SidebarColumn.Width = new GridLength(Math.Max(0, width));

        // Only once there is nothing left to draw. The rail is clipped to its column, so the
        // collapse reads as the card being wiped off the edge rather than as it shrinking — and a
        // panel left visible at zero width would still take a hit test in the page's first pixel.
        RailPanel.Visibility = width < 1
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Puts the rail at <paramref name="width"/> at once, for a layout being restored.</summary>
    private void SetRailWidth(double width)
    {
        BeginAnimation(RailWidthProperty, null);
        ApplyRailWidth(width);
    }

    private void AnimateRailTo(double width)
    {
        var from = CurrentRailWidth;
        if (Math.Abs(from - width) < 0.5)
        {
            SetRailWidth(width);
            return;
        }

        // From wherever the rail is on screen right now, not from where the animation in flight was
        // headed: pressing the button again mid-slide reverses it instead of finishing first.
        // Critically damped — eased out with no overshoot. The rail is a panel being put somewhere,
        // not something thrown, and a bounce here would read as it having missed.
        BeginAnimation(RailWidthProperty, new DoubleAnimation
        {
            From = from,
            To = width,
            Duration = RailDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>Whether the rail is currently put away.</summary>
    /// <remarks>
    /// Held here rather than read back off the column, so a press that lands while the slide is
    /// still running toggles what the rail is going to be rather than whatever width it happens to
    /// be passing through at that instant.
    /// </remarks>
    private bool _railCollapsed;

    private void SidebarToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _railCollapsed = !_railCollapsed;
        AnimateRailTo(_railCollapsed ? 0 : RailWidth);
        // Now, rather than when the slide lands: the button already offers the opposite of what it
        // just did, and a tooltip that only caught up 320ms later would be wrong for the whole of
        // the one moment the pointer is still sitting on it.
        RefreshSidebarToggle();
    }

    /// <summary>
    /// Says what pressing the toggle would do next, in the language the window is currently in.
    /// </summary>
    /// <remarks>
    /// The glyph does not change with the state — it names the rail rather than a direction, the
    /// way Windows' own pane toggle does — so this text is the only place that direction is stated.
    /// It is the accessible name as well as the tooltip: a glyph-only button has nothing else for a
    /// screen reader to read out.
    /// </remarks>
    private void RefreshSidebarToggle()
    {
        var label = LocalizationService.Get(_railCollapsed
            ? "S.Shell.ShowSidebar"
            : "S.Shell.HideSidebar");

        SidebarToggleBtn.ToolTip = label;
        AutomationProperties.SetName(SidebarToggleBtn, label);
    }

    /// <summary>Whether the user has asked for this window to stay above the others.</summary>
    private bool _pinned;

    private void PinBtn_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    /// <summary>
    /// Turns the pin on or off, which is only ever the window's z-order.
    /// </summary>
    /// <remarks>
    /// Topmost and nothing else, on purpose. Everything that already takes this window off the
    /// screen goes on doing it while pinned — the shortcut that closes the shell when it is the
    /// window in front, 截圖翻譯 hiding it so it is not baked into the shot, a realtime session
    /// putting it away for the duration. A pin that also held against those would be a second,
    /// invisible rule about when the window may close, and the user asked for one thing: that it
    /// stop going behind whatever they click next.
    /// </remarks>
    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        Topmost = pinned;
        RefreshPinButton();
    }

    /// <summary>
    /// Draws the pin: the glyph is the action, the accent is the state.
    /// </summary>
    /// <remarks>
    /// The same two glyphs and the same accent as 取詞翻譯's pin, so the two read as one control
    /// even though they hold against different things. Segoe MDL2 codepoints so they survive
    /// Windows 10, where Segoe Fluent Icons is not installed; see Views/Controls/TtsGlyphs.
    ///
    /// Off, it is quieter than the caption glyphs beside it: unpinned is the state every session
    /// starts in, and a strip where four buttons are equally loud says the pin is as ordinary as
    /// closing the window. The tooltip is the accessible name too — there is nothing else on a
    /// glyph-only button for a screen reader to read out.
    /// </remarks>
    private void RefreshPinButton()
    {
        PinBtn.Content = _pinned ? "" : "";

        var label = LocalizationService.Get(_pinned ? "S.Shell.Unpin" : "S.Shell.Pin");
        PinBtn.ToolTip = label;
        AutomationProperties.SetName(PinBtn, label);

        PinBtn.SetResourceReference(
            ForegroundProperty, _pinned ? "AppAccent" : "AppTextSecondary");
    }

    private void CaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow owns the whole capture session (hotkey, screenshot, overlay, teardown), so
        // the shell only asks for one and hands itself over to be hidden and brought back.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartCaptureFromShell(this);
    }

    private void QuickLookupBtn_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow owns 取詞翻譯's guards the same way it owns the capture session's, so the shell
        // asks for the popup rather than summoning one behind them.
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartQuickLookupFromShell();
    }

    public void Navigate(ShellPage page)
    {
        var target = NavItemFor(page);
        if (target.IsChecked == true)
        {
            // Already here — still make sure the content is mounted (first call from the ctor)
            if (_current != page) ShowPage(page);
            return;
        }
        target.IsChecked = true;   // raises Nav_Checked
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        var page =
            ReferenceEquals(sender, SettingsNav) ? ShellPage.Settings :
            ReferenceEquals(sender, RealtimeNav) ? ShellPage.Realtime :
            ShellPage.Translation;
        if (_current == page) return;
        ShowPage(page);
    }

    private void ShowPage(ShellPage page)
    {
        _current = page;
        _lastPage = page;

        // These pages read state that can change while the user is elsewhere: shared translation
        // preferences, the settings file, and attached monitors plus realtime session state.
        if (page == ShellPage.Translation) _translationPage.Reload();
        if (page == ShellPage.Settings) _settingsPage.Reload();
        if (page == ShellPage.Realtime) _realtimePage.Reload();

        ContentHost.Child = page switch
        {
            ShellPage.Settings => _settingsPage,
            ShellPage.Realtime => _realtimePage,
            _                  => (UIElement)_translationPage
        };

        MoveIndicatorTo(NavItemFor(page));
        AnimateContentIn();
    }

    private System.Windows.Controls.RadioButton NavItemFor(ShellPage page) => page switch
    {
        ShellPage.Settings => SettingsNav,
        ShellPage.Realtime => RealtimeNav,
        _                  => TranslationNav
    };

    /// <summary>
    /// Slides the accent bar to the selected item. The offset is measured from the live layout
    /// rather than assumed from an item height, so it stays correct if the nav gains items.
    /// </summary>
    private void MoveIndicatorTo(FrameworkElement item)
    {
        if (!item.IsLoaded)
        {
            // First navigation happens before layout — retry once the nav has been measured
            item.Loaded += OnceLoaded;
            return;
        }

        var top = item.TransformToAncestor(NavPanel).Transform(new Point(0, 0)).Y;
        var to  = top + (item.ActualHeight - NavIndicator.Height) / 2;

        NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = to,
            Duration = IndicatorDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        void OnceLoaded(object? s, RoutedEventArgs e)
        {
            item.Loaded -= OnceLoaded;
            MoveIndicatorTo(item);
        }
    }

    private void AnimateContentIn()
    {
        // Identifies this particular transition, so a fast page switch cannot have the previous
        // animation's completion handler tear down the animation that replaced it.
        var transition = new object();
        _contentTransition = transition;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated or
        // scrolled, then ramps snapping back on over roughly a second once the motion stops. That
        // ramp is why the page used to sit visibly settled and stay soft for a beat before turning
        // sharp, and there is no API to shorten or disable it. Rendering the page into a bitmap
        // cache for the duration of the slide sidesteps the whole mechanism: the glyphs are
        // rasterised once as static, snapped text, and the render thread then only moves the
        // finished bitmap, so nothing ever looks like animating text to the detector.
        // SnapsToDevicePixels keeps that bitmap on whole pixels as it moves, so no frame of the
        // slide is resampled either. The cache is dropped again in ReleaseContentAnimations.
        ContentHost.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        // Both animations run for the same duration, so one Completed handler covers the pair.
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = ContentDuration };
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_contentTransition, transition)) return;
            ReleaseContentAnimations();
        };

        ContentHost.BeginAnimation(OpacityProperty, fade);
        ContentTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 8, To = 0,
            Duration = ContentDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // DoubleAnimation defaults to FillBehavior.HoldEnd, which keeps the animated properties under
    // the animation clock's control even after the animation has visually finished, holding the
    // content in an intermediate composition layer indefinitely. Handing the properties back to the
    // elements drops that layer the moment the transition ends, so the settled page renders exactly
    // as it would have if it had never animated.
    private void ReleaseContentAnimations()
    {
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = 1;

        ContentTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ContentTransform.Y = 0;

        // Back to rendering the live visual tree — the cache existed only for the slide, and the
        // settled page has to be real text again for selection, scrolling and DPI changes.
        ContentHost.CacheMode = null;
    }

    private void AboutBtn_Click(object sender, RoutedEventArgs e) => About.Open();

    /// <summary>
    /// Opens the panel holding what one translation service has to be told, over the whole window.
    /// </summary>
    /// <remarks>
    /// Hosted here rather than inside the settings page so the scrim covers the nav rail too. The
    /// page is told when it closes because what was typed in there is what its service tiles report.
    /// </remarks>
    public void OpenServiceSettings(Models.TranslationProvider provider)
        => ServiceSettings.Open(provider);

    /// <summary>
    /// Opens the panel that collects what the user wants to say about the problem and sends the
    /// bundle with it.
    /// </summary>
    /// <remarks>
    /// Hosted here for the same reason the service panel is: the scrim has to cover the nav rail,
    /// and an upload in flight is exactly the moment the user must not be able to navigate away
    /// from the thing that will tell them how it went.
    /// </remarks>
    public void OpenDiagnosticNote(string bundlePath)
        => DiagnosticNote.Open(bundlePath);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // RestoreBounds rather than the live size: a maximised window's actual size is the screen's,
        // and handing that back as a normal-state size would reopen a window that fills the display
        // without being maximised — and with no way to shrink it back short of dragging.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _lastSize = new Size(bounds.Width, bounds.Height);
        // Minimised is a state to reopen out of, not into.
        _lastWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _lastRailCollapsed = _railCollapsed;
        _lastPinned = _pinned;

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // The window is destroyed on close (not hidden), so the pages' timers, TTS playback
        // and HTTP handles have to go with it.
        _translationPage.Teardown();
        // Only detaches the page from the session controller: a realtime session already running is
        // on the screen, not in this window, and closing the shell is not a request to end it.
        _realtimePage.Teardown();
        Realtime.RealtimeSessionController.Instance.StateChanged -= OnRealtimeStateChanged;
        // UpdateNotifier is static and outlives every window, so a handler left attached would keep
        // this closed window alive for as long as the application runs.
        UpdateNotifier.AvailabilityChanged -= OnUpdateAvailabilityChanged;
        _instance = null;
        base.OnClosed(e);
    }
}
