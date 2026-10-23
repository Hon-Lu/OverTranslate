using OverTranslate.Models;
using OverTranslate.Services.Realtime.Capture;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Whether the 即時翻譯 shortcut can drop straight into block framing, and what to say when it
/// cannot (#324).
/// </summary>
/// <remarks>
/// <para>The page's 選取翻譯區塊 button asks the same questions, but it asks them of its own pickers.
/// A shortcut has no page — it is pressed with the shell closed to the tray, which is its whole
/// point — so the answers come from the settings file instead. They are all there now: the page
/// writes every pick through as it is made, the window included, by application and title.</para>
///
/// <para>That last one is why a shortcut like this was once taken out again: a session reading one
/// window needs a live handle, and the settings file had nothing to find it by. It has the stored
/// application and title now, and <see cref="CaptureWindowList.FindStored"/> is the same match the
/// page makes when it opens — so the shortcut picks the window the page would have shown as picked,
/// or refuses where the page would have shown none.</para>
///
/// <para>Checked in the button's order, so the two refuse for the same reason when more than one
/// thing is wrong. Whether a capture or a session is already running is not asked here: those are
/// facts about the moment rather than about the settings, and the caller holds them.</para>
/// </remarks>
internal readonly record struct RealtimeQuickStart(RealtimeStartRequest? Request, string? RefusalKey)
{
    /// <summary>Reads the settings and either builds a request or names the first thing missing.</summary>
    public static RealtimeQuickStart From(AppSettings settings)
    {
        var realtime = settings.Realtime;

        if (!WgcCapability.SupportsWindowMode && !WgcCapability.SupportsScreenMode)
            return Refuse("S.Realtime.RealtimeUnsupported");

        var source = LanguageData.GetValidOcrSourceCode(realtime.SourceLanguage);
        if (string.IsNullOrWhiteSpace(source))
            return Refuse("S.Realtime.ChooseSourceFirst");

        // The same coercion the page applies when it opens: a saved 螢幕擷取 on a machine that cannot
        // do it is 視窗擷取, without the preference being rewritten.
        var windowMode = realtime.CaptureMode == RealtimeCaptureMode.Window || !WgcCapability.SupportsScreenMode;

        System.Drawing.Rectangle bounds;
        var sourceWindow = IntPtr.Zero;

        if (windowMode)
        {
            var stored = CaptureWindowList.FindStored(
                CaptureWindowList.Enumerate(), realtime.CaptureWindowProcess, realtime.CaptureWindowTitle);

            // Never picked, or picked and not open now. The second is the one worth naming: a game
            // that is not running is not a decision to forget it, and the page will still be holding
            // the stored choice when the user gets there.
            if (stored is not { } window)
            {
                return Refuse(realtime.CaptureWindowProcess.Length == 0
                    ? "S.Realtime.ChooseWindowFirst"
                    : "S.Realtime.WindowCaptureGone");
            }

            if (ScreenOf(window.Hwnd) is not { } windowScreen)
                return Refuse("S.Realtime.WindowCaptureGone");

            bounds = windowScreen.Bounds;
            sourceWindow = window.Hwnd;
        }
        else
        {
            if (ScreenFor(realtime.CaptureScreenDeviceName) is not { } screen)
                return Refuse("S.Realtime.NoScreens");

            bounds = screen.Bounds;
        }

        return new RealtimeQuickStart(
            Build(
                settings,
                bounds,
                source,
                LanguageData.GetValidTargetCode(realtime.TargetLanguage),
                realtime.Provider,
                windowMode ? RealtimeCaptureMode.Window : RealtimeCaptureMode.Screen,
                sourceWindow),
            null);
    }

    /// <summary>
    /// A request from the answers to the questions a sitting asks, and everything else from the
    /// settings file.
    /// </summary>
    /// <remarks>
    /// Shared with the page's button so the two cannot start sessions that look different: the
    /// colours, the scrim, the border and the font size come from here either way.
    /// </remarks>
    public static RealtimeStartRequest Build(
        AppSettings settings,
        System.Drawing.Rectangle bounds,
        string sourceLanguage,
        string targetLanguage,
        TranslationProvider provider,
        RealtimeCaptureMode captureMode,
        IntPtr sourceWindow)
    {
        var realtime = settings.Realtime;
        return new RealtimeStartRequest(
            bounds,
            sourceLanguage,
            targetLanguage,
            provider,
            realtime.TextColor,
            realtime.ScrimColor,
            realtime.ScrimOpacity,
            realtime.NaturalBackgroundEnabled,
            realtime.SampleSourceTextColor,
            captureMode,
            sourceWindow,
            realtime.BorderEnabled,
            realtime.BorderColorMode == RealtimeBorderColorMode.Fixed ? realtime.BorderColor : null,
            realtime.FontSizeMode == RealtimeFontSizeMode.Fixed ? realtime.FixedFontSize : null);
    }

    private static RealtimeQuickStart Refuse(string key) => new(null, key);

    /// <summary>
    /// The stored monitor; the primary when that one is not attached any more; and the one under the
    /// pointer when nothing was ever stored.
    /// </summary>
    /// <remarks>
    /// The page's three cases, with its last answer swapped: it falls back to the monitor its own
    /// window is on, and a shortcut may have no window open. The pointer is the nearest thing to
    /// "the screen the user is looking at" that is always there.
    /// </remarks>
    private static Screen? ScreenFor(string deviceName)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return null;

        if (deviceName.Length > 0)
        {
            return Array.Find(screens, screen => screen.DeviceName == deviceName)
                ?? Array.Find(screens, screen => screen.Primary)
                ?? screens[0];
        }

        return Screen.FromPoint(System.Windows.Forms.Cursor.Position);
    }

    private static Screen? ScreenOf(IntPtr hwnd)
    {
        try
        {
            return CaptureWindowList.StillAvailable(hwnd) ? Screen.FromHandle(hwnd) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
