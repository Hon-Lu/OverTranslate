using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services.Ocr;
using ComboBox = System.Windows.Controls.ComboBox;

namespace OverTranslate.Controls;

/// <summary>
/// The rule a source-language box on a page or toolbar follows for languages that need a download:
/// the box never holds a language that cannot be read yet.
/// </summary>
/// <remarks>
/// <para>Picking one puts the box back on what it had and asks instead (<see cref="OcrLanguagePackCard"/>);
/// the language goes in only once its model is here. That is what lets everything downstream go on
/// assuming a selected language works: 翻譯, the start button and the running capsule have no third
/// state to deal with, and cancelling leaves nothing to undo.</para>
///
/// <para>Interception happens in the box's SelectionChanged because that is the one place every way of
/// choosing passes through — a click, or the arrow keys on a closed box. The owner calls
/// <see cref="Intercept"/> first in its handler, so that the settings never see the language. Code
/// setting the box passes through there as well, and goes to the fallback without asking.</para>
///
/// <para>After 下載 the language is held as pending, and goes in when the download lands unless the user
/// picked something else meanwhile, or the box has been locked by a session starting. Clicking away
/// only hides the card; the download carries on and the list's row shows how far along it is.</para>
/// </remarks>
internal sealed class OcrSourcePicker : IDisposable
{
    private readonly ComboBox _box;
    private readonly Func<string> _fallback;
    private readonly Popup _popup;
    private bool _subscribed;
    private bool _reverting;
    private bool _applying;
    private LangItem? _pending;

    /// <param name="fallback">What the box goes to when the language it holds is deleted.</param>
    public OcrSourcePicker(ComboBox box, Func<string> fallback)
    {
        _box = box;
        _fallback = fallback;
        _popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        box.Loaded += (_, _) => Subscribe();
        box.Unloaded += (_, _) => Unsubscribe();
        if (box.IsLoaded) Subscribe();
    }

    /// <summary>
    /// Call first in the box's SelectionChanged. True when this change is not one the owner should act
    /// on: the box has been put back, and the card is on its way.
    /// </summary>
    public bool Intercept(SelectionChangedEventArgs e)
    {
        if (_reverting) return true;

        if (_box.SelectedItem is LangItem { Pack: { IsReady: false } } language)
        {
            // Only someone choosing gets asked. A page putting back what it held after a rebind can
            // land here too, when the pack went while it was away: that is no question to put to
            // anyone, and the owner has to hear where the box ended up so the setting follows.
            if (!_box.IsDropDownOpen && !_box.IsKeyboardFocusWithin)
            {
                if (e.RemovedItems.OfType<LangItem>().FirstOrDefault() is { } previous)
                    _box.SelectedItem = previous;
                else
                    _box.SelectedValue = _fallback();
                return true;
            }

            _reverting = true;
            try
            {
                _box.SelectedItem = e.RemovedItems.OfType<LangItem>().FirstOrDefault();
            }
            finally
            {
                _reverting = false;
            }

            _box.IsDropDownOpen = false;
            // After the list has finished closing: a Popup that does not stay open would take the
            // press that closes the list as a press outside itself.
            _box.Dispatcher.BeginInvoke(() => ShowCard(language), DispatcherPriority.Input);
            return true;
        }

        // A later choice wins over a download still landing. The null that rebinding the items
        // produces is not a choice.
        if (!_applying && _box.SelectedItem is not null)
            _pending = null;
        return false;
    }

    private void ShowCard(LangItem language)
    {
        // A failure nobody was shown is not news to someone choosing again: they are asked again.
        if (language.Pack is { IsDownloading: false } pack && !_popup.IsOpen)
            pack.ClearFailure();

        // Asking while it is already on its way means they want this language when it lands.
        if (language.Pack is { IsDownloading: true })
            _pending = language;

        var card = new OcrLanguagePackCard(language);
        card.DownloadRequested += (_, _) => _pending = language;
        card.Finished += (_, _) => _popup.IsOpen = false;

        // The surface and the room its shadow is drawn in.
        var surface = new Border
        {
            Margin = new Thickness(12, 4, 12, 16),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = card,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        surface.SetResourceReference(Border.BackgroundProperty, "AppSurfaceBg");
        surface.SetResourceReference(Border.BorderBrushProperty, "AppBorder");

        _popup.HorizontalOffset = -12;
        _popup.Child = surface;
        _popup.IsOpen = true;
    }

    private void OnPackChanged(object? sender, EventArgs e) => Reconcile();

    /// <summary>
    /// Brings the box in line with the packs as they stand now: what was waiting on a download that has
    /// landed goes in, and a language whose pack has gone comes out.
    /// </summary>
    /// <remarks>
    /// Run again each time the box is loaded, because a page hears nothing while it is away, and the
    /// place a pack is deleted from is another page: the settings. Measured, a realtime page left
    /// holding 俄文 that way started its next session on 自動.
    /// </remarks>
    internal void Reconcile()
    {
        if (_pending is { Pack: { } waited } language)
        {
            if (waited.IsReady)
            {
                _pending = null;
                // Locked: a session started on another language while this downloaded, and the page
                // does not change under one.
                if (_box.IsEnabled)
                {
                    _applying = true;
                    try
                    {
                        _box.SelectedValue = language.Code;
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            }
            else if (!waited.IsDownloading)
            {
                // Failed, or cancelled. A retry from the card asks again.
                _pending = null;
            }
        }

        if (_box.SelectedItem is LangItem { Pack: { IsReady: false, IsDownloading: false } })
            _box.SelectedValue = _fallback();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        foreach (var pack in OcrLanguagePacks.All)
            pack.Changed += OnPackChanged;
        Reconcile();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        foreach (var pack in OcrLanguagePacks.All)
            pack.Changed -= OnPackChanged;
    }

    public void Dispose() => Unsubscribe();
}
