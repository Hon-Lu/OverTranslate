using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Overlay;

/// <summary>
/// Draws text with the colour emoji in it in colour.
/// </summary>
/// <remarks>
/// <para>WPF has no colour font support: an emoji typed or pasted into a 文字 mark comes out as the
/// font's one-colour outline, in the mark's colour. The emoji font does carry the colours, as a
/// COLR table — each emoji glyph is a stack of plain glyphs, each with a colour from the CPAL table
/// beside it — so the text is laid out by WPF exactly as before and only the glyphs that have such a
/// stack are swapped for it.</para>
///
/// <para>Laid out by WPF rather than by anything here because the layout is the hard part: which
/// font each character falls back to, and the ligatures that make one glyph of a family, a flag or
/// a skin tone. All of that is already in the glyph runs WPF produces, so the only thing done here
/// is to take those runs apart.</para>
///
/// <para>Only the version 0 part of the table, which every version of Segoe UI Emoji has — the
/// newer gradient form is an addition alongside it, not a replacement, so the flat colours are
/// always there to read.</para>
/// </remarks>
internal static class ColorGlyphText
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The layer palette index that means "the text's own colour".</summary>
    private const ushort ForegroundPaletteIndex = 0xFFFF;

    private static readonly ConcurrentDictionary<Uri, ColorFont?> Fonts = new();

    /// <summary>Draws <paramref name="text"/> with its top-left at <paramref name="origin"/>, emoji in colour.</summary>
    public static void Draw(DrawingContext dc, FormattedText text, Point origin)
    {
        var laid = new DrawingGroup();
        using (var inner = laid.Open()) inner.DrawText(text, origin);

        if (!HasColorGlyphs(laid))
        {
            dc.DrawDrawing(laid);
            return;
        }

        Replay(dc, laid);
    }

    /// <summary>Whether any glyph in the drawing has colour layers. Most text has none, and is drawn as it was.</summary>
    private static bool HasColorGlyphs(Drawing drawing) => drawing switch
    {
        DrawingGroup group => group.Children.Any(HasColorGlyphs),
        GlyphRunDrawing { GlyphRun: { } run } =>
            FontOf(run.GlyphTypeface) is { } font && run.GlyphIndices.Any(font.HasLayers),
        _ => false,
    };

    private static void Replay(DrawingContext dc, Drawing drawing)
    {
        switch (drawing)
        {
            case DrawingGroup group:
            {
                int pushed = 0;
                if (group.Transform is { } transform) { dc.PushTransform(transform); pushed++; }
                if (group.ClipGeometry is { } clip)   { dc.PushClip(clip); pushed++; }
                if (group.Opacity < 1)                { dc.PushOpacity(group.Opacity); pushed++; }

                foreach (var child in group.Children) Replay(dc, child);

                for (int i = 0; i < pushed; i++) dc.Pop();
                break;
            }

            case GlyphRunDrawing { GlyphRun: { } run } glyphs
                when FontOf(run.GlyphTypeface) is { } font && run.GlyphIndices.Any(font.HasLayers):
                DrawLayered(dc, run, glyphs.ForegroundBrush, font);
                break;

            default:
                dc.DrawDrawing(drawing);
                break;
        }
    }

    /// <summary>One glyph at a time, so the ones with colour can be drawn as their stack of layers.</summary>
    private static void DrawLayered(DrawingContext dc, GlyphRun run, Brush foreground, ColorFont font)
    {
        bool rightToLeft = (run.BidiLevel & 1) == 1;
        double pen = run.BaselineOrigin.X;

        for (int i = 0; i < run.GlyphIndices.Count; i++)
        {
            double advance = run.AdvanceWidths[i];
            var offset = run.GlyphOffsets is { Count: > 0 } offsets ? offsets[i] : default;

            double x = rightToLeft ? pen - advance : pen;
            var at = new Point(x + offset.X, run.BaselineOrigin.Y - offset.Y);

            ushort glyph = run.GlyphIndices[i];
            if (font.LayersOf(glyph) is { } layers)
            {
                foreach (var (layerGlyph, color) in layers)
                {
                    var brush = color is { } c ? Frozen(c) : foreground;
                    dc.DrawGlyphRun(brush, Single(run, layerGlyph, at, advance));
                }
            }
            else
            {
                dc.DrawGlyphRun(foreground, Single(run, glyph, at, advance));
            }

            pen += rightToLeft ? -advance : advance;
        }
    }

    private static GlyphRun Single(GlyphRun from, ushort glyph, Point at, double advance) =>
        new(from.GlyphTypeface, 0, false, from.FontRenderingEmSize, from.PixelsPerDip,
            [glyph], at, [advance], null, null, null, null, null, null);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static ColorFont? FontOf(GlyphTypeface typeface) =>
        Fonts.GetOrAdd(typeface.FontUri, _ => ColorFont.TryRead(typeface));

    /// <summary>The COLR and CPAL tables of one font, read once.</summary>
    internal sealed class ColorFont
    {
        private readonly Dictionary<ushort, (ushort Glyph, Color? Color)[]> _layers;

        private ColorFont(Dictionary<ushort, (ushort, Color?)[]> layers) => _layers = layers;

        public bool HasLayers(ushort glyph) => _layers.ContainsKey(glyph);

        /// <summary>The glyph's layers bottom first, each with its colour or null for the text's own.</summary>
        public (ushort Glyph, Color? Color)[]? LayersOf(ushort glyph) =>
            _layers.TryGetValue(glyph, out var layers) ? layers : null;

        public int Count => _layers.Count;

        public static ColorFont? TryRead(GlyphTypeface typeface)
        {
            try
            {
                using var stream = typeface.GetFontStream();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return Parse(copy.ToArray(), FaceIndexOf(typeface.FontUri));
            }
            catch (Exception e)
            {
                Log.Debug(e, "Could not read colour tables of {Font}", typeface.FontUri);
                return null;
            }
        }

        /// <summary>Which face of a collection the URI names — the number after the '#', or the first.</summary>
        private static int FaceIndexOf(Uri uri) =>
            int.TryParse(uri.Fragment.TrimStart('#'), out int index) ? index : 0;

        /// <summary>Reads the tables, or null for a font that has no colour glyphs.</summary>
        internal static ColorFont? Parse(byte[] file, int faceIndex = 0)
        {
            int face = 0;
            if (Tag(file, 0) == "ttcf")
            {
                int faces = (int)U32(file, 8);
                if (faceIndex < 0 || faceIndex >= faces) return null;
                face = (int)U32(file, 12 + faceIndex * 4);
            }

            int? colr = null, cpal = null;
            int tables = U16(file, face + 4);
            for (int i = 0; i < tables; i++)
            {
                int record = face + 12 + i * 16;
                string tag = Tag(file, record);
                if (tag == "COLR") colr = (int)U32(file, record + 8);
                else if (tag == "CPAL") cpal = (int)U32(file, record + 8);
            }
            if (colr is not { } c || cpal is not { } p) return null;

            var palette = ReadFirstPalette(file, p);

            int baseCount   = U16(file, c + 2);
            int baseOffset  = (int)U32(file, c + 4);
            int layerOffset = (int)U32(file, c + 8);
            int layerCount  = U16(file, c + 12);

            var layers = new Dictionary<ushort, (ushort, Color?)[]>(baseCount);
            for (int i = 0; i < baseCount; i++)
            {
                int record = c + baseOffset + i * 6;
                ushort glyph = U16(file, record);
                int first    = U16(file, record + 2);
                int count    = U16(file, record + 4);
                if (count == 0 || first + count > layerCount) continue;

                var stack = new (ushort, Color?)[count];
                for (int j = 0; j < count; j++)
                {
                    int layer = c + layerOffset + (first + j) * 4;
                    ushort index = U16(file, layer + 2);
                    Color? color = index == ForegroundPaletteIndex || index >= palette.Length
                        ? null
                        : palette[index];
                    stack[j] = (U16(file, layer), color);
                }
                layers[glyph] = stack;
            }

            return layers.Count == 0 ? null : new ColorFont(layers);
        }

        private static Color[] ReadFirstPalette(byte[] file, int cpal)
        {
            int entries       = U16(file, cpal + 2);
            int recordsOffset = (int)U32(file, cpal + 8);
            int firstIndex    = U16(file, cpal + 12);

            var colors = new Color[entries];
            for (int i = 0; i < entries; i++)
            {
                int at = cpal + recordsOffset + (firstIndex + i) * 4;
                // Stored blue, green, red, alpha.
                colors[i] = Color.FromArgb(file[at + 3], file[at + 2], file[at + 1], file[at]);
            }
            return colors;
        }

        private static ushort U16(byte[] b, int at) => (ushort)(b[at] << 8 | b[at + 1]);

        private static uint U32(byte[] b, int at) =>
            (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static string Tag(byte[] b, int at) =>
            System.Text.Encoding.ASCII.GetString(b, at, 4);
    }
}
