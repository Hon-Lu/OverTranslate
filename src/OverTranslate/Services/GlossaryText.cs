using System.Text;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>Why a line pasted or imported into a glossary was not read as an entry.</summary>
internal enum GlossaryLineProblem
{
    None,

    /// <summary>No tab, no = or ＝, and none of the other separators.</summary>
    NoSeparator,

    /// <summary>The source or the translation is blank.</summary>
    Empty,

    /// <summary>The source or the translation is longer than <see cref="GlossarySettings.MaxTermLength"/>.</summary>
    TooLong,
}

/// <summary>One non-blank line of pasted or imported text, and what was read from it.</summary>
/// <param name="Number">The line's number in the text, from 1.</param>
/// <param name="Text">The line, trimmed.</param>
internal sealed record GlossaryLine(int Number, string Text, string Source, string Target, GlossaryLineProblem Problem)
{
    public bool IsEntry => Problem == GlossaryLineProblem.None;
}

/// <summary>Glossary entries as text: pasted, imported from a file, exported to one.</summary>
/// <remarks>
/// <para>One entry a line. A line with a tab is read by the tab — two cells copied from a
/// spreadsheet or a web table arrive that way. Otherwise it is split at its first = or full-width
/// ＝, which is what the paste box shows as its example: a tab is a blank that cannot be typed into
/// the box and does not show what goes between the two. A line with neither is split at its first
/// comma, full-width comma or →, still read though not shown. Only the first separator splits, so
/// a translation may contain one; a source that contains = is entered in the table instead — no
/// escaping to learn for something that rare.</para>
///
/// <para>Blank lines and lines starting with # are skipped — the export's first line says the
/// language that way — and so is a first line that reads as column headings.</para>
/// </remarks>
internal static class GlossaryText
{
    /// <summary>The line an export starts with, naming its language: <c># target=ZH-HANT</c>.</summary>
    internal const string TargetLinePrefix = "# target=";

    private static readonly char[] EqualSigns = ['=', '＝'];
    private static readonly char[] OtherSeparators = [',', '，', '→'];

    private static readonly HashSet<string> SourceHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "原文", "source", "原文 (source)",
    };

    private static readonly HashSet<string> TargetHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "譯文", "译文", "譯名", "译名", "translation", "target", "訳文", "번역",
    };

    /// <summary>Every non-blank, non-comment line of <paramref name="text"/>, read.</summary>
    public static IReadOnlyList<GlossaryLine> Parse(string text)
    {
        var lines = new List<GlossaryLine>();
        var first = true;
        var number = 0;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            number++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var read = Read(raw.TrimStart('﻿'), line);
            var wasFirst = first;
            first = false;
            if (read is not { } cells)
            {
                lines.Add(new GlossaryLine(number, line, "", "", GlossaryLineProblem.NoSeparator));
                continue;
            }

            var (source, target) = cells;
            if (wasFirst && SourceHeadings.Contains(source) && TargetHeadings.Contains(target)) continue;

            var problem = source.Length == 0 || target.Length == 0 ? GlossaryLineProblem.Empty
                : source.Length > GlossarySettings.MaxTermLength || target.Length > GlossarySettings.MaxTermLength
                    ? GlossaryLineProblem.TooLong
                    : GlossaryLineProblem.None;
            lines.Add(new GlossaryLine(number, line, source, target, problem));
        }

        return lines;
    }

    /// <summary>The entries of <paramref name="lines"/> that could be read, as entries.</summary>
    public static List<GlossaryEntry> Entries(IEnumerable<GlossaryLine> lines) =>
        [.. lines.Where(line => line.IsEntry).Select(line => new GlossaryEntry { Source = line.Source, Target = line.Target })];

    /// <summary>The language an exported text says it is in, or null.</summary>
    public static string? TargetOf(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (!line.StartsWith(TargetLinePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var code = line[TargetLinePrefix.Length..].Trim();
            return LanguageData.TargetLanguages.FirstOrDefault(language =>
                language.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Code;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="list"/> as an export: its language on the first line, then a tab between the
    /// two halves of each entry, which a spreadsheet opens as two columns.
    /// </summary>
    public static string Export(GlossaryList list)
    {
        var text = new StringBuilder();
        text.Append(TargetLinePrefix).Append(list.TargetLanguage).Append("\r\n");
        foreach (var entry in list.Entries)
            text.Append(OneCell(entry.Source)).Append('\t').Append(OneCell(entry.Target)).Append("\r\n");
        return text.ToString();
    }

    /// <summary>The encoding an export is written in: UTF-8 with its mark, or Excel opens it as ANSI.</summary>
    public static readonly Encoding ExportEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static string OneCell(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>The two halves of a line, or null when nothing separates them.</summary>
    private static (string Source, string Target)? Read(string raw, string line)
    {
        if (raw.Contains('\t'))
        {
            var cells = raw.Split('\t');
            return (Unquote(cells[0].Trim()), Unquote(cells.Length > 1 ? cells[1].Trim() : ""));
        }

        var at = line.IndexOfAny(EqualSigns);
        if (at >= 0) return (line[..at].Trim(), line[(at + 1)..].Trim());

        if (line.Contains('"') && line.Contains(',')) return ReadCsv(line);

        at = line.IndexOfAny(OtherSeparators);
        if (at >= 0) return (line[..at].Trim(), line[(at + 1)..].Trim());
        return null;
    }

    /// <summary>A comma-separated line with quoted cells: <c>"Snow, the Elder",史諾</c>.</summary>
    private static (string, string)? ReadCsv(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }

        cells.Add(cell.ToString().Trim());
        return cells.Count < 2 ? null : (cells[0], cells[1]);
    }

    /// <summary>A spreadsheet's quoted cell without its quotes.</summary>
    private static string Unquote(string cell) =>
        cell.Length >= 2 && cell[0] == '"' && cell[^1] == '"' ? cell[1..^1].Replace("\"\"", "\"") : cell;
}
