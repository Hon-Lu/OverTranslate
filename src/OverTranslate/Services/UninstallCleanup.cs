using System.IO;
using Microsoft.Win32;

namespace OverTranslate.Services;

/// <summary>
/// What uninstalling removes besides the install folder: the large downloads under
/// <see cref="AppDataPaths.DefaultAssetsRoot"/> — models, the DirectML runtime — and the sign-in
/// entry pointing at the executable that is about to go.
/// </summary>
/// <remarks>
/// <para>Settings and logs under <see cref="AppDataPaths.Root"/> are left alone: a reinstall picks
/// them up again, and they are a few kilobytes. The downloads are hundreds of megabytes that nothing
/// else will ever use.</para>
///
/// <para>Run from Velopack's before-uninstall hook (see App.Main), which starts this executable with
/// an argument, waits for it, and ends the process straight after: before WPF, before settings are
/// loaded, with no window to show anything in. So this does nothing but delete: no UI, no logging,
/// and never an exception, since an uninstall that stopped here would leave the app half removed.
/// A file something else still holds open is left where it is.</para>
///
/// <para>The portable build has no uninstall and never gets here.</para>
/// </remarks>
internal static class UninstallCleanup
{
    internal static void Run()
    {
        try
        {
            RemoveAssets(AppDataPaths.DefaultAssetsRoot);
        }
        catch
        {
            // Best effort; see the remarks.
        }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupService.RunKey, writable: true);
            if (run is not null)
                StartupService.RemoveIfPointsInto(run, InstallRoot(AppContext.BaseDirectory));
        }
        catch
        {
            // As above.
        }
    }

    /// <summary>
    /// Removes the assets folder, and only a folder by that name: whatever path it is handed, this
    /// never deletes <c>%LocalAppData%</c> or anything else that is not ours. Returns how many files
    /// and folders had to be left behind, or -1 when the path was refused.
    /// </summary>
    internal static int RemoveAssets(string assetsRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsRoot));
        if (!string.Equals(Path.GetFileName(full), AppDataPaths.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
            return -1;
        return RemoveAll(full);
    }

    /// <summary>
    /// Deletes <paramref name="root"/> and everything in it, as far as it can. Returns how many files
    /// and folders had to be left behind — held open by another process, or not ours to delete.
    /// </summary>
    internal static int RemoveAll(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var left = RemoveContents(root);
        return left + (TryDelete(() => Directory.Delete(root)) ? 0 : 1);
    }

    // Velopack runs the app from <root>\current\; the sign-in entry may name that executable or one
    // beside it, so anything under the install root counts as this install's.
    internal static string InstallRoot(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(baseDirectory);
        return string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;
    }

    private static int RemoveContents(string directory)
    {
        int left = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left++;
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                // A junction or link is removed, not followed: what it points at is not ours.
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    left += RemoveContents(entry);
                if (!TryDelete(() => Directory.Delete(entry))) left++;
            }
            else if (!TryDelete(() =>
                     {
                         if (attributes.HasFlag(FileAttributes.ReadOnly))
                             File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                         File.Delete(entry);
                     }))
            {
                left++;
            }
        }

        return left;
    }

    private static bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
