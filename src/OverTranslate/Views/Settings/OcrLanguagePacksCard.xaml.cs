using System.IO;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

/// <summary>辨識語言包 in 設定: which downloaded recognition languages are here, and removing them.</summary>
public partial class OcrLanguagePacksCard : UserControl
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public OcrLanguagePacksCard()
    {
        InitializeComponent();

        // While shown, as MangaModelCard does: static events holding an instance handler have to let
        // go of it when the page is navigated away from.
        Loaded += (_, _) =>
        {
            LocalizationService.LanguageChanged += OnChanged;
            foreach (var pack in OcrLanguagePacks.All) pack.Changed += OnChanged;
            Render();
        };
        Unloaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnChanged;
            foreach (var pack in OcrLanguagePacks.All) pack.Changed -= OnChanged;
        };
    }

    /// <summary>A failure the card cannot show itself; the page puts it on its status line.</summary>
    public event EventHandler<string>? ErrorRaised;

    private void OnChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        Rows.Children.Clear();
        foreach (var pack in OcrLanguagePacks.All)
        {
            var row = BuildRow(pack);
            if (Rows.Children.Count > 0) row.Margin = new Thickness(0, 12, 0, 0);
            Rows.Children.Add(row);
        }
    }

    private FrameworkElement BuildRow(OcrLanguagePack pack)
    {
        var status = pack.IsDownloading
            ? LocalizationService.Format("S.Settings.MangaModelsProgress",
                Math.Round(pack.Megabytes * pack.Percent / 100.0, 1), pack.Megabytes, pack.Percent)
            : pack.FailureText
              ?? LocalizationService.Get(pack.IsReady ? "S.Settings.MangaModelsReady" : "S.Settings.MangaModelsNotDownloaded");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(pack.LanguagesText, "AppTextPrimary", 13));
        text.Children.Add(Label(status, pack.Failure is null ? "AppTextSecondary" : "AppError", 12));

        Button action;
        if (pack.IsDownloading)
        {
            action = Action("S.Common.Cancel", "FlatButton");
            action.Click += (_, _) => pack.CancelDownload();
        }
        else if (pack.IsReady)
        {
            action = Action("S.Settings.MangaModelsDelete", "FlatButton");
            action.Click += (_, _) => Delete(pack);
        }
        else
        {
            action = Action("S.Settings.MangaModelsDownload", "AccentButton", pack.Megabytes);
            action.Click += (_, _) => pack.StartDownload();
        }

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);
        row.Children.Add(text);
        return row;
    }

    private void Delete(OcrLanguagePack pack)
    {
        try
        {
            pack.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(ex, "OCR language pack {Key} could not be deleted", pack.Key);
            ErrorRaised?.Invoke(this, LocalizationService.Format("S.Settings.MangaModelsDeleteFailed", ex.Message));
        }
    }

    private static TextBlock Label(string text, string brush, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "AppFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return label;
    }

    private Button Action(string key, string style, params object[] args) => new()
    {
        Content = args.Length == 0 ? LocalizationService.Get(key) : LocalizationService.Format(key, args),
        Style = (Style)FindResource(style),
        Height = 30,
        Padding = new Thickness(14, 0, 14, 0),
        Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
