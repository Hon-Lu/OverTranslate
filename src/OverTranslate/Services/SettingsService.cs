using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

public class SettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Velopack installs each version into ...\OverTranslate\current\ and replaces that entire folder
    // on update. Settings used to live in BaseDirectory, i.e. inside current\, so every update wiped
    // them and the app came back up on factory defaults. Roaming AppData sits outside the install.
    private static readonly string SettingsDirectory = AppDataPaths.Root;

    private static readonly string SettingsPath = AppDataPaths.SettingsFile;

    /// <summary>
    /// Where the settings actually are, for the one caller that needs the file rather than the
    /// values in it: <see cref="DiagnosticBundleService"/> copies it into a problem report.
    /// </summary>
    public static string FilePath => SettingsPath;

    // Where 1.7.0 and earlier kept the file. Velopack has usually deleted it by the time an updated
    // build runs, but when it survives — a build relaunched in place, a dev run — those are still the
    // user's settings, so read them once on the way to the new location.
    private static readonly string LegacySettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    /// <summary>
    /// How the file is written: indented, and with the whole of Unicode left as itself.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes everything outside a conservative ASCII set, which writes a
    /// prompt someone named 「測試」 as an unbroken run of <c>\uXXXX</c>. This is a file a user can
    /// open — see <see cref="Services.Realtime.RealtimeSubtitleColors"/>, which is written on the
    /// same assumption — and now that it holds the prompt library, the names and the prose in it are
    /// the user's own words rather than paths and key codes.
    ///
    /// The same encoder <see cref="DiagnosticBundleService"/> uses for its redacted copy, and for
    /// the same reason it gives: this text goes into a file, never into a web page, so the escaping
    /// of <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> that guards against being embedded in HTML buys
    /// nothing here. Widening only the character range was tried first and is not enough — it
    /// leaves <c>+</c> escaped, which writes every shortcut in the file as
    /// <c>Ctrl\u002BAlt\u002BA</c>. Half a readable file is not the point.
    ///
    /// The two copies also then read alike, which is worth something on the one occasion both are
    /// in front of the same person: a bug report held against the file it came from.
    ///
    /// Cosmetic by construction: nothing reads this file as text, both forms parse to the same
    /// string, and <see cref="Parse"/> goes through a JSON reader either way.
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    public AppSettings Current { get; private set; } = new();

    /// <summary>Where the glossaries' entries are — see <see cref="GlossaryStore"/>.</summary>
    private readonly GlossaryStore _glossaries = new(AppDataPaths.GlossariesFile);

    private SettingsService()
    {
        Load();
    }

    public void Load()
    {
        // Read before the settings: loading them can save them, and a save has to know what the
        // glossaries file holds so as not to write the empty lists over it.
        var glossaries = _glossaries.Read();
        Current = Load(SettingsPath, LegacySettingsPath, SettingsBackup.CurrentVersion, settings =>
        {
            Current = settings;
            Save();
        }, glossaries);
    }

    /// <summary>
    /// <see cref="Load()"/> with the paths, the running version and the save passed in, for a test
    /// that cannot use the real ones.
    /// </summary>
    /// <remarks>
    /// The backup comes before every save: once anything is saved, the file it would copy is gone.
    /// </remarks>
    /// <param name="glossaries">The glossaries read from their own file, put together with the one in use the settings name.</param>
    internal static AppSettings Load(
        string path, string legacyPath, string version, Action<AppSettings> save,
        List<GlossaryList>? glossaries = null)
    {
        var (json, readFrom) = ReadFirstAvailable(path, legacyPath);
        if (json is null)
        {
            var fresh = new AppSettings { LastRunVersion = version };
            fresh.Glossary = WithLists(fresh.Glossary, glossaries);
            save(fresh);
            return fresh;
        }

        var settings = Parse(json);
        settings.Glossary = WithLists(settings.Glossary, glossaries);
        if (settings.LastRunVersion != version)
        {
            // The first start after an update (or after 2.9.0 or earlier, which never wrote a
            // version). Anything else a later version has to do once on its way in goes here too,
            // before the version is written: after that this branch is not taken again.
            if (SettingsBackup.Needed(settings.LastRunVersion))
                SettingsBackup.Copy(readFrom!, SettingsBackup.PathFor(path, version));

            settings.LastRunVersion = version;
            save(settings);
        }
        // Nothing at the canonical path means what we just read came from the legacy one. Persist it
        // now rather than waiting for the user to happen to change a setting.
        else if (!File.Exists(path))
            save(settings);
        return settings;
    }

    /// <summary>
    /// Reads the settings one field at a time so a single bad value cannot cost the user everything
    /// else. A file that is not JSON at all still falls back to defaults, but an individual field
    /// that cannot be read — an enum value a later build dropped, a type that changed, an explicit
    /// null — costs only that field. The previous all-or-nothing catch reset API keys and hotkeys
    /// alike whenever any one value went bad.
    /// </summary>
    public static AppSettings Parse(string json)
    {
        var settings = new AppSettings();

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "appsettings.json is not valid JSON; falling back to defaults");
            return settings;
        }

        if (root is null)
            return settings;

        Apply(settings, root, "");

        // Retired options, read as the ones that replaced them — the pickers no longer list them.
        settings.Provider = LanguageData.CurrentProvider(settings.Provider);
        settings.Realtime.Provider = LanguageData.CurrentProvider(settings.Realtime.Provider);

        // 替換翻譯's languages (QuickTranslate, its name before #277). The grouped section wins.
        // Older files used flat QuickTranslate* keys, and before that shared the text-translation
        // pair. Migrate only when the group is absent.
        if (!root.ContainsKey(nameof(AppSettings.QuickTranslate)))
        {
            string LegacyLanguage(string key, string shared) =>
                !root.TryGetPropertyValue(key, out var value) ? shared :
                value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

            settings.QuickTranslate.SourceLanguage = LanguageData.GetValidSourceCode(
                LegacyLanguage("QuickTranslateSourceLanguage", settings.SourceLanguage));
            var legacyTarget = LegacyLanguage("QuickTranslateTargetLanguage",
                root.ContainsKey(nameof(AppSettings.TargetLanguage)) ? settings.TargetLanguage : "");
            var target = LanguageData.TargetLanguages.FirstOrDefault(
                language => language.Code.Equals(legacyTarget, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
                settings.QuickTranslate.TargetLanguage = target.Code;
        }

        settings.Realtime.Profiles = RealtimeSettings.CleanProfiles(
            settings.Realtime.Profiles.Count > 0 ? settings.Realtime.Profiles : ReadProfilesOneByOne(root));

        // Same treatment for the kept framings: a list that would not read in one piece is read one
        // entry at a time, so a damaged block costs that preset rather than every one.
        settings.Realtime.FramingPresets = RealtimeSettings.CleanFramingPresets(
            settings.Realtime.FramingPresets.Count > 0
                ? settings.Realtime.FramingPresets
                : ReadFramingPresetsOneByOne(root));
        settings.Realtime.FramingSkippedTargets =
            RealtimeSettings.CleanFramingSkipped(settings.Realtime.FramingSkippedTargets);

        // The lists are not in this file (see GlossaryStore): a Glossary section with lists in it,
        // written by a build between #228 and the store, is read past and dropped, and the one in
        // use is checked against the lists once they are read (WithLists).
        settings.Glossary = new GlossarySettings { ActiveId = (settings.Glossary.ActiveId ?? "").Trim() };
        return settings;
    }

    /// <summary>
    /// <paramref name="read"/>'s choice of glossary with <paramref name="lists"/> under it, cleaned:
    /// a choice naming no list is no choice.
    /// </summary>
    private static GlossarySettings WithLists(GlossarySettings read, List<GlossaryList>? lists) =>
        GlossarySettings.Clean(new GlossarySettings { Lists = lists ?? [], ActiveId = read.ActiveId });

    /// <summary>
    /// The kept 即時翻譯 framings one at a time, for a list that would not read in one piece.
    /// </summary>
    private static IEnumerable<RealtimeFramingPreset?> ReadFramingPresetsOneByOne(JsonObject root)
    {
        var read = new List<RealtimeFramingPreset?>();
        if (root[nameof(AppSettings.Realtime)] is not JsonObject realtime ||
            realtime[nameof(RealtimeSettings.FramingPresets)] is not JsonArray entries) return read;

        foreach (var entry in entries)
        {
            try
            {
                read.Add(entry?.Deserialize<RealtimeFramingPreset>());
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring an unreadable realtime framing preset; keeping the others");
            }
        }
        return read;
    }

    /// <summary>
    /// The custom 即時翻譯 modes one at a time, for a list that would not read in one piece: a
    /// value of the wrong type in one mode costs that mode, not every mode the user made.
    /// </summary>
    private static List<RealtimeProfile?> ReadProfilesOneByOne(JsonObject root)
    {
        var read = new List<RealtimeProfile?>();
        if (root[nameof(AppSettings.Realtime)] is not JsonObject realtime ||
            realtime[nameof(RealtimeSettings.Profiles)] is not JsonArray entries) return read;

        foreach (var entry in entries)
        {
            try
            {
                read.Add(entry?.Deserialize<RealtimeProfile>());
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring an unreadable realtime mode; keeping the others");
            }
        }
        return read;
    }

    /// <summary>
    /// Copies one object's worth of JSON onto <paramref name="target"/>, field by field, descending
    /// into grouped sections.
    /// </summary>
    /// <remarks>
    /// The descent is what keeps the promise above once settings are grouped. Handing a whole group
    /// to <c>Deserialize</c> would make it the unit that fails: one unreadable value inside
    /// <see cref="AppSettings.Realtime"/> would throw, and every other value in that group would go
    /// back to its default with it. Read one at a time all the way down, and the blast radius stays
    /// one value however deep it sits.
    /// </remarks>
    /// <param name="path">Where in the file this object sits, for the log to name a field properly.</param>
    private static void Apply(object target, JsonObject source, string path)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            // Not written to the file, so not read from it either: what an older build left under
            // that name (GlossarySettings.Lists) is read past.
            if (!property.CanWrite || property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
                continue;

            // Absent and explicitly-null both leave the property on its initialiser, which keeps a
            // hand-edited "ApiKey": null from turning into a null string the rest of the app trips on.
            if (!source.TryGetPropertyValue(property.Name, out var node) || node is null)
                continue;

            var name = path + property.Name;

            if (node is JsonObject group && IsSettingsGroup(property.PropertyType))
            {
                // Never null: a group is always initialised by the class that declares it, so there
                // is something to write onto whatever the file says.
                if (property.GetValue(target) is { } child)
                    Apply(child, group, name + ".");
                continue;
            }

            try
            {
                var value = node.Deserialize(property.PropertyType);
                if (value is not null)
                    property.SetValue(target, value);
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring unreadable setting '{0}'; keeping its default", name);
            }
        }
    }

    /// <summary>
    /// Whether a property is a grouped section of the settings rather than a value.
    /// </summary>
    /// <remarks>
    /// Decided by where the type is declared, not by a list to keep in step: anything this
    /// application defines alongside <see cref="AppSettings"/> is a group of settings, and anything
    /// from the framework — string above all, which is a class and would otherwise qualify — is a
    /// value.
    /// </remarks>
    private static bool IsSettingsGroup(Type type) =>
        type is { IsClass: true, IsArray: false }
        && type != typeof(string)
        && type.Namespace == typeof(AppSettings).Namespace;

    /// <summary>The exact bytes <see cref="Save"/> writes, for a test that has no file to read.</summary>
    internal static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, WriteOptions);

    public event EventHandler? OcrDebugChanged;

    public void UpdateOcrDebug(bool? showLines = null, bool? showGroups = null, bool? showOnTranslation = null)
    {
        var debug = Current.OcrDebug;
        var lines = showLines ?? debug.ShowLineBoxes;
        var groups = showGroups ?? debug.ShowGroupBoxes;
        var onTranslation = showOnTranslation ?? debug.ShowOnTranslation;
        if (lines == debug.ShowLineBoxes && groups == debug.ShowGroupBoxes && onTranslation == debug.ShowOnTranslation) return;
        debug.ShowOnTranslation = onTranslation;
        debug.ShowLineBoxes = lines;
        debug.ShowGroupBoxes = groups;
        Save();
        OcrDebugChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when 自動翻譯, 儲存截圖, 截圖後自動關閉, 過濾數字與縮寫 or Ctrl+C 複製截圖 changes. The first two are
    /// set from two places — the settings page and the capture toolbar's 設定 panel — and each has
    /// to show what the other just wrote.
    /// </summary>
    public event EventHandler? CaptureOptionsChanged;

    public void UpdateCaptureOptions(
        bool? autoTranslate = null, bool? saveScreenshot = null, bool? closeAfterCopy = null,
        bool? filterNumbers = null, bool? copyOnCtrlC = null)
    {
        var current = Current;
        var auto = autoTranslate ?? current.AutoTranslateAfterSelection;
        var save = saveScreenshot ?? current.SaveScreenshotToDisk;
        var close = closeAfterCopy ?? current.Capture.CloseAfterCopy;
        var filter = filterNumbers ?? current.Capture.FilterNumbersAndAbbreviations;
        var ctrlC = copyOnCtrlC ?? current.Capture.CopyOnCtrlC;
        if (auto == current.AutoTranslateAfterSelection && save == current.SaveScreenshotToDisk &&
            close == current.Capture.CloseAfterCopy && filter == current.Capture.FilterNumbersAndAbbreviations &&
            ctrlC == current.Capture.CopyOnCtrlC) return;
        current.AutoTranslateAfterSelection = auto;
        current.SaveScreenshotToDisk = save;
        current.Capture.CloseAfterCopy = close;
        current.Capture.FilterNumbersAndAbbreviations = filter;
        current.Capture.CopyOnCtrlC = ctrlC;
        Save();
        CaptureOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when 使用漫畫直排模型 or 顯示模型提示 changes. The first is set on the settings
    /// page's card and put back on by a delete or a download; the second from the capture toolbar's
    /// menu. The engine and every hint on screen follow both.
    /// </summary>
    public event EventHandler? MangaModelOptionsChanged;

    public void UpdateMangaModelOptions(bool? useModels = null, bool? showHint = null)
    {
        var current = Current;
        var use = useModels ?? current.UseMangaModels;
        var show = showHint ?? current.Capture.ShowModelHint;
        if (use == current.UseMangaModels && show == current.Capture.ShowModelHint) return;
        current.UseMangaModels = use;
        current.Capture.ShowModelHint = show;
        Save();
        MangaModelOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the translation service (<see cref="AppSettings.Provider"/>) changes, once it is
    /// in the file — for what depends on it elsewhere, such as which service reads aloud (#436).
    /// </summary>
    public event EventHandler? ProviderChanged;

    /// <summary>Stores the translation service the user picked, wherever they picked it.</summary>
    public void UpdateProvider(TranslationProvider provider)
    {
        Current.Provider = provider;
        Save();
        ProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the services the menus offer change. Each open menu rebuilds its list from it,
    /// keeping whatever it has selected.
    /// </summary>
    public event EventHandler? ProviderVisibilityChanged;

    /// <summary>Stores which services the menus leave out.</summary>
    /// <returns>Whether anything changed, and so was saved.</returns>
    public bool UpdateHiddenProviders(IEnumerable<TranslationProvider> hidden)
    {
        var stored = ProviderVisibility.ToStored(Current.HiddenProviders, hidden);
        if (stored.SequenceEqual(Current.HiddenProviders)) return false;
        Current.HiddenProviders = stored;
        Save();
        ProviderVisibilityChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Raised when the custom 即時翻譯 modes change, once the change is in the file — for a running
    /// session to put its blocks on the new values, and for anything else listing the modes.
    /// </summary>
    public event EventHandler? RealtimeProfilesChanged;

    /// <summary>
    /// Stores the custom 即時翻譯 modes: the whole list as it should now be, in order. Adding,
    /// renaming, deleting, changing a value and putting one back are all this one call with a
    /// changed list — see <see cref="RealtimeProfile"/> for how a value is put back (null).
    /// </summary>
    /// <remarks>
    /// <para>Stored as <see cref="RealtimeSettings.SetProfiles"/> says: copies, tidied, and a
    /// deleted mode taking <see cref="RealtimeSettings.NewBlockProfile"/> back to 標準 with it.</para>
    /// </remarks>
    /// <returns>Whether anything changed, and so was saved and announced.</returns>
    public bool UpdateRealtimeProfiles(IEnumerable<RealtimeProfile> profiles)
    {
        if (!Current.Realtime.SetProfiles(profiles)) return false;
        Save();
        RealtimeProfilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Raised when the kept 即時翻譯 framings change — saved from the floating bar, renamed or
    /// forgotten on the page. The page's list follows it wherever the change was made.
    /// </summary>
    public event EventHandler? RealtimeFramingChanged;

    /// <summary>
    /// Stores the kept 即時翻譯 framings: the whole list as it should now be. Saving a framing,
    /// renaming one and forgetting one are all this one call with a changed list — see
    /// <see cref="RealtimeFramingPreset"/>.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="RealtimeSettings.SetFramingPresets"/> says: copies, tidied, newest first
    /// and one per target.
    /// </remarks>
    /// <returns>Whether anything changed, and so was saved and announced.</returns>
    /// <param name="skipped">
    /// The targets chosen 不套用 for (see <see cref="RealtimeSettings.FramingSkippedTargets"/>), or
    /// null to leave them as they are.
    /// </param>
    public bool UpdateRealtimeFramingPresets(
        IEnumerable<RealtimeFramingPreset> presets, IEnumerable<string>? skipped = null)
    {
        if (!Current.Realtime.SetFramingPresets(presets, skipped)) return false;
        Save();
        RealtimeFramingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Stores where the models are (<see cref="AppSettings.AssetsLocation"/>, empty for the default)
    /// and the old location still to be emptied. Unlike every other setting this one has to have
    /// reached the disk before anything goes on — a move restarts the app straight after — so a
    /// failed save throws, with the values put back as they were.
    /// </summary>
    public void UpdateAssetsLocation(string location, string pendingDelete)
    {
        var current = Current;
        var (oldLocation, oldPending) = (current.AssetsLocation, current.AssetsPendingDelete);
        current.AssetsLocation = location;
        current.AssetsPendingDelete = pendingDelete;
        try
        {
            Write();
        }
        catch
        {
            current.AssetsLocation = oldLocation;
            current.AssetsPendingDelete = oldPending;
            throw;
        }
    }

    public void Save()
    {
        try
        {
            Write();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {0}", SettingsPath);
        }

        // Only when the lists were edited: anything else saved leaves the file alone.
        _glossaries.SaveIfChanged(Current.Glossary.Lists);
    }

    /// <summary>
    /// How many glossaries there are, how many entries each holds and how many the one in use
    /// holds, for a problem report — which carries the counts and never the entries.
    /// </summary>
    public string DescribeGlossaries() => Describe(Current.Glossary);

    internal static string Describe(GlossarySettings glossary)
    {
        var counts = string.Join(",", glossary.Lists.Select(list => list.Entries.Count));
        var active = glossary.Active is { } list ? list.Entries.Count.ToString() : "none";
        return $"lists={glossary.Lists.Count} entries=[{counts}] active={active}";
    }

    private void Write()
    {
        Directory.CreateDirectory(SettingsDirectory);

        // Write-then-rename: losing power mid-write leaves the previous file intact instead of a
        // truncated one, which the next launch would read as corrupt and replace with defaults.
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, Serialize(Current));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    /// <returns>The text of the first file that could be read, and which file that was.</returns>
    private static (string? Json, string? Path) ReadFirstAvailable(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                return (File.ReadAllText(path), path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn(ex, "Could not read settings from {0}", path);
            }
        }

        return (null, null);
    }
}
