using System.Drawing;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Where a realtime session's pixels come from. One instance serves a whole run of translating —
/// every region asks the same backend for its rectangle.
/// </summary>
/// <remarks>
/// This exists to hold one rule in one place: <b>recognition must never run on a frame that may
/// contain OverTranslate's own overlays.</b> Which is a question about the capture source, not about
/// the overlays — and for most of this feature's life it was answered in the wrong place, by asking
/// the subtitle windows to hide themselves from the screen via <c>WDA_EXCLUDEFROMCAPTURE</c>. That
/// works only where the source is the composited desktop, and it fails outright on every Windows
/// before 11 24H2 (#94), where it cost users a loop that translated its own output.
///
/// So a backend is answerable for its own isolation, and answerable structurally — by what it
/// captures, not by what it asks other windows to do about themselves. A window capture never had
/// the overlays in frame to begin with; a monitor capture has the system compose the screen without
/// them and refuses to read a frame composed before that took effect. There is no third kind: the
/// backend that grabbed the composited desktop and relied on the overlays hiding themselves was
/// removed in #105, because its isolation was a claim the program could not check.
///
/// There is deliberately no property on this interface for a backend to <i>declare</i> that it is
/// isolated. One existed, and the session refused to start on a backend that answered false — but
/// once every implementation was structurally isolated it could only ever answer true, and a claim
/// nobody can check is the exact shape of the thing #105 removed: <c>WDA_EXCLUDEFROMCAPTURE</c> also
/// reported success, and #94 is what believing it cost. A new backend satisfies this rule by being
/// built so that it cannot fail — by capturing something the overlays are not part of, or by
/// refusing to construct until the system has agreed to leave them out — not by returning true.
///
/// None of how that is arranged reaches the session, which asks for a rectangle and gets pixels.
/// </remarks>
public interface IRealtimeCaptureBackend : IDisposable
{
    /// <summary>How this backend appears in the log. Short, stable, and one word for the source.</summary>
    string Name { get; }

    /// <summary>
    /// The pixels currently inside <paramref name="screenBounds"/>, given in physical screen
    /// coordinates, as a bitmap the caller owns and disposes.
    /// </summary>
    /// <remarks>
    /// The one way anything in a running session is allowed to see the screen. Recognition is the
    /// obvious caller, but not the only one: the natural-background repair needs the picture
    /// <i>under</i> this application's overlays, and that is exactly what a backend promises and
    /// nothing else in the process can obtain.
    /// </remarks>
    /// <returns>
    /// Null when this poll produced nothing — a locked screen, a source that has gone away, a
    /// transient failure. The caller skips the poll; the next one is one poll interval behind it.
    /// </returns>
    Bitmap? GrabRegion(Rectangle screenBounds);

    /// <summary>
    /// What this backend did over its lifetime, for the one line logged when the session ends. Free
    /// text — each source counts different things.
    /// </summary>
    string DescribeActivity();
}
