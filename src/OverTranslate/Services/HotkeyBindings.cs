using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>The global shortcuts, in priority order — see <see cref="HotkeyBindings"/>.</summary>
public enum HotkeyAction
{
    Capture,
    TranslationWindow,
    RealtimePause,
    QuickLookup,
    QuickTranslate,
    RealtimeCompare,
    RealtimeHide,
}

/// <summary>
/// One physical shortcut trigger. Keyboard uses modifiers + virtual key; the mouse buttons carry no
/// code, the button being the kind itself; gamepad stores exactly one XInput button value in
/// <see cref="Code"/>.
/// </summary>
public readonly record struct ShortcutTrigger(ShortcutInputKind Kind, uint Modifiers, uint Code)
{
    public static ShortcutTrigger Keyboard(uint modifiers, uint virtualKey) =>
        new(ShortcutInputKind.Keyboard, modifiers, virtualKey);

    public static ShortcutTrigger MouseMiddle() =>
        new(ShortcutInputKind.MouseMiddle, 0, 0);

    public static ShortcutTrigger Mouse(ShortcutInputKind kind) => new(kind, 0, 0);

    public static ShortcutTrigger Gamepad(GamepadShortcutButton button) =>
        new(ShortcutInputKind.Gamepad, 0, (uint)button);

    /// <summary>Ctrl twice, or Ctrl+C+C — the sequence being the kind itself, as with the mouse.</summary>
    public static ShortcutTrigger KeySequence(ShortcutInputKind kind) => new(kind, 0, 0);

    /// <summary>Whether this trigger is one of the observed mouse buttons.</summary>
    public bool IsMouse => Kind.IsMouse();

    /// <summary>Whether this trigger is one of the observed key sequences.</summary>
    public bool IsKeySequence => Kind.IsKeySequence();

    public uint VirtualKey => Kind == ShortcutInputKind.Keyboard ? Code : 0;
    public GamepadShortcutButton GamepadButton => Kind == ShortcutInputKind.Gamepad
        ? (GamepadShortcutButton)Code
        : GamepadShortcutButton.None;
}

/// <summary>Which shortcut kinds the mouse hook is responsible for.</summary>
public static class ShortcutInputKinds
{
    public static bool IsMouse(this ShortcutInputKind kind) => kind is
        ShortcutInputKind.MouseMiddle or
        ShortcutInputKind.MouseX1 or
        ShortcutInputKind.MouseX2;

    /// <summary>Which shortcut kinds the keyboard hook is responsible for.</summary>
    public static bool IsKeySequence(this ShortcutInputKind kind) => kind is
        ShortcutInputKind.CtrlDoubleTap or
        ShortcutInputKind.CtrlCC;
}

/// <param name="Enabled">
/// Whether the shortcut is set. False both when the user cleared it and when what is stored is a
/// trigger that may not be bound at all — see <paramref name="Refused"/>.
/// </param>
/// <param name="ShadowedBy">
/// The higher-priority action holding this trigger, or null when this one gets it. Carried so the
/// settings page can say which shortcut took it rather than only that this one is off.
/// </param>
/// <param name="Refused">
/// Set but holding a trigger <see cref="HotkeyBindings.IsBindable"/> refuses, so treated as not set.
/// Carried only so the one log line that says why can be written.
/// </param>
public readonly record struct HotkeyBinding(
    HotkeyAction Action,
    ShortcutTrigger Trigger,
    bool Enabled,
    HotkeyAction? ShadowedBy,
    bool Refused = false)
{
    /// <summary>Whether this one should actually be registered/observed.</summary>
    public bool IsActive => Enabled && ShadowedBy is null;

    // Compatibility/readability helpers for the keyboard registration path and existing tests.
    public ShortcutInputKind InputKind => Trigger.Kind;
    public uint Modifiers => Trigger.Modifiers;
    public uint VirtualKey => Trigger.VirtualKey;
    public GamepadShortcutButton GamepadButton => Trigger.GamepadButton;
}

