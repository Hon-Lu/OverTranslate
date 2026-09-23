using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using OverTranslate.Services;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

public class OcrDebugLiveUpdateTests
{
    [Fact]
    public void CachedRecognitionAppearsImmediatelyAndClearsForANewSelection() => OnSta(() =>
    {
        var settings = SettingsService.Instance;
        var debug = settings.Current.OcrDebug;
        var oldLines = debug.ShowLineBoxes;
        var oldGroups = debug.ShowGroupBoxes;
        debug.ShowLineBoxes = debug.ShowGroupBoxes = false;
        var window = new OverlayWindow([], [], 0, 0, 100, 100, "EN", "ZH-HANT", false);
        var eventField = typeof(SettingsService).GetField("OcrDebugChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Notify() => ((EventHandler?)eventField.GetValue(settings))?.Invoke(settings, EventArgs.Empty);
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var canvas = (Canvas)window.FindName("DebugCanvas");
            window.ShowOcrDebug([new OcrTextBlock("recognized but not translated", new Rect(2, 3, 50, 20),
                [new Rect(2, 3, 50, 9), new Rect(2, 14, 50, 9)])], 120, 230);
            Assert.Empty(canvas.Children);
            // Deliver the real settings notification without writing the user's settings file.
            debug.ShowGroupBoxes = true;
            Notify();
            Assert.Single(canvas.Children.Cast<UIElement>());
            debug.ShowLineBoxes = true;
            Notify();
            Assert.Equal(3, canvas.Children.Count);
            var box = (FrameworkElement)canvas.Children[0];
            Assert.True(double.IsFinite(Canvas.GetLeft(box)));
            Assert.Null(window.RenderOverlayForSelection(120, 230, 100, 100));
            window.SetBubblesVisible(false);
            Assert.Equal(Visibility.Visible, canvas.Visibility);
            debug.ShowGroupBoxes = false;
            Notify();
            Assert.Equal(2, canvas.Children.Count);
            window.ShowOcrDebug([], 500, 600);
            Notify();
            Assert.Empty(canvas.Children);
        }
        finally
        {
            window.Close();
            debug.ShowLineBoxes = oldLines;
            debug.ShowGroupBoxes = oldGroups;
        }
        Assert.DoesNotContain(((EventHandler?)eventField.GetValue(settings))?.GetInvocationList() ?? [],
            handler => ReferenceEquals(handler.Target, window));
    });

    [Fact]
    public void PopupDismissesOutsideAndUsesTheSharedSettingsSwitches()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Capture", "ToolbarWindow.xaml"));
        var popup = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MorePopup");
        Assert.Equal("False", (string?)popup.Attribute("StaysOpen"));
        Assert.Equal("{Binding ElementName=BarSurface}", (string?)popup.Attribute("PlacementTarget"));
        Assert.Equal("Bottom", (string?)popup.Attribute("Placement"));
        Assert.Equal("6", (string?)popup.Attribute("VerticalOffset"));
        Assert.DoesNotContain(popup.Descendants(), e => (string?)e.Attribute(x + "Name") == "DebugPointer");
        var more = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MoreBtn");
        Assert.Equal("{StaticResource ToolbarIconButton}", (string?)more.Attribute("Style"));
        Assert.Null(more.Attribute("Background"));
        Assert.Equal("30", (string?)more.Attribute("Width"));
        Assert.Equal("30", (string?)more.Attribute("Height"));
        Assert.Equal("{DynamicResource S.Toolbar.ShowMore}", (string?)more.Attribute("ToolTip"));
        Assert.Equal(new[] { "AutoTranslateSwitch", "SaveScreenshotSwitch", "DebugGroupsSwitch", "DebugLinesSwitch" },
            popup.Descendants().Where(e => e.Name.LocalName == "CheckBox").Select(e => (string?)e.Attribute(x + "Name")));
        Assert.All(popup.Descendants().Where(e => e.Name.LocalName == "CheckBox"),
            e => Assert.Equal("{StaticResource ToolbarMenuToggle}", (string?)e.Attribute("Style")));
        var scope = popup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "DebugScopePanel");
        Assert.Equal("{StaticResource DimWhenDisabled}", (string?)scope.Attribute("Style"));
    }

    [Fact]
    public void ScopeSwitchesImmediatelyBetweenSourceAndTranslation() => OnSta(() =>
    {
        var settings = SettingsService.Instance;
        var debug = settings.Current.OcrDebug;
        var oldScope = debug.ShowOnTranslation;
        var oldGroups = debug.ShowGroupBoxes;
        debug.ShowOnTranslation = false;
        debug.ShowGroupBoxes = true;
        var bounds = new Rect(2, 3, 50, 20);
        var window = new OverlayWindow([new TranslatedBlock("source", "translation", bounds)], [], 0, 0, 100, 100, "EN", "ZH-HANT", false);
        var eventField = typeof(SettingsService).GetField("OcrDebugChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var canvas = (Canvas)window.FindName("DebugCanvas");
            window.ShowOcrDebug([new OcrTextBlock("source", bounds)], 0, 0);
            Assert.NotEmpty(canvas.Children.Cast<UIElement>());
            Assert.Equal(Visibility.Collapsed, canvas.Visibility);
            window.SetBubblesVisible(false);
            Assert.Equal(Visibility.Visible, canvas.Visibility);
            window.SetBubblesVisible(true);
            Assert.Equal(Visibility.Collapsed, canvas.Visibility);
            debug.ShowOnTranslation = true;
            ((EventHandler?)eventField.GetValue(settings))?.Invoke(settings, EventArgs.Empty);
            Assert.Equal(Visibility.Visible, canvas.Visibility);
            debug.ShowOnTranslation = false;
            ((EventHandler?)eventField.GetValue(settings))?.Invoke(settings, EventArgs.Empty);
            Assert.Equal(Visibility.Collapsed, canvas.Visibility);
            window.ShowProcessing(0, 0, 100, 100, "processing");
            window.ShowOcrDebug([new OcrTextBlock("new source", bounds)], 0, 0);
            Assert.Equal(Visibility.Visible, canvas.Visibility);
        }
        finally
        {
            window.Close();
            debug.ShowOnTranslation = oldScope;
            debug.ShowGroupBoxes = oldGroups;
        }
    });

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
