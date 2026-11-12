using System.IO;

namespace OverTranslate.Services.Assets;

/// <summary>
/// Copies the app's folders of one assets root into another, all or nothing.
/// </summary>
/// <remarks>
/// <para>Everything is first copied into a staging folder inside the new location, so a move that is
/// cancelled, or fails halfway — a full disk, a drive pulled out — leaves the new location as it was
/// and the old one untouched. Only once every file is there are they put in place, a rename on the
/// same volume. The old location is emptied later, after the restart, by
/// <see cref="AssetsRelocation.CleanUpPending"/>: by then nothing has the old files open.</para>
///
/// <para>A file already at the new location under the same name and size is kept rather than
/// copied again: moving back to a location that still has the models costs nothing.</para>
/// </remarks>
internal static class AssetsMover
{
    internal const string StagingName = ".moving";

    /// <summary>Copies one file; replaced in tests to fail or to stall.</summary>
    internal delegate Task CopyFile(string from, string to, Action<long> copied, CancellationToken token);

    /// <summary>
    /// Copies <see cref="AssetsPaths.AppFolders"/> from <paramref name="from"/> into <paramref name="to"/>.
    /// Throws <see cref="OperationCanceledException"/> when cancelled, or what the copy threw when it
    /// failed; either way nothing of it is left at <paramref name="to"/>.
    /// </summary>
    /// <param name="progress">Bytes done so far and bytes in all.</param>
    internal static async Task CopyAsync(
        string from, string to, Action<long, long>? progress, CancellationToken token, CopyFile? copy = null)
    {
        copy ??= CopyFileAsync;
        var files = AssetsUsage.Files(from)
            .Select(file => (File: file, Relative: Path.GetRelativePath(from, file.FullName)))
            .ToList();
        long total = files.Sum(entry => entry.File.Length);
        long done = 0;
        progress?.Invoke(0, total);

        var createdRoot = !Directory.Exists(to);
        var staging = Path.Combine(to, StagingName);
        var placed = new List<string>();
        try
        {
            DeleteFolder(staging);
            var staged = new List<(string Staged, string Final)>();
            foreach (var (file, relative) in files)
            {
                token.ThrowIfCancellationRequested();
                var final = Path.Combine(to, relative);
                if (new FileInfo(final) is { Exists: true } there && there.Length == file.Length)
                {
                    done += file.Length;
                    progress?.Invoke(done, total);
                    continue;
                }

                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                long before = done;
                await copy(file.FullName, target, bytes => progress?.Invoke(before + bytes, total), token)
                    .ConfigureAwait(false);
                done += file.Length;
                progress?.Invoke(done, total);
                staged.Add((target, final));
            }

            token.ThrowIfCancellationRequested();
            foreach (var (stagedFile, final) in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(stagedFile, final, overwrite: true);
                placed.Add(final);
            }

            DeleteFolder(staging);
        }
        catch
        {
            DeleteFolder(staging);
            foreach (var file in placed)
                TryDelete(file);
            RemoveEmptyFolders(to, removeRoot: createdRoot);
            throw;
        }
    }

    private static async Task CopyFileAsync(string from, string to, Action<long> copied, CancellationToken token)
    {
        // Shared for writing and deleting too: the DirectML runtime is loaded, and a model may be
        // mapped by the server that runs it; reading them is all a copy needs.
        await using var input = new FileStream(from, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        await using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
            copied(total);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    /// <summary>
    /// Empties the app's folders at an old location: <see cref="AssetsPaths.AppFolders"/> go, and the
    /// root with them if nothing else is left in it. Whatever else is there was not put there by the app.
    /// </summary>
    /// <returns>How many files and folders had to be left behind.</returns>
    internal static int RemoveAppFolders(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(Path.GetFileName(full), AppDataPaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
            return -1;
        int left = 0;
        foreach (var name in AssetsPaths.AppFolders.Append(StagingName))
            left += UninstallCleanup.RemoveAll(Path.Combine(full, name));
        try
        {
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left++;
        }

        return left;
    }

    private static void RemoveEmptyFolders(string root, bool removeRoot)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            // Only inside the app's own folders: anything else at the new location is not ours.
            foreach (var name in AssetsPaths.AppFolders)
            {
                var own = Path.Combine(root, name);
                if (!Directory.Exists(own)) continue;
                foreach (var folder in Directory.EnumerateDirectories(own, "*", SearchOption.AllDirectories)
                             .Append(own)
                             .OrderByDescending(path => path.Length))
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
            }

            if (removeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
