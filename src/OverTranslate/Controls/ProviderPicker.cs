using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using OverTranslate.Models;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;

namespace OverTranslate.Controls;

/// <summary>
/// Fills a translation-service picker, and keeps a hidden service that is still the selection out
/// of reach.
/// </summary>
/// <remarks>
/// <para>A service hidden in 設定 that a picker still has selected stays in its list
/// (<see cref="ProviderItem.IsHiddenSelection"/>), because the closed picker can only show what is
/// in its list. Everywhere else it is gone: its entry is collapsed and disabled in the open list,
/// and the moment the selection moves off it the entry is taken out of the list altogether.</para>
///
/// <para>The removal is what the arrow keys and the wheel depend on. A closed picker steps through
/// its list without ever having built the entries the collapsed style lives on — those only exist
/// once the list has been opened — so a style alone would leave the hidden service one keystroke
/// away. Taken out, it is not there to step back onto. Removing it does not move anything in an
/// open list, since it was never drawn there.</para>
///
/// <para>Layered on whatever item style the picker already has, as
/// <see cref="ComboBoxItemHints"/> is; the two wrap each other in either order.</para>
/// </remarks>
public static class ProviderPicker
{
    // Marks a picker this has already set up, so rebinding it does not wrap its item style again
    // or add a second handler.
    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached(
            "Attached", typeof(bool), typeof(ProviderPicker), new PropertyMetadata(false));

    /// <summary>
    /// Points <paramref name="box"/> at <paramref name="items"/>, regenerating its entries so their
    /// labels are read in the current interface language (see
    /// <see cref="Services.LocalizationService.BindLocalizedItems"/>).
    /// </summary>
    /// <remarks>Callers set SelectedValue afterwards: rebinding drops the selection.</remarks>
    public static void Bind(ComboBox box, IEnumerable<ProviderItem> items)
    {
        Attach(box);
        box.ItemsSource = null;
        box.ItemsSource = new ObservableCollection<ProviderItem>(items);
    }

    /// <summary>
    /// The labels the closed picker can show from here on: every entry but a hidden one that is no
    /// longer selected.
    /// </summary>
    public static IEnumerable<ProviderItem> ShowableItems(ComboBox box) =>
        box.Items.OfType<ProviderItem>()
            .Where(item => !item.IsHiddenSelection || ReferenceEquals(item, box.SelectedItem));

    private static void Attach(ComboBox box)
    {
        if ((bool)box.GetValue(AttachedProperty)) return;
        box.SetValue(AttachedProperty, true);

        var hidden = new DataTrigger { Binding = new Binding(nameof(ProviderItem.IsHiddenSelection)), Value = true };
        hidden.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        hidden.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
        hidden.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        box.ItemContainerStyle = new Style(typeof(ComboBoxItem), box.ItemContainerStyle) { Triggers = { hidden } };

        box.SelectionChanged += OnSelectionChanged;
    }

    /// <remarks>
    /// Only once something else is actually selected: a rebind passes through no selection at all,
    /// and the hidden entry is about to be selected again. Deferred out of the event the picker is
    /// still raising, at the one priority input cannot overtake, so the next keystroke already finds
    /// it gone.
    /// </remarks>
    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (box.SelectedItem is not ProviderItem { IsHiddenSelection: false }) return;
        if (box.ItemsSource is not ObservableCollection<ProviderItem> items) return;
        if (!items.Any(item => item.IsHiddenSelection)) return;

        box.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (!ReferenceEquals(box.ItemsSource, items)) return;
            foreach (var item in items.Where(item => item.IsHiddenSelection && !ReferenceEquals(item, box.SelectedItem)).ToList())
                items.Remove(item);
        });
    }
}
