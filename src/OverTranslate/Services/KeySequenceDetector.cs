using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>A key sequence that has just completed.</summary>
/// <param name="ClipboardBefore">
/// For Ctrl+C+C, the clipboard's sequence number from just before the first C reached the
/// foreground application — that press is the user's own copy, and comparing against this number is
/// how the reader tells a copy that happened from one that had nothing to copy. Null for Ctrl twice.
/// </param>
internal readonly record struct KeySequenceHit(ShortcutInputKind Kind, uint? ClipboardBefore);

/// <summary>
/// Recognises Ctrl twice and Ctrl+C+C in a stream of key events.
/// </summary>
/// <remarks>
/// <para>Fed by <see cref="GlobalAuxiliaryHotkeys"/>' keyboard hook and by the settings page while
/// it records, so what can be recorded is exactly what will fire. It never decides anything about
/// the keys themselves — every one of them still reaches the application in front.</para>
///
/// <para>Ctrl twice is strict, because Ctrl is pressed all day for other reasons. Each press has to
/// be a short tap with nothing else pressed while it is down — a Ctrl+click, Ctrl+scroll or Ctrl+C is
/// not a tap — and the second has to start soon after the first ends. It fires when the second tap is
/// released rather than pressed: until then it could still turn out to be Ctrl+something.</para>
///
/// <para>Ctrl+C+C fires on the second C going down. Ctrl has to be held across both and nothing else
/// held with it — Ctrl+Shift+C is the browsers' element inspector, not a copy. A C held down repeats
/// without counting as a second press.</para>
///
/// <para>Times are the hook's millisecond tick and are compared with unsigned subtraction, so the
/// tick wrapping every 49 days does not read as a long gap.</para>
/// </remarks>
internal sealed class KeySequenceDetector
{
    /// <summary>The longest a Ctrl press may be held and still count as a tap.</summary>
    public const uint TapMaxHold = 350;

    /// <summary>The longest gap between the first tap's release and the second's press.</summary>
    public const uint TapMaxGap = 400;

    /// <summary>The longest gap between the two presses of C.</summary>
    public const uint RepeatMaxGap = 500;

    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkC = 0x43;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;

    private readonly Func<uint> _clipboardSequence;
    private readonly Func<uint, bool> _isKeyDown;

    // Ctrl twice.
    private bool _ctrlHeld;
    private uint _ctrlDownAt;
    private bool _ctrlDirty;
    private uint? _lastTapUpAt;

    // Ctrl+C+C.
    private bool _cHeld;
    private bool _firstCSeen;
    private uint _firstCAt;
    private uint _clipboardBefore;

    /// <param name="clipboardSequence">The clipboard's sequence number, read at the first C.</param>
    /// <param name="isKeyDown">
    /// Whether a modifier is down. Asked of the system rather than tracked here: a key-up this
    /// never saw — the lock screen took it — would otherwise leave a modifier down for good.
    /// </param>
    public KeySequenceDetector(Func<uint> clipboardSequence, Func<uint, bool> isKeyDown)
    {
        _clipboardSequence = clipboardSequence;
        _isKeyDown = isKeyDown;
    }

    /// <summary>Feeds one key event; returns the sequence it completes, if any.</summary>
    public KeySequenceHit? OnKey(uint virtualKey, bool down, uint time)
    {
        var key = Normalize(virtualKey);
        return down ? OnKeyDown(key, time) : OnKeyUp(key, time);
    }

    /// <summary>
    /// A mouse button or the wheel. Breaks a Ctrl tap — Ctrl+click and Ctrl+scroll are not taps —
    /// and leaves Ctrl+C+C alone.
    /// </summary>
    public void OnOtherInput()
    {
        _ctrlDirty = true;
        _lastTapUpAt = null;
    }

    private KeySequenceHit? OnKeyDown(uint key, uint time)
    {
        if (key == VkControl)
        {
            // A held key repeats its down; only the first one starts a press.
            if (_ctrlHeld) return null;
            _ctrlHeld = true;
            _ctrlDownAt = time;
            _ctrlDirty = false;
            return null;
        }

        // Anything else pressed breaks Ctrl twice, whether or not Ctrl is down at the moment.
        _ctrlDirty = true;
        _lastTapUpAt = null;

        if (key != VkC)
        {
            _firstCSeen = false;
            return null;
        }

        var repeat = _cHeld;
        _cHeld = true;
        if (repeat) return null;

        if (!IsPlainCtrl())
        {
            _firstCSeen = false;
            return null;
        }

        if (_firstCSeen && time - _firstCAt <= RepeatMaxGap)
        {
            _firstCSeen = false;
            return new KeySequenceHit(ShortcutInputKind.CtrlCC, _clipboardBefore);
        }

        _firstCSeen = true;
        _firstCAt = time;
        _clipboardBefore = _clipboardSequence();
        return null;
    }

    private KeySequenceHit? OnKeyUp(uint key, uint time)
    {
        if (key == VkC)
        {
            _cHeld = false;
            return null;
        }

        if (key != VkControl) return null;

        var wasHeld = _ctrlHeld;
        _ctrlHeld = false;
        _firstCSeen = false;

        if (!wasHeld || _ctrlDirty || time - _ctrlDownAt > TapMaxHold)
        {
            _lastTapUpAt = null;
            return null;
        }

        if (_lastTapUpAt is { } last && _ctrlDownAt - last <= TapMaxGap)
        {
            _lastTapUpAt = null;
            return new KeySequenceHit(ShortcutInputKind.CtrlDoubleTap, null);
        }

        _lastTapUpAt = time;
        return null;
    }

    /// <summary>Ctrl down, and none of Shift, Alt or Windows with it.</summary>
    private bool IsPlainCtrl() =>
        _isKeyDown(VkControl) &&
        !_isKeyDown(VkShift) &&
        !_isKeyDown(VkMenu) &&
        !_isKeyDown(VkLWin) &&
        !_isKeyDown(VkRWin);

    /// <summary>The hook reports which Ctrl, Shift or Alt; the sequences do not care.</summary>
    private static uint Normalize(uint virtualKey) => virtualKey switch
    {
        0xA2 or 0xA3 => VkControl,
        0xA0 or 0xA1 => VkShift,
        0xA4 or 0xA5 => VkMenu,
        _ => virtualKey,
    };
}
