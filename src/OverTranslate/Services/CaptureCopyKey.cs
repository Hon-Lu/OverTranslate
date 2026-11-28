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

/// <summary>Where the box stands, as far as Ctrl+C 複製截圖 is concerned.</summary>
/// <remarks>
/// Written by <see cref="Views.Capture.ScreenCaptureWindow"/> on the interface thread and read by
/// <see cref="CaptureCopyKeyHook"/> on its own, so it is a plain value the window publishes as it
/// changes rather than anything the hook would have to ask the window for.
/// </remarks>
internal enum CaptureSelectionState
{
    /// <summary>No box yet, and the button is not down.</summary>
    None,

    /// <summary>Dragging, but the box is still smaller than a release would keep.</summary>
    DraggingTooSmall,

    /// <summary>Dragging, and a release now would keep the box.</summary>
    DraggingReady,

    /// <summary>The box is drawn.</summary>
    Selected,

    /// <summary>The box is drawn and one of its handles, or the box itself, is being dragged.</summary>
    Adjusting,
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
/// <para>Listening from the moment the capture is up, not from the moment its toolbar is (r2): the
/// toolbar comes about a tenth of a second after the mouse is let go, and a Ctrl+C pressed straight
/// after the release fell through to the application under the capture. So the key is the hook's
/// from the start, and whether it takes a given Ctrl+C is down to <see cref="TakesCopy"/>: with a
/// box drawn, or one being dragged that is already big enough to keep — that one is settled where
/// it is, the drag cut short, as if the button had come up there.</para>
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

    /// <summary>The smallest box, in either direction, that letting the button go keeps.</summary>
    public const double MinBoxSize = 4;

    /// <summary>Whether to listen at all: only with the switch on, and only while a capture is up.</summary>
    public static bool ShouldListen(bool switchOn, bool captureUp) => switchOn && captureUp;

    /// <summary>
    /// Whether a Ctrl+C now is a screenshot. Before there is a box worth keeping there is nothing to
    /// copy, and the key is the application's as it always was.
    /// </summary>
    public static bool TakesCopy(CaptureSelectionState state) =>
        state is CaptureSelectionState.DraggingReady or CaptureSelectionState.Selected or CaptureSelectionState.Adjusting;

    /// <summary>What a drag with the box at this size is.</summary>
    public static CaptureSelectionState WhileDragging(double width, double height) =>
        width >= MinBoxSize && height >= MinBoxSize
            ? CaptureSelectionState.DraggingReady
            : CaptureSelectionState.DraggingTooSmall;

    /// <summary>
    /// Whether the Ctrl of a Ctrl+C already taken is still down — while it is, a C repeating or
    /// pressed again is still this one's to swallow. See <see cref="CaptureCopyKeyHook.Dispose"/>.
    /// </summary>
    public bool HoldingTakenCopy => _ctrlTaken;

    /// <summary>One key event, with the modifiers as they stood when it arrived.</summary>
    /// <param name="ready">
    /// Asked only for a plain Ctrl+C that would be a new screenshot: whether <see cref="TakesCopy"/>
    /// says yes right now. Not asked for a C under a Ctrl already taken, which is swallowed either way.
    /// </param>
    /// <param name="ctrl">
    /// Whether Ctrl is down. For Ctrl's own release the system may still say it is; the release
    /// itself is read from <paramref name="virtualKey"/>.
    /// </param>
    public CaptureCopyKeyVerdict OnKey(
        uint virtualKey, bool down, bool ctrl, bool shift, bool alt, bool win, bool typing, Func<bool> ready)
    {
        bool ctrlKey = virtualKey is VkControl or VkLControl or VkRControl;
        if (!ctrl || (ctrlKey && !down)) _ctrlTaken = false;

        if (virtualKey != VkC || !down) return CaptureCopyKeyVerdict.Pass;

        if (!ctrl || shift || alt || win || typing) return CaptureCopyKeyVerdict.Pass;
        if (_ctrlTaken) return CaptureCopyKeyVerdict.Swallow;
        if (!ready()) return CaptureCopyKeyVerdict.Pass;

        _ctrlTaken = true;
        return CaptureCopyKeyVerdict.Copy;
    }
}

/// <summary>
/// A Ctrl+C taken before the toolbar it presses exists (r2): held until <see cref="ToolbarReady"/>,
/// then run once.
/// </summary>
/// <remarks>
/// The key can come in the tenth of a second between the box being settled and the toolbar being
/// built. Dropping it there was the complaint; running the copy without the toolbar would bypass the
/// button it is meant to be. Kept on the interface thread only.
/// </remarks>
internal sealed class PendingCaptureCopy
{
    private bool _pending;

    /// <summary>A Ctrl+C was taken. True when it can be done now, the toolbar being up.</summary>
    public bool Request(bool toolbarUp)
    {
        if (toolbarUp) { _pending = false; return true; }
        _pending = true;
        return false;
    }

    /// <summary>The toolbar has just been built. True, once, when a copy was waiting for it.</summary>
    public bool ToolbarReady()
    {
        bool run = _pending;
        _pending = false;
        return run;
    }

    /// <summary>The capture is over; nothing waiting survives it.</summary>
    public void Clear() => _pending = false;
}
