using System.IO;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Assets;

/// <summary>Why a folder cannot take the assets.</summary>
internal enum AssetsProblem
{
    None,
    Invalid,
    Network,
    Cloud,
    Fat,
    NoSpace,
    NotWritable,
    InsideCurrent,
    InsideInstall,
    InsideProgram,
}

/// <summary>What is known about the volume a path is on.</summary>
/// <param name="Drive">The letter, as shown in 「C 槽」.</param>
/// <param name="Format">The file system: NTFS, exFAT, FAT32…</param>
internal sealed record AssetsVolume(string Drive, bool IsNetwork, string Format, long FreeBytes);

/// <summary>The machine, as far as checking a folder needs it; replaced in tests.</summary>
internal interface IAssetsMachine
{
    /// <summary>See <see cref="AssetsPaths.Resolve"/>.</summary>
    string Resolve(string path);

    /// <summary>The volume <paramref name="path"/> is on, or null when there is none there (a drive not connected).</summary>
    AssetsVolume? Volume(string path);

    /// <summary>Whether a cloud service (OneDrive and the like) syncs <paramref name="path"/>.</summary>
    bool IsCloudSynced(string path);

    /// <summary>Whether a file can be written where <paramref name="path"/> would be created.</summary>
    bool CanWrite(string path);

    /// <summary>Whether the assets folder at <paramref name="root"/> already holds models.</summary>
    bool HasModels(string root);
}

/// <summary>Where the app itself is, which a new location may not be inside.</summary>
/// <param name="Current">Where the assets are now.</param>
/// <param name="Install">%LocalAppData%\OverTranslate, Velopack's install root.</param>
/// <param name="Program">The portable build's own folder, or null for the installed build.</param>
/// <param name="CloudRoots">The OneDrive folders the environment names.</param>
/// <param name="UsedBytes">What the assets take now: the room the new location must have.</param>
internal sealed record AssetsContext(
    string Current, string Install, string? Program, IReadOnlyList<string> CloudRoots, long UsedBytes);

/// <summary>The verdict on one folder.</summary>
/// <param name="Target">Where the assets would go (<see cref="AssetsPaths.Target"/>); null when the path is not one.</param>
internal sealed record AssetsCheck(
    AssetsProblem Problem, string? Target, AssetsVolume? Volume, bool HasModels, long NeededBytes)
{
    internal bool Ok => Problem == AssetsProblem.None;
}

