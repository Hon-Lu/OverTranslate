using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Settings;

/// <summary>辨識語言 in 設定 › 辨識模型: what is built in, and each language pack's state and action.</summary>
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
        Rows.Children.Add(BuiltInRow());
        foreach (var pack in OcrLanguagePacks.All)
            Rows.Children.Add(PackRow(pack));
    }

    /// <summary>
    /// The languages the installer reads, by the names the pickers give them, 自動 left out since it
    /// is a way of reading them rather than one of them. Nothing to do with it, so 內建 stands where
    /// the button would.
    /// </summary>
    private FrameworkElement BuiltInRow()
    {
        var name = Names(LanguageData.OcrSourceLanguages
            .Where(language => language.Pack is null && !LanguageData.IsAutomaticSource(language.Code))
            .Select(language => language.Code));

        var builtIn = Label(LocalizationService.Get("S.Settings.OcrPacksBuiltIn"), "AppTextSecondary", 12);
        builtIn.TextWrapping = TextWrapping.NoWrap;
        return Row(Label(name, "AppTextPrimary", 13), meta: null, builtIn, progress: null, first: true);
    }

    private FrameworkElement PackRow(OcrLanguagePack pack)
    {
        var size = $"{pack.Megabytes} MB";
        string meta;
        string dot;
        // The assets location is on a drive that is not connected: nothing can be downloaded there.
        var unreachable = !pack.IsReady && !pack.IsDownloading && !Services.Assets.AssetsRelocation.IsAccessible;
        if (unreachable)
        {
            meta = LocalizationService.Get("S.Assets.StoreUnreachable");
            dot = "AppError";
        }
        else if (pack.IsDownloading)
        {
            meta = LocalizationService.Format("S.Settings.MangaModelsProgress",
                Math.Round(pack.Megabytes * pack.Percent / 100.0, 1), pack.Megabytes, pack.Percent);
            dot = "AppAccent";
        }
        else if (pack.FailureText is { } failure)
        {
            meta = failure;
            dot = "AppError";
        }
        else
        {
            meta = LocalizationService.Format(pack.IsReady ? "S.Settings.OcrPacksReady" : "S.Settings.OcrPacksMissing", size);
            dot = pack.IsReady ? "AppSuccess" : "AppTextSecondary";
        }

        Button action;
        if (pack.IsDownloading)
        {
            action = Action("S.Common.Cancel", "FlatButton");
            action.Click += (_, _) => pack.CancelDownload();
        }
        else if (pack.IsReady)
        {
            action = Action("S.Settings.OcrPacksDelete", "FlatButton");
            action.Click += (_, _) => Delete(pack);
        }
        else if (pack.Failure is not null)
        {
            action = Action("S.Settings.MangaModelsRetry", "AccentButton");
            action.Click += (_, _) => pack.StartDownload();
        }
        else
        {
            action = Action("S.OcrPack.Download", "AccentButton");
            action.Click += (_, _) => pack.StartDownload();
        }
        action.IsEnabled = !unreachable;
        System.Windows.Automation.AutomationProperties.SetName(action,
            $"{action.Content} {pack.LanguagesText}");

        var metaLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var mark = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(Shape.FillProperty, dot);
        metaLine.Children.Add(mark);
        var metaText = Label(meta, !unreachable && (pack.Failure is null || pack.IsDownloading) ? "AppTextSecondary" : "AppError", 12);
        metaLine.Children.Add(metaText);

        return Row(Label(Names(pack.Codes), "AppTextPrimary", 13), metaLine, action,
            pack.IsDownloading ? Progress(pack.Percent) : null, first: false);
    }

    /// <summary>
    /// The languages as the pickers name them, listed: 俄文、烏克蘭文, the way the list beside them reads,
    /// rather than the sentence the download card uses.
    /// </summary>
    private static string Names(IEnumerable<string> codes) =>
        string.Join(LocalizationService.Get("S.Settings.OcrPacksListSeparator"),
            codes.Select(code => LanguageData.OcrSourceLanguages.First(language => language.Code == code).Name));

    /// <summary>
    /// Name and state on the left, the one action on the right, a hairline above every line but the
    /// first, and a progress track under the line while it downloads.
    /// </summary>
    private static FrameworkElement Row(FrameworkElement name, FrameworkElement? meta, FrameworkElement action,
        FrameworkElement? progress, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        if (meta is not null) text.Children.Add(meta);
        grid.Children.Add(text);

        action.VerticalAlignment = VerticalAlignment.Center;
        action.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        if (progress is not null)
        {
            progress.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(progress, 1);
            Grid.SetColumnSpan(progress, 2);
            grid.Children.Add(progress);
        }

        var line = new Border { Padding = new Thickness(0, 9, 0, 9), Child = grid };
        if (!first)
        {
            line.BorderThickness = new Thickness(0, 1, 0, 0);
            line.SetResourceReference(Border.BorderBrushProperty, "AppSeparator");
        }
        return line;
    }

    private static FrameworkElement Progress(int percent)
    {
        var track = new Grid { Height = 4 };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(percent, 0), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - percent, 0), GridUnitType.Star) });
        var back = new Border { CornerRadius = new CornerRadius(2) };
        back.SetResourceReference(Border.BackgroundProperty, "AppSeparator");
        Grid.SetColumnSpan(back, 2);
        var done = new Border { CornerRadius = new CornerRadius(2) };
        done.SetResourceReference(Border.BackgroundProperty, "AppAccent");
        track.Children.Add(back);
        track.Children.Add(done);
        return track;
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

    private Button Action(string key, string style) => new()
    {
        Content = LocalizationService.Get(key),
        Style = (Style)FindResource(style),
        Height = 30,
        Padding = new Thickness(14, 0, 14, 0),
    };
}
