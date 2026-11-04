using System.Text.Json;
using OverTranslate.Services;
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
    public void ABodyNotInTheTemplatesShapeHasNoSections()
    {
        Assert.Empty(ReleaseNotesParser.Parse("Just some text.", LocalizationService.English));
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

    private static ReleaseNotesFeed? ForUpdate(string current, string target, params ReleaseRecord[] listing) =>
        ReleaseNotesSelection.ForUpdate(listing, V(current), V(target), includeFlagged: false, pageSize: 30);

    private static SemanticVersion V(string version) => SemanticVersion.Parse(version);

    private static ReleaseRecord Release(string tag, bool prerelease = false, bool draft = false) =>
        new(tag, draft, prerelease, null, "", "https://example.com");
}
