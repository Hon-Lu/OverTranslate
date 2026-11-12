using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Assets;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>Changing where the models are kept: the folder name, path checks, the setting.</summary>
public sealed class AssetsLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-assets-" + Guid.NewGuid().ToString("N"));

    public AssetsLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // Junctions first, so deleting the tree never follows one.
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).ToList())
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(dir);
        Directory.Delete(_root, recursive: true);
    }

    // ══════════ The folder name ══════════

    [Theory]
    [InlineData(@"D:\Apps", @"D:\Apps\OverTranslate-assets")]
    [InlineData(@"D:\Apps\", @"D:\Apps\OverTranslate-assets")]
    [InlineData(@"D:\", @"D:\OverTranslate-assets")]
    [InlineData(@"D:\Apps\OverTranslate-assets", @"D:\Apps\OverTranslate-assets")]
    [InlineData(@"D:\Apps\overtranslate-ASSETS\", @"D:\Apps\overtranslate-ASSETS")]
    [InlineData(@"D:\Apps\OverTranslate-assets-old", @"D:\Apps\OverTranslate-assets-old\OverTranslate-assets")]
    public void TheTarget_IsAlwaysAFolderNamedOverTranslateAssets(string chosen, string expected) =>
        Assert.Equal(expected, AssetsPaths.Target(chosen));

    // ══════════ Comparing ══════════

    [Theory]
    [InlineData(@"D:\Apps", @"D:\Apps", true)]
    [InlineData(@"D:\Apps\x", @"D:\Apps", true)]
    [InlineData(@"d:\apps\X\Y", @"D:\APPS\", true)]
    [InlineData(@"D:\Apps2", @"D:\Apps", false)]
    [InlineData(@"D:\Apps2\x", @"D:\Apps", false)]
    [InlineData(@"D:\App", @"D:\Apps", false)]
    [InlineData(@"D:\x", @"D:\", true)]
    [InlineData(@"E:\x", @"D:\", false)]
    public void Within_IsOnSeparatorBoundaries_AndIgnoresCase(string path, string folder, bool within) =>
        Assert.Equal(within, AssetsPaths.IsWithin(path, folder));

    [Fact]
    public void Below_DoesNotCountTheFolderItself()
    {
        Assert.False(AssetsPaths.IsBelow(@"D:\Apps", @"d:\apps\"));
        Assert.True(AssetsPaths.IsBelow(@"D:\Apps\x", @"D:\Apps"));
    }

    // ══════════ Resolving ══════════

    [Fact]
    public void AJunction_ResolvesToWhereItPoints()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "link");
        MakeJunction(link, real);

        Assert.True(AssetsPaths.Same(Path.Combine(real, "sub"), AssetsPaths.Resolve(Path.Combine(link, "sub"))));
        Assert.True(AssetsPaths.Same(real, AssetsPaths.Resolve(link)));
    }

    [Fact]
    public void APathNotYetThere_ResolvesItsNearestParent_AndKeepsTheRestAsWritten()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "link");
        MakeJunction(link, real);

        Assert.True(AssetsPaths.Same(Path.Combine(real, "a", "b", "OverTranslate-assets"),
            AssetsPaths.Resolve(Path.Combine(link, "a", "b", "OverTranslate-assets"))));
    }

    [Fact]
    public void AShortName_ResolvesToTheLongOne()
    {
        var longFolder = Directory.CreateDirectory(Path.Combine(_root, "A rather long folder name")).FullName;
        var shortFolder = ShortPath(longFolder);
        // 8.3 names can be turned off for the volume; there is then nothing to resolve.
        if (shortFolder is null || AssetsPaths.Same(shortFolder, longFolder)) return;

        Assert.True(AssetsPaths.Same(longFolder, AssetsPaths.Resolve(shortFolder)));
        Assert.True(AssetsPaths.Same(Path.Combine(longFolder, "new"), AssetsPaths.Resolve(Path.Combine(shortFolder, "new"))));
    }

    [Fact]
    public void ADriveThatIsNotThere_ComesBackAsGiven()
    {
        var missing = FreeDriveLetter() + @":\Somewhere\OverTranslate-assets";
        Assert.Equal(missing, AssetsPaths.Resolve(missing));
    }

    // ══════════ Checking ══════════

    private const string Install = @"C:\Users\u\AppData\Local\OverTranslate";
    private const string Current = @"C:\Users\u\AppData\Local\OverTranslate-assets";

    private static AssetsContext Context(long used = 6_800_000_000, string? program = null, string current = Current,
        IReadOnlyList<string>? cloud = null) =>
        new(current, Install, program, cloud ?? [], used);

    private static AssetsCheck Check(string chosen, AssetsContext? context = null, FakeMachine? machine = null) =>
        AssetsLocationCheck.Check(chosen, context ?? Context(), machine ?? new FakeMachine());

    [Fact]
    public void AnOrdinaryFolder_IsAccepted_WithTheFolderNameAdded()
    {
        var check = Check(@"D:\Apps");

        Assert.True(check.Ok);
        Assert.Equal(@"D:\Apps\OverTranslate-assets", check.Target);
        Assert.Equal("D", check.Volume!.Drive);
        Assert.False(check.HasModels);
    }

    [Fact]
    public void AFolderAlreadyHoldingModels_IsAccepted_AndSaysSo()
    {
        var machine = new FakeMachine { WithModels = { @"D:\Apps\OverTranslate-assets" } };
        Assert.True(Check(@"D:\Apps\OverTranslate-assets", machine: machine).HasModels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Apps\models")]
    [InlineData(@"\Apps")]
    [InlineData(@"D:Apps")]
    [InlineData(@"D:\Ap|ps")]
    [InlineData(@"D:\Apps:stream")]
    [InlineData(@"\\?\D:\Apps")]
    public void ANonsensePath_IsInvalid(string chosen) =>
        Assert.Equal(AssetsProblem.Invalid, Check(chosen).Problem);

    [Fact]
    public void ADriveThatIsNotThere_IsInvalid() =>
        Assert.Equal(AssetsProblem.Invalid, Check(@"Q:\Apps").Problem);

    [Theory]
    [InlineData(@"\\NAS\share")]
    [InlineData(@"\\NAS\share\OverTranslate-assets")]
    [InlineData(@"//NAS/share")]
    [InlineData(@"N:\Apps")]
    public void ANetworkLocation_IsRefused(string chosen) =>
        Assert.Equal(AssetsProblem.Network, Check(chosen).Problem);

    [Fact]
    public void ALocalPathThatResolvesOntoTheNetwork_IsRefused()
    {
        var machine = new FakeMachine { Links = { [@"D:\Share"] = @"\\NAS\share" } };
        Assert.Equal(AssetsProblem.Network, Check(@"D:\Share\x", machine: machine).Problem);
    }

    [Fact]
    public void AFolderInsideTheCurrentLocation_IsRefused_ButTheLocationItselfIsNot()
    {
        Assert.Equal(AssetsProblem.InsideCurrent, Check(Current + @"\models").Problem);
        Assert.Equal(AssetsProblem.InsideCurrent, Check(Current + @"\models\deep\er").Problem);
        // Picking where it already is: allowed, and the dialog keeps 繼續 off instead.
        Assert.True(Check(Current).Ok);
    }

    [Theory]
    [InlineData(Install)]
    [InlineData(Install + @"\current")]
    [InlineData(Install + @"\current\models\a\b")]
    [InlineData(@"c:\users\U\appdata\local\overtranslate\packages")]
    public void TheInstallFolder_AtAnyDepth_IsRefused(string chosen) =>
        Assert.Equal(AssetsProblem.InsideInstall, Check(chosen).Problem);

    [Fact]
    public void AFolderBesideTheInstallFolder_IsNotInsideIt() =>
        Assert.True(Check(Install + "2").Ok);

    [Fact]
    public void AJunctionIntoTheInstallFolder_IsRefused()
    {
        var machine = new FakeMachine { Links = { [@"D:\Sneaky"] = Install + @"\current" } };
        Assert.Equal(AssetsProblem.InsideInstall, Check(@"D:\Sneaky", machine: machine).Problem);
    }

    [Fact]
    public void AJunctionIntoTheCurrentLocation_IsRefused()
    {
        var machine = new FakeMachine { Links = { [@"D:\Sneaky"] = Current + @"\models" } };
        Assert.Equal(AssetsProblem.InsideCurrent, Check(@"D:\Sneaky", machine: machine).Problem);
    }

    [Theory]
    [InlineData(@"D:\Tools\OverTranslate", true)]
    [InlineData(@"D:\Tools\OverTranslate\", true)]
    [InlineData(@"D:\Tools\OverTranslate\OverTranslate-assets", true)]
    [InlineData(@"d:\tools\overtranslate\overtranslate-assets\", true)]
    [InlineData(@"D:\Tools\OverTranslate\current", false)]
    [InlineData(@"D:\Tools\OverTranslate\current\models", false)]
    [InlineData(@"D:\Tools\OverTranslate\x\OverTranslate-assets", false)]
    [InlineData(@"D:\Tools\OverTranslate\OverTranslate-assets\x", false)]
    [InlineData(@"D:\Tools\OverTranslate2", true)]
    [InlineData(@"D:\Tools", true)]
    public void ThePortableBuild_AllowsOnlyItsOwnAssetsFolder_InsideItsFolder(string chosen, bool ok)
    {
        var check = Check(chosen, Context(program: @"D:\Tools\OverTranslate"));
        Assert.Equal(ok ? AssetsProblem.None : AssetsProblem.InsideProgram, check.Problem);
    }

    [Fact]
    public void TheInstalledBuild_HasNoProgramFolderRule() =>
        Assert.True(Check(@"D:\Tools\OverTranslate\current").Ok);

    [Fact]
    public void AOneDriveFolder_IsRefused()
    {
        var context = Context(cloud: [@"C:\Users\u\OneDrive"]);
        Assert.Equal(AssetsProblem.Cloud, Check(@"C:\Users\u\OneDrive\Models", context).Problem);
        Assert.Equal(AssetsProblem.Cloud, Check(@"c:\users\u\onedrive", context).Problem);
        Assert.True(Check(@"C:\Users\u\OneDrive2", context).Ok);
    }

    [Fact]
    public void AFolderTheCloudSyncs_IsRefused()
    {
        var machine = new FakeMachine { Synced = { @"D:\Dropbox" } };
        Assert.Equal(AssetsProblem.Cloud, Check(@"D:\Dropbox\x", machine: machine).Problem);
    }

    [Theory]
    [InlineData("FAT32", false)]
    [InlineData("FAT", false)]
    [InlineData("exFAT", true)]
    [InlineData("NTFS", true)]
    [InlineData("ReFS", true)]
    public void FatDrives_AreRefused_ExFatIsNot(string format, bool ok)
    {
        var machine = new FakeMachine { Formats = { ["E"] = format } };
        var check = Check(@"E:\USB", machine: machine);
        Assert.Equal(ok ? AssetsProblem.None : AssetsProblem.Fat, check.Problem);
        if (!ok) Assert.Equal(format, check.Volume!.Format);
    }

    [Fact]
    public void AFolderThatCannotBeWritten_IsRefused()
    {
        var machine = new FakeMachine { ReadOnly = { "G" } };
        Assert.Equal(AssetsProblem.NotWritable, Check(@"G:\Locked", machine: machine).Problem);
    }

    [Fact]
    public void TooLittleRoom_IsRefused_WithWhatIsNeeded()
    {
        var machine = new FakeMachine { Free = { ["F"] = 2_100_000_000 } };
        var check = Check(@"F:\Small", machine: machine);

        Assert.Equal(AssetsProblem.NoSpace, check.Problem);
        Assert.Equal(6_800_000_000, check.NeededBytes);
        Assert.Equal(2_100_000_000, check.Volume!.FreeBytes);
    }

    [Fact]
    public void WithNothingToMove_RoomIsNotChecked()
    {
        var machine = new FakeMachine { Free = { ["F"] = 1000 } };
        Assert.True(Check(@"F:\Small", Context(used: 0), machine).Ok);
    }

    // ══════════ The setting ══════════

    private string SettingsFile(string location, string pending)
    {
        var file = Path.Combine(_root, "appsettings.json");
        var settings = new AppSettings { AssetsLocation = location, AssetsPendingDelete = pending, TargetLanguage = "ja" };
        File.WriteAllText(file, SettingsService.Serialize(settings));
        return file;
    }

    [Fact]
    public void TheLocation_IsReadBackStraightFromTheSettingsFile()
    {
        var file = SettingsFile(@"E:\Models\OverTranslate-assets", @"C:\old\OverTranslate-assets");

        var read = SavedAssetsLocation.Read(file);
        Assert.Equal(@"E:\Models\OverTranslate-assets", read!.Path);
        Assert.Equal(@"C:\old\OverTranslate-assets", read.PendingDelete);

        // And through the settings, beside everything else in the file.
        var parsed = SettingsService.Parse(File.ReadAllText(file));
        Assert.Equal(@"E:\Models\OverTranslate-assets", parsed.AssetsLocation);
        Assert.Equal(@"C:\old\OverTranslate-assets", parsed.AssetsPendingDelete);
        Assert.Equal("ja", parsed.TargetLanguage);
    }

    [Fact]
    public void TheDefault_IsAnEmptySetting()
    {
        var read = SavedAssetsLocation.Read(SettingsFile("", ""));
        Assert.Null(read!.Path);
        Assert.Null(read.PendingDelete);
        Assert.Equal(Current, AppDataPaths.ResolveAssetsRoot("", Current));
        Assert.Equal(Current, AppDataPaths.ResolveAssetsRoot(null, Current));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"AssetsLocation\": \"\"}")]
    [InlineData("{\"AssetsLocation\": \"relative\\\\path\"}")]
    [InlineData("{\"AssetsLocation\": 3}")]
    public void ASettingThatIsNotAFullPath_MeansTheDefault(string text)
    {
        var file = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(file, text);

        Assert.Null(SavedAssetsLocation.Read(file)?.Path);
        Assert.Null(SavedAssetsLocation.Read(Path.Combine(_root, "missing.json")));
    }

    [Fact]
    public void AChosenLocation_ComesBeforeTheDefault()
    {
        Assert.Equal(@"E:\M\OverTranslate-assets", AppDataPaths.ResolveAssetsRoot(@"E:\M\OverTranslate-assets", Current));
        Assert.Equal(Current, AppDataPaths.ResolveAssetsRoot(@"relative\path", Current));
    }

    // ══════════ Helpers ══════════

    /// <summary>
    /// Drives C D E F G; N is a network drive. Paths resolve through <see cref="Links"/>, then as written.
    /// </summary>
    private sealed class FakeMachine : IAssetsMachine
    {
        internal Dictionary<string, string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> Formats { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, long> Free { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ReadOnly { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Synced { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> WithModels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Resolve(string path)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var (link, target) in Links)
                if (AssetsPaths.IsWithin(full, link))
                    return target + full[Path.TrimEndingDirectorySeparator(Path.GetFullPath(link)).Length..];
            return full;
        }

        public AssetsVolume? Volume(string path)
        {
            var drive = path[..1].ToUpperInvariant();
            if (!"CDEFGN".Contains(drive, StringComparison.Ordinal)) return null;
            return new AssetsVolume(drive, drive == "N",
                Formats.GetValueOrDefault(drive, "NTFS"), Free.GetValueOrDefault(drive, 800_000_000_000));
        }

        public bool IsCloudSynced(string path) => Synced.Any(root => AssetsPaths.IsWithin(path, root));

        public bool CanWrite(string path) => !ReadOnly.Contains(path[..1]);

        public bool HasModels(string root) => WithModels.Contains(Path.TrimEndingDirectorySeparator(root));
    }

    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");
    }

    private static string FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "QRSTUVWXYZ".First(letter => !used.Contains(letter)).ToString();
    }

    private static string? ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        return GetShortPathNameW(path, buffer, buffer.Capacity) == 0 ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int length);
}
