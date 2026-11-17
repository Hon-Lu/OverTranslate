using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 譯文對齊: reading a group's alignment off its rows, what each value comes to on a group, the
/// value as a mode stores it, and the overlay drawing by it — with the two presets that read one
/// way only drawing exactly as before the value existed.
/// </summary>
public class RealtimeTextAlignTests
{
    // Three rows of 30px type, set flush left, centred and flush right on x = 100…500.
    private static readonly Rect[] FlushLeft = [new(100, 0, 400, 30), new(100, 40, 260, 30), new(100, 80, 330, 30)];
    private static readonly Rect[] Centred = [new(100, 0, 400, 30), new(170, 40, 260, 30), new(135, 80, 330, 30)];
    private static readonly Rect[] FlushRight = [new(100, 0, 400, 30), new(240, 40, 260, 30), new(170, 80, 330, 30)];

    // ── Reading the source ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void RowsThatLineUpOnOneSide_AreReadAsThatSide()
    {
        Assert.Equal(SourceAlignment.Left, RealtimeSourceAlignment.Of(FlushLeft));
        Assert.Equal(SourceAlignment.Center, RealtimeSourceAlignment.Of(Centred));
        Assert.Equal(SourceAlignment.Right, RealtimeSourceAlignment.Of(FlushRight));
    }

    [Fact]
    public void EdgesAStrokeOrTwoApart_StillLineUp()
    {
        // 0.3 of a 30px row is 9px: a box drawn a few pixels off by recognition is still the margin.
        Rect[] rows = [new(100, 0, 400, 30), new(106, 40, 250, 30), new(97, 80, 330, 30)];
        Assert.Equal(SourceAlignment.Left, RealtimeSourceAlignment.Of(rows));
    }

    [Fact]
    public void RowsOfOneWidth_SayNothing()
    {
        Rect[] rows = [new(100, 0, 400, 30), new(104, 40, 396, 30)];
        Assert.Null(RealtimeSourceAlignment.Of(rows));
    }

    [Fact]
    public void RaggedRows_SayNothing()
    {
        // A name plate joined to its line: neither edge nor centre agree.
        Rect[] rows = [new(100, 0, 120, 30), new(180, 40, 500, 30)];
        Assert.Null(RealtimeSourceAlignment.Of(rows));
    }

    [Fact]
    public void ASingleRow_SaysNothing_EvenInPieces()
    {
        Assert.Null(RealtimeSourceAlignment.Of(null));
        Assert.Null(RealtimeSourceAlignment.Of([new Rect(100, 0, 400, 30)]));
        // Two pieces side by side are one row, not two.
        Assert.Null(RealtimeSourceAlignment.Of([new Rect(100, 0, 150, 30), new Rect(300, 2, 200, 28)]));
    }

    [Fact]
    public void BrokenBoxes_AreLeftOut()
    {
        Assert.Null(RealtimeSourceAlignment.Of([new Rect(100, 0, 400, 30), Rect.Empty]));
        Assert.Equal(SourceAlignment.Left, RealtimeSourceAlignment.Of([.. FlushLeft, new Rect(0, 0, 0, 30)]));
    }

    // ── What each value comes to ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle)]
    [InlineData(RealtimeBlockMode.Panel)]
    public void Automatic_FollowsTheRows_AndLeavesTheRestAsDrawn(RealtimeBlockMode drawnAs)
    {
        Assert.Equal(SourceAlignment.Left, RealtimeSourceAlignment.For(RealtimeTextAlign.Auto, drawnAs, FlushLeft));
        Assert.Equal(SourceAlignment.Center, RealtimeSourceAlignment.For(RealtimeTextAlign.Auto, drawnAs, Centred));
        Assert.Equal(SourceAlignment.Right, RealtimeSourceAlignment.For(RealtimeTextAlign.Auto, drawnAs, FlushRight));
        Assert.Null(RealtimeSourceAlignment.For(RealtimeTextAlign.Auto, drawnAs, [new Rect(100, 0, 400, 30)]));
        Assert.Null(RealtimeSourceAlignment.For(RealtimeTextAlign.Auto, drawnAs, null));
    }

