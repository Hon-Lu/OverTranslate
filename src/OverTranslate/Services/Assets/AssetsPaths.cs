using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OverTranslate.Services.Assets;

/// <summary>
/// What <see cref="AppDataPaths.AssetsPointerFile"/> holds: where the assets were moved to, and the
/// old location still to be emptied after the restart that follows a move.
/// </summary>
internal sealed record AssetsPointer(string Path, string? PendingDelete = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The pointer, or null when there is none or it cannot be read: the default location then.</summary>
    internal static AssetsPointer? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var path = json.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
            var pending = json.RootElement.TryGetProperty("pendingDelete", out var d) ? d.GetString() : null;
            return new AssetsPointer(path, string.IsNullOrWhiteSpace(pending) ? null : pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the pointer, or removes the file when it would only say the default location with
    /// nothing left to clean: the default needs no pointer.
    /// </summary>
    internal static void Write(string file, AssetsPointer pointer, string defaultRoot)
    {
        if (AssetsPaths.Same(pointer.Path, defaultRoot) && pointer.PendingDelete is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new { path = pointer.Path, pendingDelete = pointer.PendingDelete }, Json));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Path arithmetic for the assets location: the folder name, comparing, resolving.</summary>
internal static class AssetsPaths
{
    /// <summary>The folders the app creates inside the assets root; the only ones it moves or deletes.</summary>
    internal static readonly string[] AppFolders = ["models", "runtimes"];

    /// <summary>
    /// Where the assets go when <paramref name="chosen"/> is picked: the folder itself when it is
    /// already called <c>OverTranslate-assets</c>, otherwise a folder by that name inside it.
    /// </summary>
    internal static string Target(string chosen)
    {
        var full = Path.GetFullPath(chosen);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        if (string.Equals(Path.GetFileName(trimmed), AppDataPaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return Path.Combine(full, AppDataPaths.AssetsFolderName);
    }

    /// <summary>The same path as written, ignoring case and a trailing separator; nothing is resolved.</summary>
    internal static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="folder"/> or anywhere under it, on
    /// separator boundaries (<c>D:\Apps2</c> is not under <c>D:\Apps</c>). Both should already be resolved.
    /// </summary>
    internal static bool IsWithin(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = f.EndsWith('\\') ? f : f + '\\';
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strictly under <paramref name="folder"/>, not the folder itself.</summary>
    internal static bool IsBelow(string path, string folder) =>
        IsWithin(path, folder) && !Same(path, folder);

    private static string Normalize(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        full = full.Replace('/', '\\');
        // A drive root keeps its separator: "D:" alone means the current folder on D.
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    /// <summary>
    /// The path as the file system has it: junctions, symbolic links, substituted drives and 8.3
    /// short names followed to the real folder. A path that does not exist yet is resolved as far
    /// as its nearest existing parent and the rest is put back as written. Never throws; what cannot
    /// be resolved comes back as given.
    /// </summary>
    internal static string Resolve(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        var rest = new Stack<string>();
        var existing = full;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return full;
            rest.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = FinalPath(existing) ?? existing;
        while (rest.Count > 0) resolved = Path.Combine(resolved, rest.Pop());
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>The nearest folder of <paramref name="path"/> that exists, itself included; null when none does.</summary>
    internal static string? NearestExisting(string path)
    {
        string? current;
        try
        {
            current = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        while (current is not null && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);
        return current;
    }

    private static string? FinalPath(string existing)
    {
        using var handle = CreateFileW(existing, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return null;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
        }

        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) return final[4..];
        return final;
    }

    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
