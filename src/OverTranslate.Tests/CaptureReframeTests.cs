using System.Windows;
using System.Xml.Linq;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 編輯框選範圍 on the capture toolbar: the translation held aside while the box is edited, the
/// pill that carries the button, and its copy.
/// </summary>
/// <remarks>
/// The point of holding the translation is that 翻譯 with nothing changed puts it straight back —
/// no recognition, no request to the engine. MainWindow returns before either whenever
/// <see cref="CaptureReframe.Take"/> hands something back, so what is pinned here is exactly when
/// it does.
/// </remarks>
public class CaptureReframeTests
{
    private static readonly Rect Box = new(100, 200, 640, 360);

    private static CaptureReframe.Inputs Inputs(
        Rect? box = null, string source = "JA", string target = "ZH-HANT", bool vertical = false,
        TranslationProvider provider = TranslationProvider.Google) =>
        new(box ?? Box, source, target, vertical, provider);

    private static CaptureResult Translated() =>
        CaptureResult.Empty("ZH-HANT") with { SourceLang = "JA" };

    [Fact]
    public void NothingChangedGivesTheHeldTranslationBack()
    {
        var reframe = new CaptureReframe();
        var held = Translated();
        reframe.Hold(Inputs(), held);

        Assert.Same(held, reframe.Take(Inputs()));
    }

    private static CaptureReframe.Inputs Changed(string what) => what switch
    {
        "box moved"    => Inputs(box: new Rect(101, 200, 640, 360)),
        "box resized"  => Inputs(box: new Rect(100, 200, 641, 360)),
        "source"       => Inputs(source: "EN"),
        "target"       => Inputs(target: "EN"),
        "direction"    => Inputs(vertical: true),
        "service"      => Inputs(provider: TranslationProvider.Bing),
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [InlineData("box moved")]
    [InlineData("box resized")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("direction")]
    [InlineData("service")]
    public void AnyChangedInputMeansTranslatingAgain(string what)
    {
        var reframe = new CaptureReframe();
        reframe.Hold(Inputs(), Translated());

        var changed = Changed(what);
        Assert.Null(reframe.Take(changed));
    }

    [Fact]
    public void ATranslationThatRunsDropsTheHeldOne()
    {
        // The new translation replaces it: changing the box back afterwards must not bring the old
        // one back over the new one.
        var reframe = new CaptureReframe();
        reframe.Hold(Inputs(), Translated());

        reframe.Take(Inputs(source: "EN"));

        Assert.False(reframe.IsHolding);
        Assert.Null(reframe.Take(Inputs()));
    }

    [Fact]
    public void ARestoreIsTakenOnlyOnce()
    {
        var reframe = new CaptureReframe();
        reframe.Hold(Inputs(), Translated());

        Assert.NotNull(reframe.Take(Inputs()));
        Assert.Null(reframe.Take(Inputs()));
    }

    [Fact]
    public void EditingLeavesNothingForCopySpeakFilterOrBadgeToRead()
    {
        // What the capture holds while its box is being edited.
        var editing = CaptureResult.Empty("ZH-HANT");

        Assert.False(editing.HasTranslation);
        Assert.Empty(editing.OcrBlocks);          // speak reads these
        Assert.Empty(editing.TranslatedBlocks);   // copy translation reads these
        Assert.Null(editing.Filter);              // 過濾數字與縮寫 does nothing without one
        Assert.Null(editing.EngineUsage);         // the backup badge
        // And with no translation on screen, copy recognises the box as it is now.
        Assert.Equal(CopyTextKind.RecognizeSource, ToolbarWindow.ResolveCopyTextKind(false, true));
    }

    [Fact]
    public void TheTranslatedPillIsSplitAndStaysSplitWhileTranslatingAgain()
    {
        var done = ToolbarWindow.ResolveTranslateButton(hasTranslated: true, busy: false, "S.Toolbar.Translating");
        var again = ToolbarWindow.ResolveTranslateButton(hasTranslated: true, busy: true, "S.Toolbar.Translating");

        Assert.True(done.Split);
        Assert.Equal("S.Toolbar.Retranslate", done.LabelKey);
        Assert.Equal(ToolbarWindow.TranslateGlyphKind.Again, done.Glyph);
        Assert.False(done.Spinning);

        Assert.True(again.Split);
        Assert.Equal("S.Toolbar.Translating", again.LabelKey);
        Assert.True(again.Spinning);
    }

    [Fact]
    public void EditingAndTheFirstTranslationAreOnePill()
    {
        var idle = ToolbarWindow.ResolveTranslateButton(hasTranslated: false, busy: false, "S.Toolbar.Translating");
        var first = ToolbarWindow.ResolveTranslateButton(hasTranslated: false, busy: true, "S.Toolbar.Translating");

        Assert.False(idle.Split);
        Assert.Equal("S.Toolbar.Translate", idle.LabelKey);
        Assert.Equal(ToolbarWindow.TranslateGlyphKind.Translate, idle.Glyph);

        // Running shows the turning arrow, the first run too.
        Assert.False(first.Split);
        Assert.Equal(ToolbarWindow.TranslateGlyphKind.Again, first.Glyph);
        Assert.True(first.Spinning);
    }

    [Fact]
    public void TheCopyIsTheSignedOffWording()
    {
        var strings = XDocument.Load(Path.Combine(
            StringsParityTests.ProjectDirectory(), "Resources", "Strings.zh-Hant.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string Get(string key) => strings.Root!.Elements().Single(e => (string?)e.Attribute(x + "Key") == key).Value;

        Assert.Equal("編輯框選範圍", Get("S.Toolbar.EditSelection"));
        Assert.Equal("返回框選畫面進行修改", Get("S.Toolbar.EditSelectionHint"));
    }
}
