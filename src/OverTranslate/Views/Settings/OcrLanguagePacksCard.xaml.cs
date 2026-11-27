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

/// <summary>辨識語言 in 設定 › 辨識模型: the languages the general model reads, then each language pack's state and action.</summary>
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
        Rows.Children.Add(Caption("S.Settings.OcrPacksGeneral", new Thickness(0, 10, 0, 6)));
        Rows.Children.Add(Tags(LanguageData.OcrSourceLanguages
            .Where(language => language.Pack is null && !LanguageData.IsAutomaticSource(language.Code))
            .Select(language => language.Code)));

        Rows.Children.Add(Caption("S.Settings.OcrPacksDedicated", new Thickness(0, 16, 0, 6)));
        var list = new StackPanel();
        foreach (var pack in OcrLanguagePacks.All)
            list.Children.Add(PackRow(pack, first: list.Children.Count == 0));
        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            Child = list,
        };
        box.SetResourceReference(Border.BorderBrushProperty, "AppSeparator");
        Rows.Children.Add(box);
    }

    /// <summary>
    /// 通用模型 and 語言專用模型, set as the realtime page's 預設／自訂 group headings are, with no note
    /// under them: the tags and the size pills say the rest.
    /// </summary>
    private static TextBlock Caption(string key, Thickness margin)
    {
        var caption = Label(LocalizationService.Get(key), "AppTextSecondary", 12);
        caption.FontWeight = FontWeights.SemiBold;
        caption.Margin = margin;
        return caption;
    }

    /// <summary>
    /// The languages a model reads, one neutral tag each, wrapping between tags. Squarer and greyer
    /// than the manga card's blue feature pills beside them, which describe the model rather than name
    /// what it reads. A list of names rather than a sentence, so nine of them can be counted and
    /// scanned, and a name is never broken across two lines.
    /// </summary>
    private static FrameworkElement Tags(IEnumerable<string> codes)
    {
        var names = codes.Select(code => LanguageData.OcrSourceLanguages.First(language => language.Code == code).Name).ToList();
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
        System.Windows.Automation.AutomationProperties.SetName(panel,
            string.Join(LocalizationService.Get("S.Settings.OcrPacksListSeparator"), names));
        foreach (var name in names)
        {
            var text = Label(name, "AppTextPrimary", 12);
            text.TextWrapping = TextWrapping.NoWrap;
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Child = text,
            };
            tag.SetResourceReference(Border.BackgroundProperty, "MangaCardChipBg");
            tag.SetResourceReference(Border.BorderBrushProperty, "MangaCardChipBorder");
            panel.Children.Add(tag);
        }
        return panel;
    }

    /// <summary>
    /// One pack: its languages as tags, a status pill in the manga card's 已就緒 style, and the one
    /// thing that can be done with it now. A failure, or a location that cannot be reached, is
    /// written out on a line of its own under the row, since it is a sentence rather than a state.
    /// </summary>
    private FrameworkElement PackRow(OcrLanguagePack pack, bool first)
    {
        var size = $"{pack.Megabytes} MB";
        // The assets location is on a drive that is not connected: nothing can be downloaded there.
        var unreachable = !pack.IsReady && !pack.IsDownloading && !Services.Assets.AssetsRelocation.IsAccessible;

        string status;
        string dot;
        if (pack.IsDownloading)
        {
            status = LocalizationService.Format("S.Settings.MangaModelsProgress",
                Math.Round(pack.Megabytes * pack.Percent / 100.0, 1), pack.Megabytes, pack.Percent);
            dot = "#52C4FA";
        }
        else if (pack.IsReady)
        {
            status = size;
            dot = "#34C759";
        }
        else
        {
            status = LocalizationService.Format("S.Settings.OcrPacksMissing", size);
            dot = "#8E8E93";
        }

        string? problem = unreachable ? LocalizationService.Get("S.Assets.StoreUnreachable")
            : pack.IsDownloading ? null
            : pack.FailureText;

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

        FrameworkElement? below = null;
        if (pack.IsDownloading)
        {
            below = Progress(pack.Percent);
        }
        else if (problem is not null)
        {
            var line = Label(problem, "AppError", 12);
            below = line;
        }

        return Row(Tags(pack.Codes), Pill(status, dot), action, below, first);
    }

    private static FrameworkElement Pill(string text, string dot)
    {
        var mark = new Ellipse
        {
            Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
            Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(dot)!,
        };
        var label = Label(text, "AppTextSecondary", 11.5);
        label.TextWrapping = TextWrapping.NoWrap;
        label.Margin = new Thickness(6, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(mark);
        content.Children.Add(label);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        pill.SetResourceReference(Border.BackgroundProperty, "MangaCardChipBg");
        pill.SetResourceReference(Border.BorderBrushProperty, "MangaCardChipBorder");
        return pill;
    }

    /// <summary>
    /// Tags on the left, the pill and the action on the right, a hairline above every row but the
    /// first, and the progress track or the problem under the row.
    /// </summary>
    private static FrameworkElement Row(FrameworkElement tags, FrameworkElement pill, FrameworkElement action,
        FrameworkElement? below, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        tags.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tags);

        pill.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        action.VerticalAlignment = VerticalAlignment.Center;
        action.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        if (below is not null)
        {
            below.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(below, 1);
            Grid.SetColumnSpan(below, 3);
            grid.Children.Add(below);
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
