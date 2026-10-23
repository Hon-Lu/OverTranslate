namespace OverTranslate.Views.Realtime;

/// <summary>
/// The failure a running session keeps retrying, as the capsule carries it: shown in the language
/// pair's place until it clears, unless the user has clicked it away.
/// </summary>
/// <remarks>
/// Clicking it away is for the failure that will not clear by itself — a service that refuses the
/// language pair goes on refusing it — when changing the pair is the fix and the pair is the very
/// thing it covers. Dismissed, it stays dismissed only while it is the same failure: a different one,
/// or the same one coming back after it had cleared, is news and shows again. The status dot stays
/// red the whole time, so the failure is set aside rather than forgotten.
/// </remarks>
internal sealed class RealtimeFailureNotice
{
    /// <summary>What is failing, or null when nothing is.</summary>
    public string? Message { get; private set; }

    /// <summary>Whether the user has clicked this failure away.</summary>
    public bool IsDismissed { get; private set; }

    /// <summary>Whether the failure takes the pair's place on the capsule.</summary>
    public bool StandsInForPair => Message is not null && !IsDismissed;

    /// <summary>Records what the session now says is failing; false when that is nothing new.</summary>
    public bool Set(string? message)
    {
        if (Message == message) return false;
        Message = message;
        IsDismissed = false;
        return true;
    }

    /// <summary>Sets the failure aside until it changes; false when there is none to set aside.</summary>
    public bool Dismiss()
    {
        if (!StandsInForPair) return false;
        IsDismissed = true;
        return true;
    }
}
