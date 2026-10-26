using System.Diagnostics;
using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A bubble's fade outlasts the build it began in: the overlay is drawn again whole whenever
/// anything changes, and a bubble drawn again mid-fade carries on from where it was.
/// </summary>
public class ArrivalFadesTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    private static long At(long start, int ms) => start + ms * Stopwatch.Frequency / 1000;

    [Fact]
    public void AnArrivalStartsFromTheBeginning()
    {
        var fades = new ArrivalFades(Duration);
        var start = Stopwatch.GetTimestamp();

        var fading = fades.Next([3], start);

        Assert.Equal(TimeSpan.Zero, Assert.Single(fading, pair => pair.Key == 3).Value);
    }

    /// <summary>
    /// The last translation of a capture and the whole answer, one frame apart: the answer's build
    /// draws that bubble again, and it goes on fading rather than appearing at full strength.
    /// </summary>
    [Fact]
    public void ABuildBeforeTheFadeIsOverCarriesItOn()
    {
        var fades = new ArrivalFades(Duration);
        var start = Stopwatch.GetTimestamp();
        fades.Next([3], start);

        var fading = fades.Next([], At(start, 10));

        var elapsed = Assert.Single(fading, pair => pair.Key == 3).Value;
        Assert.InRange(elapsed.TotalMilliseconds, 9, 11);
    }

    [Fact]
    public void AFadeThatIsOverIsForgotten()
    {
        var fades = new ArrivalFades(Duration);
        var start = Stopwatch.GetTimestamp();
        fades.Next([3], start);

        Assert.Empty(fades.Next([], At(start, 150)));
        Assert.Empty(fades.Next([], At(start, 160)));
    }

    [Fact]
    public void EachPlaceFadesOnItsOwnClock()
    {
        var fades = new ArrivalFades(Duration);
        var start = Stopwatch.GetTimestamp();
        fades.Next([1], start);

        var fading = fades.Next([2], At(start, 100));

        Assert.InRange(fading[1].TotalMilliseconds, 99, 101);
        Assert.Equal(TimeSpan.Zero, fading[2]);
        Assert.Equal([2], fades.Next([], At(start, 160)).Keys);
    }

    [Fact]
    public void ClearingForgetsEveryFade()
    {
        var fades = new ArrivalFades(Duration);
        var start = Stopwatch.GetTimestamp();
        fades.Next([1, 2], start);

        fades.Clear();

        Assert.Empty(fades.Next([], At(start, 10)));
    }
}
