using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

/// <summary>
/// A real WGC/CPU/WPF pipeline against the probe's own moving source window. Deliberately separate
/// from the production capture cadence: no 120ms readback throttle or OCR poll drives this stream.
/// </summary>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class LiveTextureProbe
{
    private sealed record Sample(int Frame, double ReadbackMs, double RepairMs,
        double CaptureToSubmissionMs, double SourceToSubmissionMs, double ProcessingToSubmissionMs, long AllocatedBytes, int TemporalPixels);

    public static void Run(string output, int seconds)
    {
        Directory.CreateDirectory(output);
        var frames = new ImageSource[16];
        for (int i = 0; i < frames.Length; i++)
        {
            using var sheet = new Bitmap(Path.Combine(output, $"game-crystals-{i:D2}.png"));
            using var crop = sheet.Clone(new Rectangle(0, 32, 640, 220), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            crop.SetResolution(96, 96);
            frames[i] = BitmapInterop.ToBitmapSource(crop);
        }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var scene = new Image { Source = frames[0], Stretch = Stretch.Fill };
        var marker = new System.Windows.Shapes.Rectangle
        {
            Width = 8, Height = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Fill = Brushes.Black,
        };
        var sourceGrid = new Grid();
        sourceGrid.Children.Add(scene);
        sourceGrid.Children.Add(marker);
        var source = new Window
        {
            Title = "Texture background probe — closes automatically", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, Width = 640, Height = 220,
            Background = Brushes.Black, Content = sourceGrid, ShowActivated = false,
        };
        var overlayImage = new Image { Stretch = Stretch.Fill };
        var text = new TextBlock
        {
            Text = "保留複雜背景紋理，讓譯文融入場景。", Foreground = Brushes.White,
            FontSize = 24, FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 1 },
        };
        var overlayGrid = new Grid();
        overlayGrid.Children.Add(overlayImage);
        overlayGrid.Children.Add(text);
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            ShowActivated = false, Width = 640, Height = 220, Content = overlayGrid,
        };
        var physical = new Rectangle(120, 160, 640, 220);
        source.SourceInitialized += (_, _) => ScreenGeometry.PinPhysicalBounds(source, physical);
        overlay.SourceInitialized += (_, _) =>
        {
            ScreenGeometry.PinPhysicalBounds(overlay, physical);
            WindowStyles.ApplyClickThrough(overlay, noActivate: true);
        };
        source.Show();
        overlay.Show();
        var handle = new WindowInteropHelper(source).Handle;
        var item = WgcInterop.CreateItemForWindow(handle) ?? throw new InvalidOperationException("WGC source unavailable.");
        using var device = WgcInterop.CreateDirect3DDevice(MonitorFromWindow(handle, 2), false,
            out var rawDevice, out var context, out var adapter);
        using var reader = new WgcSurfaceReader(rawDevice, context);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        using var cancellation = new CancellationTokenSource();
        var samples = new List<Sample>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var submitted = new long[65536];
        var engine = new RealtimeTextureBackground();
        var captureGate = new object();
        int busy = 0, disposed = 0, sequence = 0, received = 0, skipped = 0;
        long lastRead = 0;
        Task? worker = null;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            int n = ++sequence;
            int phase = n % 32;
            scene.Source = frames[phase < 16 ? phase : 31 - phase];
            marker.Fill = new SolidColorBrush(Color.FromRgb(127, (byte)n, (byte)(n >> 8)));
            Volatile.Write(ref submitted[n & 65535], Stopwatch.GetTimestamp());
        };
        timer.Start();
        pool.FrameArrived += OnFrame;
        void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            lock (captureGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try
                {
                    Direct3D11CaptureFrame? latest = sender.TryGetNextFrame();
                    if (latest is null) return;
                    // Drain the tiny pool; processing always starts from its newest frame.
                    while (sender.TryGetNextFrame() is { } newer) { latest.Dispose(); latest = newer; }
                    using var frame = latest;
                    Interlocked.Increment(ref received);
                    if (Stopwatch.GetElapsedTime(lastRead).TotalMilliseconds < 50 ||
                        Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                    { Interlocked.Increment(ref skipped); return; }
                    lastRead = Stopwatch.GetTimestamp();
                    var bitmap = reader.Read(frame.Surface, frame.ContentSize.Width, frame.ContentSize.Height);
                    long processingStarted = lastRead;
                    double readback = Stopwatch.GetElapsedTime(processingStarted).TotalMilliseconds;
                    double capturedAtMs = frame.SystemRelativeTime.TotalMilliseconds;
                    var code = bitmap.GetPixel(2, 2);
                    int frameNumber = code.G + (code.B << 8);
                    long sourceAt = Volatile.Read(ref submitted[frameNumber]);
                    worker = Task.Run(() =>
                    {
                        bool dispatched = false;
                        try
                        {
                            using (bitmap)
                            {
                                long before = GC.GetAllocatedBytesForCurrentThread();
                                using var repaired = engine.Repair(bitmap, [new Rect(43, 90, 510, 30)], cancellation.Token);
                                var image = BitmapInterop.ToBitmapSource(repaired.Overlay);
                                long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
                                if (cancellation.IsCancellationRequested) return;
                                app.Dispatcher.BeginInvoke(() =>
                                {
                                    try
                                    {
                                        if (cancellation.IsCancellationRequested) return;
                                        overlayImage.Source = image;
                                        double nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                                        samples.Add(new(frameNumber, readback, repaired.Stats.Milliseconds,
                                            nowMs - capturedAtMs,
                                            sourceAt == 0 ? double.NaN : Stopwatch.GetElapsedTime(sourceAt).TotalMilliseconds,
                                            Stopwatch.GetElapsedTime(processingStarted).TotalMilliseconds,
                                            allocation, repaired.Stats.TemporalPixels));
                                    }
                                    finally { Volatile.Write(ref busy, 0); }
                                }, DispatcherPriority.Render);
                                dispatched = true;
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { errors.Enqueue(ex.ToString()); }
                        finally { if (!dispatched) Volatile.Write(ref busy, 0); }
                    });
                }
                catch (Exception ex) { errors.Enqueue(ex.ToString()); Volatile.Write(ref busy, 0); }
            }
        }
        var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        end.Tick += (_, _) => { end.Stop(); source.Close(); };
        source.Closed += (_, _) =>
        {
            timer.Stop();
            cancellation.Cancel();
            lock (captureGate)
            {
                Volatile.Write(ref disposed, 1);
                pool.FrameArrived -= OnFrame;
            }
            overlay.Close();
            app.Shutdown();
        };
        using var process = Process.GetCurrentProcess();
        var cpuStarted = process.TotalProcessorTime;
        long wallStarted = Stopwatch.GetTimestamp();
        session.StartCapture();
        end.Start();
        app.Run();
        worker?.Wait(TimeSpan.FromSeconds(2));
        double wallSeconds = Stopwatch.GetElapsedTime(wallStarted).TotalSeconds;
        double cpuSeconds = (process.TotalProcessorTime - cpuStarted).TotalSeconds;
        process.Refresh();
        var sorted = samples.Select(s => s.CaptureToSubmissionMs).Order().ToArray();
        var sourceAges = samples.Select(s => s.SourceToSubmissionMs).Where(double.IsFinite).Order().ToArray();
        var processing = samples.Select(s => s.ProcessingToSubmissionMs).Order().ToArray();
        static double Percentile(double[] values, double fraction) =>
            values.Length == 0 ? double.NaN : values[(int)((values.Length - 1) * fraction)];
        var report = new
        {
            scope = "Real WGC capture of probe-owned 640x220 moving source, repair, bitmap conversion and WPF submission. Excludes OCR/translation and final display scanout.",
            adapter, seconds, received, skipped, samples = samples.Count,
            medianSourceToSubmissionMs = Percentile(sourceAges, .5),
            p95SourceToSubmissionMs = Percentile(sourceAges, .95),
            maxSourceToSubmissionMs = Percentile(sourceAges, 1),
            medianProcessingToSubmissionMs = Percentile(processing, .5),
            p95ProcessingToSubmissionMs = Percentile(processing, .95),
            cpuCoreEquivalent = cpuSeconds / wallSeconds,
            totalCpuPercent = cpuSeconds / wallSeconds / Environment.ProcessorCount * 100,
            logicalProcessors = Environment.ProcessorCount,
            workingSetMiB = process.WorkingSet64 / 1048576.0,
            peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0,
            allocationScope = "Repair worker plus bitmap conversion; CPU and working set include the moving source window and capture.",
            medianCaptureToSubmissionMs = sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2],
            p95CaptureToSubmissionMs = sorted.Length == 0 ? double.NaN : sorted[(int)((sorted.Length - 1) * .95)],
            errors = errors.ToArray(), frames = samples,
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
        File.WriteAllText(Path.Combine(output, "live-metrics.json"), json);
        Console.WriteLine($"Live: {samples.Count} frames; source-to-submission median {report.medianSourceToSubmissionMs:F1}ms; P95 {report.p95SourceToSubmissionMs:F1}ms; errors {errors.Count}");
        if (samples.Count == 0 || !errors.IsEmpty) throw new InvalidOperationException("Live probe failed; see live-metrics.json.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}
