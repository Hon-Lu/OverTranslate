using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 自動 — which of the two readings each group of a frame is given. Geometry here is taken from the
/// captures the rules were measured on (.ai/test-images, OcrHarness --auto-probe).
/// </summary>
public class RealtimeAutoLayoutTests
{
    private const double FrameHeight = 1080;

    // region-comic-en/comic-en.png: four centred lines of hand lettering, set solid.
    private static List<OcrTextBlock> Balloon(double x = 420, double y = 300) =>
    [
        Centred("WHY ARE", x, y, 52),
        Centred("YOU PICKING ON", x, y + 52, 52),
        Centred("AN INNOCENT", x, y + 104, 52),
        Centred("GOBLIN?", x, y + 156, 52),
    ];

    // screen-panel-en-ja: a title menu, four entries down one margin, 1.4 line heights apart.
    private static List<OcrTextBlock> Menu(double x = 200, double y = 400) =>
    [
        Block("New Game", x, y, 150, 38),
        Block("Continue", x, y + 53, 140, 38),
        Block("Game Options", x, y + 106, 215, 38),
        Block("Link Cygames ID", x, y + 159, 260, 38),
    ];

    [Fact]
    public void ABalloonIsOnePassage()
    {
        var group = Assert.Single(Auto(Balloon()));

        Assert.Equal("WHY ARE YOU PICKING ON AN INNOCENT GOBLIN?", group.Text);
        Assert.Equal(RealtimeBlockMode.Subtitle, group.ReadAs);
    }

    [Fact]
    public void AMenuIsItsEntries()
    {
        var groups = Auto(Menu());

        Assert.Equal(["New Game", "Continue", "Game Options", "Link Cygames ID"], groups.Select(g => g.Text));
        Assert.All(groups, group => Assert.Equal(RealtimeBlockMode.Panel, group.ReadAs));
    }

    /// <summary>
    /// The case 自動 exists for: one block over a dialogue box that shows a line of dialogue and then
    /// a list of choices. Each frame is read as what it holds, with nothing carried between them.
    /// </summary>
    [Fact]
    public void ADialogueBoxThatTurnsIntoChoicesIsReadAsEach()
    {
        var dialogue = Auto(
        [
            Block("気安く触れないでほしい。", 562, 1153, 560, 69, script: OcrLayoutScript.Cjk),
            Block("忘れてもらっては困る。私は君が嫌いなんだ。", 562, 1227, 974, 69, script: OcrLayoutScript.Cjk),
        ], 1437);
        var choices = Auto(
        [
            Block("話を聞く", 562, 1100, 190, 48, script: OcrLayoutScript.Cjk),
            Block("立ち去る", 562, 1170, 190, 48, script: OcrLayoutScript.Cjk),
            Block("様子を見る", 562, 1240, 236, 48, script: OcrLayoutScript.Cjk),
        ], 1437);

        Assert.Equal(RealtimeBlockMode.Subtitle, Assert.Single(dialogue).ReadAs);
        Assert.Equal(3, choices.Count);
        Assert.All(choices, choice => Assert.Equal(RealtimeBlockMode.Panel, choice.ReadAs));
    }

    /// <summary>
    /// A subtitle over a game's interface: the dialogue is joined and drawn as a subtitle, the
    /// interface around it stays items — in one frame.
    /// </summary>
    [Fact]
    public void DialogueOverABusyScreenIsReadApartFromTheInterface()
    {
        var frame = new List<OcrTextBlock>
        {
            Block("あ……もしもし……映像って見えてます……", 566, 1152, 951, 65, script: OcrLayoutScript.Cjk),
            Block("何せ古くて故障が多いので……やれやれ。", 566, 1228, 930, 65, script: OcrLayoutScript.Cjk),
        };
        string[] buttons = ["QSAVE", "QLOAD", "SAVE", "LOAD", "AUTO", "SKIP", "CONFIG", "LOG"];
        for (int i = 0; i < buttons.Length; i++)
            frame.Add(Block(buttons[i], 80 + i * 300, 40 + i % 2 * 500, 90, 21));

        var groups = Auto(frame, 1437);

        var dialogue = Assert.Single(groups, group => group.ReadAs == RealtimeBlockMode.Subtitle);
        Assert.Contains("もしもし", dialogue.Text);
        Assert.Contains("やれやれ", dialogue.Text);
        Assert.Equal(buttons.Length, groups.Count(group => group.ReadAs == RealtimeBlockMode.Panel));
    }

