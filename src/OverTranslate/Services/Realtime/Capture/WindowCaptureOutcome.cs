namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// How an attempt to start capturing one window ended.
/// </summary>
/// <remarks>
/// Three failures rather than one, because they are three different things to tell the user. A
/// window that hands over only flat frames is presenting past the compositor — a game in exclusive
/// fullscreen — and the answer is a setting inside that game. A window that hands over nothing at
/// all has a capture chain that never ran, which the user cannot act on and a log can. Anything
/// else was refused outright, before any frame was waited for.
///
/// The distinction is new because the old message covered all of them with the fullscreen
/// explanation, which sent a user whose game was already windowed looking for a setting that was
/// not the problem.
/// </remarks>
public enum WindowCaptureOutcome
{
    /// <summary>The capture never got as far as waiting for a frame.</summary>
    Refused,

    /// <summary>
    /// This Windows is missing the capture component itself (REGDB_E_CLASSNOTREG from
    /// CreateForWindow): no window can be captured, whatever its display mode. Seen on a Windows 10
    /// 22H2 that reported capture as supported — most likely a trimmed-down install.
    /// </summary>
    Unavailable,

    /// <summary>No frame arrived at all within the start-up window.</summary>
    NoFrame,

    /// <summary>Frames arrived and every one of them was a single flat colour.</summary>
    Blank,

    /// <summary>A frame with something in it; the backend is running.</summary>
    Started
}
