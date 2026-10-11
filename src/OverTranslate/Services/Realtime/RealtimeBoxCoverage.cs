using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Which of an overlay's translation boxes another application's window is touching, kept per box
/// so that only a box whose state changes is touched on the screen.
/// </summary>
/// <remarks>
/// A box is one translated line with its band or repaired patch — or a panel group's rows under
/// their one patch, which are one thing to look at. A window over one of them takes that one away
/// and leaves the rest of the block, and whatever is under an empty part of the block is not
/// covering anything the overlay shows.
///
/// The covers outlive the boxes: a translation that arrives while a window is over its place is
/// drawn hidden straight away, and comes out as itself — the newest words, not the ones that were
/// there when it was covered — once the window moves. Measuring is left to the caller and asked for
/// only while there is something to measure against, so an uncovered overlay pays nothing.
/// </remarks>
internal sealed class RealtimeBoxCoverage
{
    private IReadOnlyList<Rectangle> _covers = [];
    private bool[] _hidden = [];

    /// <summary>How many of the current boxes are hidden.</summary>
    public int HiddenCount => _hidden.Count(hidden => hidden);

    /// <summary>
    /// The boxes were drawn again, all of them shown. Returns the ones to hide at once under the
    /// covers already known.
    /// </summary>
    /// <param name="measure">Each box's drawn areas in physical pixels, by index.</param>
    public IReadOnlyList<int> Redrawn(int count, Func<IReadOnlyList<IReadOnlyList<Rectangle>>> measure)
    {
        _hidden = new bool[count];
        if (count == 0 || _covers.Count == 0) return [];

        var hide = new List<int>();
        var boxes = measure();
        for (var i = 0; i < count && i < boxes.Count; i++)
        {
            if (!Touches(_covers, boxes[i])) continue;
            _hidden[i] = true;
            hide.Add(i);
        }
        return hide;
    }

    /// <summary>
    /// The windows over the source are now <paramref name="covers"/>. Returns each box whose state
    /// changed and what it changed to; an unchanged box is not in the list.
    /// </summary>
    public IReadOnlyList<(int Index, bool Hidden)> Covered(
        IReadOnlyList<Rectangle> covers, Func<IReadOnlyList<IReadOnlyList<Rectangle>>> measure)
    {
        _covers = covers;
        if (_hidden.Length == 0) return [];

        // Nothing over the source: no need to measure anything to know every box is clear.
        var boxes = covers.Count == 0 ? null : measure();

        var changes = new List<(int, bool)>();
        for (var i = 0; i < _hidden.Length; i++)
        {
            var hidden = boxes is not null && i < boxes.Count && Touches(covers, boxes[i]);
            if (hidden == _hidden[i]) continue;
            _hidden[i] = hidden;
            changes.Add((i, hidden));
        }
        return changes;
    }

    /// <summary>Whether any of a box's drawn areas meets any of the covering windows.</summary>
    public static bool Touches(IReadOnlyList<Rectangle> covers, IReadOnlyList<Rectangle> box) =>
        box.Any(area => covers.Any(cover => cover.IntersectsWith(area)));
}
