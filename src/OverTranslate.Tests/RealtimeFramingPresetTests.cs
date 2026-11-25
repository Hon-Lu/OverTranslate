using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The framings kept between sittings: the file's shape, the rules that decide what the list can
/// hold, which of a target's presets a session opens on, where a window's blocks go when the window
/// moves, and what a stored rectangle turns back into.
/// </summary>
public class RealtimeFramingPresetTests
{
    private static readonly Rectangle Screen1080 = new(0, 0, 1920, 1080);
    private static readonly Rectangle WindowAt = new(100, 200, 1280, 720);
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    // ── The file ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeptFramingRoundTripsThroughTheFile()
    {
        var written = new AppSettings();
        written.Realtime.FramingPresets =
        [
            new RealtimeFramingPreset
            {
                Id = "a",
                Name = "漫畫",
                Ordinal = 2,
                Selected = true,
                CaptureMode = RealtimeCaptureMode.Window,
                ScreenDeviceName = Display1,
                Screen = new RealtimeFramingRect { X = 0, Y = 0, Width = 1920, Height = 1080 },
                WindowProcess = "game",
                WindowTitle = "Game — Chapter 3",
                WindowSize = new RealtimeFramingRect { Width = 1280, Height = 720 },
                SavedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Blocks =
                [
                    new RealtimeFramingBlock
                    {
                        X = 10, Y = 20, Width = 300, Height = 40,
                        Profile = RealtimePresets.SubtitleId,
                        Orientation = RealtimeTextOrientation.Vertical,
                        CompareOffsetX = 5, CompareOffsetY = -6,
                        CompareScale = 0.8, CompareHidden = true,
                    },
                ],
            },
        ];

        var reloaded = SettingsService.Parse(SettingsService.Serialize(written));
        var preset = Assert.Single(reloaded.Realtime.FramingPresets);

        Assert.Equal("a", preset.Id);
        Assert.Equal("漫畫", preset.Name);
        Assert.Equal(2, preset.Ordinal);
        Assert.True(preset.Selected);
        Assert.Equal(RealtimeCaptureMode.Window, preset.CaptureMode);
        Assert.Equal(Display1, preset.ScreenDeviceName);
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), preset.Screen.ToRectangle());
        Assert.Equal("game", preset.WindowProcess);
        Assert.Equal("Game — Chapter 3", preset.WindowTitle);
        Assert.Equal(new Rectangle(0, 0, 1280, 720), preset.WindowSize.ToRectangle());
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), preset.SavedAt);

        var block = Assert.Single(preset.Blocks);
        Assert.Equal(new Rectangle(10, 20, 300, 40), block.ToPlacement().Bounds);
        Assert.Equal(RealtimePresets.SubtitleId, block.ToPlacement().ProfileId);
        Assert.Equal(RealtimeTextOrientation.Vertical, block.ToPlacement().Orientation);
        Assert.Equal(new Point(5, -6), block.ToPlacement().CompareOffset);
        Assert.Equal(0.8, block.ToPlacement().CompareScale);
        Assert.True(block.ToPlacement().CompareHidden);
    }

    [Fact]
    public void FourIntegersPerRectangle_NotTheLibraryTypesShape()
    {
        var settings = new AppSettings();
        settings.Realtime.FramingPresets =
        [
            Make("a", blocks: [new Rectangle(1, 2, 3, 4)]),
        ];

        var json = SettingsService.Serialize(settings);

        Assert.Contains("\"Screen\": {", json);
        Assert.Contains("\"X\": 1,", json);
        Assert.Contains("\"Width\": 3,", json);
        // The library type would write Location and Size as well, which is the same two numbers a
        // second time; nothing in a preset's rectangles is written that way.
        Assert.DoesNotContain("\"Location\": {", json);
        Assert.DoesNotContain("IsEmpty", json);
    }

    /// <summary>
    /// The properties worked out from the others — the target's key above all, which carries
    /// separators — are a view of the preset, not part of it, and must not reach the settings file.
    /// </summary>
    [Fact]
    public void DerivedProperties_AreNotWrittenToTheFile()
    {
        var settings = new AppSettings();
        settings.Realtime.FramingPresets =
        [
            Make("a", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
                windowSize: new Size(1280, 720)),
        ];

        var json = SettingsService.Serialize(settings);

        Assert.DoesNotContain("IsForWindow", json);
        Assert.DoesNotContain("TargetKey", json);
        Assert.DoesNotContain("IsUsable", json);
        Assert.DoesNotContain("\\u001f", json);
    }

    [Fact]
    public void OneUnreadableEntry_CostsOnlyThatEntry()
    {
        // The second preset's direction is a name this build does not know, which makes the whole
        // list unreadable in one piece; read one at a time, the first preset still comes back.
        const string json = """
            {"Realtime":{"FramingPresets":[
              {"Id":"a","ScreenDeviceName":"D","Screen":{"Width":1920,"Height":1080},
               "Blocks":[{"X":0,"Y":0,"Width":10,"Height":10}]},
              {"Id":"b","ScreenDeviceName":"D","Screen":{"Width":1920,"Height":1080},
               "Blocks":[{"X":0,"Y":0,"Width":10,"Height":10,"Orientation":"Sideways"}]}
            ]}}
            """;

        var settings = SettingsService.Parse(json);

        Assert.Equal("a", Assert.Single(settings.Realtime.FramingPresets).Id);
    }

    [Fact]
    public void APresetWithNoScreen_IsDroppedRatherThanBreakingTheFile()
    {
        // Hand-edited, or written by something that did not mean it: the preset has nowhere to be
        // offered, and reading the rest of the settings must not fail over it.
        const string json = """
            {"Realtime":{"FramingPresets":[
              {"Id":"a","Screen":null,"Blocks":[{"X":0,"Y":0,"Width":10,"Height":10}]}
            ]}}
            """;

        Assert.Empty(SettingsService.Parse(json).Realtime.FramingPresets);
    }

    [Fact]
    public void AWindowPresetWithNoWindowSize_IsDropped()
    {
        // Its blocks are relative to a window this build cannot ask about, so there is nowhere to
        // put them.
        const string json = """
            {"Realtime":{"FramingPresets":[
              {"Id":"a","CaptureMode":"Window","WindowProcess":"game","WindowTitle":"Game",
               "Blocks":[{"X":0,"Y":0,"Width":10,"Height":10}]}
            ]}}
            """;

        Assert.Empty(SettingsService.Parse(json).Realtime.FramingPresets);
    }

    [Fact]
    public void MissingOrEmptyList_ReadsAsNoFramings()
    {
        Assert.Empty(SettingsService.Parse("{}").Realtime.FramingPresets);
        Assert.Empty(SettingsService.Parse("""{"Realtime":{"FramingPresets":[]}}""").Realtime.FramingPresets);
    }

    // ── What the list can hold ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OneTargetKeepsSeveralPresets_NewestFirst()
    {
        var older = Make("older", savedAt: new DateTime(2026, 1, 1));
        var newer = Make("newer", savedAt: new DateTime(2026, 2, 1));
        var other = Make("other", device: Display2, savedAt: new DateTime(2026, 3, 1));

        var clean = RealtimeSettings.CleanFramingPresets([older, newer, other]);

        // Two of them are the same target and both are kept; the newest leads.
        Assert.Equal(new[] { "other", "newer", "older" }, clean.Select(preset => preset.Id));
    }

    [Fact]
    public void EachPresetGetsANumberOfItsTarget_NewestFirst()
    {
        var older = Make("older", savedAt: new DateTime(2026, 1, 1));
        var newer = Make("newer", savedAt: new DateTime(2026, 2, 1));
        var other = Make("other", device: Display2, savedAt: new DateTime(2026, 3, 1));

        var clean = RealtimeSettings.CleanFramingPresets([older, newer, other]);
        var byId = clean.ToDictionary(preset => preset.Id);

        // 螢幕 1 · …, then 螢幕 1 · …（2）: the newest of a target is its first, whichever order the
        // file happened to hold them in.
        Assert.Equal(1, byId["newer"].Ordinal);
        Assert.Equal(2, byId["older"].Ordinal);
        Assert.Equal(1, byId["other"].Ordinal);
    }

    [Fact]
    public void ANumberInTheFile_IsKept_AndAMissingOrRepeatedOneTakesTheNextFree()
    {
        var kept = Make("kept", ordinal: 5, savedAt: new DateTime(2026, 2, 1));
        var repeated = Make("repeated", ordinal: 5, savedAt: new DateTime(2026, 1, 2));
        var missing = Make("missing", ordinal: 0, savedAt: new DateTime(2026, 1, 1));

        var byId = RealtimeSettings.CleanFramingPresets([kept, repeated, missing])
            .ToDictionary(preset => preset.Id);

        // Numbers already in the file are what the user has been reading, so 5 stays 5; the repeated
        // one takes the first number free, and the one with none takes the next.
        Assert.Equal(5, byId["kept"].Ordinal);
        Assert.Equal(1, byId["repeated"].Ordinal);
        Assert.Equal(2, byId["missing"].Ordinal);
    }

    [Fact]
    public void OnePresetOfATargetIsSelected_NotTwo()
    {
        var newer = Make("newer", selected: true, savedAt: new DateTime(2026, 2, 1));
        var older = Make("older", selected: true, savedAt: new DateTime(2026, 1, 1));
        var other = Make("other", device: Display2, selected: true, savedAt: new DateTime(2026, 1, 15));

        var byId = RealtimeSettings.CleanFramingPresets([newer, older, other])
            .ToDictionary(preset => preset.Id);

        Assert.True(byId["newer"].Selected);
        Assert.False(byId["older"].Selected);
        // Another target's own pick is not affected by the first target's.
        Assert.True(byId["other"].Selected);
    }

    [Fact]
    public void AnEntryThisBuildCannotUse_IsDropped()
    {
        var withoutId = Make("", savedAt: DateTime.UtcNow);
        var withoutScreen = Make("no-screen", savedAt: DateTime.UtcNow, screen: new Rectangle(0, 0, 0, 0));
        var withoutBlocks = Make("no-blocks", savedAt: DateTime.UtcNow);
        withoutBlocks.Blocks.Clear();
        var zeroBlock = Make("zero-block", savedAt: DateTime.UtcNow, blocks: [new Rectangle(0, 0, 0, 0)]);
        var duplicateId = Make("kept", savedAt: new DateTime(2026, 2, 1));
        var sameId = Make("kept", savedAt: new DateTime(2026, 1, 1));

        var clean = RealtimeSettings.CleanFramingPresets(
        [
            withoutId,
            withoutScreen,
            withoutBlocks,
            zeroBlock,
            duplicateId,
            sameId,
            null,
        ]);

        Assert.Equal("kept", Assert.Single(clean).Id);
    }

    [Fact]
    public void MoreBlocksThanASessionCanFrame_AreCutToWhatItCan()
    {
        var preset = Make("many", blocks:
        [
            new Rectangle(0, 0, 10, 10),
            new Rectangle(10, 0, 10, 10),
            new Rectangle(20, 0, 10, 10),
            new Rectangle(30, 0, 10, 10),
        ]);

        Assert.Equal(RealtimeSettings.MaxBlockCount, Assert.Single(RealtimeSettings.CleanFramingPresets([preset])).Blocks.Count);
    }

    [Fact]
    public void TheListStopsAtItsCap()
    {
        var many = Enumerable.Range(0, RealtimeSettings.MaxFramingPresets + 5)
            .Select(index => Make("p" + index, device: Display2 + index, savedAt: new DateTime(2026, 1, 1).AddDays(index)))
            .ToList();

        var clean = RealtimeSettings.CleanFramingPresets(many);

        Assert.Equal(RealtimeSettings.MaxFramingPresets, clean.Count);
        // Newest first, so the oldest fall off the end.
        Assert.Equal("p" + (RealtimeSettings.MaxFramingPresets + 4), clean[0].Id);
    }

    [Fact]
    public void ANamesLineBreaksBecomeSpaces_AndItIsCutToOneLine()
    {
        var preset = Make("a");
        preset.Name = "  漫畫\n第一卷\t ";

        Assert.Equal("漫畫 第一卷", Assert.Single(RealtimeSettings.CleanFramingPresets([preset])).Name);

        preset.Name = new string('長', RealtimeFramingPreset.MaxNameLength + 10);
        Assert.Equal(
            RealtimeFramingPreset.MaxNameLength,
            Assert.Single(RealtimeSettings.CleanFramingPresets([preset])).Name.Length);
    }

    [Fact]
    public void ACaptureModeThisBuildDoesNotKnow_ReadsAsScreen()
    {
        var preset = Make("a");
        preset.CaptureMode = (RealtimeCaptureMode)99;

        Assert.Equal(
            RealtimeCaptureMode.Screen,
            Assert.Single(RealtimeSettings.CleanFramingPresets([preset])).CaptureMode);
    }

    // ── Which framing a session opens on ───────────────────────────────────────────────────────

    [Fact]
    public void AScreen_MatchesItsOwnDeviceAndRectangleOnly()
    {
        var presets = new[] { Make("a") };
        var target = ScreenTarget();

        Assert.Same(presets[0], RealtimeFramingStore.Match(presets, target));

        // Same resolution, a different monitor: physical pixels on one monitor mean nothing on the
        // other, so this is not the same target.
        Assert.Null(RealtimeFramingStore.Match(presets, target with { DeviceName = Display2 }));

        // The same monitor at another resolution: the blocks would be in the wrong place.
        Assert.Null(RealtimeFramingStore.Match(presets, target with { Screen = new Rectangle(0, 0, 2560, 1440) }));
    }

    [Fact]
    public void AScreensSelectedPreset_WinsOverTheNewerOne()
    {
        var newer = Make("newer", savedAt: new DateTime(2026, 2, 1));
        var older = Make("older", selected: true, savedAt: new DateTime(2026, 1, 1));
        var target = ScreenTarget();

        Assert.Equal("older", RealtimeFramingStore.Match([newer, older], target)!.Id);

        // Nothing selected: the newest is the answer, which is what a target with one preset has too.
        Assert.Equal("newer", RealtimeFramingStore.Match([newer, Make("older")], target)!.Id);
    }

    [Fact]
    public void AWindow_MatchesItsTitle_ThenItsSelectedPreset()
    {
        var video = Make("video", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video",
            savedAt: new DateTime(2026, 2, 1));
        var chat = Make("chat", mode: RealtimeCaptureMode.Window, process: "browser", title: "Chat",
            savedAt: new DateTime(2026, 1, 1));
        var presets = new[] { video, chat };
        var target = WindowTarget("browser", "Video");

        // The title decides: this is the Video window, whatever was selected.
        Assert.Equal("video", RealtimeFramingStore.Match(presets, target)!.Id);
        Assert.Equal("chat", RealtimeFramingStore.Match(presets, target with { WindowTitle = "Chat" })!.Id);

        // The title matches neither: the user's own pick speaks for the application.
        chat.Selected = true;
        Assert.Equal("chat", RealtimeFramingStore.Match(presets, target with { WindowTitle = "Something" })!.Id);

        // No pick either, and two windows' titles: there is no telling which was meant.
        chat.Selected = false;
        Assert.Null(RealtimeFramingStore.Match(presets, target with { WindowTitle = "Something" }));
    }

    [Fact]
    public void AWindow_OnAnotherScreenOrInAnotherPlace_IsStillTheSameTarget()
    {
        // A window preset belongs to its application and size, not to a monitor or a position: its
        // blocks are relative, so the window can be moved anywhere and still get them.
        var preset = Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            windowSize: new Size(1280, 720));
        var moved = WindowTarget("game", "Game", new Rectangle(600, 700, 1280, 720));
        var elsewhere = WindowTarget("game", "Game", new Rectangle(100, 200, 1280, 720)) with
        {
            Screen = new Rectangle(1920, 0, 1920, 1080),
            DeviceName = Display2,
        };

        Assert.Same(preset, RealtimeFramingStore.Match([preset], moved));
        Assert.Same(preset, RealtimeFramingStore.Match([preset], elsewhere));
    }

    [Fact]
    public void AWindowOfAnotherSize_IsAnotherTarget()
    {
        var preset = Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            windowSize: new Size(1280, 720));
        var target = WindowTarget("game", "Game", new Rectangle(100, 200, 1600, 900));

        Assert.Null(RealtimeFramingStore.Match([preset], target));
        Assert.Equal(
            RealtimeFramingStore.Miss.WindowSize,
            RealtimeFramingStore.Explain([preset], target, out var nearest));
        Assert.Same(preset, nearest);
    }

    [Fact]
    public void TwoWindowsOfOneApplication_AreAmbiguous_NotASizeProblem()
    {
        // Both fit the window's size; what Match cannot do is tell the two windows apart without a
        // title. That is not a resize, and saying it was would send the user to resize a window that
        // is the right size already.
        var presets = new[]
        {
            Make("video", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video"),
            Make("chat", mode: RealtimeCaptureMode.Window, process: "browser", title: "Chat"),
        };
        var target = WindowTarget("browser", "Something");

        Assert.Null(RealtimeFramingStore.Match(presets, target));
        Assert.Equal(
            RealtimeFramingStore.Miss.None,
            RealtimeFramingStore.Explain(presets, target, out var nearest));
        Assert.Null(nearest);
    }

    [Fact]
    public void ARetitledWindow_KeepsItsOnlyTitle()
    {
        // A browser retitles itself as it plays. One title among this target's presets can only be
        // that window, so its layout is offered even though the title no longer matches. The list is
        // newest first, as the settings file holds it.
        var presets = new[]
        {
            Make("two", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video — 1",
                savedAt: new DateTime(2026, 2, 1)),
            Make("one", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video — 1"),
        };
        var target = WindowTarget("browser", "Video — 2");

        Assert.Equal("two", RealtimeFramingStore.Match(presets, target)!.Id);
    }

    [Fact]
    public void AnotherApplication_OnTheSameScreen_IsAnotherTarget()
    {
        var presets = new[] { Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game") };
        var target = WindowTarget("browser", "Video");

        Assert.Null(RealtimeFramingStore.Match(presets, target));
        Assert.Equal(RealtimeFramingStore.Miss.None, RealtimeFramingStore.Explain(presets, target, out _));
    }

    [Fact]
    public void AScreenAtAnotherResolution_SaysWhichSizeItWants()
    {
        var preset = Make("a", screen: new Rectangle(0, 0, 1920, 1080));
        var target = ScreenTarget() with { Screen = new Rectangle(0, 0, 2560, 1440) };

        Assert.Null(RealtimeFramingStore.Match([preset], target));
        Assert.Equal(
            RealtimeFramingStore.Miss.ScreenSize,
            RealtimeFramingStore.Explain([preset], target, out var nearest));
        Assert.Same(preset, nearest);
    }

    [Fact]
    public void TheFramingRememberedFromTheSittingBefore_ComesBackForTheSameTargetOnly()
    {
        var target = WindowTarget("game", "Game — Chapter 1", new Rectangle(100, 200, 1280, 720));

        // A window that retitled itself, moved, or landed on another screen between two sittings is
        // still the window the blocks were drawn on; the size is what makes it the same target.
        Assert.True(RealtimeFramingStore.SameTarget(target, target with { WindowTitle = "Game — Chapter 2" }));
        Assert.True(RealtimeFramingStore.SameTarget(target, target with { WindowBounds = new Rectangle(400, 50, 1280, 720) }));
        Assert.True(RealtimeFramingStore.SameTarget(target, target with { DeviceName = Display2, Screen = new Rectangle(1920, 0, 1920, 1080) }));

        Assert.True(RealtimeFramingStore.SameTarget(ScreenTarget(), ScreenTarget()));
        Assert.False(RealtimeFramingStore.SameTarget(target, target with { WindowProcess = "browser" }));
        Assert.False(RealtimeFramingStore.SameTarget(target, target with { WindowBounds = new Rectangle(100, 200, 1600, 900) }));
        Assert.False(RealtimeFramingStore.SameTarget(target, ScreenTarget()));
        Assert.False(RealtimeFramingStore.SameTarget(ScreenTarget(), ScreenTarget() with { DeviceName = Display2 }));
        Assert.False(RealtimeFramingStore.SameTarget(
            ScreenTarget(), ScreenTarget() with { Screen = new Rectangle(0, 0, 2560, 1440) }));
    }

    [Fact]
    public void TheBarListsEveryPresetOfItsOwnTarget()
    {
        var screen = Make("screen", savedAt: new DateTime(2026, 2, 1));
        var game = Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            savedAt: new DateTime(2026, 1, 2));
        var browser = Make("browser", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video",
            savedAt: new DateTime(2026, 1, 1));
        var elsewhere = Make("elsewhere", device: Display2, savedAt: new DateTime(2026, 1, 3));
        var resized = Make("resized", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            windowSize: new Size(1600, 900), savedAt: new DateTime(2026, 1, 4));

        var target = WindowTarget("game", "Game");

        // Only the game window's own, at its size: not the screen's, not another application's, not
        // another monitor's, and not the same application at a size this window does not have.
        Assert.Equal(
            new[] { "game" },
            RealtimeFramingStore.ForTarget([screen, game, browser, elsewhere, resized], target)
                .Select(preset => preset.Id));
    }

    // ── Saving ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWindowPreset_KeepsItsBlocksRelativeToTheWindow_AndItsSize()
    {
        var target = WindowTarget(bounds: new Rectangle(300, 400, 1280, 720));

        var (_, saved) = RealtimeFramingStore.SaveInto(
            [], target, [Block(350, 450, 100, 20)], new DateTime(2026, 5, 1));

        var stored = Assert.Single(saved!.Blocks);
        Assert.Equal(50, stored.X);
        Assert.Equal(50, stored.Y);
        Assert.Equal(new Rectangle(0, 0, 1280, 720), saved.WindowSize.ToRectangle());
    }

    [Fact]
    public void AWindowPreset_PlacesItsBlocksWhereTheWindowIsNow()
    {
        var savedAt = new DateTime(2026, 5, 1);
        var (_, saved) = RealtimeFramingStore.SaveInto(
            [], WindowTarget(bounds: new Rectangle(300, 400, 1280, 720)),
            [Block(350, 450, 100, 20)], savedAt);

        // The same window, moved; the blocks go with it.
        var moved = WindowTarget(bounds: new Rectangle(900, 600, 1280, 720));
        var placements = RealtimeFramingStore.Placements(saved!, moved);

        Assert.Equal(new Rectangle(950, 650, 100, 20), Assert.Single(placements).Bounds);
    }

    [Fact]
    public void AWindowPreset_IsNotPlacedOnAWindowOfAnotherSize()
    {
        var (_, saved) = RealtimeFramingStore.SaveInto(
            [], WindowTarget(bounds: new Rectangle(300, 400, 1280, 720)),
            [Block(350, 450, 100, 20)], new DateTime(2026, 5, 1));

        var resized = WindowTarget(bounds: new Rectangle(300, 400, 1600, 900));

        Assert.Empty(RealtimeFramingStore.Placements(saved!, resized));
    }

    [Fact]
    public void AnotherLayoutForTheSameTarget_IsASecondPreset_AndTheSelectedOne()
    {
        var existing = Make("a", name: "我的漫畫", ordinal: 1, blocks: [new Rectangle(0, 0, 100, 20)]);
        var target = ScreenTarget();

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(50, 60, 400, 80)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.NotEqual(existing.Id, saved!.Id);
        // The new one leads the list and is what this target now opens on.
        Assert.Same(saved.Id, presets[0].Id);
        Assert.True(saved.Selected);
        Assert.False(presets[1].Selected);
        // The preset already kept keeps its name and its number.
        Assert.Equal("我的漫畫", presets[1].Name);
        Assert.Equal(1, presets[1].Ordinal);
        Assert.Equal(2, saved.Ordinal);
        Assert.Equal("", saved.Name);
        Assert.Equal(new Rectangle(50, 60, 400, 80), Assert.Single(saved.Blocks).ToPlacement().Bounds);
    }

    [Fact]
    public void TheSameLayoutSavedAgain_IsThatPresetSelected_NotACopy()
    {
        var existing = Make("a", ordinal: 1, savedAt: new DateTime(2026, 4, 1), blocks: [new Rectangle(0, 0, 100, 20)]);
        var target = ScreenTarget();

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Same(existing.Id, saved!.Id);
        Assert.Single(presets);
        Assert.True(presets[0].Selected);
        // Its time is when it was made: nothing was added to move.
        Assert.Equal(new DateTime(2026, 4, 1), presets[0].SavedAt);
    }

    [Fact]
    public void ALayoutThatDiffersInAsmallWay_IsANewPreset()
    {
        var existing = Make("a", ordinal: 1, blocks: [new Rectangle(0, 0, 100, 20)]);
        var target = ScreenTarget();

        // One block one pixel wider: a variant the user kept on purpose.
        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 101, 20)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.NotEqual(existing.Id, saved!.Id);
    }

    [Fact]
    public void Saving_ATargetNothingIsKeptFor_AddsAPresetAtTheFront()
    {
        var existing = Make("old", device: Display2, savedAt: new DateTime(2026, 1, 1));
        var target = ScreenTarget();

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.Same(saved!.Id, presets[0].Id);
        Assert.Equal("", saved.Name);
        Assert.Equal(1, saved.Ordinal);
        Assert.True(saved.Selected);
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
    }

    /// <summary>
    /// The list is full: nothing is saved, and — the point of the rule — nothing already there is
    /// taken away to make room. A preset the user named, or the one a target opens on, is not the
    /// program's to throw away.
    /// </summary>
    [Fact]
    public void WhenTheListIsFull_NothingIsSaved_AndNothingIsDropped()
    {
        var full = Enumerable.Range(0, RealtimeSettings.MaxFramingPresets)
            .Select(index => Make("p" + index, device: Display2 + index,
                savedAt: new DateTime(2026, 1, 1).AddDays(index), name: index == 0 ? "留著的" : ""))
            .ToList();
        var before = full.Select(preset => preset.Id).Order().ToList();
        var target = ScreenTarget() with { DeviceName = @"\\.\DISPLAY-NEW" };

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            full, target, [Block(0, 0, 100, 20)], new DateTime(2026, 6, 1));

        Assert.Null(saved);
        Assert.Equal(RealtimeSettings.MaxFramingPresets, presets.Count);
        Assert.Equal(before, presets.Select(preset => preset.Id).Order());
        Assert.Contains(presets, preset => preset.Name == "留著的");
    }

    /// <summary>At the cap, re-saving a layout that is already kept selects it rather than refusing.</summary>
    [Fact]
    public void WhenTheListIsFull_TheSameLayoutCanStillBeSelected()
    {
        var full = Enumerable.Range(0, RealtimeSettings.MaxFramingPresets)
            .Select(index => Make("p" + index, device: Display2 + index, savedAt: new DateTime(2026, 1, 1).AddDays(index)))
            .ToList();
        var target = ScreenTarget() with { DeviceName = Display2 + "0" };
        var existing = full.Single(preset => preset.Id == "p0");
        var blocks = existing.Blocks.Select(block => block.ToPlacement()).ToList();

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            full, target, blocks, new DateTime(2026, 6, 1));

        Assert.Same(existing.Id, saved!.Id);
        Assert.True(saved.Selected);
        Assert.Equal(RealtimeSettings.MaxFramingPresets, presets.Count);
    }

    [Fact]
    public void AScreenSave_CarriesNoWindowIdentity_AndAWindowSaveDoes()
    {
        var screen = RealtimeFramingStore.SaveInto(
            [], ScreenTarget(), [Block(0, 0, 100, 20)], DateTime.UtcNow).Saved;
        Assert.Equal("", screen!.WindowProcess);
        Assert.Equal("", screen.WindowTitle);
        Assert.Equal(0, screen.WindowSize.Width);

        var window = RealtimeFramingStore.SaveInto(
            [], WindowTarget("game", "Game"), [Block(0, 0, 100, 20)], DateTime.UtcNow).Saved;
        Assert.Equal("game", window!.WindowProcess);
        Assert.Equal("Game", window.WindowTitle);
        Assert.Equal(new Rectangle(0, 0, 1280, 720), window.WindowSize.ToRectangle());
    }

    [Fact]
    public void SavingAnotherSizeOfTheSameWindow_IsAnotherTarget()
    {
        var small = Make("small", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            windowSize: new Size(1280, 720), ordinal: 1, savedAt: new DateTime(2026, 1, 1));
        var target = WindowTarget("game", "Game", new Rectangle(100, 200, 1600, 900));

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [small], target, [Block(120, 220, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.Equal(1, saved!.Ordinal);
        Assert.Equal(1600, saved.WindowSize.Width);
        Assert.False(presets.Single(preset => preset.Id == "small").Selected);
    }

    // ── Picking one by hand ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectingAPreset_UnselectsTheRestOfItsTarget_AndLeavesOtherTargetsAlone()
    {
        var first = Make("first", ordinal: 1, savedAt: new DateTime(2026, 1, 1));
        var second = Make("second", ordinal: 2, selected: true, savedAt: new DateTime(2026, 2, 1));
        var elsewhere = Make("elsewhere", device: Display2, selected: true, savedAt: new DateTime(2026, 3, 1));

        var byId = RealtimeFramingStore.SelectInto([second, first, elsewhere], "first")
            .ToDictionary(preset => preset.Id);

        Assert.True(byId["first"].Selected);
        Assert.False(byId["second"].Selected);
        Assert.True(byId["elsewhere"].Selected);
    }

    // ── What a stored framing turns back into ──────────────────────────────────────────────────

    [Fact]
    public void AStoredBlock_ComesBackAsTheSessionTakesIt()
    {
        var placement = new RealtimeBlockPlacement(
            new Rectangle(100, 200, 300, 40), RealtimePresets.InterfaceId,
            RealtimeTextOrientation.Vertical, new Point(10, -20), 0.75, true);

        var roundTripped = RealtimeFramingBlock.From(placement).ToPlacement();

        Assert.Equal(placement, roundTripped);
    }

    [Fact]
    public void ABlockValueOutOfRange_IsHeldToWhatTheLayersCanProduce()
    {
        var block = new RealtimeFramingBlock
        {
            Width = 10, Height = 10, Orientation = (RealtimeTextOrientation)7,
            CompareOffsetX = 5, CompareScale = 4.0,
        };

        var placement = block.ToPlacement();

        Assert.Equal(RealtimeTextOrientation.Horizontal, placement.Orientation);
        // Half an offset is no offset: the automatic placement is the only predictable answer.
        Assert.Null(placement.CompareOffset);
        Assert.Equal(1.0, placement.CompareScale);

        block.CompareScale = double.NaN;
        Assert.Equal(1.0, block.ToPlacement().CompareScale);

        block.CompareScale = 0.1;
        Assert.Equal(RealtimeComparePlacement.MinScale, block.ToPlacement().CompareScale);
    }

    [Fact]
    public void ScreenPlacements_AreClampedToTheScreen_AndDroppedWhenNothingIsLeft()
    {
        var preset = Make("a", blocks:
        [
            new Rectangle(100, 100, 300, 40),
            new Rectangle(-50, 100, 100, 40),
            new Rectangle(5000, 100, 100, 40),
        ]);

        var placements = RealtimeFramingStore.Placements(preset, ScreenTarget());

        Assert.Equal(2, placements.Count);
        Assert.Equal(new Rectangle(100, 100, 300, 40), placements[0].Bounds);
        Assert.Equal(new Rectangle(0, 100, 50, 40), placements[1].Bounds);
    }

    [Fact]
    public void Placements_NeverExceedWhatASessionCanFrame()
    {
        var preset = Make("a", blocks:
        [
            new Rectangle(0, 0, 10, 10),
            new Rectangle(20, 0, 10, 10),
            new Rectangle(40, 0, 10, 10),
            new Rectangle(60, 0, 10, 10),
        ]);

        Assert.Equal(RealtimeSettings.MaxBlockCount, RealtimeFramingStore.Placements(preset, ScreenTarget()).Count);
    }

    // ── The name a preset is listed under ─────────────────────────────────────────────────────

    [Fact]
    public void AWindowKeepsItsTitleAsItsDefaultName_AndThePageCanGiveItAnother()
    {
        var preset = Make("a", mode: RealtimeCaptureMode.Window, process: "game", title: "Game — Chapter 1");

        // The box shows the title; leaving it as it is stores no name of the user's own, so the
        // default goes on following the window.
        Assert.Equal("Game — Chapter 1", RealtimeFramingDisplay.Name(preset));
        Assert.Equal("", RealtimeFramingDisplay.StoredName(preset, "  Game — Chapter 1 "));

        // Anything else is the user's choice and kept as typed.
        Assert.Equal("第一章", RealtimeFramingDisplay.StoredName(preset, "第一章"));

        preset.Name = "第一章";
        Assert.Equal("第一章", RealtimeFramingDisplay.Name(preset));
    }

    [Fact]
    public void AWindowWithoutATitle_IsNamedByItsApplication()
    {
        var preset = Make("a", mode: RealtimeCaptureMode.Window, process: "game", title: "");

        Assert.Equal("game", RealtimeFramingDisplay.Name(preset));
    }

    [Fact]
    public void ASecondPresetOfATarget_CarriesItsNumberInTheDefaultName()
    {
        var second = Make("b", mode: RealtimeCaptureMode.Window, process: "game", title: "Game", ordinal: 2);
        var name = RealtimeFramingDisplay.Name(second);

        Assert.Contains("Game", name);
        Assert.Contains("2", name);
        // The default name as typed is stored as "no name of the user's own", numbering included.
        Assert.Equal("", RealtimeFramingDisplay.StoredName(second, name));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static RealtimeBlockPlacement Block(int x, int y, int width, int height) =>
        new(new Rectangle(x, y, width, height), RealtimePresets.StandardId);

    private static RealtimeFramingStore.Target ScreenTarget() =>
        new(RealtimeCaptureMode.Screen, Screen1080, Display1);

    private static RealtimeFramingStore.Target WindowTarget(
        string process = "game", string title = "Game", Rectangle? bounds = null) =>
        new(RealtimeCaptureMode.Window, Screen1080, Display1, process, title,
            bounds ?? WindowAt);

    private static RealtimeFramingPreset Make(
        string id,
        RealtimeCaptureMode mode = RealtimeCaptureMode.Screen,
        Rectangle? screen = null,
        string device = Display1,
        string process = "",
        string title = "",
        DateTime? savedAt = null,
        string? name = null,
        int ordinal = 0,
        bool selected = false,
        Size? windowSize = null,
        params Rectangle[] blocks) => new()
    {
        Id = id,
        Name = name ?? "",
        Ordinal = ordinal,
        Selected = selected,
        CaptureMode = mode,
        ScreenDeviceName = device,
        Screen = RealtimeFramingRect.From(screen ?? Screen1080),
        WindowProcess = process,
        WindowTitle = title,
        WindowSize = mode == RealtimeCaptureMode.Window
            ? RealtimeFramingRect.From(new Rectangle(0, 0, windowSize?.Width ?? 1280, windowSize?.Height ?? 720))
            : new RealtimeFramingRect(),
        SavedAt = savedAt ?? new DateTime(2026, 1, 1),
        Blocks = [.. (blocks.Length == 0 ? [new Rectangle(0, 0, 100, 20)] : blocks)
            .Select(bounds => new RealtimeFramingBlock
            {
                X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
            })],
    };
}
