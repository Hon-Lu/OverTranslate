using System.Text.Json;
using Xunit;

namespace OverTranslate.Tests.Imaging;

/// <summary>
/// The managed image routines against outputs recorded from OpenCV 4.13.0, which they replaced:
/// every repair scene and every routine case must hash to exactly what OpenCV produced.
/// </summary>
/// <remarks>
/// Exact, not close. The repair compares these outputs against thresholds at every step — an Otsu
/// level scaled by four fifths, a share of 250 out of 255, a radius of five — so a rounding of
/// difference anywhere is a pixel of mask of difference somewhere, and "close" cannot be told apart
/// from "drifting". See <see cref="GoldenCases"/> for what the cases are and how the hashes were
/// recorded.
/// </remarks>
public class ImagingGoldenTests
{
    private static readonly Lazy<Dictionary<string, string>> Expected = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Imaging", "golden.json")))!);

    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (var (name, _) in GoldenCases.All()) names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MatchesWhatOpenCvProduced(string name)
    {
        var run = GoldenCases.All().Single(c => c.Name == name).Run;
        Assert.True(Expected.Value.TryGetValue(name, out var expected), $"No recorded output for {name}.");
        Assert.Equal(expected, GoldenCases.Hash(run()));
    }

    [Fact]
    public void EveryRecordedCaseStillExists()
    {
        var names = GoldenCases.All().Select(c => c.Name).ToHashSet();
        Assert.DoesNotContain(Expected.Value.Keys, k => !names.Contains(k));
    }
}