    /// <summary>
    /// Punctuated rows on a busy screen that are the same size as everything else are not taken
    /// for a passage: a page of headlines is rows of sentences that do not continue each other.
    /// </summary>
    [Fact]
    public void PunctuatedRowsTheSizeOfTheInterfaceStayWhatThePanelGrouperMadeOfThem()
    {
        var frame = new List<OcrTextBlock>();
        for (int i = 0; i < 6; i++)
        {
            frame.Add(Block($"Entry {i + 1}: the patch is out today!", 100, 100 + i * 150, 420, 28));
            frame.Add(Block("Posted an hour ago.", 100, 136 + i * 150, 230, 28));
        }

        var auto = Auto(frame);
        var panel = OcrService.GroupRealtime(Copy(frame), FrameHeight, RealtimeBlockMode.Panel);

        Assert.Equal(panel.Select(g => g.Text), auto.Select(g => g.Text));
    }

    /// <summary>
    /// Lettering with its full stop misread has nothing but its geometry to go by, and that is
    /// believed only where little else is in the frame.
    /// </summary>
    [Fact]
    public void UnpunctuatedLetteringIsAPassageOnlyInAFrameOfItsOwn()
    {
        List<OcrTextBlock> Lettering() =>
        [
            Centred("AH- IT WAS", 420, 390, 58),
            Centred("NOTHINGI", 420, 442, 58),
        ];

        Assert.Single(Auto(Lettering()));

        var busy = Lettering();
        for (int i = 0; i < 8; i++)
            busy.Add(Block($"Slot {i}", 900 + i % 2 * 300, 100 + i * 110, 110, 58));
        Assert.Equal(2, Auto(busy).Count(group => group.Text.Contains("AH-") || group.Text.Contains("NOTHINGI")));
    }

    /// <summary>
    /// A menu set as tightly as lettering, framed on its own, with nothing but its capitals to say
    /// it is not a sentence: every row starts afresh.
    /// </summary>
    [Fact]
    public void AMenuSetSolidIsStillItsEntries()
    {
        var groups = Auto(
        [
            Block("New Game", 420, 30, 140, 40),
            Block("Continue", 420, 78, 130, 40),
            Block("Game Options", 420, 126, 200, 40),
            Block("Quit to Title", 420, 174, 190, 40),
        ]);

        Assert.Equal(4, groups.Count);
        Assert.All(groups, group => Assert.Equal(RealtimeBlockMode.Panel, group.ReadAs));
    }

    /// <summary>A sentence wrapped without punctuation carries on in lower case, and is one passage.</summary>
    [Fact]
    public void AnUnpunctuatedSentenceAcrossTwoRowsIsOnePassage()
    {
        var group = Assert.Single(Auto(
        [
            Block("I heard a bunch of", 400, 60, 330, 40),
            Block("experts are coming", 400, 104, 340, 40),
        ]));

        Assert.Equal(RealtimeBlockMode.Subtitle, group.ReadAs);
    }

    /// <summary>
    /// A dialogue box with a reading printed over its kanji. The groupers take the reading out on
    /// the way, and the first build threw on the group that was left without members ("Sequence
    /// contains no elements" on the bar). It is one passage, and the reading is not drawn at all.
    /// </summary>
    [Fact]
    public void DialogueWithAReadingOverItsKanjiIsOnePassageWithoutTheReading()
    {
        var groups = Auto(
        [
            Block("桜羽エマ", 280, 950, 380, 110, OcrLayoutScript.Cjk),
            Block("(ウソでしょ……", 450, 1140, 345, 46, OcrLayoutScript.Cjk),
            Block("にかいどう", 492, 1196, 90, 18, OcrLayoutScript.Cjk),
            Block("二階堂ヒロ……ちゃん？)", 470, 1214, 530, 46, OcrLayoutScript.Cjk),
        ], 1352);

        Assert.DoesNotContain(groups, group => group.Text.Contains("にかいどう"));
        var dialogue = Assert.Single(groups, group => group.Text.Contains("ウソでしょ"));
        Assert.Contains("二階堂ヒロ", dialogue.Text);
        Assert.Equal(RealtimeBlockMode.Subtitle, dialogue.ReadAs);
    }

