using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OverTranslate.Controls;
using OverTranslate.Models;
using Xunit;
using ComboBox = System.Windows.Controls.ComboBox;

namespace OverTranslate.Tests;

// A hidden service a picker still has selected is shown while closed, and is never reachable again
// once the selection leaves it — by the arrow keys or the wheel on the closed picker above all,
// which step through the list without the open list's entries ever having been built.
//
// A real ComboBox in a window that is never shown: keys and the wheel are raised on it the way the
// input system would, and it answers with its own selection logic.
public class ProviderPickerTests
{
    [Theory]
    [InlineData(Key.Down, TranslationProvider.Youdao)]
    [InlineData(Key.Up, TranslationProvider.Bing)]
    public void Arrow_keys_leave_a_hidden_selection_and_cannot_return_to_it(Key away, TranslationProvider landed)
    {
        OnUiThread(() =>
        {
            var (box, source) = Picker("""["Microsoft"]""", TranslationProvider.Microsoft);
            using var _ = source;

            Press(box, source, away);
            Assert.Equal(landed, box.SelectedValue);

            // Back the other way, twice over: it steps past where Microsoft was.
            var back = away == Key.Down ? Key.Up : Key.Down;
            Press(box, source, back);
            Press(box, source, back);
            Assert.NotEqual(TranslationProvider.Microsoft, box.SelectedValue);
            Assert.DoesNotContain(box.Items.OfType<ProviderItem>(), item => item.Provider == TranslationProvider.Microsoft);
        });
    }

    [Fact]
    public void The_wheel_on_the_closed_picker_never_lands_on_a_hidden_selection_it_has_left()
    {
        OnUiThread(() =>
        {
            var (box, source) = Picker("""["Microsoft"]""", TranslationProvider.Microsoft);
            using var _ = source;

            // Leave it, then turn the wheel both ways across where it was.
            box.SelectedValue = TranslationProvider.Bing;
            Flush();
            foreach (var delta in new[] { -120, -120, 120, 120, 120, -120 })
            {
                box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent });
                Flush();
                Assert.NotEqual(TranslationProvider.Microsoft, box.SelectedValue);
            }
        });
    }

    [Fact]
    public void The_hidden_selection_stays_while_it_is_still_selected()
    {
        OnUiThread(() =>
        {
            var (box, source) = Picker("""["Microsoft"]""", TranslationProvider.Microsoft);
            using var _ = source;

            Assert.Equal(TranslationProvider.Microsoft, box.SelectedValue);
            Assert.Contains(ProviderPicker.ShowableItems(box), item => item.Provider == TranslationProvider.Microsoft);
        });
    }

    // What the capture toolbar measures its width from: the hidden selection only while selected.
    [Fact]
    public void Showable_items_drop_a_hidden_selection_once_it_is_left()
    {
        OnUiThread(() =>
        {
            var (box, source) = Picker("""["Microsoft"]""", TranslationProvider.Microsoft);
            using var _ = source;

            box.SelectedValue = TranslationProvider.Google;
            Assert.DoesNotContain(ProviderPicker.ShowableItems(box), item => item.Provider == TranslationProvider.Microsoft);
            Flush();
            Assert.Equal(
                LanguageData.Providers.Select(item => item.Provider).Where(p => p != TranslationProvider.Microsoft),
                box.Items.OfType<ProviderItem>().Select(item => item.Provider));
        });
    }

    // A rebind passes through no selection before the caller selects again; the hidden entry has to
    // survive that, or a hidden service in use would vanish from its own picker on every rebuild.
    [Fact]
    public void Rebinding_keeps_the_hidden_selection()
    {
        OnUiThread(() =>
        {
            var (box, source) = Picker("""["Microsoft"]""", TranslationProvider.Microsoft);
            using var _ = source;

            ProviderPicker.Bind(box, ProviderVisibility.MenuItems(["Microsoft"], TranslationProvider.Microsoft));
            Flush();
            box.SelectedValue = TranslationProvider.Microsoft;
            Flush();

            Assert.Equal(TranslationProvider.Microsoft, box.SelectedValue);
        });
    }

    private static (ComboBox Box, HwndSource Source) Picker(string hiddenJson, TranslationProvider selected)
    {
        var hidden = System.Text.Json.JsonSerializer.Deserialize<List<string>>(hiddenJson)!;
        var box = new ComboBox { SelectedValuePath = nameof(ProviderItem.Provider) };

        // Never shown: no WS_VISIBLE, so it is a source for input routing and nothing on screen.
        var source = new HwndSource(new HwndSourceParameters("ProviderPickerTests") { Width = 300, Height = 60, WindowStyle = 0 })
        {
            RootVisual = box,
        };

        ProviderPicker.Bind(box, ProviderVisibility.MenuItems(hidden, selected));
        box.SelectedValue = selected;
        Flush();
        return (box, source);
    }

    private static void Press(ComboBox box, HwndSource source, Key key)
    {
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Flush();
    }

    // Runs what the picker deferred, the way the next pass of the message loop would.
    private static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    // WPF elements may only be built on an STA thread, and xunit runs its tests on MTA ones.
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
