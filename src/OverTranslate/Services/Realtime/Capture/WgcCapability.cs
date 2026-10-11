using System.Runtime.Versioning;
using Windows.Foundation.Metadata;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// What Windows.Graphics.Capture can actually do on the machine this is running on, asked of the
/// system rather than inferred from its build number.
/// </summary>
/// <remarks>
/// The build number is not the answer to any of these questions. Capture is refused outright on
/// systems whose graphics stack cannot serve it regardless of version; the window exclusion list is
/// new enough that being on a build that shipped it is no promise that this projection can reach it;
/// and turning off the capture border needs a consent this application's deployment model may not be
/// able to ask for. Each is asked separately, and the answers go in the log — the first thing any
/// report about this feature needs is which capture path the user was actually on.
///
/// Nothing here throws. An unavailable API is a fact about the system, not a failure.
///
/// Every answer is cached after the first ask. None of them can change while the process runs — a
/// Windows that gains the exclusion list gained it in an update that needed a restart — and they are
/// now read from the interface, where 擷取來源 decides which modes to offer before the user has
/// chosen anything. Asked once at launch by <see cref="DisplayDiagnostics"/>, which puts them in the
/// log next to the build number that decides them, and warm for everything after.
/// </remarks>
public static class WgcCapability
{
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string DisplaySessionType = "Windows.Graphics.Capture.IDisplayGraphicsCaptureSession";

    private static readonly Lazy<bool> CaptureSupported = new(() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) && WgcInterop.IsCaptureSupported());

    private static readonly Lazy<bool> BorderlessRequestable = new(() =>
        IsApiPresent(() => ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired")));

    private static readonly Lazy<bool> WindowExclusion = new(() =>
        IsApiPresent(() =>
            ApiInformation.IsMethodPresent(SessionType, "SetWindowExclusionList")
            || ApiInformation.IsMethodPresent(DisplaySessionType, "SetWindowExclusionList")));

    private static readonly Lazy<bool> DisplaySession = new(() =>
        IsApiPresent(() => ApiInformation.IsTypePresent(DisplaySessionType)));

    private static readonly Lazy<bool> MinUpdateInterval = new(() =>
        IsApiPresent(() => ApiInformation.IsPropertyPresent(SessionType, "MinUpdateInterval")));

    /// <summary>Capture at all: the 1903 API surface, and a system willing to serve it.</summary>
    /// <remarks>
    /// The version check is here as well as inside, and it is not redundant: it is what lets the
    /// platform analyser see that nothing newer than 1809 is reached on a system that only has 1809.
    /// </remarks>
    [SupportedOSPlatformGuard("windows10.0.18362.0")]
    public static bool IsCaptureSupported => CaptureSupported.Value;

    /// <summary>
    /// Whether the yellow capture indicator around a captured window can be turned off. Present from
    /// Windows 11 21H2 — and present is not permitted: doing it also needs
    /// <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c>, which is granted against a
    /// package identity that an unpackaged Velopack build does not have.
    /// </summary>
    public static bool CanRequestBorderless => BorderlessRequestable.Value;

    /// <summary>
    /// Whether a capture session can be told to leave specific windows out of its frames — the 2026
    /// API that lets a monitor capture exclude OverTranslate's own overlays, and so keeps the "frame
    /// any part of the screen" interface with none of the WDA problem. What
    /// <see cref="WgcMonitorCaptureBackend"/> is built on, and the reason it is offered only on some
    /// machines.
    /// </summary>
    /// <remarks>
    /// Asked of both places it could live. The documentation puts it on the display-capture session,
    /// while the SDK release notes graduate <c>IGraphicsCaptureSession7</c> — which the projection
    /// would fold into <see cref="Windows.Graphics.Capture.GraphicsCaptureSession"/> — so neither on
    /// its own is a safe question.
    /// </remarks>
    public static bool SupportsWindowExclusion => WindowExclusion.Value;

    /// <summary>
    /// Whether the display-capture session interface the exclusion list is documented against exists
    /// here. Tracked separately from <see cref="SupportsWindowExclusion"/> because the two shipped at
    /// different times and under different stability promises.
    /// </summary>
    public static bool SupportsDisplaySession => DisplaySession.Value;

    /// <summary>
    /// Whether a capture session can be told not to deliver frames more often than a given interval
    /// — Windows 11 24H2 and later. See <see cref="WgcFrameThread.LimitFrameRate"/>.
    /// </summary>
    public static bool SupportsMinUpdateInterval => MinUpdateInterval.Value;

    /// <summary>
    /// Whether 螢幕擷取 can work on this machine at all: a monitor capture that composes the screen
    /// without this application's overlays, which needs both capture and the exclusion list.
    /// </summary>
    /// <remarks>
    /// The one statement of that rule, read by the interface to decide whether to offer the mode and
    /// by <c>RealtimeSessionController.CreateScreenCapture</c> to decide whether to build the
    /// backend. Two copies of it would be one copy that can go stale, and the shape of that failure
    /// is the worst available here: an interface offering a mode the session then refuses.
    ///
    /// It is not the whole of what can go wrong — a graphics device that will not build, a monitor
    /// that refuses capture, and a capture chain that produces no isolated frame in time are all
    /// only knowable by trying, and the refusal at start covers them. This is the part that is
    /// decided before the user has chosen anything, and it never changes while the program runs.
    /// </remarks>
    public static bool SupportsScreenMode => IsCaptureSupported && SupportsWindowExclusion;

    /// <summary>
    /// Whether 視窗擷取 can work on this machine at all. Window capture asks nothing of the overlays
    /// — the source is somebody else's window — so capture existing is the whole requirement, which
    /// is why this is the mode a machine without the exclusion list is sent to.
    /// </summary>
    public static bool SupportsWindowMode => IsCaptureSupported;

    /// <summary>One line for the log, listing every answer above.</summary>
    public static string Describe() =>
        $"os={Environment.OSVersion.Version} capture={IsCaptureSupported} " +
        $"borderless={CanRequestBorderless} windowExclusion={SupportsWindowExclusion} " +
        $"displaySession={SupportsDisplaySession} minUpdateInterval={SupportsMinUpdateInterval}";

    private static bool IsApiPresent(Func<bool> ask)
    {
        try
        {
            // ApiInformation itself is 1607 and older than anything supported here, but the metadata
            // lookup behind it can fail on a system whose projection is incomplete.
            return ask();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
