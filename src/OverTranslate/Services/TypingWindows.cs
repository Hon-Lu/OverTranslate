using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OverTranslate.Services;

/// <summary>
/// The windows of this session that are taking typing right now, for the keyboard hooks to stand
/// aside for.
/// </summary>
/// <remarks>
/// <para>The capture session swallows Esc, Ctrl+Z and Ctrl+Y before any window sees them — see
/// <see cref="GlobalEscapeHook"/> and <see cref="AnnotationShortcutHook"/> — because nothing in it
/// normally holds the keyboard. A 文字 mark being typed (#362), or a size typed into the 標記
/// panel, does: there Esc has to finish the typing rather than end the capture, and Ctrl+Z has to
/// take back a character rather than a whole mark.</para>
///
/// <para>Asked from the hooks' own threads, so it is a lock-free set and the question is put to
/// the system rather than to WPF: a key passes only while one of these windows really is the one in
/// the foreground. A window left registered by mistake therefore costs nothing once anything else
/// is in front — Esc goes back to closing the capture.</para>
/// </remarks>
internal static class TypingWindows
{
    private static readonly ConcurrentDictionary<IntPtr, byte> Windows = new();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static void Add(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Windows[hwnd] = 0;
    }

    public static void Remove(IntPtr hwnd) => Windows.TryRemove(hwnd, out _);

    /// <summary>Whether the keyboard is going to one of them right now.</summary>
    public static bool HaveForeground => !Windows.IsEmpty && Windows.ContainsKey(GetForegroundWindow());
}