    [Fact]
    public void AnAskedForValue_IgnoresTheRows()
    {
        foreach (var rows in new[] { FlushLeft, Centred, FlushRight, [new Rect(100, 0, 400, 30)] })
        {
            Assert.Equal(SourceAlignment.Center, RealtimeSourceAlignment.For(RealtimeTextAlign.Center, RealtimeBlockMode.Subtitle, rows));
            Assert.Equal(SourceAlignment.Center, RealtimeSourceAlignment.For(RealtimeTextAlign.Center, RealtimeBlockMode.Panel, rows));
            Assert.Equal(SourceAlignment.Left, RealtimeSourceAlignment.For(RealtimeTextAlign.Left, RealtimeBlockMode.Subtitle, rows));
            // A panel's rows are on their left edge already; null keeps its fallback as it was.
            Assert.Null(RealtimeSourceAlignment.For(RealtimeTextAlign.Left, RealtimeBlockMode.Panel, rows));
        }
    }

    // ── The value in a mode ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePresets_HoldWhatTheyDrewWith()
    {
        Assert.Equal(RealtimeTextAligns.Auto, RealtimePresets.Create(RealtimePresets.StandardId).Align);
        Assert.Equal(RealtimeTextAligns.Center, RealtimePresets.Create(RealtimePresets.SubtitleId).Align);
        Assert.Equal(RealtimeTextAligns.Left, RealtimePresets.Create(RealtimePresets.InterfaceId).Align);
        Assert.Equal(RealtimeTextAlign.Auto, ResolvedProfile.Standard.Align);
        Assert.Equal(RealtimeTextAlign.Center, ResolvedProfile.Subtitle.Align);
        Assert.Equal(RealtimeTextAlign.Left, ResolvedProfile.Interface.Align);
    }

    [Theory]
    [InlineData(null, RealtimePresets.SubtitleId, RealtimeTextAligns.Center)]
    [InlineData("left", RealtimePresets.SubtitleId, RealtimeTextAligns.Left)]
    [InlineData(" CENTER ", RealtimePresets.StandardId, RealtimeTextAligns.Center)]
    [InlineData("auto", RealtimePresets.InterfaceId, RealtimeTextAligns.Auto)]
    [InlineData("Right", RealtimePresets.InterfaceId, RealtimeTextAligns.Left)]
    [InlineData("", RealtimePresets.StandardId, RealtimeTextAligns.Auto)]
    public void AStoredValue_IsReadOrFollowsThePreset(string? stored, string basedOn, string expected)
    {
        var mode = new RealtimeProfile { Id = "m", BasedOn = basedOn, Align = stored };
        Assert.Equal(expected, mode.Filled().Align);
    }

