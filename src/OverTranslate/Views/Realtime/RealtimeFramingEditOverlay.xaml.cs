using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 編輯翻譯區域預設: renames one kept framing, from the ✎ on its row of the page's card.
/// </summary>
/// <remarks>
/// <para>A panel over the window rather than a name typed into the row: the row used to be its own
/// text box, and a name that turned into a field under the pointer read as a form. The panel also
/// says what the preset is, and where its blocks are changed — not here.</para>
///
/// <para>An empty name, or one that only repeats the target's, stores nothing of the user's: the
/// preset goes back to the name the target gives it (see
/// <see cref="RealtimeFramingDisplay.StoredName"/>).</para>
/// </remarks>
public partial class RealtimeFramingEditOverlay : UserControl
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(150));

    // How tall the preset's picture is drawn beside its description, in DIP.
    private const double ThumbnailHeight = 30;

    private string? _id;

    public RealtimeFramingEditOverlay() => InitializeComponent();

    /// <summary>Opens on the preset <paramref name="id"/>; nothing when it has gone since.</summary>
    public void Open(string id)
    {
        if (RealtimeFramingStore.ForId(id) is not { } preset) return;

        _id = id;
        Fill(preset);
        LocalizationService.LanguageChanged += OnLanguageChanged;
        SettingsService.Instance.RealtimeFramingChanged += OnFramingChanged;
        Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            // The card is cached as a bitmap while it scales, so its text does not soften on the way
            // in — the same as the other panels.
            Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

            var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
            fade.Completed += (_, _) => ReleaseAnimations();
            BeginAnimation(OpacityProperty, fade);

            var grow = new DoubleAnimation
            {
                From = 0.96, To = 1,
                Duration = FadeDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        // The first time, the box has never been laid out and would refuse the focus.
        UpdateLayout();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>Whether the panel is open.</summary>
    public bool IsOpen => _id is not null;

    private void Fill(RealtimeFramingPreset preset)
    {
        NameBox.Text = RealtimeFramingDisplay.Name(preset);
        Thumbnail.Content = RealtimeFramingThumbnail.Create(preset, ThumbnailHeight);
        DetailText.Text = RealtimeFramingDisplay.EditDetail(preset);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_id is not null && RealtimeFramingStore.ForId(_id) is { } preset)
            DetailText.Text = RealtimeFramingDisplay.EditDetail(preset);
    }

    // Deleted from the bar while the panel was open: there is nothing left to name.
    private void OnFramingChanged(object? sender, EventArgs e)
    {
        if (_id is not null && RealtimeFramingStore.ForId(_id) is null) Close();
    }

    private void Save()
    {
        if (_id is not null && RealtimeFramingStore.ForId(_id) is { } preset)
        {
            var stored = RealtimeFramingDisplay.StoredName(preset, NameBox.Text);
            if (!string.Equals(stored, preset.Name, StringComparison.Ordinal))
                RealtimeFramingStore.Rename(_id, stored);
        }

        Close();
    }

    private void Close()
    {
        if (_id is null) return;
        _id = null;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        SettingsService.Instance.RealtimeFramingChanged -= OnFramingChanged;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            // Opened again while fading out: that open owns the panel now.
            if (_id is null) Visibility = Visibility.Collapsed;
            ReleaseAnimations();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // An animation holds the properties it ran on after it finishes; handing them back drops the
    // composition layer the transition needed.
    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        Card.CacheMode = null;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Save();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled, or the window behind would take the same Escape too.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // A press on the card itself is not a press on the scrim behind it.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
