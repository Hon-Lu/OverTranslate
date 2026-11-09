using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Controls;
using Point = System.Windows.Point;

namespace OverTranslate.Views.Settings;

/// <summary>The page's sections, in the order they are stacked and the rail lists them.</summary>
public enum SettingsSection
{
    General,
    Models,
    Services,
    Hotkeys,
    Advanced,
}

/// <summary>
/// The page as a run of full-width sections: which one is in view for the rail, scrolling to one
/// when the rail asks, and the two sections that lay their halves side by side only while there is
/// room for both.
/// </summary>
public partial class SettingsPage
{
    // Below these widths the halves no longer fit side by side and go one under the other. The
    // shortcut rows need their box at its 140 minimum and the switch beside it; the models need the
    // manga card's three feature cells on one line.
    private const double HotkeyTwoColumnWidth = 780;
    private const double ModelsTwoColumnWidth = 640;

    private static readonly Duration SectionScrollDuration = new(TimeSpan.FromMilliseconds(320));
    private static readonly IEasingFunction SectionScrollEasing = new CubicEase { EasingMode = EasingMode.EaseOut };

    private SettingsSection _currentSection;
    private bool? _hotkeysTwoColumns;
    private bool? _modelsTwoColumns;

    /// <summary>The section at the top of the view changed, from scrolling or from <see cref="ScrollToSection"/>.</summary>
    public event EventHandler<SettingsSection>? CurrentSectionChanged;

    public SettingsSection CurrentSection => _currentSection;

    private FrameworkElement SectionElement(SettingsSection section) => section switch
    {
        SettingsSection.General => GeneralSection,
        SettingsSection.Models => ModelsSection,
        SettingsSection.Hotkeys => HotkeysSection,
        SettingsSection.Services => ServicesSection,
        _ => AdvancedSection,
    };

    /// <summary>Brings a section's heading to the top of the view, or as near as the page's end allows.</summary>
    public void ScrollToSection(SettingsSection section)
    {
        if (!IsLoaded || CardsScroll.Content is not Visual content)
        {
            // Asked before the page is up, by the rail on the way in: once it is laid out.
            Dispatcher.BeginInvoke(() => { if (IsLoaded) ScrollToSection(section); },
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var element = SectionElement(section);
        var top = element.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
        var offset = Math.Clamp(top, 0, CardsScroll.ScrollableHeight);

        // Said at once rather than when the scroll arrives: the rail answers the click, and a section
        // too near the end to reach the top would otherwise never be the one marked.
        SetCurrentSection(section);
        _scrollTarget = section;

        if (!SystemParameters.ClientAreaAnimation) CardsScroll.ScrollToVerticalOffset(offset);
        else SmoothScroll.To(CardsScroll, offset, SectionScrollDuration, SectionScrollEasing);
    }

    // The section a click asked for, held while the scroll it started runs, so the sections passed
    // on the way do not each light up in the rail.
    private SettingsSection? _scrollTarget;

    private void CardsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0) return;
        if (_scrollTarget is { } target)
        {
            var settled = Math.Abs(CardsScroll.VerticalOffset - TargetOffset(target)) < 1;
            if (!settled) return;
            _scrollTarget = null;
            return;
        }

        SetCurrentSection(SectionInView());
    }

    private double TargetOffset(SettingsSection section)
    {
        if (CardsScroll.Content is not Visual content) return CardsScroll.VerticalOffset;
        var top = SectionElement(section).TransformToAncestor(content).Transform(new Point(0, 0)).Y;
        return Math.Clamp(top, 0, CardsScroll.ScrollableHeight);
    }

