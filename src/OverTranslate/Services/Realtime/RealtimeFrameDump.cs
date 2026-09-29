using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NLog;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Writes out the frames that tell us something about how a realtime pass read, when asked to.
/// </summary>
/// <remarks>
/// "Recognition found nothing" has causes that the log cannot separate however much is written to
/// it: the line may be framed outside the watched block, clipped by its edge, too small or too faint
/// for the detector, or the grab may have caught the player mid-repaint. All of them look identical
/// from the outside — an empty result — and all of them are obvious in one glance at the frame.
///
/// Two kinds are kept, and they answer different questions. <see cref="SaveUnread"/> holds the
/// frames every size gave up on; a session of those was what showed the reading problem was mostly
/// not one — thirty of thirty-three held no subtitle at all, and the cost was the fallback sweep
/// running over blank picture. <see cref="SaveFallbackRescue"/> holds the frames the first size read
/// nothing in and a later one read fine, which is the only sample that can say whether the detector
/// really is fussy about scale. The second kind is rare where the first is common — five against
/// thirty-three in that same session — so they are counted separately. Sharing one allowance would
/// let the common kind spend it all and leave none for the kind actually being asked about.
///
/// Off unless the marker file exists, and deliberately so: these are pictures of whatever the user
/// has on screen. A file rather than an environment variable because the application is normally
/// started from a shortcut or a debugger, neither of which inherits a variable typed into a shell —
/// creating a file next to the log is something anyone can do, and deleting it turns this off again.
/// Each kind is kept to <see cref="MaxFrames"/> per run so leaving it on cannot quietly fill a disk.
/// </remarks>
internal static class RealtimeFrameDump
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int MaxFrames = 60;

    private static readonly string LogDirectory = AppDataPaths.Logs;

    private static readonly string Directory = Path.Combine(LogDirectory, "frames");

    /// <summary>Create <c>%AppData%\OverTranslate\logs\dumpframes</c> and restart to switch on.</summary>
    public static readonly bool IsEnabled = File.Exists(Path.Combine(LogDirectory, "dumpframes"));

    /// <summary>
    /// One primary-read frame kept per this many. Chosen so a couple of minutes of watching yields
    /// a sample comparable in size to the rescued set it is there to be compared against.
    /// </summary>
    private const int PrimarySampleEvery = 6;

    private static int _unreadWritten;
    private static int _rescuedWritten;
    private static int _primaryWritten;
    private static int _primarySeen;

    /// <summary>A frame no size read anything in, after the fallbacks had their turn.</summary>
    public static void SaveUnread(Bitmap frame, int regionId) =>
        Save(frame, ref _unreadWritten, "unread", $"region{regionId}-{Stamp()}-unread.png");

    /// <summary>
    /// A frame the primary size read nothing in that a fallback then read fine — the case that
    /// costs a pass its extra inferences, and the only evidence that scale is what went wrong.
    /// </summary>
    /// <remarks>
    /// The two sizes go in the name because the question these are kept for is which pairs of
    /// fractions land on opposite sides of working, and reading that off the picture is impossible.
    /// </remarks>
    public static void SaveFallbackRescue(Bitmap frame, int regionId, int primarySize, int rescueSize) =>
        Save(
            frame, ref _rescuedWritten, "fallback-rescued",
            $"region{regionId}-{Stamp()}-rescued-p{primarySize}-r{rescueSize}.png");

    /// <summary>
    /// Every <see cref="PrimarySampleEvery"/>th frame the primary size read fine on its own — the
    /// control group, without which the frames above cannot be acted on.
    /// </summary>
    /// <remarks>
    /// The other two kinds are both collected on the condition that the primary size failed, so a
    /// sweep over them can say which size to fall back to but nothing at all about which size to
    /// start with: the frames that would be hurt by moving the primary are exactly the ones never
    /// kept. Sampled rather than kept whole because these are the common case — the measured session
    /// had 101 of them against 15 of the other kind — and a handful is all a comparison needs.
    /// </remarks>
    public static void SamplePrimaryRead(Bitmap frame, int regionId, int primarySize)
    {
        if (!IsEnabled) return;

        // Counted before the allowance is touched, so the sampling stays evenly spread rather than
        // taking the first ones and stopping.
        if (Interlocked.Increment(ref _primarySeen) % PrimarySampleEvery != 0) return;

        Save(
            frame, ref _primaryWritten, "primary-read",
            $"region{regionId}-{Stamp()}-primaryok-p{primarySize}.png");
    }

    private static string Stamp() => DateTime.Now.ToString("HHmmss-fff");

    private static void Save(Bitmap frame, ref int written, string kind, string fileName)
    {
        if (!IsEnabled || Interlocked.Increment(ref written) > MaxFrames) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, fileName);

            // Cloned because the caller disposes this frame at the end of its poll, and because
            // Save on a bitmap another thread may still be reading from is not safe.
            using var copy = new Bitmap(frame);
            copy.Save(path, ImageFormat.Png);

            Log.Debug("Saved {Kind} realtime frame to {Path}", kind, path);
        }
        catch (Exception ex)
        {
            // Diagnostics must never be the reason a session stops.
            Log.Warn(ex, "Could not save a {Kind} realtime frame", kind);
        }
    }
}
