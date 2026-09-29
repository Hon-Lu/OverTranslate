using System.IO;

namespace OverTranslate.Services;

/// <summary>
/// Where the app keeps what it writes outside its install folder: settings, logs, diagnostic
/// exports and downloaded models, all under one <c>%AppData%\OverTranslate</c>.
/// </summary>
/// <remarks>
/// <para>Not <c>%LocalAppData%\OverTranslate</c>: that is Velopack's install folder, and each update
/// replaces its <c>current\</c> wholesale. One root rather than a path per feature, so that
/// uninstalling can remove all of it (<see cref="UninstallCleanup"/>) without a list to keep in step.</para>
///
/// <para>NLog.config spells the log folder out itself, <c>${specialfolder:folder=ApplicationData}/OverTranslate/logs</c>;
/// it is read before any code runs and cannot refer to this class.</para>
/// </remarks>
internal static class AppDataPaths
{
    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverTranslate");

    internal static string Logs => Path.Combine(Root, "logs");

    internal static string Diagnostics => Path.Combine(Root, "diagnostics");

    internal static string Models => Path.Combine(Root, "models");
}
