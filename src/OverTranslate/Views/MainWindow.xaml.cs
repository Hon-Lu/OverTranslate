using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using NLog;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Capture;
using OverTranslate.Views.Overlay;
using OverTranslate.Views.Realtime;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Translation;

namespace OverTranslate.Views;

public partial class MainWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _trayMenu;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _windowHotkey;
    private GlobalHotkey? _realtimePauseHotkey;
    private GlobalHotkey? _quickLookupHotkey;
    private GlobalHotkey? _quickTranslateHotkey;
    private GlobalAuxiliaryHotkeys? _auxiliaryHotkeys;
    private OverlayWindow? _overlayWindow;
    private ScreenCaptureWindow? _captureWindow;
    private ToolbarWindow? _toolbarWindow;
    private AnnotationPanelWindow? _annotationPanel; // only while 標記 is on
    private AnnotationShortcutHook? _annotationKeys;  // likewise

    // Which pen 標記 has in hand. Reset by every new capture and kept nowhere else: what it is for
    // is closing the panel and reopening it inside one capture without losing the colour just
    // chosen, and that is the whole of it. Across captures the defaults are the point — a fresh
    // capture is a fresh piece of work, and starting it on whatever was left over from the last one
    // is a state the user has to notice and undo before drawing.
    private Models.AnnotationTool _annotationTool = AnnotationPanelWindow.DefaultTool;
    private System.Windows.Media.Color _annotationColor = AnnotationPanelWindow.DefaultColor;
    private double _annotationThickness = AnnotationPanelWindow.DefaultThickness;
    private double _annotationOpacity = AnnotationPanelWindow.DefaultOpacity;
    private GlobalEscapeHook? _escapeHook; // lives for the whole capture session, see CloseAll
    private SystemRecoveryYield? _recoveryYield; // same lifetime; lets Task Manager out from under the layers
    private CancellationTokenSource? _sessionCts; // cancelled on teardown so abandoned work stops
    private EventHandler? _overlayClosedHandler; // tracked so we can detach before re-translate
    // Recognition and translation are not owned here — see AppServices. This window is one of two
    // callers, not the holder, and the call sites below name AppServices directly so that reading
    // any one of them shows where the engine comes from.

    // The voice for the capture toolbar's speak button. Its own instance rather than the
    // translation page's: the two are separate places with separate stop buttons, and one shared
    // player would have the page's speaker silently stop a capture that is still reading.
    private readonly TtsService _tts = new();

    // Kept alive so toolbar translate can re-run OCR/translation on the current selection
    private List<OcrTextBlock> _lastOcrBlocks = [];

    // What the translator answered, one entry per group. Copying, the translation window and the
    // toolbar read this, and they must keep reading it: placement below may cut a group up so that
    // each source line gets its own share on screen, and a sentence handed back to the user in
    // pieces is a worse answer than the one it was translated as.
    private List<TranslatedBlock> _lastTranslatedBlocks = [];

    // The same translation after placement, with the colours sampled from underneath each of these
    // boxes. This is what the overlay draws, and the only list whose indices line up with it.
    private List<TranslatedBlock> _lastColoredBlocks = [];
    private double _lastSelPhysLeft;
    private double _lastSelPhysTop;
    private double _lastSelPhysWidth;
    private double _lastSelPhysHeight;
    private bool _lastVerticalText;
    private int _selectionSessionId;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void InitializeApp()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // The service reports every end of playback — natural, failed, or stopped — and the toolbar
        // button has to come back from ⏹ to the speaker whichever it was. Start is reflected on the
        // press instead, so the icon does not wait on the fetch.
        _tts.StateChanged += (_, _) => _toolbarWindow?.SetSpeaking(_tts.IsActive);

        InitNotifyIcon();
        RegisterHotkey();
        ShowStartupBalloon();

        // The one thing about a realtime session that has to reach the user outside this
        // application: the window they were watching closed, so the session is over and they are
        // looking at whatever was behind it.
        RealtimeSessionController.Instance.SessionEnded += OnRealtimeSessionEnded;

        // A session composes the monitor without this application's overlays and cannot be told
        // about a window created after it started, so a popup left up would be read back into the
        // subtitles — see OnQuickLookupHotkeyPressed, which is the half that keeps a new one out.
        RealtimeSessionController.Instance.StateChanged += OnRealtimeStateChanged;
    }

    private void OnRealtimeStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!RealtimeSessionController.Instance.IsActive) return;

            QuickLookup.QuickLookupWindow.Dismiss();
            QuickTranslate.QuickTranslateHintWindow.Dismiss();
        }));

    private void OnRealtimeSessionEnded(object? sender, string message) =>
        Dispatcher.Invoke(() => ShowTrayNotification(
            LocalizationService.Get("S.Realtime.SessionEndedTitle"), message));

    private void InitNotifyIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconService.CreateTrayIcon(),
            Text = "OverTranslate",
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)  OnTrayLeftClick();
            if (e.Button == MouseButtons.Right) ShowTrayMenu();
        };
    }

    /// <summary>
    /// Binds the shortcuts the settings say should be live, highest priority first.
    /// </summary>
    /// <remarks>
    /// Through <see cref="HotkeyBindings"/> rather than one <c>Register</c> call per shortcut,
    /// because two of them can want the same combination and Windows resolves that badly: it keys a
    /// registration by window and combination, so the second claim is refused, <c>RegisterHotKey</c>
    /// returns false, and nothing else happens — one shortcut stops working and no code here would
    /// know which. Which one lost would also be whichever happened to be registered second, an
    /// ordering nobody chose.
    ///
    /// The settings page refuses to RECORD a combination another shortcut holds, so this is not
    /// reachable by editing. It is reachable two other ways, and both are the point: settings.json is
    /// a text file someone can edit, and shipping a new shortcut hands every existing installation a
    /// default it never agreed to — anyone already using that combination for something else would
    /// have had one of the two go quiet.
    ///
    /// A shortcut with no action would be absent from the table below rather than registered against
    /// nothing: claiming a combination globally takes it away from every other application, which is
    /// a real cost to pay for a key that would do nothing.
    /// </remarks>
    private void RegisterHotkey()
    {
        var settings = SettingsService.Instance.Current;
        var hwnd = new WindowInteropHelper(this).Handle;

        _hotkey = new GlobalHotkey(GlobalHotkey.CaptureId);
        _hotkey.HotkeyPressed += OnHotkeyPressed;

        // Deliberately quiet, unlike the one above: no startup notification and nothing in the
        // interface naming it. It saves a trip to the tray for a user who already knows it is
        // there, and a user who does not loses nothing by never finding it.
        _windowHotkey = new GlobalHotkey(GlobalHotkey.TranslationWindowId);
        _windowHotkey.HotkeyPressed += OnTranslationWindowHotkeyPressed;

        _realtimePauseHotkey = new GlobalHotkey(GlobalHotkey.RealtimePauseId);
        _realtimePauseHotkey.HotkeyPressed += OnRealtimePauseHotkeyPressed;

        _quickLookupHotkey = new GlobalHotkey(GlobalHotkey.QuickLookupId);
        _quickLookupHotkey.HotkeyPressed += OnQuickLookupHotkeyPressed;

        _quickTranslateHotkey = new GlobalHotkey(GlobalHotkey.QuickTranslateId);
        _quickTranslateHotkey.HotkeyPressed += OnQuickTranslateHotkeyPressed;

        var hooks = new Dictionary<HotkeyAction, GlobalHotkey>
        {
            [HotkeyAction.Capture] = _hotkey,
            [HotkeyAction.TranslationWindow] = _windowHotkey,
            [HotkeyAction.RealtimePause] = _realtimePauseHotkey,
            [HotkeyAction.QuickLookup] = _quickLookupHotkey,
            [HotkeyAction.QuickTranslate] = _quickTranslateHotkey,
        };

        var resolved = HotkeyBindings.Resolve(settings);
        foreach (var binding in resolved)
        {
            if (binding.ShadowedBy is { } holder)
            {
                // Warn rather than Debug: the user pressed a key and nothing happened, and this line
                // is the only place that says why.
                Log.Warn(
                    "Hotkey {Action} not registered: {Holder} already claims that trigger",
                    binding.Action, holder);
                continue;
            }

            if (!binding.Enabled)
            {
                Log.Info("Hotkey {Action} is switched off in settings", binding.Action);
                continue;
            }

            // Reachable only through a hand-edited settings.json — the settings page refuses to
            // record it — and refused here for the reason it is refused there: a bare typing key
            // registered globally stops working in every other application.
            if (!HotkeyBindings.IsBindable(binding.Trigger))
            {
                Log.Warn(
                    "Hotkey {Action} not registered: {Display} is a single key that cannot be claimed globally",
                    binding.Action, binding.Trigger.VirtualKey);
                continue;
            }

            if (binding.InputKind == OverTranslate.Models.ShortcutInputKind.Keyboard &&
                hooks.TryGetValue(binding.Action, out var hook))
            {
                hook.Register(hwnd, binding.Modifiers, binding.VirtualKey);
            }
        }

        _auxiliaryHotkeys = new GlobalAuxiliaryHotkeys();
        _auxiliaryHotkeys.ShortcutPressed += OnAuxiliaryHotkeyPressed;
        _auxiliaryHotkeys.Register(resolved);
    }

    private void OnAuxiliaryHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Capture:
                OnHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.TranslationWindow:
                OnTranslationWindowHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.RealtimePause:
                OnRealtimePauseHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.QuickLookup:
                OnQuickLookupHotkeyPressed(this, EventArgs.Empty);
                break;
            case HotkeyAction.QuickTranslate:
                OnQuickTranslateHotkeyPressed(this, EventArgs.Empty);
                break;
        }
    }

    private void ShowStartupBalloon()
    {
        var hotkeyDisplay = SettingsService.Instance.Current.HotkeyDisplay;
        var shortcutText = string.IsNullOrWhiteSpace(hotkeyDisplay)
            ? LocalizationService.Get("S.Main.DefaultShortcutName")
            : hotkeyDisplay;

        ShowTrayNotification(
            LocalizationService.Get("S.Main.MinimizedTitle"),
            LocalizationService.Format("S.Main.MinimizedBody", shortcutText));
    }

    /// <summary>
    /// A notification through the tray icon, which Windows presents in its own notification centre.
    /// </summary>
    /// <remarks>
    /// The application's own <see cref="ToastWindow"/> is for things that belong to a capture — it
    /// appears beside the selection it is talking about and disappears with it. Something the user
    /// caused from outside any capture, such as a shortcut that declined to start one, has no such
    /// anchor, and telling them through the shell they already associate with this application is
    /// both less startling and something they can go back and read.
    /// </remarks>
    private void ShowTrayNotification(string title, string message)
    {
        if (_notifyIcon == null) return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// Rebinds every shortcut, after any one of them has been changed.
    /// </summary>
    /// <remarks>
    /// All of them rather than the one that changed, because the cost is two Win32 calls and the
    /// alternative is a second entry point that has to be kept in step with which setting the
    /// settings page happened to write.
    /// </remarks>
    public void ReRegisterHotkey()
    {
        _hotkey?.Unregister();
        _windowHotkey?.Unregister();
        _realtimePauseHotkey?.Unregister();
        _quickLookupHotkey?.Unregister();
        _quickTranslateHotkey?.Unregister();
        _auxiliaryHotkeys?.Dispose();
        _auxiliaryHotkeys = null;
        RegisterHotkey();
    }

    /// <summary>
    /// Starts a capture, unless a realtime session has the screen.
    /// </summary>
    /// <remarks>
    /// This shortcut used to pause and resume a running session, on the reasoning that a session
    /// rules a capture out anyway — so the key was free for the whole of a session that may run for
    /// hours, and pausing is what a user in front of one keeps needing. It was still one key with two
    /// meanings, and the reader could neither choose what to press for the frequent one nor put it
    /// where their hands are while a game has the screen. 暫停 / 繼續 has its own shortcut now — see
    /// <see cref="OnRealtimePauseHotkeyPressed"/> — and this key means the one thing it is named after.
    ///
    /// Which brings back the refusal: the two features share one OCR engine and one bounded pool of
    /// inference slots, so a capture during a session is turned away and told why rather than
    /// competing for them. <see cref="RefuseWhileRealtimeRuns"/> covers block framing too, where a
    /// capture is equally out of the question.
    /// </remarks>
    private void OnHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(async () =>
        {
            if (RefuseWhileRealtimeRuns()) return;

            await RunCaptureSessionAsync();
        });

    /// <summary>
    /// Pauses a running realtime session, or resumes a paused one.
    /// </summary>
    /// <remarks>
    /// Silent when there is no session, and while blocks are being framed: there is nothing running
    /// to pause, and a key named after one action has nothing to say about a mode it does not apply
    /// to. <see cref="Views.Realtime.RealtimeSessionController.TogglePause"/> is what decides that,
    /// and the bar's own button is the other way in.
    /// </remarks>
    private void OnRealtimePauseHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() => Views.Realtime.RealtimeSessionController.Instance.TogglePause());

    /// <summary>
    /// Brings 取詞翻譯's popup up over whatever the user is reading, unpinned.
    /// </summary>
    /// <remarks>
    /// Not a toggle, unlike the shortcut below it: pressed again it refills the popup with the new
    /// selection rather than dismissing it, because the popup already goes away on its own and the
    /// thing a second press means is "this word now".
    ///
    /// Unpinned, because dismissing itself is what makes this shortcut cheap to press: it is used
    /// on a word mid-sentence, and a popup left behind after every press would be litter the user
    /// has to clear. <see cref="StartQuickLookupFromShell"/> is the door that opens it pinned.
    /// </remarks>
    private void OnQuickLookupHotkeyPressed(object? sender, EventArgs e) =>
        StartQuickLookup(pinned: false);

    /// <summary>
    /// Replaces the selection with its translation, in whatever the user is writing in.
    /// </summary>
    /// <remarks>
    /// Turned away in the same two states as 取詞翻譯 and for the same reasons — a capture in
    /// progress is a gesture of the user's not to be interrupted, and a realtime session composes
    /// the monitor without this application's own layers, so a card created afterwards would be read
    /// back into the subtitles. Silently in both cases: this shortcut is silent whenever it has
    /// nothing to do, and one refusal that talks would be the exception nobody expects.
    /// </remarks>
    private void OnQuickTranslateHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(async () =>
        {
            if (HasActiveSession) return;
            if (RealtimeSessionController.Instance.IsActive) return;

            await Views.QuickTranslate.QuickTranslateFlow.RunAsync();
        });

    /// <summary>
    /// Opens 取詞翻譯 from the shell window's nav rail, pinned.
    /// </summary>
    /// <remarks>
    /// Through <see cref="StartQuickLookup"/> rather than calling <c>SummonAsync</c> itself: the two
    /// states a popup must not appear in are about the screen, not about which control asked, and a
    /// second entry point with its own guards is one that can fall out of step with them. The rail's
    /// own button being disabled during a realtime session is a presentation detail on top of this,
    /// not a replacement for it.
    ///
    /// Pinned, which is the one thing this door does differently. Someone arriving by the shortcut
    /// has already been told what it does; someone who found the feature in the nav rail is meeting
    /// it, and a popup that dismissed itself the moment they looked back at their text would be a
    /// rule they were never told — they would press the button again, and again. The pin is on the
    /// popup's own header for them to turn off once they know.
    ///
    /// The shell stays where it is. Unlike a capture, this puts a popup over the screen rather than
    /// photographing it, so there is nothing to get out of the way of.
    /// </remarks>
    public void StartQuickLookupFromShell() => StartQuickLookup(pinned: true);

    /// <summary>
    /// The one way in to 取詞翻譯, holding the guards both doors have to pass.
    /// </summary>
    /// <remarks>
    /// Turned away in the two states where a window of ours must not appear, and for two different
    /// reasons. During a capture the user is framing or reading a screen of their own and a popup
    /// dropped into it takes the foreground away mid-gesture — the same reason the translation
    /// window stays out, and silent for the same reason. During a realtime session it is the session
    /// that cannot afford it: a monitor capture is composed without this application's own overlays
    /// (#94), a popup created afterwards is not on that list, and a session would end up reading and
    /// translating this window's text back to the user. That one is announced, because the shortcut
    /// is otherwise available everywhere and silence would read as breakage.
    /// </remarks>
    private void StartQuickLookup(bool pinned) =>
        Dispatcher.Invoke(async () =>
        {
            if (HasActiveSession) return;
            if (RefuseWhileRealtimeRuns()) return;

            await Views.QuickLookup.QuickLookupWindow.SummonAsync(pinned);
        });

    /// <summary>
    /// Opens the translation window, and during a realtime session brings its layers to the front
    /// instead — the same two answers the tray icon gives, for the same reasons.
    /// </summary>
    /// <remarks>
    /// It never closes the window: every other way in opens or activates, and a shortcut that also
    /// dismissed would be the one thing in the application whose meaning depends on what is already
    /// on screen.
    ///
    /// Two states it does nothing in, for opposite reasons.
    ///
    /// A realtime session is not one of them. The window is no use during a session, but the
    /// shortcut is the fastest way to a control bar that something else has covered, so it does
    /// what the tray icon does and lifts the layers instead.
    ///
    /// A capture in progress is. The selection layer, the overlay and the toolbar are a single
    /// piece of work with its own controls, and dropping a window into the middle of it takes the
    /// foreground away from a screen the user is in the act of framing or reading. The toolbar has
    /// a button for everything reachable from there, so nothing is lost by staying out. Silently,
    /// unlike the capture shortcut's own refusal: that one announces itself because it is the
    /// feature's main entry point and its silence would read as breakage, while this is a
    /// convenience nothing advertises and a notification would be more intrusive than the miss.
    /// </remarks>
    /// <summary>
    /// Brings the shell up, or puts it away when it is already the window in front.
    /// </summary>
    /// <remarks>
    /// A toggle rather than a summons: this is a global shortcut, so it is pressed without looking
    /// for anything to click, and the way back out should be the same key rather than a trip to the
    /// window's own close button.
    ///
    /// Closed, not hidden — the shell is destroyed on close and rebuilt on the next open, which is
    /// what the tray menu's own close already does. IsActive rather than a foreground-window
    /// check: a global hotkey does not move focus, so the window that was in front when the key
    /// went down is still the active one when this runs.
    /// </remarks>
    private void OnTranslationWindowHotkeyPressed(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            if (HasActiveSession) return;

            if (ShellWindow.Current is { IsActive: true } shell)
            {
                shell.Close();
                return;
            }

            OnTrayLeftClick();
        });

    /// <summary>
    /// Turns a capture away while a realtime session owns the screen, and says why.
    /// </summary>
    /// <remarks>
    /// The two features share one OCR engine and one bounded pool of inference slots, and a
    /// realtime session uses them continuously. Running a capture alongside it would have them
    /// competing for those slots, and — if the two were set to different source languages — swapping
    /// the loaded model back and forth between every read. See OcrEngineConcurrencyTests for what
    /// that measured out as before this rule existed.
    ///
    /// Told rather than ignored, because the ways in that still come here are deliberate presses on
    /// a control that looks available — the shell's own button. The capture shortcut no longer goes
    /// through this at all: it has its own answer during a session, see <see cref="OnHotkeyPressed"/>.
    /// </remarks>
    private bool RefuseWhileRealtimeRuns()
    {
        if (!Views.Realtime.RealtimeSessionController.Instance.IsActive) return false;

        ShowTrayNotification(
            LocalizationService.Get("S.Main.RealtimeRunningTitle"),
            LocalizationService.Get("S.Main.RealtimeRunningBody"));
        return true;
    }

    /// <summary>
    /// Starts a capture from the shell window's nav rail. The shell has to leave the screen first:
    /// <see cref="RunCaptureSessionAsync"/> grabs the desktop with a synchronous CopyFromScreen, so
    /// a still-visible shell ends up baked into the very image the user is about to select from.
    /// <see cref="WindowScreenPresence.HideAndWaitForScreen"/> is what makes that ordering real —
    /// it does not return until the compositor has presented a frame without the window.
    /// </summary>
    public void StartCaptureFromShell(Window shell)
    {
        // The rail's button is disabled while a session runs, so this is the guard rather than the
        // notice — but it is the one that actually enforces the rule, and a disabled button is a
        // presentation detail that a future layout change could drop.
        if (RefuseWhileRealtimeRuns()) return;

        if (HasActiveSession)
        {
            CloseAll();
            return;
        }

        _shellHiddenForCapture = shell;
        WindowScreenPresence.HideAndWaitForScreen(shell);

        // Started inline, not queued. This used to go through the dispatcher at Background
        // priority, which sits *below* Input: hiding the shell hands activation to another window
        // and the user is already moving the mouse toward what they want to select, so the queued
        // capture kept being overtaken by that input and started whenever the stream happened to
        // pause. There is nothing left to wait for either — HideAndWaitForScreen has already
        // cleared the screen, and the button whose press feedback the deferral used to protect is
        // no longer visible.
        _ = RunShellCaptureAsync();
    }

    private async Task RunShellCaptureAsync()
    {
        try
        {
            await RunCaptureSessionAsync("shell-button");
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception would otherwise be silent.
            Log.Error(ex, "Capture started from the shell window failed");
        }
        finally
        {
            // A live session owns the screen, and the shell stays away until it ends — CloseAll and
            // the overlay's own teardown both restore it. No session here means the user cancelled
            // during selection, and a window that vanished because they pressed a button inside it
            // must come straight back.
            if (!HasActiveSession)
                RestoreShellAfterCapture();
        }
    }

    // Set for the lifetime of a shell-initiated capture. Null for hotkey captures, which never hid
    // anything and so have nothing to put back.
    private Window? _shellHiddenForCapture;

    private void RestoreShellAfterCapture()
    {
        var shell = _shellHiddenForCapture;
        _shellHiddenForCapture = null;
        // Already visible when the toolbar's 開啟翻譯視窗 carried the result into it, which shows
        // the shell itself before tearing the session down.
        if (shell is null || shell.IsVisible) return;

        try
        {
            shell.Show();
            shell.Activate();
        }
        catch (Exception ex)
        {
            // Racing an app shutdown that already destroyed the window. Nothing left to restore,
            // and it must not take the session teardown down with it.
            Log.Warn(ex, "Could not restore the shell window after a capture");
        }
    }

    private async Task RunCaptureSessionAsync(string origin = "hotkey")
    {
        if (HasActiveSession)
        {
            CloseAll();
            return;
        }

        Bitmap screenshot;
        System.Drawing.Rectangle screenBounds;
        try
        {
            screenBounds = ScreenGeometry.VirtualDesktopBounds();
            screenshot = new Bitmap(screenBounds.Width, screenBounds.Height);
            using var g = Graphics.FromImage(screenshot);
            g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0,
                screenBounds.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screen capture failed — aborting selection");
            return;
        }

        // After the capture, never before: with the level enabled this queries every monitor and
        // writes a multi-line record, which on the way in would delay the freeze the user just
        // asked for. The values it reports are the same either side of the capture.
        DisplayDiagnostics.LogSnapshot(origin);

        // Anything that escapes from here would leave the full-screen dim window on top of the
        // desktop with no owner left to close it, so the whole session setup is guarded.
        try
        {
            Log.Info("Capture session starting, origin={Origin}, bounds={Bounds}", origin, screenBounds);
            var captureWindow = new ScreenCaptureWindow(screenshot, screenBounds);
            _captureWindow = captureWindow;

            // The box stays adjustable until translation starts, so the toolbar anchored to it has to
            // keep up. Only the toolbar: the overlay carries no bubbles yet at this stage, and the
            // crop is re-read from the window at translate time.
            captureWindow.SelectionAdjusted += (_, selection) =>
            {
                _lastSelPhysLeft   = selection.Left;
                _lastSelPhysTop    = selection.Top;
                _lastSelPhysWidth  = selection.Width;
                _lastSelPhysHeight = selection.Height;
                _toolbarWindow?.FollowSelection(selection);
                // OCR geometry belongs to the old crop until the user recognises this one.
                _lastOcrBlocks = [];
                _overlayWindow?.ShowOcrDebug([], selection.Left, selection.Top);

                // The marks stay where they were drawn; what moves is the window onto them. See
                // OverlayWindow.SetAnnotationBounds for why that is the way round it is.
                _overlayWindow?.SetAnnotationBounds(
                    selection.Left, selection.Top, selection.Width, selection.Height);

                // 標記 cannot be on while the box is being dragged — it takes the box away for
                // exactly that reason — but the toolbar this hangs from moves with the box, and a
                // panel that stayed put would be pointing at nothing.
                PlaceAnnotationPanel();
            };

            captureWindow.Show();

            // Diagnostic: where the dim window physically landed and at what scale, versus the
            // screenBounds the screenshot was taken with. A difference between the two is the
            // misalignment users report, and Stretch="Fill" makes it invisible otherwise.
            DisplayDiagnostics.LogSnapshot("capture-window-shown", captureWindow);

            // After Show, not before: everything on the path between creating the window and
            // presenting it delays the first frame, during which the window's black background
            // is what the user sees. The hook is still installed within the same dispatcher
            // pass, so Esc is live long before anyone can press it.
            // Release any previous one first — this hook swallows Esc process-wide, so an
            // orphaned instance would break Esc across the entire desktop, which is far worse
            // than the stuck overlay it exists to prevent.
            DisposeSessionHooks();
            _escapeHook = GlobalEscapeHook.Install(CloseAll);

            // Paired with the Esc hook and for the same reason: these windows cover the screen, so
            // both ways out of a session that has gone wrong have to be set up before it can.
            _recoveryYield = SystemRecoveryYield.Install(ApplyCaptureTopmost);

            CancelSession();
            _sessionCts = new CancellationTokenSource();

            bool selected = await captureWindow.WaitForSelectionAsync();
            if (!selected || !captureWindow.HasSelection)
            {
                // Also reached when the capture window cancelled itself (its own Esc fallback),
                // which never goes through CloseAll — so the session is torn down here too.
                DisposeSessionHooks();
                CancelSession();
                captureWindow.Close();
                _captureWindow = null;
                screenshot.Dispose();
                return;
            }

            var settings      = SettingsService.Instance.Current;
            var selection     = captureWindow.Selection;
            EnterOverlayState(captureWindow, selection, [], [], settings.SourceLanguage, hasTranslated: false);

            // Fire in the same pass that built the overlay, before it paints: the toolbar's first
            // frame already reads "翻譯中..." and the overlay's first frame already shows "辨識中".
            // Deferring this to a later dispatcher pass only adds a visible gap where the toolbar
            // sits idle after the selection is done.
            if (settings.AutoTranslateAfterSelection)
                _toolbarWindow?.RequestTranslate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture session setup failed — tearing down overlay windows");
            CloseAll();
            screenshot.Dispose();
        }
    }

    private void EnterOverlayState(
        ScreenCaptureWindow captureWindow,
        System.Windows.Rect selection,
        List<TranslatedBlock> blocks,
        List<OcrTextBlock> ocrBlocks,
        string srcLang,
        bool hasTranslated)
    {
        _selectionSessionId++;
        _lastOcrBlocks     = ocrBlocks;
        _lastTranslatedBlocks = blocks;
        _lastColoredBlocks = blocks;
        _lastSelPhysLeft   = selection.Left;
        _lastSelPhysTop    = selection.Top;
        _lastSelPhysWidth  = selection.Width;
        _lastSelPhysHeight = selection.Height;
        _lastVerticalText  = false;

        _annotationTool      = AnnotationPanelWindow.DefaultTool;
        _annotationColor     = AnnotationPanelWindow.DefaultColor;
        _annotationThickness = AnnotationPanelWindow.DefaultThickness;
        _annotationOpacity   = AnnotationPanelWindow.DefaultOpacity;

        var settings = SettingsService.Instance.Current;
        ShowOverlay(
            blocks,
            ocrBlocks,
            selection.Left,
            selection.Top,
            selection.Width,
            selection.Height,
            srcLang,
            settings.TargetLanguage,
            verticalText: false);

        _overlayWindow?.SetAnnotationBounds(
            selection.Left, selection.Top, selection.Width, selection.Height);

        var toolbar  = new ToolbarWindow(
            selection.Left, selection.Top, selection.Width, selection.Height,
            srcLang, settings.TargetLanguage);
        toolbar.Owner = captureWindow;
        toolbar.TranslateRequested      += OnTranslateRequested;
        toolbar.OpenWindowRequested     += OnOpenWindowRequested;
        toolbar.CopyTextRequested       += OnCopyTextRequested;
        toolbar.CopyScreenshotRequested += OnCopyScreenshotRequested;
        toolbar.CloseAllRequested       += (_, _) => CloseAll();
        toolbar.BubblesVisibilityChanged += (_, visible) => _overlayWindow?.SetBubblesVisible(visible);
        toolbar.SpeakToggleRequested    += OnSpeakToggleRequested;
        toolbar.SpeakStopRequested      += (_, _) => _tts.Stop();
        toolbar.AnnotateModeChanged     += OnAnnotateModeChanged;
        _toolbarWindow = toolbar;
        toolbar.SetTranslationState(hasTranslated);
        toolbar.SetToggleEnabled(blocks.Count > 0);
        toolbar.SetSpeakableText(SourceTextForSpeech().Length > 0);
        toolbar.Show();

        ApplyCaptureTopmost();
    }

    // On re-translate: update the existing overlay in-place to avoid z-order fights with
    // ScreenCaptureWindow (both Topmost — close+reopen loses the z-position race).
    // On first call: create a new overlay and wire its Closed handler.
    private void ShowOverlay(
        List<TranslatedBlock> blocks,
        IReadOnlyList<OcrTextBlock> ocrBlocks,
        double selPhysLeft,
        double selPhysTop,
        double selPhysWidth,
        double selPhysHeight,
        string sourceLang,
        string targetLang,
        bool verticalText)
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.UpdateBlocks(
                blocks,
                ocrBlocks,
                selPhysLeft,
                selPhysTop,
                selPhysWidth,
                selPhysHeight,
                sourceLang,
                targetLang,
                verticalText);
            return;
        }

        _overlayWindow = new OverlayWindow(
            blocks,
            ocrBlocks,
            selPhysLeft,
            selPhysTop,
            selPhysWidth,
            selPhysHeight,
            sourceLang,
            targetLang,
            verticalText);
        if (_captureWindow != null)
            _overlayWindow.Owner = _captureWindow;
        // This runs when the overlay closes on its own (Esc via the keyboard hook). Same fault
        // tolerance as CloseAll: a throwing toolbar Close() must not strand the capture window's
        // full-screen dim layer, which is the one window the user cannot get rid of.
        _overlayClosedHandler = (_, _) =>
        {
            _selectionSessionId++;
            DisposeSessionHooks();
            CancelSession();
            ToastWindow.Dismiss();
            CloseAnnotationPanel();
            CloseWindow(_toolbarWindow, w => w.Close(), nameof(ToolbarWindow));
            _toolbarWindow = null;
            CloseWindow(_captureWindow, w => w.Close(), nameof(ScreenCaptureWindow));
            _captureWindow = null;
            _overlayWindow = null;
            RestoreShellAfterCapture();
        };
        _overlayWindow.Closed += _overlayClosedHandler;
        _overlayWindow.AnnotationsChanged += (_, _) =>
            _annotationPanel?.SetHistoryState(
                _overlayWindow?.CanUndoAnnotation == true, _overlayWindow?.CanRedoAnnotation == true);

        // The marks are drawn by the overlay and shown by the capture window — see
        // OverlayWindow.InkLayer for why they are not shown where they are drawn.
        _overlayWindow.InkLayerChanged += (_, _) =>
        {
            if (_overlayWindow is { } overlay)
                _captureWindow?.ShowOverlayContent(overlay.CaptureHostedLayers);
        };

        _overlayWindow.Show();
    }

    private async void OnTranslateRequested(object? sender, TranslateRequest req)
    {
        var requestToolbar = sender as ToolbarWindow;
        var requestCaptureWindow = _captureWindow;
        var requestSessionId = _selectionSessionId;
        // Captured now: _sessionCts is replaced by the next capture, and this request must keep
        // observing the token belonging to the session it was started for.
        var cancellationToken = _sessionCts?.Token ?? CancellationToken.None;
        var settings = SettingsService.Instance.Current;
        var selRect  = requestCaptureWindow?.Selection
            ?? new System.Windows.Rect(_lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowBalloon(
                LocalizationService.Get("S.Main.MissingApiKeyTitle"),
                LocalizationService.Get("S.Main.MissingApiKeyBody"), selRect);
            return;
        }

        requestToolbar?.SetBusy(true);

        // Whether the frame may still be handed back: this run is what locked it, and recognition —
        // the one stage a redrawn box would fix — has not got past finding text yet. Cleared the
        // moment there is text to translate, because from there the box is settled for good: the
        // bubbles are laid out against it, so it must not move under them even if the engine fails.
        bool frameStillRestorable = false;

        try
        {
            if (requestCaptureWindow == null || !requestCaptureWindow.PrepareForProcessing(out frameStillRestorable))
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.RecogniseFailedTitle"),
                    LocalizationService.Get("S.Main.NoImageBody"), selRect);
                return;
            }

            _lastSelPhysLeft   = requestCaptureWindow.Selection.Left;
            _lastSelPhysTop    = requestCaptureWindow.Selection.Top;
            _lastSelPhysWidth  = requestCaptureWindow.Selection.Width;
            _lastSelPhysHeight = requestCaptureWindow.Selection.Height;
            selRect = requestCaptureWindow.Selection;

            // The capture window owns CroppedBitmap and disposes it the instant it closes (Esc,
            // CloseAll, re-capture). OCR runs on a thread pool thread and the colour sampling below
            // happens after a second await, so both would read freed GDI+ memory if they used that
            // instance directly. Take our own copy up front — cloning here is safe because we are
            // still on the UI thread with no await since PrepareForTranslation — and let its
            // lifetime match this request instead of the window's.
            using var workBitmap = ClonePixels(requestCaptureWindow.CroppedBitmap!);

            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                LocalizationService.Get("S.Main.Recognising"));

            var recognizedBlocks = await AppServices.Ocr.RecognizeAsync(
                workBitmap,
                req.SourceLang,
                cancellationToken,
                req.IsVerticalText,
                CaptureLayoutPolicy.ForApplication(req.LayoutMode));
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            _lastOcrBlocks = recognizedBlocks;
            if (_lastOcrBlocks.Count == 0)
            {
                requestToolbar?.SetTranslationState(false);
                if (frameStillRestorable) requestCaptureWindow.RestoreSelectionEditing();
                ShowBalloon(
                    LocalizationService.Get("S.Main.NoTextTitle"),
                    LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
                return;
            }

            frameStillRestorable = false;

            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                LocalizationService.Get("S.Main.Translating"));

            // Recognition is available even while translation is still pending.
            _overlayWindow?.ShowOcrDebug(_lastOcrBlocks, _lastSelPhysLeft, _lastSelPhysTop);

            var (translated, _) = await AppServices.Translation.TranslateAsync(
                _lastOcrBlocks, req.SourceLang, req.TargetLang, settings.ApiKey,
                cancellationToken: cancellationToken);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            _lastTranslatedBlocks = [.. translated];

            // Between the translation and the colour sampling, and it has to be exactly here.
            // Placement is what decides the boxes the overlay will draw, and a colour sampled from
            // a box that is about to be cut into four is the average of four backgrounds — the
            // group box of a wrapped paragraph spans whatever the page put between its lines.
            var placed = OverlayPlacement.Place(translated, CaptureLayoutPolicy.ForApplication(req.LayoutMode), req.IsVerticalText);

            // Re-use sampled colours from the previous overlay, but only where the block at that
            // index is recognisably the same block. Re-translating after switching capture mode,
            // re-drawing the selection, or simply letting the detector wobble all change how many
            // boxes there are and where they sit, and a colour re-used across that lands a bubble
            // in the colours of whatever used to be at its index — quietly, with nothing on screen
            // to say the picture underneath was never looked at. Re-sampling costs a pass over the
            // crop, next to nothing beside the OCR and the translation that just ran.
            var previousVerticalText = _lastVerticalText;
            var coloredTranslated = placed
                .Select((b, i) =>
                {
                    if (SampledColorReuse.CanReuse(
                            _lastColoredBlocks, i, b, req.IsVerticalText, previousVerticalText))
                    {
                        return b with
                        {
                            BackgroundColor = _lastColoredBlocks[i].BackgroundColor,
                            TextColor       = _lastColoredBlocks[i].TextColor
                        };
                    }

                    var (bg, fg) = SourceTextColorSampler.ForCaptureOverlay(
                        workBitmap, b.Bounds, b.SourceLineBounds, req.IsVerticalText);
                    return b with { BackgroundColor = bg, TextColor = fg };
                })
                .ToList();

            _lastColoredBlocks = coloredTranslated;
            _lastVerticalText = req.IsVerticalText;
            ShowOverlay(
                coloredTranslated,
                _lastOcrBlocks,
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                req.SourceLang,
                req.TargetLang,
                req.IsVerticalText);
            requestToolbar?.SetTranslationState(true);
            requestToolbar?.SetToggleEnabled(coloredTranslated.Count > 0);
            requestToolbar?.SetEngineBadge(AppServices.Translation.LastEngineUsage);
        }
        // The session was torn down (Esc, re-capture, toolbar close) while this was in flight.
        // Expected and user-initiated — it must stay completely silent, with no error toast.
        catch (OperationCanceledException)
        {
            Log.Debug("Translate request abandoned — capture session ended");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("sequence contains no elements", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug(ex, "OCR produced no text blocks");
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            requestToolbar?.SetTranslationState(false);
            if (frameStillRestorable) requestCaptureWindow?.RestoreSelectionEditing();
            ShowBalloon(
                LocalizationService.Get("S.Main.NoTextTitle"),
                LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translate request failed (src={Src}, tgt={Tgt}, selection={Sel})",
                req.SourceLang, req.TargetLang, selRect);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            // On failure, restore old bubbles so the overlay isn't left blank
            _overlayWindow?.UpdateBlocks(
                _lastColoredBlocks,
                _lastOcrBlocks,
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                req.SourceLang,
                req.TargetLang,
                _lastVerticalText);
            requestToolbar?.SetTranslationState(_lastColoredBlocks.Count > 0);
            requestToolbar?.SetToggleEnabled(_lastColoredBlocks.Count > 0);
            ShowBalloon(
                LocalizationService.Get("S.Main.TranslateFailedTitle"),
                LocalizationService.Format("S.Main.TranslateFailedBody", ex.Message), selRect);
        }
        finally
        {
            if (IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
            {
                _overlayWindow?.RestoreIdle(_lastColoredBlocks.Count > 0);

                // Tied to a translation being on screen, not merely to recognition having run: the
                // 複製文字 button also recognises, and the box stays editable after it, so text on
                // its own is no promise that what would be read still matches the selection. A
                // re-translate that fails keeps the button, because its bubbles are still up.
                requestToolbar?.SetSpeakableText(
                    _lastColoredBlocks.Count > 0 && SourceTextForSpeech().Length > 0);
                requestToolbar?.SetBusy(false);
            }
        }
    }

    // Deep copy of a capture crop, owned by the caller. Clone(Rectangle, PixelFormat) allocates a
    // fresh GDI+ bitmap and copies the pixels, so the copy stays valid after the source is disposed.
    private static Bitmap ClonePixels(Bitmap source) =>
        source.Clone(new Rectangle(0, 0, source.Width, source.Height), source.PixelFormat);

    private async void OnCopyTextRequested(object? sender, CopyTextRequest req)
    {
        var requestToolbar = sender as ToolbarWindow;
        var requestCaptureWindow = _captureWindow;
        var requestSessionId = _selectionSessionId;
        var cancellationToken = _sessionCts?.Token ?? CancellationToken.None;
        var selRect = CurrentSelectionRect();

        if (req.Kind != CopyTextKind.RecognizeSource)
        {
            try
            {
                var cachedText = req.Kind == CopyTextKind.Translation
                    ? JoinWithoutLineBreaks(_lastTranslatedBlocks.Select(block => block.TranslatedText))
                    : JoinWithoutLineBreaks(_lastOcrBlocks.Select(block => block.Text));
                CopyCaptureText(cachedText, req.Kind, selRect);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Copy cached capture text failed (kind={Kind})", req.Kind);
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopyFailedTitle"),
                    LocalizationService.Get("S.Main.CopyTextFailedBody"), selRect);
            }
            return;
        }

        requestToolbar?.SetRecognitionBusy(true);

        // Copying text needs the region held still while it is read, nothing more, so the frame goes
        // back to the user afterwards — but only if this is what locked it. A frame already locked
        // by a translation belongs to that translation and stays put.
        bool frameLockedByThisCopy = false;

        try
        {
            if (requestCaptureWindow == null || !requestCaptureWindow.PrepareForProcessing(out frameLockedByThisCopy))
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.RecogniseFailedTitle"),
                    LocalizationService.Get("S.Main.NoImageBody"), selRect);
                return;
            }

            _lastSelPhysLeft   = requestCaptureWindow.Selection.Left;
            _lastSelPhysTop    = requestCaptureWindow.Selection.Top;
            _lastSelPhysWidth  = requestCaptureWindow.Selection.Width;
            _lastSelPhysHeight = requestCaptureWindow.Selection.Height;
            selRect = requestCaptureWindow.Selection;

            using var workBitmap = ClonePixels(requestCaptureWindow.CroppedBitmap!);
            _overlayWindow?.ShowProcessing(
                _lastSelPhysLeft,
                _lastSelPhysTop,
                _lastSelPhysWidth,
                _lastSelPhysHeight,
                LocalizationService.Get("S.Main.Recognising"));

            var recognizedBlocks = await AppServices.Ocr.RecognizeAsync(
                workBitmap,
                req.SourceLang,
                cancellationToken,
                req.IsVerticalText,
                CaptureLayoutPolicy.ForApplication(req.LayoutMode));
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            _lastOcrBlocks = recognizedBlocks;
            _overlayWindow?.ShowOcrDebug(_lastOcrBlocks, _lastSelPhysLeft, _lastSelPhysTop);
            if (_lastOcrBlocks.Count == 0)
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.NoTextTitle"),
                    LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
                return;
            }

            CopyCaptureText(
                JoinWithoutLineBreaks(_lastOcrBlocks.Select(block => block.Text)),
                CopyTextKind.Source,
                selRect);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Copy text request abandoned — capture session ended");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy text request failed (src={Src}, selection={Sel})", req.SourceLang, selRect);
            if (!IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
                return;

            ShowBalloon(
                LocalizationService.Get("S.Main.CopyFailedTitle"),
                LocalizationService.Get("S.Main.CopyTextFailedBody"), selRect);
        }
        finally
        {
            if (IsCurrentSelectionSession(requestSessionId, requestToolbar, requestCaptureWindow))
            {
                _overlayWindow?.RestoreIdle(_lastColoredBlocks.Count > 0);
                if (frameLockedByThisCopy) requestCaptureWindow?.RestoreSelectionEditing();
                requestToolbar?.SetRecognitionBusy(false);
            }
        }
    }

    /// <summary>
    /// Puts one side of the capture on the clipboard and says which side it was.
    /// </summary>
    /// <param name="text">The recognised original or the translation, already joined into one line</param>
    /// <param name="kind">Which of the two was copied, so the confirmation names the right one</param>
    /// <param name="selRect">Selection the confirmation is positioned against</param>
    private static void CopyCaptureText(string text, CopyTextKind kind, System.Windows.Rect selRect)
    {
        var payload = text.Trim();

        // Blocks made only of whitespace get this far: they count as recognised text but leave
        // nothing to paste, and a "已複製" over an untouched clipboard is the one thing the user
        // cannot check without switching away.
        if (payload.Length == 0)
        {
            ShowBalloon(
                LocalizationService.Get("S.Main.NoTextTitle"),
                LocalizationService.Get("S.Main.NoTextBody"), selRect, ToastKind.Info);
            return;
        }

        System.Windows.Clipboard.SetText(payload);
        ShowBalloon(
            LocalizationService.Get("S.Main.CopiedTitle"),
            LocalizationService.Get(
                kind == CopyTextKind.Translation
                    ? "S.Main.TranslationCopiedBody"
                    : "S.Main.TextCopiedBody"),
            selRect,
            ToastKind.Success);
    }

    // Builds the "copy screenshot" image by compositing what the user actually sees in the
    // selection — WITHOUT OverTranslate's own editing chrome. The background is the clean original
    // capture (so the selection border/handles are never included), and the translation bubbles
    // (when present) are rendered on top. The loading indicator is excluded because the bubble
    // layers are empty while processing, so RenderBubblesForSelection returns null then.
    private void OnCopyScreenshotRequested(object? sender, EventArgs e)
    {
        var selRect = new System.Windows.Rect(
            _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);
        try
        {
            var background = _captureWindow?.CreateSelectionImage();
            if (background is null)
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopyFailedTitle"),
                    LocalizationService.Get("S.Main.NoImageBody"), selRect);
                return;
            }

            int w = background.PixelWidth;
            int h = background.PixelHeight;

            var bubbles = _overlayWindow?.RenderOverlayForSelection(
                _lastSelPhysLeft, _lastSelPhysTop, w, h);

            System.Windows.Media.Imaging.BitmapSource result = background;
            if (bubbles is not null)
            {
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var bounds = new System.Windows.Rect(0, 0, w, h);
                    dc.DrawImage(background, bounds);
                    dc.DrawImage(bubbles, bounds);
                }
                var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                composed.Render(dv);
                composed.Freeze();
                result = composed;
            }

            System.Windows.Clipboard.SetImage(result);

            var settings = SettingsService.Instance.Current;
            if (!settings.SaveScreenshotToDisk)
            {
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedTitle"),
                    LocalizationService.Get("S.Main.CopiedBody"), selRect, ToastKind.Success);
                return;
            }

            // Saving is a bonus on top of the copy — a failed write must not read as a failed copy.
            try
            {
                var savedPath = ScreenshotSaveService.Save(result, settings.ScreenshotSavePath);
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedTitle"),
                    LocalizationService.Format("S.Main.CopiedAndSavedBody", savedPath), selRect, ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Screenshot copied to clipboard but saving to disk failed");
                ShowBalloon(
                    LocalizationService.Get("S.Main.CopiedSaveFailedTitle"),
                    LocalizationService.Format("S.Main.CopiedSaveFailedBody", ex.Message), selRect);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy screenshot failed");
            ShowBalloon(
                LocalizationService.Get("S.Main.CopyFailedTitle"),
                LocalizationService.Format("S.Main.CopyFailedBody", ex.Message), selRect);
        }
    }

    /// <summary>
    /// Reads the recognised source text aloud, or stops it if it is already being read.
    /// </summary>
    /// <remarks>
    /// The whole recognised text in one go, joined exactly the way the translation window is opened
    /// with it. The blocks are how the picture was cut up for recognition — one per line of a
    /// subtitle, one per label on a form — not how the sentence was written, so reading them one at
    /// a time would deliver a paragraph as a list of fragments with a pause after each.
    ///
    /// The source text rather than the translation: this is for hearing how the thing on screen is
    /// said, which is also why it needs a real source language and why the button is switched off
    /// until it has one. See <c>ToolbarWindow.RenderSpeakButton</c>.
    /// </remarks>
    private async void OnSpeakToggleRequested(object? sender, EventArgs e)
    {
        if (_tts.IsActive) { _tts.Stop(); return; }
        if (_toolbarWindow is not { } toolbar) return;

        var text = SourceTextForSpeech();
        if (text.Length == 0) return;

        var lang = toolbar.CurrentSourceLang;
        // Belt and braces: the button is already disabled on 自動, and a voice picked for a language
        // nobody chose reads English aloud in Chinese.
        if (Models.LanguageData.IsAutomaticSource(lang)) return;

        // Shown on the press rather than waited for: fetching the audio takes a moment, and the one
        // thing the user needs immediately is the way to stop it.
        toolbar.SetSpeaking(true);
        try
        {
            await _tts.SpeakAsync(text, lang);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Toolbar text-to-speech failed");
            ShowBalloon(
                LocalizationService.Get("S.Main.SpeakFailedTitle"),
                LocalizationService.Format("S.Main.SpeakFailedBody", ex.Message),
                CurrentSelectionRect());
        }
    }

    private string SourceTextForSpeech() =>
        JoinWithoutLineBreaks(_lastOcrBlocks.Select(b => b.Text)).Trim();

    private System.Windows.Rect CurrentSelectionRect() => new(
        _lastSelPhysLeft, _lastSelPhysTop, _lastSelPhysWidth, _lastSelPhysHeight);

    private void OnOpenWindowRequested(object? sender, EventArgs e)
    {
        var srcText = JoinWithoutLineBreaks(_lastOcrBlocks.Select(b => b.Text));
        var tgtText = JoinWithoutLineBreaks(_lastTranslatedBlocks.Select(b => b.TranslatedText));
        var srcLang = _toolbarWindow?.CurrentSourceLang ?? SettingsService.Instance.Current.SourceLanguage;
        var tgtLang = _toolbarWindow?.CurrentTargetLang ?? SettingsService.Instance.Current.TargetLanguage;

        var shell = ShellWindow.ShowOrActivate(ShellPage.Translation);
        shell.TranslationPage.SetContent(srcText, tgtText, srcLang, tgtLang);

        CloseAll(); // close overlay, dim background, and toolbar
    }

    private static string JoinWithoutLineBreaks(IEnumerable<string> parts) =>
        string.Join(" ", parts.Select(text => text.Replace('\r', ' ').Replace('\n', ' ')));

    private void CloseAll()
    {
        // Paired with "Capture session starting": between them they show whether a session the user
        // reports as stuck ever actually ended.
        Log.Info("Tearing down capture session (overlay={Overlay}, toolbar={Toolbar}, capture={Capture})",
            _overlayWindow != null, _toolbarWindow != null, _captureWindow != null);
        _selectionSessionId++;
        DisposeSessionHooks();
        CancelSession();

        // A voice reading a selection that is no longer on screen has nothing left to be about, and
        // nothing would be left to stop it: the button that does is going with the toolbar.
        _tts.Stop();

        // A toast is positioned against the selection it reported on. Once that selection is gone it
        // has nothing left to point at, so it goes with the session rather than lingering on an
        // empty desktop until its own timer runs out. The close button on the toast is what covers
        // the reader who wants it gone sooner.
        ToastWindow.Dismiss();

        // Detach handler before closing so we drive the teardown order ourselves
        if (_overlayWindow != null && _overlayClosedHandler != null)
            _overlayWindow.Closed -= _overlayClosedHandler;

        // Each window is torn down independently: the capture window paints a full-screen dim layer
        // that is click-through once processing starts, so if an earlier Close() threw we must still
        // reach it. Clearing the field first also guarantees the state never claims a window that is
        // actually gone, which would make the next hotkey press a no-op teardown.
        // Before the overlay: closing the panel gives the pen back and takes the overlay out of
        // click-through mode, and doing that to a window that has already gone is a needless throw
        // on the one teardown path that must never leave a window behind.
        CloseAnnotationPanel();

        CloseWindow(_overlayWindow, w => w.CloseOverlay(), nameof(OverlayWindow));
        _overlayWindow = null;

        CloseWindow(_toolbarWindow, w => w.Close(), nameof(ToolbarWindow));
        _toolbarWindow = null;

        CloseWindow(_captureWindow, w => w.Close(), nameof(ScreenCaptureWindow));
        _captureWindow = null;

        // Last, so a shell hidden for this capture comes back only once the screen is clear of the
        // dim layer and overlay it would otherwise be raised behind.
        RestoreShellAfterCapture();
    }

    /// <summary>
    /// 標記 was switched on or off on the capture toolbar.
    /// </summary>
    /// <remarks>
    /// Three things move together and none of them makes sense without the others: the panel of
    /// tools appears, the overlay takes the pointer so a drag inside the box becomes a stroke, and
    /// the box stops being draggable because that same drag used to move it. Switching off puts all
    /// three back. The marks are not part of it — they stay on screen either way, which is what
    /// makes this a mode for editing them rather than a mode for having them.
    /// </remarks>
    private void OnAnnotateModeChanged(object? sender, bool annotating)
    {
        if (!annotating || _overlayWindow is null || _toolbarWindow is null)
        {
            CloseAnnotationPanel();
            return;
        }

        // Never two panels. Nothing reaches here twice today — the button is a toggle and the mode
        // is switched off before anything reopens it — but a second one would be an orphan window
        // with no way back to it. Only the window is closed, deliberately: CloseAnnotationPanel also
        // unchecks the button, and the button is the thing that has just been pressed.
        CloseWindow(_annotationPanel, w => w.Close(), nameof(AnnotationPanelWindow));
        _annotationPanel = null;

        var panel = new AnnotationPanelWindow(
            _annotationTool, _annotationColor, _annotationThickness, _annotationOpacity);
        if (_captureWindow != null) panel.Owner = _captureWindow;
        panel.SettingsChanged += (_, _) => ApplyAnnotationSettings();
        panel.UndoRequested   += (_, _) => _overlayWindow?.UndoAnnotation();
        panel.RedoRequested   += (_, _) => _overlayWindow?.RedoAnnotation();
        _annotationPanel = panel;

        _captureWindow?.SetAnnotationHold(true);
        _overlayWindow.BeginAnnotating(panel.Tool, panel.InkColor, panel.Thickness, panel.InkOpacity);

        // Ctrl+Z and Ctrl+Y, for as long as the panel is up. Nothing in this session takes the
        // keyboard focus, so the two buttons on the panel are otherwise the only way to reach these.
        _annotationKeys?.Dispose();
        _annotationKeys = AnnotationShortcutHook.Install(
            () => _overlayWindow?.UndoAnnotation(),
            () => _overlayWindow?.RedoAnnotation());

        panel.Show();
        PlaceAnnotationPanel();
        RaiseToolbarsAboveInk();
        panel.SetHistoryState(_overlayWindow.CanUndoAnnotation, _overlayWindow.CanRedoAnnotation);
    }

    /// <summary>
    /// Pushes what the panel now says onto the pen, and holds it for the rest of this capture.
    /// </summary>
    private void ApplyAnnotationSettings()
    {
        if (_annotationPanel is null) return;

        _annotationTool      = _annotationPanel.Tool;
        _annotationColor     = _annotationPanel.InkColor;
        _annotationThickness = _annotationPanel.ThicknessFraction;
        _annotationOpacity   = _annotationPanel.OpacityFraction;

        _overlayWindow?.SetAnnotationTool(_annotationPanel.Tool);
        _overlayWindow?.SetAnnotationColor(_annotationPanel.InkColor);
        _overlayWindow?.SetAnnotationThickness(_annotationPanel.Thickness);
        _overlayWindow?.SetAnnotationOpacity(_annotationPanel.InkOpacity);

        // Deliberately not placed again. 螢光筆 carries a control the other two do not, so the panel
        // is wider in that mode — and left alone it simply grows to the right, because the window
        // keeps its left edge and sizes to its content. Re-centring it would be the tidier result on
        // paper and much the worse one in use: the tool buttons are on the left, the user has just
        // pressed one, and re-centring pulls the whole row out from under the pointer. The row they
        // are working in stays put; the panel gets longer beside it.
        //
        // Off the right of the monitor is allowed rather than nudged back on, for the same reason:
        // a panel that shifted left only when it was near an edge would move for reasons the user
        // cannot see. Centred again on the next open — see OnAnnotateModeChanged.
    }

    /// <summary>
    /// Puts both toolbars in front of the ink layer.
    /// </summary>
    /// <remarks>
    /// Said rather than inherited from the order the windows happened to be created in. While 標記
    /// is on, the ink surface covers the whole selection and the toolbar is placed inside it
    /// whenever there is no room outside — so anything that lifted the ink over the buttons would
    /// leave the user holding a pen and no way to put it down. In front, the buttons keep their own
    /// pointer and their own cursor, and a stroke dragged across them carries on underneath because
    /// the ink surface holds the mouse capture for the length of the drag.
    /// </remarks>
    private void RaiseToolbarsAboveInk()
    {
        // Not while the session has stepped aside for Task Manager. Coming forward here would put
        // the two toolbars back over the window everything else just made room for, and 標記 can be
        // switched on while the layers are already behind it. Placing them is the recovery watch's
        // job in that state, and it does the whole session in one go.
        if (_recoveryYield?.HasYielded == true)
        {
            ApplyCaptureTopmost();
            return;
        }

        if (_toolbarWindow is { } toolbar) AlwaysOnTop.Reassert(toolbar);
        if (_annotationPanel is { } panel) AlwaysOnTop.Reassert(panel);
    }

    private void PlaceAnnotationPanel()
    {
        if (_annotationPanel is null || _toolbarWindow is null) return;
        var (visible, scale) = _toolbarWindow.VisiblePhysicalBounds();
        _annotationPanel.PlaceNear(visible, scale);
    }

    /// <summary>
    /// Puts the panel away and hands the box and the pointer back.
    /// </summary>
    /// <remarks>
    /// Safe to call when 標記 was never on, and called from every teardown path for that reason. The
    /// toolbar is told last and told silently — see ToolbarWindow.ExitAnnotateMode — so that a
    /// session ending does not come back round through this method a second time.
    /// </remarks>
    private void CloseAnnotationPanel()
    {
        // First: this one is process-wide and swallows Ctrl+Z, so it must never outlive the mode
        // that justifies taking it.
        _annotationKeys?.Dispose();
        _annotationKeys = null;

        _overlayWindow?.EndAnnotating();
        _captureWindow?.SetAnnotationHold(false);

        CloseWindow(_annotationPanel, w => w.Close(), nameof(AnnotationPanelWindow));
        _annotationPanel = null;

        _toolbarWindow?.ExitAnnotateMode();
    }

    // The Esc hook is process-wide and swallows Esc, so it must never outlive the session that owns
    // it; the recovery watch goes with it because there is nothing left to yield the top to.
    private void DisposeSessionHooks()
    {
        _escapeHook?.Dispose();
        _escapeHook = null;

        _recoveryYield?.Dispose();
        _recoveryYield = null;
    }

    // Puts the session's windows where the recovery watch says they belong. Called both when it
    // changes its mind and after a window is created, since the overlay and the toolbar are built
    // after the selection is drawn and would otherwise come up on top of a Task Manager already out.
    private void ApplyCaptureTopmost()
    {
        bool onTop = _recoveryYield?.HasYielded != true;

        // Bottom of the stack first: each one steps in directly behind the recovery window, so the
        // last one placed ends up highest and the layers keep their own order among themselves.
        foreach (var window in new Window?[]
                 { _captureWindow, _overlayWindow, _toolbarWindow, _annotationPanel })
        {
            if (window is null) continue;

            window.Topmost = onTop;

            // Both halves or neither. A step-aside that could not be completed — Task Manager gone
            // between the event and here — would otherwise leave the layer out of the topmost band
            // and still in front of it, which is worse than never having moved.
            if (!onTop && !AlwaysOnTop.PlaceBehind(window, _recoveryYield!.RecoveryWindow))
                window.Topmost = true;
        }
    }

    // Signals recognition/translation started by this session to stop. The source is not disposed
    // here: work already in flight still holds the token, and disposing it underneath them would
    // throw. Letting it be collected is the safe trade for an object this small.
    private void CancelSession()
    {
        _sessionCts?.Cancel();
        _sessionCts = null;
    }

    private static void CloseWindow<T>(T? window, Action<T> close, string name) where T : Window
    {
        if (window == null) return;
        try
        {
            close(window);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to close {Window} — forcing teardown of the remaining windows", name);
        }
    }

    // Last-resort teardown for the unhandled-exception handler. Returns whether a capture session
    // was actually torn down, which is what tells the caller the app is back in a clean state.
    internal bool ForceCloseOverlays()
    {
        if (!HasActiveSession) return false;

        CloseAll();
        return true;
    }

    // The Esc hook counts: it is process-wide, so a session that left only the hook behind still
    // needs tearing down even though every window is already gone.
    private bool HasActiveSession =>
        _overlayWindow != null || _toolbarWindow != null || _captureWindow != null || _escapeHook != null;

    /// <summary>
    /// Whether a screenshot capture — selection, overlay or toolbar — is on screen right now, so
    /// the other feature can decline to start on top of it.
    /// </summary>
    public bool IsCapturing => HasActiveSession;

    private bool IsCurrentSelectionSession(int sessionId, ToolbarWindow? toolbar, ScreenCaptureWindow? captureWindow) =>
        sessionId == _selectionSessionId &&
        ReferenceEquals(toolbar, _toolbarWindow) &&
        ReferenceEquals(captureWindow, _captureWindow);

    private static void OpenSettings() => ShellWindow.ShowOrActivate(ShellPage.Settings);

    private void ShowTrayMenu()
    {
        if (_trayMenu != null) return;
        _trayMenu = new TrayMenuWindow();
        _trayMenu.OpenTranslationRequested += (_, _) => OnTrayLeftClick();
        _trayMenu.SetRealtimeRunning(Views.Realtime.RealtimeSessionController.Instance.IsActive);
        _trayMenu.OpenSettingsRequested    += (_, _) => OpenSettings();
        _trayMenu.ExitRequested            += (_, _) => ExitApp();
        _trayMenu.Closed                   += (_, _) => _trayMenu = null;
        _trayMenu.Show();
    }

    /// <summary>
    /// Opens the shell, or — while a realtime session owns the screen — puts its layers back on
    /// top instead.
    /// </summary>
    /// <remarks>
    /// The window is no use during a session: the layers cover the screen and the session's own
    /// controls are the only thing to interact with. So the click is spent on the one thing the
    /// user might actually need it for, which is reaching a control bar that something else has
    /// covered. Without it the only way out of that would be killing the application from the
    /// tray, which takes the block layout with it.
    /// </remarks>
    private static void OnTrayLeftClick()
    {
        if (Views.Realtime.RealtimeSessionController.Instance.IsActive)
        {
            Views.Realtime.RealtimeSessionController.Instance.BringToFront();
            return;
        }

        OpenShell();
    }

    /// <remarks>
    /// No page named: both ways in here — this shortcut and the tray's left click — mean "show me
    /// the window", so it opens on whichever page it was last left on.
    /// </remarks>
    private static void OpenShell() => ShellWindow.ShowOrActivate();

    private void ExitApp()
    {
        // Its overlays are Topmost and click-through; left behind by a shutdown they would be
        // painted onto the desktop with no process left to close them.
        RealtimeSessionController.Instance.Stop();
        DisposeSessionHooks();
        _hotkey?.Dispose();
        _windowHotkey?.Dispose();
        _realtimePauseHotkey?.Dispose();
        _quickLookupHotkey?.Dispose();
        _quickTranslateHotkey?.Dispose();
        _auxiliaryHotkeys?.Dispose();
        _auxiliaryHotkeys = null;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private static void ShowBalloon(
        string title, string message, System.Windows.Rect? sel = null, ToastKind kind = ToastKind.Error) =>
        ToastWindow.Show(title, message, sel, kind);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitApp();
    }
}
