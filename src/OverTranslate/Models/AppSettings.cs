using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
// Stored by name, so a new engine can go where it reads best without moving anyone's saved choice.
public enum TranslationProvider { Google, Google2, GoogleChrome, Bing, Microsoft, DeepL, OpenAI }

public class AppSettings
{
    /// <summary>
    /// Identifies this installation across the diagnostic reports it sends.
    /// </summary>
    /// <remarks>
    /// First in the file because it is what a maintainer opening a bundle looks for first, and
    /// deliberately named for what it is rather than for the one thing it is used for today — it
    /// identifies an install, and diagnostics is only the first thing that wants to know.
    ///
    /// Empty is a valid state and the one every existing install starts from. See
    /// <see cref="Services.AppIdentityService"/> for what fills it and when.
    /// </remarks>
    public string ID { get; set; } = "";

    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyVirtualKey { get; set; } = 0x41;
    public string HotkeyDisplay { get; set; } = "Ctrl+Alt+A";
    public ShortcutInputKind HotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton HotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// The shortcut that opens the translation window. Ctrl+Alt+W by default.
    /// </summary>
    /// <remarks>
    /// A convenience, not a headline: unlike the capture shortcut above it is not announced at
    /// startup and nothing in the interface advertises it, because the window it opens is already
    /// one click away in the tray. It opens and only opens — pressing it again brings the window
    /// forward rather than closing it, which is what every other way into this window does.
    /// </remarks>
    public uint TranslationWindowHotkeyModifiers { get; set; } = 3;

    public uint TranslationWindowHotkeyVirtualKey { get; set; } = 0x57;

    public string TranslationWindowHotkeyDisplay { get; set; } = "Ctrl+Alt+W";
    public ShortcutInputKind TranslationWindowHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton TranslationWindowHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <summary>
    /// Whether the translation-window shortcut is registered at all.
    /// </summary>
    /// <remarks>
    /// There is no matching field for the capture shortcut, and deliberately not: that one is the
    /// feature the application exists for, so its checkbox is ticked and disabled rather than backed
    /// by a value. A stored flag that must always be true is a way to end up with it false.
    /// </remarks>
    public bool TranslationWindowHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Pauses and resumes a running realtime session. Ctrl+Alt+S by default.
    /// </summary>
    /// <remarks>
    /// Stored as three fields — the modifiers and key Windows is given, plus the text the settings
    /// page shows — because the display string cannot be derived from the other two without a
    /// key-name table, and the recorder already has the user's own spelling of it at the moment they
    /// press the combination.
    ///
    /// Ctrl+Alt+S was the block-framing shortcut's default until that shortcut was removed: a session
    /// now begins by naming what it reads, which is a live window handle chosen from what is open,
    /// and no settings file can answer that. The key it left behind goes to the one realtime shortcut
    /// there still is. Anyone who had already recorded their own combination keeps it — a default
    /// only fills in what nobody has answered.
    /// </remarks>
    public uint RealtimePauseHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public uint RealtimePauseHotkeyVirtualKey { get; set; } = 0x53;

    /// <inheritdoc cref="RealtimePauseHotkeyModifiers"/>
    public string RealtimePauseHotkeyDisplay { get; set; } = "Ctrl+Alt+S";
    public ShortcutInputKind RealtimePauseHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimePauseHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimePauseHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Summons 取詞翻譯's popup over whatever the user is reading. Ctrl+Alt+Q by default.
    /// </summary>
    /// <remarks>
    /// Flat, with every other shortcut: 設定 owns them as one set on one page, and a shortcut filed
    /// under the feature it starts would be the only one of the four the user could not find beside
    /// its siblings.
    ///
    /// Q because the three combinations already taken are the letters of what they do (A, W, S) and
    /// this one is the 取 of 取詞 — and because Ctrl+Alt+Q is claimed by nothing on a stock Windows.
    /// </remarks>
    public uint QuickLookupHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public uint QuickLookupHotkeyVirtualKey { get; set; } = 0x51;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public string QuickLookupHotkeyDisplay { get; set; } = "Ctrl+Alt+Q";
    public ShortcutInputKind QuickLookupHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton QuickLookupHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickLookupHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Replaces the selected text with its translation, in place. Ctrl+Alt+E by default.
    /// </summary>
    /// <remarks>
    /// E for 取代 — the letters the other four are named after are taken (A, W, S, Q), and this one
    /// is the shortcut people reach for while writing in a language that is not theirs, where what
    /// they want is the replacement rather than a window about it. Ctrl+Alt+E is claimed by nothing
    /// on a stock Windows.
    ///
    /// On by default, like every other optional shortcut here. It costs nothing until it is pressed,
    /// and a feature nobody can find is not a safer one — see
    /// <see cref="Views.QuickTranslate.QuickTranslateFlow"/> for what it does when there is nothing
    /// selected, which is nothing at all.
    /// </remarks>
    public uint QuickTranslateHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickTranslateHotkeyModifiers"/>
    public uint QuickTranslateHotkeyVirtualKey { get; set; } = 0x45;

