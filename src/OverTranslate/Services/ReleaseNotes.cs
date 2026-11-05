using System.Text.RegularExpressions;
using Velopack;

namespace OverTranslate.Services;

/// <summary>One release as GitHub lists it, before anything has been decided about it.</summary>
public sealed record ReleaseRecord(
    string Tag,
    bool Draft,
    bool Prerelease,
    DateTimeOffset? Published,
    string Body,
    string HtmlUrl);

/// <summary>A release the update window will show.</summary>
public sealed record ReleaseEntry(
    string Version,
    DateTimeOffset? Published,
    string Body,
    string HtmlUrl);

/// <param name="Shown">Newest first, at most <see cref="ReleaseNotesSelection.MaxShown"/>.</param>
/// <param name="Total">How many releases the range holds, including those not shown.</param>
/// <param name="TotalIsPartial">
/// The listing ran out before reaching the installed version, so <paramref name="Total"/> is a lower
/// bound.
/// </param>
public sealed record ReleaseNotesFeed(
    IReadOnlyList<ReleaseEntry> Shown,
    int Total,
    bool TotalIsPartial)
{
    public int Earlier => Total - Shown.Count;

    /// <summary>Whether any release listed has words to show in <paramref name="language"/>.</summary>
    public bool HasAnythingToRead(string language) =>
        Shown.Any(entry => ReleaseNotesParser.Parse(entry.Body, language).Count > 0);
}

/// <summary>Decides which releases the update window lists.</summary>
public static class ReleaseNotesSelection
{
    public const int MaxShown = 3;

    /// <summary>The stable releases an update from <paramref name="current"/> to <paramref name="target"/> takes in.</summary>
    /// <param name="includeFlagged">
    /// Also takes releases GitHub flags as pre-release, provided the version itself has no suffix. A
    /// CI build is flagged until a person clears it, and this is how its notes can be checked before
    /// that happens. Off for every real user.
    /// </param>
    public static ReleaseNotesFeed? ForUpdate(
        IReadOnlyList<ReleaseRecord> listing,
        SemanticVersion current,
        SemanticVersion target,
        bool includeFlagged,
        int pageSize)
    {
        var stable = Stable(listing, includeFlagged).ToList();
        var range = stable
            .Where(r => r.Version > current && r.Version <= target)
            .ToList();
        if (range.Count == 0) return null;

        var partial = listing.Count >= pageSize && stable[^1].Version > current;
        return new ReleaseNotesFeed(
            range.Take(MaxShown).Select(r => r.Entry).ToList(),
            range.Count,
            partial);
    }

    /// <summary>The stable releases up to and including <paramref name="current"/>.</summary>
    public static ReleaseNotesFeed? History(IReadOnlyList<ReleaseRecord> listing, SemanticVersion current)
    {
        var range = Stable(listing, includeFlagged: false)
            .Where(r => r.Version <= current)
            .ToList();
        if (range.Count == 0) return null;

        return new ReleaseNotesFeed(
            range.Take(MaxShown).Select(r => r.Entry).ToList(),
            range.Count,
            TotalIsPartial: false);
    }

    private static IEnumerable<(SemanticVersion Version, ReleaseEntry Entry)> Stable(
        IEnumerable<ReleaseRecord> listing, bool includeFlagged)
    {
        return listing
            .Where(r => !r.Draft && (includeFlagged || !r.Prerelease))
            .Select(r => (Record: r, Name: r.Tag.TrimStart('v', 'V')))
            .Select(x => (x.Record, x.Name, Parsed: SemanticVersion.TryParse(x.Name, out var v) ? v : null))
            .Where(x => x.Parsed is not null && !x.Parsed.IsPrerelease)
            .OrderByDescending(x => x.Parsed)
            .Select(x => (x.Parsed!, new ReleaseEntry(x.Name, x.Record.Published, x.Record.Body, x.Record.HtmlUrl)));
    }
}

public enum NotesSpanKind
{
    Plain,
    Bold,
    Code,
}

public sealed record NotesSpan(string Text, NotesSpanKind Kind);

/// <param name="Level">0 for a top-level line, 1 for one nested under it.</param>
/// <param name="Bullet">False for a line of prose, such as a thank-you quoted above the sections.</param>
public sealed record NotesLine(int Level, bool Bullet, IReadOnlyList<NotesSpan> Spans);

/// <param name="Glyph">The section's marker, or empty when it has none.</param>
/// <param name="Title">Empty for prose that comes before the first heading.</param>
public sealed record NotesSection(string Glyph, string Title, IReadOnlyList<NotesLine> Lines);

