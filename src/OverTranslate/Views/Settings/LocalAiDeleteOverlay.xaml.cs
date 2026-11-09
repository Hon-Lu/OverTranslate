using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.LocalAi;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

/// <summary>Asks before deleting a downloaded model, and deletes it.</summary>
public partial class LocalAiDeleteOverlay : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private LocalAiModel? _model;
    private bool _deleting;

    /// <summary>Raised once the question is gone, so the panel behind can take focus back.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised after a model's files are gone.</summary>
    internal event EventHandler<LocalAiModel>? Deleted;

    public LocalAiDeleteOverlay() => InitializeComponent();

    internal void Open(LocalAiModel model)
    {
        _model = model;
        WriteMessage();
        FailureText.Visibility = Visibility.Collapsed;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Visibility = Visibility.Visible;

        // The first time, the button has never been laid out and would refuse the focus, leaving
        // Escape to the panel behind.
        UpdateLayout();

        // The safe half takes focus, so a key still held from the click cannot land on 刪除.
        CancelButton.Focus();
    }

    /// <summary>Takes the question away, unanswered, when the panel it is over goes.</summary>
    internal void Dismiss()
    {
        if (!_deleting) Close();
    }

    private void Close()
    {
        if (_model is null) return;
        _model = null;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => WriteMessage();

    private void WriteMessage()
    {
        if (_model is { } model)
            ConfirmMessage.Text = LocalizationService.Format("S.LocalAi.DeleteConfirm", model.Name);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model || _deleting) return;

        _deleting = true;
        Buttons.IsEnabled = false;
        FailureText.Visibility = Visibility.Collapsed;
        try
        {
            await AppServices.LocalAi.DeleteAsync(model);
            Close();
            Deleted?.Invoke(this, model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(ex, "Local AI model {Model:l} could not be deleted", model.Id);
            FailureText.Visibility = Visibility.Visible;
        }
        finally
        {
            _deleting = false;
            Buttons.IsEnabled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled, or the panel behind would take the same Escape and close as well.
        if (e.Key == Key.Escape)
        {
            if (!_deleting) Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
