using System.Windows;
using OverTranslate.Services;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// When a realtime overlay draws only the lines that have just arrived and leaves the rest as they
/// are: a streamed answer coming in, and nothing else.
/// </summary>
public class RealtimeBlockArrivalsTests
{
    private static TranslatedBlock Line(string translated, double top) =>
        new("source", translated, new Rect(10, top, 200, 24));

    [Fact]
    public void ALineThatWasWaitingAndNowHasTextIsTheOnlyOneDrawn()
    {
        var current = new[] { Line("已在畫面上", 0), Line("", 30) };
        var next = new[] { Line("已在畫面上", 0), Line("剛到", 30) };

        var arrived = RealtimeBlockWindow.ArrivedOnly(current, next);

        Assert.NotNull(arrived);
        Assert.Same(next[1], Assert.Single(arrived));
    }

    [Fact]
    public void AJitterWithinTolerance_StillOnlyTheArrival()
    {
        var current = new[] { Line("已在畫面上", 0), Line("", 30) };
        var next = new[] { Line("已在畫面上", 1.5), Line("剛到", 30) };

        Assert.NotNull(RealtimeBlockWindow.ArrivedOnly(current, next));
    }

    [Fact]
    public void ALineDrawnWithOtherWords_IsAWholeRedraw()
    {
        var current = new[] { Line("串流時的譯文", 0), Line("", 30) };
        var next = new[] { Line("完整答案的譯文", 0), Line("剛到", 30) };

        Assert.Null(RealtimeBlockWindow.ArrivedOnly(current, next));
    }

    [Fact]
    public void ALineThatMovedOrALineMoreOrLess_IsAWholeRedraw()
    {
        var current = new[] { Line("已在畫面上", 0), Line("", 30) };

        Assert.Null(RealtimeBlockWindow.ArrivedOnly(current, [Line("已在畫面上", 10), Line("剛到", 30)]));
        Assert.Null(RealtimeBlockWindow.ArrivedOnly(current, [Line("已在畫面上", 0)]));
    }

    [Fact]
    public void NothingNew_IsNotAnArrival()
    {
        var current = new[] { Line("已在畫面上", 0), Line("", 30) };

        Assert.Null(RealtimeBlockWindow.ArrivedOnly(current, [Line("已在畫面上", 0), Line("", 30)]));
    }
}
