using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Rectangle = System.Drawing.Rectangle;

namespace OverTranslate.Tests;

/// <summary>
/// The 模式 panel over the edit layer: opened from a block's 模式設定… or the framing bar's 模式, it
/// tunes the blocks being framed, so the layer and the panel have to agree on which block is which.
/// </summary>
public class RealtimeEditLayerPanelTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void ABlockOnlyMoved_DoesNotRedrawThePanel()
    {
        var before = new[] { Block(0, RealtimePresets.StandardId), Block(1, RealtimePresets.SubtitleId) };
        var after = new[] { before[0] with { Bounds = new Rectangle(300, 300, 50, 50) }, before[1] };

        Assert.True(RealtimeSessionController.SameBlocksForPanel(before, after));
    }

    [Fact]
    public void AModeOrDirectionOrCountChanged_RedrawsThePanel()
    {
        var before = new[] { Block(0, RealtimePresets.StandardId), Block(1, RealtimePresets.SubtitleId) };

        Assert.False(RealtimeSessionController.SameBlocksForPanel(
            before, [before[0], before[1] with { ProfileId = RealtimePresets.StandardId }]));
        Assert.False(RealtimeSessionController.SameBlocksForPanel(
            before, [before[0], before[1] with { Orientation = RealtimeTextOrientation.Vertical }]));
        Assert.False(RealtimeSessionController.SameBlocksForPanel(before, [before[0]]));
    }

    [Theory]
    [InlineData(2, 0, 1)] // a block before the one shown: it moves up one place
    [InlineData(1, 1, 0)] // the one shown: the block before it
    [InlineData(0, 0, 0)] // the first, shown: the new first
    [InlineData(0, 2, 0)] // a block after the one shown: nothing moves
    public void ABlockRemoved_ThePanelKeepsItsPlace(int shown, int removed, int expected) =>
        Assert.Equal(expected, RealtimeProfilePanel.KeptBlock(shown, removed));

    [Fact]
    public void ModeChosenOnThePanelWhileFraming_IsAPickFromTheBlocksOwnList()
    {
        OnStaThread(() =>
        {
            var layer = Layer(out var add);
            add(RealtimePresets.StandardId);
            add(RealtimePresets.StandardId);
            (string Profile, RealtimeTextOrientation Orientation)? defaults = null;
            int changed = 0;
            layer.BlockDefaultsChanged += (_, d) => defaults = d;
            layer.BlocksChanged += (_, _) => changed++;

            layer.SetBlockProfile(1, RealtimePresets.SubtitleId);

            var blocks = layer.GetPhysicalBlocks();
            Assert.Equal(RealtimePresets.StandardId, blocks[0].ProfileId);
            Assert.Equal(RealtimePresets.SubtitleId, blocks[1].ProfileId);
            // The next block drawn starts on it too, as with a pick from the list.
            Assert.Equal(RealtimePresets.SubtitleId, defaults?.Profile);
            Assert.Equal(1, changed);

            // Out of range is nothing, not a fault: the panel may be a beat behind the layer.
            layer.SetBlockProfile(5, RealtimePresets.StandardId);
            layer.Close();
        });
    }

    [Fact]
    public void ABlockRemoved_IsReportedByPosition_AndTheNamesAfterItMoveUp()
    {
        OnStaThread(() =>
        {
            var layer = Layer(out var add);
            add(RealtimePresets.StandardId);
            add(RealtimePresets.SubtitleId);
            add(RealtimePresets.InterfaceId);
            layer.SetMarkers(true, 2);

            int? removed = null;
            layer.BlockRemoved += (_, index) => removed = index;
            Remove(layer, 1);

            Assert.Equal(1, removed);
            var visuals = Visuals(layer);
            Assert.Equal(2, visuals.Count);
            // Names are by position: the third block is the second now.
            Assert.EndsWith("2", MarkerText(visuals[1]));
            layer.Close();
        });
    }

    [Fact]
    public void Markers_ShowWhileThePanelIsOpen_AndThickenOnlyTheBlockShown()
    {
        OnStaThread(() =>
        {
            var layer = Layer(out var add);
            add(RealtimePresets.StandardId);
            add(RealtimePresets.SubtitleId);

            var visuals = Visuals(layer);
            Assert.All(visuals, v => Assert.Equal(Visibility.Collapsed, Marker(v).Visibility));

            layer.SetMarkers(true, 1);
            Assert.All(visuals, v => Assert.Equal(Visibility.Visible, Marker(v).Visibility));
            Assert.True(Body(visuals[1]).BorderThickness.Left > Body(visuals[0]).BorderThickness.Left);

            // Drawn the same again changes nothing — it is called on every redraw of the panel.
            var thickness = Body(visuals[1]).BorderThickness;
            layer.SetMarkers(true, 1);
            Assert.Equal(thickness, Body(visuals[1]).BorderThickness);

            layer.SetMarkers(false, 1);
            Assert.All(visuals, v => Assert.Equal(Visibility.Collapsed, Marker(v).Visibility));
            Assert.Equal(Body(visuals[0]).BorderThickness, Body(visuals[1]).BorderThickness);
            layer.Close();
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static RealtimeBlockPlacement Block(int i, string profile) =>
        new(new Rectangle(i * 100, 0, 80, 40), profile);

    /// <summary>
    /// An edit layer that is never shown — a window that covers the screen has no place in a test —
    /// with a way to add blocks as drawing one would.
    /// </summary>
    private static RealtimeEditWindow Layer(out Action<string> add)
    {
        var layer = new RealtimeEditWindow(
            Screen, [], RealtimeSettings.MaxBlockCount, RealtimePresets.StandardId,
            RealtimeTextOrientation.Horizontal, RealtimeProfileEditing.Choices([]));
        var addBlock = typeof(RealtimeEditWindow).GetMethod("AddBlock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int count = 0;
        add = profile =>
        {
            var bounds = new Rect(100 + count * 200, 400, 150, 60);
            count++;
            addBlock.Invoke(layer, [bounds, profile, RealtimeTextOrientation.Horizontal, true, null, 1.0, false]);
        };
        return layer;
    }

    private static List<object> Visuals(RealtimeEditWindow layer) =>
        [.. ((IEnumerable)typeof(RealtimeEditWindow)
            .GetField("_blocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(layer)!).Cast<object>()];

    private static void Remove(RealtimeEditWindow layer, int index) =>
        typeof(RealtimeEditWindow).GetMethod("RemoveBlock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(layer, [Visuals(layer)[index]]);

    private static T Part<T>(object visual, string name) =>
        (T)visual.GetType().GetProperty(name)!.GetValue(visual)!;

    private static Border Marker(object visual) => Part<Border>(visual, "Marker");
    private static string MarkerText(object visual) => Part<TextBlock>(visual, "MarkerText").Text;
    private static System.Windows.Controls.Primitives.Thumb Body(object visual) =>
        Part<System.Windows.Controls.Primitives.Thumb>(visual, "Body");

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
