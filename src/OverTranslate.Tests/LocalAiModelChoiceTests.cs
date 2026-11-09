using OverTranslate.Services.LocalAi;
using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LocalAiModelChoiceTests
{
    private static LocalAiModel Model(int vramMiB) => new(
        "m", "M", "M", false, "r", "m.gguf", "https://models.example/m.gguf", 1, new string('0', 64), vramMiB, "gemma-4", "S.X");

    private static readonly IReadOnlyList<LocalAiGpuDevice> Card = [new("GPU", 4096)];

    [Fact]
    public void AModelTooBigForTheCard_CannotBePicked_UntilItIsHere()
    {
        Assert.False(LocalAiSettingsPanel.CanChoose(Model(8192), Card, downloaded: false));
        Assert.True(LocalAiSettingsPanel.CanChoose(Model(8192), Card, downloaded: true));
    }

    [Fact]
    public void AModelThatFits_CanBePicked()
    {
        Assert.True(LocalAiSettingsPanel.CanChoose(Model(2048), Card, downloaded: false));
    }
}
