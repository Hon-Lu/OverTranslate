using NLog;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Turns off the yellow frame Windows draws around whatever a realtime session captures (#320).
/// </summary>
/// <remarks>
/// <para>The frame is the system's capture indicator. For a realtime session it says nothing the
/// floating bar does not already say — the session only runs because the user started it, and the
/// bar stays on screen for as long as it does — while sitting around a game or a video for hours.
/// So it is turned off wherever Windows allows, with no setting: nobody running a session wants it
/// back, and a switch for it would be one more thing to explain.</para>
///
/// <para>Windows 11 21H2 and later only. The property does not exist before that, so Windows 10
/// always shows the frame. The documentation ties the permission to a package capability this
/// unpackaged build cannot declare, but measured on 26200 the request comes back Allowed all the
/// same (#96). It is not remembered between runs, so it is asked once per process — off the frame
/// thread, whose apartment is where Windows 10 deadlocked on a completion it was blocked waiting
/// for — and every capture session started after that is told before it starts.</para>
/// </remarks>
internal static class WgcCaptureBorder
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<string> Request = new(Ask);

    /// <summary>What Windows said, in words, for the log and WgcProbe.</summary>
    public static string Status => Request.Value;

    private static bool Allowed => Status == "allowed";

    /// <summary>
    /// Tells <paramref name="session"/> not to draw the indicator, where that is allowed. Before
    /// <c>StartCapture</c>, so the frame never appears at all.
    /// </summary>
    public static void Apply(GraphicsCaptureSession session)
    {
        if (!Allowed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;

        try { session.IsBorderRequired = false; }
        catch (Exception ex) { Log.Debug(ex, "Capture indicator could not be turned off"); }
    }

    private static string Ask()
    {
        string status;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) || !WgcCapability.CanRequestBorderless)
        {
            status = "unavailable (needs Windows 11 21H2)";
        }
        else
        {
            try
            {
                var request = Task.Run(async () =>
                    await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless));
                status = !request.Wait(RequestTimeout)
                    ? "timed out"
                    : request.Result == AppCapabilityAccessStatus.Allowed
                        ? "allowed"
                        : $"denied ({request.Result})";
            }
            catch (Exception ex)
            {
                // Refusal arrives as an exception on some systems rather than a status.
                var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
                status = $"failed ({cause.GetType().Name}: {cause.Message.Trim()})";
            }
        }

        Log.Info("Realtime capture indicator: {Status}", status);
        return status;
    }
}
