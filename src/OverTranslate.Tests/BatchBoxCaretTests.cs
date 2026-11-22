using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Xml.Linq;
using OverTranslate.Controls;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Tests;

/// <summary>
/// The batch box's caret after Backspace on a fresh line. Typed through an IME — Bopomofo, or a
/// letter while Bopomofo is in English mode — the caret is left leaning backward onto the line break,
/// which draws it at the end of the line above while the next key still goes on this line (measured
/// in the app; see .ai/glossary-228/redesign/report-fix-2.md). A test cannot type through an IME,
/// so these put the caret in that state the way the IME's text store does, right after the edit,
/// and check that <see cref="LineStartCaret"/> leans it forward again. The direction is read from
/// WPF's own selection, the thing the caret is drawn from.
/// </summary>
public class BatchBoxCaretTests
{
    private const string NL = "\r\n";

    [Fact]
    public void LeftAloneTheCaretStaysLeaningOnTheLineAbove()
    {
        // The state the IME leaves, kept when nothing straightens it: the defect, so the tests below
        // are known to be testing something.
        InBox(enabled: false, box =>
        {
            TypeAt(box, "abc" + NL, "x");
            EditingCommands.Backspace.Execute(null, box);
            LeanBack(box);
            Pump();

            Assert.Equal("abc" + NL, box.Text);
            Assert.Equal(LogicalDirection.Backward, Leaning(box));
        });
    }

    [Fact]
    public void ALetterTypedOnLineTwoAndDeletedLeavesTheCaretOnLineTwo()
    {
        InBox(enabled: true, box =>
        {
            TypeAt(box, "abc" + NL, "x");
            EditingCommands.Backspace.Execute(null, box);
            LeanBack(box);
            Pump();

            Assert.Equal("abc" + NL, box.Text);
            Assert.Equal(5, box.CaretIndex);
            Assert.Equal(LogicalDirection.Forward, Leaning(box));
        });
    }

    [Fact]
    public void AfterSeveralEnters()
    {
        InBox(enabled: true, box =>
        {
            TypeAt(box, "abc" + NL + NL + NL, "x");
            EditingCommands.Backspace.Execute(null, box);
            LeanBack(box);
            Pump();

            Assert.Equal(9, box.CaretIndex);
            Assert.Equal(LogicalDirection.Forward, Leaning(box));
        });
    }

    [Fact]
    public void OnAnEmptyLineBetweenTwo()
    {
        InBox(enabled: true, box =>
        {
            box.Text = "abc" + NL + NL + "def";
            box.Select(5, 0);
            box.SelectedText = "字";
            box.Select(6, 0);
            EditingCommands.Backspace.Execute(null, box);
            LeanBack(box);
            Pump();

            Assert.Equal("abc" + NL + NL + "def", box.Text);
            Assert.Equal(5, box.CaretIndex);
            Assert.Equal(LogicalDirection.Forward, Leaning(box));
        });
    }

    [Fact]
    public void ABackspaceThatJoinsTwoLinesIsLeftAlone()
    {
        InBox(enabled: true, box =>
        {
            box.Text = "abc" + NL + "def";
            box.Select(5, 0);
            EditingCommands.Backspace.Execute(null, box);
            Pump();

            Assert.Equal("abcdef", box.Text);
            Assert.Equal(3, box.CaretIndex);
        });
    }

    [Fact]
    public void ASelectionIsLeftAlone()
    {
        InBox(enabled: true, box =>
        {
            box.Text = "abc" + NL + "def";
            box.Select(5, 3);
            Pump();

            Assert.Equal(5, box.SelectionStart);
            Assert.Equal(3, box.SelectionLength);
        });
    }

    [Fact]
    public void TheBatchBoxHasIt()
    {
        var xaml = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Settings", "GlossaryOverlay.xaml"));
        var box = xaml.Descendants().First(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "PasteBox");

        Assert.Equal("True", (string?)box.Attribute(XNamespace.Get("clr-namespace:OverTranslate.Controls") + "LineStartCaret.IsEnabled"));
    }

    /// <summary>Puts <paramref name="typed"/> in at the end of <paramref name="text"/>, as a key would, caret after it.</summary>
    private static void TypeAt(TextBox box, string text, string typed)
    {
        box.Text = text;
        box.Select(text.Length, 0);
        box.SelectedText = typed;
        box.Select(box.Text.Length, 0);
        Pump();
    }

    /// <summary>Runs what the edit queued for after its input, as the dispatcher would before the next key.</summary>
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    /// <summary>
    /// Leaves the caret where it is but leaning backward, as the IME's text store does when it puts
    /// the selection back after an edit.
    /// </summary>
    private static void LeanBack(TextBox box)
    {
        var selection = typeof(System.Windows.Controls.Primitives.TextBoxBase)
            .GetProperty("TextSelectionInternal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;
        var moving = Property(selection, "MovingPosition").GetValue(selection)!;
        var create = moving.GetType().GetInterfaces().SelectMany(i => i.GetMethods())
            .First(m => m.Name == "CreatePointer" && m.GetParameters() is [{ ParameterType.IsEnum: true }]);
        var back = create.Invoke(moving, [LogicalDirection.Backward])!;
        var set = selection.GetType().GetInterfaces().SelectMany(i => i.GetMethods())
            .First(m => m.Name == "SetCaretToPosition");
        set.Invoke(selection, [back, LogicalDirection.Backward, true, true]);
        Assert.Equal(LogicalDirection.Backward, Leaning(box));
    }

    /// <summary>Which way WPF's caret leans: the direction of the selection's moving end.</summary>
    private static LogicalDirection Leaning(TextBox box)
    {
        var selection = typeof(System.Windows.Controls.Primitives.TextBoxBase)
            .GetProperty("TextSelectionInternal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;
        var moving = Property(selection, "MovingPosition").GetValue(selection)!;
        return (LogicalDirection)Property(moving, "LogicalDirection").GetValue(moving)!;
    }

    private static PropertyInfo Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? target.GetType().GetInterfaces().Select(i => i.GetProperty(name)).First(p => p is not null)!;

    /// <summary>A batch-box-like TextBox in a window off every screen, never activated, closed after.</summary>
    private static void InBox(bool enabled, Action<TextBox> check)
    {
        OnUiThread(() =>
        {
            var box = new TextBox { AcceptsReturn = true, FontSize = 13, Width = 300, Height = 140 };
            LineStartCaret.SetIsEnabled(box, enabled);
            var window = new Window
            {
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Left = -32000, Top = -32000, Width = 320, Height = 160, Content = box,
            };
            try
            {
                window.Show();
                check(box);
            }
            finally
            {
                window.Close();
            }
        });
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
