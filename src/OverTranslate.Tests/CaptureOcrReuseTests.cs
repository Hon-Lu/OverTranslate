using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A capture's recognition is handed back only when everything recognition is given is unchanged.
/// </summary>
public class CaptureOcrReuseTests
{
    private static readonly List<OcrTextBlock> Found = [new("HELLO", new Rect(0, 0, 10, 10))];

    private static readonly object Capture = new();
    private static readonly Rect Region = new(100, 200, 300, 40);

    private static CaptureOcrReuse Remembering(bool verticalText = false,
        CaptureLayoutMode layoutMode = CaptureLayoutMode.General)
    {
        var reuse = new CaptureOcrReuse();
        reuse.Remember(Capture, Region, "ja", verticalText, layoutMode, Found);
        return reuse;
    }

    [Fact]
    public void TheSameCropAskedTheSameWay_IsReused()
    {
        Assert.True(Remembering().TryGet(Capture, Region, "ja", false, CaptureLayoutMode.General, out var blocks));
        Assert.Same(Found, blocks);
    }

    /// <summary>
    /// 複製原文 hands the frame back and the next 翻譯 cuts it again: a new bitmap, the same pixels.
    /// </summary>
    [Fact]
    public void TheSameRegionCutAgain_IsReused()
    {
        var again = new Rect(Region.X, Region.Y, Region.Width, Region.Height);

        Assert.True(Remembering().TryGet(Capture, again, "ja", false, CaptureLayoutMode.General, out _));
    }

    /// <summary>The language is compared as recognition receives it.</summary>
    [Fact]
    public void TheSameLanguageWrittenDifferently_IsReused()
    {
        Assert.True(Remembering().TryGet(Capture, Region, " JA ", false, CaptureLayoutMode.General, out _));
    }

    [Fact]
    public void AnotherRegion_IsRecognisedAgain()
    {
        var moved = new Rect(Region.X + 1, Region.Y, Region.Width, Region.Height);

        Assert.False(Remembering().TryGet(Capture, moved, "ja", false, CaptureLayoutMode.General, out var blocks));
        Assert.Empty(blocks);
    }

    /// <summary>Another capture is another screenshot, whatever region of it is asked about.</summary>
    [Fact]
    public void AnotherCapture_IsRecognisedAgain()
    {
        Assert.False(Remembering().TryGet(new object(), Region, "ja", false, CaptureLayoutMode.General, out _));
    }

    [Fact]
    public void AnotherSourceLanguage_IsRecognisedAgain()
    {
        Assert.False(Remembering().TryGet(Capture, Region, "en", false, CaptureLayoutMode.General, out _));
    }

    [Fact]
    public void AnotherOrientation_IsRecognisedAgain()
    {
        Assert.False(Remembering().TryGet(Capture, Region, "ja", true, CaptureLayoutMode.General, out _));
    }

    [Fact]
    public void AnotherLayoutMode_IsRecognisedAgain()
    {
        Assert.False(Remembering().TryGet(Capture, Region, "ja", false, CaptureLayoutMode.Interface, out _));
    }

    /// <summary>Vertical recognition never looks at the mode, so changing it changes nothing.</summary>
    [Fact]
    public void AnotherLayoutMode_OnVerticalText_IsReused()
    {
        var reuse = Remembering(verticalText: true);

        Assert.True(reuse.TryGet(Capture, Region, "ja", true, CaptureLayoutMode.Interface, out _));
    }

    [Fact]
    public void AfterClear_NothingIsReused()
    {
        var reuse = Remembering();
        reuse.Clear();

        Assert.False(reuse.TryGet(Capture, Region, "ja", false, CaptureLayoutMode.General, out _));
    }

    /// <summary>Nothing found sends the box back to be redrawn; there is nothing worth keeping.</summary>
    [Fact]
    public void ARecognitionThatFoundNothing_IsNotKept()
    {
        var reuse = Remembering();
        reuse.Remember(Capture, Region, "ja", false, CaptureLayoutMode.General, []);

        Assert.False(reuse.TryGet(Capture, Region, "ja", false, CaptureLayoutMode.General, out _));
    }
}
