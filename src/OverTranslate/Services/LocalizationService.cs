using System.Globalization;
using System.Windows;
// UseWindowsForms puts System.Windows.Forms and System.Drawing in the implicit usings, so these
// names collide
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Services;

/// <param name="Display">
/// The language's name written in itself, and the one string in the app that is never translated.
/// </param>
/// <remarks>
/// Deliberately not a resource key. Someone who has landed in a language they cannot read has to be
/// able to find their way back out, and "繁體中文" is legible from an English interface in a way
/// that "Traditional Chinese" is not from a Chinese one.
/// </remarks>
/// <param name="NameKey">
/// Resource key for the language's name in the interface's current language, the same S.Lang.* key
/// the translation pickers use. Only searched, never shown — see <see cref="SearchText"/>.
/// </param>
/// <param name="English">Its English name, also only searched.</param>
public record UiLanguageOption(string Code, string Display, string NameKey, string English)
    : OverTranslate.Models.ISearchableItem
{
    /// <summary>
    /// The code, the name written in itself, its name in the current interface language and its
    /// English name.
    /// </summary>
    /// <remarks>
    /// The list shows each language only in itself, which is the one spelling a searcher is least
    /// likely to type: someone in a Chinese interface looking for Japanese types 日文, not 日本語, and
    /// an English one types "japanese".
    /// </remarks>
    public string SearchText => $"{Code} {Display} {LocalizationService.Get(NameKey)} {English}";
}

/// <summary>
/// The UI language, swapped exactly the way <see cref="ThemeService"/> swaps colours.
/// </summary>
/// <remarks>
/// Strings live in a merged <see cref="ResourceDictionary"/> and are consumed through
/// DynamicResource, so replacing the dictionary re-resolves every binding in place. That matters
/// here more than it would elsewhere: this app keeps several windows alive at once (the overlay,
/// the capture toolbar, the realtime blocks, the tray menu), and asking the user to restart to
/// change a language would leave all of them stale until they did.
///
/// Code-behind reads the same dictionary through <see cref="Get"/> / <see cref="Format"/>. Those
/// return a snapshot rather than a binding, so a string handed to a control that way is only
/// correct until the next swap — see <see cref="LanguageChanged"/> for how pages refresh it.
/// </remarks>
public static class LocalizationService
{
    public const string TraditionalChinese = "zh-Hant";
    public const string SimplifiedChinese  = "zh-Hans";
    public const string English            = "en";
    public const string Japanese           = "ja";
    public const string Korean             = "ko";
    public const string Russian            = "ru";

    /// <summary>The languages offered in settings, in the order they are listed.</summary>
    /// <remarks>
    /// The two Chinese entries sit together because a reader scanning for one of them is scanning
    /// for the script, and splitting them across the list makes the second look like it is missing.
    ///
    /// Adding a language is an entry here and its Resources/Strings.{code}.xaml. Everything that
    /// varies by language — fonts, documentation, the OpenAI prompt — falls back to what English
    /// gets, so a language needs its own entry there only when it should differ from English.
    /// </remarks>
    public static readonly List<UiLanguageOption> Options =
    [
        new(TraditionalChinese, "繁體中文", "S.Lang.TraditionalChinese", "Traditional Chinese"),
        new(SimplifiedChinese,  "简体中文", "S.Lang.SimplifiedChinese",  "Simplified Chinese"),
        new(English,            "English",  "S.Lang.English",            "English"),
        new(Japanese,           "日本語",   "S.Lang.Japanese",           "Japanese"),
        new(Korean,             "한국어",   "S.Lang.Korean",             "Korean"),
        new(Russian,            "Русский",  "S.Lang.Russian",            "Russian"),
    ];

