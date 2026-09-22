namespace OverTranslate.Services.Realtime;

/// <summary>
/// What a screenshot capture has to do to a realtime session that is already on screen, and what it
/// owes that session once the capture is over.
/// </summary>
/// <remarks>
/// <para>A capture used to be refused outright while a session ran: the two share one OCR engine and
/// one bounded pool of inference slots, and letting them read at the same time had them competing
/// for both — see OcrEngineConcurrencyTests for what that measured out as. The refusal is gone
/// because what it protected can be had another way. A session that is paused for the duration is
/// not reading anything, so there is no competition left to arbitrate, and pausing is what a user
/// who wanted a capture would have done by hand first.</para>
///
/// <para>Worth allowing because a capture is the one thing a running session cannot do for itself: a
/// session reads blocks the user framed once, loosely, around where the text usually appears, and it
/// only ever shows the latest reading. A capture is a second look at one moment the user chose, at a
/// size they chose, and it can be repeated on the same frozen frame until it comes out right.</para>
///
/// <para>Block framing is the one state still refused. There is nothing running to pause there, and
/// the edit layer covers the whole screen — a screenshot taken over it would be a picture of that
/// layer rather than of anything the user meant to read.</para>
///
/// <para>Kept as a rule of its own, apart from the controller that carries it out, because the part
/// worth being sure about is small and has nothing to do with windows: which of the states lets a
/// capture through, and — the one the user notices when it is wrong — that a session they had
/// already paused themselves is still paused when they come back to it.</para>
/// </remarks>
/// <param name="Allowed">Whether the capture may start at all.</param>
/// <param name="HideLayers">
/// Whether this session has windows that have to leave the screen first. False when there is no
/// session, which is the ordinary case and costs nothing.
/// </param>
/// <param name="PauseWatching">
/// Whether the capture is the one pausing the session — and therefore the one that has to start it
/// watching again afterwards. False for a session the user had already paused: that pause is theirs,
/// and a capture that ended by resuming it would have moved a switch nobody touched.
/// </param>
public readonly record struct RealtimeCaptureInterlude(bool Allowed, bool HideLayers, bool PauseWatching)
{
    /// <summary>Nothing to stand down, and nothing owed afterwards.</summary>
    public static readonly RealtimeCaptureInterlude None = new(Allowed: true, HideLayers: false, PauseWatching: false);

    /// <summary>
    /// Reads the session's state as a capture finds it.
    /// </summary>
    /// <param name="sessionActive">Whether a realtime session exists at all.</param>
    /// <param name="translating">Whether it is watching the screen, as opposed to framing blocks.</param>
    /// <param name="alreadyPaused">Whether the user had paused the watching themselves.</param>
    public static RealtimeCaptureInterlude For(bool sessionActive, bool translating, bool alreadyPaused)
    {
        if (!sessionActive) return None;

        if (!translating)
            return new(Allowed: false, HideLayers: false, PauseWatching: false);

        return new(Allowed: true, HideLayers: true, PauseWatching: !alreadyPaused);
    }
}
