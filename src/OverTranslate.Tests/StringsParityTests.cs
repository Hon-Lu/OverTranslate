using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Holds the string dictionaries to each other, so a key added to one is never left out of the
/// rest.
/// </summary>
/// <remarks>
/// A missing key does not break the build and does not throw: LocalizationService.Get returns the
/// key itself, so the mistake ships as a label reading "S.Settings.SavePath" on someone's screen.
/// That is exactly the kind of thing nobody notices in the language they don't use, which is what
/// this test is for.
///
/// The files are parsed as XML rather than loaded as ResourceDictionaries — no WPF application
/// object is needed to compare keys, and parsing keeps the failure message pointed at the file.
/// </remarks>
public class StringsParityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Placeholders like {0}, ignoring {} escapes.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>The src/OverTranslate directory, found by walking up from the test binaries.</summary>
    /// <remarks>Public because other tests that read the project's own files need the same walk.</remarks>
    public static string ProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OverTranslate");
            if (Directory.Exists(Path.Combine(candidate, "Resources"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find src/OverTranslate above {AppContext.BaseDirectory}");
    }

    private static string ResourcesDirectory() => Path.Combine(ProjectDirectory(), "Resources");

    private static Dictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(ResourcesDirectory(), fileName);
        return XDocument.Load(path).Root!
            .Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value);
    }

    /// <summary>
    /// The dictionary the strings are authored in, and the one every other is held against.
    /// </summary>
    public static readonly string ChineseFile = "Strings.zh-Hant.xaml";
    public static readonly string EnglishFile = "Strings.en.xaml";

    /// <summary>Every dictionary that ships, the authored one included.</summary>
    /// <remarks>
    /// Written out rather than globbed so that adding a language is a deliberate act here too.
    /// <see cref="Every_dictionary_on_disk_is_covered_here"/> is what stops a file being dropped
    /// into Resources and quietly going unchecked by everything below.
    /// </remarks>
    public static readonly string[] AllFiles =
    [
        ChineseFile,
        "Strings.zh-Hans.xaml",
        EnglishFile,
        "Strings.ja.xaml",
        "Strings.ko.xaml",
    ];

    /// <summary>The dictionaries held against the authored one, which is every other one.</summary>
    public static TheoryData<string> TranslatedFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in AllFiles.Where(f => f != ChineseFile)) data.Add(file);
        return data;
    }

    [Fact]
    public void Every_dictionary_on_disk_is_covered_here()
    {
        var onDisk = Directory.GetFiles(ResourcesDirectory(), "Strings.*.xaml")
            .Select(path => Path.GetFileName(path)!)
            .Order()
            .ToList();

        Assert.Equal(AllFiles.Order().ToList(), onDisk);
    }

    /// <summary>
    /// An empty <c>&lt;sys:String&gt;&lt;/sys:String&gt;</c> parses as XML and passes every other
    /// check here, and then throws the first time WPF builds it: with no text to convert, XAML looks
    /// for a String constructor taking no arguments, and there is none. The Japanese and Korean
    /// capture hint had one each, and it took down the first capture after every launch. A string
    /// that is meant to be empty is written as <c>x:Static sys:String.Empty</c> instead.
    /// </summary>
    [Fact]
    public void No_dictionary_holds_an_empty_string_element()
    {
        var empty = AllFiles
            .SelectMany(file => XDocument.Load(Path.Combine(ResourcesDirectory(), file)).Root!
                .Elements()
                .Where(e => e.Name.LocalName == "String" && e.Value.Length == 0)
                .Select(e => $"{file}: {e.Attribute(X + "Key")?.Value}"))
            .ToList();

        Assert.Empty(empty);
    }

    [Theory]
    [MemberData(nameof(TranslatedFiles))]
    public void Every_dictionary_defines_exactly_the_keys_the_authored_one_does(string file)
    {
        var zh = Load(ChineseFile);
        var other = Load(file);

        var missingFromOther = zh.Keys.Except(other.Keys).OrderBy(k => k).ToList();
        var missingFromChinese = other.Keys.Except(zh.Keys).OrderBy(k => k).ToList();

        Assert.True(
            missingFromOther.Count == 0,
            $"Missing from {file}: {string.Join(", ", missingFromOther)}");
        Assert.True(
            missingFromChinese.Count == 0,
            $"Missing from {ChineseFile}: {string.Join(", ", missingFromChinese)}");
    }

    /// <summary>
    /// A translation that drops or invents a {0} is a FormatException at the moment the message is
    /// shown — which is to say, on the error path, where it replaces a real diagnostic with a crash.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranslatedFiles))]
    public void Composite_formats_use_the_same_placeholders_in_every_language(string file)
    {
        var zh = Load(ChineseFile);
        var other = Load(file);

        foreach (var (key, chinese) in zh)
        {
            if (!other.TryGetValue(key, out var translated)) continue;

            var inChinese = Placeholder.Matches(chinese).Select(m => m.Groups[1].Value).ToHashSet();
            var inOther = Placeholder.Matches(translated).Select(m => m.Groups[1].Value).ToHashSet();

            Assert.True(
                inChinese.SetEquals(inOther),
                $"{key} uses {{{string.Join(",", inChinese.Order())}}} in {ChineseFile} " +
                $"but {{{string.Join(",", inOther.Order())}}} in {file}");
        }
    }

    /// <summary>Every "S.…" key named in XAML or code-behind.</summary>
    /// <remarks>
    /// Keys are looked up by string at runtime, so neither a typo nor a rename is a compile error —
    /// the reference just resolves to nothing. DynamicResource silently renders empty and
    /// LocalizationService.Get returns the key itself, which is how a label saying
    /// "S.Settings.SavePath" would reach a user.
    /// </remarks>
    private static Dictionary<string, List<string>> KeysReferencedInSource()
    {
        var project = ProjectDirectory();
        var references = new Dictionary<string, List<string>>();

        var inXaml = new Regex(@"DynamicResource\s+(S\.[A-Za-z0-9_.]+)", RegexOptions.Compiled);
        var inCode = new Regex(@"""(S\.[A-Za-z0-9_.]+)""", RegexOptions.Compiled);

        foreach (var path in Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path);

            // obj/ holds generated copies of the same XAML; Resources/ is the definitions themselves.
            if (relative.StartsWith("obj") || relative.StartsWith("bin") ||
                relative.StartsWith("Resources")) continue;

            var pattern = Path.GetExtension(path) switch
            {
                ".xaml" => inXaml,
                ".cs"   => inCode,
                _       => null
            };
            if (pattern is null) continue;

            foreach (Match match in pattern.Matches(File.ReadAllText(path)))
            {
                var key = match.Groups[1].Value;
                if (!references.TryGetValue(key, out var files))
                    references[key] = files = [];
                files.Add(relative);
            }
        }

        return references;
    }

    [Fact]
    public void Every_key_referenced_in_source_is_defined()
    {
        var defined = Load(ChineseFile).Keys.ToHashSet();

        var missing = KeysReferencedInSource()
            .Where(entry => !defined.Contains(entry.Key))
            .Select(entry => $"{entry.Key} (in {string.Join(", ", entry.Value.Distinct())})")
            .Order()
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Referenced but not defined:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Catches the other half of a rename: the old key left behind in the dictionaries.
    /// </summary>
    [Fact]
    public void Every_defined_key_is_referenced_somewhere()
    {
        var referenced = KeysReferencedInSource().Keys.ToHashSet();

        var orphans = Load(ChineseFile).Keys
            .Where(key => !referenced.Contains(key))
            .Order()
            .ToList();

        Assert.True(
            orphans.Count == 0,
            $"Defined but never referenced:{Environment.NewLine}" +
            string.Join(Environment.NewLine, orphans));
    }

    /// <summary>
    /// The OpenAI hint is the only place the interface tells anyone that a local model is an
    /// option, and Ollama is the route it points at. Losing that in one language loses it for
    /// those users entirely.
    /// </summary>
    [Fact]
    public void OpenAi_hint_recommends_Ollama_in_every_language()
    {
        foreach (var file in AllFiles)
            Assert.Contains("Ollama", Load(file)["S.Provider.OpenAIHint"]);
    }

    /// <summary>
    /// A hint's [[…]] marks decide which words carry the accent colour — see HighlightedText — and
    /// they are the easiest thing to drop when translating, because the sentence still reads fine
    /// without them. It just reads flat, in the language nobody here is checking.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranslatedFiles))]
    public void Highlight_marks_are_balanced_and_appear_in_every_language(string file)
    {
        var zh = Load(ChineseFile);
        var other = Load(file);

        foreach (var (key, chinese) in zh)
        {
            if (!other.TryGetValue(key, out var translated)) continue;

            var inChinese = HighlightCount(chinese);
            var inOther = HighlightCount(translated);

            Assert.True(
                inChinese == inOther,
                $"{key} highlights {inChinese} stretch(es) in {ChineseFile} " +
                $"but {inOther} in {file}");

            // A count that matches on both sides is still no use if one of them is a stray bracket,
            // which HighlightedText renders as ordinary text.
            foreach (var (which, text) in new[] { (ChineseFile, chinese), (file, translated) })
                Assert.True(
                    Occurrences(text, "[[") == Occurrences(text, "]]"),
                    $"{key} has unbalanced [[ ]] in {which}: {text}");
        }
    }

    private static int HighlightCount(string text) =>
        OverTranslate.Views.Controls.HighlightedText.Split(text).Count(s => s.Highlighted);

    private static int Occurrences(string text, string token)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += token.Length;
        }

        return count;
    }

    /// <summary>
    /// The strip of controls a running session puts on screen has one name in each language.
    /// </summary>
    /// <remarks>
    /// It had four in Chinese — 浮動列, 浮動工具列, 浮動控制列, 浮動視窗 — spread across the steps on
    /// 即時翻譯, the running hint and the settings page, so nothing told a reader that all four were
    /// the same strip. Each was written by someone naming it afresh in the sentence they happened to
    /// be writing, which is exactly what this stops.
    /// </remarks>
    [Theory]
    [InlineData("Strings.zh-Hant.xaml", "浮動(?!視窗列)", "浮動視窗列")]
    [InlineData("Strings.zh-Hans.xaml", "浮动(?!窗口栏)", "浮动窗口栏")]
    [InlineData("Strings.en.xaml", "(?i)floating (?!bar)", "floating bar")]
    [InlineData("Strings.ja.xaml", "フローティング(?!バー)", "フローティングバー")]
    [InlineData("Strings.ko.xaml", "플로팅(?! 바)", "플로팅 바")]
    public void The_floating_bar_is_called_the_same_thing_everywhere(
        string file, string pattern, string agreedName)
    {
        var offenders = Load(file)
            .Where(kv => Regex.IsMatch(kv.Value, pattern))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{file} names it something other than \"{agreedName}\":{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The instruction sent to an OpenAI-compatible model has one name in each language.
    /// </summary>
    /// <remarks>
    /// The Chinese pair had two — the labels said "Prompt" and the prose beside them said 提示詞 —
    /// which leaves a reader working out for themselves that the box called Prompt is the thing the
    /// sentence underneath calls 提示詞. Settled on 提示詞 / 提示词, so the Latin word appears in no
    /// Chinese string at all; the other three each carry the loanword their own software uses
    /// (prompt, プロンプト, 프롬프트) and the check is that none of them falls back to the English
    /// spelling either.
    ///
    /// The keys are still named Prompt, and that is not a mistake: identifiers are English
    /// everywhere in this project, and it is the displayed word that has to agree with itself.
    ///
    /// "System Prompt" and "User Prompt" are the exception, and are excluded by the patterns below.
    /// Those two are drawn on screen in English in every language — they name the two halves of a
    /// chat request the way every model's own documentation names them — so a sentence that points
    /// at one of them has to spell it the way the label does. The common noun is still 提示詞.
    /// </remarks>
    [Theory]
    [InlineData("Strings.zh-Hant.xaml", "(?i)(?<!System )(?<!User )prompt", "提示詞")]
    [InlineData("Strings.zh-Hans.xaml", "(?i)(?<!System )(?<!User )prompt", "提示词")]
    [InlineData("Strings.ja.xaml", "(?i)(?<!System )(?<!User )prompt", "プロンプト")]
    [InlineData("Strings.ko.xaml", "(?i)(?<!System )(?<!User )prompt", "프롬프트")]
    // English says "prompt" and not a synonym for it: "wording" and "instruction" are both what it
    // was called at one point or another in the code that produces this string.
    [InlineData("Strings.en.xaml", "(?i)wording|(?i)instruction", "prompt")]
    public void The_model_instruction_is_called_the_same_thing_everywhere(
        string file, string pattern, string agreedName)
    {
        var offenders = Load(file)
            .Where(kv => Regex.IsMatch(kv.Value, pattern))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{file} names it something other than \"{agreedName}\":{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Full-width punctuation is correct in Chinese and wrong in English, and it is the single
    /// easiest thing to carry over when translating by copying a line and editing it in place.
    /// </summary>
    [Fact]
    public void English_strings_use_no_full_width_punctuation()
    {
        var offenders = Load(EnglishFile)
            .Where(kv => kv.Value.Any("，。：；「」（）、？！／".Contains))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Full-width punctuation in {EnglishFile}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }
}
