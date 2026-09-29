using System.IO;
using Microsoft.Win32;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class UninstallCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-uninstall-" + Guid.NewGuid().ToString("N"));
    private readonly string _runKeyPath = @"Software\OverTranslateTests-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }

        Registry.CurrentUser.DeleteSubKeyTree(_runKeyPath, throwOnMissingSubKey: false);
    }

    private string Write(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void EverythingUnderTheRoot_IsRemoved_TheRootWithIt()
    {
        Write("appsettings.json");
        Write(@"logs\app.log");
        Write(@"logs\frames\0001.png");
        Write(@"models\manga-vertical\v1\vocab.txt");
        File.SetAttributes(Write(@"diagnostics\readonly.zip"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(_root, @"models\empty"));

        Assert.Equal(0, UninstallCleanup.RemoveAll(_root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void AFileHeldOpen_IsLeft_AndNothingThrows()
    {
        Write("appsettings.json");
        Write(@"models\manga-vertical\v1\vocab.txt");
        var log = Write(@"logs\app.log");

        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The file, its folder and the root stay; everything else goes.
            Assert.Equal(3, UninstallCleanup.RemoveAll(_root));
        }

        Assert.True(File.Exists(log));
        Assert.False(File.Exists(Path.Combine(_root, "appsettings.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "models")));
    }

    [Fact]
    public void ARootThatIsNotThere_IsNothingToDo() =>
        Assert.Equal(0, UninstallCleanup.RemoveAll(_root));

    [Fact]
    public void AJunctionInside_IsRemovedWithoutTouchingWhatItPointsAt()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ot-uninstall-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        try
        {
            Write("appsettings.json");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
        }
        catch (IOException)
        {
            // Creating a symbolic link needs developer mode or elevation; nothing to test without it.
            Directory.Delete(outside, recursive: true);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            Assert.Equal(0, UninstallCleanup.RemoveAll(_root));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\OverTranslate\current\", @"C:\Users\u\AppData\Local\OverTranslate")]
    [InlineData(@"C:\Users\u\AppData\Local\OverTranslate\current", @"C:\Users\u\AppData\Local\OverTranslate")]
    [InlineData(@"D:\Tools\OverTranslate\", @"D:\Tools\OverTranslate")]
    public void TheInstallRoot_IsTheFolderAboveCurrent(string baseDirectory, string expected) =>
        Assert.Equal(expected, UninstallCleanup.InstallRoot(baseDirectory));

    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\OverTranslate\current\OverTranslate.exe""", true)]
    [InlineData(@"C:\Users\u\AppData\Local\OverTranslate\OverTranslate.exe", true)]
    [InlineData(@"""D:\Tools\OverTranslate\OverTranslate.exe""", false)]
    [InlineData(@"""C:\Users\u\AppData\Local\OverTranslateOther\OverTranslate.exe""", false)]
    public void TheSignInEntry_IsRemovedOnlyWhenItStartsThisInstall(string command, bool removed)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
        key.SetValue(StartupService.AppName, command);
        key.SetValue("SomethingElse", "\"C:\\other.exe\"");

        StartupService.RemoveIfPointsInto(key, @"C:\Users\u\AppData\Local\OverTranslate");

        Assert.Equal(!removed, key.GetValue(StartupService.AppName) is not null);
        Assert.NotNull(key.GetValue("SomethingElse"));
    }
}
