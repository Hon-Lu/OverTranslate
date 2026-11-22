using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using ToolTipEventArgs = System.Windows.Controls.ToolTipEventArgs;

namespace OverTranslate.Controls;

/// <summary>
/// Shows a text's whole content as its ToolTip, but only while it does not fit: a
/// <see cref="TextBlock"/> cut short by its ellipsis, or a <see cref="TextBox"/> whose text runs past
/// its edge.
/// </summary>
/// <remarks>
/// <para>Decided when the ToolTip is about to open rather than kept up to date: whether a text fits
/// changes with the text, the font, the language and every resize, and the moment of pointing at it
/// is the only one where the answer matters.</para>
///
/// <para>A text that fits cancels its ToolTip instead of having none. A ToolTip that is switched off
/// lets WPF go on looking among the element's parents for one to show; cancelling ends the search
/// there (see <c>ToolbarLabel.Add</c>).</para>
/// </remarks>
public static class TrimmedToolTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrimmedToolTip), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.ToolTipOpening -= OnToolTipOpening;
        if (!(bool)e.NewValue) return;
        // Something for the ToolTip service to open; the text itself is put in when it does.
        element.ToolTip ??= string.Empty;
        element.ToolTipOpening += OnToolTipOpening;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!IsTrimmed(element))
        {
            e.Handled = true;
            return;
        }
        element.ToolTip = TextOf(element);
    }

    /// <summary>
    /// The text shown. A block written as Runs — the batch preview's 譯文 with what it replaces — has
    /// an empty <see cref="TextBlock.Text"/>, so its Runs are read instead.
    /// </summary>
    internal static string TextOf(FrameworkElement element) => element switch
    {
        TextBlock { Inlines.Count: > 0 } block => string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)),
        TextBlock block => block.Text,
        TextBox box => box.Text,
        _ => "",
    };

    /// <summary>Whether <paramref name="element"/>, as last laid out, shows less than all of its text.</summary>
    internal static bool IsTrimmed(FrameworkElement element)
    {
        switch (element)
        {
            case TextBox box:
                // The text's own width against the width it is shown in, however far it is scrolled.
                return box.ExtentWidth > box.ViewportWidth + 0.5;

            case TextBlock block when TextOf(block) is { Length: > 0 } shown:
                // The width the text would take on one line, in the block's own font, against the
                // width it was given. Its own MaxWidth or ellipsis do not enter into it.
                var text = new FormattedText(
                    shown,
                    CultureInfo.CurrentUICulture,
                    block.FlowDirection,
                    new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                    block.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(block).PixelsPerDip);
                var room = block.ActualWidth - block.Padding.Left - block.Padding.Right;
                return text.WidthIncludingTrailingWhitespace > room + 0.5;

            default:
                return false;
        }
    }
}
