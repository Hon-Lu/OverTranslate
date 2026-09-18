namespace OverTranslate.Services.Realtime;

/// <summary>
/// Which way the source text in a watched block is written, as the user says it.
/// </summary>
/// <remarks>
/// The detector only ever looks for rows. Vertical writing reaches it by turning the picture 270°
/// first, so every column of the original arrives as a row — see
/// <see cref="OcrService.RecognizeVerticalAsync"/>, which the screenshot path has used since
/// issue #132. Nothing in that pipeline can decide for itself which of the two it is looking at:
/// a column of Japanese and a narrow panel of horizontal Japanese produce boxes of the same shape,
/// and the answer changes what is read as well as how it is drawn back.
///
/// So it is asked, exactly as <see cref="RealtimeBlockMode"/> is, and for the same reason: the
/// user is looking at the text and the program is not. It sits beside the mode on the block rather
/// than on the session because the two questions are independent and both are per-block — a game
/// can show a horizontal HUD and a vertical dialogue column at the same moment.
///
/// It is deliberately not in the settings file. Which way a block's text runs belongs to that block
/// of that sitting, the same line <see cref="RealtimeBlockPlacement"/> already draws for the mode.
/// </remarks>
public enum RealtimeTextOrientation
{
    /// <summary>Written across, left to right. What all but a handful of blocks are.</summary>
    Horizontal,

    /// <summary>Written down the column, top to bottom, columns running right to left.</summary>
    Vertical,
}
