using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OverTranslate.Controls;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Tests;

/// <summary>
/// The glossary card's names and cells are cut short with an ellipsis (the list) or run past the box
/// edge (the title and the table); pointing at one shows the whole text, and only when some of it is
/// hidden.
/// </summary>
public class TrimmedToolTipTests
{
    private const string Long = "星環旅記（GS 測試）星環旅記（GS 測試）";

    [Fact]
    public void AListNameCutShortByItsMaxWidthIsTrimmed()
    {
        OnUiThread(() =>
        {
            var block = Laid(new TextBlock { Text = Long, FontSize = 14, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis });

            Assert.True(TrimmedToolTip.IsTrimmed(block));
        });
    }

    [Fact]
    public void AShortNameIsNot()
    {
        OnUiThread(() =>
        {
            var block = Laid(new TextBlock { Text = "星環旅記", FontSize = 14, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis });

            Assert.False(TrimmedToolTip.IsTrimmed(block));
        });
    }

    [Fact]
    public void ANameThatJustFitsIsNot()
    {
        OnUiThread(() =>
        {
            // Laid out at exactly its own width, as a name a hair under the limit would be.
            var block = new TextBlock { Text = "霧港偵探社", FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            block.Arrange(new Rect(block.DesiredSize));

            Assert.False(TrimmedToolTip.IsTrimmed(block));
        });
    }

    [Fact]
    public void ABlockWrittenAsRunsIsReadFromItsRuns()
    {
        OnUiThread(() =>
        {
            var block = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            block.Inlines.Add(new Run("傑諾"));
            block.Inlines.Add(new Run("  取代「" + Long + "」"));
            Laid(block, 120);

            Assert.Equal("傑諾  取代「" + Long + "」", TrimmedToolTip.TextOf(block));
            Assert.True(TrimmedToolTip.IsTrimmed(block));
        });
    }

    [Fact]
    public void ACellWhoseTextRunsPastTheBoxIsTrimmed()
    {
        OnUiThread(() =>
        {
            InWindow(new TextBox { Text = Long + Long, FontSize = 13, Width = 160 },
                box => Assert.True(TrimmedToolTip.IsTrimmed(box)));
        });
    }

    [Fact]
    public void ACellThatFitsIsNot()
    {
        OnUiThread(() =>
        {
            InWindow(new TextBox { Text = "アリサ", FontSize = 13, Width = 160 },
                box => Assert.False(TrimmedToolTip.IsTrimmed(box)));
        });
    }

    /// <summary>
    /// A box's text is laid out only once it is in a window, so the box is put in one: borderless,
    /// off every screen, never activated, and closed again straight after.
    /// </summary>
    private static void InWindow(TextBox box, Action<TextBox> check)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -32000, Top = -32000, Width = 300, Height = 80, Content = box,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            check(box);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TurningItOnGivesTheToolTipServiceSomethingToOpen()
    {
        OnUiThread(() =>
        {
            var block = new TextBlock { Text = Long };

            TrimmedToolTip.SetIsEnabled(block, true);

            // Without a ToolTip the service never asks, and the text could not be shown at all.
            Assert.NotNull(block.ToolTip);
        });
    }

    private static T Laid<T>(T element, double width = 400) where T : FrameworkElement
    {
        element.Measure(new Size(width, 100));
        element.Arrange(new Rect(new Size(Math.Min(width, element.DesiredSize.Width), element.DesiredSize.Height)));
        element.UpdateLayout();
        return element;
    }

    /// <summary>WPF elements may only be built on an STA thread, and xunit runs its tests on MTA ones.</summary>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