    /// <summary>The dictionary each language is served by, and the set of languages that exist.</summary>
    /// <remarks>
    /// Derived from <see cref="Options"/> rather than written out beside it: <see cref="Apply"/>,
    /// <see cref="Options"/> and <see cref="IsSupported"/> all have to agree on which codes are real,
    /// and a language added to one place but not another is exactly the kind of half-addition that
    /// ships as an interface that silently stays in the previous language. Declared after
    /// <see cref="Options"/> because static fields initialise in order.
    /// </remarks>
    private static readonly Dictionary<string, Uri> Dictionaries = Options.ToDictionary(
        option => option.Code,
        option => new Uri($"Resources/Strings.{option.Code}.xaml", UriKind.Relative),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="language"/> is one this app actually has strings for.</summary>
    public static bool IsSupported(string? language) =>
        !string.IsNullOrEmpty(language) && Dictionaries.ContainsKey(language);

    /// <summary>
    /// The interface font for each language, overriding the one SharedStyles declares.
    /// </summary>
    /// <remarks>
    /// WPF falls through a family list per character rather than per family, which is why Segoe UI
    /// can lead every one of these and CJK text still lands somewhere sensible. What it does not do
    /// is tell Chinese, Japanese and Korean apart: the Han characters they share have different
    /// printed shapes in each, and a single list ending in one CJK family renders all three in that
    /// language's shapes. Reading Japanese set in a Traditional Chinese face is the sort of thing a
    /// native reader notices immediately and cannot name, so the CJK family follows the interface.
    ///
    /// English keeps the Traditional Chinese family it already had. Nothing in an English interface
    /// is Han to begin with; what reaches it is the text the user is translating, and that was
    /// already being set this way. A language with no entry is set the way English is.
    ///
    /// Japanese is Yu Gothic UI, which is what Windows sets its own interface in. It is the narrowed
    /// "UI" cut of Yu Gothic and looks tighter than the Chinese families beside it here — that is
    /// the typeface, not a mistake in the list, and it is what a Japanese reader expects a Windows
    /// application to be set in. Meiryo was tried and put back: it is wider, and wider is not what
    /// this is supposed to look like.
    /// </remarks>
    private static readonly Dictionary<string, string> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        [TraditionalChinese] = "Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI, Sans-Serif",
        [SimplifiedChinese]  = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Sans-Serif",
        [English]            = "Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI, Sans-Serif",
        [Japanese]           = "Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI, Sans-Serif",
        [Korean]             = "Segoe UI Variable Text, Segoe UI, Malgun Gothic, Sans-Serif",
    };

    /// <summary>
    /// Raised after the dictionary swap, for text that DynamicResource cannot reach.
    /// </summary>
    /// <remarks>
    /// DynamicResource covers everything declared in XAML. It cannot cover a string that was
    /// composed in code — a hint chosen by which provider is selected, a caption with a percentage
    /// in it — because that string was already materialised. Pages holding such text subscribe and
    /// recompose it.
    /// </remarks>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// The language in effect: the stored choice, or the OS default when none was made.
    /// </summary>
    public static string Current
    {
        get
        {
            var stored = SettingsService.Instance.Current.UiLanguage;

            // Not just "is it empty": a code this build has no dictionary for would otherwise be
            // handed out here and compared against by everything downstream — LangItem.Display and
            // the prompt templates both branch on it — while Apply had already quietly fallen back
            // to something else, leaving the two disagreeing about what language is on screen.
            return IsSupported(stored) ? stored : ResolveSystemDefault();
        }
    }

    /// <summary>
    /// The language to start a first-run profile in, from the display language Windows is set to.
    /// </summary>
    /// <remarks>
    /// Chinese is split by script rather than by region, because that is the thing a reader can or
    /// cannot read: Windows reports zh-TW, zh-HK and zh-MO for the Traditional-writing regions and
    /// zh-CN, zh-SG and plain zh for the rest, so the region list is the script list. A Chinese
    /// tag nobody thought of lands on Simplified, which is what the unlisted ones overwhelmingly
    /// are. Any other language this app has strings for matches on the language alone; everything
    /// else gets English.
    ///
    /// CurrentUICulture, not InstalledUICulture: the latter is the language Windows was installed
    /// in and does not move when the user changes their display language afterwards, so someone
    /// running a Chinese-installed Windows in English would have been handed a Chinese interface
    /// despite having said otherwise. This only decides the starting point — an explicit choice on
    /// the settings page is stored and consulted first from then on.
    /// </remarks>
    public static string ResolveSystemDefault()
    {
        var culture = CultureInfo.CurrentUICulture;

        var language = culture.TwoLetterISOLanguageName.ToLowerInvariant();

        if (language == "zh")
            return IsTraditionalScript(culture) ? TraditionalChinese : SimplifiedChinese;

        return IsSupported(language) ? language : English;
    }

