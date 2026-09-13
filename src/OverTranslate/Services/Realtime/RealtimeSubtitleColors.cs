using System.Globalization;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The two colours a realtime subtitle is drawn in, and the rules for reading them back out of the
/// settings file.
/// </summary>
/// <remarks>
/// The text colour has no alpha to speak of — it is the thing being read, so it is always fully
/// opaque. The scrim carries one, and it is the user's.
///
/// It was fixed here at first, on the argument that the band exists to hide the source line
/// underneath and a sheer one leaves the reader with two overlapping sentences. That argument is
/// still right about subtitles burnt into a video, and wrong about everything else this feature is
/// pointed at: a dialogue box in a game is drawn on its own opaque panel, and there the band is
/// covering artwork rather than text. One value cannot serve both, and the person who can see the
/// screen is the one who knows which they are looking at.
/// </remarks>
public static class RealtimeSubtitleColors
{
    public const string DefaultText = "#FAFAFA";
    public const string DefaultScrim = "#000000";

    /// <summary>
    /// The scrim's opacity as a percentage — 0 for none at all, 100 for a solid band.
    /// </summary>
    /// <remarks>
    /// Stored as a percentage rather than as the alpha byte it becomes, because appsettings.json is a
    /// file people open and edit, and <c>72</c> is a number they can reason about where <c>184</c> is
    /// not. The rounding back and forth is lossy in the last bit and nothing here cares: the eye
    /// cannot see one step of alpha, and nothing compares two scrims for equality.
    ///
    /// The floor really is zero. It lets the band be turned off entirely, which over a video means
    /// the original subtitle showing through the translation — the failure the fixed value existed to
    /// prevent. It is offered anyway: the preview on 顯示外觀 shows exactly that happening before the
    /// session starts, and refusing the setting would also refuse the game panel it was released for.
    /// </remarks>
    public const int MinScrimOpacity = 0;

    /// <inheritdoc cref="MinScrimOpacity"/>
    public const int MaxScrimOpacity = 100;

    /// <summary>
    /// Opaque enough to hide a subtitle, sheer enough to keep the picture behind it — what the band
    /// was fixed at before it was offered, and so what it still starts at.
    /// </summary>
    public const int DefaultScrimOpacity = 72;

    /// <summary>The translated text's colour, always fully opaque.</summary>
    public static Color Text(string? hex) => Parse(hex) ?? Parse(DefaultText)!.Value;

    /// <summary>The band behind the text, at the given opacity.</summary>
    public static Color Scrim(string? hex, int opacity)
    {
        var rgb = Parse(hex) ?? Parse(DefaultScrim)!.Value;
        return Color.FromArgb(ScrimAlpha(opacity), rgb.R, rgb.G, rgb.B);
    }

    /// <summary>The alpha channel a stored opacity comes to, clamping anything out of range.</summary>
    /// <remarks>
    /// Clamped rather than rejected for the same reason <see cref="Parse"/> returns null rather than
    /// throwing: a hand-edited <c>150</c> should cost the user the difference between what they typed
    /// and full opacity, not their subtitles.
    /// </remarks>
    public static byte ScrimAlpha(int opacity) =>
        (byte)Math.Round(ClampOpacity(opacity) * 255.0 / MaxScrimOpacity);

    /// <inheritdoc cref="ScrimAlpha"/>
    public static int ClampOpacity(int opacity) =>
        Math.Clamp(opacity, MinScrimOpacity, MaxScrimOpacity);

    /// <summary>The theme's accent blue, which a fixed border starts at.</summary>
    public const string DefaultBorder = "#52C4FA";

    /// <summary>How thick the border around the band is drawn. Not a setting.</summary>
    public const double BorderThickness = 2;

    // Bright enough to stay visible against a dark band, and far enough apart to tell neighbours apart.
    private static readonly Color[] RandomBorders =
    [
        Color.FromRgb(0x52, 0xC4, 0xFA), Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0x7B, 0xE0, 0x7B),
        Color.FromRgb(0xFF, 0xC2, 0x4B), Color.FromRgb(0xC9, 0x8B, 0xFF), Color.FromRgb(0xFF, 0x8F, 0xD1),
        Color.FromRgb(0x4F, 0xE3, 0xC1), Color.FromRgb(0xFF, 0x9F, 0x55),
    ];

    /// <summary>The fixed border colour, always fully opaque.</summary>
    public static Color Border(string? hex) => Parse(hex) ?? Parse(DefaultBorder)!.Value;

    /// <summary>
    /// The colour 隨機 gives a line. Chosen from its source text rather than drawn fresh, so a line
    /// keeps its colour however often it is rebuilt, and every row of one group shares it.
    /// </summary>
    public static Color RandomBorder(string? key)
    {
        uint hash = 2166136261;
        foreach (var character in key ?? "")
            hash = (hash ^ character) * 16777619;
        return RandomBorders[hash % (uint)RandomBorders.Length];
    }

    /// <summary>Back to the form stored in the settings file.</summary>
    public static string Format(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Null for anything unreadable rather than throwing. appsettings.json is a file a user can open
    /// and edit, and a typo in a colour should cost them that colour — not the subtitles, and
    /// certainly not the session.
    /// </summary>
    /// <remarks>
    /// An eight-digit value keeps its RGB and loses its alpha instead of being rejected: someone who
    /// writes one is asking for that colour, and the alpha it carries is not where this reads one —
    /// see <see cref="MinScrimOpacity"/> for the key that is.
    /// </remarks>
    private static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        var digits = hex.Trim().TrimStart('#');
        if (digits.Length == 8) digits = digits[2..];
        if (digits.Length != 6) return null;

        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;

        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
