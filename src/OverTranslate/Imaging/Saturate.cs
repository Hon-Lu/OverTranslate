using System.Runtime.CompilerServices;

namespace OverTranslate.Imaging;

/// <summary>
/// Conversions to a narrower type that clamp instead of wrapping, rounding half to even — the
/// rounding every routine here inherits from the library it replaced, and the one the results were
/// tuned on.
/// </summary>
internal static class Saturate
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(float value)
    {
        // A NaN comparison is false both ways, and the cast of NaN to int is unspecified, so it is
        // answered first: the replaced library turned it into zero.
        if (!(value > -1f)) return 0;
        if (value >= 255.5f) return 255;
        int rounded = (int)MathF.Round(value, MidpointRounding.ToEven);
        return (byte)Math.Clamp(rounded, 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(double value)
    {
        if (!(value > -1d)) return 0;
        if (value >= 255.5d) return 255;
        int rounded = (int)Math.Round(value, MidpointRounding.ToEven);
        return (byte)Math.Clamp(rounded, 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), int.MinValue, int.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ToShort(float value) =>
        (short)Math.Clamp((int)Math.Clamp(MathF.Round(value, MidpointRounding.ToEven), int.MinValue, int.MaxValue), short.MinValue, short.MaxValue);
}
