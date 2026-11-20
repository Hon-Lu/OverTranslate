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
/// The bracket that sets 自動判斷's five steps apart under 語句合併's slider.
/// </summary>
/// <remarks>
/// The bracket was made heavier and given a text colour because users missed it. What it has to keep
/// doing is read as the group of 1–5: span all five names whatever width the card is and however far
/// long names push them, carry its name centred under them, and take the accent — line and name —
/// whenever the slider is on any of the five, not only on the step's own number.
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
    [InlineData(560, true)]
    [InlineData(420, true)]
    public void The_bracket_spans_one_to_five_with_its_name_under_them(double width, bool russian)
    {
        OnUiThread(() =>
        {
            var scale = Build(width, russian ? Ru : ZhHant);
            var bracket = Box(scale, scale.Bracket);

            for (int step = RealtimeMergeLevels.AutomaticLowest; step <= RealtimeMergeLevels.AutomaticHighest; step++)
            {
                var label = Box(scale, scale.StepLabel(step));
                Assert.True(bracket.Left <= label.Left && label.Right <= bracket.Right, $"step {step} {label} outside {bracket}");
            }

            var name = Box(scale, scale.BracketLabel);
            Assert.Equal((bracket.Left + bracket.Right) / 2, (name.Left + name.Right) / 2, 3);
            Assert.True(name.Top >= Box(scale, scale.StepLabel(RealtimeMergeLevels.AutomaticMiddle)).Bottom, "the name sits over the numbers");
            Assert.True(name.Bottom <= scale.Height, $"the name {name} runs out of the scale");
        });
    }

    [Theory]
    [InlineData(RealtimeMergeLevels.Min, false)]
    [InlineData(RealtimeMergeLevels.AutomaticLowest - 1, false)]
    [InlineData(RealtimeMergeLevels.AutomaticLowest, true)]
    [InlineData(RealtimeMergeLevels.AutomaticLowest + 1, true)]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, true)]
    [InlineData(RealtimeMergeLevels.AutomaticHighest, true)]
    [InlineData(RealtimeMergeLevels.AutomaticHighest + 1, false)]
    [InlineData(RealtimeMergeLevels.Max, false)]
    public void The_bracket_and_its_name_take_the_accent_on_any_of_the_five(int step, bool lit)
    {
        OnUiThread(() =>
        {
            var scale = Build(880, ZhHant);
            scale.Current = step;
            Assert.Equal(lit, scale.OnAutomatic);
            Assert.Equal(lit ? Accent : Secondary, ((SolidColorBrush)scale.Bracket.BorderBrush).Color);
            Assert.Equal(lit ? Accent : Primary, ((SolidColorBrush)scale.BracketLabel.Foreground).Color);
            Assert.Equal(Accent, ((SolidColorBrush)scale.StepLabel(step).Foreground).Color);
        });
    }

    /// <summary>A preset swaps the accent for grey above the scale; the bracket follows it there.</summary>
    [Fact]
    public void On_a_preset_the_lit_bracket_is_grey()
    {
        OnUiThread(() =>
        {
            var scale = Build(880, ZhHant);
            scale.Resources["AppAccent"] = new SolidColorBrush(Secondary);
            scale.Current = RealtimeMergeLevels.AutomaticMiddle + 1;
            Assert.Equal(Secondary, ((SolidColorBrush)scale.Bracket.BorderBrush).Color);
            Assert.Equal(Secondary, ((SolidColorBrush)scale.BracketLabel.Foreground).Color);
        });
    }

    private static readonly Color Accent = Color.FromRgb(0x52, 0xC4, 0xFA);
    private static readonly Color Primary = Color.FromRgb(0xF0, 0xF0, 0xF0);
    private static readonly Color Secondary = Color.FromRgb(0xAA, 0xAA, 0xAA);

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
        resources["AppTextSecondary"] = new SolidColorBrush(Secondary);
        resources["AppInputBorder"] = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48));
        resources["AppCardBg"] = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));

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
