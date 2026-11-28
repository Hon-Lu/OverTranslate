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

    // ── One per Ctrl ────────────────────────────────────────────────────────

    [Fact]
    public void HeldC_CopiesOnceAndSwallowsTheRepeats()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        for (int i = 0; i < 30; i++)
            Assert.Equal(CaptureCopyKeyVerdict.Swallow, Down(keys));
    }

    /// <summary>Ctrl+C+C: one screenshot, and neither C reaches 取詞翻譯 or the application.</summary>
    [Fact]
    public void CtrlCC_IsOneScreenshotAndBothCsSwallowed()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Up(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Swallow, Down(keys));
    }

    [Fact]
    public void CtrlReleasedAndPressedAgain_CopiesAgain()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Up(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys, 0xA2, ctrl: true)); // left Ctrl coming up
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
    }

    /// <summary>The release can come through as the generic Ctrl, or be missed and only show in the modifier state.</summary>
    [Fact]
    public void CtrlSeenUpByAnyKey_CopiesAgain()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Assert.True(keys.HoldingTakenCopy);
        Down(keys, V, ctrl: false);
        Assert.False(keys.HoldingTakenCopy);
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
    }

    /// <summary>
    /// Ctrl held, C then V: the copy is taken and the paste is not — with 截圖後自動關閉 on it is the
    /// one stroke that copies, closes and pastes the picture.
    /// </summary>
    [Fact]
    public void CtrlCThenV_UnderOneCtrl_PastesThrough()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, V));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys, V));
        Assert.True(keys.HoldingTakenCopy);
    }

    /// <summary>While a taken copy's Ctrl is down, only C is held back; every other key passes.</summary>
    [Theory]
    [InlineData(0x56u)] // V
    [InlineData(0x5Au)] // Z
    [InlineData(0x41u)] // A
    [InlineData(0x10u)] // Shift
    public void WhileHoldingTakenCopy_OtherKeysPass(uint key)
    {
        var keys = new CaptureCopyKey();
        Down(keys);
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, key));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Up(keys, key));
    }

    [Fact]
    public void ARepeatAfterTyping_IsNotTakenEither()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, typing: true));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, typing: true));
        Assert.False(keys.HoldingTakenCopy);
    }

    /// <summary>Ctrl let go with C still held: what repeats now is a plain c, and it is not ours.</summary>
    [Fact]
    public void ARepeatAfterCtrlIsReleased_IsLeftAlone()
    {
        var keys = new CaptureCopyKey();
        Assert.Equal(CaptureCopyKeyVerdict.Copy, Down(keys));
        Assert.Equal(CaptureCopyKeyVerdict.Pass, Down(keys, ctrl: false));
        Assert.False(keys.HoldingTakenCopy);
    }
}
