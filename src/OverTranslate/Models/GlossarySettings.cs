using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>The user's glossaries — names and terms with the translation to use — and which one is in use.</summary>
/// <remarks>
/// <para>Used by the two AI services only, 本機 AI 翻譯 and OpenAI: what the entries a text contains
/// are sent with is a request a model reads, and a machine translator has no such place. See
/// <c>OpenAiPrompt.Glossary</c>.</para>
///
/// <para>One in use at a time, one per work: switching games is switching the glossary, and two
/// works' names never go into one request.</para>
///
/// <para>Replaced whole when edited, never changed in place: a translation running on another thread
/// holds the instance it read, and a list edited under it would throw mid-enumeration. See
/// <see cref="Clone"/>.</para>
/// </remarks>
public class GlossarySettings
{
    public const int MaxLists = 20;
    public const int MaxEntries = 1000;
    public const int MaxNameLength = 40;
    public const int MaxTermLength = 40;

    /// <summary>The glossaries themselves.</summary>
    /// <remarks>
    /// Kept in a file of their own, <c>glossaries.json</c>, not in the settings: they are what the
    /// user typed and collected, and a settings file that goes bad, is reset or is handed over in a
    /// problem report should not take them with it. See <see cref="Services.GlossaryStore"/>.
    /// </remarks>
    [JsonIgnore]
    public List<GlossaryList> Lists { get; set; } = [];

    /// <summary>The <see cref="GlossaryList.Id"/> in use; empty for none. The one part of this written to the settings.</summary>
    public string ActiveId { get; set; } = "";

    /// <summary>The glossary in use, or null.</summary>
    [JsonIgnore]
    public GlossaryList? Active =>
        ActiveId.Length == 0 ? null : Lists.FirstOrDefault(list => list.Id == ActiveId);

    /// <summary>A copy deep enough to edit without touching this one.</summary>
    public GlossarySettings Clone() => new()
    {
        ActiveId = ActiveId,
        Lists = [.. Lists.Select(list => list.Clone())],
    };

    /// <summary>
    /// <paramref name="read"/> as the application can use it: no list without an id, no blank or
    /// repeated entry, nothing past the limits, and an <see cref="ActiveId"/> that names a list.
    /// </summary>
    /// <remarks>
    /// For a file edited by hand or written by a later build. A repeated source keeps its last
    /// translation, as pasting the same name twice does.
    /// </remarks>
    public static GlossarySettings Clean(GlossarySettings? read)
    {
        var clean = new GlossarySettings();
        if (read is null) return clean;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in read.Lists ?? [])
        {
            if (list is null || clean.Lists.Count == MaxLists) continue;
            var id = (list.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id)) id = NewId();
            ids.Add(id);

            clean.Lists.Add(new GlossaryList
            {
                Id = id,
                Name = Cut((list.Name ?? "").Trim(), MaxNameLength),
                TargetLanguage = LanguageData.GetValidTargetCode(list.TargetLanguage),
                Entries = CleanEntries(list.Entries),
            });
        }

        var active = (read.ActiveId ?? "").Trim();
        clean.ActiveId = clean.Lists.Any(list => list.Id == active) ? active : "";
        return clean;
    }

    /// <summary>
    /// <paramref name="entries"/> trimmed and cut to the limits, blanks left out, a repeated source
    /// kept once where it first stood with the last translation given for it.
    /// </summary>
    public static List<GlossaryEntry> CleanEntries(IEnumerable<GlossaryEntry?>? entries)
    {
        var result = new List<GlossaryEntry>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (entry is null) continue;
            var source = Cut((entry.Source ?? "").Trim(), MaxTermLength);
            var target = Cut((entry.Target ?? "").Trim(), MaxTermLength);
            if (source.Length == 0 || target.Length == 0) continue;

            if (at.TryGetValue(source, out var index))
            {
                result[index] = new GlossaryEntry { Source = source, Target = target };
                continue;
            }

            if (result.Count == MaxEntries) continue;
            at[source] = result.Count;
            result.Add(new GlossaryEntry { Source = source, Target = target });
        }

        return result;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}

/// <summary>One glossary: usually one work's names.</summary>
public class GlossaryList
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// The language the translations are written in, a target code. The glossary is used only for
    /// requests into this language: a Traditional Chinese name sent with a request into English
    /// would come back in the English text.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageData.DefaultTargetLanguage;

    public List<GlossaryEntry> Entries { get; set; } = [];

    public GlossaryList Clone() => new()
    {
        Id = Id,
        Name = Name,
        TargetLanguage = TargetLanguage,
        Entries = [.. Entries.Select(entry => new GlossaryEntry { Source = entry.Source, Target = entry.Target })],
    };
}

/// <summary>A name or term as it appears in the source, and the translation to use for it.</summary>
public class GlossaryEntry
{
    public string Source { get; set; } = "";

    public string Target { get; set; } = "";
}
