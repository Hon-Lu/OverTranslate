using System.Windows;
using System.Windows.Input;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// 編輯翻譯區域預設 and 刪除翻譯區域預設, asked over the window from a row of the page's card.
/// </summary>
/// <remarks>
/// <para>編輯 renames: the row used to be its own text box, and a name that turned into a field under
/// the pointer read as a form. An empty name, or one that only repeats 預設 N, stores nothing of the
/// user's: the preset goes back to its automatic name (see
/// <see cref="RealtimeFramingDisplay.StoredName"/>).</para>
///
/// <para>刪除 asks first, as a mode's delete does, and names the target when the preset is a 預設 N
/// that several targets have. A target whose selected preset is deleted is left with none — see
/// <see cref="RealtimeFramingStore.Match"/>.</para>
/// </remarks>
public partial class RealtimeFramingOverlay : UserControl
{
    // How tall the preset's picture is drawn beside its description, in DIP.
    private const double ThumbnailHeight = 30;

    // The preset being asked about, and which question.
    private string? _id;
    private bool _deleting;

    public RealtimeFramingOverlay() => InitializeComponent();

    /// <summary>Opens 編輯 on the preset <paramref name="id"/>; nothing when it has gone since.</summary>
    public void OpenEdit(string id)
    {
        if (RealtimeFramingStore.ForId(id) is not { } preset) return;

        Show(id, deleting: false);
        NameBox.Text = RealtimeFramingDisplay.Name(preset);
        Thumbnail.Content = RealtimeFramingThumbnail.Create(preset, ThumbnailHeight);
        DetailText.Text = RealtimeFramingDisplay.EditDetail(preset);

        // The first time, the box has never been laid out and would refuse the focus.
        UpdateLayout();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>Asks before deleting the preset <paramref name="id"/>; nothing when it has gone since.</summary>
    public void OpenDelete(string id)
    {
        if (RealtimeFramingStore.ForId(id) is not { } preset) return;

        Show(id, deleting: true);
        DeleteMessage.Text = RealtimeFramingDisplay.DeleteQuestion(preset);

        UpdateLayout();
        DeleteCancelButton.Focus();
    }

    private void Show(string id, bool deleting)
    {
        _id = id;
        _deleting = deleting;
        EditCard.Visibility = deleting ? Visibility.Collapsed : Visibility.Visible;
        DeleteCard.Visibility = deleting ? Visibility.Visible : Visibility.Collapsed;
        SettingsService.Instance.RealtimeFramingChanged -= OnFramingChanged;
        SettingsService.Instance.RealtimeFramingChanged += OnFramingChanged;
        Visibility = Visibility.Visible;
    }

    // Deleted from the bar while the question was open: there is nothing left to ask about.
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
        SettingsService.Instance.RealtimeFramingChanged -= OnFramingChanged;
        Visibility = Visibility.Collapsed;
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

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting && _id is not null) RealtimeFramingStore.Forget(_id);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // A press on the card itself is not a press on the scrim behind it.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
