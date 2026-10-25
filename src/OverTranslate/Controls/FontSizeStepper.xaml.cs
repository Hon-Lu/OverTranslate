using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataFormats = System.Windows.DataFormats;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Controls;

/// <summary>
/// A whole-number size between − and ＋, typed or stepped (#355).
/// </summary>
/// <remarks>
/// Each press of − or ＋ is one size applied; what is typed is applied on Enter or on leaving the
/// box — "3" on the way to "32" is not a size anyone asked for. No hold-to-repeat: where this sits
/// on the realtime bar every size applied rebuilds the overlays, so a run of steps is a run of
/// presses the user chose to make. Up and down step while the box has the keyboard.
/// </remarks>
public partial class FontSizeStepper : UserControl
{
    private int _value;

    public FontSizeStepper()
    {
        InitializeComponent();
        Show(_value);
    }

    public int Minimum { get; set; } = 1;

    public int Maximum { get; set; } = 99;

    public int Value
    {
        get => _value;
        set => Show(value);
    }

    /// <summary>Raised with each size applied, after it is shown.</summary>
    public event EventHandler<int>? ValueCommitted;

    private void DownBtn_Click(object sender, RoutedEventArgs e) => Commit(_value - 1);

    private void UpBtn_Click(object sender, RoutedEventArgs e) => Commit(_value + 1);

    // A click anywhere between the buttons, "px" included, is a click in the box.
    private void Field_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ValueBox.IsMouseOver) return;
        ValueBox.Focus();
        ValueBox.SelectAll();
        e.Handled = true;
    }

    // The first click into the box takes the whole number: it holds two digits at most, so typing
    // after the old one would be refused outright.
    private void ValueBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ValueBox.IsKeyboardFocusWithin) return;
        ValueBox.Focus();
        ValueBox.SelectAll();
        e.Handled = true;
    }

    private void ValueBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !IsDigits(e.Text);

    private void ValueBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !IsDigits(text))
            e.CancelCommand();
    }

    private void ValueBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: Commit(_value + 1); e.Handled = true; break;
            case Key.Down: Commit(_value - 1); e.Handled = true; break;
            case Key.Enter: CommitTyped(); e.Handled = true; break;
            case Key.Escape: Show(_value); break; // left unhandled: the host gives the keyboard back on it
            case Key.Space: e.Handled = true; break;
        }
    }

    private void ValueBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ValueBox.SelectAll();

    private void ValueBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTyped();

    /// <summary>
    /// Applies a size typed and not yet entered, for a host that knows the typing is over when the
    /// box itself has not been told — the keyboard taken away by another application.
    /// </summary>
    public void CommitPending() => CommitTyped();

    /// <summary>What was typed, held inside the range; an empty box goes back to the size in use.</summary>
    private void CommitTyped()
    {
        if (int.TryParse(ValueBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed))
            Commit(typed);
        else
            Show(_value);
    }

    private void Commit(int value)
    {
        var previous = _value;
        Show(value);
        if (_value != previous) ValueCommitted?.Invoke(this, _value);
    }

    private void Show(int value)
    {
        _value = Math.Clamp(value, Minimum, Maximum);
        ValueBox.Text = _value.ToString(CultureInfo.InvariantCulture);
        if (ValueBox.IsKeyboardFocused) ValueBox.SelectAll();
        DownBtn.IsEnabled = _value > Minimum;
        UpBtn.IsEnabled = _value < Maximum;
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);
}
