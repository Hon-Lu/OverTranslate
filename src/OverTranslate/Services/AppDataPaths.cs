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
    /// <summary>
    /// Points <see cref="AssetsRoot"/> somewhere else, for trying a download without touching the
    /// real one. Uninstalling ignores it and always removes <see cref="DefaultAssetsRoot"/>.
    /// </summary>
    internal const string AssetsOverride = "OVERTRANSLATE_ASSETS_DIR";

    internal const string AssetsFolderName = "OverTranslate-assets";

    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverTranslate");

    internal static string Logs => Path.Combine(Root, "logs");

    internal static string Diagnostics => Path.Combine(Root, "diagnostics");

    /// <summary><c>%LocalAppData%\OverTranslate-assets</c>, whatever <see cref="AssetsOverride"/> says.</summary>
    internal static string DefaultAssetsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AssetsFolderName);

    /// <summary>Whether <see cref="AssetsOverride"/> names the assets root; the location cannot be changed then.</summary>
    internal static bool IsAssetsOverridden =>
        Environment.GetEnvironmentVariable(AssetsOverride) is { Length: > 0 };

    /// <summary>
    /// The small file naming where the person moved the assets to (<see cref="Assets.AssetsPointer"/>).
    /// A file of its own rather than a setting: uninstalling reads it before any setting is loaded,
    /// and backing settings up or restoring them must not carry a path to another machine's disk.
    /// </summary>
    internal static string AssetsPointerFile => Path.Combine(Root, "assets-location.json");

    private static string? _assetsRoot;

    /// <summary>
    /// Where the assets are: <see cref="AssetsOverride"/> when set, else what the person chose
    /// (<see cref="AssetsPointerFile"/>), else <see cref="DefaultAssetsRoot"/>. Changes only through
    /// <see cref="UseAssetsRoot"/>, when the location is changed with nothing in it to move.
    /// </summary>
    internal static string AssetsRoot
    {
        get
        {
            var root = Volatile.Read(ref _assetsRoot);
            if (root is not null) return root;
            root = ResolveAssetsRoot(Environment.GetEnvironmentVariable(AssetsOverride), AssetsPointerFile, DefaultAssetsRoot);
            Interlocked.CompareExchange(ref _assetsRoot, root, null);
            return Volatile.Read(ref _assetsRoot)!;
        }
    }

    /// <summary>The override when there is one, else the pointer's location, else the default.</summary>
    internal static string ResolveAssetsRoot(string? overridden, string pointerFile, string defaultRoot) =>
        overridden is { Length: > 0 }
            ? Path.GetFullPath(overridden)
            : Assets.AssetsPointer.Read(pointerFile)?.Path is { } chosen ? Path.GetFullPath(chosen) : defaultRoot;

    /// <summary>Whether <see cref="AssetsRoot"/> is the default location.</summary>
    internal static bool IsDefaultAssetsRoot => Assets.AssetsPaths.Same(AssetsRoot, DefaultAssetsRoot);

    internal static void UseAssetsRoot(string root) => Volatile.Write(ref _assetsRoot, Path.GetFullPath(root));

    internal static string Models => Path.Combine(AssetsRoot, "models");

    /// <summary>Native libraries downloaded on demand, one folder per library and version.</summary>
    internal static string Runtimes => Path.Combine(AssetsRoot, "runtimes");
}