    /// <summary>Every group a grouper returns reaches the screen, whatever its trace could account for.</summary>
    [Fact]
    public void NoTextIsLostBetweenTheGroupersAndTheAnswer()
    {
        List<OcrTextBlock> Frame() =>
        [
            Block("(ウソでしょ……", 450, 1140, 345, 46, OcrLayoutScript.Cjk),
            Block("にかいどう", 492, 1196, 90, 18, OcrLayoutScript.Cjk),
            Block("二階堂ヒロ……ちゃん？)", 470, 1214, 530, 46, OcrLayoutScript.Cjk),
            Block("Auto", 10, 1300, 60, 30),
        ];

        var auto = string.Concat(Auto(Frame(), 1352).Select(group => group.Text));
        foreach (var group in OcrService.GroupRealtime(Frame(), 1352, RealtimeBlockMode.Subtitle))
            foreach (var word in group.Text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(word, auto);
    }

    /// <summary>A frame with no passage in it is the panel grouper's own answer, in its order.</summary>
    [Fact]
    public void AFrameWithoutAPassageIsThePanelGroupersAnswer()
    {
        var auto = Auto(Menu());
        var panel = OcrService.GroupRealtime(Menu(), FrameHeight, RealtimeBlockMode.Panel);

        Assert.Equal(panel.Select(g => (g.Text, g.Bounds)), auto.Select(g => (g.Text, g.Bounds)));
    }

    /// <summary>A frame that is one passage is the dialogue grouper's own answer.</summary>
    [Fact]
    public void AFrameThatIsOnePassageIsTheDialogueGroupersAnswer()
    {
        var auto = Auto(Balloon());
        var dialogue = OcrService.GroupRealtime(Balloon(), FrameHeight, RealtimeBlockMode.Subtitle);

        Assert.Equal(dialogue.Select(g => (g.Text, g.Bounds)), auto.Select(g => (g.Text, g.Bounds)));
    }

    [Fact]
    public void ASingleSubtitleLineIsDrawnAsASubtitle()
    {
        var line = Assert.Single(Auto([Block("Marina-san, are you okay?", 420, 60, 760, 88)], 196));

        Assert.Equal(RealtimeBlockMode.Subtitle, line.ReadAs);
    }

    /// <summary>The explicit modes mark nothing: their block reads every group as the mode it is set to.</summary>
    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle)]
    [InlineData(RealtimeBlockMode.Panel)]
    public void AnExplicitModeLeavesGroupsUnmarked(RealtimeBlockMode mode)
    {
        Assert.All(OcrService.GroupRealtime(Balloon(), FrameHeight, mode), group => Assert.Null(group.ReadAs));
    }

    [Theory]
    [InlineData("Did I hear that wrong...?", 3)]
    [InlineData("気安く触れないでほしい。", 1)]
    [InlineData("他の牢屋にいた誰かが、", 1)]
    [InlineData("It's fine, really.", 2)]
    [InlineData("Game Options", 0)]
    [InlineData("HP 2298/2692", 0)]
    [InlineData("v1.5 3,149", 0)]
    [InlineData("Lv. 100", 0)]
    [InlineData("forum.example.com", 0)]
    [InlineData("Learned Skills : Cleave in Two", 0)]
    public void SentencePunctuationIsWhatASentenceHasAndALabelDoesNot(string text, int marks)
    {
        Assert.Equal(marks, RealtimeAutoLayout.CountMarks(text));
    }

    /// <summary>
    /// What the block as a whole is read as follows its latest reading — see
    /// <see cref="RealtimeRegionState.AutoReadsAs"/> — and an explicit mode is never overruled.
    /// </summary>
    [Fact]
    public void ABlockInAutoIsSizedAndTimedAsWhatItLastHeld()
    {
        var state = new RealtimeRegionState();

        Assert.Equal(RealtimeBlockMode.Subtitle, state.ReadsAs(RealtimeBlockMode.Auto));

        state.AutoReadsAs = RealtimeBlockMode.Panel;
        Assert.Equal(RealtimeBlockMode.Panel, state.ReadsAs(RealtimeBlockMode.Auto));
        Assert.Equal(RealtimeBlockMode.Subtitle, state.ReadsAs(RealtimeBlockMode.Subtitle));

        var asPanel = RealtimeDetectorSize.For(1920, 1080, state.ReadsAs(RealtimeBlockMode.Auto));
        Assert.Equal(RealtimeDetectorSize.For(1920, 1080, RealtimeBlockMode.Panel), asPanel, SizesEqual);
    }

    private static bool SizesEqual(
        (int Primary, IReadOnlyList<int> Fallbacks) a, (int Primary, IReadOnlyList<int> Fallbacks) b) =>
        a.Primary == b.Primary && a.Fallbacks.SequenceEqual(b.Fallbacks);

    private static List<OcrTextBlock> Auto(List<OcrTextBlock> blocks, double frameHeight = FrameHeight) =>
        OcrService.GroupRealtime(blocks, frameHeight, RealtimeBlockMode.Auto);

    private static List<OcrTextBlock> Copy(List<OcrTextBlock> blocks) => [.. blocks.Select(block => block with { })];

    // A line of lettering centred on x, as wide as its letters make it.
    private static OcrTextBlock Centred(string text, double centre, double y, double height)
    {
        double width = text.Length * height * 0.5;
        return Block(text, centre - width / 2, y, width, height);
    }

    private static OcrTextBlock Block(
        string text, double x, double y, double w, double h, OcrLayoutScript script = OcrLayoutScript.Latin) =>
        new(text, new Rect(x, y, w, h), Confidence: 0.99, LayoutBounds: new Rect(x, y, w, h),
            LayoutScript: script, LayoutGlyphHeight: h * 0.82);
}