/// <summary>Reads the five-language release body the release template produces.</summary>
/// <remarks>
/// Only what that template writes is understood; anything else is kept as plain text rather than
/// dropped, so a one-off line in a future release still reaches the reader. Links lose their address:
/// these bodies are read here for their words, and the full page is a click away.
/// <para>
/// A body is text someone typed into a web page, and the window that shows it opens at startup, so
/// no body may make this throw: a mistake in one has to cost at most that release's notes. A block
/// ends at the next one's start as well as at its own close, so a lost close cannot swallow the next
/// language.
/// </para>
/// </remarks>
public static class ReleaseNotesParser
{
    private const string InstallSectionMark = "↓";

    private static readonly Regex Block = new(
        @"<summary>\s*(?:<b>)?(?<label>.*?)(?:</b>)?\s*</summary>(?<body>.*?)(?=</details>|<details\b|<summary>|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Inline = new(
        @"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|\[(?<link>[^\]]+)\]\([^)]*\)",
        RegexOptions.Compiled);

    private static readonly Regex ListItem = new(@"^(?<indent>\s*)[-*]\s+(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>The release notes' own name for each interface language that has notes.</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [LocalizationService.English] = "English",
        [LocalizationService.TraditionalChinese] = "繁體中文",
        [LocalizationService.SimplifiedChinese] = "简体中文",
        [LocalizationService.Japanese] = "日本語",
        [LocalizationService.Korean] = "한국어",
    };

    /// <summary>
    /// The sections for <paramref name="language"/>, or for English when the notes are not written
    /// in it. A body with no language blocks at all is read whole. Empty when it has blocks but
    /// neither of those, or has nothing to read.
    /// </summary>
    public static IReadOnlyList<NotesSection> Parse(string body, string language)
    {
        var matches = Block.Matches(body ?? "");
        if (matches.Count == 0) return ParseBlock(body ?? "");

        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in matches)
            blocks.TryAdd(match.Groups["label"].Value.Trim(), match.Groups["body"].Value);

        var wanted = Labels.GetValueOrDefault(language, Labels[LocalizationService.English]);
        if (!blocks.TryGetValue(wanted, out var text)
            && !blocks.TryGetValue(Labels[LocalizationService.English], out text))
        {
            return [];
        }

        return ParseBlock(text);
    }

    private static List<NotesSection> ParseBlock(string text)
    {
        var sections = new List<NotesSection>();
        string glyph = "", title = "";
        var lines = new List<NotesLine>();
        var skipping = false;

        void Flush()
        {
            if (!skipping && (lines.Count > 0 || title.Length > 0))
                sections.Add(new NotesSection(glyph, title, lines));
            lines = [];
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.All(c => c == '-')) continue;

            if (trimmed.StartsWith("# ", StringComparison.Ordinal)) continue;

            if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                Flush();
                var heading = trimmed.TrimStart('#').Trim();
                skipping = heading.StartsWith(InstallSectionMark, StringComparison.Ordinal);
                (glyph, title) = SplitGlyph(heading);
                continue;
            }

            if (skipping) continue;

            var item = ListItem.Match(line);
            if (item.Success)
            {
                var indent = item.Groups["indent"].Value;
                var level = indent.Length >= 2 || indent.Contains('\t') ? 1 : 0;
                lines.Add(new NotesLine(level, true, Spans(item.Groups["text"].Value)));
                continue;
            }

            var prose = trimmed.StartsWith('>') ? trimmed.TrimStart('>').Trim() : trimmed;
            lines.Add(new NotesLine(0, false, Spans(prose)));
        }

        Flush();
        return sections;
    }

    private static (string Glyph, string Title) SplitGlyph(string heading)
    {
        if (heading.Length >= 2 && heading[0] is '◆' or '◇' && char.IsWhiteSpace(heading[1]))
            return (heading[..1], heading[2..].Trim());

        return ("", heading);
    }

    private static List<NotesSpan> Spans(string text)
    {
        var spans = new List<NotesSpan>();
        var at = 0;

        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > at)
                spans.Add(new NotesSpan(text[at..match.Index], NotesSpanKind.Plain));

            if (match.Groups["code"].Success)
                spans.Add(new NotesSpan(match.Groups["code"].Value, NotesSpanKind.Code));
            else if (match.Groups["bold"].Success)
                spans.Add(new NotesSpan(match.Groups["bold"].Value, NotesSpanKind.Bold));
            else
                spans.Add(new NotesSpan(match.Groups["link"].Value, NotesSpanKind.Plain));

            at = match.Index + match.Length;
        }

        if (at < text.Length)
            spans.Add(new NotesSpan(text[at..], NotesSpanKind.Plain));

        return spans;
    }
}
