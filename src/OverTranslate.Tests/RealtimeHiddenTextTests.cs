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
    // A clock: AM and PM are the clock's, as the colon is.
    [InlineData("03:33 AM")]
    [InlineData("4:03 PM")]
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
    [InlineData("Lose 3 HP.")]
    [InlineData("x2 Speed")]
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

    [Theory]
    // Capitals that are no English word and cannot be read aloud: a stat, a button, a setting, a
    // tag. No list has them; no word or name looks like them.
    [InlineData("CDR")]
    [InlineData("CDR 30%")]
    [InlineData("PWR")]
    [InlineData("LB")]
    [InlineData("LB RB")]
    [InlineData("CTRL")]
    [InlineData("BGM")]
    [InlineData("TV")]
    [InlineData("RTX")]
    [InlineData("MVP")]
    // Ones that read aloud, as the label of a number in their group.
    [InlineData("SAN 45")]
    [InlineData("VIG 20")]
    [InlineData("MATK 120")]
    [InlineData("SAN: 45/99")]
    [InlineData("1400RP")]
    // Camel case made of abbreviations only, and one letter among abbreviations.
    [InlineData("SpAtk")]
    [InlineData("CritDmg 150%")]
    [InlineData("Sp. Atk 95")]
    [InlineData("LvSP")]
    [InlineData("TK/K/A")]
    // A name beside a number is a stat's label as far as the text can tell; the name goes with it.
    [InlineData("KAI 45")]
    public void AbbreviationsNoListHasAreLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // Capitals without a vowel that end a sentence, or hold one letter three times, are a sound a
    // comic lettered in capitals spells out, and are drawn as a name is.
    [InlineData("PFFT.")]
    [InlineData("TSK.")]
    [InlineData("TSK, TSK.")]
    [InlineData("FLNCH.")]
    [InlineData("HMPH!")]
    [InlineData("HM?")]
    [InlineData("GRR...")]
    [InlineData("NGH…")]
    [InlineData("PFFT.. HAHA!")]
    [InlineData("GRRR")]
    [InlineData("ZZZ")]
    [InlineData("SHHH")]
    [InlineData("「HMPH.」")]
    [InlineData("HMMPH?!")]
    [InlineData("HNG...")]
    [InlineData("GRRR GRRR")]
    public void SoundsInCapitalsAreDrawn(string text)
    {
        Assert.False(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // Not a listed stat with a mark after it, a sound beside a number, a tag before a colon or a
    // comma or a dash, or a sound effect with no mark and no letter three times — nothing tells
    // BZZT from CDR.
    [InlineData("HP!")]
    [InlineData("GRRR 100")]
    [InlineData("PFFT. 3")]
    [InlineData("BGM:")]
    [InlineData("TSK,")]
    [InlineData("SSK-")]
    [InlineData("RTX VSR")]
    [InlineData("BZZT")]
    [InlineData("KRSH")]
    [InlineData("BARRO00OMM")]
    public void WhatIsNoSoundIsStillLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // Beside a level mark, capitals that read aloud are a name or a spell: no stat has a level.
    [InlineData("KAI Lv 12")]
    [InlineData("KAI Lv12")]
    [InlineData("KAI Lv.12")]
    [InlineData("TERRA LV 12")]
    [InlineData("KAI Lvi 12")]
    [InlineData("KAI Lv 12 HP 45/120")]
    [InlineData("FIRA Lv3")]
    // A computer player's tag goes with its level as a name does; translated, it does no harm.
    [InlineData("CPU Lv 9")]
    public void ANameBesideItsLevelIsDrawn(string text)
    {
        Assert.False(RealtimeHiddenText.CarriesNoWords(text));
        Assert.False(RealtimeHiddenText.NothingToTranslate(text));
    }

    [Theory]
    // A listed abbreviation, or one that cannot be read aloud, is no name beside a level either.
    [InlineData("DEF Lv 3")]
    [InlineData("CDR Lv 2")]
    [InlineData("4:03 PM Lv 3")]
    public void NoNameBesideALevelIsLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // A numbering sign with its number: a monster's entry on its summary page.
    [InlineData("No. 025")]
    [InlineData("No.025")]
    [InlineData("NO. 151")]
    [InlineData("No 12")]
    [InlineData("No.1")]
    public void ANumberingSignWithItsNumberIsLeftOut(string text)
    {
        Assert.True(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // Alone it is the word no; with any other word the group is that word's.
    [InlineData("No")]
    [InlineData("No.")]
    [InlineData("NO")]
    [InlineData("No way")]
    [InlineData("No. 1 fan")]
    [InlineData("No, 5 of them")]
    [InlineData("YES NO")]
    public void NoAsAWordIsDrawn(string text)
    {
        Assert.False(RealtimeHiddenText.CarriesNoWords(text));
    }

    [Theory]
    // English words, whatever a game uses them for: the translators give their meaning.
    [InlineData("END")]
    [InlineData("HIT")]
    [InlineData("END 20")]
    [InlineData("HIT 95%")]
    [InlineData("FLOOR 3")]
    [InlineData("DAY 12")]
    [InlineData("AXE")]
    [InlineData("MAGE")]
    [InlineData("MAX")]
    [InlineData("MIN")]
    [InlineData("NERF")]
    [InlineData("SAVES")]
    // Read aloud and alone: a name plate, a truncated word, an icon's edge read into a word —
    // and a stat's head with no number in its group, which is drawn as it was before.
    [InlineData("KAI")]
    [InlineData("MIA")]
    [InlineData("LEO")]
    [InlineData("IO")]
    [InlineData("COFFE")]
    [InlineData("ILOAD")]
    [InlineData("SAN")]
    [InlineData("ESC")]
    [InlineData("USA")]
    [InlineData("PM")]
    [InlineData("AM")]
    // Title case is a name unless listed; camel case with a word in it is translated usefully.
    [InlineData("Kai")]
    [InlineData("Kai 45")]
    [InlineData("PvP")]
    [InlineData("AoE")]
    [InlineData("MaxHP")]
    [InlineData("HPRegen")]
    // Six capitals or more, and capitals with a word.
    [InlineData("ACTING")]
    [InlineData("LB Jump")]
    [InlineData("HP Potion")]
    public void WordsNamesAndShortTextsThatReadAloudAreDrawn(string text)
    {
        Assert.False(RealtimeHiddenText.CarriesNoWords(text));
        Assert.False(RealtimeHiddenText.NothingToTranslate(text));
    }

    [Fact]
    public void TheWordListHasTheWordsAndNoneOfTheAbbreviations()
    {
        var words = RealtimeHiddenText.EnglishShortWords.Value;

        foreach (var word in new[] { "go", "ok", "run", "bag", "end", "hit", "dev", "new", "save", "load", "win", "top", "max", "min", "axe", "mage" })
            Assert.Contains(word, words);
        foreach (var abbreviation in new[] { "hp", "mp", "atk", "def", "str", "dex", "int", "exp", "agi", "luk", "san", "vig", "cdr", "esc", "ko", "wis", "cha" })
            Assert.DoesNotContain(abbreviation, words);
        // Two to five letters, lower case, nothing else.
        Assert.All(words, word => Assert.Matches("^[a-z]{2,5}$", word));
    }

    private static OcrTextBlock Block(string text) => new(text, new Rect(10, 10, 80, 20), Confidence: 0.95);

    [Fact]
    public void AGroupWithNothingToTranslateIsHiddenAndNotWaitingOnAnything()
    {
        var line = RealtimeTranslationSession.ForOverlay(Block("Lv 15"), translation: null, filter: true);

        Assert.True(line.Hidden);
        // Not untranslated: the pump retries those for as long as they are up.
        Assert.False(line.Untranslated);
        Assert.Equal("Lv 15", line.TranslatedText);
    }

    [Fact]
    public void ALineNoEngineAnsweredIsNeverHidden()
    {
        var line = RealtimeTranslationSession.ForOverlay(Block("QSAVE"), translation: null, filter: true);

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
        var line = RealtimeTranslationSession.ForOverlay(Block(source), translation, filter: true);

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

        var line = RealtimeTranslationSession.ForOverlay(block, translation: null, filter: true);

        // Its place among the others, and its height for the type size of the rest.
        Assert.Equal(block.Bounds, line.Bounds);
        Assert.Equal(block.SourceLineBounds, line.SourceLineBounds);
        Assert.Equal(block.RenderGlyphHeight, line.RenderGlyphHeight);
        Assert.Equal(block.FontGlyphHeight, line.FontGlyphHeight);
        Assert.Equal(block.ReadAs, line.ReadAs);
    }
}
