using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Puts what the user asked to copy on the clipboard: text, or the screenshot.
/// </summary>
/// <remarks>
/// <para>Written straight to the clipboard with the Win32 calls rather than through WPF's
/// <c>Clipboard.SetText</c> / <c>SetImage</c>. Those publish a data object first and only hand its
/// contents over in a second step, the flush. In between, anything watching the clipboard — a
/// clipboard manager, an input method, a chat app — is told it changed, opens it, and asks for the
/// text; Windows then has to ask this process for it, on the UI thread. That thread is sitting in
/// WPF's flush retry loop, which sleeps without pumping messages, so neither side can move until
/// the retries run out about a second later and WPF throws CLIPBRD_E_CANT_OPEN. With a watcher
/// that reads every format this failed 19 copies in 50 on a test machine; written here, 0 in 50.</para>
///
/// <para>Here the contents go in whole while the clipboard is open, so nobody reading it ever needs
/// anything from this process, and they stay after it exits. What remains is the ordinary case of
/// another program having the clipboard open for a moment to read it; opening is retried with a
/// doubling wait for that (<see cref="RetryDelays"/>), and only a clipboard still held after that
/// is reported as a failure.</para>
/// </remarks>
internal static class ClipboardWriter
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// The waits between attempts to open the clipboard, in milliseconds: about 1.3 seconds in all,
    /// which outlasts anyone who merely opened it to read.
    /// </summary>
    internal static readonly int[] RetryDelays = [10, 20, 40, 80, 160, 320, 640];

    private static HwndSource? _owner;

    /// <summary>Replaces the clipboard's contents with <paramref name="text"/>.</summary>
    /// <exception cref="ClipboardBusyException">The clipboard stayed locked, or Windows refused the data.</exception>
    public static void SetText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        Buffer.BlockCopy(text.ToCharArray(), 0, bytes, 0, text.Length * 2);
        Write([(CF_UNICODETEXT, bytes)]);
    }

    /// <summary>
    /// Replaces the clipboard's contents with <paramref name="image"/>, as a DIB — what Paint and
    /// most of Windows read, and from which Windows makes the other bitmap formats on request — and
    /// as PNG, which browsers, chat apps and Office prefer.
    /// </summary>
    /// <exception cref="ClipboardBusyException">The clipboard stayed locked, or Windows refused the data.</exception>
    public static void SetImage(BitmapSource image)
    {
        var png = RegisterClipboardFormat("PNG");
        var formats = new List<(uint, byte[])> { (CF_DIB, Dib(image)) };
        if (png != 0) formats.Add((png, Png(image)));
        Write(formats);
    }

    /// <summary>
    /// A packed DIB: a BITMAPINFOHEADER for a 32-bit bottom-up bitmap, then its rows from the last
    /// up. Bottom-up because that is the layout every reader of CF_DIB handles; a negative height
    /// (top-down) is legal but not everything honours it.
    /// </summary>
    internal static byte[] Dib(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgr32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        // A screenshot is opaque. Bgr32 leaves the fourth byte undefined, and a reader that does
        // look at it in a BI_RGB bitmap would paste something see-through.
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        const int header = 40;
        var dib = new byte[header + pixels.Length];
        BitConverter.GetBytes(header).CopyTo(dib, 0);       // biSize
        BitConverter.GetBytes(width).CopyTo(dib, 4);        // biWidth
        BitConverter.GetBytes(height).CopyTo(dib, 8);       // biHeight: positive, bottom-up
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);    // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);   // biBitCount
        // biCompression = BI_RGB (0), biSizeImage, resolution and palette fields stay 0.
        BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);

        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(pixels, row * stride, dib, header + (height - 1 - row) * stride, stride);
        return dib;
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Write(IReadOnlyList<(uint Format, byte[] Data)> formats)
    {
        var owner = Owner();
        var attempts = 1;
        while (!OpenClipboard(owner))
        {
            if (attempts > RetryDelays.Length)
                throw new ClipboardBusyException(
                    $"The clipboard stayed locked through {attempts} attempts ({Holder()})", Marshal.GetLastWin32Error());
            Thread.Sleep(RetryDelays[attempts - 1]);
            attempts++;
        }

        try
        {
            if (!EmptyClipboard())
                throw new ClipboardBusyException("EmptyClipboard failed", Marshal.GetLastWin32Error());

            foreach (var (format, data) in formats)
            {
                var memory = Global(data);
                if (SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    GlobalFree(memory);
                    throw new ClipboardBusyException($"SetClipboardData({format}) failed", error);
                }
                // From here the clipboard owns the memory.
            }
        }
        finally
        {
            CloseClipboard();
        }

        if (attempts > 1)
            Log.Debug("Clipboard written after {Attempts} attempts", attempts);
    }

    private static IntPtr Global(byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero)
            throw new ClipboardBusyException("GlobalAlloc failed", Marshal.GetLastWin32Error());
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            GlobalFree(memory);
            throw new ClipboardBusyException("GlobalLock failed", error);
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        return memory;
    }

    /// <summary>
    /// A message-only window to open the clipboard with. Opening it with no window makes
    /// EmptyClipboard leave it ownerless, and SetClipboardData then fails.
    /// </summary>
    private static IntPtr Owner()
    {
        _owner ??= new HwndSource(new HwndSourceParameters("OverTranslate clipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        return _owner.Handle;
    }

    /// <summary>Which process has the clipboard open, for the log of a copy that gave up.</summary>
    private static string Holder()
    {
        var window = GetOpenClipboardWindow();
        if (window == IntPtr.Zero) return "held by no window";
        GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return $"held by {process.ProcessName} (pid {pid})";
        }
        catch (Exception)
        {
            return $"held by pid {pid}";
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}

/// <summary>The clipboard could not be written: still locked by another program, or refused.</summary>
internal sealed class ClipboardBusyException(string message, int win32Error)
    : IOException($"{message} (Win32 error {win32Error})");