    /// <summary>
    /// The last section whose heading is in the top third of the view, or the last one outright
    /// once the page is scrolled to its end, where the short sections at the foot can never reach
    /// the top.
    /// </summary>
    private SettingsSection SectionInView()
    {
        if (CardsScroll.Content is not Visual content) return SettingsSection.General;
        if (CardsScroll.ScrollableHeight > 0 && CardsScroll.VerticalOffset >= CardsScroll.ScrollableHeight - 1)
            return SettingsSection.Advanced;

        var line = CardsScroll.VerticalOffset + CardsScroll.ViewportHeight / 3;
        var current = SettingsSection.General;
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            var top = SectionElement(section).TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            if (top <= line) current = section;
        }
        return current;
    }

    private void SetCurrentSection(SettingsSection section)
    {
        if (section == _currentSection) return;
        _currentSection = section;
        CurrentSectionChanged?.Invoke(this, section);
    }

    // ── 快捷鍵: two to a line where they fit ──

    private void HotkeyGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        LayoutHotkeys(e.NewSize.Width >= HotkeyTwoColumnWidth);

    /// <summary>
    /// Two columns: 截圖｜取詞, 替換｜翻譯視窗, 即時翻譯. One: all of
    /// them down the left in that order. The rows keep their own bottom margin; the gap between the
    /// columns is split between the two sides.
    /// </summary>
    private void LayoutHotkeys(bool twoColumns)
    {
        if (_hotkeysTwoColumns == twoColumns) return;
        _hotkeysTwoColumns = twoColumns;

        (FrameworkElement Element, int Row, int Column)[] cells = twoColumns
            ?
            [
                (CaptureHotkeyRow, 0, 0), (QuickLookupHotkeyRow, 0, 1),
                (QuickTranslateHotkeyRow, 1, 0), (WindowHotkeyRow, 1, 1),
                (RealtimePauseHotkeyRow, 2, 0),
            ]
            :
            [
                (CaptureHotkeyRow, 0, 0), (QuickLookupHotkeyRow, 1, 0),
                (QuickTranslateHotkeyRow, 2, 0),
                (WindowHotkeyRow, 3, 0), (RealtimePauseHotkeyRow, 4, 0),
            ];

        foreach (var (element, row, column) in cells)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);

            var bottom = element.Margin.Bottom;
            element.Margin = !twoColumns ? new Thickness(0, 0, 0, bottom)
                : column == 0 ? new Thickness(0, 0, 5, bottom)
                : new Thickness(5, 0, 0, bottom);
            Grid.SetColumnSpan(element, twoColumns ? 1 : 2);
        }
    }

    // ── 辨識模型: the manga card beside the languages where they fit ──

    private void ModelsGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        LayoutModels(e.NewSize.Width >= ModelsTwoColumnWidth);

    private void LayoutModels(bool twoColumns)
    {
        if (_modelsTwoColumns == twoColumns) return;
        _modelsTwoColumns = twoColumns;

        Grid.SetRow(MangaCard, 0);
        Grid.SetColumn(MangaCard, 0);
        Grid.SetColumnSpan(MangaCard, twoColumns ? 1 : 3);
        Grid.SetRow(LanguagePacks, twoColumns ? 0 : 1);
        Grid.SetColumn(LanguagePacks, twoColumns ? 2 : 0);
        Grid.SetColumnSpan(LanguagePacks, twoColumns ? 1 : 3);
        LanguagePacks.Margin = twoColumns ? new Thickness(0) : new Thickness(0, 16, 0, 0);
        MangaCard.Horizontal = !twoColumns;
    }

    /// <summary>
    /// 已下載 on the section heading: what the section shows as downloaded and nothing else - the
    /// manga models with their DirectML runtime once they are ready, and each language pack that is.
    /// A runtime left behind by deleted manga models is not counted: nothing on the page says it is
    /// there, so a number that included it could not be accounted for. Blank while there is nothing.
    /// </summary>
    private void RefreshModelsUsed()
    {
        long bytes = 0;
        var manga = AppServices.MangaModels;
        if (manga.State == Services.Ocr.Manga.MangaModelState.Ready)
            bytes += FolderBytes(manga.Folder) + FolderBytes(manga.RuntimeFolder);
        foreach (var pack in OcrLanguagePacks.All)
            if (pack.IsReady) bytes += FolderBytes(pack.Store.Folder);

        ModelsUsedText.Text = bytes > 0
            ? LocalizationService.Format("S.Settings.ModelsUsed", FormatSize(bytes))
            : "";
        ModelsUsedText.Visibility = bytes > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static long FolderBytes(string? folder)
    {
        long bytes = 0;
        try
        {
            if (folder is not null && Directory.Exists(folder))
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                    bytes += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file removed while being counted; the next change counts again.
        }
        return bytes;
    }

    internal static string FormatSize(long bytes)
    {
        var megabytes = bytes / 1_000_000.0;
        if (megabytes >= 1000) return $"{megabytes / 1000:0.0} GB";
        if (megabytes >= 10) return $"{Math.Round(megabytes):0} MB";
        return $"{Math.Max(0.1, megabytes):0.0} MB";
    }

    private void OnModelsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshModelsUsed);

    private void SubscribeModels()
    {
        AppServices.MangaModels.Changed += OnModelsChanged;
        foreach (var pack in OcrLanguagePacks.All) pack.Changed += OnModelsChanged;
        RefreshModelsUsed();
    }

    private void UnsubscribeModels()
    {
        AppServices.MangaModels.Changed -= OnModelsChanged;
        foreach (var pack in OcrLanguagePacks.All) pack.Changed -= OnModelsChanged;
    }

    private void OpenModelsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.AssetsRoot);
            Process.Start(new ProcessStartInfo(AppDataPaths.AssetsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Format("S.Settings.OpenFolderFailed", ex.Message));
        }
    }
}
