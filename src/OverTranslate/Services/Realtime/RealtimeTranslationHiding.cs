namespace OverTranslate.Services.Realtime;

/// <summary>
/// 隱藏譯文: the running session's translations taken off the screen and put back, without the
/// session stopping in between.
/// </summary>
/// <remarks>
/// <para>暫停 already takes the words away, and that was not what users who asked for this wanted.
/// A pause stops the reading, so coming back means every block being read again from nothing before
/// a word reappears — and a reading that comes out one character different is translated again.
/// Someone looking past the overlay at the picture under it for a moment wants the words back the
/// instant they ask, and wants them to be the words for what is on screen now. So the session keeps
/// reading, translating, following a scroll and clearing a line that went away, exactly as if it
/// were on screen; only the drawing is withheld. What that costs is what the session was already
/// costing, and the user who wants it to cost nothing has 暫停.</para>
///
/// <para>A state of the sitting, not a setting: going back to framing or ending the session lets go
/// of it, and the next sitting starts with the words showing. Framing has to show the blocks, and a
/// new session that opened invisible would read as a broken one.</para>
///
/// <para>Kept apart from the controller that applies it, like <see cref="RealtimeCaptureInterlude"/>,
/// because the rule is small and has nothing to do with windows.</para>
/// </remarks>
public sealed class RealtimeTranslationHiding
{
    /// <summary>Whether the translations are being withheld from the screen.</summary>
    public bool Hidden { get; private set; }

    /// <summary>
    /// The press of the button or the shortcut. Returns whether it changed anything.
    /// </summary>
    /// <param name="translating">
    /// Whether the session is past framing. Nothing is drawn while framing, so there is nothing to
    /// hide, and the shortcut is quietly ignored there as 暫停's is.
    /// </param>
    /// <param name="standingDownForCapture">
    /// Whether a screenshot capture has the layers off the screen. The bar is gone then, so only the
    /// shortcut could reach this, and a switch moved under a capture would come back as a surprise.
    /// </param>
    public bool Toggle(bool translating, bool standingDownForCapture)
    {
        if (!translating || standingDownForCapture) return false;

        Hidden = !Hidden;
        return true;
    }

    /// <summary>Back to showing: for going back to framing, and for the end of the session.</summary>
    public void Reset() => Hidden = false;
}
