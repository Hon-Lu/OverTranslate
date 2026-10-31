using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Emoji in a 文字 mark come out in their own colours (#373), not as an outline in the mark's colour,
/// and text without them is drawn exactly as before.
/// </summary>
public class AnnotationColorEmojiTests
{
    private static readonly Rect Surface = new(0, 0, 400, 200);

    private static string EmojiFontPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");

    private static AnnotationStroke Text(string text, Color color) => new()
    {
        Tool      = AnnotationTool.Text,
        Color     = color,
        Thickness = 0,
        Opacity   = 1,
        Points    = [new Point(20, 20)],
        Text      = text,
        TextStyle = new AnnotationTextStyle(new FontFamily("Segoe UI"), 64, false, false, false, false),
    };

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>The distinct colours of the solid pixels laid down, rounded so anti-aliasing does not count.</summary>
    private static HashSet<int> SolidColours(AnnotationStroke stroke)
    {
        var surface = new InkSurface();
        surface.Ensure(Surface, 1);
        surface.Lay(stroke);
        surface.Flush();

        var bitmap = (BitmapSource)surface.Source!;
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);

        var colours = new HashSet<int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 250) continue;
            colours.Add((pixels[i + 2] >> 5) << 6 | (pixels[i + 1] >> 5) << 3 | pixels[i] >> 5);
        }
        return colours;
    }

    [Fact]
    public void EmojiFont_HasColourLayers()
    {
        if (!File.Exists(EmojiFontPath)) return;

        var font = ColorGlyphText.ColorFont.Parse(File.ReadAllBytes(EmojiFontPath));

        Assert.NotNull(font);
        Assert.True(font!.Count > 1000, $"only {font.Count} colour glyphs");
    }

    [Fact]
    public void PlainFont_HasNoColourLayers()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (!File.Exists(path)) return;

        Assert.Null(ColorGlyphText.ColorFont.Parse(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Emoji_IsDrawnInItsOwnColours()
    {
        if (!File.Exists(EmojiFontPath)) return;

        OnStaThread(() =>
        {
            // A grinning face drawn in black: in colour it is mostly yellow, which black is not.
            var colours = SolidColours(Text("\U0001F600", Colors.Black));

            Assert.True(colours.Count >= 3, $"only {colours.Count} solid colours");
            Assert.Contains(colours, c => c >> 6 >= 6 && (c >> 3 & 7) >= 5 && (c & 7) <= 3);
        });
    }

    [Fact]
    public void JoinedEmoji_StaysOneGlyph()
    {
        if (!File.Exists(EmojiFontPath)) return;

        OnStaThread(() =>
        {
            // A family is one ligature in the font. Drawn as its parts it would be four faces side by
            // side, far wider than the single glyph.
            var stroke = Text("\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466", Colors.Black);
            var single = Text("\U0001F600", Colors.Black);

            double familyWidth = stroke.TextStyle!.Layout(stroke.Text!, Colors.Black, 1).Width;
            double faceWidth   = single.TextStyle!.Layout(single.Text!, Colors.Black, 1).Width;

            Assert.True(familyWidth < faceWidth * 1.5, $"family {familyWidth} vs face {faceWidth}");
            Assert.True(SolidColours(stroke).Count >= 3);
        });
    }

    [Fact]
    public void TextWithoutEmoji_KeepsTheMarkColour()
    {
        OnStaThread(() =>
        {
            var colours = SolidColours(Text("Water", Colors.Red));

            Assert.Single(colours);
            Assert.Equal(7 << 6, colours.Single());
        });
    }
}
