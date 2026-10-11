namespace OverTranslate.Services.Realtime;

/// <summary>
/// Counts the polls in a row on which a region was not on its source at all, and says — once — when
/// what it last drew should come off the screen.
/// </summary>
/// <remarks>
/// A region is off its source when the window it reads is minimised, hidden or gone, or has moved or
/// changed size so that the block is no longer over it — as the window is now, see
/// <see cref="Capture.CaptureMiss"/>. Carrying on as if nothing happened left the last translation
/// and its repaired background on the screen with nothing behind them: a piece of the game, frozen,
/// floating over whatever the user switched to — which over a second game is exactly what a frozen
/// screen looks like.
///
/// Only those polls are counted. A poll whose picture is merely late — the last frame is of the
/// window at its old size, frames are held back for a moment, none has come yet — is neither a miss
/// nor a sighting: the caller does not report it, and the count stands. A still page that was made
/// smaller and then larger again would otherwise lose a translation that is still right, and nothing
/// would put it back until the page changed.
///
/// Not on the first miss either: a window being dragged can leave a block for a poll or two.
/// A few polls is well under a second and longer than that.
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
