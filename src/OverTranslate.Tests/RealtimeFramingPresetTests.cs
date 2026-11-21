using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The framings kept between sittings: the file's shape, the rules that decide what the list can
/// hold, which of a target's presets a session opens on, and what a stored rectangle turns back into.
/// </summary>
public class RealtimeFramingPresetTests
{
    private static readonly Rectangle Screen1080 = new(0, 0, 1920, 1080);
    private const string Display1 = @"\\.\DISPLAY1";

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
        var other = Make("other", device: @"\\.\DISPLAY2", savedAt: new DateTime(2026, 3, 1));

        var clean = RealtimeSettings.CleanFramingPresets([older, newer, other]);

        // Two of them are the same target and both are kept; the newest leads.
        Assert.Equal(new[] { "other", "newer", "older" }, clean.Select(preset => preset.Id));
    }

    [Fact]
    public void EachPresetGetsANumberOfItsTarget_NewestFirst()
    {
        var older = Make("older", savedAt: new DateTime(2026, 1, 1));
        var newer = Make("newer", savedAt: new DateTime(2026, 2, 1));
        var other = Make("other", device: @"\\.\DISPLAY2", savedAt: new DateTime(2026, 3, 1));

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
        var other = Make("other", device: @"\\.\DISPLAY2", selected: true, savedAt: new DateTime(2026, 1, 15));

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
            .Select(index => Make("p" + index, device: @"\\.\DISPLAY" + index, savedAt: new DateTime(2026, 1, 1).AddDays(index)))
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
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

        Assert.Same(presets[0], RealtimeFramingStore.Match(presets, target));

        // Same resolution, a different monitor: physical pixels on one monitor mean nothing on the
        // other, so this is not the same target.
        Assert.Null(RealtimeFramingStore.Match(presets, target with { DeviceName = @"\\.\DISPLAY2" }));

        // The same monitor at another resolution: the blocks would be in the wrong place.
        Assert.Null(RealtimeFramingStore.Match(presets, target with { Screen = new Rectangle(0, 0, 2560, 1440) }));

        // A window is not a screen, however alike the rectangles.
        Assert.Null(RealtimeFramingStore.Match(
            presets, target with { Mode = RealtimeCaptureMode.Window, WindowProcess = "game" }));
    }

    [Fact]
    public void AScreensSelectedPreset_WinsOverTheNewerOne()
    {
        var newer = Make("newer", savedAt: new DateTime(2026, 2, 1));
        var older = Make("older", selected: true, savedAt: new DateTime(2026, 1, 1));
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

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
        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "browser", "Video");

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
        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "browser", "Video — 2");

        Assert.Equal("two", RealtimeFramingStore.Match(presets, target)!.Id);
    }

    [Fact]
    public void AnotherApplication_OnTheSameScreen_IsAnotherTarget()
    {
        var presets = new[] { Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game") };
        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "browser", "Video");

        Assert.Null(RealtimeFramingStore.Match(presets, target));
    }

    [Fact]
    public void TheBarListsEveryPresetOfItsOwnTarget()
    {
        var screen = Make("screen", savedAt: new DateTime(2026, 2, 1));
        var game = Make("game", mode: RealtimeCaptureMode.Window, process: "game", title: "Game",
            savedAt: new DateTime(2026, 1, 2));
        var browser = Make("browser", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video",
            savedAt: new DateTime(2026, 1, 1));
        var elsewhere = Make("elsewhere", device: @"\\.\DISPLAY2", savedAt: new DateTime(2026, 1, 3));

        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "game", "Game");

        // Only the game window's own: not the screen's, not another application's, not another
        // monitor's — and in the order the settings list holds them.
        Assert.Equal(
            new[] { "game" },
            RealtimeFramingStore.ForTarget([screen, game, browser, elsewhere], target)
                .Select(preset => preset.Id));
    }

    [Fact]
    public void TheFramingRememberedFromTheSittingBefore_ComesBackForTheSameTargetOnly()
    {
        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "game", "Game — Chapter 1");

        // A window that retitled itself between two sittings is still the window the blocks were
        // drawn on: there is only one candidate, and it is this one.
        Assert.True(RealtimeFramingStore.SameTarget(target, target with { WindowTitle = "Game — Chapter 2" }));

        Assert.True(RealtimeFramingStore.SameTarget(
            target with { Mode = RealtimeCaptureMode.Screen },
            target with { Mode = RealtimeCaptureMode.Screen }));

        Assert.False(RealtimeFramingStore.SameTarget(target, target with { WindowProcess = "browser" }));
        Assert.False(RealtimeFramingStore.SameTarget(target, target with { Mode = RealtimeCaptureMode.Screen }));
        Assert.False(RealtimeFramingStore.SameTarget(target, target with { DeviceName = @"\\.\DISPLAY2" }));
        Assert.False(RealtimeFramingStore.SameTarget(
            target, target with { Screen = new Rectangle(0, 0, 2560, 1440) }));
    }

    // ── Saving ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnotherLayoutForTheSameTarget_IsASecondPreset_AndTheSelectedOne()
    {
        var existing = Make("a", name: "我的漫畫", ordinal: 1, blocks: [new Rectangle(0, 0, 100, 20)]);
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(50, 60, 400, 80)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.NotEqual(existing.Id, saved.Id);
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
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Same(existing.Id, saved.Id);
        Assert.Single(presets);
        Assert.True(presets[0].Selected);
        // Its time is when it was made: nothing was added to move.
        Assert.Equal(new DateTime(2026, 4, 1), presets[0].SavedAt);
    }

    [Fact]
    public void ALayoutThatDiffersInAsmallWay_IsANewPreset()
    {
        var existing = Make("a", ordinal: 1, blocks: [new Rectangle(0, 0, 100, 20)]);
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

        // One block one pixel wider: a variant the user kept on purpose.
        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 101, 20)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.NotEqual(existing.Id, saved.Id);
    }

    [Fact]
    public void Saving_ATargetNothingIsKeptFor_AddsAPresetAtTheFront()
    {
        var existing = Make("old", device: @"\\.\DISPLAY2", savedAt: new DateTime(2026, 1, 1));
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1);

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Equal(2, presets.Count);
        Assert.Same(saved.Id, presets[0].Id);
        Assert.Equal("", saved.Name);
        Assert.Equal(1, saved.Ordinal);
        Assert.True(saved.Selected);
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
    }

    [Fact]
    public void SavingPastTheCap_DropsTheOldest()
    {
        var full = Enumerable.Range(0, RealtimeSettings.MaxFramingPresets)
            .Select(index => Make("p" + index, device: @"\\.\DISPLAY" + index, savedAt: new DateTime(2026, 1, 1).AddDays(index)))
            .Reverse()
            .ToList();
        var target = new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, @"\\.\DISPLAY-NEW");

        var (presets, _) = RealtimeFramingStore.SaveInto(
            full, target, [Block(0, 0, 100, 20)], new DateTime(2026, 6, 1));

        Assert.Equal(RealtimeSettings.MaxFramingPresets, presets.Count);
        Assert.DoesNotContain(presets, preset => preset.Id == "p0");
    }

    [Fact]
    public void AScreenSave_CarriesNoWindowIdentity_AndAWindowSaveDoes()
    {
        var screen = RealtimeFramingStore.SaveInto(
            [], new RealtimeFramingStore.Target(RealtimeCaptureMode.Screen, Screen1080, Display1),
            [Block(0, 0, 100, 20)], DateTime.UtcNow).Saved;
        Assert.Equal("", screen.WindowProcess);
        Assert.Equal("", screen.WindowTitle);

        var window = RealtimeFramingStore.SaveInto(
            [], new RealtimeFramingStore.Target(RealtimeCaptureMode.Window, Screen1080, Display1, "game", "Game"),
            [Block(0, 0, 100, 20)], DateTime.UtcNow).Saved;
        Assert.Equal("game", window.WindowProcess);
        Assert.Equal("Game", window.WindowTitle);
    }

    [Fact]
    public void SavingUnderARetitledWindow_UpdatesThatWindowsPresetRatherThanAddingAnother()
    {
        var existing = Make("a", mode: RealtimeCaptureMode.Window, process: "browser", title: "Video — 1");
        var target = new RealtimeFramingStore.Target(
            RealtimeCaptureMode.Window, Screen1080, Display1, "browser", "Video — 2");

        var (presets, saved) = RealtimeFramingStore.SaveInto(
            [existing], target, [Block(0, 0, 100, 20)], new DateTime(2026, 5, 1));

        Assert.Same(existing.Id, saved.Id);
        Assert.Equal("Video — 2", saved.WindowTitle);
        Assert.Single(presets);
    }

    // ── Picking one by hand ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectingAPreset_UnselectsTheRestOfItsTarget_AndLeavesOtherTargetsAlone()
    {
        var first = Make("first", ordinal: 1, savedAt: new DateTime(2026, 1, 1));
        var second = Make("second", ordinal: 2, selected: true, savedAt: new DateTime(2026, 2, 1));
        var elsewhere = Make("elsewhere", device: @"\\.\DISPLAY2", selected: true, savedAt: new DateTime(2026, 3, 1));

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
    public void Placements_AreClampedToTheScreen_AndDroppedWhenNothingIsLeft()
    {
        var preset = Make("a", blocks:
        [
            new Rectangle(100, 100, 300, 40),
            new Rectangle(-50, 100, 100, 40),
            new Rectangle(5000, 100, 100, 40),
        ]);

        var placements = RealtimeFramingStore.Placements(preset, Screen1080);

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

        Assert.Equal(RealtimeSettings.MaxBlockCount, RealtimeFramingStore.Placements(preset, Screen1080).Count);
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
        SavedAt = savedAt ?? new DateTime(2026, 1, 1),
        Blocks = [.. (blocks.Length == 0 ? [new Rectangle(0, 0, 100, 20)] : blocks)
            .Select(bounds => new RealtimeFramingBlock
            {
                X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
            })],
    };
}
