using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ctrl+C 複製截圖 (#445): which key events are taken for 截圖 and which are left to the
/// application. Ctrl+C is pressed all day for other things, so what is NOT taken matters as much as
/// what is.
/// </summary>
public class CaptureCopyKeyTests
{
    private const uint C = 0x43;
    private const uint V = 0x56;
    private const uint Ctrl = 0x11;

    private static CaptureCopyKeyVerdict Down(
        CaptureCopyKey keys, uint key = C, bool ctrl = true, bool shift = false, bool alt = false,
        bool win = false, bool typing = false)
        => keys.OnKey(key, down: true, ctrl, shift, alt, win, typing);

    private static CaptureCopyKeyVerdict Up(CaptureCopyKey keys, uint key = C, bool ctrl = true)
        => keys.OnKey(key, down: false, ctrl, shift: false, alt: false, win: false, typing: false);

    // ── When to listen ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]  // off: the default, nothing changes for anyone
    [InlineData(true, false, false)]  // on, but no selection yet or still being dragged
    [InlineData(true, true, true)]
    public void ListensOnlyWithTheSwitchOnAndASelection(bool switchOn, bool hasSelection, bool expected)
        => Assert.Equal(expected, CaptureCopyKey.ShouldListen(switchOn, hasSelection));

    // ── Which keys ──────────────────────────────────────────────────────────

    [Fact]
    public void PlainCtrlC_IsTaken()
        => Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(new CaptureCopyKey()));

    [Fact]
    public void CWithoutCtrl_IsLeftAlone()
        => Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(new CaptureCopyKey(), ctrl: false));

    [Theory]
    [InlineData(true, false, false)]  // Ctrl+Shift+C: the browsers' element inspector
    [InlineData(false, true, false)]  // Ctrl+Alt+C
    [InlineData(false, false, true)]  // Ctrl+Win+C
    [InlineData(true, true, false)]
    public void CtrlCWithAnotherModifier_IsLeftAlone(bool shift, bool alt, bool win)
        => Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(new CaptureCopyKey(), shift: shift, alt: alt, win: win));

    [Theory]
    [InlineData(V)]
    [InlineData(Ctrl)]
    [InlineData(0x5A)] // Z: 標記's undo has its own hook
    public void OtherKeys_AreLeftAlone(uint key)
        => Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(new CaptureCopyKey(), key));

    [Fact]
    public void KeyUps_AreAlwaysLeftAlone()
    {
        var keys = new CaptureCopyKey();
        Down(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys, Ctrl));
    }

    // ── Typing ──────────────────────────────────────────────────────────────

    [Fact]
    public void TypingIntoOneOfOurs_CtrlCIsTheBoxsOwnCopy()
        => Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(new CaptureCopyKey(), typing: true));

    [Fact]
    public void AfterTyping_CtrlCIsTakenAgain()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, typing: true));
        Up(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
    }

    // ── Held and repeated ───────────────────────────────────────────────────

    [Fact]
    public void HeldC_CopiesOnceAndSwallowsTheRepeats()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        for (int i = 0; i < 30; i++)
            Assert.Equal(CaptureCopyKeyVerdict.Swallow, Down(keys));
    }

    [Fact]
    public void ReleasedAndPressedAgain_CopiesAgain()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Down(keys);
        Up(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
    }

    /// <summary>Ctrl+C+C: two presses of C under one Ctrl are two screenshots, never 取詞.</summary>
    [Fact]
    public void CtrlCC_IsTwoPressesBothTaken()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Up(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
    }

    [Fact]
    public void ARepeatOfAPressThatWasNotTaken_IsNotTakenEither()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, typing: true));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, shift: true));
    }

    /// <summary>Ctrl let go with C still held: what repeats now is a plain c, and it is not ours.</summary>
    [Fact]
    public void ARepeatAfterCtrlIsReleased_IsLeftAlone()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, ctrl: false));
    }

    [Fact]
    public void ReleasingAnotherKey_DoesNotEndTheHeldC()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Up(keys, V);
        Assert.Equal(CaptureCopyKeyVerdict.Swallow, Down(keys));
    }
}
