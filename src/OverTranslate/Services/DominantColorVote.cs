using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Services;

/// <summary>
/// Picks the colour most of the glyph pixels share, rather than the average of all of them.
/// </summary>
/// <remarks>
/// A line is not always one colour: a chat line puts the speaker's name in one colour and the message
/// in another, a dialogue line highlights a keyword. Averaging those pixels gives a colour neither
/// part was drawn in — white with a few yellow words comes back cream, white with a red name comes
/// back pink. The overlay can only draw one colour per block, so the one that covers most of the text
/// is the one to keep.
///
/// <para>Pixels are voted into coarse buckets (8 levels per channel) so antialiasing and gradients
/// do not split one colour into many small ones, and the winner is the mean of its own bucket.</para>
/// </remarks>
internal sealed class DominantColorVote
{
    private const int Shift = 5;
    private const int Levels = 256 >> Shift;

    private readonly int[] _count = new int[Levels * Levels * Levels];
    private readonly long[] _r = new long[Levels * Levels * Levels];
    private readonly long[] _g = new long[Levels * Levels * Levels];
    private readonly long[] _b = new long[Levels * Levels * Levels];

    public void Add(byte r, byte g, byte b)
    {
        int key = ((r >> Shift) * Levels + (g >> Shift)) * Levels + (b >> Shift);
        _count[key]++;
        _r[key] += r;
        _g[key] += g;
        _b[key] += b;
    }

    /// <summary>The mean colour of the most-voted bucket, or null when nothing was added.</summary>
    public MediaColor? Dominant()
    {
        int best = -1;
        for (int key = 0; key < _count.Length; key++)
        {
            if (_count[key] > 0 && (best < 0 || _count[key] > _count[best]))
                best = key;
        }

        if (best < 0)
            return null;

        int n = _count[best];
        return MediaColor.FromRgb((byte)(_r[best] / n), (byte)(_g[best] / n), (byte)(_b[best] / n));
    }
}
