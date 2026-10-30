using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What a stored OpenAI-compatible profile opens as: the prompts and options of earlier builds are
/// not carried over, and saving drops them.
/// </summary>
public class OpenAiProfileReadTests
{
    /// <summary>
    /// The old Auto / Explicit prompt pairs, the user's own words included, leave every profile on
    /// the default template, and are gone from the file once it is saved.
    /// </summary>
    [Fact]
    public void OldPromptPairs_AreNotCarriedOver()
    {
        var settings = SettingsService.Parse("""
            {
              "SourceLanguage": "AUTO",
              "OpenAi": {
                "Profiles": [
                  {
                    "Id": "a", "Name": "改過的", "Model": "m",
                    "Auto": { "SystemPrompt": "Be casual.", "UserPrompt": "Translate into {target}:\n\n" },
                    "Explicit": { "SystemPrompt": "Be formal.", "UserPrompt": "指定：" }
                  },
                  {
                    "Id": "b", "Name": "沒改的", "Model": "m",
                    "Auto": { "SystemPrompt": "", "UserPrompt": "將以下文本翻譯為{target_name}，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n" }
                  }
                ]
              }
            }
            """);

        Assert.Equal(["", ""], settings.OpenAi.Profiles.Select(profile => profile.Template));

        var json = SettingsService.Serialize(settings);
        Assert.DoesNotContain("\"Auto\":", json);
        Assert.DoesNotContain("\"Explicit\":", json);
        Assert.DoesNotContain("Prompt\"", json);
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
