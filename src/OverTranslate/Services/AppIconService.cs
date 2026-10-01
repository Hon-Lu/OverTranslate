using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

public static class AppIconService
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetStream("icons.app-compact.ico");
        return new System.Drawing.Icon(stream, 32, 32);
    }

    public static ImageSource CreateMainIcon()
        => CreateImageSource("icons.app.ico", 256);

    public static ImageSource CreateCompactIcon()
        => CreateImageSource("icons.app-compact.ico", 64);

    private static ImageSource CreateImageSource(string relativeName, int size)
    {
        using var stream = GetStream(relativeName);
        using var icon   = new System.Drawing.Icon(stream, size, size);
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    private static System.IO.Stream GetStream(string relativeName)
    {
        var name = $"OverTranslate.{relativeName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
    }
}
