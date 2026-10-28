using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A profile saved with the four old prompts: the pair that was being sent is picked as it always
/// was, and its user half, when it is the user's own, becomes the template as it was; otherwise the
/// template is the application's.
/// </summary>
public class OpenAiPromptMigrationTests
{
    private const string ZhHant = "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n";
    private const string ZhHans = "将以下文本翻译为{target_name}，注意只需要输出翻译后的结果，不要额外解释：\n\n";
    private const string English =
        "Translate the following text into {target_name}. Note that you should only " +
        "output the translated result without any additional explanation:\n\n";

    private static OpenAiPromptMigration.OldPrompts Pair(string system, string user) => new(system, user);

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

        Assert.Equal(expected, kept?.System);
    }

    /// <summary>
    /// The three cases: the user's own user half is the template, as it was, blank line and all —
    /// only its line breaks made bare; a pair whose only own half was the system message, and no
    /// pair of the user's at all, leave the application's.
    /// </summary>
    [Fact]
    public void TheTemplate_IsTheUsersOwnUserHalfOrNothing()
    {
        Assert.Equal("Translate into {target}:\n\n", OpenAiPromptMigration.Template(Pair("Be casual.", "Translate into {target}:\r\n\r\n")));
        Assert.Equal("", OpenAiPromptMigration.Template(Pair("你是遊戲翻譯。", ZhHant)));
        Assert.Equal("", OpenAiPromptMigration.Template(Pair("你是遊戲翻譯。", "")));
        Assert.Equal("", OpenAiPromptMigration.Template(null));
    }

    /// <summary>Read from the file: each profile gets the template its old prompts leave.</summary>
    [Fact]
    public void ProfilesWithOldPrompts_GetATemplateWhenRead()
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
                  },
                  {
                    "Id": "c", "Name": "只改系統", "Model": "m",
                    "Auto": { "SystemPrompt": "You are a game translator.", "UserPrompt": "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n" }
                  }
                ]
              }
            }
            """);

        Assert.Equal(["Translate into {target}:\n\n", "", ""], settings.OpenAi.Profiles.Select(profile => profile.Template));
    }

    [Fact]
    public void BothPairsTheUsersOwn_AChosenSourceLanguageKeepsTheChosenPair()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "SourceLanguage": "JA", "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "Auto": { "SystemPrompt": "", "UserPrompt": "自動：" },
              "Explicit": { "SystemPrompt": "", "UserPrompt": "指定：" } } ] } }
            """).OpenAi.Profiles);

        Assert.Equal("指定：", profile.Template);
    }

    /// <summary>
    /// A profile that has a template is left as it is, old prompts or not: it was saved by a build
    /// that knows templates, and the user may have chosen the application's over them.
    /// </summary>
    [Fact]
    public void AProfileThatHasATemplate_IsNotMigratedAgain()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m", "Template": "",
              "Auto": { "SystemPrompt": "", "UserPrompt": "Be casual." } } ] } }
            """).OpenAi.Profiles);

        Assert.Equal("", profile.Template);
    }

    /// <summary>The template survives being written and read again, and the old fields are not written.</summary>
    [Fact]
    public void AMigratedProfile_RoundTripsThroughTheFile()
    {
        var settings = SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "Auto": { "SystemPrompt": "", "UserPrompt": "Be casual: " } } ] } }
            """);

        var json = SettingsService.Serialize(settings);
        var reread = Assert.Single(SettingsService.Parse(json).OpenAi.Profiles);

        Assert.Contains("\"Template\": \"Be casual: \"", json);
        Assert.DoesNotContain("\"UserPrompt\"", json);
        Assert.DoesNotContain("\"Mode\"", json);
        Assert.Equal("Be casual: ", reread.Template);
    }

    [Fact]
    public void ANewProfile_IsOnTheApplicationsTemplate()
    {
        Assert.Equal("", new OpenAiModelProfile().Template);
    }

    /// <summary>
    /// A profile with nothing to say about thinking leaves 關閉思考 unticked.
    /// </summary>
    [Fact]
    public void ThinkingOff_IsOffByDefault()
    {
        var profile = Assert.Single(SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m" } ] } }
            """).OpenAi.Profiles);

        Assert.False(profile.ThinkingOff);
    }

    [Fact]
    public void ThinkingOff_IsReadBack()
    {
        var settings = SettingsService.Parse("""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m", "ThinkingOff": true } ] } }
            """);

        Assert.True(Assert.Single(settings.OpenAi.Profiles).ThinkingOff);
        Assert.True(Assert.Single(SettingsService.Parse(SettingsService.Serialize(settings)).OpenAi.Profiles).ThinkingOff);
    }

    /// <summary>
    /// The effort list and its switch never shipped, so whatever they say opens unticked — "none"
    /// included — and saving drops them.
    /// </summary>
    [Theory]
    [InlineData("true", "\"none\"")]
    [InlineData("true", "\"low\"")]
    [InlineData("true", "\"high\"")]
    [InlineData("false", "\"none\"")]
    [InlineData("true", "\"\"")]
    public void TheUnshippedReasoningEffort_OpensAsThinkingLeftOn(string enabled, string effort)
    {
        var settings = SettingsService.Parse($$"""
            { "OpenAi": { "Profiles": [ { "Id": "a", "Name": "n", "Model": "m",
              "ReasoningEffortEnabled": {{enabled}}, "ReasoningEffort": {{effort}} } ] } }
            """);

        var profile = Assert.Single(settings.OpenAi.Profiles);
        Assert.False(profile.ThinkingOff);

        settings.OpenAi.SelectedProfileId = "a";
        Assert.Null(OpenAiCompatibleProvider.FromSettings(settings).ReasoningEffort);

        Assert.DoesNotContain("ReasoningEffort", SettingsService.Serialize(settings));
    }
}
