using System.Diagnostics;

namespace OverTranslate.Layout;

/// <summary>
/// Which bubbles of a capture overlay are fading in, and how far along each one is, from one build
/// of the overlay to the next.
/// </summary>
/// <remarks>
/// <para>The overlay is drawn again whole whenever anything in it changes, and a bubble drawn again
/// is a new element. A fade begun on the old one would stop there, the bubble jumping to full
/// strength part way in: that is how the last translation of a capture came to appear without
/// fading, when the whole answer arrived in the same frame and drew everything again.</para>
///
/// <para>So each fade is remembered by the bubble's place in the list the overlay is handed — the
/// same for every build of one capture, arrived or pending — and a bubble drawn again before its fade
/// is over carries on from where it was.</para>
/// </remarks>
internal sealed class ArrivalFades(TimeSpan duration)
{
    private readonly Dictionary<int, long> _started = [];

    /// <summary>How far into its fade each bubble is, by place, after a build.</summary>
    /// <param name="arriving">The places whose bubbles this build draws for the first time.</param>
    /// <param name="now">A <see cref="Stopwatch"/> timestamp.</param>
    /// <returns>Only the places still fading; a fade that is over is forgotten.</returns>
    public IReadOnlyDictionary<int, TimeSpan> Next(IEnumerable<int> arriving, long now)
    {
        foreach (var place in arriving) _started[place] = now;

        var fading = new Dictionary<int, TimeSpan>();
        foreach (var (place, started) in _started.ToList())
        {
            var elapsed = Stopwatch.GetElapsedTime(started, now);
            if (elapsed < duration) fading[place] = elapsed;
            else _started.Remove(place);
        }

        return fading;
    }

    /// <summary>Forgets every fade: what is drawn next is another capture.</summary>
    public void Clear() => _started.Clear();
}
