using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ctrl twice and Ctrl+C+C (#321): what counts, and the everyday uses of Ctrl that must not.
/// </summary>
public class KeySequenceDetectorTests
{
    private const uint LCtrl = 0xA2;
    private const uint RCtrl = 0xA3;
    private const uint LShift = 0xA0;
    private const uint C = 0x43;
    private const uint V = 0x56;

    private readonly HashSet<uint> _down = [];
    private uint _clipboard = 7;
    private readonly KeySequenceDetector _detector;

    public KeySequenceDetectorTests()
    {
        _detector = new KeySequenceDetector(() => _clipboard, key => _down.Contains(key));
    }

    /// <summary>Presses a key the way the system reports it: generic modifiers for the async state.</summary>
    private KeySequenceHit? Down(uint key, uint time)
    {
        var hit = _detector.OnKey(key, true, time);
        _down.Add(Generic(key));
        return hit;
    }

    private KeySequenceHit? Up(uint key, uint time)
    {
        var hit = _detector.OnKey(key, false, time);
        _down.Remove(Generic(key));
        return hit;
    }

    private static uint Generic(uint key) => key switch
    {
        LCtrl or RCtrl => 0x11,
        LShift => 0x10,
        _ => key,
    };

    [Fact]
    public void TwoQuickTapsOfCtrlFireOnTheSecondRelease()
    {
        Assert.Null(Down(LCtrl, 1000));
        Assert.Null(Up(LCtrl, 1080));
        Assert.Null(Down(LCtrl, 1250));

        var hit = Up(LCtrl, 1320);

        Assert.Equal(new KeySequenceHit(ShortcutInputKind.CtrlDoubleTap, null), hit);
    }

    [Fact]
    public void LeftAndRightCtrlCountAsTheSameKey()
    {
        Down(LCtrl, 1000);
        Up(LCtrl, 1080);
        Down(RCtrl, 1250);

        Assert.NotNull(Up(RCtrl, 1320));
    }

    [Fact]
    public void ASlowSecondTapIsAFreshFirstTap()
    {
        Down(LCtrl, 1000);
        Up(LCtrl, 1080);
        Down(LCtrl, 1080 + KeySequenceDetector.TapMaxGap + 1);
        Assert.Null(Up(LCtrl, 1600));

        Down(LCtrl, 1700);
        Assert.NotNull(Up(LCtrl, 1760));
    }

    [Fact]
    public void HoldingCtrlIsNotATap()
    {
        // Crouching in a game, or a Ctrl held while deciding what to press.
        Down(LCtrl, 1000);
        for (uint t = 1500; t < 2000; t += 33) Down(LCtrl, t); // auto-repeat
        Up(LCtrl, 2000);
        Down(LCtrl, 2100);

        Assert.Null(Up(LCtrl, 2160));
    }

    [Fact]
    public void ACopyThenACtrlTapIsNotCtrlTwice()
    {
        Down(LCtrl, 1000);
        Down(C, 1040);
        Up(C, 1090);
        Up(LCtrl, 1120);
        Down(LCtrl, 1250);

        Assert.Null(Up(LCtrl, 1300));
    }

    [Fact]
    public void ACtrlTapThenCtrlCIsNotCtrlTwice()
    {
        // Fires on release precisely so this stays a copy.
        Down(LCtrl, 1000);
        Up(LCtrl, 1060);
        Down(LCtrl, 1200);
        Down(C, 1240);
        Up(C, 1290);

        Assert.Null(Up(LCtrl, 1320));
    }

    [Fact]
    public void ACtrlClickBreaksTheTap()
    {
        Down(LCtrl, 1000);
        Up(LCtrl, 1060);
        Down(LCtrl, 1200);
        _detector.OnOtherInput();

        Assert.Null(Up(LCtrl, 1260));
    }

    [Fact]
    public void ThreeTapsFireOnceNotTwice()
    {
        Down(LCtrl, 1000); Up(LCtrl, 1050);
        Down(LCtrl, 1150);
        Assert.NotNull(Up(LCtrl, 1200));
        Down(LCtrl, 1300);

        Assert.Null(Up(LCtrl, 1350));
    }

    [Fact]
    public void CtrlCCFiresOnTheSecondCAndCarriesTheClipboardFromBeforeTheFirst()
    {
        Down(LCtrl, 1000);
        Assert.Null(Down(C, 1050));
        _clipboard = 8; // the application has answered the first copy
        Up(C, 1100);

        var hit = Down(C, 1250);

        Assert.Equal(new KeySequenceHit(ShortcutInputKind.CtrlCC, 7), hit);
    }

    [Fact]
    public void AHeldCIsOneCopyNotTwo()
    {
        Down(LCtrl, 1000);
        Down(C, 1050);
        for (uint t = 1550; t < 1700; t += 33) Assert.Null(Down(C, t)); // auto-repeat
    }

    [Fact]
    public void ReleasingCtrlBetweenTheCopiesIsTwoCopies()
    {
        Down(LCtrl, 1000);
        Down(C, 1050); Up(C, 1100);
        Up(LCtrl, 1150);
        Down(LCtrl, 1200);

        Assert.Null(Down(C, 1250));
    }

    [Fact]
    public void SlowCopiesAreSeparateCopies()
    {
        Down(LCtrl, 1000);
        Down(C, 1050); Up(C, 1100);

        Assert.Null(Down(C, 1050 + KeySequenceDetector.RepeatMaxGap + 1));
    }

    [Fact]
    public void ShiftWithCtrlIsTheInspectorNotACopy()
    {
        Down(LCtrl, 1000);
        Down(LShift, 1010);
        Down(C, 1050); Up(C, 1100);

        Assert.Null(Down(C, 1250));
    }

    [Fact]
    public void AnotherKeyBetweenTheCopiesStartsOver()
    {
        // Ctrl+C, Ctrl+V, Ctrl+C: copy, paste, copy — not a request to translate.
        Down(LCtrl, 1000);
        Down(C, 1050); Up(C, 1080);
        Down(V, 1120); Up(V, 1150);

        Assert.Null(Down(C, 1200));
    }
}
