using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OverTranslate.Services;
using OverTranslate.Views.Shell;
using Velopack;
using Xunit;

namespace OverTranslate.Tests;

public class ReleaseNotesTests
{
    private const string Body = """
        ## 🌐 Select your language

        <details>
        <summary><b>English</b></summary>

        # OverTranslate 2.10.0

        ## ◆ Real-time Translation
        - Added `Appearance`: adjust the text
        - Added modes
          - Standard: one at a time

        ## ◇ Other improvements
        - Fixed **errors** in [apps](https://example.com/x)

        ## ↓ Install / Update
        - **New users**: download

        </details>

        <details>
        <summary><b>繁體中文</b></summary>

        # OverTranslate 2.10.0

        > 感謝 [isdzjfs](https://github.com/isdzjfs) 的 fork

        ---

        ## ◆ 即時翻譯
        - 新增 `顯示外觀` 功能

        ## ↓ 安裝 / 更新
        - **新使用者**：可下載

        </details>
        """;

    [Fact]
    public void ReadsTheSectionsOfTheInterfaceLanguage()
    {
        var sections = ReleaseNotesParser.Parse(Body, LocalizationService.English);

        Assert.Equal(["Real-time Translation", "Other improvements"], sections.Select(s => s.Title));
        Assert.Equal("◆", sections[0].Glyph);
        Assert.Equal("◇", sections[1].Glyph);
    }

    [Fact]
    public void LeavesOutTheInstallSection()
    {
        var titles = ReleaseNotesParser.Parse(Body, LocalizationService.TraditionalChinese).Select(s => s.Title);

        Assert.DoesNotContain(titles, t => t.Contains("安裝"));
    }

    [Theory]
    [InlineData(LocalizationService.Russian)]
    [InlineData("xx")]
    public void ALanguageWithNoNotesGetsEnglish(string language)
    {
        var sections = ReleaseNotesParser.Parse(Body, language);

        Assert.Equal("Real-time Translation", sections[0].Title);
    }

    [Fact]
    public void ALanguageMissingFromThisReleaseGetsEnglish()
    {
        var sections = ReleaseNotesParser.Parse(Body, LocalizationService.Korean);

        Assert.Equal("Real-time Translation", sections[0].Title);
    }

    [Fact]
    public void ABodyWithNoLanguageBlocksIsReadWhole()
    {
        const string body = """
            # OverTranslate 2.11.0

            Thanks for testing.

            ## ◆ Fixes
            - Fixed a crash

            ## ↓ Install
            - Download it
            """;

        var sections = ReleaseNotesParser.Parse(body, LocalizationService.Japanese);

        Assert.Equal(["", "Fixes"], sections.Select(s => s.Title));
        Assert.False(sections[0].Lines[0].Bullet);
        Assert.True(sections[1].Lines[0].Bullet);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("\n---\n")]
    [InlineData("<details>\n<summary><b>Deutsch</b></summary>\n\n## ◆ Neu\n- Etwas\n\n</details>")]
    [InlineData("<details>\n<summary><b>English</b></summary>\n\n## ↓ Install\n- Download\n\n</details>")]
    public void ABodyWithNothingToReadHasNoSections(string? body)
    {
        Assert.Empty(ReleaseNotesParser.Parse(body!, LocalizationService.English));
    }

    [Theory]
    [InlineData(true, "", Body)]    // one readable release is enough
    [InlineData(false, "", "<details>\n<summary><b>Deutsch</b></summary>\n\n## ◆ Neu\n</details>")]
    public void AFeedHasSomethingToReadWhenAnyReleaseDoes(bool expected, string first, string second)
    {
        var feed = new ReleaseNotesFeed(
            [new ReleaseEntry("2.10.0", null, first, ""), new ReleaseEntry("2.9.0", null, second, "")],
            2,
            false);

        Assert.Equal(expected, feed.HasAnythingToRead(LocalizationService.English));
    }

    [Fact]
    public void ALanguageWrittenTwiceKeepsTheFirst()
    {
        const string body = """
            <details>
            <summary><b>English</b></summary>

            ## ◆ First
            - a

            </details>

            <details>
            <summary><b>English</b></summary>

            ## ◆ Second
            - b

            </details>
            """;

        var sections = ReleaseNotesParser.Parse(body, LocalizationService.English);

        Assert.Equal(["First"], sections.Select(s => s.Title));
    }

