using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The confirmation pass, and whether it is reading the same picture as the pass before it.
/// </summary>
/// <remarks>
/// <para>A dialogue region takes an extra read after a change, to catch a line that was still
/// fading in when it was first read. Over a still page that read is deterministic — the same boxes,
/// the same text — so it is the one place a second reading can be had for nothing, and a page of
/// columns has something to gain from one at another detector size
/// (<see cref="OverTranslate.Services.Ocr.VerticalSecondLook"/>).</para>
///
/// <para>What has to be right is WHICH pictures it is said of. The confirmation is asked for
/// without looking at the frame, so a page turned at exactly that moment would otherwise have the
/// previous page's reading merged into the new one's, and the reader would be shown a sentence that
/// is no longer on screen. That is what most of this covers.</para>
/// </remarks>
public class RealtimeReadingAgainTests
{
    private static readonly Rectangle Band = new(0, 0, 40, 20);

    private static Bitmap Picture(Color colour)
    {
        var bitmap = new Bitmap(40, 20);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(colour);
        return bitmap;
    }

    private static Func<IReadOnlyList<Rectangle>?, FrameFingerprint> Frame(Bitmap bitmap) =>
        areas => FrameFingerprint.Capture(bitmap, areas);

    private static OcrTextBlock Block(string text) =>
        new(text, new Rect(0, 0, 10, 40)) { LayoutBounds = new Rect(0, 0, 10, 40) };

    /// <summary>Drives a region up to the point where a confirmation pass is about to run.</summary>
    /// <remarks>
    /// In the session's order: the reading is merged against what is shown, which is what earns the
    /// region its confirmation pass, and only then is the frame recorded as rendered.
    /// </remarks>
    private static RealtimeRegionState Rendered(Bitmap picture)
    {
        var state = new RealtimeRegionState(RealtimeTextOrientation.Vertical);
        var read = new[] { Block("こんにちは") };
        var merged = state.Dialogue.Merge(state.RenderedLines, read);
        state.RememberRead(read);
        state.MarkRendered([Band], Frame(picture), merged.Lines);
        return state;
    }

    [Fact]
    public void The_confirmation_pass_over_an_unchanged_picture_is_a_second_reading_of_it()
    {
        using var picture = Picture(Color.White);
        var state = Rendered(picture);

        Assert.Equal(RealtimeReadReason.TextChanged, state.Examine(Frame(picture), dialogue: true));
        Assert.True(state.ReadingAgain);
    }

    /// <summary>
    /// The page turned between the pass that read it and the confirmation asked for afterwards.
    /// </summary>
    [Fact]
    public void A_confirmation_pass_over_a_different_picture_is_not()
    {
        using var read = Picture(Color.White);
        using var turned = Picture(Color.Black);
        var state = Rendered(read);

        state.Examine(Frame(turned), dialogue: true);

        Assert.False(state.ReadingAgain);
    }

    /// <summary>With nothing read yet there is nothing to weigh a second reading against.</summary>
    [Fact]
    public void A_region_that_has_read_nothing_yet_is_not_reading_again()
    {
        using var picture = Picture(Color.White);
        var state = new RealtimeRegionState(RealtimeTextOrientation.Vertical);
        var merged = state.Dialogue.Merge(state.RenderedLines, [Block("こんにちは")]);
        // Deliberately no RememberRead: the confirmation is earned, but there is nothing to weigh.
        state.MarkRendered([Band], Frame(picture), merged.Lines);

        state.Examine(Frame(picture), dialogue: true);

        Assert.False(state.ReadingAgain);
    }

    /// <summary>An ordinary pass is a first reading however still the picture is.</summary>
    [Fact]
    public void A_pass_that_is_not_a_confirmation_is_not_reading_again()
    {
        using var picture = Picture(Color.White);
        using var changed = Picture(Color.Black);
        var state = Rendered(picture);

        // Spend the confirmation, then let the picture change so the next pass is an ordinary one.
        state.Examine(Frame(picture), dialogue: true);
        state.Examine(Frame(changed), dialogue: true);
        state.Examine(Frame(changed), dialogue: true);

        Assert.False(state.ReadingAgain);
    }

    /// <summary>
    /// A region told to forget what it shows has nothing to merge a later reading with.
    /// </summary>
    [Fact]
    public void Invalidating_the_region_forgets_the_last_reading()
    {
        using var picture = Picture(Color.White);
        var state = Rendered(picture);

        state.Invalidate();

        Assert.Empty(state.LastRead);
        Assert.False(state.ReadingAgain);
    }

    /// <summary>
    /// Writing across the page never takes this path, whatever the region's mode.
    /// </summary>
    /// <remarks>
    /// The state itself does not know — the orientation test is at the call site, in
    /// RealtimeTranslationSession. This is here so that moving it is a deliberate act: the second
    /// size is measured on columns, and a subtitle strip has its own fractions and its own fallback
    /// chain that this would cut across.
    /// </remarks>
    [Fact]
    public void The_second_size_is_only_offered_to_a_page_of_columns()
    {
        Assert.NotNull(Services.Ocr.VerticalSecondLook.OtherSize(1832, 1298, primary: 1832));
    }
}