/// <summary>Whether a folder can take the assets, and why not.</summary>
/// <remarks>
/// Every comparison is between resolved paths (<see cref="AssetsPaths.Resolve"/>): a junction, a
/// substituted drive or a short name must not be a way into the install folder.
/// </remarks>
internal static class AssetsLocationCheck
{
    internal static AssetsCheck Check(string chosen, AssetsContext context, IAssetsMachine machine)
    {
        AssetsCheck Fail(AssetsProblem problem, string? target = null, AssetsVolume? volume = null) =>
            new(problem, target, volume, false, context.UsedBytes);

        if (string.IsNullOrWhiteSpace(chosen)) return Fail(AssetsProblem.Invalid);
        var raw = chosen.Trim();
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal) || raw.StartsWith(@"\\.\", StringComparison.Ordinal))
            return Fail(AssetsProblem.Invalid);
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            return Fail(AssetsProblem.Network);

        string target;
        try
        {
            if (!Path.IsPathFullyQualified(raw) || raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                raw.IndexOf(':', 2) >= 0)
                return Fail(AssetsProblem.Invalid);
            target = AssetsPaths.Target(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(AssetsProblem.Invalid);
        }

        var volume = machine.Volume(target);
        if (volume is null) return Fail(AssetsProblem.Invalid, target);
        if (volume.IsNetwork) return Fail(AssetsProblem.Network, target, volume);

        var resolved = machine.Resolve(target);
        if (resolved.StartsWith(@"\\", StringComparison.Ordinal)) return Fail(AssetsProblem.Network, target, volume);

        if (AssetsPaths.IsBelow(resolved, machine.Resolve(context.Current)))
            return Fail(AssetsProblem.InsideCurrent, target, volume);
        if (AssetsPaths.IsWithin(resolved, machine.Resolve(context.Install)))
            return Fail(AssetsProblem.InsideInstall, target, volume);
        if (context.Program is { } program)
        {
            var programRoot = machine.Resolve(program);
            if (AssetsPaths.IsWithin(resolved, programRoot) &&
                !AssetsPaths.Same(resolved, Path.Combine(programRoot, AppDataPaths.AssetsFolderName)))
                return Fail(AssetsProblem.InsideProgram, target, volume);
        }

        if (context.CloudRoots.Any(root => AssetsPaths.IsWithin(resolved, machine.Resolve(root))) ||
            machine.IsCloudSynced(target))
            return Fail(AssetsProblem.Cloud, target, volume);

        if (volume.Format.Equals("FAT", StringComparison.OrdinalIgnoreCase) ||
            volume.Format.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            return Fail(AssetsProblem.Fat, target, volume);

        if (!machine.CanWrite(target)) return Fail(AssetsProblem.NotWritable, target, volume);

        if (context.UsedBytes > 0 && volume.FreeBytes < context.UsedBytes)
            return Fail(AssetsProblem.NoSpace, target, volume);

        return new AssetsCheck(AssetsProblem.None, target, volume, machine.HasModels(target), context.UsedBytes);
    }

    /// <summary>The OneDrive folders the environment names, for <see cref="AssetsContext.CloudRoots"/>.</summary>
    internal static IReadOnlyList<string> OneDriveRoots() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>The real machine.</summary>
internal sealed class SystemAssetsMachine : IAssetsMachine
{
    internal static SystemAssetsMachine Instance { get; } = new();

    public string Resolve(string path) => AssetsPaths.Resolve(path);

    public AssetsVolume? Volume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            return new AssetsVolume(
                root.TrimEnd('\\', ':'), drive.DriveType == DriveType.Network, drive.DriveFormat, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public bool IsCloudSynced(string path)
    {
        // The Cloud Files API answers for any folder under a registered sync root: OneDrive, and
        // the other services that sync through it.
        if (AssetsPaths.NearestExisting(path) is not { } existing) return false;
        try
        {
            var buffer = new byte[1024];
            int hr = CfGetSyncRootInfoByPath(existing, 0, buffer, (uint)buffer.Length, out _);
            // ERROR_MORE_DATA: there is a sync root, with more to say than the buffer holds.
            return hr >= 0 || hr == unchecked((int)0x800700EA);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public bool CanWrite(string path)
    {
        var folder = Directory.Exists(path) ? path : AssetsPaths.NearestExisting(path);
        if (folder is null) return false;
        try
        {
            var probe = Path.Combine(folder, ".overtranslate-write-" + Guid.NewGuid().ToString("N")[..8]);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public bool HasModels(string root) => AssetsUsage.HasFiles(root);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath, int infoClass, byte[] info, uint infoLength, out uint returnedLength);
}

/// <summary>How much the app's folders inside an assets root take.</summary>
internal static class AssetsUsage
{
    /// <summary>The bytes in <see cref="AssetsPaths.AppFolders"/> under <paramref name="root"/>; links are not followed.</summary>
    internal static long UsedBytes(string root) => Files(root).Sum(file => file.Length);

    internal static bool HasFiles(string root) => Files(root).Any();

    /// <summary>Every file the app put under <paramref name="root"/>, in its own folders.</summary>
    internal static IEnumerable<FileInfo> Files(string root)
    {
        foreach (var name in AssetsPaths.AppFolders)
        {
            var folder = new DirectoryInfo(Path.Combine(root, name));
            bool exists;
            try
            {
                exists = folder.Exists;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exists = false;
            }

            if (!exists) continue;
            foreach (var file in Walk(folder))
                yield return file;
        }
    }

    private static IEnumerable<FileInfo> Walk(DirectoryInfo folder)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (entry is DirectoryInfo sub)
            {
                foreach (var file in Walk(sub)) yield return file;
            }
            else if (entry is FileInfo file)
            {
                yield return file;
            }
        }
    }
}