    [Fact]
    public void ALostCloseDoesNotSwallowTheNextLanguage()
    {
        const string body = """
            <details>
            <summary><b>English</b></summary>

            ## ◆ Fixes
            - a

            <details>
            <summary><b>繁體中文</b></summary>

            ## ◆ 修正
            - b
            """;

        var english = ReleaseNotesParser.Parse(body, LocalizationService.English);
        var chinese = ReleaseNotesParser.Parse(body, LocalizationService.TraditionalChinese);

        Assert.Equal(["Fixes"], english.Select(s => s.Title));
        Assert.Single(english[0].Lines);
        Assert.Equal(["修正"], chinese.Select(s => s.Title));
    }

    [Fact]
    public void ALabelWithoutBoldIsStillALabel()
    {
        const string body = "<details>\n<summary>English</summary>\n\n## ◆ Fixes\n- a\n\n</details>";

        Assert.Equal(["Fixes"], ReleaseNotesParser.Parse(body, LocalizationService.English).Select(s => s.Title));
    }

    [Fact]
    public void ATabIndentedItemIsNested()
    {
        var sections = ReleaseNotesParser.Parse("## ◆ Fixes\n- a\n\t- b", LocalizationService.English);

        Assert.Equal([0, 1], sections[0].Lines.Select(l => l.Level));
    }

    // Reads the real template rather than a copy of it: changing it without the parser would raise no
    // error anywhere, and every release from then on would quietly lose its formatting.
    [Theory]
    [InlineData(".claude")]
    [InlineData(".codex")]
    public void EveryLanguageInTheReleaseTemplateIsRead(string agentFolder)
    {
        var body = File.ReadAllText(ReleaseTemplatePath(agentFolder))
            .Replace("{VERSION}", "2.11.0")
            .Replace("{BODY_EN}", "## ◆ Section EN\n- line")
            .Replace("{BODY_ZH_HANT}", "## ◆ Section ZH_HANT\n- line")
            .Replace("{BODY_ZH_HANS}", "## ◆ Section ZH_HANS\n- line")
            .Replace("{BODY_JA}", "## ◆ Section JA\n- line")
            .Replace("{BODY_KO}", "## ◆ Section KO\n- line");

        Assert.Equal(["Section EN"], TitlesIn(LocalizationService.English));
        Assert.Equal(["Section ZH_HANT"], TitlesIn(LocalizationService.TraditionalChinese));
        Assert.Equal(["Section ZH_HANS"], TitlesIn(LocalizationService.SimplifiedChinese));
        Assert.Equal(["Section JA"], TitlesIn(LocalizationService.Japanese));
        Assert.Equal(["Section KO"], TitlesIn(LocalizationService.Korean));

        IEnumerable<string> TitlesIn(string language) =>
            ReleaseNotesParser.Parse(body, language).Select(s => s.Title);
    }

