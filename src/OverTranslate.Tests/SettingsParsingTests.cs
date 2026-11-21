using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

// Settings are read back from a file the user may have carried across several versions, so the
// parser meets shapes the current build never wrote: keys added since, keys since removed, enum
// values retired, a file truncated by a power cut. The old parser answered all of those the same
// way — throw away everything and start from defaults — which silently cost people their API key
// and hotkey. These tests pin the rule that only the unreadable field pays.
public class SettingsParsingTests
{
    // 「Google 翻譯 (RPC)」 is no longer an option of its own; whoever picked it gets 「(標準)」, the
    // option it became part of — the same translations, not the default engine.
    [Fact]
    public void RetiredGoogleRpcChoice_IsReadAsTheStandardGoogleOption()
    {
        var settings = SettingsService.Parse("""{"Provider":"Google2","Realtime":{"Provider":"Google2"}}""");

        Assert.Equal(TranslationProvider.Google, settings.Provider);
        Assert.Equal(TranslationProvider.Google, settings.Realtime.Provider);
        Assert.DoesNotContain(LanguageData.Providers, item => item.Provider == TranslationProvider.Google2);
    }

    [Fact]
    public void YoudaoAndTranSmart_AreReadBackByName()
    {
        var settings = SettingsService.Parse("""{"Provider":"Youdao","Realtime":{"Provider":"TranSmart"}}""");

        Assert.Equal(TranslationProvider.Youdao, settings.Provider);
        Assert.Equal(TranslationProvider.TranSmart, settings.Realtime.Provider);
    }

    [Fact]
    public void Yandex_IsReadBackByName()
    {
        var settings = SettingsService.Parse("""{"Provider":"Yandex","Realtime":{"Provider":"Yandex"}}""");

        Assert.Equal(TranslationProvider.Yandex, settings.Provider);
        Assert.Equal(TranslationProvider.Yandex, settings.Realtime.Provider);
    }

