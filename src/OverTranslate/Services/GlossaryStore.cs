using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// <c>glossaries.json</c>: the glossaries' lists and entries, kept apart from the settings. The
/// settings file keeps only which one is in use (<see cref="GlossarySettings.ActiveId"/>).
/// </summary>
/// <remarks>
/// <para>A file of its own because the entries are the user's own work — names typed in and
/// collected over a game — not preferences. A settings file that goes bad, is reset or is copied
/// into a problem report should not take them with it, and a thousand entries should not be
/// rewritten every time a slider moves.</para>
///
/// <para>Written only when the lists changed (<see cref="SaveIfChanged"/>), and always through a
/// temporary file renamed into place, so losing power mid-write leaves the previous file whole.</para>
///
/// <para>A file that cannot be read as a glossary is never written over: it is renamed to
/// <c>glossaries.json.bad-&lt;time&gt;</c> and the app goes on with no glossaries. One that cannot be
/// opened at all — held by another program — is moved aside the same way before the first write,
/// and while it cannot be, nothing is written.</para>
///
/// <para><see cref="SettingsService"/> loads and saves through it. The lists are only edited on the
/// UI thread, but the settings are saved from wherever a value changed, so a save takes a lock.</para>
/// </remarks>
internal sealed class GlossaryStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Indented, and the whole of Unicode left as itself — the same as the settings file, and for
    /// the same reason: the names in it are the user's own words in a file the user can open.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    /// <summary>The lists last read or written, so a save of anything else costs a reference compare.</summary>
    private List<GlossaryList>? _lastLists;

    /// <summary>The text last read or written, so a list replaced with an equal one is not written again.</summary>
    private string _lastText;

    /// <summary>
    /// A file is there that was not read — it could not be opened — and could not be moved aside
    /// either. It has to be moved before anything is written over it.
    /// </summary>
    private bool _moveAsideFirst;

    internal GlossaryStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
        _lastText = Serialize([]);
    }

    internal string FilePath => _path;

    /// <summary>
    /// The lists in the file, cleaned as <see cref="GlossarySettings.Clean"/> cleans them; empty when
    /// there is no file or it cannot be read, with a line in the log saying which.
    /// </summary>
    internal List<GlossaryList> Read()
    {
        var lists = ReadFile();
        var clean = GlossarySettings.Clean(new GlossarySettings { Lists = lists }).Lists;
        _lastLists = clean;
        _lastText = Serialize(clean);
        return clean;
    }

    private List<GlossaryList> ReadFile()
    {
        if (!File.Exists(_path))
        {
            Log.Info("No glossaries file at {0}; starting with none", _path);
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Could not read the glossaries file; going on with none and keeping the file");
            if (!MoveAside()) _moveAsideFirst = true;
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<GlossaryFile>(text)
                ?? throw new JsonException("The glossaries file holds null.");
            var lists = file.Lists ?? [];
            Log.Info("Read {0} glossaries ({1} entries)", lists.Count, lists.Sum(list => list?.Entries?.Count ?? 0));
            return [.. lists.Where(list => list is not null)];
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "The glossaries file is not readable; going on with none");
            if (!MoveAside()) _moveAsideFirst = true;
            return [];
        }
    }

    /// <summary>
    /// Writes <paramref name="lists"/> when they differ from what the file holds. Errors go to the
    /// log, not to the caller: like the settings, a save that fails is tried again with the next change.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    internal bool SaveIfChanged(List<GlossaryList> lists)
    {
        lock (_gate) return SaveIfChangedLocked(lists);
    }

    private bool SaveIfChangedLocked(List<GlossaryList> lists)
    {
        // The settings are saved for every change anywhere in the app; the glossaries are replaced
        // whole whenever they are edited (see GlossarySettings), so the same list is nothing new.
        if (ReferenceEquals(lists, _lastLists)) return false;

        var text = Serialize(lists);
        if (text == _lastText)
        {
            _lastLists = lists;
            return false;
        }

        if (_moveAsideFirst && File.Exists(_path))
        {
            if (!MoveAside())
            {
                Log.Error("Not saving the glossaries: {0} could not be read and cannot be moved aside", _path);
                return false;
            }
        }
        _moveAsideFirst = false;

        try
        {
            Write(text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save the glossaries to {0}", _path);
            return false;
        }

        _lastLists = lists;
        _lastText = text;
        return true;
    }

    /// <summary>Write-then-rename, as the settings are written: a half-written file is never the one read next.</summary>
    private void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// Renames the file to <c>glossaries.json.bad-&lt;time&gt;</c>, a name no save writes to, so
    /// what was in it is still there for the user — or for a later build — to recover.
    /// </summary>
    /// <returns>Whether the file is out of the way.</returns>
    private bool MoveAside()
    {
        var stamp = _now().ToString("yyyyMMdd-HHmmss");
        var target = _path + ".bad-" + stamp;
        for (var n = 2; File.Exists(target); n++)
            target = _path + ".bad-" + stamp + "-" + n;

        try
        {
            File.Move(_path, target);
            Log.Warn("Kept the unreadable glossaries file as {0}", Path.GetFileName(target));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Could not move the unreadable glossaries file aside");
            return false;
        }
    }

    internal static string Serialize(List<GlossaryList> lists) =>
        JsonSerializer.Serialize(new GlossaryFile { Lists = lists }, WriteOptions);

    /// <summary>The file's shape: an object rather than a bare array, so it has room to grow.</summary>
    private sealed class GlossaryFile
    {
        public List<GlossaryList>? Lists { get; set; } = [];
    }
}
