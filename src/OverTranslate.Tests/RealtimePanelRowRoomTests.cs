using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// How large a panel sets its rows when the size is automatic: a CJK source used to come out at
/// 0.82 of the glyphs it replaced, because its rows arrive no taller than their letters and the
/// translated line had to fit inside one.
/// </summary>
public class RealtimePanelRowRoomTests
{
    private const string Source = "彼女は静かに本を閉じて、こちらを見た。";
    private const string Translation = "她靜靜地闔上書。";

    [Fact]
    public void Rows_with_leading_between_them_are_set_at_the_size_a_subtitle_sets_them() => OnSta(() =>
    {
        // Three rows of dialogue, 32 tall and 46 apart.
        var rows = Rows(pitch: 46);

        var panel = Draw(RealtimeBlockMode.Panel, "JA", rows);
        var subtitle = Draw(RealtimeBlockMode.Subtitle, "JA", rows);

        Assert.Equal(3, panel.Count);
        Assert.Single(panel.Distinct());
        Assert.Equal(subtitle[0], panel[0], 2);
    });

    [Fact]
    public void Rows_set_solid_take_no_more_than_the_distance_to_the_next_one() => OnSta(() =>
    {
        double roomy = Draw(RealtimeBlockMode.Panel, "JA", Rows(pitch: 46))[0];
        double solid = Draw(RealtimeBlockMode.Panel, "JA", Rows(pitch: 34))[0];
        double packed = Draw(RealtimeBlockMode.Panel, "JA", Rows(pitch: 32))[0];

        // 34 between centres is less than the line of text a row would otherwise be given, and
        // with no leading at all the row has its own box, which is what a panel row always had.
        Assert.True(packed < solid && solid < roomy, $"packed {packed:0.0}, solid {solid:0.0}, roomy {roomy:0.0}");
        Assert.Equal(solid * 32 / 34, packed, 1);
    });

    [Fact]
    public void A_row_on_its_own_is_set_at_that_size_too() => OnSta(() =>
    {
        IReadOnlyList<TranslatedBlock> alone = [new(Source, Translation, new Rect(20, 60, 560, 32))];

        Assert.Equal(Draw(RealtimeBlockMode.Panel, "JA", Rows(pitch: 46))[0], Assert.Single(Draw(RealtimeBlockMode.Panel, "JA", alone)), 2);
    });

    [Fact]
    public void A_Latin_row_is_the_size_it_was_its_box_being_taller_than_its_letters() => OnSta(() =>
    {
        IReadOnlyList<TranslatedBlock> rows =
        [
            .. Enumerable.Range(0, 3).Select(row => new TranslatedBlock(
                $"She quietly closed her book, line {row}.", Translation, new Rect(20, 14 + row * 46, 560, 35),
                RenderGlyphHeight: 22) { FontGlyphHeight = 22 }),
        ];

        // Held by the font scale, which the row's box was never short enough to undercut.
        Assert.Equal(
            Draw(RealtimeBlockMode.Subtitle, "EN", rows)[0],
            Draw(RealtimeBlockMode.Panel, "EN", rows)[0], 2);
    });

    private static IReadOnlyList<TranslatedBlock> Rows(double pitch) =>
    [
        .. Enumerable.Range(0, 3).Select(row => new TranslatedBlock(
            Source + row, Translation, new Rect(20, 14 + row * pitch, 560, 32))),
    ];

    /// <summary>The font size of each line one window draws for <paramref name="lines"/>.</summary>
    private static List<double> Draw(RealtimeBlockMode mode, string source, IReadOnlyList<TranslatedBlock> lines)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 700, 200),
            _ => null, source, "ZH-HANT", "#FFFFFF", "#000000", 70, mode: mode);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            return [.. ((Canvas)window.FindName("TextCanvas")).Children.Cast<Border>().Select(box =>
                (box.Child as TextBlock ?? Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(box.Child).Child)).FontSize)];
        }
        finally { window.Close(); }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
