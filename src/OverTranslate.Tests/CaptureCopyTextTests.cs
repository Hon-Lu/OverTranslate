using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public class CaptureCopyTextTests
{
    [Fact]
    public void BeforeTranslation_CopyRecognizesSourceText() =>
        Assert.Equal(CopyTextKind.RecognizeSource, ToolbarWindow.ResolveCopyTextKind(false, true));

    [Fact]
    public void TranslationVisible_CopyUsesCachedTranslation() =>
        Assert.Equal(CopyTextKind.Translation, ToolbarWindow.ResolveCopyTextKind(true, true));

    [Fact]
    public void SourceVisible_CopyUsesCachedSourceText() =>
        Assert.Equal(CopyTextKind.Source, ToolbarWindow.ResolveCopyTextKind(true, false));
}
