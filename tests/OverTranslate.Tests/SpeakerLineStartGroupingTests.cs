using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class SpeakerLineStartGroupingTests
{
    [Theory]
    [InlineData("[21:37] Alice: another message here")]
    [InlineData("[GLOBAL] Xa-mul: I mean hes above water")]
    [InlineData("IGLoBAL1 Xa-mul: And I think like 34-40")]
    [InlineData("@alice: another message here")]
    [InlineData("Aa119119: 酸黃瓜先生 不要上網查")]
    public void ChatEntrySetSolidUnderTheOneAbove_StaysApartOnTheLivePanelPath(string next)
    {
        var blocks = new[] { Row("[21:35] Bob: the first message in the log", 0), Row(next, 26) };

        Assert.Equal(2, OcrTextBlockGrouper.Group(blocks, GroupingProfile.Realtime).Count);
        Assert.Single(OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface));
    }

    [Fact]
    public void BareSpeakerLabel_KeepsItsMessageOnItsOwnRows()
    {
        var blocks = new[] { Row("[GLOBAL] Conor_Braxton:", 0), Row("feels good making over 1m off a 33k rental ship", 26) };

        Assert.Equal(2, OcrTextBlockGrouper.Group(blocks, GroupingProfile.Realtime).Count);
    }

    [Fact]
    public void WrappedSentence_StillJoinsOnTheLivePanelPath()
    {
        var blocks = new[]
        {
            Row("You have items remaining in Temporary Quest", 0),
            Row("Storage and should move them to another one", 26),
        };

        Assert.Single(OcrTextBlockGrouper.Group(blocks, GroupingProfile.Realtime));
    }

    [Theory]
    [InlineData("https://translate.google.com")]
    [InlineData("flex: 1 30px;")]
    [InlineData("and continues with some ordinary prose")]
    public void OrdinaryLines_DoNotOpenLikeASpeaker(string text) =>
        Assert.False(SpeakerLineStart.Opens(text));

    private static OcrTextBlock Row(string text, double y) =>
        new(text, new Rect(0, y, 350, 24), LayoutBounds: new Rect(0, y, 350, 24));
}
