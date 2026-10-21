using System.IO;
using Microsoft.Win32;
using NLog;
using Velopack.Locators;

namespace OverTranslate.Services;

/// <summary>
/// The name and icon Windows puts on this application's notifications.
/// </summary>
/// <remarks>
/// <para>Velopack gives the process an explicit AppUserModelID, <c>velopack.OverTranslate</c>, and
/// Windows heads every notification with whatever it can find registered under that ID. Velopack
/// registers it in one place only: the Start menu shortcut it creates on install. A user who has
/// deleted that shortcut, and every portable copy, which never had one, gets the bare ID as the
/// header and no icon.</para>
///
/// <para>So the ID is registered here as well, under <c>HKCU\Software\Classes\AppUserModelId</c> —
/// per user, no elevation. It is the key Windows App SDK's notification registration writes for an
/// unpackaged app, and the one the shell itself fills in for tray balloons from processes it cannot
/// identify (<c>NotifyIconGeneratedAumid_*</c>).</para>
///
/// <para>Checked on every launch rather than written once at install: a portable copy has no
/// install, and whichever copy ran last — an install, a portable folder — is the one whose icon
/// file exists. Only written when it differs. Uninstalling removes it if it is
/// still this install's (<see cref="UninstallCleanup"/>); a portable copy has no uninstall and
/// leaves it behind, the same as its settings.</para>
/// </remarks>
internal static class NotificationIdentityService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const string AumidKeyPath = @"Software\Classes\AppUserModelId";
    internal const string DisplayName = "OverTranslate";

    /// <summary>The application icon, shipped beside the executable for this alone.</summary>
    internal const string IconRelativePath = @"icons\app.ico";

    /// <summary>
    /// The ID Velopack set on this process, or null when it set none — a build run from the IDE,
    /// which has no package around it.
    /// </summary>
    internal static string? CurrentAumid =>
        VelopackLocator.IsCurrentSet ? VelopackLocator.Current.AppUserModelId : null;

    public static void Register()
    {
        var aumid = CurrentAumid;
        if (string.IsNullOrEmpty(aumid)) return;

        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(AumidKeyPath);
            Write(root, aumid, Path.Combine(AppContext.BaseDirectory, IconRelativePath));
        }
        catch (Exception ex)
        {
            // Only the notification header is at stake; it falls back to the bare ID.
            Log.Warn(ex, "Could not register the notification identity {0}", aumid);
        }
    }

    /// <summary>
    /// Brings the registration in line with this copy, and returns whether anything had to change.
    /// Read before writing: on an ordinary launch both values are already right, and an unchanged
    /// write still wakes anything watching the key.
    /// </summary>
    internal static bool Write(RegistryKey root, string aumid, string iconPath)
    {
        using (var existing = root.OpenSubKey(aumid, writable: false))
        {
            if (existing?.GetValue("DisplayName") as string == DisplayName
                && existing.GetValue("IconUri") as string == iconPath)
                return false;
        }

        using var key = root.CreateSubKey(aumid);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("IconUri", iconPath);
        return true;
    }

    /// <summary>
    /// Removes the registration if its icon lies under <paramref name="directory"/>, for
    /// <see cref="UninstallCleanup"/>. One a portable copy wrote last is that copy's, and stays.
    /// </summary>
    internal static void RemoveIfPointsInto(RegistryKey root, string aumid, string directory)
    {
        bool ours;
        using (var key = root.OpenSubKey(aumid, writable: false))
        {
            if (key?.GetValue("IconUri") is not string icon || icon.Length == 0) return;
            var under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            ours = Path.GetFullPath(icon).StartsWith(under, StringComparison.OrdinalIgnoreCase);
        }

        if (ours) root.DeleteSubKeyTree(aumid, throwOnMissingSubKey: false);
    }
}
