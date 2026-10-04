using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The screenshot overlay's half of issue #73: a bubble is never shorter than the text inside it.
/// </summary>
/// <remarks>
/// The visible symptom was <c>TextTrimming.CharacterEllipsis</c>, and removing it was only half the
/// fix — the bubble clips its contents, so a height capped to the gap above the next block dropped
/// the tail just as thoroughly, and silently. Both halves have to hold for a translation to survive.
/// </remarks>
public class OverlayBubbleHeightTests
{
    // A one-line source and a translation that wrapped onto three: the shape the bug needed.
    private const double SourceHeight = 30;
    private const double PreferredHeight = 30;
    private const double WrappedTextHeight = 74;

    [Fact]
    public void With_nothing_below_it_the_bubble_grows_to_the_text()
    {
        Assert.Equal(
            WrappedTextHeight,
            OverlayBubbleHeight.ForWrapped(SourceHeight, PreferredHeight, WrappedTextHeight, null));
    }

    [Fact]
    public void A_gap_that_the_text_fits_in_is_respected()
    {
        // 74 of text with 90 of room: the bubble takes what it needs and leaves the rest.
        Assert.Equal(
            WrappedTextHeight,
            OverlayBubbleHeight.ForWrapped(SourceHeight, PreferredHeight, WrappedTextHeight, 90));
    }

    [Fact]
    public void A_gap_too_small_for_the_text_does_not_cut_it_off()
    {
        // The old clamp returned 40 here and the bubble clipped the third line away. Reaching this
        // means no font size the caller tried fitted the gap, so the bubble grows past its
        // neighbour — which the reader can see, unlike the missing half of a sentence.
        Assert.Equal(
            WrappedTextHeight,
            OverlayBubbleHeight.ForWrapped(SourceHeight, PreferredHeight, WrappedTextHeight, 40));
    }

    [Fact]
    public void A_bubble_never_shrinks_below_the_source_it_covers()
    {
        // A block sitting directly under a multi-line source makes the gap shorter than the source
        // itself. Clamping to it left the last source line showing through from underneath.
        Assert.Equal(
            SourceHeight,
            OverlayBubbleHeight.ForWrapped(SourceHeight, PreferredHeight, 24, 18));
    }

    [Fact]
    public void Room_beyond_what_the_bubble_wants_is_left_alone()
    {
        // The cap is a ceiling, not a target: a bubble does not cover picture it has no text for.
        Assert.Equal(
            PreferredHeight,
            OverlayBubbleHeight.ForWrapped(SourceHeight, PreferredHeight, 22, 400));
    }
}
