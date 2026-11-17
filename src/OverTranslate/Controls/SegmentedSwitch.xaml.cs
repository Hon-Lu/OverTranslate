using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Controls;

/// <summary>
/// A choice between two, as a tray with a marker that slides under the chosen half — 自動／固定,
/// 隨機／固定 (#355) — or between three, given three labels (譯文對齊).
/// </summary>
/// <remarks>
/// The halves are given their words by resource key, so they follow the interface language like
/// every other label. Setting <see cref="IsSecondSelected"/> from code moves the marker without
/// raising <see cref="SelectionChanged"/>: that event is the user choosing, and a value being shown
/// is not a choice. The host sets Grid.IsSharedSizeScope above its switches, so their halves come
/// out one width — see the markup.
/// </remarks>
public partial class SegmentedSwitch : UserControl
{
    private bool _showing;

    public SegmentedSwitch()
    {
        InitializeComponent();

        // Each switch its own group: radios with no group name are grouped by parent, which is one
        // grid per switch, but saying so costs nothing and survives a change of layout.
        var group = Guid.NewGuid().ToString("N");
        FirstRadio.GroupName = group;
        SecondRadio.GroupName = group;
        ThirdRadio.GroupName = group;

        // Here rather than in XAML: checked while the markup is still loading, the first half would
        // raise Checked before the second half exists.
        _showing = true;
        FirstRadio.IsChecked = true;
        _showing = false;
    }

    /// <summary>Raised with true for the second half, false for the first, when the user picks one.</summary>
    /// <remarks>On a switch of three, true for either of the last two — see <see cref="IndexChanged"/>.</remarks>
    public event EventHandler<bool>? SelectionChanged;

    /// <summary>Raised with the part's index, from 0, when the user picks one.</summary>
    public event EventHandler<int>? IndexChanged;

    /// <summary>The chosen part, from 0. Set from code, it moves the marker and raises nothing.</summary>
    public int SelectedIndex
    {
        get => ThirdRadio.IsChecked == true ? 2 : SecondRadio.IsChecked == true ? 1 : 0;
        set
        {
            int index = Math.Clamp(value, 0, ThirdRadio.Visibility == Visibility.Visible ? 2 : 1);
            if (SelectedIndex == index) return;
            _showing = true;
            (index switch { 2 => ThirdRadio, 1 => SecondRadio, _ => FirstRadio }).IsChecked = true;
            _showing = false;
        }
    }

    public bool IsSecondSelected
    {
        get => SecondRadio.IsChecked == true;
        set => SelectedIndex = value ? 1 : 0;
    }

    /// <summary>The words on the two halves, as string resource keys.</summary>
    public void SetLabels(string firstKey, string secondKey)
    {
        FirstRadio.SetResourceReference(ContentProperty, firstKey);
        SecondRadio.SetResourceReference(ContentProperty, secondKey);
    }

    /// <summary>
    /// The words on three parts, as string resource keys: the third is unfolded and takes the same
    /// width as the other two.
    /// </summary>
    public void SetLabels(string firstKey, string secondKey, string thirdKey)
    {
        SetLabels(firstKey, secondKey);
        ThirdRadio.SetResourceReference(ContentProperty, thirdKey);
        ThirdColumn.SharedSizeGroup = "SegmentedSwitchHalf";
        ThirdRadio.Visibility = Visibility.Visible;
    }

    private void Radio_Checked(object sender, RoutedEventArgs e)
    {
        MoveThumb(animate: !_showing);
        if (_showing) return;
        SelectionChanged?.Invoke(this, SelectedIndex > 0);
        IndexChanged?.Invoke(this, SelectedIndex);
    }

    // A half's width is not known until the tray is laid out.
    private void Thumb_SizeChanged(object sender, SizeChangedEventArgs e) => MoveThumb(animate: false);

    /// <summary>
    /// The marker is the width of one half and starts under the first, so the second is one of its
    /// own widths to the right, the third two — the same slide as the page's trays.
    /// </summary>
    private void MoveThumb(bool animate)
    {
        var target = SelectedIndex * Thumb.ActualWidth;

        if (!animate || Thumb.ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            ThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            ThumbShift.X = target;
            return;
        }

        ThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