    /// <inheritdoc cref="QuickTranslateHotkeyModifiers"/>
    public string QuickTranslateHotkeyDisplay { get; set; } = "Ctrl+Alt+E";
    public ShortcutInputKind QuickTranslateHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton QuickTranslateHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickTranslateHotkeyEnabled { get; set; } = true;

    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// The OpenAI-compatible server to talk to, or empty for
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.DefaultBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Empty rather than a copy of the default: stamping today's value into the settings file would
    /// freeze the user on it if it ever changed.
    ///
    /// The server, not the model — which model to ask it for lives on a profile, so that switching
    /// model does not ask the user to retype an address that has not moved. See
    /// <see cref="OpenAiModelProfile"/>.
    /// </remarks>
    public string OpenAiBaseUrl { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";

    public string Theme { get; set; } = "Dark";
    /// <summary>
    /// The interface language — one of the codes in
    /// <see cref="Services.LocalizationService.Options"/> ("zh-Hant", "zh-Hans", "en", "ja", "ko").
    /// Empty means "not chosen yet".
    /// </summary>
    /// <remarks>
    /// Empty rather than a hardcoded default so a first run can follow the OS language — see
    /// <see cref="Services.LocalizationService.ResolveSystemDefault"/>. Once the user picks one it
    /// is stored verbatim and the OS is never consulted again, because an explicit choice should
    /// survive someone changing their Windows display language.
    ///
    /// This is the interface language only. It has no bearing on
    /// <see cref="TargetLanguage"/> or <see cref="RealtimeSettings.TargetLanguage"/>: what someone reads the
    /// buttons in and what they want subtitles translated into are unrelated, and a Taiwanese user
    /// running the app in English still wants Chinese output.
    /// </remarks>
    public string UiLanguage { get; set; } = "";
    public bool AutoTranslateAfterSelection { get; set; } = false;
    public bool SaveScreenshotToDisk { get; set; } = false;
    /// <summary>Empty means "use ScreenshotSaveService.DefaultDirectory" (圖片\OverTranslate).</summary>
    public string ScreenshotSavePath { get; set; } = "";
    /// <summary>Off by default: Debug records the recognised text, i.e. the user's screen contents.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// The newest version the user has told us to stop interrupting them about, or empty for none.
    /// </summary>
    /// <remarks>
    /// Compared as a version rather than for equality, so it silences that release and nothing later:
    /// skipping 1.9.0 leaves 1.9.1 free to prompt again. It suppresses only the startup dialog — the
    /// nav rail still offers the update — because what the user declined was being interrupted, not
    /// the update itself. See <see cref="Services.UpdateNotifier"/>.
    /// </remarks>
    public string SkippedUpdateVersion { get; set; } = "";

    /// <summary>What 截圖翻譯 keeps between capture sessions, grouped.</summary>
    /// <remarks>
    /// The first grouped section in declaration order because 截圖翻譯 appears before 即時翻譯 in the
    /// product's feature order. Properties are written in this order, so appsettings.json keeps its
    /// flat legacy half first and its feature-owned groups together afterwards.
    /// </remarks>
    public CaptureSettings Capture { get; set; } = new();

    /// <summary>What 取詞翻譯 keeps between lookups, grouped.</summary>
    public QuickLookupSettings QuickLookup { get; set; } = new();

    /// <summary>Language preferences used only by quick translation.</summary>
    public QuickTranslateSettings QuickTranslate { get; set; } = new();

    /// <summary>What 即時翻譯 keeps between sittings, grouped.</summary>
    public RealtimeSettings Realtime { get; set; } = new();

    /// <summary>What the OCR debug overlay draws, grouped.</summary>
    public OcrDebugSettings OcrDebug { get; set; } = new();

    /// <summary>The OpenAI-compatible provider's prompt library, grouped.</summary>
    /// <remarks>
    /// Last, and the only group not named after one of the three features: this belongs to a
    /// translation service rather than to a page. See <see cref="OpenAiSettings"/> for why the
    /// endpoint and the model above it stayed flat.SelectedProfileId
    /// </remarks>
    public OpenAiSettings OpenAi { get; set; } = new();
}
