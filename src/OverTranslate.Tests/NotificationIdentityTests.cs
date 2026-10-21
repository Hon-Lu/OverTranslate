using Microsoft.Win32;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// Uninstalling removes the notification registration only when it is this install's: a portable
// copy that ran last owns it, and taking it away would put the bare ID back on that copy's
// notifications. Run against a throwaway key under HKCU, never the real AppUserModelId one.
public class NotificationIdentityTests : IDisposable
{
    private const string Aumid = "velopack.OverTranslate";
    private const string Parent = @"Software\OverTranslate.Tests";
    private readonly string _path = $@"{Parent}\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public NotificationIdentityTests() => _root = Registry.CurrentUser.CreateSubKey(_path);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);

        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent))
            empty = parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0;
        if (empty) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Write_RecordsTheNameAndIcon()
    {
        NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate\current\icons\app.ico");

        using var key = _root.OpenSubKey(Aumid)!;
        Assert.Equal("OverTranslate", key.GetValue("DisplayName"));
        Assert.Equal(@"C:\Apps\OverTranslate\current\icons\app.ico", key.GetValue("IconUri"));
    }

    [Fact]
    public void Write_LeavesAnUnchangedRegistrationAlone()
    {
        NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate\current\icons\app.ico");

        Assert.False(NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate\current\icons\app.ico"));
    }

    [Fact]
    public void Write_TakesOverWhenAnotherCopyRegisteredLast()
    {
        NotificationIdentityService.Write(_root, Aumid, @"D:\Portable\OverTranslate\current\icons\app.ico");

        Assert.True(NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate\current\icons\app.ico"));
        using var key = _root.OpenSubKey(Aumid)!;
        Assert.Equal(@"C:\Apps\OverTranslate\current\icons\app.ico", key.GetValue("IconUri"));
    }

    [Fact]
    public void Uninstall_RemovesThisInstallsRegistration()
    {
        NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate\current\icons\app.ico");

        NotificationIdentityService.RemoveIfPointsInto(_root, Aumid, @"C:\Apps\OverTranslate");

        Assert.Null(_root.OpenSubKey(Aumid));
    }

    [Fact]
    public void Uninstall_LeavesAPortableCopysRegistration()
    {
        NotificationIdentityService.Write(_root, Aumid, @"D:\Portable\OverTranslate\current\icons\app.ico");

        NotificationIdentityService.RemoveIfPointsInto(_root, Aumid, @"C:\Apps\OverTranslate");

        Assert.NotNull(_root.OpenSubKey(Aumid));
    }

    [Fact]
    public void Uninstall_DoesNotMistakeASiblingFolderForItsOwn()
    {
        NotificationIdentityService.Write(_root, Aumid, @"C:\Apps\OverTranslate-Portable\current\icons\app.ico");

        NotificationIdentityService.RemoveIfPointsInto(_root, Aumid, @"C:\Apps\OverTranslate");

        Assert.NotNull(_root.OpenSubKey(Aumid));
    }
}
