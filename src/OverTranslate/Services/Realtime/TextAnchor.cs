using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Where the strokes of one piece of text were when it was read, kept so a later frame can be
/// asked whether that text is still there — or is somewhere else now.
/// </summary>
/// <remarks>
/// <para>What is kept is the edges: pairs of pixels two apart, one on a stroke and one beside it,
/// with which of the two was the brighter and by how much. Text is the part of a picture made
/// almost entirely of such pairs, and they are the part of it that survives what happens around
/// it. A subtitle is still there when the scene behind it changes, and so are its edges — a white
/// letter is still brighter than what is beside it. A page that has been scrolled has something
/// else where the letter was, and the edges are not there.</para>
///
/// <para>That is the difference between this and <see cref="ScrollPrint"/>, and why both exist.
/// The print says, exactly and cheaply, that the picture moved and how far. It cannot say that a
/// particular line did not: a line over a picture that scrolls behind it has no row left the same.
/// This can, and it is the only thing that takes a translation off the screen.</para>
/// </remarks>
internal sealed class TextAnchor
{
    // How far apart the two pixels of a pair are. More than the width of the soft edge a rendered
    // glyph has, so the pair spans stroke and background rather than two shades of one edge.
    private const int Gap = 2;

    // Pairs kept for one piece of text. Enough that a share of them is a steady number.
    private const int MaxEdges = 1200;

    // Below this there is not enough here to say anything from.
    private const int MinEdges = 12;

    // Pixels looked at to find the pairs, before thinning.
    private const int MaxSamples = 24000;

    private readonly Edge[] _edges;

    private TextAnchor(Edge[] edges) => _edges = edges;

    private readonly record struct Edge(short X, short Y, bool Down, short Step);

    /// <summary>Whether there was enough contrast in the box to recognise it by.</summary>
    public bool CanJudge => _edges.Length >= MinEdges;

    public static TextAnchor Capture(FramePixels frame, Rectangle box)
    {
        box.Intersect(new Rectangle(0, 0, frame.Width - Gap, frame.Height - Gap));
        if (box.Width <= 0 || box.Height <= 0) return new TextAnchor([]);

        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)box.Width * box.Height / MaxSamples)));
        var found = new List<Edge>();
        var strongest = 0;
        for (var y = box.Top; y < box.Bottom; y += stride)
        for (var x = box.Left; x < box.Right; x += stride)
        {
            var here = frame.Brightness(x, y);
            var across = frame.Brightness(x + Gap, y) - here;
            var down = frame.Brightness(x, y + Gap) - here;
            strongest = Math.Max(strongest, Math.Max(Math.Abs(across), Math.Abs(down)));
            if (Math.Abs(across) >= WeakestEdge) found.Add(new Edge((short)x, (short)y, false, (short)across));
            if (Math.Abs(down) >= WeakestEdge) found.Add(new Edge((short)x, (short)y, true, (short)down));
        }

        // Against the text's own contrast rather than a fixed figure: grey on white and white on
        // black are both text, and the faint steps inside a photograph behind either are not.
        var bar = Math.Max(WeakestEdge, strongest * 0.4);
        found.RemoveAll(edge => Math.Abs(edge.Step) < bar);

        if (found.Count > MaxEdges)
        {
            var thinned = new Edge[MaxEdges];
            for (var i = 0; i < MaxEdges; i++) thinned[i] = found[(int)((long)i * found.Count / MaxEdges)];
            return new TextAnchor(thinned);
        }
        return new TextAnchor([.. found]);
    }

    private const int WeakestEdge = 20;

    /// <summary>
    /// The share of this text's edges found in <paramref name="frame"/> with the text moved by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) from where it was read; null when too little
    /// of it would still be in frame to say.
    /// </summary>
    public double? PresenceAt(FramePixels frame, int dx, int dy)
    {
        if (!CanJudge) return null;

        int judged = 0, present = 0;
        foreach (var edge in _edges)
        {
            int x = edge.X + dx, y = edge.Y + dy;
            int otherX = edge.Down ? x : x + Gap, otherY = edge.Down ? y + Gap : y;
            if (x < 0 || y < 0 || otherX >= frame.Width || otherY >= frame.Height) continue;

            judged++;
            var step = frame.Brightness(otherX, otherY) - frame.Brightness(x, y);
            // The same way round and at least half as strong: an edge that has faded with the
            // scene behind it is still that edge, and one that has turned over is not.
            if (Math.Sign(step) == Math.Sign(edge.Step) && Math.Abs(step) * 2 >= Math.Abs(edge.Step)) present++;
        }

        return judged * 2 >= _edges.Length ? (double)present / judged : null;
    }
}
