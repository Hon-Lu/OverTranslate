using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class HotkeyBindingsTests
{
    private const uint CtrlAlt = 3;

    [Fact]
    public void EveryDefaultCombinationIsDistinctSoAllOfThemStayOn()
    {
        var active = HotkeyBindings.Active(new AppSettings()).ToList();

        Assert.Equal(
            [
                HotkeyAction.Capture,
                HotkeyAction.TranslationWindow,
                HotkeyAction.RealtimePause,
                HotkeyAction.QuickLookup,
                HotkeyAction.QuickTranslate,
            ],
            active.Select(binding => binding.Action));
    }

    [Fact]
    public void QuickLookupIsTheLastToBeOfferedACombinationSomebodyElseHolds()
    {
        // 取詞翻譯 shipped after the other three, so its default is the one that has to give way:
        // an existing installation may already have recorded Ctrl+Alt+Q somewhere else, and that is
        // a combination its owner picked against one they have never seen. (New installations get
        // Ctrl+C+C instead; an existing one keeps the Ctrl+Alt+Q stored here.)
        var settings = new AppSettings
        {
            QuickLookupHotkeyInputKind = ShortcutInputKind.Keyboard,
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x51,
            TranslationWindowHotkeyDisplay = "Ctrl+Alt+Q",
        };

        var lookup = HotkeyBindings.Resolve(settings)
            .Single(binding => binding.Action == HotkeyAction.QuickLookup);

        Assert.False(lookup.IsActive);
        Assert.Equal(HotkeyAction.TranslationWindow, lookup.ShadowedBy);
    }

    [Fact]
    public void TheShortcutAddedLastLosesToOneSomebodyAlreadyChose()
    {
        // The upgrade this whole type exists for. 暫停 / 繼續 defaults to Ctrl+Alt+S, and an existing
        // installation may already have put Ctrl+Alt+S on the translation window — a combination its
        // owner picked, against one they have never seen. Left to Windows, whichever registered
        // second would simply fail and the user would be told nothing.
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x53,
            TranslationWindowHotkeyDisplay = "Ctrl+Alt+S",
        };

        var resolved = HotkeyBindings.Resolve(settings);
        var window = resolved.Single(b => b.Action == HotkeyAction.TranslationWindow);
        var pause = resolved.Single(b => b.Action == HotkeyAction.RealtimePause);

        Assert.True(window.IsActive);
        Assert.False(pause.IsActive);
        Assert.Equal(HotkeyAction.TranslationWindow, pause.ShadowedBy);
    }

    [Fact]
    public void CaptureOutranksEverythingBecauseItIsWhatTheApplicationIsFor()
    {
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x41, // the capture default
            RealtimePauseHotkeyModifiers = CtrlAlt,
            RealtimePauseHotkeyVirtualKey = 0x41,
        };

        var resolved = HotkeyBindings.Resolve(settings);

        Assert.True(resolved.Single(b => b.Action == HotkeyAction.Capture).IsActive);
        Assert.Equal(
            HotkeyAction.Capture,
            resolved.Single(b => b.Action == HotkeyAction.TranslationWindow).ShadowedBy);
        Assert.Equal(
            HotkeyAction.Capture,
            resolved.Single(b => b.Action == HotkeyAction.RealtimePause).ShadowedBy);
    }

    [Fact]
    public void SwitchingOffTheHolderHandsTheCombinationDown()
    {
        // A shortcut that is off does not reserve its combination. Without this, turning the window
        // shortcut off would leave the pause one still shadowed by something no longer running,
        // which is the kind of state a user cannot reason their way out of.
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = CtrlAlt,
            TranslationWindowHotkeyVirtualKey = 0x53,
            TranslationWindowHotkeyEnabled = false,
        };

        var pause = HotkeyBindings.Resolve(settings)
            .Single(binding => binding.Action == HotkeyAction.RealtimePause);

        Assert.True(pause.IsActive);
        Assert.Null(pause.ShadowedBy);
    }

    [Fact]
    public void ASwitchedOffShortcutIsNotRegisteredEvenWithNothingInItsWay()
    {
        var settings = new AppSettings { RealtimePauseHotkeyEnabled = false };

        var pause = HotkeyBindings.Resolve(settings)
            .Single(binding => binding.Action == HotkeyAction.RealtimePause);

        Assert.False(pause.IsActive);
        Assert.Null(pause.ShadowedBy); // off by choice, not because something took it
        Assert.DoesNotContain(
            HotkeyBindings.Active(settings), binding => binding.Action == HotkeyAction.RealtimePause);
    }

    [Fact]
    public void CaptureCannotBeSwitchedOff()
    {
        // There is no setting for it — see AppSettings.TranslationWindowHotkeyEnabled for why — so
        // this pins that the resolver never reports it as anything but enabled.
        Assert.True(HotkeyBindings.Resolve(new AppSettings())
            .Single(binding => binding.Action == HotkeyAction.Capture).Enabled);
    }

    [Fact]
    public void MiddleMouseUsesTheSamePriorityRulesAsKeyboard()
    {
        var settings = new AppSettings
        {
            TranslationWindowHotkeyInputKind = ShortcutInputKind.MouseMiddle,
            RealtimePauseHotkeyInputKind = ShortcutInputKind.MouseMiddle,
        };

        var resolved = HotkeyBindings.Resolve(settings);
        Assert.True(resolved.Single(b => b.Action == HotkeyAction.TranslationWindow).IsActive);
        Assert.Equal(HotkeyAction.TranslationWindow,
            resolved.Single(b => b.Action == HotkeyAction.RealtimePause).ShadowedBy);
    }

    [Fact]
    public void EachMouseButtonIsItsOwnTrigger()
    {
        // The three share a kind-only trigger with no code beside it, so nothing but the kind tells
        // them apart — if that ever stopped holding, two shortcuts on different buttons would read
        // as a clash and one of them would silently stop working.
        var settings = new AppSettings
        {
            TranslationWindowHotkeyInputKind = ShortcutInputKind.MouseX1,
            RealtimePauseHotkeyInputKind = ShortcutInputKind.MouseX2,
            QuickLookupHotkeyInputKind = ShortcutInputKind.MouseMiddle,
        };

        var resolved = HotkeyBindings.Resolve(settings);
        Assert.All(
            // 原文對照 starts unset, so it holds nothing to clash with.
            resolved.Where(b => b.Action is not (HotkeyAction.Capture or HotkeyAction.RealtimeCompare)),
            binding => Assert.True(binding.IsActive));
    }

    [Fact]
    public void TwoShortcutsOnTheSameSideButtonStillResolveByPriority()
    {
        var settings = new AppSettings
        {
            TranslationWindowHotkeyInputKind = ShortcutInputKind.MouseX1,
            RealtimePauseHotkeyInputKind = ShortcutInputKind.MouseX1,
        };

        var resolved = HotkeyBindings.Resolve(settings);
        Assert.True(resolved.Single(b => b.Action == HotkeyAction.TranslationWindow).IsActive);
        Assert.Equal(HotkeyAction.TranslationWindow,
            resolved.Single(b => b.Action == HotkeyAction.RealtimePause).ShadowedBy);
    }

    [Fact]
    public void ASideButtonSettingWinsOverAStaleGamepadButton()
    {
        // Switching a shortcut from the controller to the mouse leaves the old button in settings —
        // nothing clears it — so the stored kind has to be what decides.
        var settings = new AppSettings
        {
            RealtimePauseHotkeyInputKind = ShortcutInputKind.MouseX2,
            RealtimePauseHotkeyGamepadButton = GamepadShortcutButton.Y,
        };

        var trigger = HotkeyBindings.TriggerFor(settings, HotkeyAction.RealtimePause);
        Assert.Equal(ShortcutInputKind.MouseX2, trigger.Kind);
        Assert.True(trigger.IsMouse);
        Assert.Equal(GamepadShortcutButton.None, trigger.GamepadButton);
    }

    [Fact]
    public void GamepadButtonCanBeAUniqueShortcut()
    {
        var settings = new AppSettings
        {
            RealtimePauseHotkeyInputKind = ShortcutInputKind.Gamepad,
            RealtimePauseHotkeyGamepadButton = GamepadShortcutButton.X,
        };

        var single = HotkeyBindings.Resolve(settings)
            .Single(b => b.Action == HotkeyAction.RealtimePause);

        Assert.True(single.IsActive);
        Assert.Equal(ShortcutInputKind.Gamepad, single.InputKind);
        Assert.Equal(GamepadShortcutButton.X, single.GamepadButton);
    }

    [Fact]
    public void AFunctionKeyMayStandAlone()
    {
        // The point of allowing a bare key at all: one key, reachable with the hand already on the
        // keyboard, and one nothing else on the machine is waiting for.
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(0, 0x74))); // F5
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(0, 0x87))); // F24
    }

    [Fact]
    public void ATypingKeyOnItsOwnIsRefused()
    {
        // RegisterHotKey takes the key from every other application, so binding a bare A would stop
        // the letter working everywhere — including in the box the user would have to type into to
        // change it back.
        Assert.False(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(0, 0x41))); // A
        Assert.False(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(0, 0x20))); // Space
        Assert.False(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(0, 0x2E))); // Delete
    }

    [Fact]
    public void TheSameTypingKeyIsFineWithAModifier()
    {
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(CtrlAlt, 0x41)));
    }

    [Fact]
    public void MouseAndGamepadTriggersAreNeverRefused()
    {
        // They are observed rather than claimed, so they take nothing away from anybody and the
        // question this rule answers does not arise.
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.MouseMiddle()));
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Mouse(ShortcutInputKind.MouseX1)));
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Mouse(ShortcutInputKind.MouseX2)));
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Gamepad(GamepadShortcutButton.Y)));
    }

    [Fact]
    public void KeyboardAndGamepadWithSameNumericCodeDoNotCollide()
    {
        var settings = new AppSettings
        {
            TranslationWindowHotkeyModifiers = 0,
            TranslationWindowHotkeyVirtualKey = 0x58,
            RealtimePauseHotkeyInputKind = ShortcutInputKind.Gamepad,
            RealtimePauseHotkeyGamepadButton = GamepadShortcutButton.X,
        };

        Assert.True(HotkeyBindings.Resolve(settings)
            .Single(b => b.Action == HotkeyAction.RealtimePause).IsActive);
    }

    [Fact]
    public void AKeySequenceIsReadBackAsItselfWhateverKeyIsStoredBesideIt()
    {
        // The keyboard fields keep whatever was recorded before; the kind is what decides.
        var settings = new AppSettings
        {
            QuickLookupHotkeyInputKind = ShortcutInputKind.CtrlCC,
            QuickTranslateHotkeyInputKind = ShortcutInputKind.CtrlDoubleTap,
        };

        Assert.Equal(
            ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlCC),
            HotkeyBindings.TriggerFor(settings, HotkeyAction.QuickLookup));
        Assert.Equal(
            ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlDoubleTap),
            HotkeyBindings.TriggerFor(settings, HotkeyAction.QuickTranslate));
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlCC)));
    }

    [Fact]
    public void OneKeySequenceCannotDoTwoThings()
    {
        var settings = new AppSettings
        {
            QuickLookupHotkeyInputKind = ShortcutInputKind.CtrlCC,
            QuickTranslateHotkeyInputKind = ShortcutInputKind.CtrlCC,
        };

        var replace = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickTranslate);

        Assert.Equal(HotkeyAction.QuickLookup, replace.ShadowedBy);
    }

    [Fact]
    public void TheCompareShortcutStartsUnset()
    {
        // #398: only the reader who flips between overlay and original needs it, so nobody is handed
        // a combination they never asked for.
        var compare = HotkeyBindings.Resolve(new AppSettings())
            .Single(b => b.Action == HotkeyAction.RealtimeCompare);

        Assert.False(compare.Enabled);
        Assert.Empty(HotkeyBindings.KeysFor(new AppSettings(), HotkeyAction.RealtimeCompare));
    }

    [Fact]
    public void TheCompareShortcutGivesWayToEveryOtherShortcut()
    {
        var settings = new AppSettings
        {
            RealtimeCompareHotkeyEnabled = true,
            RealtimeCompareHotkeyModifiers = CtrlAlt,
            RealtimeCompareHotkeyVirtualKey = 0x53,
            RealtimeCompareHotkeyDisplay = "Ctrl+Alt+S",
        };

        var compare = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.RealtimeCompare);

        Assert.Equal(HotkeyAction.RealtimePause, compare.ShadowedBy);
    }

    [Fact]
    public void AnUnsetShortcutLeavesItsCombinationForTheNextOne()
    {
        // Clearing replaced the switch, with the same rule: what is not set holds nothing, and its
        // combination stays stored but out of the way.
        var settings = new AppSettings
        {
            RealtimePauseHotkeyEnabled = false,
            RealtimeCompareHotkeyEnabled = true,
            RealtimeCompareHotkeyModifiers = CtrlAlt,
            RealtimeCompareHotkeyVirtualKey = 0x53,
            RealtimeCompareHotkeyDisplay = "Ctrl+Alt+S",
        };

        var compare = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.RealtimeCompare);

        Assert.True(compare.IsActive);
        Assert.Equal("Ctrl+Alt+S", settings.RealtimePauseHotkeyDisplay);
    }

    [Fact]
    public void KeysAreDrawnOneCapAtATime()
    {
        var settings = new AppSettings();

        Assert.Equal(["Ctrl", "Alt", "S"], HotkeyBindings.KeysFor(settings, HotkeyAction.RealtimePause));
        Assert.Equal(["Ctrl", "C", "C"], HotkeyBindings.KeysFor(settings, HotkeyAction.QuickLookup));
    }

    [Fact]
    public void AnUnsetShortcutHasNoKeysEvenWithACombinationStored()
    {
        var settings = new AppSettings { RealtimePauseHotkeyEnabled = false };

        Assert.Empty(HotkeyBindings.KeysFor(settings, HotkeyAction.RealtimePause));
    }

    private const uint Ctrl = 2;
    private const uint VkC = 0x43;
    private const uint VkV = 0x56;

    [Fact]
    public void TheCopyAndPasteThisApplicationSendsCannotBeShortcuts()
    {
        // 2026-11-20: 取詞翻譯 on Ctrl+C fired itself with its own copy until the desktop's input
        // stopped. Refused for every shortcut — elsewhere it would take copy and paste from the system.
        Assert.False(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(Ctrl, VkC)));
        Assert.False(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(Ctrl, VkV)));
    }

    [Fact]
    public void OnlyTheExactChordIsRefused()
    {
        // The chord is sent with every other modifier released, so these are never produced.
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(Ctrl | 4, VkC))); // Ctrl+Shift+C
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(CtrlAlt, VkV)));  // Ctrl+Alt+V
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.Keyboard(Ctrl, 0x58)));    // Ctrl+X
        Assert.True(HotkeyBindings.IsBindable(ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlCC)));
    }

    [Fact]
    public void RecordingCtrlCSaysHowCtrlCCIsPressed()
    {
        // Mostly reached by a second C that came too late for Ctrl+C+C.
        Assert.Equal("S.Settings.HotkeyCopyRefused",
            HotkeyBindings.RefusalKey(ShortcutTrigger.Keyboard(Ctrl, VkC)));
        Assert.Equal("S.Settings.HotkeyPasteRefused",
            HotkeyBindings.RefusalKey(ShortcutTrigger.Keyboard(Ctrl, VkV)));
        Assert.Equal("S.Settings.HotkeyNeedsModifier",
            HotkeyBindings.RefusalKey(ShortcutTrigger.Keyboard(0, 0x41)));
        Assert.Null(HotkeyBindings.RefusalKey(ShortcutTrigger.Keyboard(CtrlAlt, 0x41)));
        Assert.Null(HotkeyBindings.RefusalKey(ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlCC)));
    }

    [Theory]
    [InlineData(HotkeyAction.QuickLookup)]
    [InlineData(HotkeyAction.QuickTranslate)]
    [InlineData(HotkeyAction.Capture)]
    public void AStoredCtrlCIsTreatedAsNotSetWithoutBeingRewritten(HotkeyAction action)
    {
        // The settings file the loop came from: 取詞翻譯 on Ctrl+C and switched on.
        var settings = WithKeyboard(new AppSettings(), action, Ctrl, VkC, "Ctrl+C");

        var binding = HotkeyBindings.Resolve(settings).Single(b => b.Action == action);

        Assert.False(binding.Enabled);
        Assert.False(binding.IsActive);
        Assert.True(binding.Refused);
        Assert.DoesNotContain(HotkeyBindings.Active(settings), b => b.Action == action);
        Assert.Empty(HotkeyBindings.KeysFor(settings, action));

        // Ignored, not rewritten: the stored values are exactly as they were.
        Assert.Equal(ShortcutTrigger.Keyboard(Ctrl, VkC), HotkeyBindings.TriggerFor(settings, action));
        Assert.Equal("Ctrl+C", HotkeyBindings.DisplayFor(settings, action));
        if (action == HotkeyAction.QuickLookup) Assert.True(settings.QuickLookupHotkeyEnabled);
    }

    [Fact]
    public void ARefusedShortcutHoldsNothing()
    {
        // Not set means not claiming: a lower shortcut on the same stored chord is not shadowed by it
        // — it is refused on its own account instead.
        var settings = WithKeyboard(new AppSettings(), HotkeyAction.QuickLookup, Ctrl, VkV, "Ctrl+V");
        settings = WithKeyboard(settings, HotkeyAction.QuickTranslate, Ctrl, VkV, "Ctrl+V");

        var replace = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickTranslate);

        Assert.Null(replace.ShadowedBy);
        Assert.True(replace.Refused);
    }

    [Fact]
    public void AnUnsetShortcutIsNotReportedAsRefused()
    {
        var settings = WithKeyboard(new AppSettings(), HotkeyAction.QuickLookup, Ctrl, VkC, "Ctrl+C");
        settings.QuickLookupHotkeyEnabled = false;

        var lookup = HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.QuickLookup);

        Assert.False(lookup.Enabled);
        Assert.False(lookup.Refused);
    }

    private static AppSettings WithKeyboard(
        AppSettings settings, HotkeyAction action, uint modifiers, uint vk, string display)
    {
        switch (action)
        {
            case HotkeyAction.Capture:
                settings.HotkeyInputKind = ShortcutInputKind.Keyboard;
                settings.HotkeyModifiers = modifiers;
                settings.HotkeyVirtualKey = vk;
                settings.HotkeyDisplay = display;
                break;
            case HotkeyAction.QuickLookup:
                settings.QuickLookupHotkeyInputKind = ShortcutInputKind.Keyboard;
                settings.QuickLookupHotkeyModifiers = modifiers;
                settings.QuickLookupHotkeyVirtualKey = vk;
                settings.QuickLookupHotkeyDisplay = display;
                settings.QuickLookupHotkeyEnabled = true;
                break;
            case HotkeyAction.QuickTranslate:
                settings.QuickTranslateHotkeyInputKind = ShortcutInputKind.Keyboard;
                settings.QuickTranslateHotkeyModifiers = modifiers;
                settings.QuickTranslateHotkeyVirtualKey = vk;
                settings.QuickTranslateHotkeyDisplay = display;
                settings.QuickTranslateHotkeyEnabled = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        return settings;
    }

    [Fact]
    public void AMouseButtonIsOneCapWithItsWholeName()
    {
        var settings = new AppSettings
        {
            RealtimePauseHotkeyInputKind = ShortcutInputKind.MouseX1,
            RealtimePauseHotkeyDisplay = "滑鼠側鍵 1",
        };

        Assert.Equal(["滑鼠側鍵 1"], HotkeyBindings.KeysFor(settings, HotkeyAction.RealtimePause));
    }
}
