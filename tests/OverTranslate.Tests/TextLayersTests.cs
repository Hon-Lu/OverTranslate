using System.Drawing;
using System.Drawing.Drawing2D;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Tests;

public class TextLayersTests
{
    /// <summary>A line of bold text, optionally with an outline of its own, drawn over a background.</summary>
    private static Bitmap Line(Color background, Color body, Color? outline, float edge = 4, Action<Graphics>? under = null)
    {
        var frame = new Bitmap(260, 90);
        using var g = Graphics.FromImage(frame);
        g.Clear(background);
        under?.Invoke(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        using var family = new FontFamily("Arial");
        path.AddString("HOME DOG", family, (int)FontStyle.Bold, 40, new PointF(20, 22), StringFormat.GenericTypographic);
        if (outline is { } colour)
        {
            using var pen = new Pen(colour, edge * 2) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var fill = new SolidBrush(body);
        g.FillPath(fill, path);
        return frame;
    }

    /// <summary>The recognition box: the glyph bodies, which is what a detector draws its box around.</summary>
    private static readonly WpfRect Box = new(18, 22, 222, 42);

    private static bool Near(System.Windows.Media.Color? c, Color expected, int tolerance = 60) =>
        c is { } v && Math.Abs(v.R - expected.R) + Math.Abs(v.G - expected.G) + Math.Abs(v.B - expected.B) <= tolerance;

    [Fact]
    public void WhiteTextWithABlackEdge_IsWhiteWithABlackOutline()
    {
        // The subtitle case: the edge stands further from the scene than the body does and used to
        // be read as the text.
        using var frame = Line(Color.FromArgb(150, 120, 90), Color.White, Color.Black);

        var sample = SourceTextColorSampler.Sample(frame, Box);

        Assert.True(Near(sample?.Text, Color.White), $"text {sample?.Text}");
        Assert.True(Near(sample?.Outline, Color.Black), $"outline {sample?.Outline}");
    }

    [Fact]
    public void WhiteTextWithABlackEdge_OverAWhiteScene_IsStillWhiteWithABlackOutline()
    {
        // The body is the scene's own colour, so only its being enclosed by the edge tells it apart
        // from a black line of text with white counters.
        using var frame = Line(Color.FromArgb(245, 248, 250), Color.White, Color.Black);

        var sample = SourceTextColorSampler.Sample(frame, Box);

        Assert.True(Near(sample?.Text, Color.White), $"text {sample?.Text}");
        Assert.True(Near(sample?.Outline, Color.Black), $"outline {sample?.Outline}");
    }

    [Fact]
    public void PlainBlackText_HasNoOutline()
    {
        using var frame = Line(Color.White, Color.Black, null);

        var sample = SourceTextColorSampler.Sample(frame, Box);

        Assert.True(Near(sample?.Text, Color.Black), $"text {sample?.Text}");
        Assert.Null(sample?.Outline);
    }

    [Fact]
    public void PlainWhiteTextOnBlack_HasNoOutline_ItsCountersAreNotABody()
    {
        using var frame = Line(Color.Black, Color.White, null);

        var sample = SourceTextColorSampler.Sample(frame, Box);

        Assert.True(Near(sample?.Text, Color.White), $"text {sample?.Text}");
        Assert.Null(sample?.Outline);
    }

    [Fact]
    public void WhiteTextWithADarkEdge_OnAnOrangeBadge_KeepsTheWhite()
    {
        // The dark edge sits in the counters too, but most of it is outside the glyphs, touching the
        // badge: that is an edge, not a body enclosed by the white.
        using var frame = Line(Color.FromArgb(240, 140, 30), Color.White, Color.FromArgb(70, 75, 80), 2);

        var sample = SourceTextColorSampler.Sample(frame, Box);

        Assert.True(Near(sample?.Text, Color.White), $"text {sample?.Text}");
    }

    [Fact]
    public void BlackTextInAWhiteBalloon_OverABlackPanel_IsBlackOnWhite()
    {
        // The ring reaches past the balloon onto the panel, and the balloon used to be read as the
        // text: white on black, drawn over a balloon that is white.
        using var frame = Line(Color.Black, Color.Black, null,
            under: g => g.FillEllipse(Brushes.White, -6, 2, 272, 86));

        var sample = SourceTextColorSampler.Sample(frame, new WpfRect(12, 16, 236, 56));

        Assert.True(Near(sample?.Background, Color.White), $"background {sample?.Background}");
        Assert.True(Near(sample?.Text, Color.Black), $"text {sample?.Text}");
        Assert.Null(sample?.Outline);
    }

    [Fact]
    public void Realtime_TunesAnOutlinedBodyAgainstItsOutline_NotAgainstTheScene()
    {
        // Tuned against a bright scene the white body would be pulled down to grey; the outline is
        // drawn around the translation, so it is what the text is read against.
        using var frame = Line(Color.FromArgb(245, 248, 250), Color.White, Color.Black);

        var sampled = RealtimeNaturalBackground.SampleText(frame, Box, System.Windows.Media.Colors.Lime);

        Assert.True(sampled.Text.R > 220 && sampled.Text.G > 220 && sampled.Text.B > 220, $"text {sampled.Text}");
        Assert.True(Near(sampled.Outline, Color.Black), $"outline {sampled.Outline}");
    }

    [Fact]
    public void Capture_WritesAnOutlinedSourceInWhicheverOfItsColoursReadsOnTheCard()
    {
        // No outline on a card: white on a near-white card cannot be read, the black edge can.
        using var bright = Line(Color.FromArgb(245, 248, 250), Color.White, Color.Black);
        var (_, onBright) = SourceTextColorSampler.ForCaptureOverlay(bright, Box);
        Assert.True(OverlayTextColor.PerceivedLuminance(onBright) < .3, $"on bright {onBright}");

        // On a mid-dark scene the white body reads, and it is what the source was written in.
        using var dark = Line(Color.FromArgb(40, 70, 120), Color.White, Color.Black);
        var (_, onDark) = SourceTextColorSampler.ForCaptureOverlay(dark, Box);
        Assert.True(OverlayTextColor.PerceivedLuminance(onDark) > .8, $"on dark {onDark}");
    }
}
