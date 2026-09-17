namespace OverTranslate.Services.Realtime;

/// <summary>
/// The cheap question put in front of the live path's two timed reads: is there anything in this
/// region at all?
/// </summary>
/// <remarks>
/// <para><see cref="RealtimeReadReason.Search"/> and <see cref="RealtimeReadReason.Rescan"/> both
/// run on a timer and are both usually spent on nothing — a region between subtitles, a region whose
/// text has not moved. Each of them was a full recognition. This is detection alone at a fraction of
/// the size, which answers the same question for about a fifth of the cost, so the timers can run
/// several times as often for what they cost today.</para>
///
/// <para><b>The size is the whole trick.</b> Asked at the size the mode reads with, the detector
/// finds boxes on every empty frame there is — 120 of 120 across three corpora — because the noise
/// it picks up is thrown away later, by recognition's confidence floor, not by the detector.
/// Shrink its input and the noise stops producing boxes while text carries on producing them.
/// Measured over 92 frames holding text the shipped size reads and 120 holding nothing any size
/// could read (OcrHarness <c>--text-presence</c>):</para>
///
/// <code>
///   detector size   rule              lets text through   turns empty away
///     0.30          any box                     98.9%              54.2%
///     0.30          score &gt; 0.6                97.8%              85.0%
///     0.40          score &gt; 0.6                96.7%              79.2%
///     0.30 / 0.40   score &gt; 0.6               100.0%              75.0%
///     shipped size  any box                    100.0%               0.0%
/// </code>
///
/// <para>Alternating is what makes it lossless. The two frames 0.30 lets through the floor on are
/// both found at 0.40 — one scores 0.56 where 0.40 gives it 0.80, the other produces no box at all
/// until 0.40 — so they are edge-of-size cases rather than text this cannot see. A gate that misses
/// text is the one failure that matters here, because a missed line is missed for as long as it is
/// on screen: the frame does not change, so neither does the answer.</para>
///
/// <para>What it costs: about 16ms at 0.30 and 25ms at 0.40 on a subtitle-sized region, against
/// 93ms for the recognition behind it. With a quarter of the empty frames still getting through,
/// a gated read averages roughly 44ms where an ungated one is 93ms.</para>
/// </remarks>
internal static class RealtimeGate
{
    /// <summary>The two detector sizes, as fractions of the region's long side.</summary>
    /// <remarks>
    /// Alternated rather than combined: running both every time would cost their sum for a recall
    /// that only matters across consecutive polls anyway. A line stays on screen for seconds and is
    /// looked at many times, so being found by either size within two polls is being found.
    /// </remarks>
    internal const double FirstFraction = 0.30;

    /// <inheritdoc cref="FirstFraction"/>
    internal const double SecondFraction = 0.40;

    /// <summary>
    /// Boxes at or below this score do not count as text.
    /// </summary>
    /// <remarks>
    /// Without it the gate turns away barely half the empty frames; with it, three quarters to
    /// eighty-five percent, and the text it lets through is unchanged at 0.40 and down by one frame
    /// at 0.30 — which the other size then catches. Above 0.6 it starts costing real text fast: at
    /// 0.7 the pair drops to 98.9% and at 0.8 it collapses to under 10%.
    /// </remarks>
    internal const float MinimumScore = 0.6f;

    /// <summary>
    /// Whether a region is big enough for the gate to be worth asking, at all.
    /// </summary>
    /// <remarks>
    /// Below this the recognition being avoided is already cheap, and the gate's own detection
    /// cannot get much smaller — the detector's input has a floor of 320 however small the region
    /// is — so the two costs converge and the gate is pure overhead. The same threshold
    /// <see cref="RealtimeDetectorSize.DownscaleMinSide"/> uses to stop downscaling, for the same
    /// reason: under it, there is nothing left to take away.
    /// </remarks>
    internal static bool WorthGating(int width, int height) =>
        Math.Max(width, height) >= RealtimeDetectorSize.DownscaleMinSide;

    /// <summary>The detector size for this poll, alternating between the two fractions.</summary>
    /// <param name="alternate">False for <see cref="FirstFraction"/>, true for the other.</param>
    internal static int SizeFor(int width, int height, bool alternate) =>
        RealtimeDetectorSize.RoundToStride(
            (int)(Math.Max(width, height) * (alternate ? SecondFraction : FirstFraction)));
}
