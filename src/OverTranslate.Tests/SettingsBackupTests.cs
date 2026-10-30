using System.IO;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The copy of the settings file kept before an update that changes what it means: when it is made,
/// that it is the file as it was, and that it is made before anything is saved over it.
/// </summary>
public class SettingsBackupTests : IDisposable
{
    private const string OldFile = """
        {
          "Theme": "Light",
          "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
            "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "口語：" } } ] }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ot-settings-backup-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "roaming", "appsettings.json");
    private string LegacyPath => Path.Combine(_dir, "install", "appsettings.json");

    public SettingsBackupTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string BackupPath(string version) => SettingsBackup.PathFor(SettingsPath, version);

    private static string WithLastRun(string version) =>
        OldFile.Replace("\"Theme\": \"Light\",", $"\"Theme\": \"Light\", \"LastRunVersion\": \"{version}\",");

    /// <summary>Loads as a start of <paramref name="version"/> would, saving to the real path.</summary>
    private Models.AppSettings Start(string version, Action? beforeSave = null) =>
        SettingsService.Load(SettingsPath, LegacyPath, version, settings =>
        {
            beforeSave?.Invoke();
            File.WriteAllText(SettingsPath, SettingsService.Serialize(settings));
        });

    /// <summary>
    /// A file from 2.9.0 or earlier has no version in it, and is copied byte for byte — also by a dev
    /// build whose csproj still says 2.9.0.
    /// </summary>
    [Theory]
    [InlineData("2.10.0")]
    [InlineData("2.9.0")]
    public void AFileWithoutAVersion_IsCopiedAsItWas(string running)
    {
        File.WriteAllText(SettingsPath, OldFile);
        var before = File.ReadAllBytes(SettingsPath);

        Start(running);

        Assert.Equal(before, File.ReadAllBytes(BackupPath(running)));
    }

    [Theory]
    [InlineData("2.9.0", true)]
    [InlineData("2.10.0-beta.1", true)]
    [InlineData("2.10.0", false)]
    [InlineData("2.10.1", false)]
    public void TheLastVersion_DecidesTheCopy(string lastRun, bool copied)
    {
        File.WriteAllText(SettingsPath, WithLastRun(lastRun));

        Start("2.10.1");

        Assert.Equal(copied, File.Exists(BackupPath("2.10.1")));
    }

    /// <summary>
    /// The same version as last time is not a first start after an update: no copy, even one that
    /// would otherwise be due and is missing, and nothing saved.
    /// </summary>
    [Fact]
    public void TheSameVersionAsLastTime_CopiesNothing()
    {
        File.WriteAllText(SettingsPath, WithLastRun("2.9.0"));
        var saves = 0;

        Start("2.9.0", () => saves++);

        Assert.False(File.Exists(BackupPath("2.9.0")));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void AFreshInstall_HasNothingToCopy()
    {
        var settings = Start("2.10.0");

        Assert.False(File.Exists(BackupPath("2.10.0")));
        Assert.Equal("2.10.0", settings.LastRunVersion);
        Assert.Equal(["appsettings.json"], Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void ACopyAlreadyThere_IsLeftAlone()
    {
        File.WriteAllText(SettingsPath, OldFile);
        File.WriteAllText(BackupPath("2.10.0"), "first start");

        Start("2.10.0");

        Assert.Equal("first start", File.ReadAllText(BackupPath("2.10.0")));
    }

    /// <summary>
    /// The copy is there before the first save, also when the file is read from where 1.7.0 kept it
    /// and saved to the new place straight away; it is that old file that is copied.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCopy_ComesBeforeAnySave(bool legacy)
    {
        File.WriteAllText(legacy ? LegacyPath : SettingsPath, OldFile);
        var saves = 0;

        Start("2.10.0", () =>
        {
            saves++;
            Assert.Equal(OldFile, File.ReadAllText(BackupPath("2.10.0")));
        });

        Assert.Equal(1, saves);
    }

    /// <summary>The running version is written once, and a second start with it saves nothing.</summary>
    [Fact]
    public void TheRunningVersion_IsWrittenOnce()
    {
        File.WriteAllText(SettingsPath, WithLastRun("2.9.0"));

        Assert.Equal("2.10.0", Start("2.10.0").LastRunVersion);
        Assert.Equal("2.10.0", SettingsService.Parse(File.ReadAllText(SettingsPath)).LastRunVersion);

        var saves = 0;
        Start("2.10.0", () => saves++);
        Assert.Equal(0, saves);
    }

    /// <summary>The version stored is the build's own, without the commit appended after a +.</summary>
    [Fact]
    public void TheCurrentVersion_HasNoCommitSuffix()
    {
        Assert.DoesNotContain("+", SettingsBackup.CurrentVersion);
        Assert.True(Velopack.SemanticVersion.TryParse(SettingsBackup.CurrentVersion, out _));
    }
}
