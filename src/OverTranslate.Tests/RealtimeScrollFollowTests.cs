using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// Translations staying on their text while it is scrolled (#390), against rendered frames: what
/// has to be told apart is a page that moved from a picture that changed, and only pixels differ.
/// </summary>
public class RealtimeScrollFollowTests(ITestOutputHelper output)
{
    private const int Width = 900;
    private const int Height = 700;

    private static readonly long Poll = Stopwatch.Frequency * 150 / 1000;

    // A tall page of balloons, as a webtoon is: text on white, artwork between.
    private sealed class Page : IDisposable
    {
        public Bitmap Image { get; } = new(Width, 4000);
        public List<Rectangle> Texts { get; } = [];

        public Page(int seed = 7)
        {
            var random = new Random(seed);
            using var g = Graphics.FromImage(Image);
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 22, FontStyle.Bold);
            for (var y = 40; y < Image.Height - 200; y += 260)
            {
                // Artwork: blocks of colour, never the same twice.
                for (var i = 0; i < 40; i++)
                {
                    using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                    g.FillRectangle(brush, random.Next(Width - 80), y + 90 + random.Next(120), 20 + random.Next(120), 6 + random.Next(30));
                }

                var x = 80 + random.Next(400);
                var words = $"Line {y} says {random.Next(100000)} things";
                g.DrawString(words, font, Brushes.Black, x, y);
                var size = g.MeasureString(words, font);
                Texts.Add(new Rectangle(x + 4, y + 4, (int)size.Width - 8, (int)size.Height - 8));
            }
        }

        public Bitmap View(int top, int left = 0)
        {
            var view = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(view);
            g.DrawImage(Image, new Rectangle(0, 0, Width, Height), new Rectangle(left, top, Width, Height), GraphicsUnit.Pixel);
            return view;
        }

        public List<OcrTextBlock> Read(int top) =>
        [
            .. Texts.Where(text => text.Top >= top && text.Bottom <= top + Height)
                .Select(text => new OcrTextBlock($"t{text.Top}", new System.Windows.Rect(text.X, text.Y - top, text.Width, text.Height)))
        ];

