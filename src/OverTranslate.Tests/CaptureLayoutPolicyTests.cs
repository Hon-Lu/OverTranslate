using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureLayoutPolicyTests
{
    [Theory]
    [InlineData(CaptureLayoutMode.General)]
    [InlineData(CaptureLayoutMode.Interface)]
    [InlineData((CaptureLayoutMode)99)]
    public void ApplicationIgnoresSavedOrRequestedMode(CaptureLayoutMode requested)
    {
        Assert.False(CaptureLayoutPolicy.IsModeSelectionAvailable);
        Assert.Equal(CaptureLayoutMode.General, CaptureLayoutPolicy.ForApplication(requested));
    }

    [Fact]
    public void InterfaceProfileRemainsAvailableForToolsAndTests()
    {
        Assert.Same(GroupingProfile.Interface, GroupingProfile.For(CaptureLayoutMode.Interface));
    }
}
