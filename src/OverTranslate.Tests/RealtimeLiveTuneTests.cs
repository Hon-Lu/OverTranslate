using System.Reflection;
using System.Windows.Threading;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 翻譯中調整模式: what keeps a run of changes from the running bar's 模式 panel from restarting the
/// blocks once per change.
/// </summary>
public class RealtimeLiveTuneTests
{
    /// <summary>
    /// The rest the controller waits out before the session reads again must really be there on the
    /// one instance the app uses. A static field for it, declared below the singleton, was still zero
    /// when the singleton was built: the timer fired at once, and two presses 21 ms apart restarted
    /// the blocks twice (measured live).
    /// </summary>
    [Fact]
    public void TheControllerWaitsForTheModesToRest_BeforeTheSessionReadsAgain()
    {
        var timer = (DispatcherTimer)typeof(RealtimeSessionController)
            .GetField("_profilesSettle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(RealtimeSessionController.Instance)!;

        Assert.Equal(RealtimeSessionController.ProfilesSettle, timer.Interval);
        Assert.True(timer.Interval >= TimeSpan.FromMilliseconds(300));
    }
}
