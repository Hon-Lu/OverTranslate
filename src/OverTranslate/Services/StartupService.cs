using System.IO;
using Microsoft.Win32;

namespace OverTranslate.Services;

public static class StartupService
{
    internal const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AppName = "OverTranslate";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppName) is not null;
        }
    }

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Removes the sign-in entry if it starts an executable under <paramref name="directory"/>, for
    /// <see cref="UninstallCleanup"/>. One naming another copy — a portable build set to start with
    /// Windows — is that copy's, and stays.
    /// </summary>
    internal static void RemoveIfPointsInto(RegistryKey runKey, string directory)
    {
        if (runKey.GetValue(AppName) is not string command) return;
        var exe = command.Trim();
        exe = exe.StartsWith('"') ? exe[1..].Split('"')[0] : exe.Split(' ')[0];

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            runKey.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
