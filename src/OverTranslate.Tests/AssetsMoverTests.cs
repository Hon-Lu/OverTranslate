using System.IO;
using OverTranslate.Services;
using OverTranslate.Services.Assets;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>Moving the models: all or nothing, reusing what is there, and emptying the old place.</summary>
public sealed class AssetsMoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-move-" + Guid.NewGuid().ToString("N"));
    private string From => Path.Combine(_root, "from", "OverTranslate-assets");
    private string To => Path.Combine(_root, "to", "OverTranslate-assets");

    public AssetsMoverTests()
    {
        Write(From, @"models\manga-vertical\v3\detector.onnx", "detector");
        Write(From, @"models\manga-vertical\v3\verified.json", "{}");
        Write(From, @"models\local-ai\hy\model.gguf", new string('g', 100_000));
        Write(From, @"runtimes\directml\1.15\DirectML.dll", "dll");
        Write(From, @"user-notes.txt", "not ours");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string[] Listing(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    [Fact]
    public async Task EverythingTheAppMade_IsCopied_AndNothingElse()
    {
        var reports = new List<(long Done, long Total)>();
        await AssetsMover.CopyAsync(From, To, (done, total) => reports.Add((done, total)), CancellationToken.None);

        Assert.Equal(
            new[] { @"models\local-ai\hy\model.gguf", @"models\manga-vertical\v3\detector.onnx",
             @"models\manga-vertical\v3\verified.json", @"runtimes\directml\1.15\DirectML.dll" },
            Listing(To));
        Assert.Equal("detector", File.ReadAllText(Path.Combine(To, @"models\manga-vertical\v3\detector.onnx")));
        Assert.False(Directory.Exists(Path.Combine(To, AssetsMover.StagingName)));
        // The old place is emptied only after the restart.
        Assert.Equal(5, Listing(From).Length);

        var total = AssetsUsage.UsedBytes(From);
        Assert.All(reports, report => Assert.Equal(total, report.Total));
        Assert.Equal(total, reports[^1].Done);
    }

    [Fact]
    public async Task AFileAlreadyThere_AtTheSameSize_IsKeptNotCopied()
    {
        var kept = Write(To, @"models\manga-vertical\v3\detector.onnx", "DETECTOR");
        var stale = Write(To, @"runtimes\directml\1.15\DirectML.dll", "an older, longer dll");
        var copied = new List<string>();

        await AssetsMover.CopyAsync(From, To, null, CancellationToken.None, async (from, to, done, token) =>
        {
            copied.Add(Path.GetFileName(from));
            File.Copy(from, to);
            await Task.CompletedTask;
        });

        Assert.Equal("DETECTOR", File.ReadAllText(kept));
        Assert.Equal("dll", File.ReadAllText(stale));
        Assert.DoesNotContain("detector.onnx", copied);
        Assert.Contains("DirectML.dll", copied);
    }

    [Fact]
    public async Task Cancelled_LeavesNothingAtTheNewPlace()
    {
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AssetsMover.CopyAsync(From, To, null, cancel.Token, async (from, to, done, token) =>
            {
                File.Copy(from, to);
                if (++calls == 2) cancel.Cancel();
                await Task.CompletedTask;
            }));

        Assert.False(Directory.Exists(To));
        Assert.Equal(5, Listing(From).Length);
    }

    [Fact]
    public async Task AFailedCopy_LeavesTheNewPlaceAsItWas_TheirFilesIncluded()
    {
        var theirs = Write(To, "theirs.txt", "keep me");
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() =>
            AssetsMover.CopyAsync(From, To, null, CancellationToken.None, async (from, to, done, token) =>
            {
                if (++calls == 3) throw new IOException("disk full");
                File.Copy(from, to);
                await Task.CompletedTask;
            }));

        Assert.Equal(new[] { "theirs.txt" }, Listing(To));
        Assert.Equal("keep me", File.ReadAllText(theirs));
        Assert.False(Directory.Exists(Path.Combine(To, "models")));
        Assert.Equal(5, Listing(From).Length);
    }

    [Fact]
    public async Task AStagingFolderLeftBehind_IsClearedFirst()
    {
        Write(To, Path.Combine(AssetsMover.StagingName, "leftover.bin"), "x");
        await AssetsMover.CopyAsync(From, To, null, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(To, AssetsMover.StagingName)));
    }

    [Fact]
    public async Task NothingToMove_StillSucceeds()
    {
        var empty = Path.Combine(_root, "empty", "OverTranslate-assets");
        await AssetsMover.CopyAsync(empty, To, null, CancellationToken.None);
        Assert.Empty(Listing(To));
    }

    [Fact]
    public void TheOldPlace_LosesTheAppsFolders_AndKeepsTheirs()
    {
        Assert.Equal(0, AssetsMover.RemoveAppFolders(From));
        Assert.Equal(new[] { "user-notes.txt" }, Listing(From));
    }

    [Fact]
    public void TheOldPlace_GoesAltogether_WhenNothingElseIsInIt()
    {
        File.Delete(Path.Combine(From, "user-notes.txt"));
        Assert.Equal(0, AssetsMover.RemoveAppFolders(From));
        Assert.False(Directory.Exists(From));
        Assert.True(Directory.Exists(Path.GetDirectoryName(From)));
    }

    [Fact]
    public void AFolderByAnotherName_IsNeverEmptied()
    {
        var other = Path.Combine(_root, "from");
        Assert.Equal(-1, AssetsMover.RemoveAppFolders(other));
        Assert.Equal(5, Listing(From).Length);
    }

    [Fact]
    public void Usage_CountsOnlyTheAppsFolders()
    {
        Assert.Equal("detector".Length + 2 + 100_000 + 3, AssetsUsage.UsedBytes(From));
        Assert.True(AssetsUsage.HasFiles(From));
        Assert.False(AssetsUsage.HasFiles(Path.Combine(_root, "nowhere")));
    }

    // ══════════ Uninstalling ══════════

    [Fact]
    public void Uninstalling_TakesTheDefaultWhole_AndOnlyTheAppsFoldersFromTheChosenPlace()
    {
        var defaultRoot = Path.Combine(_root, "local", "OverTranslate-assets");
        Write(defaultRoot, @"models\x.bin", "x");
        Write(defaultRoot, @"stray.txt", "x");
        var pointer = Path.Combine(_root, "assets-location.json");
        AssetsPointer.Write(pointer, new AssetsPointer(From), defaultRoot);

        UninstallCleanup.RemoveAssets(defaultRoot, pointer);

        Assert.False(Directory.Exists(defaultRoot));
        Assert.Equal(new[] { "user-notes.txt" }, Listing(From));
    }

    [Fact]
    public void Uninstalling_AlsoEmptiesAnOldPlaceStillWaitingToBe()
    {
        var defaultRoot = Path.Combine(_root, "local", "OverTranslate-assets");
        var pointer = Path.Combine(_root, "assets-location.json");
        AssetsPointer.Write(pointer, new AssetsPointer(To, From), defaultRoot);
        Write(To, @"models\y.bin", "y");

        UninstallCleanup.RemoveAssets(defaultRoot, pointer);

        Assert.Equal(new[] { "user-notes.txt" }, Listing(From));
        Assert.False(Directory.Exists(To));
    }

    [Fact]
    public void Uninstalling_WithAPointerNamingAnotherFolder_LeavesIt()
    {
        var defaultRoot = Path.Combine(_root, "local", "OverTranslate-assets");
        var pointer = Path.Combine(_root, "assets-location.json");
        var wrong = Path.Combine(_root, "from");
        File.WriteAllText(pointer, $"{{\"path\": \"{wrong.Replace(@"\", @"\\")}\"}}");

        UninstallCleanup.RemoveAssets(defaultRoot, pointer);

        Assert.Equal(5, Listing(From).Length);
    }
}
