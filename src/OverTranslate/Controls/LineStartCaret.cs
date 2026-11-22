using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using TextBox = System.Windows.Controls.TextBox;

namespace OverTranslate.Controls;

/// <summary>
/// Keeps a multi-line box's caret on the line it is at the start of after an edit.
/// </summary>
/// <remarks>
/// <para>WPF's Backspace leaves the caret leaning backward, on the character it deleted towards.
/// Right after a line break that character is the break, and a caret leaning on it is drawn at the
/// end of the line above: type a letter on a fresh line, Backspace it, and the caret is shown back on
/// the previous line though it is still at the start of this one. A Bopomofo syllable dropped with
/// Backspace does the same. The next key still lands where the caret really is — on the line the
/// caret is not shown on, which reads as the text jumping lines.</para>
///
/// <para>Put back at the same place, the caret leans forward, onto the line it starts. Done after the
/// edit's own input has run, so neither the editor nor an IME finishing a composition puts it back,
/// and only while the caret is still there and nothing is selected.</para>
/// </remarks>
public static class LineStartCaret
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(LineStartCaret), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.TextChanged -= OnTextChanged;
        if ((bool)e.NewValue) box.TextChanged += OnTextChanged;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (!AtLineStart(box)) return;
        var at = box.CaretIndex;
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (box.CaretIndex == at && AtLineStart(box)) Settle(box);
        });
    }

    /// <summary>The caret, with nothing selected, just after a line break.</summary>
    internal static bool AtLineStart(TextBox box) =>
        box.SelectionLength == 0 && box.CaretIndex > 0 && box.CaretIndex <= box.Text.Length && box.Text[box.CaretIndex - 1] == '\n';

    /// <summary>The caret put back where it is, which leaves it leaning forward.</summary>
    internal static void Settle(TextBox box) => box.Select(box.CaretIndex, 0);
}