/// <summary>
/// Works out which shortcuts are live when two of them want the same physical trigger.
/// </summary>
/// <remarks>
/// Keyboard combinations are registered with RegisterHotKey; the mouse and gamepad buttons are
/// observed by the application's global input listener. They all share the same conflict resolver so
/// one mouse button cannot silently do two different things, and the same is true of one gamepad
/// button.
///
/// The keyboard half is where the cost of not resolving it shows. Windows keys a registration by
/// window and combination, so the second claim on one is simply refused: <c>RegisterHotKey</c>
/// returns false and nothing else happens. Which of the two loses is then whichever happened to be
/// registered second, i.e. an ordering nobody chose.
///
/// The settings page already refuses to RECORD a trigger another shortcut holds, so a user cannot
/// create the clash by hand. Stored settings can still arrive in one anyway, and that is what this
/// exists for: shipping <see cref="HotkeyAction.RealtimePause"/> gave every existing installation a
/// Ctrl+Alt+S it never agreed to, and anyone who had already put Ctrl+Alt+S on the translation
/// window would have had one of the two stop working with no explanation.
///
/// So the order is declared rather than discovered, and it runs from the feature the application is
/// for down to the most recently added: capture, then the translation window, then pausing a
/// realtime session, then 取詞翻譯, then 替換翻譯. A shortcut shadowed by a higher one is reported
/// rather than silently dropped.
/// </remarks>
public static class HotkeyBindings
{
    /// <summary>Returns the trigger stored for one action.</summary>
    public static ShortcutTrigger TriggerFor(AppSettings settings, HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => BuildTrigger(
            settings.HotkeyInputKind,
            settings.HotkeyModifiers,
            settings.HotkeyVirtualKey,
            settings.HotkeyGamepadButton),
        HotkeyAction.TranslationWindow => BuildTrigger(
            settings.TranslationWindowHotkeyInputKind,
            settings.TranslationWindowHotkeyModifiers,
            settings.TranslationWindowHotkeyVirtualKey,
            settings.TranslationWindowHotkeyGamepadButton),
        HotkeyAction.RealtimePause => BuildTrigger(
            settings.RealtimePauseHotkeyInputKind,
            settings.RealtimePauseHotkeyModifiers,
            settings.RealtimePauseHotkeyVirtualKey,
            settings.RealtimePauseHotkeyGamepadButton),
        HotkeyAction.QuickLookup => BuildTrigger(
            settings.QuickLookupHotkeyInputKind,
            settings.QuickLookupHotkeyModifiers,
            settings.QuickLookupHotkeyVirtualKey,
            settings.QuickLookupHotkeyGamepadButton),
        HotkeyAction.QuickTranslate => BuildTrigger(
            settings.QuickTranslateHotkeyInputKind,
            settings.QuickTranslateHotkeyModifiers,
            settings.QuickTranslateHotkeyVirtualKey,
            settings.QuickTranslateHotkeyGamepadButton),
        HotkeyAction.RealtimeCompare => BuildTrigger(
            settings.RealtimeCompareHotkeyInputKind,
            settings.RealtimeCompareHotkeyModifiers,
            settings.RealtimeCompareHotkeyVirtualKey,
            settings.RealtimeCompareHotkeyGamepadButton),
        HotkeyAction.RealtimeHide => BuildTrigger(
            settings.RealtimeHideHotkeyInputKind,
            settings.RealtimeHideHotkeyModifiers,
            settings.RealtimeHideHotkeyVirtualKey,
            settings.RealtimeHideHotkeyGamepadButton),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>The text 設定 shows for one action's shortcut, whether or not it is set.</summary>
    public static string DisplayFor(AppSettings settings, HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => settings.HotkeyDisplay,
        HotkeyAction.TranslationWindow => settings.TranslationWindowHotkeyDisplay,
        HotkeyAction.RealtimePause => settings.RealtimePauseHotkeyDisplay,
        HotkeyAction.QuickLookup => settings.QuickLookupHotkeyDisplay,
        HotkeyAction.QuickTranslate => settings.QuickTranslateHotkeyDisplay,
        HotkeyAction.RealtimeCompare => settings.RealtimeCompareHotkeyDisplay,
        HotkeyAction.RealtimeHide => settings.RealtimeHideHotkeyDisplay,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>
    /// The keys to draw one at a time for a shortcut — Ctrl, Alt, S — or none when it is not set.
    /// </summary>
    /// <remarks>
    /// Split on the "+" only for the keyboard and the key sequences, whose display is built from key
    /// names joined that way. A mouse or controller button is one thing to press and its name is
    /// localized prose, so it stays whole.
    /// </remarks>
    public static IReadOnlyList<string> KeysFor(AppSettings settings, HotkeyAction action)
    {
        var binding = Resolve(settings).Single(b => b.Action == action);
        var display = DisplayFor(settings, action)?.Trim() ?? "";
        if (!binding.Enabled || display.Length == 0) return [];

        return binding.Trigger.Kind == ShortcutInputKind.Keyboard || binding.Trigger.IsKeySequence
            ? display.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [display];
    }

    // The virtual keys a shortcut may claim on its own, with no Ctrl/Alt/Shift beside them.
    //
    // RegisterHotKey does not observe a combination, it takes it: while it is registered the key
    // never reaches any other application, the foreground game included. That is affordable for a
    // key nothing else needs and ruinous for one the user types with — bind a bare A and the letter
    // stops working everywhere, including in the settings box they would have to type into to undo
    // it. So the offer is limited to keys that produce no text and edit nothing: the function keys,
    // and the two the rest of the system has no use for.
    //
    // The editing pad is deliberately absent. Insert, Delete, Home, End, PageUp and PageDown look
    // like spare keys and are not: they are how text is edited and pages are read, and losing one
    // globally is the same class of mistake as losing a letter.
    private static readonly HashSet<uint> KeysThatMayStandAlone =
    [
        .. Enumerable.Range(0x70, 24).Select(vk => (uint)vk), // F1–F24
        0x13, // Pause
        0x91, // Scroll Lock
    ];

    /// <summary>
    /// Whether a trigger may be bound at all, as opposed to whether anything else has taken it.
    /// </summary>
    /// <remarks>
    /// Only keyboard triggers can fail this: the mouse and controller buttons are observed rather
    /// than claimed, so binding one takes nothing away from anybody — see
    /// <see cref="GlobalAuxiliaryHotkeys"/>.
    ///
    /// Asked in two places on purpose. The settings page refuses to record what this refuses, so the
    /// interface never offers it; <see cref="Resolve"/> asks again and treats a refused trigger as
    /// not set, because settings.json is a text file someone can edit and an older version recorded
    /// combinations this now refuses.
    /// </remarks>
    public static bool IsBindable(ShortcutTrigger trigger) =>
        trigger.Kind != ShortcutInputKind.Keyboard ||
        (trigger.Modifiers != 0
            ? !IsSentByThisApplication(trigger)
            : KeysThatMayStandAlone.Contains(trigger.VirtualKey));

    /// <summary>
    /// Whether this is one of the chords 取詞翻譯 and 替換翻譯 send — Ctrl+C and Ctrl+V, exactly.
    /// </summary>
    /// <remarks>
    /// <para>Refused for every shortcut, not only the two that send them. On 取詞翻譯 or 替換翻譯 the
    /// shortcut fires itself: the copy it sends is the shortcut again, which sent hundreds a second
    /// until the whole desktop's input stopped (2026-11-20). On any other shortcut the loop cannot
    /// happen, but RegisterHotKey takes the combination from every application on the system, so
    /// copy or paste would stop working everywhere — and the copy those two features send would
    /// start a capture or open a window instead of copying.</para>
    ///
    /// <para>Exactly Ctrl and nothing else: the chord is sent with every other modifier released
    /// (see <see cref="KeyboardInput.SendControlChordAsync"/>), so Ctrl+Shift+C is never produced and
    /// stays available. Ctrl+C+C is a different trigger — observed, never claimed — and is not
    /// affected.</para>
    /// </remarks>
    public static bool IsSentByThisApplication(ShortcutTrigger trigger) =>
        trigger.Kind == ShortcutInputKind.Keyboard &&
        trigger.Modifiers == GlobalHotkey.MOD_CONTROL &&
        trigger.VirtualKey is KeyboardInput.VK_C or KeyboardInput.VK_V;

    /// <summary>
    /// The string key of the message the settings page shows for a trigger it refuses to record, or
    /// null when the trigger may be bound.
    /// </summary>
    /// <remarks>
    /// Ctrl+C has its own: it is mostly reached by someone aiming for Ctrl+C+C whose second C came
    /// too late, so its message says how that one is pressed.
    /// </remarks>
    public static string? RefusalKey(ShortcutTrigger trigger) =>
        IsBindable(trigger) ? null
        : !IsSentByThisApplication(trigger) ? "S.Settings.HotkeyNeedsModifier"
        : trigger.VirtualKey == KeyboardInput.VK_C ? "S.Settings.HotkeyCopyRefused"
        : "S.Settings.HotkeyPasteRefused";

    private static ShortcutTrigger BuildTrigger(
        ShortcutInputKind kind,
        uint modifiers,
        uint virtualKey,
        GamepadShortcutButton gamepadButton) => kind switch
    {
        _ when kind.IsMouse() => ShortcutTrigger.Mouse(kind),
        _ when kind.IsKeySequence() => ShortcutTrigger.KeySequence(kind),
        ShortcutInputKind.Gamepad when gamepadButton != GamepadShortcutButton.None =>
            ShortcutTrigger.Gamepad(gamepadButton),
        // A malformed/old setting that says Gamepad but has no button is safer as the keyboard it
        // already has stored than as a trigger that can never fire.
        _ => ShortcutTrigger.Keyboard(modifiers, virtualKey),
    };

    /// <summary>
    /// Every shortcut, in priority order, each marked with whether it is live and what took it.
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Resolve(AppSettings settings)
    {
        // Declaration order IS the priority order; nothing else encodes it.
        var declared = new (HotkeyAction Action, ShortcutTrigger Trigger, bool Enabled)[]
        {
            (HotkeyAction.Capture, TriggerFor(settings, HotkeyAction.Capture), true),
            (HotkeyAction.TranslationWindow,
                TriggerFor(settings, HotkeyAction.TranslationWindow),
                settings.TranslationWindowHotkeyEnabled),
            (HotkeyAction.RealtimePause,
                TriggerFor(settings, HotkeyAction.RealtimePause),
                settings.RealtimePauseHotkeyEnabled),
            (HotkeyAction.QuickLookup,
                TriggerFor(settings, HotkeyAction.QuickLookup),
                settings.QuickLookupHotkeyEnabled),
            (HotkeyAction.QuickTranslate,
                TriggerFor(settings, HotkeyAction.QuickTranslate),
                settings.QuickTranslateHotkeyEnabled),
            // Last: the newest, and live only while a realtime session is up.
            (HotkeyAction.RealtimeCompare,
                TriggerFor(settings, HotkeyAction.RealtimeCompare),
                settings.RealtimeCompareHotkeyEnabled),
            // After 原文對照 for the same reason: newer still, and held only for a session.
            (HotkeyAction.RealtimeHide,
                TriggerFor(settings, HotkeyAction.RealtimeHide),
                settings.RealtimeHideHotkeyEnabled),
        };

        var claimed = new Dictionary<ShortcutTrigger, HotkeyAction>();
        var resolved = new List<HotkeyBinding>(declared.Length);

        foreach (var (action, trigger, stored) in declared)
        {
            HotkeyAction? shadowedBy = null;

            // A stored trigger that may not be bound is as good as not set: not registered, shown as
            // 未設定, and holding nothing. Only the stored value is ignored — nothing is rewritten.
            var refused = stored && !IsBindable(trigger);
            var enabled = stored && !refused;

            // A disabled shortcut does not claim its trigger, so turning one off hands the trigger to
            // whatever was shadowed by it rather than leaving it reserved.
            if (enabled)
            {
                if (claimed.TryGetValue(trigger, out var holder)) shadowedBy = holder;
                else claimed[trigger] = action;
            }

            resolved.Add(new HotkeyBinding(action, trigger, enabled, shadowedBy, refused));
        }

        return resolved;
    }

    /// <summary>The shortcuts to register/observe, in priority order.</summary>
    public static IEnumerable<HotkeyBinding> Active(AppSettings settings) =>
        Resolve(settings).Where(binding => binding.IsActive);
}
