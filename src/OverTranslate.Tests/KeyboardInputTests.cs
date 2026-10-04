using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The shape of the chords 取詞翻譯 and 快速翻譯 synthesise into somebody else's window.
/// </summary>
/// <remarks>
/// The keystrokes themselves are not testable here — they go to whatever has the foreground, which
/// in a test run is the runner. What is pinned is the order, because getting it wrong leaves a
/// modifier held down in another application.
/// </remarks>
public class KeyboardInputTests
{
    [Fact]
    public async Task The_chord_is_released_only_after_its_hold_interval()
    {
        var events = new List<string>();

        await KeyboardInput.HoldChordAsync(
            () => events.Add("press"),
            () => { events.Add("hold"); return Task.CompletedTask; },
            () => events.Add("release"));

        Assert.Equal(["press", "hold", "release"], events);
    }

    [Fact]
    public async Task A_chord_that_fails_while_held_is_still_released()
    {
        // Otherwise the failure costs the user their Ctrl key in whatever they were working in,
        // which is the whole keyboard broken rather than one translation missed.
        var released = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => KeyboardInput.HoldChordAsync(
            () => { },
            () => throw new InvalidOperationException("interrupted"),
            () => released = true));

        Assert.True(released);
    }
}
