using System.Runtime.InteropServices;
using NLog;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Services;

/// <summary>What became of an attempt to replace the selection.</summary>
public enum PasteOutcome
{
    /// <summary>The paste was sent. See the remarks on <see cref="SelectionReplacer"/>.</summary>
    Pasted,

    /// <summary>The user moved to another window while the translation was still being fetched.</summary>
    FocusMoved,

    /// <summary>The clipboard could not be borrowed, so nothing was sent.</summary>
    ClipboardUnavailable,
}

/// <summary>
/// Puts text where the user's selection is, in whatever application they are working in.
/// </summary>
/// <remarks>
/// 替換翻譯's second half. <see cref="SelectedTextReader"/> borrows the copy shortcut to find out
/// what is selected; this borrows the paste one to replace it, for the same reason — no process can
/// edit another's text, so the only lever available is the editing shortcut the application already
/// implements for its own user.
///
/// Which is the whole of what this can promise. A paste sent to a read-only surface — a web page, a
/// PDF viewer, a chat transcript — does nothing at all, and the sending application is told nothing
/// about it. So <see cref="PasteOutcome.Pasted"/> means the keystroke went out, never that anything
/// changed on the other end.
/// </remarks>
internal static class SelectionReplacer
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// How long the paste is given before the user's own clipboard goes back on.
    /// </summary>
    /// <remarks>
    /// The keystroke is delivered asynchronously and the application reads the clipboard when it
    /// gets round to handling it, so restoring immediately is a race this would lose — and losing it
    /// means the user's previous clipboard contents land on their selection instead of the
    /// translation, which is worse than any failure this feature could otherwise produce. Long
    /// enough for an application that is merely busy, short enough that the clipboard is not held
    /// while the user moves on to something else.
    /// </remarks>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Replaces the foreground application's selection with <paramref name="text"/>.
    /// </summary>
    /// <param name="expectedForeground">
    /// The window the selection was read from. A paste is a destructive edit and it lands wherever
    /// the foreground happens to be, so if the user has moved on — to another document, to a chat
    /// box — while the translation was being fetched, it is not sent at all: overwriting a selection
    /// they made somewhere else is not something they could have asked for.
    /// </param>
    public static async Task<PasteOutcome> ReplaceSelectionAsync(string text, IntPtr expectedForeground)
    {
        if (expectedForeground != IntPtr.Zero && GetForegroundWindow() != expectedForeground)
        {
            Log.Info("替換翻譯 did not paste: the foreground moved to another window while translating");
            return PasteOutcome.FocusMoved;
        }

        object? snapshot;

        try
        {
            snapshot = ClipboardBackup.Capture();
        }
        catch (Exception ex)
        {
            // With no snapshot there is nothing to put back, so the clipboard is not taken at all:
            // the user's own contents are worth more than this one translation.
            Log.Info(ex, "替換翻譯 could not snapshot the clipboard, so nothing was pasted");
            return PasteOutcome.ClipboardUnavailable;
        }

        try
        {
            if (!TryPut(text)) return PasteOutcome.ClipboardUnavailable;

            await KeyboardInput.SendControlChordAsync(KeyboardInput.VK_V);
            await Task.Delay(PasteSettle);
            return PasteOutcome.Pasted;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "替換翻譯 could not paste the translation");
            return PasteOutcome.ClipboardUnavailable;
        }
        finally
        {
            ClipboardBackup.Restore(snapshot);
        }
    }

    /// <remarks>
    /// WPF already retries for about a second before it throws, so a throw here means the clipboard
    /// stayed locked for that whole second — and a paste sent anyway would put whatever was on it
    /// before over the user's selection.
    /// </remarks>
    private static bool TryPut(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "替換翻譯 could not put the translation on the clipboard");
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
