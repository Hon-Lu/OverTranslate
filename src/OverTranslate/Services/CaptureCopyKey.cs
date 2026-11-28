namespace OverTranslate.Services;

/// <summary>What <see cref="CaptureCopyKey"/> makes of one key event.</summary>
internal enum CaptureCopyKeyVerdict
{
    /// <summary>Not ours: on to the next hook and the application in front.</summary>
    Pass,

    /// <summary>A Ctrl+C for 截圖: swallowed, and the screenshot taken.</summary>
    Copy,

    /// <summary>
    /// The held C of a Ctrl+C already taken, repeating: swallowed, so the application behind the
    /// capture does not copy instead, but not a second screenshot.
    /// </summary>
    Swallow,
}

/// <summary>
/// Ctrl+C 複製截圖 (#445): when to listen for it, and which key events are it.
/// </summary>
/// <remarks>
/// <para>The rules on their own, with no hook and no window, so every one of them can be tested.
/// <see cref="CaptureCopyKeyHook"/> feeds it.</para>
///
/// <para>Only plain Ctrl+C. Ctrl+Shift+C is the browsers' element inspector and Ctrl+Alt+C is
/// someone's shortcut too; neither is a copy, and Ctrl+C+C for 取詞翻譯 is told apart the same
/// way.</para>
///
/// <para>One screenshot for each time Ctrl is held. A C held down repeats its key-down, and the
/// low-level hook says nothing to tell a repeat from a press; and Ctrl+C+C, 取詞翻譯's shortcut,
/// is two presses of C under one Ctrl. Both are one intent, and both would otherwise have copied
/// the same picture again and stacked a notice for each. The C presses after the first are still
/// swallowed, so none of them reaches the application behind the capture as a copy of its own.
/// Ctrl let go, the next Ctrl+C is a screenshot again.</para>
///
/// <para>Typing into one of ours — a 文字 mark, a size on the 標記 panel — Ctrl+C is that box's
/// own copy, exactly as Esc and Ctrl+Z stand aside there. See <see cref="TypingWindows"/>.</para>
/// </remarks>
internal sealed class CaptureCopyKey
{
    private const uint VkC = 0x43;
    private const uint VkControl = 0x11;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;

    // Whether a Ctrl+C has been taken since Ctrl last went down.
    private bool _ctrlTaken;

    /// <summary>
    /// Whether to listen at all: only with the switch on, and only once there is a selection to copy
    /// — before it is drawn, and while it is being dragged, there is no 截圖 button to stand in for.
    /// </summary>
    public static bool ShouldListen(bool switchOn, bool hasSelection) => switchOn && hasSelection;

    /// <summary>
    /// Whether the Ctrl of a Ctrl+C already taken is still down — while it is, a C repeating or
    /// pressed again is still this one's to swallow. See <see cref="CaptureCopyKeyHook.Dispose"/>.
    /// </summary>
    public bool HoldingTakenCopy => _ctrlTaken;

    /// <summary>One key event, with the modifiers as they stood when it arrived.</summary>
    /// <param name="ctrl">
    /// Whether Ctrl is down. For Ctrl's own release the system may still say it is; the release
    /// itself is read from <paramref name="virtualKey"/>.
    /// </param>
    public CaptureCopyKeyVerdict OnKey(
        uint virtualKey, bool down, bool ctrl, bool shift, bool alt, bool win, bool typing)
    {
        bool ctrlKey = virtualKey is VkControl or VkLControl or VkRControl;
        if (!ctrl || (ctrlKey && !down)) _ctrlTaken = false;

        if (virtualKey != VkC || !down) return CaptureCopyKeyVerdict.Pass;

        if (!ctrl || shift || alt || win || typing) return CaptureCopyKeyVerdict.Pass;
        if (_ctrlTaken) return CaptureCopyKeyVerdict.Swallow;

        _ctrlTaken = true;
        return CaptureCopyKeyVerdict.Copy;
    }
}
