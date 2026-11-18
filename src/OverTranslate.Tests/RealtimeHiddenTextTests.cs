using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// What a realtime block leaves off the overlay. Hiding a group a reader wanted translated costs
/// them that translation; drawing one that says nothing covers the screen with boxes that repeat
/// it — so both directions are held down by the readings and translations that were measured.
/// </summary>
public class RealtimeHiddenTextTests
{
    [Theory]
    // Numbers, symbols and punctuation, as an interface prints them.
    [InlineData("12:30")]
    [InlineData("1/2")]
    [InlineData("+5")]
    [InlineData("108%")]
    [InlineData("2026/07/24 23:29")]
    [InlineData("31℃|25℃ 30%")]
    [InlineData("5597885710/85710")]
    [InlineData("② 1/ 2")]
    [InlineData("$21360")]
    [InlineData("11111")]
    [InlineData("1-1-1-1-1")]
    // A unit letter against a number, and a lone letter beside one: a button, a grade, an icon.
    [InlineData("7600R")]
    [InlineData("84s")]
    [InlineData("x 958")]
    [InlineData("×958")]
    [InlineData("ｘ９５８")]
    [InlineData("A 1/3")]
    [InlineData("89 F 120")]
    [InlineData("a 189")]
    [InlineData("00 O o")]
    // Level marks, as printed and as misread over a moving picture.
    [InlineData("Lv 15")]
    [InlineData("Lv15")]
    [InlineData("Lv.15")]
    [InlineData("Lvl 100 50")]
    [InlineData("Lvi 100 1")]
    [InlineData("LvL 15")]
    [InlineData("LxI 15")]
    // Symbols alone: a dialogue box reading 「……」 has nothing to translate.
    [InlineData("……")]
    [InlineData("！？")]
    [InlineData("★★★")]
    // Stat abbreviations, alone and with their numbers.
    [InlineData("HP")]
    [InlineData("MP")]
    [InlineData("SP")]
    [InlineData("EP")]
    [InlineData("DEX")]
    [InlineData("Def")]
    [InlineData("Hp :331")]
    [InlineData("HP:315")]
    [InlineData("HP2298/2692")]
    [InlineData("SP 76/80")]
    [InlineData("MP:145 MP")]
    [InlineData("HP/MP")]
    [InlineData("Dex / Int")]
    [InlineData("LV")]
    [InlineData("Lv")]
    [InlineData("Lv.")]
    [InlineData("60 FPS")]
    // A sort button.
    [InlineData("A-Z")]
    [InlineData("A-Z▼")]
    public void GroupsWithNothingToTranslateAreLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.CarriesNoWords(text));
        Assert.True(RealtimeHiddenText.NothingToTranslate(text));
    }

    [Theory]
    // The short words a reader may need translated, in every case they are printed in: every
    // engine translates them, and none is a listed abbreviation.
    [InlineData("GO")]
    [InlineData("Go")]
    [InlineData("go")]
    [InlineData("OK")]
    [InlineData("Dev")]
    [InlineData("DEV")]
    [InlineData("RUN")]
    [InlineData("BAG")]
    [InlineData("NEW")]
    [InlineData("SAVE")]
    [InlineData("LOAD")]
    [InlineData("AUTO")]
    [InlineData("No")]
    // Names: Io is a character, ④ is the element icon in front of it.
    [InlineData("Io")]
    [InlineData("④ lo")]
    [InlineData("EVA")]
    [InlineData("Ko")]
    // Abbreviations that are words, and lower case, which is prose or code.
    [InlineData("CON")]
    [InlineData("hp")]
    [InlineData("U.S.")]
    [InlineData("I/O")]
    // A word beside the numbers keeps the whole group.
    [InlineData("03:33 AM")]
    [InlineData("Lose 3 HP.")]
    [InlineData("x2 Speed")]
    [InlineData("No.1")]
    [InlineData("1st")]
    [InlineData("Lo 15")]
    [InlineData("Lap 3/5")]
    [InlineData("Sv 15")]
    // Any other script is words.
    [InlineData("是")]
    [InlineData("はい")]
    [InlineData("월")]
    [InlineData("5月")]
    [InlineData("15일")]
    [InlineData("3ユーステス")]
    [InlineData("Lv 100 ML50나루메아")]
    [InlineData("Lvl 15回")]
    [InlineData("「……っ」")]
    // A letter or a level mark with no number to belong to is a word too.
    [InlineData("A")]
    [InlineData("A B")]
    [InlineData("lv")]
    [InlineData("III")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GroupsWithWordsAreDrawn(string? text)
    {
        Assert.False(RealtimeHiddenText.CarriesNoWords(text));
        Assert.False(RealtimeHiddenText.NothingToTranslate(text));
    }

    [Theory]
    // Rings on a costume, a fence, a railing, a ruled line: the translators make words of these.
    [InlineData("OOOOOO")]
    [InlineData("OOOOO OOOOOO")]
    [InlineData("OOO")]
    [InlineData("oOo")]
    [InlineData("0O0")]
    [InlineData("lll")]
    [InlineData("Il1")]
    [InlineData("1l1l")]
    [InlineData("ーーー")]
    [InlineData("一一一")]
    [InlineData("口口口")]
    [InlineData("ロロロ")]
    public void PatternsReadAsLettersAreLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.IsShapeNoise(text));
        Assert.True(RealtimeHiddenText.NothingToTranslate(text));
    }

    [Theory]
    // Two families, too short, or a Roman numeral: words.
    [InlineData("Io")]
    [InlineData("lol")]
    [InlineData("Oil")]
    [InlineData("OO")]
    [InlineData("一一")]
    [InlineData("ーー")]
    [InlineData("II")]
    [InlineData("III")]
    [InlineData("ははは")]
    [InlineData("Hello")]
    public void WordsMadeOfLookAlikesAreNotPatterns(string text)
    {
        Assert.False(RealtimeHiddenText.IsShapeNoise(text));
    }

    private static OcrTextBlock Block(string text) => new(text, new Rect(10, 10, 80, 20), Confidence: 0.95);

    [Fact]
    public void AGroupWithNothingToTranslateIsHiddenAndNotWaitingOnAnything()
    {
        var line = RealtimeTranslationSession.ForOverlay(Block("Lv 15"), translation: null);

        Assert.True(line.Hidden);
        // Not untranslated: the pump retries those for as long as they are up.
        Assert.False(line.Untranslated);
        Assert.Equal("Lv 15", line.TranslatedText);
    }

    [Fact]
    public void ALineNoEngineAnsweredIsNeverHidden()
    {
        var line = RealtimeTranslationSession.ForOverlay(Block("QSAVE"), translation: null);

        Assert.True(line.Untranslated);
        Assert.False(line.Hidden);
    }

    [Theory]
    // What an engine answered never hides a line: Microsoft hands back short names and QLOAD
    // unchanged and Google does not, and a column would come out half boxed by engine and length.
    [InlineData("QLOAD", "QLOAD")]
    [InlineData("Io", "Io")]
    [InlineData("Folca", "Folca")]
    [InlineData("DEV", "DEV")]
    [InlineData("QSAVE", "快速存檔")]
    public void ALineWithWordsIsDrawnWhateverItsTranslation(string source, string translation)
    {
        var line = RealtimeTranslationSession.ForOverlay(Block(source), translation);

        Assert.False(line.Hidden);
        Assert.Equal(translation, line.TranslatedText);
    }

    [Fact]
    public void EverythingTheOverlayNeedsIsCarriedOnAHiddenLine()
    {
        var block = new OcrTextBlock(
            "12:30", new Rect(5, 6, 70, 22), [new Rect(5, 6, 70, 22)], RenderGlyphHeight: 18, Confidence: 0.9)
        {
            ReadAs = RealtimeBlockMode.Panel,
            FontGlyphHeight = 17,
        };

        var line = RealtimeTranslationSession.ForOverlay(block, translation: null);

        // Its place among the others, and its height for the type size of the rest.
        Assert.Equal(block.Bounds, line.Bounds);
        Assert.Equal(block.SourceLineBounds, line.SourceLineBounds);
        Assert.Equal(block.RenderGlyphHeight, line.RenderGlyphHeight);
        Assert.Equal(block.FontGlyphHeight, line.FontGlyphHeight);
        Assert.Equal(block.ReadAs, line.ReadAs);
    }
}
