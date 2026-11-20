using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The runtime half of the Ctrl+C loop fix (2026-11-20): a keyboard shortcut that arrives while
/// this application is sending keys itself, or just after, does not act.
/// </summary>
/// <remarks>
/// No real key is sent here. The loop is simulated — a "send" that synchronously delivers the
/// shortcut back to its own handler, which is what RegisterHotKey did with an injected Ctrl+C — so
/// the test can show it stops after one turn without locking the machine's input to prove it.
/// </remarks>
public class SyntheticKeyGuardTests
{
    private long _now = 1_000_000;

    private SyntheticKeyGuard NewGuard() => new(() => _now);

    private static readonly long GraceMs = (long)SyntheticKeyGuard.Grace.TotalMilliseconds;

    [Fact]
    public void Shortcuts_act_when_nothing_is_being_sent()
    {
        var guard = NewGuard();

        Assert.False(guard.IsSuppressing);
        Assert.True(guard.AllowsShortcut("test"));
    }

    [Fact]
    public void A_shortcut_during_a_send_and_within_the_grace_after_it_is_dropped()
    {
        var guard = NewGuard();

        var scope = guard.Enter();
        Assert.True(guard.IsSuppressing);

        _now += 5_000; // however long the send takes
        Assert.True(guard.IsSuppressing);

        scope.Dispose();
        Assert.True(guard.IsSuppressing);

        _now += GraceMs - 1;
        Assert.True(guard.IsSuppressing);

        _now += 1;
        Assert.False(guard.IsSuppressing);
    }

    [Fact]
    public void Nested_sends_hold_until_the_outermost_one_ends()
    {
        // A selection read encloses the chord it sends.
        var guard = NewGuard();

        var read = guard.Enter();
        guard.Enter().Dispose();

        _now += GraceMs * 10;
        Assert.True(guard.IsSuppressing);

        read.Dispose();
        _now += GraceMs;
        Assert.False(guard.IsSuppressing);
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_release_another_one()
    {
        var guard = NewGuard();

        var outer = guard.Enter();
        var inner = guard.Enter();
        inner.Dispose();
        inner.Dispose();

        _now += GraceMs * 10;
        Assert.True(guard.IsSuppressing);
        outer.Dispose();
    }

    [Fact]
    public void A_shortcut_bound_to_the_key_its_action_sends_runs_once_instead_of_forever()
    {
        var guard = NewGuard();
        var runs = 0;
        Action? press = null;

        // The action: copy the selection by sending the shortcut's own chord, which the system
        // delivers straight back as another press — the 取詞翻譯-on-Ctrl+C loop.
        press = () =>
        {
            if (!guard.AllowsShortcut("simulated")) return;
            if (++runs > 50) throw new InvalidOperationException("the loop did not stop");

            using (guard.Enter())
                press!();
        };

        press();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_press_the_send_left_in_the_queue_is_still_dropped_after_it_ends()
    {
        // WM_HOTKEY is posted while the key is down and may be handled only after the release.
        var guard = NewGuard();
        var runs = 0;
        var queued = new Queue<Action>();

        void Press()
        {
            if (!guard.AllowsShortcut("simulated")) return;
            runs++;
            using (guard.Enter())
                queued.Enqueue(Press);
        }

        Press();
        _now += 40;
        while (queued.TryDequeue(out var next)) next();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void The_user_pressing_again_after_the_grace_is_heard()
    {
        var guard = NewGuard();
        var runs = 0;

        void Press()
        {
            if (!guard.AllowsShortcut("simulated")) return;
            runs++;
            guard.Enter().Dispose();
        }

        Press();
        _now += GraceMs;
        Press();

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_chord_is_guarded_from_press_to_release()
    {
        // KeyboardInput guards every chord itself, so a sender added later is covered too.
        var guard = NewGuard();
        var seen = new List<bool>();

        await KeyboardInput.HoldChordAsync(
            () => seen.Add(guard.IsSuppressing),
            () => { seen.Add(guard.IsSuppressing); return Task.CompletedTask; },
            () => seen.Add(guard.IsSuppressing),
            guard);

        Assert.Equal([true, true, true], seen);
        Assert.True(guard.IsSuppressing); // the grace

        _now += GraceMs;
        Assert.False(guard.IsSuppressing);
    }

    [Fact]
    public async Task A_chord_that_fails_still_ends_its_guard()
    {
        var guard = NewGuard();

        await Assert.ThrowsAsync<InvalidOperationException>(() => KeyboardInput.HoldChordAsync(
            () => { },
            () => throw new InvalidOperationException("interrupted"),
            () => { },
            guard));

        _now += GraceMs;
        Assert.False(guard.IsSuppressing);
    }
}
