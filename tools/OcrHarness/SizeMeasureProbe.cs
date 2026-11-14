using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;

// What the realtime overlay sizes its text from, line by line, as a table.
//
// 自動 picks a line's font from the height recognition reports for it, and that height is a
// measurement with noise in it: the same typeface on the same screen comes back a fifth taller or
// shorter from one line to the next (see RealtimeTextSizes for the numbers). This prints the
// measurement on its own, so the noise can be read off real frames instead of argued about.
//
// Every frame is read the way a session reads it — realtime detector size, the confidence filter,
// the mode's own grouper — and then read again from a few crops shifted by a pixel or two. The
// shifted reads are the same words in the same typeface, so whatever moves between them is noise by
// construction; the spread between different lines of one frame set is noise plus whatever sizes
// the set really holds.
//
// One row per measurement, tab separated:
//   image  shift  block  row  kind  value  x  y  w  h  glyphs  text
// kind is "glyph" for a block's own glyph height (a Latin source carries one) and "row" for the
// height of one source row's box (what a CJK source is sized from, having no other).
internal static class SizeMeasureProbe
{
    private static readonly (int X, int Y)[] Shifts = [(0, 0), (1, 0), (0, 1), (2, 1), (1, 2), (3, 3)];

    internal static async Task<int> Run(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(argument => argument != "--panel")];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --size-measures [--panel] <lang> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var ocr = new OcrService();

        foreach (var path in args.Skip(1))
        {
            Bitmap whole;
            try { whole = new Bitmap(path); }
            catch (Exception) { continue; }

            using (whole)
            {
                foreach (var (dx, dy) in Shifts)
                {
                    if (whole.Width - dx < 32 || whole.Height - dy < 32) continue;
                    using var frame = whole.Clone(
                        new Rectangle(dx, dy, whole.Width - dx, whole.Height - dy), whole.PixelFormat);
                    var size = RealtimeDetectorSize.For(frame.Width, frame.Height, mode).Primary;
                    var blocks = await ocr.TryRecognizeAsync(frame, language, size, default, mode) ?? [];

                    for (var index = 0; index < blocks.Count; index++)
                    {
                        var block = blocks[index];
                        var glyphs = block.Text.Count(c => !char.IsWhiteSpace(c));
                        if ((block.FontGlyphHeight ?? block.RenderGlyphHeight) is { } glyph)
                            Print(path, dx, dy, index, -1, "glyph", glyph, block.Bounds, glyphs, block.Text);

                        var rows = block.Lines;
                        for (var row = 0; row < rows.Count; row++)
                            Print(path, dx, dy, index, row, "row", rows[row].Height, rows[row], glyphs, block.Text);
                    }
                }
            }
        }