    [Fact]
    public void AFileWithoutTheValue_FollowsThePreset()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"interface","MergeLevel":7},{"Id":"b","Name":"B","BasedOn":"subtitle","Align":"Left"}]}}
            """);

        Assert.Null(settings.Realtime.Profiles[0].Align);
        Assert.Equal(RealtimeTextAlign.Left, settings.Realtime.ResolveProfile("a").Align);
        Assert.Equal(RealtimeTextAlign.Left, settings.Realtime.ResolveProfile("b").Align);
        Assert.Contains("\"Align\": \"Left\"", SettingsService.Serialize(settings).Replace("\":\"", "\": \""));
    }

    [Fact]
    public void TheValue_IsModifiedAndPutBackLikeAnyOther()
    {
        var mode = new RealtimeProfile { Id = "m", BasedOn = RealtimePresets.SubtitleId, Align = RealtimeTextAligns.Center };
        // The preset's own value is stored as following it.
        Assert.False(RealtimeProfileEditing.IsSet(RealtimeProfileEditing.WithoutTemplateValues(mode), ProfileParam.Align));

        mode.Align = RealtimeTextAligns.Left;
        var tidy = RealtimeProfileEditing.WithoutTemplateValues(mode);
        Assert.True(RealtimeProfileEditing.IsSet(tidy, ProfileParam.Align));
        Assert.Equal(1, RealtimeProfileEditing.ModifiedCount(tidy));
        Assert.DoesNotContain(ProfileParam.Align, RealtimeProfileEditing.Advanced);
        Assert.True(RealtimeProfileEditing.IsHorizontalOnly(ProfileParam.Align));

        RealtimeProfileEditing.Clear(tidy, ProfileParam.Align);
        Assert.Null(tidy.Align);
        Assert.Null(RealtimeProfileEditing.ResetAll(mode).Align);
    }

    [Fact]
    public void AChangeOfAlignmentAlone_ReadsNothingAgain()
    {
        var left = ResolvedProfile.Standard with { Align = RealtimeTextAlign.Left };
        Assert.True(left.ReadsLike(ResolvedProfile.Standard));
        Assert.NotEqual(ResolvedProfile.Standard, left);
        Assert.False((left with { IgnoreSmallFraction = 0.2 }).ReadsLike(ResolvedProfile.Standard));
        // Not in the line the replay reports open with, so they stay byte for byte as before.
        Assert.Equal(ResolvedProfile.Standard.Describe(), left.Describe());
    }

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 字幕・漫畫 draws every group as a band round its source's centre, as before 譯文對齊 — a flush
    /// left passage included: what it draws is what the same group drawn with no alignment at all,
    /// the band's old path, comes to.
    /// </summary>
    [Fact]
    public void SubtitlesDrawAsBefore() => OnSta(() =>
    {
        TranslatedBlock[] groups =
        [
            Group("左揃えの対話", "靠左的對話框。", FlushLeft),
            Group("中央の字幕", "置中的字幕。", Centred),
            Group("右の説明", "靠右的說明。", FlushRight),
            new("一行", "單行字幕", new Rect(200, 150, 300, 30)),
        ];
        var drawn = Draw(RealtimeBlockMode.Subtitle, ResolvedProfile.Subtitle.Align, groups);
        var before = DrawLines(groups, alignment: null);
        Assert.Equal(before, drawn);
    });

    /// <summary>
    /// 遊戲介面・聊天室 splits each group back into its rows, each from its source row's left edge,
    /// and a group it cannot split is a band round its centre — as before 譯文對齊, whichever way
    /// the source is set.
    /// </summary>
    [Fact]
    public void InterfaceDrawsAsBefore() => OnSta(() =>
    {
        foreach (var rows in new[] { FlushLeft, Centred, FlushRight })
        {
            var group = Group("ui", "第一列項目 第二列 第三列項目", rows);
            var drawn = Texts(RealtimeBlockMode.Panel, ResolvedProfile.Interface.Align, [group]);
            Assert.Equal(3, drawn.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(rows[i].X, Canvas.GetLeft(drawn[i]), 6);
                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<Viewbox>(drawn[i].Child).HorizontalAlignment);
            }
        }

        // Rows it cannot flow the translation through (one of them has no width): drawn as the
        // band, centred as ever.
        var whole = Group("ui", "第一列項目 第二列 第三列項目", [.. FlushLeft, new Rect(100, 120, 0, 30)]);
        var band = Assert.Single(Texts(RealtimeBlockMode.Panel, ResolvedProfile.Interface.Align, [whole]));
        Assert.Equal(DrawLines([whole], alignment: null), Draw(RealtimeBlockMode.Panel, ResolvedProfile.Interface.Align, [whole]));
        Assert.Equal(HorizontalAlignment.Center, Assert.IsType<TextBlock>(band.Child).HorizontalAlignment);
    });

    [Fact]
    public void Automatic_SetsAFlushLeftPassageFromItsLeftEdge() => OnSta(() =>
    {
        var group = Group("左揃えの対話", "短譯文。", FlushLeft) with { ReadAs = RealtimeBlockMode.Subtitle };
        var text = Assert.Single(Texts(RealtimeBlockMode.Auto, RealtimeTextAlign.Auto, [group]));
        Assert.Equal(100 - text.Padding.Left, Canvas.GetLeft(text), 6);
        Assert.Equal(HorizontalAlignment.Left, Assert.IsType<TextBlock>(text.Child).HorizontalAlignment);
        Assert.Equal(TextAlignment.Left, ((TextBlock)text.Child).TextAlignment);

        var right = Group("右の説明", "短譯文。", FlushRight) with { ReadAs = RealtimeBlockMode.Subtitle };
        var set = Assert.Single(Texts(RealtimeBlockMode.Auto, RealtimeTextAlign.Auto, [right]));
        Assert.Equal(500 + set.Padding.Right, Canvas.GetLeft(set) + set.Width, 6);
        Assert.Equal(HorizontalAlignment.Right, Assert.IsType<TextBlock>(set.Child).HorizontalAlignment);

        // Centred rows, one row, rows of one width: the band as it always was.
        TranslatedBlock[] asBefore =
        [
            Group("中央の字幕", "置中的字幕。", Centred) with { ReadAs = RealtimeBlockMode.Subtitle },
            new("一行", "單行", new Rect(200, 150, 300, 30)) { ReadAs = RealtimeBlockMode.Subtitle },
            Group("同じ幅", "同寬兩列。", [new Rect(100, 0, 400, 30), new Rect(102, 40, 398, 30)]) with { ReadAs = RealtimeBlockMode.Subtitle },
        ];
        Assert.Equal(DrawLines(asBefore, alignment: null), Draw(RealtimeBlockMode.Auto, RealtimeTextAlign.Auto, asBefore));
    });

    [Fact]
    public void Automatic_SetsACentredPanelRoundEachRow() => OnSta(() =>
    {
        var group = Group("ui", "第一列項目 第二列 第三列項目", Centred) with { ReadAs = RealtimeBlockMode.Panel };
        var drawn = Texts(RealtimeBlockMode.Auto, RealtimeTextAlign.Auto, [group]);
        Assert.Equal(3, drawn.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Centred[i].X + Centred[i].Width / 2, Canvas.GetLeft(drawn[i]) + drawn[i].Width / 2, 6);
            Assert.Equal(HorizontalAlignment.Center, Assert.IsType<Viewbox>(drawn[i].Child).HorizontalAlignment);
        }
    });

    [Fact]
    public void Centred_SetsEveryPanelRowRoundItsSourceRow() => OnSta(() =>
    {
        var drawn = Texts(RealtimeBlockMode.Panel, RealtimeTextAlign.Center, [Group("ui", "第一列項目 第二列 第三列項目", FlushLeft)]);
        Assert.Equal(3, drawn.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(FlushLeft[i].X + FlushLeft[i].Width / 2, Canvas.GetLeft(drawn[i]) + drawn[i].Width / 2, 6);
            Assert.Equal(HorizontalAlignment.Center, Assert.IsType<Viewbox>(drawn[i].Child).HorizontalAlignment);
        }
    });

    [Fact]
    public void Left_SetsABandFromItsSourcesLeftEdge() => OnSta(() =>
    {
        // One row, read as a passage: a short translation starts where the source did, and a long
        // one grows to the right of it.
        var shortLine = new TranslatedBlock("A line", "短", new Rect(100, 150, 300, 30));
        var text = Assert.Single(Texts(RealtimeBlockMode.Subtitle, RealtimeTextAlign.Left, [shortLine]));
        Assert.Equal(100 - text.Padding.Left, Canvas.GetLeft(text), 6);
        Assert.Equal(HorizontalAlignment.Left, Assert.IsType<TextBlock>(text.Child).HorizontalAlignment);

        var longLine = shortLine with { TranslatedText = "這一句譯文比它所取代的原文長上許多" };
        var grown = Assert.Single(Texts(RealtimeBlockMode.Subtitle, RealtimeTextAlign.Left, [longLine]));
        Assert.Equal(100 - grown.Padding.Left, Canvas.GetLeft(grown), 6);
        Assert.True(grown.Width > 300 + grown.Padding.Left + grown.Padding.Right);
    });

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static TranslatedBlock Group(string source, string translation, Rect[] rows) =>
        new(source, translation, rows.Aggregate(Rect.Union), rows, 22);

    private static RealtimeBlockWindow Window(RealtimeBlockMode mode, RealtimeTextAlign? align) =>
        new(0, new System.Drawing.Rectangle(0, 0, 700, 240), _ => null, "JA", "ZH-HANT", "#FFFFFF", "#000000", 70,
            mode: mode, align: align);

    private static List<Border> Texts(RealtimeBlockMode mode, RealtimeTextAlign? align, IReadOnlyList<TranslatedBlock> lines)
    {
        var window = Window(mode, align);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            return ((Canvas)window.FindName("TextCanvas")).Children.Cast<Border>().ToList();
        }
        finally { window.Close(); }
    }

    /// <summary>Where and how every box of the drawing is set, as text to compare two drawings by.</summary>
    private static List<string> Draw(RealtimeBlockMode mode, RealtimeTextAlign? align, IReadOnlyList<TranslatedBlock> lines)
    {
        var window = Window(mode, align);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            return Describe((Canvas)window.FindName("ScrimCanvas"), (Canvas)window.FindName("TextCanvas"));
        }
        finally { window.Close(); }
    }

    /// <summary>The same lines through BuildLine one by one with no alignment: the band's own path.</summary>
    private static List<string> DrawLines(IReadOnlyList<TranslatedBlock> lines, SourceAlignment? alignment)
    {
        var window = Window(RealtimeBlockMode.Subtitle, null);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            // Rebuilt once first for the sizes it settles the lines at, which are what is drawn.
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var settled = (IReadOnlyList<TranslatedBlock>)type.GetField("_layoutLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var build = type.GetMethod("BuildLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var backgrounds = new Canvas();
            var texts = new Canvas();
            foreach (var line in settled)
            {
                var visual = build.Invoke(window, [line, 700.0, 240.0, null, null, alignment])!;
                var background = (Border?)visual.GetType().GetProperty("Background")!.GetValue(visual);
                if (background is not null) backgrounds.Children.Add(background);
                texts.Children.Add((UIElement)visual.GetType().GetProperty("Text")!.GetValue(visual)!);
            }
            return Describe(backgrounds, texts);
        }
        finally { window.Close(); }
    }

    private static List<string> Describe(Canvas backgrounds, Canvas texts) =>
        [
            .. backgrounds.Children.Cast<Border>().Select(b =>
                FormattableString.Invariant($"bg {Canvas.GetLeft(b):0.###},{Canvas.GetTop(b):0.###} {b.Width:0.###}x{b.Height:0.###} r{b.CornerRadius.TopLeft}")),
            .. texts.Children.Cast<Border>().Select(b =>
            {
                var child = b.Child is Viewbox fit ? fit.Child : b.Child;
                var block = (TextBlock)child;
                var place = b.Child is Viewbox box ? box.HorizontalAlignment : block.HorizontalAlignment;
                return FormattableString.Invariant(
                    $"text {Canvas.GetLeft(b):0.###},{Canvas.GetTop(b):0.###} {b.Width:0.###}x{b.Height:0.###} {place} {block.TextAlignment} {block.TextWrapping} {block.FontSize:0.###} {block.Text}");
            }),
        ];

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
