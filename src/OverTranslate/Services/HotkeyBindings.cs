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

    /// <summary>Whether this trigger is one of the observed mouse buttons.</summary>
    public bool IsMouse => Kind.IsMouse();

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
}

/// <param name="ShadowedBy">
/// The higher-priority action holding this trigger, or null when this one gets it. Carried so the
/// settings page can say which shortcut took it rather than only that this one is off.
/// </param>
public readonly record struct HotkeyBinding(
    HotkeyAction Action,
    ShortcutTrigger Trigger,
    bool Enabled,
    HotkeyAction? ShadowedBy)
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
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

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
    /// interface never offers it; the registration path asks again because settings.json is a text
    /// file someone can edit, which is the same reason the shadowing above is resolved here rather
    /// than left to Windows.
    /// </remarks>
    public static bool IsBindable(ShortcutTrigger trigger) =>
        trigger.Kind != ShortcutInputKind.Keyboard ||
        trigger.Modifiers != 0 ||
        KeysThatMayStandAlone.Contains(trigger.VirtualKey);

    private static ShortcutTrigger BuildTrigger(
        ShortcutInputKind kind,
        uint modifiers,
        uint virtualKey,
        GamepadShortcutButton gamepadButton) => kind switch
    {
        _ when kind.IsMouse() => ShortcutTrigger.Mouse(kind),
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
        };

        var claimed = new Dictionary<ShortcutTrigger, HotkeyAction>();
        var resolved = new List<HotkeyBinding>(declared.Length);

        foreach (var (action, trigger, enabled) in declared)
        {
            HotkeyAction? shadowedBy = null;

            // A disabled shortcut does not claim its trigger, so turning one off hands the trigger to
            // whatever was shadowed by it rather than leaving it reserved.
            if (enabled)
            {
                if (claimed.TryGetValue(trigger, out var holder)) shadowedBy = holder;
                else claimed[trigger] = action;
            }

            resolved.Add(new HotkeyBinding(action, trigger, enabled, shadowedBy));
        }

        return resolved;
    }

    /// <summary>The shortcuts to register/observe, in priority order.</summary>
    public static IEnumerable<HotkeyBinding> Active(AppSettings settings) =>
        Resolve(settings).Where(binding => binding.IsActive);
}