        return 0;
    }

    // The same frames as a session draws them: one block window for the whole run, fed each frame's
    // reading in turn, because what 自動 does to a line depends on the lines that window has shown
    // before it. A window built per frame — which is what --panel-layout does — would show every
    // frame as the first of its session and none of what a reader actually sits through.
    //
    // Recognition is the real one; the translations are handed in ({ "source": "translation" }, a
    // line without one is drawn as its source), since what is being looked at is the size the
    // text is set at and not what it says. Out comes one picture with the frames stacked in order,
    // each drawn over its own capture, and a table of the sizes on the console.
    internal static async Task<int> RunSession(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(argument => argument != "--panel")];
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: --size-session [--panel] <lang> <translations.json> <out.png> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;

        var frames = new List<(string Path, int Width, int Height, List<TranslatedBlock> Lines)>();
        using (var ocr = new OcrService())
        {
            foreach (var path in args.Skip(3))
            {
                using var frame = new Bitmap(path);
                var size = RealtimeDetectorSize.For(frame.Width, frame.Height, mode).Primary;
                var blocks = await ocr.TryRecognizeAsync(frame, language, size, default, mode) ?? [];
                if (blocks.Count == 0) continue;
                frames.Add((path, frame.Width, frame.Height, [.. blocks.Select(block =>
                    new TranslatedBlock(
                        block.Text, translations.GetValueOrDefault(block.Text, block.Text), block.Bounds,
                        block.SourceLineBounds, block.RenderGlyphHeight)
                    {
                        FontGlyphHeight = block.FontGlyphHeight,
                    })]));
                foreach (var block in blocks.Where(block => !translations.ContainsKey(block.Text)))
                    Console.WriteLine($"UNTRANSLATED\t{block.Text}");
            }
        }

        if (frames.Count == 0) return 1;

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                int width = frames.Max(frame => frame.Width);
                var sheet = new DrawingVisual();
                var window = new RealtimeBlockWindow(0,
                    new Rectangle(0, 0, frames[0].Width, frames[0].Height), _ => null,
                    language, "ZH-HANT", "#FFFFFF", "#000000", 85, mode: mode);
                double top = 0;
                try
                {
                    var type = typeof(RealtimeBlockWindow);
                    void Set(string field, object value) =>
                        type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                    Set("_dpiX", 1.0); Set("_dpiY", 1.0);

                    using var dc = sheet.RenderOpen();
                    foreach (var (path, frameWidth, frameHeight, lines) in frames)
                    {
                        Set("_lines", lines);
                        type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

                        var picture = new BitmapImage();
                        picture.BeginInit();
                        picture.UriSource = new Uri(Path.GetFullPath(path));
                        picture.CacheOption = BitmapCacheOption.OnLoad;
                        picture.EndInit();
                        var area = new System.Windows.Rect(0, top, frameWidth, frameHeight);
                        dc.DrawImage(picture, area);

                        var text = (Canvas)window.FindName("TextCanvas");
                        foreach (var canvas in new[] { (Canvas)window.FindName("ScrimCanvas"), text })
                        {
                            canvas.Width = frameWidth; canvas.Height = frameHeight;
                            canvas.Measure(new System.Windows.Size(frameWidth, frameHeight));
                            canvas.Arrange(new System.Windows.Rect(0, 0, frameWidth, frameHeight));
                            canvas.UpdateLayout();
                            var layer = new RenderTargetBitmap(frameWidth, frameHeight, 96, 96, PixelFormats.Pbgra32);
                            layer.Render(canvas);
                            dc.DrawImage(layer, area);
                        }

                        var sizes = text.Children.Cast<Border>()
                            .Select(box => box.Child is Viewbox fit ? (TextBlock)fit.Child : (TextBlock)box.Child)
                            .Select(drawn => drawn.FontSize.ToString("0.0", CultureInfo.InvariantCulture));
                        Console.WriteLine(string.Join('\t',
                            Path.GetFileName(path),
                            string.Join(' ', sizes),
                            string.Join(' ', lines.Select(line =>
                                (line.FontGlyphHeight ?? line.RenderGlyphHeight ?? line.Bounds.Height)
                                    .ToString("0.0", CultureInfo.InvariantCulture)))));
                        top += frameHeight + 4;
                    }
                }
                finally { window.Close(); }

                var target = new RenderTargetBitmap(width, (int)Math.Ceiling(top), 96, 96, PixelFormats.Pbgra32);
                target.Render(sheet);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using var output = File.Create(args[2]);
                encoder.Save(output);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return 0;
    }

    private static void Print(
        string path, int dx, int dy, int block, int row, string kind, double value,
        System.Windows.Rect box, int glyphs, string text) =>
        Console.WriteLine(string.Join('\t',
            Path.GetFileName(path), $"{dx},{dy}", block, row, kind,
            value.ToString("0.00", CultureInfo.InvariantCulture),
            (box.X + dx).ToString("0.0", CultureInfo.InvariantCulture),
            (box.Y + dy).ToString("0.0", CultureInfo.InvariantCulture),
            box.Width.ToString("0.0", CultureInfo.InvariantCulture),
            box.Height.ToString("0.0", CultureInfo.InvariantCulture),
            glyphs, text.Replace('\t', ' ').Replace('\n', ' ')));
}
