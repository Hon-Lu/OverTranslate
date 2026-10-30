using System.IO;
using System.Reflection;
using NLog;
using Velopack;

namespace OverTranslate.Services;

/// <summary>
/// Keeps a copy of the settings file from before an update that changes what something in it means,
/// so what the update replaced is still there to look at or put back.
/// </summary>
/// <remarks>
/// <para>Decided by the version that last ran (<see cref="Models.AppSettings.LastRunVersion"/>),
/// against a list of the versions that broke something. Not by the version now running: a dev build
/// still says 2.9.0 in its csproj while already writing the new format, and must keep the copy all
/// the same.</para>
///
/// <para>A copy rather than a migration: the old OpenAI prompts were a system and a user message per
/// language mode, and a guess at turning them into one template would be wrong for some of the
/// people it touched without saying so. The update note points at the copy instead.</para>
/// </remarks>
public static class SettingsBackup
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>A version whose settings mean something different from those of every version before it.</summary>
    /// <param name="Change">What changed, for whoever reads this list next.</param>
    public sealed record Break(SemanticVersion Version, string Change);

    public static readonly IReadOnlyList<Break> Breaks =
    [
        new(SemanticVersion.Parse("2.10.0"), "OpenAI 提示詞改為單一模板"),
    ];

    /// <summary>
    /// The running version as <see cref="Models.AppSettings.LastRunVersion"/> stores it: the
    /// assembly's informational version without the <c>+commit</c> the build appends.
    /// </summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>
    /// Whether a file last run by <paramref name="lastRunVersion"/> is from before one of the
    /// <see cref="Breaks"/>.
    /// </summary>
    /// <remarks>
    /// Compared as semantic versions, so 2.10.0-beta.1 comes before 2.10.0. Empty — a file from 2.9.0
    /// or earlier — or unreadable counts as older than all of them.
    /// </remarks>
    public static bool Needed(string lastRunVersion) =>
        !SemanticVersion.TryParse(lastRunVersion, out var last)
            ? Breaks.Count > 0
            : Breaks.Any(b => last < b.Version);

    /// <summary>Where the copy for <paramref name="version"/> goes, beside the settings file.</summary>
    public static string PathFor(string settingsPath, string version) =>
        Path.Combine(Path.GetDirectoryName(settingsPath)!, $"appsettings.before-{version}.json");

    /// <summary>
    /// Copies <paramref name="source"/> byte for byte to <paramref name="destination"/>, unless a copy
    /// is already there.
    /// </summary>
    /// <remarks>
    /// The one already there is from the first start of this version, and so the older of the two.
    /// A failure is only logged: no copy is worth not starting over.
    /// </remarks>
    public static void Copy(string source, string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                Log.Info("Settings backup {0} already there; left as it is", destination);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            Log.Info("Settings backed up to {0} before an incompatible update", destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Could not back the settings up to {0}", destination);
        }
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational is { Length: > 0 }
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
