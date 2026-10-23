using OverTranslate.Models;
using OverTranslate.Services.Realtime.Capture;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the 即時翻譯 shortcut can start from the settings file alone, and what it says when it
/// cannot (#324).
/// </summary>
/// <remarks>
/// Run against the machine's own monitors and windows, since that is what the shortcut reads. Each
/// case is chosen so the answer does not depend on what happens to be open: a window that cannot be
/// running, a monitor that cannot be attached.
/// </remarks>
public class RealtimeQuickStartTests
{
    private static AppSettings WindowMode(string process, string title = "")
    {
        var settings = new AppSettings();
        settings.Realtime.CaptureMode = RealtimeCaptureMode.Window;
        settings.Realtime.CaptureWindowProcess = process;
        settings.Realtime.CaptureWindowTitle = title;
        return settings;
    }

    [Fact]
    public void AWindowNeverPickedAsksForOne()
    {
        if (!WgcCapability.SupportsWindowMode) return;  // nothing to ask on a machine without it

        var quickStart = RealtimeQuickStart.From(WindowMode(""));

        Assert.Null(quickStart.Request);
        Assert.Equal("S.Realtime.ChooseWindowFirst", quickStart.RefusalKey);
    }

    [Fact]
    public void APickedWindowThatIsNotOpenSaysSo()
    {
        if (!WgcCapability.SupportsWindowMode) return;  // nothing to ask on a machine without it

        var quickStart = RealtimeQuickStart.From(WindowMode("overtranslate-test-no-such-app", "Nothing"));

        Assert.Null(quickStart.Request);
        Assert.Equal("S.Realtime.WindowCaptureGone", quickStart.RefusalKey);
    }

    [Fact]
    public void AStoredMonitorThatIsGoneFallsBackToThePrimary()
    {
        if (!WgcCapability.SupportsScreenMode) return;

        var settings = new AppSettings();
        settings.Realtime.CaptureMode = RealtimeCaptureMode.Screen;
        settings.Realtime.CaptureScreenDeviceName = @"\\.\DISPLAY-NOT-ATTACHED";

        var request = RealtimeQuickStart.From(settings).Request;

        Assert.NotNull(request);
        Assert.Equal(RealtimeCaptureMode.Screen, request.CaptureMode);
        Assert.Equal(System.Windows.Forms.Screen.PrimaryScreen!.Bounds, request.ScreenBounds);
    }

    [Fact]
    public void TheRequestCarriesTheSettings()
    {
        var settings = new AppSettings();
        settings.Realtime.TextColor = "#123456";
        settings.Realtime.BorderEnabled = true;
        settings.Realtime.BorderColorMode = RealtimeBorderColorMode.Fixed;
        settings.Realtime.BorderColor = "#ABCDEF";

        var request = RealtimeQuickStart.Build(
            settings,
            new System.Drawing.Rectangle(0, 0, 100, 100),
            "JA",
            "ZH-HANT",
            TranslationProvider.Microsoft,
            RealtimeCaptureMode.Screen,
            IntPtr.Zero);

        Assert.Equal("#123456", request.TextColor);
        Assert.True(request.Border);
        Assert.Equal("#ABCDEF", request.FixedBorderColor);
    }
}
