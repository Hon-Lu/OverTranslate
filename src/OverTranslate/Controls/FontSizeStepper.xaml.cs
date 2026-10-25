using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataFormats = System.Windows.DataFormats;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Controls;

/// <summary>
/// A whole-number size between − and ＋, typed or stepped (#355).
/// </summary>
/// <remarks>
/// A press of − or ＋ steps at once, and holding it keeps stepping; the size is applied when the
/// button is let go, once, however many steps the hold took — on the realtime bar every size applied
/// redraws the overlays. In between, <see cref="ValueChanging"/> lets a host preview each step.
/// What is typed is applied on Enter or on leaving the box — "3" on the way to "32" is not a size
/// anyone asked for. Up and down step while the box has the keyboard.
/// </remarks>
public partial class FontSizeStepper : UserControl
{
    /// <summary>How long a button is held before it starts to repeat, and how often it then steps.</summary>
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(100);

    // What the box shows, and the size last handed to the host — apart only during a hold.
    private int _value;
    private int _applied;

    // The button being held, if one is, and when its next step is due — see StartRepeat.
    private readonly DispatcherTimer _repeat = new(DispatcherPriority.Input);
    private readonly Stopwatch _repeatClock = new();
    private ButtonBase? _repeatingButton;
    private TimeSpan _nextRepeatAt;

    public FontSizeStepper()
    {
        InitializeComponent();
        _repeat.Tick += Repeat_Tick;
        Show(_value);
    }

    public int Minimum { get; set; } = 1;

    public int Maximum { get; set; } = 99;

    public int Value
    {
        get => _value;
        set
        {
            Show(value);
            _applied = _value;
        }
    }

    /// <summary>Raised with each size applied, after it is shown.</summary>
    public event EventHandler<int>? ValueCommitted;

    /// <summary>Raised with each step of a hold, before the size it ends on is applied.</summary>
    public event EventHandler<int>? ValueChanging;

    /// <summary>
    /// A press on − or ＋: one step at once, and if the mouse is what pressed it, more while it is
    /// held. A press that holds nothing — the button invoked through accessibility — is one step,
    /// applied there and then.
    /// </summary>
    private void StepBtn_Click(object sender, RoutedEventArgs e)
    {
        var button = (ButtonBase)sender;
        Step(button);

        if (Mouse.Captured == button)
            StartRepeat(button);
        else
            Commit(_value);
    }

    // The one place every hold ends — let go, or the button greying out under the pointer at the
    // end of the range, which takes the capture with it and never sees the button come up.
    private void StepBtn_LostMouseCapture(object sender, MouseEventArgs e) => StopRepeat();

    /// <summary>
    /// Repeats a held button on a timer of its own, at input priority, against a clock.
    /// </summary>
    /// <remarks>
    /// A RepeatButton's repeat runs on a dispatcher timer at background priority, which is the lowest
    /// thing waiting: while any animation runs — the box's own blinking caret is enough — the frames
    /// go first and the repeat waits behind them, so a hold came out at anything from 60 ms to over a
    /// second a step (measured on the realtime page). Input priority is served between frames. Each
    /// step is due a fixed time after the last one was due, not after it ran, so the timer's own
    /// lateness does not add up over a hold; one that falls a whole step behind — a stall, or the
    /// pointer wandering off the button and back — starts counting again from now rather than
    /// catching up in a burst.
    /// </remarks>
    private void StartRepeat(ButtonBase button)
    {
        _repeatingButton = button;
        _repeatClock.Restart();
        _nextRepeatAt = RepeatDelay;
        ScheduleRepeat();
    }

    private void Repeat_Tick(object? sender, EventArgs e)
    {
        if (_repeatingButton is not { } button)
        {
            _repeat.Stop();
            return;
        }

        var now = _repeatClock.Elapsed;
        if (now >= _nextRepeatAt)
        {
            // Held, but off the button: paused, as any repeating button pauses, until it is back.
            if (button.IsPressed) Step(button);
            // That step reached the end of the range, greyed the button and so ended the hold.
            if (_repeatingButton is null) return;

            _nextRepeatAt += RepeatInterval;
            if (_nextRepeatAt <= now)
                _nextRepeatAt = now + RepeatInterval;
        }

        ScheduleRepeat();
    }

    private void ScheduleRepeat()
    {
        var wait = _nextRepeatAt - _repeatClock.Elapsed;
        _repeat.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _repeat.Start();
    }

    /// <summary>The hold is over: the size it ended on is applied, once.</summary>
    private void StopRepeat()
    {
        if (_repeatingButton is null) return;
        _repeatingButton = null;
        _repeat.Stop();
        Commit(_value);
    }

    /// <summary>One step from what the box shows, held inside the range, shown but not yet applied.</summary>
    private void Step(ButtonBase button)
    {
        var previous = _value;
        Show(_value + (ReferenceEquals(button, UpBtn) ? 1 : -1));
        if (_value != previous) ValueChanging?.Invoke(this, _value);
    }

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
        Show(value);
        if (_value == _applied) return;
        _applied = _value;
        ValueCommitted?.Invoke(this, _value);
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
