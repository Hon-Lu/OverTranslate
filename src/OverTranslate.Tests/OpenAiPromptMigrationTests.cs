using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A profile saved with the four old prompts: put on the built-in instruction when none of them was
/// the user's own, and on its own prompts, kept as they were, when any was.
/// </summary>
public class OpenAiPromptMigrationTests
{
    private const string ZhHant = "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n";
    private const string ZhHans = "将以下文本翻译为{target_name}，注意只需要输出翻译后的结果，不要额外解释：\n\n";
    private const string English =
        "Translate the following text into {target_name}. Note that you should only " +
        "output the translated result without any additional explanation:\n\n";

    private static OpenAiPromptPair Pair(string system, string user) => new() { SystemPrompt = system, UserPrompt = user };

    /// <summary>
    /// Each language's own wording, because a profile is a copy of whichever built-in prompt was
    /// showing; with the blank line it ends with, without it, with the line breaks a TextBox writes,
    /// and in the older placeholder spelling — none of those is something the user chose to say.
    /// </summary>
    [Theory]
    [InlineData(ZhHant)]
    [InlineData(ZhHans)]
    [InlineData(English)]
    [InlineData("將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：")]
    [InlineData("將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\r\n\r\n")]
    [InlineData("Translate the following text into {target}. Note that you should only " +
        "output the translated result without any additional explanation:\n\n")]
    [InlineData("将以下文本翻译为{target}，注意只需要输出翻译后的结果，不要额外解释：")]
    public void TheBuiltInWording_IsNotTheUsersOwn(string builtIn)
    {
        Assert.Null(OpenAiPromptMigration.Kept(Pair("", builtIn), Pair("", builtIn), automaticSource: true));
    }

    [Fact]
    public void BuiltInWordingsFromDifferentInterfaces_AreNeitherTheUsersOwn()
    {
        Assert.Null(OpenAiPromptMigration.Kept(Pair("", ZhHant), Pair("", English), automaticSource: false));
    }

    [Fact]
    public void EmptyAndBlankBoxes_AreNotTheUsersOwn()
    {
        Assert.Null(OpenAiPromptMigration.Kept(Pair("", "  "), Pair("\r\n", ""), automaticSource: true));
    }

    /// <summary>A custom system prompt makes the pair the user's own, built-in user half and all.</summary>
    [Fact]
    public void ACustomSystemPrompt_KeepsThePairAsItWas()
    {
        var kept = OpenAiPromptMigration.Kept(Pair("你是遊戲翻譯。", ZhHant), Pair("", ZhHant), automaticSource: false);

        Assert.NotNull(kept);
        Assert.Equal("你是遊戲翻譯。", kept.SystemPrompt);
        Assert.Equal(ZhHant, kept.UserPrompt);
    }

    /// <summary>Only one pair the user's own: that one, whichever the source language is.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyOnePairTheUsersOwn_IsTheOneKept(bool automaticSource)
    {
        var own = Pair("", "翻成{target_name}：\n\n");

        Assert.Same(own, OpenAiPromptMigration.Kept(Pair("", ZhHant), own, automaticSource));
        Assert.Same(own, OpenAiPromptMigration.Kept(own, Pair("", ZhHant), automaticSource));
    }

    /// <summary>Both the user's own and different: the one the source language was sending.</summary>
    [Theory]
    [InlineData(true, "自動")]
    [InlineData(false, "指定")]
    public void BothPairsTheUsersOwn_TheSourceLanguagePicks(bool automaticSource, string expected)
    {
        var kept = OpenAiPromptMigration.Kept(Pair("自動", ""), Pair("指定", ""), automaticSource);

        Assert.Equal(expected, kept?.SystemPrompt);
    }

