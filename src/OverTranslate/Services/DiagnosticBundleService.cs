using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using NLog;
using NLog.Targets;

namespace OverTranslate.Services;

/// <summary>
/// Packs the log files and a description of the machine into one zip, so reporting a problem is a
/// drag and a drop instead of being talked through where %AppData% is.
/// </summary>
/// <remarks>
/// Everything here stays on the user's disk: nothing is sent anywhere, and the file is left
/// somewhere they can open it and read what they are about to hand over. That is the whole privacy
/// story, and it is why the bundle can afford to contain the log verbatim — 記錄詳細資訊 puts the
/// recognised text in there, which is whatever was on their screen.
///
/// The one thing that is not verbatim is the settings file: an API key is a credential, it is of no
/// diagnostic use, and a user pasting a zip into a public forum thread cannot be expected to think
/// of it. See <see cref="RedactSettings"/>.
/// </remarks>
public static class DiagnosticBundleService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The NLog target whose file name tells us where the log actually is.</summary>
    private const string FileTargetName = "file";

    /// <summary>
    /// Property-name endings whose value is a credential. Matched on the ending rather than a list
    /// of the three names that exist today, so a provider added later is covered by default — the
    /// failure mode of a new key leaking is worse than that of a new field being hidden.
    /// </summary>
    /// <remarks>
    /// "Key" alone is deliberately not here: <c>HotkeyVirtualKey</c> and its siblings end in it, and
    /// hiding which key a user's shortcut is bound to would remove one of the more useful things in
    /// the file.
    /// </remarks>
    private static readonly string[] SecretSuffixes = { "ApiKey", "Token", "Secret", "Password" };

    /// <summary>
    /// Where NLog is writing, asked of NLog rather than restated from NLog.config — the two would
    /// otherwise have to be kept in step by hand, and an environment that redirected the log is
    /// exactly the kind whose log we would then fail to collect.
    /// </summary>
    public static string LogDirectory
    {
        get
        {
            try
            {
                if (LogManager.Configuration?.FindTargetByName(FileTargetName) is FileTarget target)
                {
                    var rendered = target.FileName.Render(LogEventInfo.CreateNullEvent());
                    var directory = Path.GetDirectoryName(rendered);
                    if (!string.IsNullOrWhiteSpace(directory))
                        return directory;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Could not read the log path from the NLog configuration");
            }

            return DefaultLogDirectory;
        }
    }

    /// <summary>What NLog.config resolves to when nothing has moved it.</summary>
    private static string DefaultLogDirectory => AppDataPaths.Logs;

    /// <summary>
    /// Opens the folder the exports are written to, creating it first so the button works on a
    /// machine that has never made one.
    /// </summary>
    /// <remarks>
    /// The exports rather than the logs: this is the folder someone is sent to when they have to
    /// hand a file over by themselves, and the logs are inside every zip in it anyway.
    /// </remarks>
    public static void OpenExportFolder()
    {
        var directory = DefaultExportDirectory;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    /// <summary>
    /// Writes the bundle and returns its full path. Blocking — call it off the UI thread.
    /// </summary>
    /// <param name="destinationDirectory">
    /// Where to put the zip, or null for <see cref="DefaultExportDirectory"/>.
    /// </param>
    public static string Export(string? destinationDirectory = null)
    {
        // Taken now rather than trusted to still be in the log: the launch snapshot is the only one
        // shipped builds keep, and on a machine that has been up for days the rolling archive has
        // long since dropped it. This one is written at Info so it survives the same trimming.
        DisplayDiagnostics.LogSnapshot("diagnostic-export", level: LogLevel.Info);

        // keepFileOpen is on, so the most recent lines — including the snapshot above — are still in
        // NLog's buffer and would be missing from the copy taken below.
        LogManager.Flush();

        var directory = ResolveDestination(destinationDirectory);
        Directory.CreateDirectory(directory);
        PruneOldExports(directory, DateTime.UtcNow);

        var path = Path.Combine(
            directory,
            $"OverTranslate-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddText(archive, "environment.txt", BuildEnvironmentReport());
            AddText(archive, "appsettings.redacted.json", ReadRedactedSettings());
            AddLogs(archive);
        }

        Log.Info("Diagnostic bundle written to {0}", CollapseUserPaths(path));
        return path;
    }

    /// <summary>
    /// Opens the bundle itself, which on Windows means Explorer showing what is inside it.
    /// </summary>
    /// <remarks>
    /// The pair of <see cref="Reveal"/>, and the difference matters: Reveal answers "where is it",
    /// which is what someone about to attach a file needs, while this answers "what is in it", which
    /// is what someone who has just uploaded one needs.
    /// </remarks>
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>What the user's own account of the problem is called inside the bundle.</summary>
    public const string NoteEntryName = "note.txt";

    /// <summary>
    /// As much as anyone writes into a box before they would rather be talking to a person, with
    /// room to spare. It exists so the field has a limit at all, not to cut anybody off.
    /// </summary>
    public const int MaxNoteLength = 4000;

    /// <summary>
    /// Writes the user's own account of the problem into a bundle already on disk, replacing any
    /// note it already carries. A blank note writes nothing. Blocking — call it off the UI thread.
    /// </summary>
    /// <remarks>
    /// Into the zip rather than alongside the upload. The note is prose someone typed in their own
    /// language and at whatever length they needed; the upload's headers are printable ASCII,
    /// trimmed to 64 characters, and land in key metadata that is capped in kilobytes. It also
    /// means the copy left on their disk says the same thing as the copy that was sent, and that
    /// carrying a note costs the receiving end no change at all.
    ///
    /// Line endings are normalised because this is read in whatever the reader double-clicks, and
    /// on Windows that is still sometimes something that renders a lone \n as one long line.
    /// </remarks>
    public static void AttachNote(string bundlePath, string note)
    {
        var text = note.Trim();
        if (text.Length == 0) return;

        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update))
        {
            // Update appends, so a second attempt after a failed upload would otherwise leave two
            // entries of the same name in the archive — legal in the format, and read back as
            // whichever one the extractor happens to reach first.
            archive.GetEntry(NoteEntryName)?.Delete();
            AddText(archive, NoteEntryName, text.ReplaceLineEndings("\r\n"));
        }

        // Length only, never the text: it is the user's own words about their own machine, and it
        // is already in the file this line is about.
        Log.Info("Diagnostic note attached, {0} characters", text.Length);
    }

    /// <summary>
    /// How long an export stays on disk, matching what the uploaded copy gets. One number for both,
    /// so the interface can state it once without having to say which copy it means.
    /// </summary>
    public static readonly TimeSpan ExportRetention = TimeSpan.FromDays(30);

    /// <summary>The names <see cref="Export"/> writes, and the only ones this will delete.</summary>
    private const string ExportPattern = "OverTranslate-diagnostics-*.zip";

    /// <summary>
    /// Deletes the exports older than <see cref="ExportRetention"/>, and returns how many went.
    /// </summary>
    /// <remarks>
    /// Done on the way to writing a new one rather than on a timer: the folder only grows when this
    /// runs, so that is the only moment it can have grown, and a background sweep would be a thread
    /// kept for a folder that is empty on most machines.
    ///
    /// The pattern is what keeps this honest. It is a folder inside the application own data, but a
    /// user who has put something in there — a renamed copy they kept, a note to themselves — put it
    /// there deliberately, and a cleanup that takes files it did not write is a bug that destroys
    /// data. Nothing outside the shape <see cref="Export"/> produces is touched.
    ///
    /// A file that cannot be deleted is left rather than reported: one open in a zip viewer will go
    /// on the next export, and there is nothing the user would do about being told.
    /// </remarks>
    public static int PruneOldExports(string directory, DateTime nowUtc)
    {
        var deleted = 0;
        if (!Directory.Exists(directory)) return deleted;

        foreach (var file in Directory.GetFiles(directory, ExportPattern))
        {
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) <= ExportRetention) continue;

                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Could not delete the expired diagnostic bundle {0}",
                    CollapseUserPaths(file));
            }
        }

        if (deleted > 0) Log.Info("Deleted {0} expired diagnostic bundle(s)", deleted);
        return deleted;
    }

    /// <summary>
    /// The folders a path sits under, written as the variables that name them rather than as where
    /// they expanded to.
    /// </summary>
    /// <remarks>
    /// Longest root first: LocalApplicationData and ApplicationData both sit under UserProfile, and
    /// matching the shortest would turn every path into %USERPROFILE%\AppData\....
    /// </remarks>
    private static readonly (string Variable, string Root)[] ProfileRoots =
    {
        ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("%APPDATA%",      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ("%USERPROFILE%",  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    };

    /// <summary>
    /// Rewrites the user's own folders back into the variables that name them, so a bundle does not
    /// carry the Windows account name out with it.
    /// </summary>
    /// <remarks>
    /// The account name is on a great many machines the person's real name, and it appeared four
    /// times in a file whose four path lines were only ever interesting for their shape: is the log
    /// where it is expected, is this a Velopack install or something run loose from a folder. That
    /// shape survives this; the name does not.
    ///
    /// A path that is not under any of those roots is returned untouched, which is deliberate. Being
    /// somewhere unexpected is precisely the condition worth seeing, and there is no account name to
    /// hide in a path that was never under the account's own folder.
    /// </remarks>
    public static string CollapseUserPaths(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        foreach (var (variable, root) in ProfileRoots)
        {
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return variable + path[root.Length..];
        }

        return path;
    }

    /// <summary>Opens Explorer with the file already selected.</summary>
    public static void Reveal(string path)
    {
        // No space after the comma: explorer treats "/select, C:\..." as two arguments and opens the
        // Documents folder instead of the one asked for.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Returns the settings JSON with every credential replaced by a note saying one was there.
    /// </summary>
    /// <remarks>
    /// A note rather than an empty string, and with the length in it: "is a key set at all, and is
    /// it the length a key of this kind should be" answers a good share of the reports this file is
    /// collected for, and neither question can be asked of a blank.
    ///
    /// Unparseable input is returned as-is only when it is not JSON at all — that file cannot hold a
    /// key in a field we would recognise anyway, and its shape is itself the bug being reported.
    /// </remarks>
    public static string RedactSettings(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is not JsonObject obj)
            return json;

        RedactInPlace(obj);
        return obj.ToJsonString(ReadableJson);
    }

    /// <summary>
    /// Relaxed escaping, because this copy is written to be read by a person. The default encoder
    /// escapes everything outside a conservative ASCII set, which writes a shortcut as
    /// <c>Ctrl+Alt+A</c> and a Chinese prompt as an unbroken run of <c>\uXXXX</c> — in a
    /// file whose whole purpose is being read by whoever is diagnosing the report. Safe here in a
    /// way it would not be elsewhere: this string goes into a zip, never into a web page.
    /// </summary>
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void RedactInPlace(JsonObject obj)
    {
        // Materialised first: the loop reassigns properties, which cannot be done while enumerating
        // the live collection.
        foreach (var name in obj.Select(pair => pair.Key).ToList())
        {
            switch (obj[name])
            {
                // Realtime keeps its own copy of the provider settings, so the walk has to recurse
                // rather than only look at the top level.
                case JsonObject nested:
                    RedactInPlace(nested);
                    continue;

                case JsonValue value when value.TryGetValue(out string? text):
                    if (IsSecret(name))
                        obj[name] = Mask(text);
                    else if (name.EndsWith("BaseUrl", StringComparison.Ordinal))
                        obj[name] = StripUrlCredentials(text);
                    continue;
            }
        }
    }

    private static bool IsSecret(string name) =>
        SecretSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : $"<redacted:{value.Length}>";

    /// <summary>
    /// Keeps the address but drops anything credential-shaped hanging off it.
    /// </summary>
    /// <remarks>
    /// The address itself is kept on purpose: whether someone is pointed at api.openai.com, at
    /// localhost:11434, or at a machine on their own network is most of the diagnosis when an
    /// OpenAI-compatible provider misbehaves. What cannot be kept is a token someone put in the
    /// user info or the query string, which some self-hosted front ends still ask for.
    /// </remarks>
    private static string StripUrlCredentials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)) return value;

        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "" };
        return builder.Uri.ToString();
    }

    private static string ReadRedactedSettings()
    {
        try
        {
            return RedactSettings(File.ReadAllText(SettingsService.FilePath));
        }
        catch (Exception ex)
        {
            // A bundle that is missing one of its three files is still worth having, and the reason
            // it is missing belongs in the bundle rather than in a message box.
            return $"Could not read {CollapseUserPaths(SettingsService.FilePath)}: {ex.Message}";
        }
    }

    private static void AddLogs(ZipArchive archive)
    {
        var directory = LogDirectory;
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, "*.log"))
        {
            try
            {
                // NLog holds app.log open for writing, so the ordinary ZipFile helpers — which ask
                // for FileShare.Read — cannot read the one file the bundle exists for.
                using var source = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entry = archive
                    .CreateEntry($"logs/{Path.GetFileName(file)}", CompressionLevel.Optimal)
                    .Open();
                source.CopyTo(entry);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Could not add {0} to the diagnostic bundle", CollapseUserPaths(file));
            }
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        using var stream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// The facts about the machine that the log does not already carry. The display topology is not
    /// repeated here — <see cref="Export"/> writes a fresh snapshot of it into the log itself, where
    /// it sits in order beside whatever went wrong.
    /// </summary>
    private static string BuildEnvironmentReport()
    {
        var sb = new StringBuilder();
        var process = Process.GetCurrentProcess();

        sb.AppendLine("=== OverTranslate diagnostics ===");
        sb.AppendLine($"generated : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"app       : v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}");
        sb.AppendLine($"install   : {CollapseUserPaths(AppContext.BaseDirectory)}");
        sb.AppendLine($"framework : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"os        : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(
            $"process   : {RuntimeInformation.ProcessArchitecture} " +
            $"uptime={DateTime.Now - process.StartTime:hh\\:mm\\:ss} " +
            $"workingSet={process.WorkingSet64 / (1024 * 1024)}MB");
        sb.AppendLine(
            $"culture   : {CultureInfo.CurrentCulture.Name} " +
            $"ui={CultureInfo.CurrentUICulture.Name} " +
            $"app={LocalizationService.Current}");
        sb.AppendLine(
            $"logging   : verbose={SettingsService.Instance.Current.VerboseLogging} " +
            $"envOverride={LogLevelService.IsOverriddenByEnvironment}");
        sb.AppendLine($"settings  : {CollapseUserPaths(SettingsService.FilePath)}");
        sb.AppendLine($"logs      : {CollapseUserPaths(LogDirectory)}");
        sb.AppendLine($"exports   : {CollapseUserPaths(DefaultExportDirectory)}");
        // Written before any upload is attempted, so this says where it would go rather than where
        // it went — which is the line worth having when the upload is what failed.
        sb.AppendLine($"upload    : {(DiagnosticUploadService.IsConfigured ? DiagnosticUploadService.Endpoint : "(disabled)")}");
        sb.AppendLine();
        sb.AppendLine("=== What is in this file ===");
        sb.AppendLine("environment.txt            this file");
        sb.AppendLine("appsettings.redacted.json  your settings, with API keys replaced by their length");
        sb.AppendLine("logs/app.log               the current log; the numbered ones are older");
        sb.AppendLine();
        sb.AppendLine("This file was written to your disk and is yours to read. It leaves this machine");
        sb.AppendLine("only if you pressed the button that uploads it, and never on its own. The log can");
        sb.AppendLine("contain text that was on your screen while 記錄詳細資訊 was switched on.");

        return sb.ToString();
    }

    /// <summary>
    /// Beside the settings and the logs, in the folder this application already owns.
    /// </summary>
    /// <remarks>
    /// Not the desktop, which is the user's space rather than ours: exports accumulate — the point
    /// of the timestamp in the name is that a second attempt does not overwrite the first — and a
    /// feature that drops another file onto someone's desktop every time they are asked for one is
    /// a feature they end up tidying up after. Here they sit beside the logs they were made from,
    /// and Explorer opens with the file already selected either way, so nothing is harder to find.
    /// </remarks>
    private static string DefaultExportDirectory => AppDataPaths.Diagnostics;

    private static string ResolveDestination(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? DefaultExportDirectory : requested;
}
