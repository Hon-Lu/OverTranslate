using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What a screenshot capture does to a realtime session it finds on screen. The rule is small; what
/// makes it worth pinning is that three of the four answers are invisible until the user comes back
/// to a session that is in the wrong state.
/// </summary>
public class RealtimeCaptureInterludeTests
{
    [Fact]
    public void Without_a_session_a_capture_starts_and_owes_nothing()
    {
        var interlude = RealtimeCaptureInterlude.For(sessionActive: false, translating: false, alreadyPaused: false);

        Assert.True(interlude.Allowed);
        Assert.False(interlude.HideLayers);
        Assert.False(interlude.PauseWatching);
    }

    // The screenshot would be of the edit layer, which covers the whole screen, and there is no
    // watching to pause — the two halves of why this is the one state still refused.
    [Fact]
    public void While_blocks_are_being_framed_the_capture_is_refused()
    {
        var interlude = RealtimeCaptureInterlude.For(sessionActive: true, translating: false, alreadyPaused: false);

        Assert.False(interlude.Allowed);
    }

    [Fact]
    public void A_watching_session_is_paused_and_taken_off_the_screen()
    {
        var interlude = RealtimeCaptureInterlude.For(sessionActive: true, translating: true, alreadyPaused: false);

        Assert.True(interlude.Allowed);
        Assert.True(interlude.HideLayers);
        Assert.True(interlude.PauseWatching);
    }

    /// <summary>
    /// The one a user would notice: 暫停 is theirs, so a capture taken while a session is already
    /// paused must not resume it on the way out. The layers still go, because the bar and the blocks
    /// are on screen either way and would otherwise be photographed.
    /// </summary>
    [Fact]
    public void A_session_the_user_paused_is_not_resumed_by_the_capture()
    {
        var interlude = RealtimeCaptureInterlude.For(sessionActive: true, translating: true, alreadyPaused: true);

        Assert.True(interlude.Allowed);
        Assert.True(interlude.HideLayers);
        Assert.False(interlude.PauseWatching);
    }
}
