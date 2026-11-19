using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Views.Realtime;
using Xunit;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// The plate that sets 自動判斷's five steps apart under 語句合併's slider.
/// </summary>
/// <remarks>
/// The plate replaced a hairline bracket that users missed. What it has to keep doing is be read as
/// the group of 1–5 and nothing else: hold all five, stay clear of 逐項 and 整段 on either side
/// whatever width the card is and however far long names push them, and carry its name inside.
/// </remarks>
public class MergeScaleTests
{
    private static readonly string[] ZhHant = ["不合併", "逐項", "自動判斷", "整段", "合併"];
    private static readonly string[] Ru = ["Без объединения", "По пунктам", "Авто", "Абзац", "Объединять"];

    [Theory]
    [InlineData(880, false)]
    [InlineData(520, false)]
    [InlineData(300, false)]
    [InlineData(880, true)]
    [InlineData(720, true)]
    public void The_plate_holds_one_to_five_and_stays_clear_of_their_neighbours(double width, bool russian)
    {
        OnUiThread(() =>
        {
            var scale = Build(width, russian ? Ru : ZhHant);
            var zone = Box(scale, scale.Zone);

            for (int step = RealtimeMergeLevels.AutomaticLowest; step <= RealtimeMergeLevels.AutomaticHighest; step++)
            {
                var label = Box(scale, scale.StepLabel(step));
                Assert.True(zone.Left < label.Left && label.Right < zone.Right, $"step {step} {label} outside {zone}");
            }

            var items = Box(scale, scale.StepLabel(RealtimeMergeLevels.AutomaticLowest - 1));
            var passage = Box(scale, scale.StepLabel(RealtimeMergeLevels.AutomaticHighest + 1));
            Assert.True(items.Right < zone.Left, $"逐項 {items} runs into {zone}");
            Assert.True(zone.Right < passage.Left, $"整段 {passage} runs into {zone}");

            var name = Box(scale, scale.ZoneLabel);
            Assert.True(zone.Left <= name.Left && name.Right <= zone.Right && name.Bottom <= zone.Bottom, $"name {name} outside {zone}");
            Assert.Equal((zone.Left + zone.Right) / 2, (name.Left + name.Right) / 2, 3);
            Assert.True(name.Top >= Box(scale, scale.StepLabel(RealtimeMergeLevels.AutomaticMiddle)).Bottom, "the name sits over the numbers");
        });
    }

    /// <summary>
    /// On a card so narrow that the names already crowd one another — Russian on a card squeezed to well under its usual 800 — there
    /// is no gap left for the plate to stop in; it still holds all five rather than cutting 1 or 5 off.
    /// </summary>
    [Fact]
    public void On_a_crowded_scale_the_plate_still_holds_one_to_five()
    {
        OnUiThread(() =>
        {
            var scale = Build(560, Ru);
            var zone = Box(scale, scale.Zone);
            for (int step = RealtimeMergeLevels.AutomaticLowest; step <= RealtimeMergeLevels.AutomaticHighest; step++)
            {
                var label = Box(scale, scale.StepLabel(step));
                Assert.True(zone.Left < label.Left && label.Right < zone.Right, $"step {step} {label} outside {zone}");
            }
        });
    }

    [Theory]
    [InlineData(RealtimeMergeLevels.AutomaticLowest - 1, false)]
    [InlineData(RealtimeMergeLevels.AutomaticLowest, true)]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, true)]
    [InlineData(RealtimeMergeLevels.AutomaticHighest, true)]
    [InlineData(RealtimeMergeLevels.AutomaticHighest + 1, false)]
    [InlineData(RealtimeMergeLevels.Max, false)]
    public void The_plate_lights_while_the_slider_is_on_it(int step, bool lit)
    {
        OnUiThread(() =>
        {
            var scale = Build(880, ZhHant);
            scale.Current = step;
            var plate = ((SolidColorBrush)scale.Zone.Background).Color;
            var name = ((SolidColorBrush)scale.ZoneLabel.Foreground).Color;
            Assert.Equal(lit, scale.OnZone);
            Assert.Equal(lit ? ActiveBg : IdleBg, plate);
            Assert.Equal(lit ? Accent : Primary, name);
        });
    }

    private static readonly Color IdleBg = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly Color ActiveBg = Color.FromArgb(0x29, 0x52, 0xC4, 0xFA);
    private static readonly Color Accent = Color.FromRgb(0x52, 0xC4, 0xFA);
    private static readonly Color Primary = Color.FromRgb(0xF0, 0xF0, 0xF0);

    private static RealtimeProfileOverlay.MergeScale Build(double width, string[] names)
    {
        var scale = new RealtimeProfileOverlay.MergeScale();
        var resources = scale.Resources;
        resources["S.Realtime.ProfileMergeNone"] = names[0];
        resources["S.Realtime.ProfileMergeItems"] = names[1];
        resources["S.Realtime.ProfileMergeAuto"] = names[2];
        resources["S.Realtime.ProfileMergePassage"] = names[3];
        resources["S.Realtime.ProfileMergeBlock"] = names[4];
        resources["AppFont"] = new FontFamily("Segoe UI");
        resources["AppAccent"] = new SolidColorBrush(Accent);
        resources["AppTextPrimary"] = new SolidColorBrush(Primary);
        resources["AppTextSecondary"] = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        resources["AppInputBorder"] = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48));
        resources["RealtimeMergeZoneBg"] = new SolidColorBrush(IdleBg);
        resources["RealtimeMergeZoneActiveBg"] = new SolidColorBrush(ActiveBg);

        scale.Measure(new Size(width, double.PositiveInfinity));
        scale.Arrange(new Rect(0, 0, width, scale.Height));
        return scale;
    }

    private static Rect Box(UIElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