    /// <summary>
    /// Whether a Chinese culture is one of the Traditional-writing ones.
    /// </summary>
    /// <remarks>
    /// Read off the name rather than asked of <see cref="CultureInfo"/>, which has no "which script"
    /// property to ask. The explicit zh-Hant tag is checked first because Windows does hand it out
    /// for a culture set that way, and it carries no region to match on at all.
    /// </remarks>
    private static bool IsTraditionalScript(CultureInfo culture)
    {
        var name = culture.Name.Replace('_', '-');

        if (name.Contains("Hant", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Hans", StringComparison.OrdinalIgnoreCase)) return false;

        var region = name.Split('-').LastOrDefault() ?? "";
        return region.Equals("TW", StringComparison.OrdinalIgnoreCase)
            || region.Equals("HK", StringComparison.OrdinalIgnoreCase)
            || region.Equals("MO", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(string language)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;

        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null) dicts.Remove(old);

        dicts.Add(new ResourceDictionary
        {
            // An unknown code lands on the language these strings are authored in rather than on
            // nothing at all: a hand-edited settings file or a language retired since should leave
            // the interface readable, not blank.
            Source = Dictionaries.TryGetValue(language, out var source)
                ? source
                : Dictionaries[TraditionalChinese]
        });

        // Straight onto the application's own dictionary rather than into a merged one, so it wins
        // over the family SharedStyles declares. Every reference to it is a DynamicResource for this
        // reason — a StaticResource would have been resolved once, at parse time, and would keep
        // whichever font the app started in.
        Application.Current.Resources["AppFont"] = new FontFamily(
            Fonts.TryGetValue(language, out var font) ? font : Fonts[English]);

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Strings for code that runs with no <see cref="Application"/>, loaded on first use.
    /// </summary>
    /// <remarks>
    /// Always Traditional Chinese, which is the language these strings are authored in. Not the
    /// current preference and deliberately not the OS language: this path is reached from unit
    /// tests and from anything running before the UI exists, and a fallback that changed with the
    /// machine's locale would make those results depend on where they ran.
    /// </remarks>
    private static volatile ResourceDictionary? _fallback;

    /// <summary>
    /// Guards the one-time load. <see cref="Get"/> is reached from several threads at once — a
    /// realtime session runs a loop per region and every one of them can report a failure — and two
    /// of them arriving here together used to run the load twice, with the second able to see a
    /// dictionary that was published before it finished reading its source.
    /// </summary>
    private static readonly object FallbackGate = new();

    private static ResourceDictionary? Fallback
    {
        get
        {
            // Read once, outside the lock: after the first call this is the only cost, and volatile
            // is what makes the dictionary another thread built safe to use here.
            if (_fallback is not null) return _fallback;

            lock (FallbackGate)
            {
                if (_fallback is not null) return _fallback;

                try
                {
                    // Touching the helper registers the pack scheme, which Application would
                    // otherwise have done on startup — without it the Uri below cannot be resolved.
                    _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

                    // Built into a local and published only once it is whole, so no other thread can
                    // ever index a dictionary that is still loading.
                    var loaded = new ResourceDictionary
                    {
                        Source = new Uri(
                            "pack://application:,,,/OverTranslate;component/Resources/Strings.zh-Hant.xaml",
                            UriKind.Absolute)
                    };

                    _fallback = loaded;
                }
                catch
                {
                    // Nothing to be done about it here, and Get still has the key to fall back on.
                }

                return _fallback;
            }
        }
    }

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself when it is missing.
    /// </summary>
    /// <remarks>
    /// Returning the key rather than throwing keeps a typo from taking down a window that was
    /// otherwise fine, and makes the mistake obvious on screen. StringsParityTests is what actually
    /// catches these, before they ship.
    /// </remarks>
    public static string Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is string fromApp) return fromApp;
        if (Fallback?[key] is string fromFallback) return fromFallback;
        return key;
    }

    /// <inheritdoc cref="Get"/>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Points a picker at a list whose item labels come from the string dictionary.
    /// </summary>
    /// <remarks>
    /// The clear is the whole point. These lists are static, so re-assigning one is a no-op to
    /// WPF — it compares the reference, sees the same list, and keeps the item containers it
    /// already generated along with the text those were built from. The label properties resolve
    /// per read, so regenerating the containers is all that is needed; nothing short of it works.
    ///
    /// Callers set SelectedValue afterwards: clearing ItemsSource drops the selection with it.
    /// </remarks>
    public static void BindLocalizedItems(
        System.Windows.Controls.ItemsControl picker, System.Collections.IEnumerable items)
    {
        picker.ItemsSource = null;
        picker.ItemsSource = items;
    }
}
