using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FlowDirection = System.Windows.FlowDirection;

// Uses cached real OCR and explicitly supplied translations. No provider/network calls.
internal static class PanelLayoutProbe
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("panel-layout inputs.json translations.json output-directory");
        var captures = JsonSerializer.Deserialize<GroupingPrototype.Capture[]>(File.ReadAllText(args[0]))!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
        Directory.CreateDirectory(args[2]);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var capture in captures)
                {
                    var raw = capture.Blocks.Select(b => b.Restore()).ToList();
                    using var bitmap = new System.Drawing.Bitmap(capture.Image);
                    var blocks = OcrService.GroupRealtime(raw, bitmap.Height, RealtimeBlockMode.Panel);
                    var stem = Path.GetFileNameWithoutExtension(capture.Image);
                    SaveVisual(Path.Combine(args[2], stem + "-Groups.png"), bitmap.Width, bitmap.Height, dc =>
                    {
                        dc.DrawImage(Load(capture.Image), new Rect(0, 0, bitmap.Width, bitmap.Height));
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new Rect(0, 0, bitmap.Width, bitmap.Height));
                        DrawGroups(dc, blocks, raw, fill: true);
                    });
                    Console.WriteLine($"{stem}-Groups: groups={blocks.Count}");
                    if (!blocks.Any(b => translations.ContainsKey(b.Text))) continue;
                    var translated = blocks.Select(b => new TranslatedBlock(b.Text,
                        translations.GetValueOrDefault(b.Text, b.Text), b.Bounds, b.SourceLineBounds, b.RenderGlyphHeight)).ToList();
                    {
                        var window = new RealtimeBlockWindow(0,
                            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), _ => null,
                            capture.Language, capture.Language.StartsWith("ZH") ? "EN" : "ZH-HANT",
                            "#FFFFFF", "#000000", 85, mode: RealtimeBlockMode.Panel);
                        try
                        {
                            var type = typeof(RealtimeBlockWindow);
                            void Set(string field, object value) =>
                                type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                            Set("_dpiX", 1.0); Set("_dpiY", 1.0); Set("_lines", translated);
                            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                            var background = (Canvas)window.FindName("ScrimCanvas");
                            var text = (Canvas)window.FindName("TextCanvas");
                            foreach (var canvas in new[] { background, text })
                            {
                                canvas.Width = bitmap.Width; canvas.Height = bitmap.Height;
                                canvas.Measure(new Size(bitmap.Width, bitmap.Height));
                                canvas.Arrange(new Rect(0, 0, bitmap.Width, bitmap.Height));
                                canvas.UpdateLayout();
                            }
                            var picture = new BitmapImage();
                            picture.BeginInit();
                            picture.UriSource = new Uri(Path.GetFullPath(capture.Image));
                            picture.CacheOption = BitmapCacheOption.OnLoad;
                            picture.EndInit();
                            var visual = new DrawingVisual();
                            using (var dc = visual.RenderOpen())
                            {
                                var area = new Rect(0, 0, bitmap.Width, bitmap.Height);
                                dc.DrawImage(picture, area);
                                foreach (var canvas in new[] { background, text })
                                {
                                    var layer = new RenderTargetBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32);
                                    layer.Render(canvas);
                                    dc.DrawImage(layer, area);
                                }
                            }
                            var target = new RenderTargetBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32);
                            target.Render(visual);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(target));
                            var name = stem + "-Overlay";
                            using (var output = File.Create(Path.Combine(args[2], name + ".png"))) encoder.Save(output);
                            SaveVisual(Path.Combine(args[2], name + "-Debug.png"), bitmap.Width, bitmap.Height, dc =>
                            {
                                dc.DrawImage(target, new Rect(0, 0, bitmap.Width, bitmap.Height));
                                DrawGroups(dc, blocks, raw, fill: false);
                            });
                            var segments = text.Children.Cast<Border>().Select(b =>
                            {
                                var child = b.Child is Viewbox fit ? (TextBlock)fit.Child : (TextBlock)b.Child;
                                return new { Text = child.Text, X = Canvas.GetLeft(b), Y = Canvas.GetTop(b),
                                    b.Width, b.Height, child.FontSize };
                            }).ToArray();
                            File.WriteAllText(Path.Combine(args[2], name + ".json"),
                                JsonSerializer.Serialize(segments, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\r\n"));
                            Console.WriteLine($"{name}: groups={blocks.Count}, suppliedTranslations={blocks.Count(b => translations.ContainsKey(b.Text))}, renderedRows={segments.Length}");
                        }
                        finally { window.Close(); }
                    }
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return 0;
    }

    static readonly Color[] Palette =
    [
        Color.FromRgb(255, 80, 80), Color.FromRgb(80, 200, 255), Color.FromRgb(120, 230, 90), Color.FromRgb(255, 190, 40),
        Color.FromRgb(210, 110, 255), Color.FromRgb(255, 120, 200), Color.FromRgb(60, 230, 200), Color.FromRgb(255, 140, 60),
    ];

    // Group = one translation unit: same color, solid outline around the union, "#n xk" tag when k source lines merged.
    // Raw OCR boxes: dashed white, so boxes that were already separate before grouping stay visible.
    static void DrawGroups(DrawingContext dc, IReadOnlyList<OcrTextBlock> groups, IReadOnlyList<OcrTextBlock> raw, bool fill)
    {
        var dash = new Pen(Brushes.White, 1) { DashStyle = new DashStyle([3, 3], 0) };
        foreach (var box in raw) dc.DrawRectangle(null, dash, box.Bounds);
        var typeface = new Typeface("Segoe UI");
        for (var i = 0; i < groups.Count; i++)
        {
            var color = Palette[i % Palette.Length];
            var lines = groups[i].Lines;
            var solid = new Pen(new SolidColorBrush(color), 2);
            if (fill)
                foreach (var line in lines)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), null, line);
            dc.DrawRectangle(null, solid, groups[i].Bounds);
            if (lines.Count > 1)
                for (var k = 1; k < lines.Count; k++)
                    dc.DrawLine(new Pen(new SolidColorBrush(color), 1),
                        new Point(groups[i].Bounds.Left, lines[k].Top), new Point(groups[i].Bounds.Left + 8, lines[k].Top));
            var tag = new FormattedText(lines.Count > 1 ? $"#{i + 1} x{lines.Count}" : $"#{i + 1}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.Black, 1.0);
            var at = new Point(Math.Max(0, groups[i].Bounds.Right - tag.Width - 4), Math.Max(0, groups[i].Bounds.Top - tag.Height));
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(at.X, at.Y, tag.Width + 4, tag.Height));
            dc.DrawText(tag, new Point(at.X + 2, at.Y));
        }
    }

    static BitmapImage Load(string path)
    {
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.UriSource = new Uri(Path.GetFullPath(path));
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.EndInit();
        return picture;
    }

    static void SaveVisual(string path, int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
