namespace OverTranslate.Views.Realtime;

/// <summary>
/// The failure a running session keeps retrying, as the capsule carries it: shown in the language
/// pair's place in the translation button until it clears.
/// </summary>
/// <remarks>
/// There is no clicking it away. The button it is shown in opens the translation panel, and opening
/// the panel leaves it where it is: the panel is where the fix is picked — another service, another
/// language — and the restart that follows is what clears it. The pair can be read in the panel in
/// the meantime, and a message cut short is whole in the button's label card.
/// </remarks>
internal sealed class RealtimeFailureNotice
{
    /// <summary>What is failing, or null when nothing is.</summary>
    public string? Message { get; private set; }

    /// <summary>Records what the session now says is failing; false when that is nothing new.</summary>
    public bool Set(string? message)
    {
        if (Message == message) return false;
        Message = message;
        return true;
    }
}
