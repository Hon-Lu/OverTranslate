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
/// <para>A C held down repeats its key-down; the low-level hook says nothing to tell a repeat from a
/// press, so the key's own release is what marks the end of one. Without that, holding the keys a
/// moment too long copied over and over and stacked a notice for each.</para>
///
/// <para>Typing into one of ours — a 文字 mark, a size on the 標記 panel — Ctrl+C is that box's
/// own copy, exactly as Esc and Ctrl+Z stand aside there. See <see cref="TypingWindows"/>.</para>
/// </remarks>
internal sealed class CaptureCopyKey
{
    private const uint VkC = 0x43;

    private bool _cHeld;
    private bool _heldTaken;

    /// <summary>
    /// Whether to listen at all: only with the switch on, and only once there is a selection to copy
    /// — before it is drawn, and while it is being dragged, there is no 截圖 button to stand in for.
    /// </summary>
    public static bool ShouldListen(bool switchOn, bool hasSelection) => switchOn && hasSelection;

    /// <summary>One key event, with the modifiers as they stood when it arrived.</summary>
    public CaptureCopyKeyVerdict OnKey(
        uint virtualKey, bool down, bool ctrl, bool shift, bool alt, bool win, bool typing)
    {
        if (virtualKey != VkC) return CaptureCopyKeyVerdict.Pass;

        if (!down)
        {
            _cHeld = false;
            _heldTaken = false;
            return CaptureCopyKeyVerdict.Pass;
        }

        bool repeat = _cHeld;
        _cHeld = true;

        bool ours = ctrl && !shift && !alt && !win && !typing;
        if (repeat)
            return _heldTaken && ours ? CaptureCopyKeyVerdict.Swallow : CaptureCopyKeyVerdict.Pass;

        _heldTaken = ours;
        return ours ? CaptureCopyKeyVerdict.Copy : CaptureCopyKeyVerdict.Pass;
    }
}
