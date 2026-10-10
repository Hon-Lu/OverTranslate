namespace OverTranslate.Services.Realtime;

/// <summary>
/// Counts the polls in a row on which a region got no picture at all, and says — once — when what
/// it last drew should come off the screen.
/// </summary>
/// <remarks>
/// A region gets no picture when the window it reads is minimised, or has moved or changed size so
/// that the block is no longer over it. Carrying on as if nothing happened left the last translation
/// and its repaired background on the screen with nothing behind them: a piece of the game, frozen,
/// floating over whatever the user switched to — which over a second game is exactly what a frozen
/// screen looks like.
///
/// Not on the first miss. A picture is briefly unavailable while a capture is rebuilt for a resize,
/// and clearing a line the user is reading for that would be a flicker for nothing. A few polls is
/// well under a second and longer than any of those.
/// </remarks>
public sealed class RealtimeFrameLoss
{
    /// <summary>How many polls in a row without a picture before the region is cleared.</summary>
    public const int ClearAfterPolls = 4;

    private int _missed;

    /// <summary>
    /// Records a poll that got no picture. True on the one poll that reaches the limit, so the
    /// caller clears once rather than on every poll for as long as the picture stays away.
    /// </summary>
    public bool Missed() => ++_missed == ClearAfterPolls;

    /// <summary>Records a poll that got a picture.</summary>
    public void Seen() => _missed = 0;
}
