using System.IO;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// #228: the glossaries live in glossaries.json beside the settings, which keep only the one in use.
public sealed class GlossaryStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 15, 30, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ot-glossary-" + Guid.NewGuid().ToString("N"));

    public GlossaryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string GlossaryPath => Path.Combine(_dir, "glossaries.json");

    private string SettingsPath => Path.Combine(_dir, "appsettings.json");

    private GlossaryStore Store() => new(GlossaryPath, () => Now);

    private static List<GlossaryList> Work() =>
    [
        new()
        {
            Id = "g1", Name = "星環旅記", TargetLanguage = "ZH-HANT",
            Entries = [new() { Source = "アリサ", Target = "艾莉莎" }, new() { Source = "ミラの森", Target = "蜜菈森林" }],
        },
    ];

    [Fact]
    public void SavedLists_ReadBack()
    {
        Assert.True(Store().SaveIfChanged(Work()));

        var read = Store().Read();

        var list = Assert.Single(read);
        Assert.Equal(("g1", "星環旅記", "ZH-HANT"), (list.Id, list.Name, list.TargetLanguage));
        Assert.Equal([("アリサ", "艾莉莎"), ("ミラの森", "蜜菈森林")], list.Entries.Select(entry => (entry.Source, entry.Target)));
        // Written as itself, not as \u escapes: a file the user can open.
        Assert.Contains("星環旅記", File.ReadAllText(GlossaryPath));
    }

    [Fact]
    public void NoFile_ReadsAsNone_AndWritesNothing()
    {
        var store = Store();

        Assert.Empty(store.Read());
        Assert.False(store.SaveIfChanged([]));
        Assert.False(File.Exists(GlossaryPath));
    }

    [Theory]
    [InlineData("{\"Lists\":[{\"Id\":\"g1\",\"Entries\":[")]
    [InlineData("not json")]
    [InlineData("{\"Lists\":\"アリサ=艾莉莎\"}")]
    [InlineData("null")]
    public void ABadFile_ReadsAsNone_AndIsKeptUnderAnotherName(string content)
    {
        File.WriteAllText(GlossaryPath, content);
        var store = Store();

        Assert.Empty(store.Read());

        var kept = GlossaryPath + ".bad-20261121-153000";
        Assert.False(File.Exists(GlossaryPath));
        Assert.Equal(content, File.ReadAllText(kept));

        // The next save writes a new file and leaves the kept one as it was.
        Assert.True(store.SaveIfChanged(Work()));
        Assert.Single(Store().Read());
        Assert.Equal(content, File.ReadAllText(kept));
    }

    [Fact]
    public void ASecondBadFile_DoesNotOverwriteTheFirstKept()
    {
        File.WriteAllText(GlossaryPath, "first");
        Store().Read();
        File.WriteAllText(GlossaryPath, "second");
        Store().Read();

        Assert.Equal("first", File.ReadAllText(GlossaryPath + ".bad-20261121-153000"));
        Assert.Equal("second", File.ReadAllText(GlossaryPath + ".bad-20261121-153000-2"));
    }

    [Fact]
    public void AFileThatCannotBeOpened_IsNeverWrittenOver()
    {
        File.WriteAllText(GlossaryPath, GlossaryStore.Serialize(Work()));
        var store = Store();
        using (new FileStream(GlossaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(store.Read());
            Assert.False(store.SaveIfChanged([new() { Id = "x", Name = "新" }]));
        }

        // Once it can be moved, it is kept aside and the save goes through.
        Assert.True(store.SaveIfChanged([new() { Id = "x", Name = "新" }]));
        Assert.Equal("g1", Assert.Single(new GlossaryStore(GlossaryPath + ".bad-20261121-153000").Read()).Id);
        Assert.Equal("x", Assert.Single(Store().Read()).Id);
    }

    [Fact]
    public void AWriteThatFails_LeavesThePreviousFileWhole()
    {
        Assert.True(Store().SaveIfChanged(Work()));
        var before = File.ReadAllText(GlossaryPath);
        // The temporary file's name taken by a folder: the write fails before anything is renamed.
        Directory.CreateDirectory(GlossaryPath + ".tmp");

        var store = Store();
        store.Read();
        Assert.False(store.SaveIfChanged([]));

        Assert.Equal(before, File.ReadAllText(GlossaryPath));
    }

    [Fact]
    public void AWrite_GoesThroughATemporaryFile_AndLeavesNoneBehind()
    {
        File.WriteAllText(GlossaryPath + ".tmp", "left from a write cut short");

        Assert.True(Store().SaveIfChanged(Work()));

        Assert.False(File.Exists(GlossaryPath + ".tmp"));
        Assert.Single(Store().Read());
    }

    [Fact]
    public void TheSameOrAnEqualList_IsNotWrittenAgain()
    {
        Store().SaveIfChanged(Work());
        var store = Store();
        var read = store.Read();
        var written = File.GetLastWriteTimeUtc(GlossaryPath);
        File.SetLastWriteTimeUtc(GlossaryPath, written.AddHours(-1));

        Assert.False(store.SaveIfChanged(read));
        Assert.False(store.SaveIfChanged(new GlossarySettings { Lists = read }.Clone().Lists));
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(GlossaryPath));

        var edited = new GlossarySettings { Lists = read }.Clone().Lists;
        edited[0].Entries.Add(new() { Source = "リオ", Target = "莉歐" });
        Assert.True(store.SaveIfChanged(edited));
    }

    /// <summary>
    /// Loading the settings and saving another value afterwards — what every settings change does —
    /// leaves glossaries.json alone, and so does choosing another glossary.
    /// </summary>
    [Fact]
    public void SavingOtherSettings_DoesNotRewriteTheGlossaries()
    {
        Store().SaveIfChanged(Work());
        File.WriteAllText(SettingsPath, "{\"Theme\":\"Light\",\"Glossary\":{\"ActiveId\":\"g1\"},\"LastRunVersion\":\"9.9.9\"}");
        var store = Store();
        var lists = store.Read();
        var written = File.GetLastWriteTimeUtc(GlossaryPath).AddHours(-1);
        File.SetLastWriteTimeUtc(GlossaryPath, written);

        var settings = SettingsService.Load(SettingsPath, Path.Combine(_dir, "none.json"), "9.9.9",
            saved => store.SaveIfChanged(saved.Glossary.Lists), lists);
        settings.Theme = "Dark";
        Assert.False(store.SaveIfChanged(settings.Glossary.Lists));
        var chosen = settings.Glossary.Clone();
        chosen.ActiveId = "";
        Assert.False(store.SaveIfChanged(chosen.Lists));

        Assert.Equal("g1", settings.Glossary.Active?.Id);
        Assert.Equal(written, File.GetLastWriteTimeUtc(GlossaryPath));
    }

    [Fact]
    public void TheListsAreCleanedAsTheyAreRead()
    {
        File.WriteAllText(GlossaryPath, """
            {"Lists":[
              {"Id":"a","Name":"一","TargetLanguage":"zh-hant","Entries":[{"Source":" アリサ ","Target":"艾莉莎"},{"Source":"","Target":"x"},{"Source":"アリサ","Target":"亞莉莎"}]},
              {"Id":"a","Name":"二","TargetLanguage":"XX","Entries":[]},
              null
            ]}
            """);

        var lists = Store().Read();

        Assert.Equal(2, lists.Count);
        Assert.NotEqual(lists[0].Id, lists[1].Id);
        Assert.Equal("ZH-HANT", lists[0].TargetLanguage);
        Assert.Equal(LanguageData.DefaultTargetLanguage, lists[1].TargetLanguage);
        Assert.Equal([("アリサ", "亞莉莎")], lists[0].Entries.Select(entry => (entry.Source, entry.Target)));
    }

    [Fact]
    public void TheLimitsHold_ForAFileOverThem()
    {
        var over = Enumerable.Range(0, 25).Select(i => new GlossaryList
        {
            Id = $"g{i}",
            Name = new string('名', 50),
            Entries = [.. Enumerable.Range(0, 1100).Select(k => new GlossaryEntry { Source = $"s{k}", Target = "t" })],
        }).ToList();
        File.WriteAllText(GlossaryPath, GlossaryStore.Serialize(over));

        var lists = Store().Read();

        Assert.Equal(GlossarySettings.MaxLists, lists.Count);
        Assert.Equal(GlossarySettings.MaxNameLength, lists[0].Name.Length);
        Assert.Equal(GlossarySettings.MaxEntries, lists[0].Entries.Count);
    }

    // ── The settings file ───────────────────────────────────────────────────

    [Fact]
    public void TheSettingsKeepOnlyTheOneInUse()
    {
        var settings = new AppSettings();
        settings.Glossary.Lists.AddRange(Work());
        settings.Glossary.ActiveId = "g1";

        var json = SettingsService.Serialize(settings);

        Assert.Contains("\"ActiveId\": \"g1\"", json);
        Assert.DoesNotContain("Lists", json);
        Assert.DoesNotContain("アリサ", json);
        Assert.DoesNotContain("\"Active\"", json);
    }

    [Fact]
    public void TheOneInUse_ReadsBackWithTheLists()
    {
        var settings = new AppSettings();
        settings.Glossary.ActiveId = "g1";
        File.WriteAllText(SettingsPath, SettingsService.Serialize(settings));

        var read = SettingsService.Load(SettingsPath, Path.Combine(_dir, "none.json"), settings.LastRunVersion, _ => { }, Work());

        Assert.Equal("艾莉莎", read.Glossary.Active!.Entries[0].Target);
    }

    [Fact]
    public void TheOneInUse_NamingNoList_IsNone()
    {
        File.WriteAllText(SettingsPath, "{\"Glossary\":{\"ActiveId\":\"gone\"},\"LastRunVersion\":\"1.0.0\"}");

        var read = SettingsService.Load(SettingsPath, Path.Combine(_dir, "none.json"), "1.0.0", _ => { }, Work());

        Assert.Equal("", read.Glossary.ActiveId);
        Assert.Single(read.Glossary.Lists);
    }

    /// <summary>
    /// The Glossary section a build between #228 and this store wrote, lists and all, is read past
    /// and not written back: no build with it was released, so nothing in it is carried over.
    /// </summary>
    [Fact]
    public void TheListsABuildWroteIntoTheSettings_AreReadPastAndDropped()
    {
        var settings = SettingsService.Parse("""
            {"Theme":"Light","Glossary":{"Lists":[{"Id":"g1","Name":"舊","TargetLanguage":"ZH-HANT",
              "Entries":[{"Source":"アリサ","Target":"艾莉莎"}]}],"ActiveId":"g1",
              "Active":{"Id":"g1","Name":"舊","Entries":[]}}}
            """);

        Assert.Equal("Light", settings.Theme);
        Assert.Empty(settings.Glossary.Lists);
        var json = SettingsService.Serialize(settings);
        Assert.DoesNotContain("Lists", json);
        Assert.DoesNotContain("アリサ", json);
    }

    [Fact]
    public void TheSummary_GivesCountsOnly()
    {
        File.WriteAllText(SettingsPath, "{\"Glossary\":{\"ActiveId\":\"g1\"},\"LastRunVersion\":\"1.0.0\"}");
        var lists = Work();
        lists.Add(new GlossaryList { Id = "g2", Name = "空", Entries = [] });

        var settings = SettingsService.Load(SettingsPath, Path.Combine(_dir, "none.json"), "1.0.0", _ => { }, lists);

        Assert.Equal("lists=2 entries=[2,0] active=2", SettingsService.Describe(settings.Glossary));
        Assert.Equal("lists=0 entries=[] active=none", SettingsService.Describe(new GlossarySettings()));
    }
}
