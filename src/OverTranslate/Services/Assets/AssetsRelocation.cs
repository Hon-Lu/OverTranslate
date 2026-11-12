using System.Diagnostics;
using System.IO;
using NLog;
using OverTranslate.Services.LocalAi;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using Velopack.Locators;

namespace OverTranslate.Services.Assets;

/// <summary>
/// Changing where the models and runtimes are kept: immediately when there is nothing to move,
/// otherwise by copying, switching and restarting.
/// </summary>
/// <remarks>
/// <para>With files to move, the app restarts on the new location instead of switching in place:
/// the DirectML runtime cannot be unloaded once loaded, and a restart is the one moment nothing
/// holds the old files, so they are deleted then (<see cref="CleanUpPending"/>), before anything
/// could load them.</para>
///
/// <para>A location on a drive that is not connected is only reported; nothing falls back to the
/// default or downloads again, since the drive coming back is the likelier ending.</para>
/// </remarks>
internal static class AssetsRelocation
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Passed to the restarted app with the old process's id, so it waits for that to end first.</summary>
    internal const string RestartArgument = "--assets-moved";

    /// <summary>The location changed in this process, without a restart.</summary>
    internal static event EventHandler? Changed;

    internal static string Current => AppDataPaths.AssetsRoot;

    internal static string Default => AppDataPaths.DefaultAssetsRoot;

    /// <summary>Velopack's install root, <c>%LocalAppData%\OverTranslate</c>.</summary>
    internal static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverTranslate");

    internal static bool IsPortable => VelopackLocator.IsCurrentSet && VelopackLocator.Current.IsPortable;

    /// <summary>The portable build's own folder, where <c>.portable</c> is; null for the installed build.</summary>
    internal static string? ProgramRoot => IsPortable ? VelopackLocator.Current.RootAppDir : null;

    /// <summary>Whether the drive the assets are on is there at all.</summary>
    internal static bool IsAccessible => SystemAssetsMachine.Instance.Volume(Current) is not null;

    internal static AssetsContext Context() =>
        new(Current, InstallRoot, ProgramRoot, AssetsLocationCheck.OneDriveRoots(), AssetsUsage.UsedBytes(Current));

    /// <summary>Whether any model is downloading; the location cannot change then.</summary>
    internal static bool AnyDownloading =>
        AppServices.MangaModels.State == MangaModelState.Downloading ||
        AppServices.LocalAi.Store.Downloading is not null ||
        OcrLanguagePacks.All.Any(pack => pack.Store.State == MangaModelState.Downloading);

    /// <summary>
    /// Switches to <paramref name="target"/> straight away, with nothing moved: there is nothing to
    /// move, or the old location is on a drive that is gone.
    /// </summary>
    internal static void SwitchWithoutMoving(string target)
    {
        var full = Path.GetFullPath(target);
        AssetsPointer.Write(AppDataPaths.AssetsPointerFile, new AssetsPointer(full), Default);
        AppDataPaths.UseAssetsRoot(full);
        AppServices.MangaModels.Relocate(MangaModelStore.DefaultRoot, MangaModelStore.DefaultRuntimeRoot);
        AppServices.LocalAi.Store.Relocate(LocalAiModelStore.DefaultRoot, LocalAiRuntimeStore.DefaultRoot);
        foreach (var pack in OcrLanguagePacks.All)
        {
            pack.Store.Relocate(OcrLanguagePacks.RootOf(pack.Key), null);
            pack.Refresh();
        }

        Log.Info("Assets location changed to {Path} with nothing to move", full);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Copies everything to <paramref name="target"/> and points the app there, leaving the old
    /// location to be emptied after the restart. Throws when cancelled or when the copy failed, and
    /// then nothing has changed.
    /// </summary>
    internal static async Task MoveAsync(string target, Action<long, long> progress, CancellationToken token)
    {
        var from = Current;
        var to = Path.GetFullPath(target);
        Log.Info("Moving the assets from {From} to {To}", from, to);
        await Task.Run(() => AssetsMover.CopyAsync(from, to, progress, token), token);
        AssetsPointer.Write(AppDataPaths.AssetsPointerFile, new AssetsPointer(to, from), Default);
        Log.Info("Assets copied to {To}; {From} is emptied after the restart", to, from);
    }

    /// <summary>
    /// Starts the app again, to come up on the settings page once this one has gone. The caller
    /// exits straight after.
    /// </summary>
    internal static void StartReplacement()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(RestartArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(start)?.Dispose();
        Log.Info("Restarting after moving the assets");
    }

    /// <summary>
    /// The process id the restart was asked by, when this launch is one: it has to have ended
    /// before this one takes the single-instance check, or this one would only wake it and exit.
    /// </summary>
    internal static int? RestartedFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == RestartArgument && int.TryParse(args[i + 1], out var pid))
                return pid;
        return null;
    }

    internal static void WaitForExit(int pid)
    {
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (!previous.WaitForExit(TimeSpan.FromSeconds(30)))
                Log.Warn("The process the restart came from, {Pid}, is still running", pid);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn(ex, "Could not wait for the process the restart came from");
        }
    }

    /// <summary>
    /// After the restart that follows a move, before anything loads a model: empties the old
    /// location. What cannot be deleted yet is tried again at the next start.
    /// </summary>
    internal static void CleanUpPending()
    {
        if (AppDataPaths.IsAssetsOverridden) return;
        var file = AppDataPaths.AssetsPointerFile;
        if (AssetsPointer.Read(file) is not { PendingDelete: { } old } pointer) return;

        // Never the location in use, whatever the file says.
        if (AssetsPaths.IsWithin(AssetsPaths.Resolve(pointer.Path), AssetsPaths.Resolve(old)) ||
            AssetsPaths.IsWithin(AssetsPaths.Resolve(old), AssetsPaths.Resolve(pointer.Path)))
        {
            Log.Warn("Not emptying {Old}: it holds the assets location {Path}", old, pointer.Path);
            WriteQuietly(file, pointer with { PendingDelete = null });
            return;
        }

        int left = 0;
        if (SystemAssetsMachine.Instance.Volume(old) is not null)
        {
            left = AssetsMover.RemoveAppFolders(old);
            Log.Info("Emptied the old assets location {Old}; {Left} entries left", old, left);
        }

        if (left <= 0) WriteQuietly(file, pointer with { PendingDelete = null });
    }

    private static void WriteQuietly(string file, AssetsPointer pointer)
    {
        try
        {
            AssetsPointer.Write(file, pointer, Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "The assets location file could not be written");
        }
    }
}
