using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The 一般 / 介面 switch on the capture toolbar: its product copy, and the wiring in the markup
/// that decides which mode a press means.
/// </summary>
/// <remarks>
/// <para>The tooltips are product copy, written to be read by someone deciding which half to press
/// and signed off as written. They are the kind of string a later edit improves the wording of
/// without anyone noticing the meaning moved.</para>
///
/// <para>Two phrases in particular. "依閱讀順序整理文字" says something specific and bounded here —
/// a group's lines are joined into one passage and re-broken, see design.md §1.3 — and is one small
/// rewrite away from promising to reorder panels. And the interface line says "採用<b>較嚴格的</b>
/// 文字合併判斷": an earlier draft read "提高文字合併判斷", which a reader takes to mean "merges
/// more readily" — the opposite of what that mode does. Pinned verbatim for those reasons.</para>
/// </remarks>
public class CaptureLayoutModeToolbarTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Toolbar() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Capture", "ToolbarWindow.xaml"));

    private static string TraditionalChinese(string key)
    {
        var path = Path.Combine(StringsParityTests.ProjectDirectory(), "Resources", "Strings.zh-Hant.xaml");
        return XDocument.Load(path).Root!
            .Elements()
            .Single(element => (string?)element.Attribute(X + "Key") == key)
            .Value;
    }

    [Fact]
    public void TheLabelsAreTheOnesTheProductCopySays()
    {
        Assert.Equal("一般", TraditionalChinese("S.Toolbar.LayoutModeGeneral"));
        Assert.Equal("介面", TraditionalChinese("S.Toolbar.LayoutModeInterface"));
    }

    [Fact]
    public void TheGeneralTooltipIsTheProductCopyVerbatim()
    {
        Assert.Equal(
            "依閱讀順序整理文字，並適度合併相鄰內容，適合文章、漫畫、對話與大多數一般情境。",
            TraditionalChinese("S.Toolbar.LayoutModeGeneralHint"));
    }

    [Fact]
    public void TheInterfaceTooltipIsTheProductCopyVerbatim()
    {
        Assert.Equal(
            "優先保留原有排列與區塊分離，並採用較嚴格的文字合併判斷，適合遊戲 UI、選單與多欄介面。",
            TraditionalChinese("S.Toolbar.LayoutModeInterfaceHint"));
    }

    /// <summary>
    /// 一般 is the left half of the switch, and it is the half that opens checked.
    /// </summary>
    /// <remarks>
    /// It is the default mode, and a two-way switch whose default is the right-hand half reads
    /// backwards — the eye takes the left one as the ordinary answer. The columns are checked as
    /// well as the checked state, because either one alone can be right while the pair is wrong:
    /// the pill's travel is computed from which column the chosen half sits in.
    /// </remarks>
    [Fact]
    public void TheDefaultModeIsTheLeftHalfOfTheSwitch()
    {
        var segments = Toolbar()
            .Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .ToDictionary(element => (string)element.Attribute(X + "Name")!);

        Assert.Equal("0", (string?)segments["GeneralModeSeg"].Attribute("Grid.Column"));
        Assert.Equal("1", (string?)segments["InterfaceModeSeg"].Attribute("Grid.Column"));
        Assert.Equal("True", (string?)segments["GeneralModeSeg"].Attribute("IsChecked"));
        Assert.Null(segments["InterfaceModeSeg"].Attribute("IsChecked"));
    }

    /// <summary>
    /// The two halves are one radio group, and it is not the direction switch's group.
    /// </summary>
    /// <remarks>
    /// Two segmented switches now sit side by side on the same bar. Sharing a GroupName would make
    /// them one four-way control: choosing 直排 would silently un-choose 漫畫・文章, and the failure
    /// would look like the mode not being remembered rather than like a name collision.
    /// </remarks>
    [Fact]
    public void TheTwoSwitchesOnTheBarAreSeparateGroups()
    {
        var groups = Toolbar()
            .Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .ToDictionary(
                element => (string)element.Attribute(X + "Name")!,
                element => (string)element.Attribute("GroupName")!);

        Assert.Equal(groups["InterfaceModeSeg"], groups["GeneralModeSeg"]);
        Assert.Equal(groups["HorizontalSeg"], groups["VerticalSeg"]);
        Assert.NotEqual(groups["InterfaceModeSeg"], groups["HorizontalSeg"]);
    }
}
