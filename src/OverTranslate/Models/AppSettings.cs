using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
// Stored by name, so a new engine can go where it reads best without moving anyone's saved choice.
// Google is 「Google 翻譯 (標準)」 — the Web endpoint backed by the RPC one, which write alike.
// Google2 was 「Google 翻譯 (RPC)」 as a choice of its own; it stays so files that saved it still
// read, and is read as Google (LanguageData.CurrentProvider). GoogleChrome is 「(Beta)」.
public enum TranslationProvider { Google, Google2, GoogleChrome, Bing, Microsoft, DeepL, OpenAI, Youdao, TranSmart, Yandex, LocalAI }

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
    /// Whether the translation-window shortcut is set at all. False is what 設定 shows as 未設定.
    /// </summary>
    /// <remarks>
    /// There is no matching field for the capture shortcut, and deliberately not: that one is the
    /// feature the application exists for, so its row offers no way to clear it rather than a value
    /// behind one. A stored flag that must always be true is a way to end up with it false.
    ///
    /// These flags used to be a switch beside each shortcut; #398 replaced the switch with clearing.
    /// The combination stays stored when the flag goes false, but nothing offers it back: the only
    /// way from 未設定 to a live shortcut is recording one, and recording is where a combination
    /// another shortcut holds is refused. The switch put the old combination back without that
    /// check, which is how two shortcuts used to end up on one key — so no "restore the last key"
    /// or "restore default" may be added that skips it either.
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
    /// Summons 取詞翻譯's popup over whatever the user is reading. Ctrl+C+C by default (#321).
    /// </summary>
    /// <remarks>
    /// Flat, with every other shortcut: 設定 owns them as one set on one page, and a shortcut filed
    /// under the feature it starts would be the only one of the four the user could not find beside
    /// its siblings.
    ///
    /// Ctrl+C+C because selecting and copying is what someone about to look a word up is already
    /// doing, and the first Ctrl+C is the copy the popup then reads — nothing is synthesised and the
    /// clipboard is left as the user made it. It also claims no combination from anyone. The cost is
    /// programs run as administrator, which a listened-for shortcut cannot hear while they are in
    /// front; the settings row says so. Only new installations get it: every saved settings file
    /// carries the lookup kind, so one that has Ctrl+Alt+Q keeps it.
    ///
    /// The keyboard fields keep Ctrl+Alt+Q, Q being the 取 of 取詞, as what a user who switches the
    /// kind back to the keyboard would have had.
    /// </remarks>
    public uint QuickLookupHotkeyModifiers { get; set; } = 3;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public uint QuickLookupHotkeyVirtualKey { get; set; } = 0x51;

    /// <inheritdoc cref="QuickLookupHotkeyModifiers"/>
    public string QuickLookupHotkeyDisplay { get; set; } = "Ctrl+C+C";
    public ShortcutInputKind QuickLookupHotkeyInputKind { get; set; } = ShortcutInputKind.CtrlCC;
    public GamepadShortcutButton QuickLookupHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool QuickLookupHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 替換翻譯's shortcut: replaces the selected text with its translation, in place. Ctrl+Alt+E
    /// by default.
    /// </summary>
    /// <remarks>
    /// QuickTranslate in these names is 替換翻譯's name from before the rename; they are settings
    /// file keys and keep it — see <see cref="QuickTranslateSettings"/>.
    ///

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

    /// <summary>
    /// Switches a running realtime session's 原文對照 on and off (#398). Not set by default.
    /// </summary>
    /// <remarks>
    /// Unset by default, unlike the shortcuts above: it is for the reader who flips between the
    /// overlay and the original all the time, and the bar's button says 未設定快捷鍵 for them to
    /// find. Nobody else is handed a combination they never asked for.
    ///
    /// Registered only while a session is up, not from startup — see
    /// <c>MainWindow.SyncSessionHotkey</c>. Outside a session it would do nothing, and a
    /// registered combination is taken from every other application for as long as it is held.
    /// </remarks>
    public uint RealtimeCompareHotkeyModifiers { get; set; }

    /// <inheritdoc cref="RealtimeCompareHotkeyModifiers"/>
    public uint RealtimeCompareHotkeyVirtualKey { get; set; }

    /// <inheritdoc cref="RealtimeCompareHotkeyModifiers"/>
    public string RealtimeCompareHotkeyDisplay { get; set; } = "";
    public ShortcutInputKind RealtimeCompareHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimeCompareHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimeCompareHotkeyEnabled { get; set; }

    /// <summary>
    /// Hides and shows a running realtime session's translations without stopping the session.
    /// Not set by default.
    /// </summary>
    /// <remarks>
    /// Unset and held only while a session is up, for the reasons given on
    /// <see cref="RealtimeCompareHotkeyModifiers"/>; see <c>MainWindow.SyncSessionHotkey</c>.
    /// </remarks>
    public uint RealtimeHideHotkeyModifiers { get; set; }

    /// <inheritdoc cref="RealtimeHideHotkeyModifiers"/>
    public uint RealtimeHideHotkeyVirtualKey { get; set; }

    /// <inheritdoc cref="RealtimeHideHotkeyModifiers"/>
    public string RealtimeHideHotkeyDisplay { get; set; } = "";
    public ShortcutInputKind RealtimeHideHotkeyInputKind { get; set; } = ShortcutInputKind.Keyboard;
    public GamepadShortcutButton RealtimeHideHotkeyGamepadButton { get; set; } = GamepadShortcutButton.None;

    /// <inheritdoc cref="TranslationWindowHotkeyEnabled"/>
    public bool RealtimeHideHotkeyEnabled { get; set; }

    public string SourceLanguage { get; set; } = LanguageData.DefaultOcrSourceLanguage;
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;

    /// <summary>
    /// The services left out of the translation-service menus, by name. Empty: all offered.
    /// </summary>
    /// <remarks>
    /// Names rather than <see cref="TranslationProvider"/> values, so one this build does not know
    /// costs only itself instead of the whole list. Read through <see cref="ProviderVisibility"/>,
    /// never directly: that is where unknown names, retired ones and a list that hides everything
    /// are settled.
    /// </remarks>
    public List<string> HiddenProviders { get; set; } = [];

    /// <summary>The service that reads aloud, unless <see cref="SpeechFollowsProvider"/> names another.</summary>
    /// <remarks>Read through <see cref="SpeechProviders.Resolve"/>, which settles the two together.</remarks>
    public SpeechProvider SpeechProvider { get; set; } = SpeechProvider.Google;

    /// <summary>
    /// Whether the translation service's own voice reads aloud, where it has one, rather than
    /// <see cref="SpeechProvider"/>.
    /// </summary>
    public bool SpeechFollowsProvider { get; set; } = true;

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

    /// <summary>
    /// How long one OpenAI-compatible request may take, in seconds, or null for
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.DefaultTimeoutSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Null rather than a copy of the default, for the same reason as <see cref="OpenAiBaseUrl"/>.
    /// A value under the minimum — a hand-edited file — is read as the minimum, see
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.TimeoutSecondsFor"/>.
    /// </remarks>
    public int? OpenAiTimeoutSeconds { get; set; }

    /// <summary>
    /// The key 「Google 翻譯 (Beta)」 sends, read from Google's translate element the first time it
    /// is used and again only when Google refuses it. Empty until then.
    /// </summary>
    /// <remarks>
    /// Kept here rather than written into the program, and kept at all so that a key that works is
    /// never looked up twice — see <c>GoogleChromeKeySource</c>.
    /// Named to end in ApiKey so the diagnostic bundle hides it like the user's own keys.
    /// </remarks>
    public string GoogleChromeApiKey { get; set; } = "";

    /// <summary>
    /// The host 「騰訊翻譯」 last reached, of the two Tencent serves it under. Empty until a host other
    /// than the first has answered.
    /// </summary>
    /// <remarks>
    /// Kept so that a user the first host does not work for — outside mainland China, where its
    /// server's certificate expired on 2026-10-11 — does not pay a failed handshake before the first
    /// translation of every run. A host kept here that later stops working is moved on from like any
    /// other; see <c>EngineHosts</c>.
    /// </remarks>
    public string TranSmartHost { get; set; } = "";

    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// The version that last started with this file, written at every start that finds a different
    /// one. Empty in a file from 2.9.0 or earlier, which never wrote it.
    /// </summary>
    /// <remarks>
    /// Read once, before anything is saved, to decide whether to keep a copy of the file from before
    /// an update that changes what it means — see <see cref="Services.SettingsBackup"/>.
    /// </remarks>
    public string LastRunVersion { get; set; } = "";

    /// <summary>
    /// The interface language — one of the codes in
    /// <see cref="Services.LocalizationService.Options"/>, such as "zh-Hant" or "en".
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

    /// <summary>
    /// Whether Japanese vertical text is read with the downloaded manga models, when they are here
    /// and can run. Off sends it to the column pipeline, as though they were not downloaded.
    /// </summary>
    /// <remarks>
    /// Only means anything while the models are downloaded: the card shows the switch then and only
    /// then, and a delete or a new download puts it back on — see
    /// <see cref="Services.MangaModelOptions"/>. Turned off mostly to give a game the video memory.
    /// </remarks>
    public bool UseMangaModels { get; set; } = true;

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

    /// <summary>
    /// Where the models and runtimes were moved to (模型存放位置); empty for the default,
    /// <c>%LocalAppData%\OverTranslate-assets</c>.
    /// </summary>
    /// <remarks>
    /// Read once, before anything loads a model — see <see cref="Services.AppDataPaths.AssetsRoot"/>.
    /// The uninstall hook reads it, and <see cref="AssetsPendingDelete"/>, straight from the file:
    /// see <see cref="Services.Assets.SavedAssetsLocation"/>. Written only through
    /// <see cref="Services.SettingsService.UpdateAssetsLocation"/>.
    /// </remarks>
    public string AssetsLocation { get; set; } = "";

    /// <summary>
    /// The location the assets were just moved from, emptied at the start after the move; empty when
    /// there is nothing left to empty.
    /// </summary>
    public string AssetsPendingDelete { get; set; } = "";

    /// <summary>What 截圖翻譯 keeps between capture sessions, grouped.</summary>
    /// <remarks>
    /// The first grouped section in declaration order because 截圖翻譯 appears before 即時翻譯 in the
    /// product's feature order. Properties are written in this order, so appsettings.json keeps its
    /// flat legacy half first and its feature-owned groups together afterwards.
    /// </remarks>
    public CaptureSettings Capture { get; set; } = new();

    /// <summary>What 取詞翻譯 keeps between lookups, grouped.</summary>
    public QuickLookupSettings QuickLookup { get; set; } = new();

    /// <summary>
    /// Language preferences used only by 替換翻譯 — named QuickTranslate from before the rename, see
    /// <see cref="QuickTranslateSettings"/>.
    /// </summary>
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

    /// <summary>What 本機 AI 翻譯 keeps, apart from the OpenAI-compatible service's own.</summary>
    public LocalAiSettings LocalAi { get; set; } = new();

    /// <summary>The glossaries the two AI services translate names with — see <see cref="GlossarySettings"/>.</summary>
    /// <remarks>
    /// Only <see cref="GlossarySettings.ActiveId"/> is written to this file. The lists are the user's
    /// own data rather than settings and live in <c>glossaries.json</c> beside it — see
    /// <see cref="Services.GlossaryStore"/>.
    /// </remarks>
    public GlossarySettings Glossary { get; set; } = new();
}