        public void Dispose() => Image.Dispose();
    }

    private sealed class Screen
    {
        public List<IReadOnlyList<TranslatedBlock>> Draws { get; } = [];
        public RealtimeScrollFollow Follow { get; }
        public long Now { get; private set; } = Stopwatch.GetTimestamp();

        public Screen() => Follow = new RealtimeScrollFollow(1, (lines, _) => Draws.Add(lines));

        public IReadOnlyList<TranslatedBlock> Shown => Draws[^1];

        public ScrollPace Poll(Bitmap frame, double polls = 1)
        {
            Now += (long)(RealtimeScrollFollowTests.Poll * polls);
            return Follow.Observe(frame, Now);
        }

        public void Read(long pass, Bitmap frame, List<OcrTextBlock> blocks)
        {
            Follow.Track(pass, blocks, frame);
            Follow.Publish(pass, [.. blocks.Select(block => new TranslatedBlock(block.Text, "譯" + block.Text, block.Bounds))], 0);
        }
    }

    [Fact]
    public void AScrolledPageHidesItsTranslationsAndBringsThemBackWhereTheTextStopped()
    {
        using var page = new Page();
        var screen = new Screen();
        using (var still = page.View(0))
        {
            screen.Poll(still);
            screen.Read(1, still, page.Read(0));
        }
        var before = screen.Shown;
        Assert.True(before.Count >= 2);

        // Scrolling: 90px, then 130px more, each one poll apart.
        using (var moving = page.View(90)) Assert.Equal(ScrollPace.Scrolling, screen.Poll(moving));
        Assert.Empty(screen.Shown);
        using (var moving = page.View(220)) screen.Poll(moving);
        Assert.Empty(screen.Shown);

        // Stopped.
        using (var rest = page.View(220)) Assert.Equal(ScrollPace.Readable, screen.Poll(rest));

        var stillInFrame = before.Where(line => line.Bounds.Top - 220 >= 0).ToList();
        Assert.Equal(stillInFrame.Count, screen.Shown.Count);
        foreach (var (was, now) in stillInFrame.Zip(screen.Shown))
        {
            Assert.Equal(was.TranslatedText, now.TranslatedText);
            Assert.Equal(was.Bounds.Top - 220, now.Bounds.Top);
            Assert.Equal(was.Bounds.Left, now.Bounds.Left);
        }
    }

    [Fact]
    public void AStoppedScrollIsNotTakenFromTheSameFrameHandedOutTwice()
    {
        using var page = new Page();
        var screen = new Screen();
        using var still = page.View(0);
        screen.Poll(still);
        screen.Read(1, still, page.Read(0));

        using var moving = page.View(90);
        screen.Poll(moving);
        // A poll that came early gets the frame the last one got.
        screen.Poll(moving, polls: 0.5);
        Assert.Empty(screen.Shown);
    }

    [Fact]
    public void APageScrolledSidewaysIsFollowedToo()
    {
        using var page = new Page();
        var screen = new Screen();
        using var wide = new Bitmap(Width + 400, Height);
        using (var g = Graphics.FromImage(wide))
        {
            g.Clear(Color.White);
            g.DrawImage(page.Image, 200, 0, new Rectangle(0, 0, Width, Height), GraphicsUnit.Pixel);
        }
        Bitmap View(int left) => wide.Clone(new Rectangle(left, 0, Width, Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        List<OcrTextBlock> Read(int left) => [.. page.Read(0).Select(block =>
            block with { Bounds = new System.Windows.Rect(block.Bounds.X + 200 - left, block.Bounds.Y, block.Bounds.Width, block.Bounds.Height) })
            .Where(block => block.Bounds.Left >= 0 && block.Bounds.Right <= Width)];

        using (var still = View(200))
        {
            screen.Poll(still);
            screen.Read(1, still, Read(200));
        }
        var before = screen.Shown;
        Assert.NotEmpty(before);

        using (var moving = View(140)) screen.Poll(moving);
        Assert.Empty(screen.Shown);
        using (var rest = View(140)) screen.Poll(rest);

        Assert.Equal(before.Count, screen.Shown.Count);
        Assert.All(before.Zip(screen.Shown), pair => Assert.Equal(pair.First.Bounds.Left + 60, pair.Second.Bounds.Left));
    }

    [Fact]
    public void TextCreepingAlongIsFollowedOnTheScreen()
    {
        using var page = new Page();
        var screen = new Screen();
        using (var still = page.View(0))
        {
            screen.Poll(still);
            screen.Read(1, still, page.Read(0));
        }
        var before = screen.Shown;

        // Credits rolling: 4px a poll, never stopping.
        for (var top = 4; top <= 40; top += 4)
        {
            using var frame = page.View(top);
            Assert.Equal(ScrollPace.Readable, screen.Poll(frame));
            var first = screen.Shown.First(line => line.TranslatedText == before[^1].TranslatedText);
            Assert.Equal(before[^1].Bounds.Top - top, first.Bounds.Top);
        }
    }

    [Fact]
    public void ATranslationThatArrivesAfterItsTextMovedIsDrawnWhereTheTextIs()
    {
        using var page = new Page();
        var screen = new Screen();
        var blocks = page.Read(0);
        using (var still = page.View(0))
        {
            screen.Poll(still);
            screen.Follow.Track(1, blocks, still);
        }

        using (var moving = page.View(120)) screen.Poll(moving);
        using (var rest = page.View(120)) screen.Poll(rest);
        screen.Follow.Publish(1, [.. blocks.Select(block => new TranslatedBlock(block.Text, "譯", block.Bounds))], 0);

        Assert.NotEmpty(screen.Shown);
        Assert.All(screen.Shown, line => Assert.Contains(blocks, block => block.Bounds.Top - 120 == line.Bounds.Top));
    }

    [Fact]
    public void TextReadWhileThePageWasMovingStaysOffTheScreenUntilItStops()
    {
        using var page = new Page();
        var screen = new Screen();
        using (var still = page.View(0)) screen.Poll(still);
        using (var moving = page.View(100))
        {
            screen.Poll(moving);
            screen.Read(1, moving, page.Read(100));
        }
        Assert.Empty(screen.Shown);

        using (var further = page.View(180)) screen.Poll(further);
        Assert.Empty(screen.Shown);
        using (var rest = page.View(180)) screen.Poll(rest);
        Assert.NotEmpty(screen.Shown);
        Assert.All(screen.Shown, line => Assert.Contains(page.Read(180), block => block.Bounds.Top == line.Bounds.Top));
    }

    [Fact]
    public void ALineThatStaysPutOverAPictureScrollingBehindItIsLeftAlone()
    {
        using var page = new Page();
        var screen = new Screen();
        var caption = new Rectangle(250, 600, 400, 40);
        Bitmap View(int top)
        {
            var view = page.View(top);
            using var g = Graphics.FromImage(view);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 24, FontStyle.Bold);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString("A caption that stays put", font.FontFamily, (int)FontStyle.Bold, 30, caption.Location, StringFormat.GenericDefault);
            using var pen = new Pen(Color.Black, 4);
            g.DrawPath(pen, path);
            g.FillPath(Brushes.White, path);
            return view;
        }

        using (var still = View(1500))
        {
            screen.Poll(still);
            screen.Read(1, still, [new OcrTextBlock("caption", new System.Windows.Rect(caption.X, caption.Y, caption.Width, caption.Height))]);
        }

        var draws = screen.Draws.Count;
        foreach (var top in new[] { 1540, 1620, 1700, 1700, 1760 })
        {
            using var frame = View(top);
            screen.Poll(frame);
        }
        Assert.Equal(draws, screen.Draws.Count);
    }

    [Fact]
    public void ASubtitleOverAVideoIsNeverMovedOrHidden()
    {
        var random = new Random(3);
        var screen = new Screen();
        var line = new Rectangle(200, 560, 500, 44);
        Bitmap Frame(string words, int pan)
        {
            var frame = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(frame);
            g.Clear(Color.FromArgb(40, 60, 90));
            // A scene that pans, with the noise no two frames of a video are without.
            for (var i = 0; i < 60; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(60 + i * 3 % 190, 80 + i * 7 % 170, 50 + i * 11 % 200));
                g.FillEllipse(brush, (i * 97 + pan) % Width, i * 53 % Height, 90, 60);
            }
            for (var i = 0; i < 6000; i++)
                frame.SetPixel(random.Next(Width), random.Next(Height), Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 26, FontStyle.Bold);
            g.DrawString(words, font, Brushes.White, line.X, line.Y);
            return frame;
        }

        using (var first = Frame("You seem rather dispirited.", 0))
        {
            screen.Poll(first);
            screen.Read(1, first, [new OcrTextBlock("line", new System.Windows.Rect(line.X, line.Y, line.Width, line.Height))]);
        }

        var draws = screen.Draws.Count;
        for (var i = 1; i <= 12; i++)
        {
            // The scene pans throughout; halfway, the subtitle is replaced.
            using var frame = Frame(i < 6 ? "You seem rather dispirited." : "I know, right?", i * 12);
            Assert.Equal(ScrollPace.Readable, screen.Poll(frame));
        }
        Assert.Equal(draws, screen.Draws.Count);
    }

    [Fact]
    public void AChatPushedUpByANewLineKeepsItsTranslationsOnTheirLines()
    {
        var screen = new Screen();
        var lines = Enumerable.Range(0, 40).Select(i => $"user{i * 37 % 91}: message number {i * 7919 % 1000}").ToList();
        const int pitch = 34;
        Bitmap View(int firstLine)
        {
            var view = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(view);
            g.Clear(Color.FromArgb(24, 24, 27));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 16);
            for (var row = 0; row * pitch < Height - pitch; row++)
                g.DrawString(lines[firstLine + row], font, Brushes.Gainsboro, 20, 10 + row * pitch);
            return view;
        }
        List<OcrTextBlock> Read(int firstLine) => [.. Enumerable.Range(0, (Height - pitch) / pitch)
            .Select(row => new OcrTextBlock(lines[firstLine + row], new System.Windows.Rect(24, 14 + row * pitch, 330, 22)))];

        using (var still = View(0))
        {
            screen.Poll(still);
            screen.Read(1, still, Read(0));
        }
        var before = screen.Shown;

        using (var pushed = View(1)) screen.Poll(pushed);
        using (var rest = View(1)) screen.Poll(rest);

        // Every line but the one pushed out, one pitch up, with its own words.
        Assert.Equal(before.Count - 1, screen.Shown.Count);
        foreach (var (was, now) in before.Skip(1).Zip(screen.Shown))
        {
            Assert.Equal(was.TranslatedText, now.TranslatedText);
            Assert.Equal(was.Bounds.Top - pitch, now.Bounds.Top);
        }
    }

    [Fact]
    public void APageThatJumpsFurtherThanCanBeFollowedKeepsWhatItShowedForTheReadingToReplace()
    {
        using var page = new Page();
        var screen = new Screen();
        using (var still = page.View(0))
        {
            screen.Poll(still);
            screen.Read(1, still, page.Read(0));
        }
        var draws = screen.Draws.Count;

        // Nothing of the old frame is in the new one: this is a different picture, not a move.
        using (var elsewhere = page.View(2000)) Assert.Equal(ScrollPace.Readable, screen.Poll(elsewhere));
        Assert.Equal(draws, screen.Draws.Count);
    }

    [Fact]
    public void LookingAtAFrameIsCheap()
    {
        using var page = new Page();
        using var frame = new Bitmap(2560, 1440, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
            for (var y = 0; y < 1440; y += Height)
            for (var x = 0; x < 2560; x += Width)
                g.DrawImage(page.Image, new Rectangle(x, y, Width, Height), new Rectangle(0, y, Width, Height), GraphicsUnit.Pixel);

        var screen = new Screen();
        screen.Poll(frame);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 20; i++) screen.Poll(frame);
        output.WriteLine($"2560x1440, unchanged: {Stopwatch.GetElapsedTime(started).TotalMilliseconds / 20:0.00}ms a poll");
    }
}
