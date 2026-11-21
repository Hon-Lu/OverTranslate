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
/// The five names under 語句合併's slider — 不合併, 逐項, 自動判斷, 整段, 合併.
/// </summary>
/// <remarks>
/// What they have to keep doing: stay inside the card and apart from each other whatever its width
/// and however long the names (Russian is the longest), hold the two end names to the ends, and pick
/// out the value the slider is on in the accent.
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
    public void The_five_names_stay_inside_and_apart(double width, bool russian)
    {
        OnUiThread(() =>
        {
            var scale = Build(width, russian ? Ru : ZhHant);
            var boxes = Enumerable.Range(0, Steps).Select(step => Box(scale, scale.StepLabel(step))).ToArray();

            Assert.Equal(0, boxes[0].Left, 3);
            Assert.Equal(width, boxes[^1].Right, 3);
            for (int step = 1; step < Steps; step++)
                Assert.True(boxes[step - 1].Right <= boxes[step].Left, $"{boxes[step - 1]} runs into {boxes[step]}");
            Assert.All(boxes, box => Assert.True(box.Bottom <= scale.Height, $"{box} runs out of the scale"));
        });
    }

    [Theory]
    [InlineData(RealtimeSentenceMerge.None)]
    [InlineData(RealtimeSentenceMerge.Items)]
    [InlineData(RealtimeSentenceMerge.Automatic)]
    [InlineData(RealtimeSentenceMerge.Passage)]
    [InlineData(RealtimeSentenceMerge.All)]
    public void Only_the_value_the_slider_is_on_takes_the_accent(RealtimeSentenceMerge merge)
    {
        OnUiThread(() =>
        {
            var scale = Build(880, ZhHant);
            scale.Current = (int)merge;
            for (int step = 0; step < Steps; step++)
            {
                var label = scale.StepLabel(step);
                Assert.Equal(step == (int)merge ? Accent : Primary, ((SolidColorBrush)label.Foreground).Color);
                Assert.Equal(step == (int)merge ? FontWeights.SemiBold : FontWeights.Normal, label.FontWeight);
            }
        });
    }

    [Fact]
    public void A_new_scale_is_on_automatic()
    {
        OnUiThread(() => Assert.Equal((int)RealtimeSentenceMerge.Automatic, Build(880, ZhHant).Current));
    }

    private static int Steps => Enum.GetValues<RealtimeSentenceMerge>().Length;

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
        resources["AppInputBorder"] = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48));

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
