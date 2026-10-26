using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The four prompts a profile used to hold, carried into the one box of extra instructions.
/// </summary>
public class OpenAiPromptMigrationTests
{
    private const string ZhHant = "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n";
    private const string ZhHans = "将以下文本翻译为{target_name}，注意只需要输出翻译后的结果，不要额外解释：\n\n";
    private const string English =
        "Translate the following text into {target_name}. Note that you should only " +
        "output the translated result without any additional explanation:\n\n";

    /// <summary>
    /// A profile nobody edited comes out empty, whichever interface it was added under.
    /// </summary>
    /// <remarks>
    /// Each language's own wording, because a profile is a copy of whichever built-in prompt was
    /// showing; with the blank line it ends with, without it, and with the line breaks a TextBox
    /// writes, because none of those is something the user chose to say.
    /// </remarks>
    [Theory]
    [InlineData(ZhHant)]
    [InlineData(ZhHans)]
    [InlineData(English)]
    [InlineData("將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：")]
    [InlineData("將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\r\n\r\n")]
    [InlineData("Translate the following text into {target}. Note that you should only " +
        "output the translated result without any additional explanation:\n\n")]
    [InlineData("将以下文本翻译为{target}，注意只需要输出翻译后的结果，不要额外解释：")]
    public void TheBuiltInWording_IsDropped(string builtIn)
    {
        Assert.Equal("", OpenAiPromptMigration.ExtraInstructionsFrom("", builtIn, "", builtIn));
    }

    /// <summary>
    /// The two cases can hold different interface languages' wordings and still both be built in.
    /// </summary>
    [Fact]
    public void BuiltInWordingsFromDifferentInterfaces_AreAllDropped()
    {
        Assert.Equal("", OpenAiPromptMigration.ExtraInstructionsFrom("", ZhHant, "", English));
    }

    [Fact]
    public void EmptyAndBlankBoxes_AreDropped()
    {
        Assert.Equal("", OpenAiPromptMigration.ExtraInstructionsFrom("", "  ", "\r\n", null));
    }

    [Fact]
    public void ACustomSystemPrompt_IsCarriedAndTheBuiltInUserPromptBesideItIsNot()
    {
        Assert.Equal("你是遊戲翻譯。",
            OpenAiPromptMigration.ExtraInstructionsFrom("你是遊戲翻譯。", ZhHant, "", ZhHant));
    }

    /// <summary>
    /// The same text in both cases is carried once: it was one wish written twice.
    /// </summary>
    [Fact]
    public void TheSameTextInBothCases_IsCarriedOnce()
    {
        Assert.Equal("用口語。",
            OpenAiPromptMigration.ExtraInstructionsFrom("用口語。", "", "用口語。\r\n", ""));
    }

    /// <summary>
    /// Different texts are kept in the order 自動 system, 自動 user, 指定語言 system, 指定語言 user,
    /// a blank line apart, with the line breaks made bare.
    /// </summary>
    [Fact]
    public void DifferentTexts_AreJoinedInOrderWithBareLineBreaks()
    {
        var extra = OpenAiPromptMigration.ExtraInstructionsFrom(
            "A1\r\nA2", "翻成{target_name}：\n\n", "B", "C");

        Assert.Equal("A1\nA2\n\n翻成{target_name}：\n\nB\n\nC", extra);
    }

    /// <summary>
    /// A profile saved with the old prompts comes back with them carried in and the notice owed.
    /// </summary>
    [Fact]
    public void AProfileWithOldPrompts_IsMigratedWhenRead()
    {
        var settings = SettingsService.Parse("""
            {
              "OpenAi": {
                "Profiles": [
                  {
                    "Id": "a", "Name": "改過的", "Model": "m",
                    "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "Translate into {target}:\n\n" },
                    "Explicit": { "SystemPrompt": "Be casual.", "UserPrompt": "Translate into {target}:\n\n" }
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
        Assert.Equal("Be casual.\n\nTranslate into {target}:", edited.ExtraInstructions);
        Assert.True(edited.ShowMigrationNotice);

        var untouched = settings.OpenAi.Profiles[1];
        Assert.Equal("", untouched.ExtraInstructions);
        Assert.False(untouched.ShowMigrationNotice);
    }

    /// <summary>
    /// A profile that already has the new box is left as it is, old prompts or not.
    /// </summary>
    /// <remarks>
    /// The key is what says the profile was saved by a build that knows the box — including one the
    /// user emptied on purpose, which must not fill up again from prompts still in the file.
    /// </remarks>
    [Fact]
    public void AProfileThatHasTheNewBox_IsNotMigratedAgain()
    {
        var settings = SettingsService.Parse("""
            {
              "OpenAi": {
                "Profiles": [
                  {
                    "Id": "a", "Name": "n", "Model": "m",
                    "ExtraInstructions": "",
                    "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "" }
                  }
                ]
              }
            }
            """);

        var profile = Assert.Single(settings.OpenAi.Profiles);
        Assert.Equal("", profile.ExtraInstructions);
        Assert.False(profile.ShowMigrationNotice);
    }

    /// <summary>
    /// What was carried in survives being written and read again, notice and all.
    /// </summary>
    [Fact]
    public void AMigratedProfile_RoundTripsThroughTheFile()
    {
        var settings = SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "" } } ] } }
            """);

        var reread = Assert.Single(SettingsService.Parse(SettingsService.Serialize(settings)).OpenAi.Profiles);

        Assert.Equal("Be casual.", reread.ExtraInstructions);
        Assert.True(reread.ShowMigrationNotice);
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