    // Found by walking up rather than copied at build time, so that moving the template fails this
    // test and not the whole test project's build.
    private static string ReleaseTemplatePath(string agentFolder)
    {
        var relative = Path.Combine(agentFolder, "skills", "git-release-overtranslate", "template.md");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"No {relative} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void ReadsNestingAndInlineMarks()
    {
        var sections = ReleaseNotesParser.Parse(Body, LocalizationService.English);
        var lines = sections[0].Lines;

        Assert.Equal(0, lines[0].Level);
        Assert.Equal(1, lines[2].Level);
        Assert.Equal(NotesSpanKind.Plain, lines[0].Spans[0].Kind);
        Assert.Equal(new NotesSpan("Appearance", NotesSpanKind.Code), lines[0].Spans[1]);

        var fixedLine = sections[1].Lines[0];
        Assert.Contains(new NotesSpan("errors", NotesSpanKind.Bold), fixedLine.Spans);
        Assert.Equal("Fixed errors in apps", string.Concat(fixedLine.Spans.Select(s => s.Text)));
    }

    [Fact]
    public void KeepsProseAboveTheSectionsWithoutItsLink()
    {
        var sections = ReleaseNotesParser.Parse(Body, LocalizationService.TraditionalChinese);

        Assert.Equal("", sections[0].Title);
        Assert.False(sections[0].Lines[0].Bullet);
        Assert.Equal("感謝 isdzjfs 的 fork", string.Concat(sections[0].Lines[0].Spans.Select(s => s.Text)));
    }

    [Fact]
    public void AnUpdateListsTheStableReleasesItTakesInNewestFirst()
    {
        var feed = ForUpdate("2.8.0", "2.10.0",
            Release("2.10.0"), Release("2.9.0"), Release("2.8.1-beta.2", prerelease: true), Release("2.8.0"));

        Assert.Equal(["2.10.0", "2.9.0"], feed!.Shown.Select(e => e.Version));
        Assert.Equal(2, feed.Total);
        Assert.Equal(0, feed.Earlier);
    }

    [Fact]
    public void AnUpdateShowsThreeAndCountsTheRest()
    {
        var feed = ForUpdate("2.7.0", "2.10.0",
            Release("2.7.1"), Release("2.10.0"), Release("2.8.0"), Release("2.7.2"), Release("2.9.0"), Release("2.7.0"));

        Assert.Equal(["2.10.0", "2.9.0", "2.8.0"], feed!.Shown.Select(e => e.Version));
        Assert.Equal(5, feed.Total);
        Assert.Equal(2, feed.Earlier);
    }

    [Fact]
    public void ReleasesPastTheTargetAreNotPartOfTheUpdate()
    {
        var feed = ForUpdate("2.8.0", "2.9.0", Release("2.10.0"), Release("2.9.0"), Release("2.8.0"));

        Assert.Equal(["2.9.0"], feed!.Shown.Select(e => e.Version));
    }

    [Fact]
    public void DraftsAreNeverListed()
    {
        var feed = ForUpdate("2.8.0", "2.10.0", Release("2.10.0", draft: true), Release("2.9.0"));

        Assert.Equal(["2.9.0"], feed!.Shown.Select(e => e.Version));
    }

    [Fact]
    public void AFlaggedBuildIsListedOnlyWhenAskedForAndNeverWithASuffix()
    {
        ReleaseRecord[] listing = [Release("2.10.0", prerelease: true), Release("2.9.1-beta.1", prerelease: true), Release("2.9.0")];

        var normal = ReleaseNotesSelection.ForUpdate(listing, V("2.8.0"), V("2.10.0"), includeFlagged: false, pageSize: 30);
        var rehearsal = ReleaseNotesSelection.ForUpdate(listing, V("2.8.0"), V("2.10.0"), includeFlagged: true, pageSize: 30);

        Assert.Equal(["2.9.0"], normal!.Shown.Select(e => e.Version));
        Assert.Equal(["2.10.0", "2.9.0"], rehearsal!.Shown.Select(e => e.Version));
    }

    [Fact]
    public void NothingInRangeMeansNoFeed()
    {
        Assert.Null(ForUpdate("2.10.0", "2.10.0", Release("2.10.0")));
    }

    [Fact]
    public void ATotalThatRanOutOfListingSaysSo()
    {
        ReleaseRecord[] listing = [Release("2.10.0"), Release("2.9.0")];

        var cut = ReleaseNotesSelection.ForUpdate(listing, V("2.0.0"), V("2.10.0"), false, pageSize: 2);
        var whole = ReleaseNotesSelection.ForUpdate(listing, V("2.0.0"), V("2.10.0"), false, pageSize: 30);

        Assert.True(cut!.TotalIsPartial);
        Assert.False(whole!.TotalIsPartial);
    }

    [Fact]
    public void HistoryStartsFromTheInstalledVersion()
    {
        var feed = ReleaseNotesSelection.History(
            [Release("2.11.0"), Release("2.10.0"), Release("2.9.1-beta.1", prerelease: true), Release("2.9.0"), Release("2.8.0"), Release("2.7.2")],
            V("2.10.0"));

        Assert.Equal(["2.10.0", "2.9.0", "2.8.0"], feed!.Shown.Select(e => e.Version));
    }

    [Fact]
    public void ReadsGitHubsListing()
    {
        const string json = """
            [{"tag_name":"v2.10.0","draft":false,"prerelease":true,"published_at":"2026-10-31T06:37:49Z","body":"text","html_url":"https://example.com/r"},
             {"tag_name":"2.9.0","body":null}]
            """;

        using var document = JsonDocument.Parse(json);
        var listing = ReleaseNotesService.ParseListing(document.RootElement);

        Assert.Equal("v2.10.0", listing[0].Tag);
        Assert.True(listing[0].Prerelease);
        Assert.Equal("text", listing[0].Body);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 6, 37, 49, TimeSpan.Zero), listing[0].Published);
        Assert.Equal("", listing[1].Body);
        Assert.Null(listing[1].Published);
    }

    [Fact]
    public void AListingEntryThatIsNotAnObjectIsSkipped()
    {
        using var document = JsonDocument.Parse("""[1, null, {"tag_name":"2.9.0"}]""");

        Assert.Equal("2.9.0", Assert.Single(ReleaseNotesService.ParseListing(document.RootElement)).Tag);
    }

    [Fact]
    public void AVPrefixedTagKeepsItsVersionWithoutThePrefix()
    {
        var feed = ForUpdate("2.8.0", "2.10.0", Release("v2.10.0"));

        Assert.Equal("2.10.0", feed!.Shown[0].Version);
    }

    [Theory]
    [InlineData("https://github.com/Hon-Lu/OverTranslate", "https://api.github.com/repos/Hon-Lu/OverTranslate/releases?per_page=30")]
    [InlineData("https://github.com/Hon-Lu/OverTranslate/", "https://api.github.com/repos/Hon-Lu/OverTranslate/releases?per_page=30")]
    [InlineData("https://github.com/Hon-Lu", null)]
    [InlineData("not a url", null)]
    public void FindsTheListingForARepository(string repository, string? expected) =>
        Assert.Equal(expected, ReleaseNotesService.ListingUrl(repository));

    [Theory]
    [InlineData("Just some text.", "https://example.com")]   // nothing but prose: no empty peek line
    [InlineData("", "")]                                     // only a link, with no address to give it
    [InlineData("", "not a url")]
    public void AReleaseThatCannotBeReadStillLists(string body, string url)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var entries = new[] { "2.10.0", "2.9.0" }
                    .Select(v => new ReleaseEntry(v, null, body, url))
                    .ToList();

                var list = new ReleaseNotesList();
                list.Show(new ReleaseNotesFeed(entries, 2, false), ReleaseNotesTag.Latest);

                var rows = ((StackPanel)list.FindName("Items")).Children.OfType<ToggleButton>().ToList();
                Assert.IsType<Grid>(rows[1].Content);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    // Through IsChecked rather than a click, which is how assistive technology toggles a row: a list
    // that only listened for clicks lit the row and left its notes closed.
    [Fact]
    public void OnlyOneVersionIsEverOpen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var entries = new[] { "2.10.0", "2.9.0", "2.8.0" }
                    .Select(v => new ReleaseEntry(v, null, Body, "https://example.com"))
                    .ToList();

                var list = new ReleaseNotesList();
                list.Show(new ReleaseNotesFeed(entries, 3, false), ReleaseNotesTag.Latest);
                list.Measure(new System.Windows.Size(400, 300));
                list.Arrange(new System.Windows.Rect(0, 0, 400, 300));

                var items = (StackPanel)list.FindName("Items");
                var rows = items.Children.OfType<ToggleButton>().ToList();
                var panels = items.Children.OfType<Border>().ToList();
                bool[] Open() => panels.Select(p => p.Visibility == Visibility.Visible).ToArray();
                bool[] Lit() => rows.Select(r => r.IsChecked == true).ToArray();

                Assert.Equal(new[] { true, false, false }, Open());
                Assert.Equal(new[] { true, false, false }, Lit());

                rows[1].IsChecked = true;
                Assert.Equal(new[] { false, true, false }, Open());
                Assert.Equal(new[] { false, true, false }, Lit());

                rows[1].IsChecked = false;
                Assert.Equal(new[] { false, false, false }, Open());
                Assert.Equal(new[] { false, false, false }, Lit());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory]
    [InlineData(true, false, true)]    // history open, update asked for: the update replaces it
    [InlineData(false, true, false)]   // update open, history asked for: the update comes forward
    [InlineData(true, true, false)]    // the same one asked for again: it just comes forward
    [InlineData(false, false, false)]
    public void TheUpdateTakesPriorityOverTheHistory(bool openIsHistory, bool wantHistory, bool replaces) =>
        Assert.Equal(replaces, UpdateWindow.ShouldReplace(openIsHistory, wantHistory));

    private static ReleaseNotesFeed? ForUpdate(string current, string target, params ReleaseRecord[] listing) =>
        ReleaseNotesSelection.ForUpdate(listing, V(current), V(target), includeFlagged: false, pageSize: 30);

    private static SemanticVersion V(string version) => SemanticVersion.Parse(version);

    private static ReleaseRecord Release(string tag, bool prerelease = false, bool draft = false) =>
        new(tag, draft, prerelease, null, "", "https://example.com");
}
