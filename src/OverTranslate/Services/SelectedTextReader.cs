using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Services;

/// <summary>
/// Whatever the user had selected in the application they were reading, at the moment a shortcut
/// was pressed.
/// </summary>
/// <remarks>
/// Windows has no way to ask another process what it has selected. UI Automation's TextPattern is
/// the closest thing and it answers for a minority of applications — not Chrome's page content, not
/// most games, not anything drawing its own text — so the general mechanism is the one every other
/// dictionary tool on this platform uses: synthesise the copy shortcut and read the clipboard.
///
/// That means borrowing something that belongs to the user, so most of this file is about giving it
/// back: the clipboard's previous contents are snapshotted before the copy and restored after it,
/// and a failure anywhere leaves the caller with an empty string rather than an exception. Coming
/// back with nothing is not an error condition here — the popup opens with an empty box and the
/// user types, which is exactly what it does when nothing was selected.
/// </remarks>
internal static class SelectedTextReader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// How long the foreground application is given to start answering the copy.
    /// </summary>
    /// <remarks>
    /// An empty selection produces no clipboard event at all. Keeping this separate from the longer
    /// completion timeout lets that common path open the popup promptly without giving up on a copy
    /// that has already started publishing its formats.
    /// </remarks>
    private static readonly TimeSpan CopyStartTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>How long an observed clipboard update may take to publish readable text.</summary>
    private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromMilliseconds(320);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// The most text this will carry into the popup.
    /// </summary>
    /// <remarks>
    /// 取詞翻譯 is a single-line box over someone else's window, not a document translator: a
    /// selection longer than this was a drag that got away, or a Ctrl+A, and pouring it into a
    /// one-line field gives the user something they cannot read and a translation they did not ask
    /// for. 文字翻譯 is the window for that, and it is one shortcut away.
    /// </remarks>
    public const int MaxLength = 1000;

    /// <summary>Runs of whitespace, newlines included.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Copies the current selection out of the foreground application, restoring the clipboard.
    /// </summary>
    /// <remarks>
    /// Must be awaited before the popup is shown. Showing it first takes the foreground away from
    /// the application holding the selection, and the copy would then be sent to a text box that has
    /// nothing in it.
    /// </remarks>
    public static async Task<string> ReadAsync()
    {
        object? snapshot;
        uint before;

        try
        {
            snapshot = ClipboardBackup.Capture();
            before = GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            // The clipboard is one system-wide resource and any process can have it locked. With no
            // snapshot there is nothing to put back, so the copy is not attempted at all.
            Log.Debug(ex, "Could not snapshot the clipboard; the selection is read as empty");
            return "";
        }

        try
        {
            await SendCopy();

            var maxStartPolls = (int)Math.Ceiling(
                CopyStartTimeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
            var maxCompletionPolls = (int)Math.Ceiling(
                CopyCompletionTimeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
            return await PollForCopiedTextAsync(
                before,
                GetClipboardSequenceNumber,
                ReadClipboardTextIfAvailable,
                () => Task.Delay(PollInterval),
                maxStartPolls,
                maxCompletionPolls);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the selection failed; it is read as empty");
            return "";
        }
        finally
        {
            ClipboardBackup.Restore(snapshot);
        }
    }

    /// <summary>
    /// Waits for a copy operation to publish readable text after changing the clipboard.
    /// </summary>
    /// <remarks>
    /// A clipboard sequence change means that an update started, not that every format is ready.
    /// Applications commonly empty the clipboard before publishing text, and another process can
    /// briefly hold it between those steps. Returning on that first change mistakes an in-progress
    /// copy for an empty selection and restores the old clipboard over the result that is still
    /// arriving.
    /// </remarks>
    internal static async Task<string> PollForCopiedTextAsync(
        uint before,
        Func<uint> getSequenceNumber,
        Func<string?> readText,
        Func<Task> delay,
        int maxStartPolls,
        int maxCompletionPolls)
    {
        var changed = false;
        var startPolls = 0;
        var completionPolls = 0;

        while (true)
        {
            changed |= getSequenceNumber() != before;

            if (changed && readText() is { } text)
                return Sanitize(text);

            if (changed)
            {
                if (completionPolls >= maxCompletionPolls)
                {
                    Log.Debug("Copy changed the clipboard but did not publish readable text before timeout");
                    return "";
                }
                completionPolls++;
            }
            else
            {
                if (startPolls >= maxStartPolls)
                {
                    Log.Debug("Copy did not change the clipboard before timeout");
                    return "";
                }
                startPolls++;
            }

            await delay();
        }
    }

    /// <summary>Returns null while copied text is not published or cannot be read yet.</summary>
    private static string? ReadClipboardTextIfAvailable()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            // Clipboard ownership changes asynchronously. A lock here is a state to poll through,
            // not the final answer to the user's copy request.
            Log.Trace(ex, "Copied text is not readable yet");
            return null;
        }
    }

    /// <summary>
    /// Trims the copied text down to one line of at most <see cref="MaxLength"/> characters.
    /// </summary>
    /// <remarks>
    /// Whitespace is collapsed rather than kept because a selection dragged across a web page or a
    /// PDF arrives hard-wrapped at whatever width it was laid out to, and those line breaks belong
    /// to the page rather than to the sentence. Left in, they reach the translation engine as
    /// sentence boundaries that are not there.
    /// </remarks>
    public static string Sanitize(string? raw)
    {
        var text = Fold(raw);
        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// Folds the text onto one line, with no cap on its length.
    /// </summary>
    /// <remarks>
    /// For text the user pasted in on purpose: the cap in <see cref="Sanitize"/> guards against a
    /// selection that got away, and a paste is no more of a runaway than typing, which has no cap.
    /// </remarks>
    public static string Fold(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : Whitespace.Replace(raw, " ").Trim();

    /// <summary>
    /// Sends Ctrl+C to whatever has the foreground — see <see cref="KeyboardInput"/> for how the
    /// chord is shaped so an application on the other end actually sees it.
    /// </summary>
    private static Task SendCopy() => KeyboardInput.SendControlChordAsync(KeyboardInput.VK_C);

    /// <summary>
    /// Counts every change to the clipboard, system-wide.
    /// </summary>
    /// <remarks>
    /// Polled rather than reading the text back and comparing, because the two cases that matter are
    /// indistinguishable by content: nothing was selected, and the selection happened to be the text
    /// already on the clipboard. The sequence number separates them.
    /// </remarks>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
