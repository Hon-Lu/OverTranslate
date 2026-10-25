using Color = System.Windows.Media.Color;

namespace OverTranslate.Controls;

/// <summary>
/// The colours a picker has gone through since it was opened, for 返回 to step back along.
/// </summary>
/// <remarks>
/// One step per colour that was applied — a drag let go, a swatch, a HEX value entered — not per
/// frame of a drag, because only those ever reached the screen. It lasts as long as the picker is
/// open: closing it is the user done with that colour, and opening it again starts clean.
/// </remarks>
public sealed class ColorPickHistory
{
    private readonly Stack<Color> _previous = new();

    public bool CanUndo => _previous.Count > 0;

    /// <summary>Starts over: nothing to step back to.</summary>
    public void Clear() => _previous.Clear();

    /// <summary>
    /// Notes the colour being replaced. A "change" to the same colour is no step at all — the same
    /// swatch pressed twice should not cost two presses of 返回 to get past.
    /// </summary>
    public void Record(Color replaced, Color applied)
    {
        if (replaced != applied) _previous.Push(replaced);
    }

    /// <summary>The colour before the last one applied, taken off the list.</summary>
    public bool TryUndo(out Color previous) => _previous.TryPop(out previous);
}
