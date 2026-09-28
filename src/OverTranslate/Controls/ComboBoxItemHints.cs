using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OverTranslate.Models;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;

namespace OverTranslate.Controls;

/// <summary>
/// Shows each entry's <see cref="ProviderItem.Hint"/> as its tooltip while the list is open.
/// </summary>
/// <remarks>
/// <para>The capture toolbar, 文字翻譯 and 取詞翻譯 have no room for the hint as a line under the
/// picker, and there the only moment it is worth anything is while the user is choosing — so it goes
/// on the entry being pointed at. 即時翻譯 has the line as well, and does this too so that every
/// picker answers the same gesture the same way.</para>
///
/// <para>An entry with no hint binds to null, which is no tooltip at all rather than an empty one.
/// Only some engines have something worth saying, and the rest stay quiet.</para>
///
/// <para>Layered on top of whatever item style the picker already has, once it is loaded — the
/// picker's own style sets that, and replacing it outright would lose the highlight and focus
/// colours.</para>
/// </remarks>
public static class ComboBoxItemHints
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ComboBoxItemHints),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Marks the item style this added, so a picker that is loaded again (a window shown a second
    // time) is not wrapped again around its own wrapper.
    private static readonly DependencyProperty AppliedProperty =
        DependencyProperty.RegisterAttached(
            "Applied", typeof(bool), typeof(ComboBoxItemHints), new PropertyMetadata(false));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box || e.NewValue is not true) return;

        if (box.IsLoaded) Apply(box);
        else box.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var box = (ComboBox)sender;
        box.Loaded -= OnLoaded;
        Apply(box);
    }

    private static void Apply(ComboBox box)
    {
        if ((bool)box.GetValue(AppliedProperty)) return;
        box.SetValue(AppliedProperty, true);

        box.ItemContainerStyle = new Style(typeof(ComboBoxItem), box.ItemContainerStyle)
        {
            Setters = { new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ProviderItem.Hint))) },
        };
    }
}
