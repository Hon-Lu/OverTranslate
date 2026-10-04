using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Matching the window a user picked last time against the windows open now.
/// </summary>
/// <remarks>
/// The rule has to hold two things apart that look alike. A window handle cannot be stored, so the
/// stored identity is a pair — which application, and what the window was called — and the title
/// half goes stale constantly: a browser retitles itself with every video. Matching titles exactly
/// would restore almost nothing for the case this feature is used on most; matching the application
/// alone would confidently restore the wrong window whenever two of its windows are open.
/// </remarks>
public class CaptureWindowRestoreTests
{
    private static CaptureWindowList.CaptureWindow Window(string process, string title) =>
        new(new IntPtr(title.GetHashCode()), title, process, new Rectangle(0, 0, 100, 100));

    [Fact]
    public void TheSameApplicationWithTheSameTitleIsTheOneMeant()
    {
        var open = new[] { Window("chrome", "A - YouTube"), Window("crab", "Crab Champions") };

        var found = CaptureWindowList.FindStored(open, "crab", "Crab Champions");

        Assert.Equal("Crab Champions", found?.Title);
    }

    [Fact]
    public void ARetitledWindowStillMatchesWhenItsApplicationHasOnlyOne()
    {
        // The everyday case: the same browser window, a different video in it.
        var open = new[] { Window("chrome", "B - YouTube"), Window("crab", "Crab Champions") };

        var found = CaptureWindowList.FindStored(open, "chrome", "A - YouTube");

        Assert.Equal("B - YouTube", found?.Title);
    }

    [Fact]
    public void TwoWindowsOfTheSameApplicationWithNeitherTitleMatchingAskAgain()
    {
        // Guessing here would point the session at the wrong window while looking like it
        // remembered correctly, which is worse than the picker saying 請選擇視窗.
        var open = new[] { Window("chrome", "B - YouTube"), Window("chrome", "C - YouTube") };

        Assert.Null(CaptureWindowList.FindStored(open, "chrome", "A - YouTube"));
    }

    [Fact]
    public void AnExactTitleWinsOverTheApplicationHavingOtherWindows()
    {
        var open = new[] { Window("chrome", "A - YouTube"), Window("chrome", "C - YouTube") };

        var found = CaptureWindowList.FindStored(open, "chrome", "A - YouTube");

        Assert.Equal("A - YouTube", found?.Title);
    }

    [Fact]
    public void AnApplicationThatIsNotRunningMatchesNothing()
    {
        var open = new[] { Window("chrome", "A - YouTube") };

        Assert.Null(CaptureWindowList.FindStored(open, "crab", "Crab Champions"));
    }

    [Fact]
    public void NothingStoredMatchesNothing()
    {
        // A first run. Without this the sole-window rule would adopt whichever application happened
        // to have exactly one window open.
        var open = new[] { Window("chrome", "A - YouTube") };

        Assert.Null(CaptureWindowList.FindStored(open, "", ""));
    }
}