    /// <summary>
    /// Read from the file: the edited profile goes on its own prompts exactly as written, the
    /// untouched one on the built-in instruction with nothing extra.
    /// </summary>
    [Fact]
    public void ProfilesWithOldPrompts_GetAModeWhenRead()
    {
        var settings = SettingsService.Parse("""
            {
              "SourceLanguage": "AUTO",
              "OpenAi": {
                "Profiles": [
                  {
                    "Id": "a", "Name": "改過的", "Model": "m",
                    "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "Translate into {target}:\n\n" },
                    "Explicit": { "SystemPrompt": "Be formal.", "UserPrompt": "" }
                  },
                  {
                    "Id": "b", "Name": "沒改的", "Model": "m",
                    "Auto": { "SystemPrompt": "", "UserPrompt": "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n" },
                    "Explicit": { "SystemPrompt": "", "UserPrompt": "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n" }
                  }
                ]
              }
            }
            """);

        var edited = settings.OpenAi.Profiles[0];
        Assert.Equal(OpenAiTranslationMode.PerSegment, edited.Mode);
        Assert.Equal("Be casual.", edited.Prompts.SystemPrompt);
        Assert.Equal("Translate into {target}:\n\n", edited.Prompts.UserPrompt);
        Assert.Equal("", edited.ExtraInstructions);

        var untouched = settings.OpenAi.Profiles[1];
        Assert.Equal(OpenAiTranslationMode.Batch, untouched.Mode);
        Assert.True(untouched.Prompts.IsEmpty);
        Assert.Equal("", untouched.ExtraInstructions);
    }

    [Fact]
    public void BothPairsTheUsersOwn_AChosenSourceLanguageKeepsTheChosenPair()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "SourceLanguage": "JA", "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "Auto": { "SystemPrompt": "自動", "UserPrompt": "" },
              "Explicit": { "SystemPrompt": "指定", "UserPrompt": "" } } ] } }
            """).OpenAi.Profiles);

        Assert.Equal(OpenAiTranslationMode.PerSegment, profile.Mode);
        Assert.Equal("指定", profile.Prompts.SystemPrompt);
    }

    /// <summary>
    /// A profile that has a mode is left as it is, old prompts or not: it was saved by a build that
    /// knows modes, and the user may have chosen the built-in instruction over them.
    /// </summary>
    [Fact]
    public void AProfileThatHasAMode_IsNotMigratedAgain()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m", "Mode": "Batch",
              "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "" } } ] } }
            """).OpenAi.Profiles);

        Assert.Equal(OpenAiTranslationMode.Batch, profile.Mode);
        Assert.True(profile.Prompts.IsEmpty);
    }

    /// <summary>The mode and the prompts survive being written and read again.</summary>
    [Fact]
    public void AMigratedProfile_RoundTripsThroughTheFile()
    {
        var settings = SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "" } } ] } }
            """);

        var json = SettingsService.Serialize(settings);
        var reread = Assert.Single(SettingsService.Parse(json).OpenAi.Profiles);

        Assert.Contains("\"Mode\": \"PerSegment\"", json);
        Assert.Equal(OpenAiTranslationMode.PerSegment, reread.Mode);
        Assert.Equal("Be casual.", reread.Prompts.SystemPrompt);
    }

    [Fact]
    public void ANewProfile_IsBatch()
    {
        Assert.Equal(OpenAiTranslationMode.Batch, new OpenAiModelProfile().Mode);
    }

    /// <summary>
    /// A profile with nothing to say about reasoning does not send it, and has none pre-filled.
    /// </summary>
    [Fact]
    public void ReasoningEffort_IsOffAndPrefilledWithNone()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m" } ] } }
            """).OpenAi.Profiles);

        Assert.False(profile.ReasoningEffortEnabled);
        Assert.Equal("none", profile.ReasoningEffort);
        Assert.Equal(["none", "minimal", "low", "medium", "high"], OpenAiModelProfile.ReasoningEfforts);
    }

    [Fact]
    public void ReasoningEffort_IsReadBack()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "ReasoningEffortEnabled": true, "ReasoningEffort": "low" } ] } }
            """).OpenAi.Profiles);

        Assert.True(profile.ReasoningEffortEnabled);
        Assert.Equal("low", profile.ReasoningEffort);
    }
}
