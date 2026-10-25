using Color = System.Windows.Media.Color;

namespace OverTranslate.Controls;

/// <summary>
/// A colour as the picker handles it: hue around the strip, saturation across the square, value up
/// it.
/// </summary>
/// <remarks>
/// <para>The picker keeps one of these rather than reading the hue back off the colour every time,
/// because a grey has no hue: dragged down to black or across to white, the colour forgets which hue
/// it came from, and the strip would jump back to red under the user's hand. The hue the user set is
/// kept until they set another.</para>
///
/// <para>Opaque only. Nothing the picker serves has an alpha of its own to set — the band's weight is
/// 底色不透明度, a separate value; see <see cref="Services.Realtime.RealtimeSubtitleColors"/>.</para>
/// </remarks>
/// <param name="Hue">0 up to, not including, 360.</param>
/// <param name="Saturation">0 to 1.</param>
/// <param name="Value">0 to 1.</param>
public readonly record struct HsvColor(double Hue, double Saturation, double Value)
{
    public static HsvColor FromColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * ((g - b) / delta % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }

        return new HsvColor(hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>
    /// The same colour, keeping <paramref name="hue"/> when the colour itself has none to give — a
    /// grey, white or black arriving from the HEX box or a swatch.
    /// </summary>
    public static HsvColor FromColor(Color color, double hue)
    {
        var hsv = FromColor(color);
        return hsv.Saturation == 0 ? hsv with { Hue = hue } : hsv;
    }

    public Color ToColor()
    {
        double hue = ((Hue % 360) + 360) % 360;
        double saturation = Math.Clamp(Saturation, 0, 1);
        double value = Math.Clamp(Value, 0, 1);

        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - chroma;

        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        return Color.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    /// <summary>The fully saturated, fully bright colour at this hue: what the square fades from.</summary>
    public Color PureHue() => new HsvColor(Hue, 1, 1).ToColor();

    private static byte Channel(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}
