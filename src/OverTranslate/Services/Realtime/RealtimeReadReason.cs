namespace OverTranslate.Services.Realtime;

/// <summary>
/// Why a poll is worth recognising — which decides whether it has to be, or only might be.
/// </summary>
/// <remarks>
/// Measured over three corpora (92 frames holding text the shipped size reads, 120 holding nothing
/// any size could read): asking the detector at the shipped size whether a frame has text in it
/// answers "yes" on every single one of the 120 empty frames. The boxes are there; recognition's
/// confidence floor is what throws them away afterwards. So "run detection and skip recognition"
/// buys nothing on its own.
///
/// Shrink the detector's input, though, and the two halves separate — the noise stops producing
/// boxes while real text keeps producing them. At 0.30 and 0.40 of the region's long side,
/// alternating, and a box score over 0.6, every one of the 92 frames with text is let through and
/// three quarters of the empty ones are turned away, for about 20ms against 93ms for the pass it
/// stands in front of. See OcrHarness <c>--text-presence</c>.
///
/// That is only worth having in front of the two reads that are asking a question rather than
/// following a change, which is why this type exists.
/// </remarks>
internal enum RealtimeReadReason
{
    /// <summary>This poll is not worth recognising.</summary>
    Nothing,

    /// <summary>
    /// The strips holding the text already on screen changed. That is the words themselves
    /// changing, it happens once per line of dialogue, and nothing cheaper can confirm it — so this
    /// goes straight to recognition.
    /// </summary>
    TextChanged,

    /// <summary>
    /// The region holds no known text and its picture is moving. Whether any of that movement is a
    /// line appearing cannot be told apart from the video behind it without recognising, so this
    /// runs on a timer and is usually spent on nothing.
    /// </summary>
    Search,

    /// <summary>
    /// The watched strips are unchanged but the region as a whole is not, so text may have appeared
    /// somewhere the strips cannot see. Also on a timer, and also usually spent on nothing.
    /// </summary>
    Rescan,
}
