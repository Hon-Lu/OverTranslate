using System.IO;

namespace OverTranslate.Services;

/// <summary>
/// Where the app keeps what it writes outside its install folder: settings, logs and diagnostic
/// exports under <c>%AppData%\OverTranslate</c>, and large downloads — the manga models and the
/// DirectML runtime they run on — under <c>%LocalAppData%\OverTranslate-assets</c>.
/// </summary>
/// <remarks>
/// <para>Two roots because the two kinds of data travel differently. <c>%AppData%</c> is the
/// roaming profile: on a domain with roaming profiles it is copied to and from the server at every
/// sign-in and sign-out, and with folder redirection it may live on a network share outright. A few
/// kilobytes of settings belong there; three hundred megabytes of models do not. <c>%LocalAppData%</c>
/// never leaves the machine, and the downloads are machine-specific anyway — they are fetched again
/// on another one at no cost but time.</para>
///
/// <para>Not <c>%LocalAppData%\OverTranslate</c> itself: that is Velopack's install root. Each update
/// replaces its <c>current\</c> wholesale, and running Setup over an existing install renames the
/// whole root aside and starts again from an empty folder (velopack
/// <c>src/bins/src/commands/install.rs</c>) — anything kept beside <c>current\</c> would be gone after
/// reinstalling. Hence a sibling folder with a name of its own.</para>
///
/// <para>Uninstalling removes the assets root and nothing else (<see cref="UninstallCleanup"/>):
/// settings and logs stay, so a reinstall picks up where the last install left off.</para>
///
/// <para>NLog.config spells the log folder out itself, <c>${specialfolder:folder=ApplicationData}/OverTranslate/logs</c>;
/// it is read before any code runs and cannot refer to this class.</para>
/// </remarks>
internal static class AppDataPaths
{
    internal const string AssetsFolderName = "OverTranslate-assets";

    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverTranslate");

    /// <summary>
    /// The settings file, named here rather than only in <see cref="SettingsService"/> for the
    /// uninstall hook, which reads the assets location from it without loading the settings.
    /// </summary>
    internal static string SettingsFile => Path.Combine(Root, "appsettings.json");

    internal static string Logs => Path.Combine(Root, "logs");

    internal static string Diagnostics => Path.Combine(Root, "diagnostics");

    /// <summary><c>%LocalAppData%\OverTranslate-assets</c>, wherever the assets were moved to.</summary>
    internal static string DefaultAssetsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AssetsFolderName);

    private static string? _assetsRoot;

    /// <summary>
    /// Where the assets are: what the person chose (<see cref="Models.AppSettings.AssetsLocation"/>),
    /// else <see cref="DefaultAssetsRoot"/>. Read from the settings the first time anything asks,
    /// which is before any model loads; changes only through <see cref="UseAssetsRoot"/>, when the
    /// location is changed with nothing in it to move.
    /// </summary>
    internal static string AssetsRoot
    {
        get
        {
            var root = Volatile.Read(ref _assetsRoot);
            if (root is not null) return root;
            root = ResolveAssetsRoot(SettingsService.Instance.Current.AssetsLocation, DefaultAssetsRoot);
            Interlocked.CompareExchange(ref _assetsRoot, root, null);
            return Volatile.Read(ref _assetsRoot)!;
        }
    }

    /// <summary>The chosen location when it is a full path, else the default.</summary>
    internal static string ResolveAssetsRoot(string? chosen, string defaultRoot) =>
        Assets.SavedAssetsLocation.FullPathOrNull(chosen) ?? defaultRoot;

    /// <summary>Whether <see cref="AssetsRoot"/> is the default location.</summary>
    internal static bool IsDefaultAssetsRoot => Assets.AssetsPaths.Same(AssetsRoot, DefaultAssetsRoot);

    internal static void UseAssetsRoot(string root) => Volatile.Write(ref _assetsRoot, Path.GetFullPath(root));

    internal static string Models => Path.Combine(AssetsRoot, "models");

    /// <summary>Native libraries downloaded on demand, one folder per library and version.</summary>
    internal static string Runtimes => Path.Combine(AssetsRoot, "runtimes");
}