    [Fact]
    public void QuickTranslateDefaultsToEnglishWithoutChangingOtherDefaults()
    {
        Assert.Equal("EN-US", new QuickTranslateSettings().TargetLanguage);
        var settings = SettingsService.Parse("{}");
        Assert.Equal("EN-US", settings.QuickTranslate.TargetLanguage);
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.TargetLanguage);
    }

    [Fact]
    public void QuickTranslateGroup_WinsOverFlatKeysAndSerializesOnlyTheGroup()
    {
        var settings = SettingsService.Parse(
            """{"QuickTranslateSourceLanguage":"JA","QuickTranslateTargetLanguage":"EN-US","QuickTranslate":{"SourceLanguage":"KO","TargetLanguage":"ZH-HANT"}}""");

        Assert.Equal("KO", settings.QuickTranslate.SourceLanguage);
        Assert.Equal("ZH-HANT", settings.QuickTranslate.TargetLanguage);
        var json = SettingsService.Serialize(settings);
        Assert.DoesNotContain("QuickTranslateSourceLanguage", json);
        Assert.DoesNotContain("QuickTranslateTargetLanguage", json);
        var reloaded = SettingsService.Parse(json);
        Assert.Equal("KO", reloaded.QuickTranslate.SourceLanguage);
        Assert.Equal("ZH-HANT", reloaded.QuickTranslate.TargetLanguage);
    }

    [Fact]
    public void QuickTranslateGroup_InvalidFieldKeepsOtherFields()
    {
        var settings = SettingsService.Parse(
            """{"QuickTranslate":{"SourceLanguage":42,"TargetLanguage":"JA"}}""");

        Assert.Equal(LanguageData.DefaultSourceLanguage, settings.QuickTranslate.SourceLanguage);
        Assert.Equal("JA", settings.QuickTranslate.TargetLanguage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void QuickTranslateGroup_InvalidOrEmptyGroupUsesIndependentDefaults(string group)
    {
        var settings = SettingsService.Parse(
            """{"SourceLanguage":"JA","TargetLanguage":"EN-US","QuickTranslate":""" + group + "}");

        Assert.Equal(LanguageData.DefaultSourceLanguage, settings.QuickTranslate.SourceLanguage);
        Assert.Equal("EN-US", settings.QuickTranslate.TargetLanguage);
    }

    [Fact]
    public void QuickTranslateLanguages_MigrateSharedPairThenRemainIndependent()
    {
        var settings = SettingsService.Parse(
            """{"SourceLanguage":"JA","TargetLanguage":"EN-US"}""");

        Assert.Equal("JA", settings.QuickTranslate.SourceLanguage);
        Assert.Equal("EN-US", settings.QuickTranslate.TargetLanguage);

        settings.SourceLanguage = "KO";
        settings.TargetLanguage = "ZH-HANT";
        var reloaded = SettingsService.Parse(System.Text.Json.JsonSerializer.Serialize(settings));

        Assert.Equal("JA", reloaded.QuickTranslate.SourceLanguage);
        Assert.Equal("EN-US", reloaded.QuickTranslate.TargetLanguage);
        Assert.Equal("KO", reloaded.SourceLanguage);
        Assert.Equal("ZH-HANT", reloaded.TargetLanguage);
    }

    [Fact]
    public void QuickTranslateLanguages_KeepExplicitPairAndIgnoreInvalidValues()
    {
        var settings = SettingsService.Parse(
            """{"SourceLanguage":"JA","TargetLanguage":"EN-US","QuickTranslateSourceLanguage":"AUTO","QuickTranslateTargetLanguage":"KO"}""");

        Assert.Equal("AUTO", settings.QuickTranslate.SourceLanguage);
        Assert.Equal("KO", settings.QuickTranslate.TargetLanguage);

        var invalid = SettingsService.Parse(
            """{"QuickTranslateSourceLanguage":null,"QuickTranslateTargetLanguage":42}""");

        Assert.Equal(LanguageData.DefaultSourceLanguage, invalid.QuickTranslate.SourceLanguage);
        Assert.Equal("EN-US", invalid.QuickTranslate.TargetLanguage);
    }

    [Fact]
    public void MissingOpenAiSettings_UseSafeDefaults()
    {
        var settings = SettingsService.Parse("{}");

        // Empty rather than the address itself: the provider fills that in, so a settings file that
        // never mentions it keeps following whatever the build defaults to.
        Assert.Equal("", settings.OpenAiBaseUrl);
        Assert.Equal("", settings.OpenAiApiKey);

        // No profiles and nothing selected is the built-in setting, which is the one shape the panel
        // cannot be left without: it carries the model, the temperature and both prompt pairs.
        Assert.Empty(settings.OpenAi.Profiles);
        Assert.Equal("", settings.OpenAi.SelectedProfileId);
        Assert.Null(settings.OpenAi.SelectedProfile());
    }

    /// <summary>
    /// A settings file written before the model profiles comes back on the built-in setting, with
    /// nothing pointing at a prompt that no longer exists.
    /// </summary>
    /// <remarks>
    /// The OpenAI settings were reshaped from four loose fields and two prompt lists into one list of
    /// whole profiles, and the old keys were dropped rather than migrated — on the owner's call, since
    /// a stored prompt has neither a name nor a model to carry into a profile.
    ///
    /// What is worth pinning is not that the old values are gone but that nothing survives them. The
    /// failure to be afraid of is a half-upgrade: the prompts gone while the selection that named one
    /// of them stays, leaving the panel showing 「我的設定 1」 with nothing behind it and the provider
    /// sending an empty instruction. It cannot happen — the selection was called
    /// <c>SelectedAutoPromptId</c> and is now <c>SelectedProfileId</c>, and the reader only ever looks
    /// for names it knows — but "cannot happen" is exactly the claim that stops being true when
    /// somebody reuses an old key name for a new purpose.
    /// </remarks>
    [Fact]
    public void OpenAiSettingsWrittenBeforeTheProfiles_ComeBackOnTheBuiltInSetting()
    {
        var settings = SettingsService.Parse("""
            {
              "OpenAiBaseUrl": "http://localhost:1234/v1",
              "OpenAiApiKey": "local-key",
              "OpenAiModel": "translategemma:4b",
              "OpenAiTemperatureEnabled": false,
              "OpenAiTemperature": 0.8,
              "OpenAi": {
                "AutoPrompts": [
                  { "Id": "kept", "Name": "我的設定 1", "Template": "into {target}" }
                ],
                "ExplicitPrompts": [
                  { "Id": "other", "Name": "我的設定 2", "Template": "{source}->{target}" }
                ],
                "SelectedAutoPromptId": "kept",
                "SelectedExplicitPromptId": "other"
              }
            }
            """);

        // The connection is the half that was deliberately left where it shipped: it is the server,
        // and switching which model to ask it for is not switching servers.
        Assert.Equal("http://localhost:1234/v1", settings.OpenAiBaseUrl);
        Assert.Equal("local-key", settings.OpenAiApiKey);

        // Everything else starts again, and nothing is left pointing at what is gone.
        Assert.Empty(settings.OpenAi.Profiles);
        Assert.Equal("", settings.OpenAi.SelectedProfileId);
        Assert.Null(settings.OpenAi.SelectedProfile());
    }

    /// <summary>
    /// A selection naming a profile that is not in the file falls back to the built-in setting.
    /// </summary>
    /// <remarks>
    /// The same question one step further on: a file someone edited by hand, or one saved while a
    /// profile was being deleted. The panel draws the built-in row as the checked one and writes the
    /// empty id back the moment that row is realized, so the file corrects itself without anyone
    /// being told a setting they never had has gone.
    /// </remarks>
    [Fact]
    public void ASelectionNamingNoProfile_FallsBackToTheBuiltInSetting()
    {
        var settings = SettingsService.Parse("""
            {
              "OpenAi": {
                "Profiles": [ { "Id": "a", "Name": "留著的", "Model": "model-a" } ],
                "SelectedProfileId": "deleted"
              }
            }
            """);

        Assert.Equal("留著的", Assert.Single(settings.OpenAi.Profiles).Name);
        Assert.Null(settings.OpenAi.SelectedProfile());
    }

    [Fact]
    public void MissingRealtimeTranslationSettings_UseIndependentDefaults()
    {
        var settings = SettingsService.Parse(
            """{"TargetLanguage":"JA","Provider":"DeepL"}""");

        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.Realtime.Provider);

        // English rather than 自動, which the realtime picker does not offer. This was empty on
        // purpose once — a blank asks the question instead of answering it badly — and stopped being
        // so when the shortcut arrived, because a shortcut has no page on which to ask. What it
        // must never be is 自動; see LanguageData.GetValidRealtimeSourceCode.
        Assert.Equal(LanguageData.DefaultRealtimeSourceLanguage, settings.Realtime.SourceLanguage);
        Assert.False(LanguageData.IsAutomaticSource(settings.Realtime.SourceLanguage));
        Assert.Equal("JA", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
    }

    [Fact]
    public void MissingCaptureSettings_UseHorizontalTextByDefault()
    {
        var settings = SettingsService.Parse("{}");

        Assert.False(settings.Capture.VerticalText);
    }

    [Fact]
    public void CaptureTextDirection_IsReadFromItsOwnGroup()
    {
        var settings = SettingsService.Parse(
            """{"Capture":{"VerticalText":true}}""");

        Assert.True(settings.Capture.VerticalText);
    }

    /// <summary>
    /// A name this build writes reads back as itself. design.md §15.5, the two cases that keep the
    /// user's answer.
    /// </summary>
    /// <remarks>
    /// The other four cases of that table — the two v1 names, an unknown one, and no key at all —
    /// are the two tests below. Between the three of them every value that can be sitting in a
    /// settings file has somewhere it lands.
    /// </remarks>
    [Theory]
    [InlineData("General", CaptureLayoutMode.General)]
    [InlineData("Interface", CaptureLayoutMode.Interface)]
    public void CaptureLayoutMode_IsReadFromItsOwnGroup(string stored, CaptureLayoutMode expected)
    {
        var settings = SettingsService.Parse(
            "{\"Capture\":{\"LayoutMode\":\"" + stored + "\"}}");

        Assert.Equal(expected, settings.Capture.LayoutMode);
    }

    /// <summary>
    /// Every settings file written before this switch existed, and every first run, opens on 一般.
    /// </summary>
    [Fact]
    public void ASettingsFileWithoutALayoutMode_OpensOnTheDefault()
    {
        var settings = SettingsService.Parse("""{"Capture":{"VerticalText":true}}""");

        Assert.Equal(CaptureLayoutMode.General, settings.Capture.LayoutMode);
        Assert.True(settings.Capture.VerticalText);
    }

    /// <summary>
    /// A mode name this build cannot read falls back to 一般, and takes nothing else with it.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole of design.md §15.5's fallback. It is not code of its own: the mode is
    /// stored by name, a name that is not a member will not deserialize, and the settings reader
    /// already keeps a property's default when a value will not read. The test is here to say the
    /// chain actually closes, because the day it stops closing is a day nobody looks.</para>
    ///
    /// <para>The v1 names go through the same door, which is why the theory carries them: a file
    /// still saying ComicArticle or Standard is not a hypothetical, it is on the disk of everyone
    /// who ran the previous build. Standard landing on 一般 rather than on 介面 is deliberate and
    /// is the one case worth arguing about — Standard was the old default, so somebody storing it
    /// most likely never opened the switch at all, and what that means is "no opinion", not
    /// "keep me conservative". No opinion gets the new default. The remaining pair of cases —
    /// General and Interface written by this build — round-trip below.</para>
    /// </remarks>
    [Theory]
    [InlineData("Webtoon")]      // a mode a later release named
    [InlineData("ComicArticle")] // v1: the mode that became General
    [InlineData("Standard")]     // v1: the old default
    public void ALayoutModeNameThisBuildCannotRead_OpensOnTheDefaultAndCostsNothingElse(string stored)
    {
        var settings = SettingsService.Parse(
            "{\"ApiKey\":\"secret\",\"Capture\":{\"LayoutMode\":\"" + stored + "\",\"VerticalText\":true}}");

        Assert.Equal(CaptureLayoutMode.General, settings.Capture.LayoutMode);
        Assert.True(settings.Capture.VerticalText);
        Assert.Equal("secret", settings.ApiKey);
    }

    /// <summary>A settings file that has never said draws its blocks on 標準, written across, and has no modes of its own.</summary>
    [Fact]
    public void ASettingsFileWithoutBlockDefaults_DrawsBlocksOnStandard()
    {
        var settings = SettingsService.Parse("{}");

        Assert.Equal(RealtimePresets.StandardId, settings.Realtime.NewBlockProfile);
        Assert.Equal(RealtimeTextOrientation.Horizontal, settings.Realtime.TextOrientation);
        Assert.Empty(settings.Realtime.Profiles);
        Assert.Equal(RealtimePresets.StandardId, new RealtimeSettings().NewBlockProfile);
        Assert.Equal(RealtimeTextOrientation.Horizontal, new RealtimeSettings().TextOrientation);
        Assert.Empty(new RealtimeSettings().Profiles);
    }

    /// <summary>
    /// The mode a file from 2.12.0 or earlier holds is not carried over, nor the one this branch
    /// wrote before modes had ids: both open on 標準.
    /// </summary>
    /// <remarks>
    /// The 2.12.0 file says 字幕 for nearly everyone, and only because 字幕 was the default of the
    /// two there were. Read as a choice, it would keep every one of them off the mode that is now the
    /// one to start from — see <see cref="RealtimeSettings.NewBlockProfile"/>. NewBlockMode was never
    /// released. The direction beside them was a choice, and is kept.
    /// </remarks>
    [Theory]
    [InlineData("BlockMode", "Subtitle")]
    [InlineData("BlockMode", "Panel")]
    [InlineData("NewBlockMode", "Subtitle")]
    [InlineData("NewBlockMode", "Panel")]
    public void TheModeAnOlderFileHolds_IsNotReadAsAChoice(string key, string mode)
    {
        var settings = SettingsService.Parse(
            "{\"Realtime\":{\"" + key + "\":\"" + mode + "\",\"TextOrientation\":\"Vertical\"}}");

        Assert.Equal(RealtimePresets.StandardId, settings.Realtime.NewBlockProfile);
        Assert.Equal(RealtimeTextOrientation.Vertical, settings.Realtime.TextOrientation);
        Assert.DoesNotContain("NewBlockMode", SettingsService.Serialize(settings));
    }

    /// <summary>What a block was last set to survives the file: the mode by id, the direction by name.</summary>
    [Theory]
    [InlineData(RealtimePresets.SubtitleId, RealtimeTextOrientation.Horizontal)]
    [InlineData(RealtimePresets.SubtitleId, RealtimeTextOrientation.Vertical)]
    [InlineData(RealtimePresets.InterfaceId, RealtimeTextOrientation.Horizontal)]
    [InlineData(RealtimePresets.StandardId, RealtimeTextOrientation.Vertical)]
    public void BlockDefaults_RoundTripThroughTheFile(string profile, RealtimeTextOrientation orientation)
    {
        var written = new AppSettings();
        written.Realtime.NewBlockProfile = profile;
        written.Realtime.TextOrientation = orientation;

        var json = SettingsService.Serialize(written);
        Assert.Contains(orientation.ToString(), json);

        var reloaded = SettingsService.Parse(json);
        Assert.Equal(profile, reloaded.Realtime.NewBlockProfile);
        Assert.Equal(orientation, reloaded.Realtime.TextOrientation);
    }

    /// <summary>A custom mode as the last one chosen survives the file with its own values.</summary>
    [Fact]
    public void CustomModes_RoundTripThroughTheFile()
    {
        var written = new AppSettings();
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.SubtitleId, "Visual novels");
        mine.SettleSeconds = 0.45;
        mine.DetectorPercent = RealtimePresets.Automatic;
        var chat = RealtimeProfile.CreateCustom(RealtimePresets.InterfaceId, "Chat");
        chat.SentenceMerge = RealtimeSentenceMerge.None;
        written.Realtime.Profiles = [mine, chat];
        written.Realtime.NewBlockProfile = mine.Id;

        var reloaded = SettingsService.Parse(SettingsService.Serialize(written)).Realtime;

        Assert.Equal(2, reloaded.Profiles.Count);
        var read = reloaded.Profiles[0];
        Assert.Equal(mine.Id, read.Id);
        Assert.Equal("Visual novels", read.Name);
        Assert.Equal(RealtimePresets.SubtitleId, read.BasedOn);
        Assert.Equal(0.45, read.SettleSeconds);
        Assert.Equal(RealtimePresets.Automatic, read.DetectorPercent);
        // Unset stays unset — it follows the preset rather than a copy of what the preset was.
        Assert.Null(read.SentenceMerge);
        Assert.Null(read.ConfidencePercent);
        Assert.Equal(mine.Id, reloaded.NewBlockProfileOrStandard());
        Assert.Equal("Chat", reloaded.Profiles[1].Name);
        Assert.Equal(RealtimeSentenceMerge.None, reloaded.Profiles[1].SentenceMerge);
        Assert.Equal(RealtimeMerge.Lines, reloaded.ResolveProfile(chat.Id).Merge);
    }

    /// <summary>
    /// 分行處理 and 合併傾向, which 語句合併 replaced before any build with modes was released, are
    /// not read: a mode written with them follows its preset's 語句合併.
    /// </summary>
    [Fact]
    public void TheReplacedLayoutValues_AreNotRead()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"standard","Layout":"items","MergeTendency":5,"ClearAfterEmptyReads":1}]}}
            """);

        var stored = settings.Realtime.Profiles.Single();
        Assert.Null(stored.SentenceMerge);
        Assert.Equal(1, stored.ClearAfterEmptyReads);
        Assert.Equal(ResolvedProfile.Standard with { Id = "a", ClearAfterEmptyReads = 1 }, settings.Realtime.ResolveProfile("a"));
    }

    /// <summary>
    /// A block default naming a mode that has since been deleted opens on 標準, and so does every
    /// block still on it.
    /// </summary>
    [Fact]
    public void ADeletedMode_IsReadAsStandard()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"NewBlockProfile":"0123456789abcdef0123456789abcdef","Profiles":[]}}""");

        Assert.Equal("0123456789abcdef0123456789abcdef", settings.Realtime.NewBlockProfile);
        Assert.Equal(RealtimePresets.StandardId, settings.Realtime.NewBlockProfileOrStandard());
        Assert.Same(ResolvedProfile.Standard, settings.Realtime.ResolveProfile(settings.Realtime.NewBlockProfile));
    }

    /// <summary>A choice this build cannot read costs that value only; the mode follows its preset there.</summary>
    [Fact]
    public void AModeValueThisBuildCannotRead_FollowsThePresetAndCostsNothingElse()
    {
        var settings = SettingsService.Parse(
            """
            {"ApiKey":"secret",
             "Realtime":{"TextOrientation":"Vertical","ScrimOpacity":12,
               "Profiles":[{"Id":"a","Name":"A","BasedOn":"interface","ReadTiming":"later","ClearAfterEmptyReads":1},
                           {"Id":"b","Name":"B","BasedOn":"subtitle"}]}}
            """);

        var realtime = settings.Realtime;
        Assert.Equal(2, realtime.Profiles.Count);
        var resolved = realtime.ResolveProfile("a");
        Assert.Equal(RealtimeBlockMode.Panel, resolved.Layout);
        // ReadTiming is no longer a value: the mode waits as 遊戲介面・聊天室 does.
        Assert.Equal(RealtimeReadTiming.SettleThenRead, resolved.Timing);
        Assert.Equal(1, resolved.ClearAfterEmptyReads);
        Assert.Equal(RealtimeTextOrientation.Vertical, realtime.TextOrientation);
        Assert.Equal(12, realtime.ScrimOpacity);
        Assert.Equal("secret", settings.ApiKey);
    }

    /// <summary>A value of the wrong type costs the one mode it is in, not every mode the user made.</summary>
    [Fact]
    public void AModeThatCannotBeRead_CostsOnlyItself()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","Name":"A","SettleSeconds":"lots"},
                                     {"Id":"b","Name":"B","SettleSeconds":0.3},
                                     42,
                                     null]}}
            """);

        Assert.Equal(new[] { "b" }, settings.Realtime.Profiles.Select(profile => profile.Id));
        Assert.Equal(0.3, settings.Realtime.Profiles[0].SettleSeconds);
    }

    /// <summary>
    /// Out of range is kept as written and held to the range as the mode is read, so a later version
    /// with a wider range reads the file as meant.
    /// </summary>
    [Fact]
    public void AValueOutOfRange_IsKeptAndHeldToTheRangeWhenRead()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","SettleSeconds":9,"ClearAfterEmptyReads":0,"DetectorPercent":20,
                                      "IgnoreSmallPercent":300,"SentenceMerge":"None","LineSpacing":12,"ConfidencePercent":120},
                                     {"Id":"b","SentenceMerge":" all "}]}}
            """);

        var stored = settings.Realtime.Profiles[0];
        Assert.Equal(9, stored.SettleSeconds);
        // A name in any case, spaces round it, is that value.
        Assert.Equal(RealtimeSentenceMerge.All, settings.Realtime.Profiles[1].SentenceMerge);
        Assert.Equal(RealtimeMerge.Merged, settings.Realtime.ResolveProfile("b").Merge);
        Assert.Equal(0, stored.ClearAfterEmptyReads);

        var filled = stored.Filled();
        Assert.Equal(RealtimeProfile.MaxSettleSeconds, filled.SettleSeconds);
        Assert.Equal(RealtimeProfile.MinClearAfterEmptyReads, filled.ClearAfterEmptyReads);
        Assert.Equal(RealtimeProfile.MinDetectorPercent, filled.DetectorPercent);
        Assert.Equal(RealtimeProfile.MaxIgnoreSmallPercent, filled.IgnoreSmallPercent);
        Assert.Equal(RealtimeSentenceMerge.None, filled.SentenceMerge);
        Assert.Equal(RealtimeProfile.MaxConfidencePercent, filled.ConfidencePercent);

        var resolved = settings.Realtime.ResolveProfile("a");
        Assert.Equal(10, resolved.SettlePolls);
        Assert.Equal(1, resolved.ClearAfterEmptyReads);
        Assert.Equal(0.6, resolved.DetectorFraction);
        Assert.Equal(0.8, resolved.IgnoreSmallFraction, 6);
        Assert.Equal(RealtimeMerge.Lines, resolved.Merge);
        Assert.Equal(0.95, resolved.ConfidenceFloor);
    }

    /// <summary>
    /// 短句辨識門檻 replaced the 1–5 ConfidenceLevel it was drafted as, never released: a file that
    /// still holds one reads the presets' 80, not step 5 held up to 60.
    /// </summary>
    [Fact]
    public void ADraftConfidenceLevel_IsNotRead()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"standard","ConfidenceLevel":5}]}}""");

        Assert.Null(settings.Realtime.Profiles.Single().ConfidencePercent);
        Assert.Equal(ResolvedProfile.Standard with { Id = "a" }, settings.Realtime.ResolveProfile("a"));
    }

    /// <summary>
    /// 原文消失後立即清除譯文 replaced a 1–4 slider that was never released: a 3 or 4 written by it is
    /// kept in the file and reads as not ticked, the presets' 2.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void AnOldKeepCount_ReadsAsNotTicked(int stored)
    {
        var settings = SettingsService.Parse(
            $$$"""{"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"interface","ClearAfterEmptyReads":{{{stored}}}}]}}""");

        Assert.Equal(stored, settings.Realtime.Profiles.Single().ClearAfterEmptyReads);
        Assert.Equal(RealtimeProfile.MaxClearAfterEmptyReads, settings.Realtime.Profiles.Single().Filled().ClearAfterEmptyReads);
        Assert.Equal(ResolvedProfile.Interface with { Id = "a" }, settings.Realtime.ResolveProfile("a"));
    }

    /// <summary>
    /// 行距容許 and 沒有標點也合併 are no longer values of a mode: a file that still has them reads
    /// every other value of the mode as written, and groups on the measured rules.
    /// </summary>
    [Fact]
    public void ARetiredLineSpacingOrUnpunctuatedMerging_IsIgnored()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"subtitle","LineSpacing":5,"MergeUnpunctuated":false,"IgnoreSmallPercent":40}]}}
            """);

        var stored = Assert.Single(settings.Realtime.Profiles);
        Assert.Equal(40, stored.IgnoreSmallPercent);
        Assert.DoesNotContain("LineSpacing", SettingsService.Serialize(settings));
        Assert.DoesNotContain("MergeUnpunctuated", SettingsService.Serialize(settings));
        Assert.Equal(ResolvedProfile.Subtitle with { Id = "a", IgnoreSmallFraction = 0.4 }, settings.Realtime.ResolveProfile("a"));
    }

    /// <summary>
    /// The list keeps no mode twice, none without an id or wearing a preset's, and no more than ten;
    /// a mode made from anything but a preset follows 標準.
    /// </summary>
    [Fact]
    public void TheModeList_IsTidiedAsItIsRead()
    {
        var entries = string.Join(",", Enumerable.Range(0, 14).Select(i => $$"""{"Id":"m{{i}}","Name":"M{{i}}"}"""));
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"","Name":"no id"},{"Id":"standard","Name":"fake"},
                                     {"Id":"x","Name":"first","BasedOn":"elsewhere"},{"Id":"x","Name":"second"},
            """ + entries + "]}}");

        var profiles = settings.Realtime.Profiles;
        Assert.Equal(RealtimeSettings.MaxCustomProfiles, profiles.Count);
        Assert.Equal("first", profiles[0].Name);
        Assert.Equal(RealtimePresets.StandardId, profiles[0].BasedOn);
        Assert.DoesNotContain(profiles, profile => RealtimePresets.IsPreset(profile.Id) || profile.Id == "");
        Assert.Equal("m8", profiles[^1].Id);
    }

    /// <summary>
    /// Written as the name, not as a number or a flag.
    /// </summary>
    /// <remarks>
    /// 一般 and 介面 are unlikely to be the last two answers — realtime already has more than two —
    /// and a bool, or an ordinal, would have to change data format the day a third arrives. An
    /// ordinal would have been worse than useless through the v2 swap in particular: the two modes
    /// exchanged positions, so every stored 0 and 1 would have quietly come back as the other mode.
    /// A round trip through the file is what proves the choice actually survives.
    /// </remarks>
    [Theory]
    [InlineData(CaptureLayoutMode.General, "General")]
    [InlineData(CaptureLayoutMode.Interface, "Interface")]
    public void TheLayoutModeIsStoredByName(CaptureLayoutMode mode, string expectedName)
    {
        var written = new AppSettings();
        written.Capture.LayoutMode = mode;

        var json = SettingsService.Serialize(written);

        Assert.Contains($"\"LayoutMode\": \"{expectedName}\"", json);
        Assert.Equal(mode, SettingsService.Parse(json).Capture.LayoutMode);
    }

    [Fact]
    public void RealtimeTranslationSettings_DoNotChangeGeneralTranslationSettings()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"TargetLanguage":"KO","Provider":"OpenAI"}}""");

        Assert.Equal("KO", settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.Realtime.Provider);
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.TargetLanguage);
        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
    }

    [Fact]
    public void TheRealtimeSourceLanguageIsItsOwnKey()
    {
        // 即時翻譯 and 截圖翻譯 read different things at different times, so what one was last pointed
        // at says nothing about the other.
        var settings = SettingsService.Parse(
            """{"Realtime":{"SourceLanguage":"JA"},"SourceLanguage":"EN"}""");

        Assert.Equal("JA", settings.Realtime.SourceLanguage);
        Assert.Equal("EN", settings.SourceLanguage);
    }

    [Fact]
    public void AFileFromBeforeTheOpacityKey_KeepsTheBandItAlwaysHad()
    {
        // Every build before this one drew the scrim at a fixed alpha, so a settings file carried
        // across must not arrive at a different-looking overlay.
        var settings = SettingsService.Parse("""{"Realtime":{"ScrimColor":"#1E3A5F"}}""");

        Assert.Equal(
            OverTranslate.Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity,
            settings.Realtime.ScrimOpacity);
    }

    [Fact]
    public void TheOpacityIsItsOwnKey_AndDoesNotDisturbTheScrimColour()
    {
        var settings = SettingsService.Parse(
            """{"Realtime":{"ScrimColor":"#1E3A5F","ScrimOpacity":0}}""");

        Assert.Equal(0, settings.Realtime.ScrimOpacity);
        Assert.Equal("#1E3A5F", settings.Realtime.ScrimColor);
    }

    [Fact]
    public void MissingKeys_KeepTheirDefaults_AndLeaveTheRestIntact()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","SourceLanguage":"JA","ApiKey":"secret"}""");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("JA", settings.SourceLanguage);
        Assert.Equal("secret", settings.ApiKey);

        Assert.Equal("ZH-HANT", settings.TargetLanguage);
        Assert.Equal("Ctrl+Alt+A", settings.HotkeyDisplay);
        Assert.False(settings.AutoTranslateAfterSelection);
    }

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","RetiredIn160":123}""");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    // The reason the old catch was dangerous: dropping or renaming a TranslationProvider member
    // would have wiped every setting of everyone still storing that value.
    [Fact]
    public void UnknownEnumValue_CostsOnlyThatField()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","Provider":"Papago"}""");

        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    [Fact]
    public void WrongType_CostsOnlyThatField()
    {
        var settings = SettingsService.Parse(
            """{"Theme":"Light","ApiKey":"secret","AutoTranslateAfterSelection":"yes"}""");

        Assert.False(settings.AutoTranslateAfterSelection);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
    }

    // Save never writes null, but a hand-edited file can hold one. It must not become a null string.
    [Fact]
    public void ExplicitNull_FallsBackToTheDefaultRatherThanNull()
    {
        var settings = SettingsService.Parse(
            """{"Theme":null,"ApiKey":null,"SourceLanguage":null}""");

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("", settings.ApiKey);
        Assert.Equal(LanguageData.AutomaticSourceLanguage, settings.SourceLanguage);
    }

    [Theory]
    [InlineData("""{"Theme":"Light","ApiKey":"my-sec""")]   // truncated mid-write
    [InlineData("")]                                        // zero-byte file
    [InlineData("not json at all")]
    public void UnparseableFile_FallsBackToDefaults(string json)
    {
        var settings = SettingsService.Parse(json);

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("", settings.ApiKey);
        Assert.Equal(TranslationProvider.Microsoft, settings.Provider);
    }

    [Fact]
    public void EmptyObject_YieldsDefaults()
    {
        var settings = SettingsService.Parse("{}");

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Ctrl+Alt+A", settings.HotkeyDisplay);
        Assert.Equal(LanguageData.AutomaticSourceLanguage, settings.SourceLanguage);
        Assert.False(settings.QuickLookup.AutoCopyTranslation);
        Assert.False(settings.QuickLookup.ResultsCollapsed);
    }

    // A round trip has to survive, or the tolerant read would quietly drop values on every save.
    [Fact]
    public void EveryFieldSurvivesARoundTrip()
    {
        var written = System.Text.Json.JsonSerializer.Serialize(new AppSettings
        {
            HotkeyModifiers = 6,
            HotkeyVirtualKey = 0x42,
            HotkeyDisplay = "Ctrl+Shift+B",
            SourceLanguage = "KO",
            TargetLanguage = "EN",
            Provider = TranslationProvider.DeepL,
            ApiKey = "round-trip",
            OpenAiBaseUrl = "http://localhost:1234/v1",
            OpenAiApiKey = "local-key",
            Theme = "Light",
            AutoTranslateAfterSelection = true,
            SaveScreenshotToDisk = true,
            ScreenshotSavePath = @"D:\shots",
            TranslationWindowHotkeyEnabled = false,
            RealtimePauseHotkeyModifiers = 5,
            RealtimePauseHotkeyVirtualKey = 0x44,
            RealtimePauseHotkeyDisplay = "Ctrl+Shift+D",
            RealtimePauseHotkeyEnabled = false,
            Capture =
            {
                VerticalText = true,
            },
            QuickLookup =
            {
                AutoCopyTranslation = true,
                ResultsCollapsed = true,
            },
            Realtime =
            {
                TargetLanguage = "JA",
                Provider = TranslationProvider.OpenAI,
                CaptureMode = RealtimeCaptureMode.Window,
                CaptureScreenDeviceName = @"\\.\DISPLAY2",
                CaptureWindowProcess = "chrome",
                CaptureWindowTitle = "Something - YouTube",
                NaturalBackgroundEnabled = true,
                SampleSourceTextColor = true,
            },
        });

        var settings = SettingsService.Parse(written);

        Assert.Equal(6u, settings.HotkeyModifiers);
        Assert.Equal(0x42u, settings.HotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+B", settings.HotkeyDisplay);
        Assert.Equal("KO", settings.SourceLanguage);
        Assert.Equal("EN", settings.TargetLanguage);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
        Assert.Equal("JA", settings.Realtime.TargetLanguage);
        Assert.Equal(TranslationProvider.OpenAI, settings.Realtime.Provider);
        Assert.Equal("round-trip", settings.ApiKey);
        Assert.Equal("http://localhost:1234/v1", settings.OpenAiBaseUrl);
        Assert.Equal("local-key", settings.OpenAiApiKey);
        Assert.Equal("Light", settings.Theme);
        Assert.True(settings.AutoTranslateAfterSelection);
        Assert.True(settings.SaveScreenshotToDisk);
        Assert.Equal(@"D:\shots", settings.ScreenshotSavePath);
        Assert.False(settings.TranslationWindowHotkeyEnabled);
        Assert.Equal(5u, settings.RealtimePauseHotkeyModifiers);
        Assert.Equal(0x44u, settings.RealtimePauseHotkeyVirtualKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimePauseHotkeyDisplay);
        Assert.False(settings.RealtimePauseHotkeyEnabled);
        Assert.True(settings.Capture.VerticalText);
        Assert.True(settings.QuickLookup.AutoCopyTranslation);
        Assert.True(settings.QuickLookup.ResultsCollapsed);
        Assert.Equal(RealtimeCaptureMode.Window, settings.Realtime.CaptureMode);
        Assert.Equal(@"\\.\DISPLAY2", settings.Realtime.CaptureScreenDeviceName);
        Assert.Equal("chrome", settings.Realtime.CaptureWindowProcess);
        Assert.Equal("Something - YouTube", settings.Realtime.CaptureWindowTitle);
        Assert.True(settings.Realtime.NaturalBackgroundEnabled);
        Assert.True(settings.Realtime.SampleSourceTextColor);
    }

    [Fact]
    public void OneUnreadableValueInAGroupCostsOnlyThatValue()
    {
        // The reason the reader descends into groups instead of deserialising them whole: handing a
        // group to the serialiser makes the group the unit that fails, so one hand-edited nonsense
        // capture mode would take the scrim opacity and the switches down with it.
        var settings = SettingsService.Parse(
            "{\n"
            + "  \"Realtime\": {\n"
            + "    \"CaptureMode\": \"Telepathy\",\n"
            + "    \"ScrimOpacity\": 12,\n"
            + "    \"NaturalBackgroundEnabled\": true\n"
            + "  }\n"
            + "}");

        Assert.Equal(RealtimeCaptureMode.Screen, settings.Realtime.CaptureMode);
        Assert.Equal(12, settings.Realtime.ScrimOpacity);
        Assert.True(settings.Realtime.NaturalBackgroundEnabled);
    }

    [Fact]
    public void AnUnreadableCaptureDirection_KeepsItsHorizontalDefault()
    {
        var settings = SettingsService.Parse(
            """{"Capture":{"VerticalText":"sometimes"},"Theme":"Light"}""");

        Assert.False(settings.Capture.VerticalText);
        Assert.Equal("Light", settings.Theme);
    }

    [Fact]
    public void AFileWrittenBeforeTheGroupExistedKeepsEverythingElse()
    {
        // What an upgrading user's file looks like: no Realtime object at all, and everything that
        // moved into it still written flat. Those values are gone — that was the trade, taken
        // knowing 即時翻譯's own page sets all of them again in one visit — but nothing outside the
        // group may go with them.
        var settings = SettingsService.Parse(
            "{\n"
            + "  \"RealtimeBlockCount\": 3,\n"
            + "  \"RealtimeTargetLanguage\": \"JA\",\n"
            + "  \"RealtimeScrimOpacity\": 12,\n"
            + "  \"RealtimePauseHotkeyDisplay\": \"Ctrl+Shift+D\",\n"
            + "  \"ApiKey\": \"kept\"\n"
            + "}");

        // Left where they were, so they still have to survive the move happening around them.
        Assert.Equal("kept", settings.ApiKey);
        Assert.Equal("Ctrl+Shift+D", settings.RealtimePauseHotkeyDisplay);

        // Moved, so a file written before the move no longer says anything about them.
        Assert.Equal(LanguageData.DefaultTargetLanguage, settings.Realtime.TargetLanguage);
        Assert.Equal(
            OverTranslate.Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity,
            settings.Realtime.ScrimOpacity);
    }

    [Fact]
    public void GroupedSettingsAreWrittenAfterEveryFlatOne()
    {
        // The file is meant to read as two halves: everything that shipped before grouping
        // existed, then everything grouped. Properties are written in declaration order, so that
        // holds only by where the group is declared — exactly the kind of thing a later edit moves
        // without noticing, and the reader of appsettings.json is who pays.
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        var lastFlatKey = json.IndexOf("\"SkippedUpdateVersion\"", StringComparison.Ordinal);
        var captureGroup = json.IndexOf("\"Capture\":", StringComparison.Ordinal);
        var quickLookupGroup = json.IndexOf("\"QuickLookup\":", StringComparison.Ordinal);
        var realtimeGroup = json.IndexOf("\"Realtime\":", StringComparison.Ordinal);
        var ocrDebugGroup = json.IndexOf("\"OcrDebug\":", StringComparison.Ordinal);
        var openAiGroup = json.IndexOf("\"OpenAi\":", StringComparison.Ordinal);
        var rootKeys = System.Text.Json.JsonDocument.Parse(json).RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            lastFlatKey >= 0 && captureGroup >= 0 && quickLookupGroup >= 0 && realtimeGroup >= 0
            && ocrDebugGroup >= 0 && openAiGroup >= 0);
        Assert.True(
            captureGroup > lastFlatKey,
            "grouped settings must be written after every flat one");
        Assert.Equal(
            ["Capture", "QuickLookup", "QuickTranslate", "Realtime", "OcrDebug", "OpenAi", "LocalAi"], rootKeys.TakeLast(7));
    }

    /// <summary>
    /// The file keeps what it holds as itself, not as a run of <c>\uXXXX</c>.
    /// </summary>
    /// <remarks>
    /// System.Text.Json escapes every non-ASCII character by default, and <c>+</c> with them. That
    /// was tolerable while the file held paths and key codes; the prompt library gave it names and
    /// prose the user wrote, and a settings file someone opens to check their own prompt is not
    /// allowed to be unreadable.
    ///
    /// Pinned because the encoder is one property that can fall off an options object without
    /// anything else noticing — it changes only how the file reads, so no other test would fail.
    /// Both halves are asserted: widening the character range alone fixes the Chinese and leaves
    /// every shortcut written as <c>Ctrl\u002BAlt\u002BA</c>.
    /// </remarks>
    [Fact]
    public void TheFileIsWrittenToBeRead()
    {
        var settings = new AppSettings();
        settings.OpenAi.Profiles.Add(new OpenAiModelProfile
        {
            Id = "a",
            Name = "測試用",
            Template = "翻成 {target_name}",
        });

        var json = SettingsService.Serialize(settings);

        Assert.Contains("\"Name\": \"測試用\"", json);
        Assert.Contains("翻成 {target_name}", json);
        Assert.Contains("\"HotkeyDisplay\": \"Ctrl+Alt+A\"", json);
        Assert.DoesNotContain(@"\u", json);
    }

    // Written before either shortcut could be switched off: both have to come back on, or upgrading
    // would silently disable two shortcuts the user still has.
    [Fact]
    public void MissingHotkeyEnabledSettings_StartOn()
    {
        var settings = SettingsService.Parse("""{"Theme":"Light"}""");

        Assert.True(settings.TranslationWindowHotkeyEnabled);
        Assert.True(settings.RealtimePauseHotkeyEnabled);
    }

    // #398's 原文對照 shortcut arrives unset, on an upgrade as on a new installation.
    [Fact]
    public void MissingCompareHotkey_StaysUnset()
    {
        var settings = SettingsService.Parse("""{"Theme":"Light"}""");

        Assert.False(settings.RealtimeCompareHotkeyEnabled);
        Assert.Equal("", settings.RealtimeCompareHotkeyDisplay);
    }

    // Ctrl+Alt+S belonged to block framing until that shortcut was removed. A file written while it
    // did says nothing about 暫停 / 繼續, so that one arrives on its new default rather than on the
    // Ctrl+Alt+Q it used to have — and a user who had recorded their own keeps theirs.
    [Fact]
    public void TheKeyLeftBehindByBlockFramingBecomesThePauseDefault()
    {
        Assert.Equal("Ctrl+Alt+S", SettingsService.Parse("""{"Theme":"Light"}""")
            .RealtimePauseHotkeyDisplay);

        Assert.Equal("Ctrl+Alt+Q", SettingsService
            .Parse("""{"RealtimePauseHotkeyDisplay":"Ctrl+Alt+Q","RealtimePauseHotkeyVirtualKey":81}""")
            .RealtimePauseHotkeyDisplay);
    }

    // 取詞翻譯 defaults to Ctrl+C+C (#321) for a new installation only. Every saved file carries the
    // lookup kind, so one that has been running on Ctrl+Alt+Q stays on it.
    [Fact]
    public void LookupDefaultsToCtrlCCButAStoredKeyboardShortcutStays()
    {
        var fresh = SettingsService.Parse("""{"Theme":"Light"}""");
        Assert.Equal(ShortcutInputKind.CtrlCC, fresh.QuickLookupHotkeyInputKind);
        Assert.Equal("Ctrl+C+C", fresh.QuickLookupHotkeyDisplay);

        var existing = SettingsService.Parse("""
            {"QuickLookupHotkeyModifiers":3,"QuickLookupHotkeyVirtualKey":81,
             "QuickLookupHotkeyDisplay":"Ctrl+Alt+Q","QuickLookupHotkeyInputKind":"Keyboard"}
            """);
        Assert.Equal(
            ShortcutTrigger.Keyboard(3, 0x51),
            HotkeyBindings.TriggerFor(existing, HotkeyAction.QuickLookup));
        Assert.Equal("Ctrl+Alt+Q", existing.QuickLookupHotkeyDisplay);
    }

    /// <summary>
    /// 語句合併 this build cannot read — a name it does not know, a number, any other type — is left
    /// unset: the mode follows its preset there, keeps every other value, and nothing else in the file
    /// is lost. A converter that threw would have cost the whole mode.
    /// </summary>
    [Theory]
    [InlineData("\"Bogus\"")]
    [InlineData("3")]
    [InlineData("\"3\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{\"Name\":\"Passage\"}")]
    [InlineData("[\"Passage\"]")]
    [InlineData("\"None,Items\"")]
    [InlineData("\"\"")]
    public void ASentenceMergeThisBuildCannotRead_FollowsThePresetAndCostsNothingElse(string value)
    {
        var settings = SettingsService.Parse(
            "{\"Theme\":\"Light\",\"ApiKey\":\"secret\",\"Realtime\":{\"TargetLanguage\":\"JA\",\"Profiles\":[" +
            "{\"Id\":\"a\",\"Name\":\"A\",\"BasedOn\":\"interface\",\"SentenceMerge\":" + value +
            ",\"ClearAfterEmptyReads\":1,\"ConfidencePercent\":90}," +
            "{\"Id\":\"b\",\"Name\":\"B\",\"BasedOn\":\"subtitle\",\"SentenceMerge\":\"None\"}]}}");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("secret", settings.ApiKey);
        Assert.Equal("JA", settings.Realtime.TargetLanguage);
        Assert.Equal(["a", "b"], settings.Realtime.Profiles.Select(profile => profile.Id));
        var mode = settings.Realtime.Profiles[0];
        Assert.Equal("A", mode.Name);
        Assert.Null(mode.SentenceMerge);
        Assert.Equal(RealtimeSentenceMerge.Items, mode.Filled().SentenceMerge);
        Assert.Equal(1, mode.ClearAfterEmptyReads);
        Assert.Equal(90, mode.ConfidencePercent);
        Assert.Equal(RealtimeSentenceMerge.None, settings.Realtime.Profiles[1].SentenceMerge);
    }

    /// <summary>
    /// The nine-step 合併程度 (MergeLevel, 0–8) a beta build wrote is read past and not written back:
    /// the mode's 語句合併 goes back to its preset's, and every other value it holds stays.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData("7")]
    [InlineData("\"lots\"")]
    public void TheMergeLevelABetaWrote_IsReadPastAndDropped(string level)
    {
        var settings = SettingsService.Parse(
            "{\"Theme\":\"Light\",\"Realtime\":{\"TargetLanguage\":\"JA\",\"Profiles\":[" +
            "{\"Id\":\"a\",\"Name\":\"A\",\"BasedOn\":\"subtitle\",\"MergeLevel\":" + level +
            ",\"ClearAfterEmptyReads\":1,\"IgnoreSmallPercent\":40}]}}");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("JA", settings.Realtime.TargetLanguage);
        var mode = Assert.Single(settings.Realtime.Profiles);
        Assert.Equal("A", mode.Name);
        Assert.Null(mode.SentenceMerge);
        Assert.Equal(RealtimeSentenceMerge.Passage, mode.Filled().SentenceMerge);
        Assert.Equal(1, mode.ClearAfterEmptyReads);
        Assert.Equal(40, mode.IgnoreSmallPercent);
        Assert.DoesNotContain("MergeLevel", SettingsService.Serialize(settings));
    }

    /// <summary>語句合併 is written by name, and reads back as written.</summary>
    [Fact]
    public void SentenceMergeIsWrittenByName()
    {
        var written = new AppSettings();
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.StandardId, "Mine");
        mine.SentenceMerge = RealtimeSentenceMerge.All;
        written.Realtime.Profiles = [mine];

        var json = SettingsService.Serialize(written);

        Assert.Contains("\"SentenceMerge\": \"All\"", json);
        Assert.Equal(RealtimeSentenceMerge.All, SettingsService.Parse(json).Realtime.Profiles.Single().SentenceMerge);
    }

    // 2.12.0 wrote whether the edit layer's guidance plate was folded away. The plate is gone; a file
    // that still says so reads as any other, and the key is not written back.
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void TheGuidanceStateAnOlderFileHolds_IsReadPastAndDropped(string expanded)
    {
        var settings = SettingsService.Parse(
            "{\"Theme\":\"Light\",\"Realtime\":{\"GuidanceExpanded\":" + expanded + ",\"TargetLanguage\":\"JA\"}}");

        Assert.Equal("Light", settings.Theme);
        Assert.Equal("JA", settings.Realtime.TargetLanguage);
        Assert.DoesNotContain("GuidanceExpanded", SettingsService.Serialize(settings));
    }

    /// <summary>
    /// A settings file from before 標記 remembered anything opens on the panel's old fixed start:
    /// black, every slider halfway, 方框 behind 形狀.
    /// </summary>
    [Fact]
    public void ASettingsFileWithoutAnnotation_OpensOnTheOldDefaults()
    {
        var annotation = SettingsService.Parse("""{"Capture":{"VerticalText":true}}""").Capture.Annotation;

        Assert.Equal("#000000", annotation.Color);
        Assert.Equal(0.5, annotation.PenThickness);
        Assert.Equal(0.5, annotation.HighlighterThickness);
        Assert.Equal(0.5, annotation.ShapeThickness);
        Assert.Equal(0.5, annotation.EraserSize);
        Assert.Equal(0.5, annotation.HighlighterOpacity);
        Assert.Equal(AnnotationTool.Rectangle, annotation.Shape);
    }

    /// <summary>The 標記 preferences survive the file, the shape by name.</summary>
    [Fact]
    public void AnnotationPreferences_RoundTripThroughTheFile()
    {
        var written = new AppSettings();
        written.Capture.Annotation.Color = "#EF4444";
        written.Capture.Annotation.PenThickness = 0.2;
        written.Capture.Annotation.HighlighterThickness = 0.9;
        written.Capture.Annotation.HighlighterOpacity = 0.3;
        written.Capture.Annotation.Shape = AnnotationTool.Ellipse;

        var json = SettingsService.Serialize(written);
        Assert.Contains("Ellipse", json);

        var annotation = SettingsService.Parse(json).Capture.Annotation;
        Assert.Equal("#EF4444", annotation.Color);
        Assert.Equal(0.2, annotation.PenThickness);
        Assert.Equal(0.9, annotation.HighlighterThickness);
        Assert.Equal(0.5, annotation.ShapeThickness);
        Assert.Equal(0.3, annotation.HighlighterOpacity);
        Assert.Equal(AnnotationTool.Ellipse, annotation.Shape);
    }

    /// <summary>A settings file from before 文字 (#362) opens on 24 px with all four styles off.</summary>
    [Fact]
    public void ASettingsFileWithoutTextPreferences_OpensOnTheTextDefaults()
    {
        var annotation = SettingsService.Parse("""{"Capture":{"Annotation":{"Color":"#EF4444"}}}""").Capture.Annotation;

        Assert.Equal(24, annotation.TextSize);
        Assert.False(annotation.TextBold);
        Assert.False(annotation.TextItalic);
        Assert.False(annotation.TextUnderline);
        Assert.False(annotation.TextStrikethrough);
    }

    /// <summary>The 文字 size and styles are remembered between captures (#362).</summary>
    [Fact]
    public void TextPreferences_RoundTripThroughTheFile()
    {
        var written = new AppSettings();
        written.Capture.Annotation.TextSize = 137;
        written.Capture.Annotation.TextBold = true;
        written.Capture.Annotation.TextStrikethrough = true;

        var annotation = SettingsService.Parse(SettingsService.Serialize(written)).Capture.Annotation;
        Assert.Equal(137, annotation.TextSize);
        Assert.True(annotation.TextBold);
        Assert.False(annotation.TextItalic);
        Assert.False(annotation.TextUnderline);
        Assert.True(annotation.TextStrikethrough);
    }
}
