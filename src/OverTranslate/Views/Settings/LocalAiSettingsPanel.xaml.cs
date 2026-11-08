using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.LocalAi;
using OverTranslate.Services.Ocr.Manga;
using OverTranslate.Services.Providers;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace OverTranslate.Views.Settings;

/// <summary>What the 模型檔案 block shows.</summary>
internal enum LocalAiFileState
{
    NoModelChosen,
    NoGpu,
    NotDownloaded,
    NoSpace,
    Downloading,
    Verifying,
    Initializing,
    Failed,
    Ready,
}

/// <summary>本機 AI 翻譯's settings: which model, getting it, and how it is used.</summary>
/// <remarks>
/// Follows the download rather than holding it: the service owns that, so the window can be closed
/// and opened again while it runs, and comes back showing where it has got to.
/// </remarks>
public partial class LocalAiSettingsPanel : UserControl
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _save;
    private bool _loading;
    private bool _open;
    private LocalAiModel? _chosen;
    private IReadOnlyList<LocalAiGpuDevice>? _gpus;
    private DateTime _lastProgress;
    private LocalAiFileState? _shownState;
    private bool _detailsOpen;

    public LocalAiSettingsPanel()
    {
        InitializeComponent();
        _save = new DispatcherTimer { Interval = Debounce };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SaveTexts();
        };
    }

    private static LocalAiService Service => AppServices.LocalAi;

    private static LocalAiSettings Settings => SettingsService.Instance.Current.LocalAi;

    // ── Open / close ─────────────────────────────────────────────────────────

    internal void Open()
    {
        _loading = true;
        try
        {
            var settings = Settings;
            _chosen = Service.Store.Downloading ?? Service.ActiveModel;
            _detailsOpen = false;

            SegmentRadio.IsChecked = !settings.Batch;
            BatchRadio.IsChecked = settings.Batch;
            StyleBox.Text = settings.Style;
            TimeoutBox.Text = settings.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
            UpdateTextChrome();

            var idle = LocalAiSettings.IdleChoices
                .Select(minutes => new IdleChoice(minutes, LocalizationService.Format("S.LocalAi.Minutes", minutes)))
                .ToList();
            IdleBox.ItemsSource = idle;
            IdleBox.SelectedItem = idle.First(choice => choice.Minutes == settings.EffectiveIdleMinutes);
        }
        finally
        {
            _loading = false;
        }

        if (!_open)
        {
            _open = true;
            Service.Changed += OnServiceChanged;
        }

        Render();
        _ = LoadGpusAsync();
    }

    /// <summary>Writes what is still waiting and stops following the service.</summary>
    internal void Close()
    {
        Flush();
        ModelPickerButton.IsChecked = false;
        if (!_open) return;
        _open = false;
        Service.Changed -= OnServiceChanged;
    }

    private async Task LoadGpusAsync()
    {
        _gpus = await Service.GpusAsync();
        if (_open) Render();
    }

    private void OnServiceChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_open) return;
            // Progress arrives many times a second, a change of state only now and then.
            var state = StateNow();
            if (state == _shownState && DateTime.UtcNow - _lastProgress < ProgressEvery) return;
            Render();
        }, DispatcherPriority.Background);

    // ── What to show ─────────────────────────────────────────────────────────

    /// <summary>What the 模型檔案 block shows, from the facts.</summary>
    /// <param name="gpuChecked">Whether the check for a card has finished.</param>
    internal static LocalAiFileState StateOf(
        bool hasModel, bool gpuChecked, bool hasGpu, bool downloaded, bool downloading, bool verifying,
        bool initializing, bool failed, bool enoughSpace) =>
        !hasModel ? LocalAiFileState.NoModelChosen
        : gpuChecked && !hasGpu ? LocalAiFileState.NoGpu
        : initializing ? LocalAiFileState.Initializing
        : verifying ? LocalAiFileState.Verifying
        : downloading ? LocalAiFileState.Downloading
        : downloaded ? LocalAiFileState.Ready
        : failed ? LocalAiFileState.Failed
        : !enoughSpace ? LocalAiFileState.NoSpace
        : LocalAiFileState.NotDownloaded;

    private LocalAiFileState StateNow()
    {
        var store = Service.Store;
        var model = _chosen;
        var thisOne = model is not null && store.Downloading?.Id == model.Id;
        var free = store.FreeSpace();
        return StateOf(
            model is not null,
            _gpus is not null,
            _gpus is { Count: > 0 },
            model is not null && store.IsDownloaded(model),
            thisOne && store.Phase == LocalAiDownloadPhase.Downloading,
            thisOne && store.Phase == LocalAiDownloadPhase.Verifying,
            model is not null && Service.Initializing && Service.ActiveModel?.Id == model.Id,
            model is not null && Service.LastDownloadFailure?.Model.Id == model.Id,
            model is null || free is null || free.Value.FreeBytes >= store.SpaceNeeded(model));
    }

    private void Render()
    {
        var state = StateNow();
        _shownState = state;
        _lastProgress = DateTime.UtcNow;

        RenderPicker();

        var (chip, tone) = state switch
        {
            LocalAiFileState.NoModelChosen => ("", ""),
            LocalAiFileState.NoGpu => ("S.LocalAi.ChipUnavailable", "Error"),
            LocalAiFileState.Downloading => ("S.LocalAi.ChipDownloading", "Accent"),
            LocalAiFileState.Verifying or LocalAiFileState.Initializing => ("S.LocalAi.ChipPreparing", "Accent"),
            LocalAiFileState.Failed => ("S.LocalAi.ChipFailed", "Error"),
            LocalAiFileState.Ready => ("S.LocalAi.ChipReady", "Success"),
            _ => ("S.LocalAi.ChipNotDownloaded", "Warning"),
        };

        FileChip.Visibility = chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (chip.Length > 0)
        {
            FileChipText.Text = LocalizationService.Get(chip);
            FileChip.SetResourceReference(Border.BackgroundProperty, tone switch
            {
                "Accent" => "AppAccentSubtle",
                "Success" => "MangaCardReadyBg",
                "Error" => "MangaCardErrorBg",
                _ => "AppWarningSubtle",
            });
            FileChipText.SetResourceReference(TextBlock.ForegroundProperty, tone == "Accent" ? "AppAccent" : $"App{tone}");
        }

        ChooseHint.Visibility = Show(state == LocalAiFileState.NoModelChosen);
        DownloadPanel.Visibility = Show(state is LocalAiFileState.NotDownloaded or LocalAiFileState.NoSpace or LocalAiFileState.NoGpu);
        ProgressPanel.Visibility = Show(state is LocalAiFileState.Downloading or LocalAiFileState.Verifying or LocalAiFileState.Initializing);
        FailedPanel.Visibility = Show(state == LocalAiFileState.Failed);
        ReadyPanel.Visibility = Show(state == LocalAiFileState.Ready);

        var model = _chosen;
        switch (state)
        {
            case LocalAiFileState.NotDownloaded or LocalAiFileState.NoSpace or LocalAiFileState.NoGpu:
                RenderDownload(state, model!);
                break;
            case LocalAiFileState.Downloading or LocalAiFileState.Verifying or LocalAiFileState.Initializing:
                RenderProgress(state);
                break;
            case LocalAiFileState.Failed:
                FailedText.Text = FailureText(Service.LastDownloadFailure!.Value.Error);
                break;
            case LocalAiFileState.Ready:
                RenderReady(model!);
                break;
        }

        RenderDownloadedList();
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static string Gigabytes(long bytes) => (bytes / 1e9).ToString("F2", CultureInfo.InvariantCulture);

    private static string OneDecimal(long bytes) => (bytes / 1e9).ToString("F1", CultureInfo.InvariantCulture);

    private static string Whole(long bytes) =>
        (bytes / 1e9).ToString(bytes >= 100_000_000_000 ? "F0" : "F1", CultureInfo.InvariantCulture);

    private void RenderDownload(LocalAiFileState state, LocalAiModel model)
    {
        var store = Service.Store;
        DownloadButtonText.Text = LocalizationService.Format("S.LocalAi.Download", Gigabytes(store.DownloadSize(model)));
        DownloadButton.IsEnabled = state == LocalAiFileState.NotDownloaded && _gpus is { Count: > 0 } && store.Downloading is null;

        var free = store.FreeSpace();
        var active = Service.ActiveModel;
        var changing = active is not null && active.Id != model.Id;

        RuntimeNote.Text = LocalizationService.Format("S.LocalAi.RuntimeNote", (int)Math.Round((store.Runtime?.Runtime.Size ?? 0) / 1e6));
        RuntimeNote.Visibility = Show(state == LocalAiFileState.NotDownloaded && store.RuntimeDownloadSize > 0 && !changing);

        SpaceNote.Text = free is { } space ? LocalizationService.Format("S.LocalAi.FreeSpace", space.Drive, Whole(space.FreeBytes)) : "";
        SpaceNote.Visibility = Show(state == LocalAiFileState.NotDownloaded && free is not null && !changing);

        KeepUsingNote.Text = active is null ? "" : LocalizationService.Format("S.LocalAi.KeepUsing", active.ShortName);
        KeepUsingNote.Visibility = Show(state == LocalAiFileState.NotDownloaded && changing);

        BlockedNote.Visibility = Show(state != LocalAiFileState.NotDownloaded);
        if (state == LocalAiFileState.NoGpu)
        {
            BlockedNote.Text = LocalizationService.Get("S.Error.LocalAiNoGpu");
            BlockedNote.SetResourceReference(TextBlock.ForegroundProperty, "AppError");
        }
        else if (state == LocalAiFileState.NoSpace && free is { } lacking)
        {
            BlockedNote.Text = LocalizationService.Format("S.LocalAi.NoSpace", lacking.Drive, OneDecimal(store.SpaceNeeded(model)), OneDecimal(lacking.FreeBytes));
            BlockedNote.SetResourceReference(TextBlock.ForegroundProperty, "AppWarning");
        }
    }

    private void RenderProgress(LocalAiFileState state)
    {
        var store = Service.Store;
        var downloading = state == LocalAiFileState.Downloading;
        CancelButton.Visibility = Show(downloading);
        BackgroundNote.Visibility = Show(downloading);
        ProgressBar.Value = downloading ? store.Fraction : 1;
        ProgressBar.Opacity = downloading ? 1 : 0.55;

        if (state == LocalAiFileState.Verifying)
            ProgressText.Text = LocalizationService.Get("S.LocalAi.Verifying");
        else if (state == LocalAiFileState.Initializing)
            ProgressText.Text = LocalizationService.Get("S.LocalAi.Initializing");
        else
        {
            var text = LocalizationService.Format(
                "S.LocalAi.Progress",
                Gigabytes(store.DownloadedBytes), Gigabytes(store.TotalBytes), (int)(store.Fraction * 100));
            if (store.Remaining is { } left)
                text += " · " + (left.TotalMinutes < 1
                    ? LocalizationService.Get("S.LocalAi.RemainingUnderMinute")
                    : LocalizationService.Format("S.LocalAi.Remaining", (int)Math.Ceiling(left.TotalMinutes)));
            if (Service.ActiveModel is { } active && active.Id != _chosen?.Id)
                text += "\n" + LocalizationService.Format("S.LocalAi.KeepUsing", active.ShortName);
            ProgressText.Text = text;
        }
    }

    private void RenderReady(LocalAiModel model)
    {
        var host = Service.Host;
        var running = Service.ActiveModel?.Id == model.Id ? host.State : LocalAiServerState.Stopped;
        var (key, dot) = running switch
        {
            LocalAiServerState.Running => ("S.LocalAi.Running", "AppSuccess"),
            LocalAiServerState.Starting => ("S.LocalAi.Starting", "AppAccent"),
            LocalAiServerState.Restarting => ("S.LocalAi.Restarting", "AppWarning"),
            LocalAiServerState.Failed => ("S.LocalAi.StartFailed", "AppError"),
            _ => ("S.LocalAi.Stopped", "AppTextSecondary"),
        };

        RunText.Text = LocalizationService.Get(key);
        RunDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);

        var isRunning = running == LocalAiServerState.Running;
        ReleaseButton.Visibility = Show(isRunning);
        ReleaseNote.Visibility = Show(isRunning);
        DetailsButton.Visibility = Show(running == LocalAiServerState.Failed);
        DetailsBox.Text = host.FailureDetail;
        DetailsBox.Visibility = Show(running == LocalAiServerState.Failed && _detailsOpen);

        DeleteOnlyLink.Visibility = Show(Service.Store.Downloaded.Count <= 1);
    }

    private static string FailureText(Exception error) => MangaModelStore.Classify(error) switch
    {
        MangaDownloadFailure.Checksum => LocalizationService.Get("S.Settings.MangaModelsErrorChecksum"),
        MangaDownloadFailure.Disk => LocalizationService.Get("S.Settings.MangaModelsErrorDisk"),
        MangaDownloadFailure.Other => LocalizationService.Get("S.Settings.MangaModelsErrorOther"),
        _ => LocalizationService.Get("S.LocalAi.NetworkError"),
    };

    // ── The model list ───────────────────────────────────────────────────────

    /// <summary>One line of the list: a model, or a heading over a group of them.</summary>
    public sealed class ModelRow
    {
        internal LocalAiModel? Source { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string File { get; init; } = "";
        public string Vram { get; init; } = "";
        public bool CanChoose { get; init; }
        public bool Star { get; init; }
        public FontWeight NameWeight { get; init; } = FontWeights.SemiBold;
        public Brush? NameBrush { get; init; }
        public string Badge { get; init; } = "";
        public Brush? BadgeBackground { get; init; }
        public Brush? BadgeForeground { get; init; }
        public Visibility StarVisibility => Star ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DescriptionVisibility => Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether a model fits on the largest of the cards.</summary>
    internal static bool Fits(LocalAiModel model, IReadOnlyList<LocalAiGpuDevice>? cards) =>
        cards is null or { Count: 0 } || model.VramMiB <= cards.Max(card => card.MemoryMiB);

    private Brush Resource(string key) => (Brush)FindResource(key);

    private void RenderPicker()
    {
        var model = _chosen;
        ModelPickerText.Text = model?.Name ?? LocalizationService.Get("S.LocalAi.ChooseModel");
        ModelPickerText.SetResourceReference(TextBlock.ForegroundProperty, model is null ? "AppTextSecondary" : "AppTextPrimary");
        ModelPickerText.FontWeight = model is null ? FontWeights.Normal : FontWeights.SemiBold;
        ModelPickerStar.Visibility = Show(model?.Recommended == true);

        if (Service.Store.Catalog is not { } catalog) return;

        var downloaded = Service.Store.Downloaded;
        var rows = new List<ModelRow> { Heading(LocalizationService.Get("S.LocalAi.Recommended")) };
        rows.AddRange(catalog.Recommended.Select(item => RowFor(item, downloaded)));
        rows.Add(Heading(LocalizationService.Get("S.LocalAi.OtherVersions")));
        rows.AddRange(catalog.Others.Select(item => RowFor(item, downloaded)));

        _loadingList = true;
        ModelList.ItemsSource = rows;
        ModelList.SelectedItem = rows.FirstOrDefault(row => row.Source?.Id == model?.Id && row.Source is not null);
        _loadingList = false;
    }

    private bool _loadingList;

    private ModelRow Heading(string text) => new()
    {
        Name = text,
        File = LocalizationService.Get("S.LocalAi.ColumnFile"),
        Vram = LocalizationService.Get("S.LocalAi.ColumnVram"),
        NameWeight = FontWeights.Normal,
        NameBrush = Resource("AppTextSecondary"),
    };

    private ModelRow RowFor(LocalAiModel model, IReadOnlyList<LocalAiModel> downloaded)
    {
        var fits = Fits(model, _gpus);
        var isDownloaded = downloaded.Contains(model);
        return new ModelRow
        {
            Source = model,
            Name = model.Name,
            Description = LocalizationService.Get(model.DescriptionKey),
            File = $"{(model.Size / 1e9).ToString("F2", CultureInfo.InvariantCulture)} GB",
            Vram = $"{(model.VramMiB / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} GB",
            CanChoose = fits,
            Star = model.Recommended,
            NameBrush = Resource(fits ? "AppTextPrimary" : "AppTextSecondary"),
            Badge = !fits ? LocalizationService.Get("S.LocalAi.NotEnoughVram")
                : isDownloaded ? LocalizationService.Get("S.LocalAi.DownloadedTag") : "",
            BadgeBackground = Resource(fits ? "MangaCardReadyBg" : "AppWarningSubtle"),
            BadgeForeground = Resource(fits ? "AppSuccess" : "AppWarning"),
        };
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingList || ModelList.SelectedItem is not ModelRow { Source: { } model }) return;
        ModelPickerButton.IsChecked = false;
        _chosen = model;
        _detailsOpen = false;
        if (Service.Store.IsDownloaded(model)) Service.Use(model);
        Render();
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chosen is { } model && Service.Store.Downloading is null)
            _ = Service.DownloadAsync(model);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Service.Store.CancelDownload();

    private void ReleaseButton_Click(object sender, RoutedEventArgs e) => Service.Release();

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        _detailsOpen = !_detailsOpen;
        Render();
    }

    private void DeleteOnlyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_chosen is not { } model) return;
        DeleteAndRender(model);
        _chosen = null;
        Render();
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is LocalAiModel model) DeleteAndRender(model);
    }

    private void DeleteAndRender(LocalAiModel model)
    {
        try
        {
            Service.Delete(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DetailsBox.Text = ex.Message;
        }

        Render();
    }

    // ── Downloaded models ────────────────────────────────────────────────────

    public sealed record DownloadedRow(object Model, string Name, string Size, bool CanDelete, Visibility InUseVisibility);

    /// <param name="Display">Read by name: it is what the shared combo box template shows.</param>
    public sealed record IdleChoice(int Minutes, string Display);

    private void RenderDownloadedList()
    {
        var store = Service.Store;
        var downloaded = store.Downloaded;
        DownloadedCard.Visibility = Show(downloaded.Count >= 2);
        if (downloaded.Count < 2) return;

        var active = Service.ActiveModel;
        DownloadedSummary.Text = LocalizationService.Format(
            "S.LocalAi.DownloadedSummary", Gigabytes(downloaded.Sum(model => model.Size)), store.Root);
        DownloadedSummary.ToolTip = store.Root;
        DownloadedList.ItemsSource = downloaded.Select(model => new DownloadedRow(
            model, model.Name, $"{Gigabytes(model.Size)} GB",
            model.Id != active?.Id,
            Show(model.Id == active?.Id))).ToList();
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private void Persist(Action<LocalAiSettings> apply)
    {
        if (_loading) return;
        apply(Settings);
        SettingsService.Instance.Save();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) =>
        Persist(settings => settings.Batch = BatchRadio.IsChecked == true);

    private void IdleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IdleBox.SelectedValue is int minutes)
            Persist(settings => settings.IdleMinutes = minutes);
    }

    private void StyleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTextChrome();
        RestartSave();
    }

    private void RestartSave()
    {
        if (_loading) return;
        _save.Stop();
        _save.Start();
    }

    private void SaveTexts() => Persist(settings =>
    {
        settings.Style = StyleBox.Text.Trim();
        if (TryReadTimeout(out var timeout)) settings.TimeoutSeconds = timeout;
    });

    private void Flush()
    {
        RaiseTimeoutToMinimum();
        if (!_save.IsEnabled) return;
        _save.Stop();
        SaveTexts();
    }

    private void UpdateTextChrome()
    {
        StylePlaceholder.Visibility = Show(StyleBox.Text.Length == 0);
        TimeoutPlaceholder.Text = OpenAiCompatibleProvider.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        TimeoutPlaceholder.Visibility = Show(TimeoutBox.Text.Length == 0);
        TimeoutError.Text = TryReadTimeout(out _)
            ? ""
            : LocalizationService.Format("S.Settings.OpenAiTimeoutMin", OpenAiCompatibleProvider.MinTimeoutSeconds);
    }

    // The same rules as the OpenAI panel's box: see ServiceSettingsOverlay.
    private bool TryReadTimeout(out int? seconds)
    {
        seconds = null;
        var text = TimeoutBox.Text;
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < OpenAiCompatibleProvider.MinTimeoutSeconds)
            return false;
        seconds = value;
        return true;
    }

    private void RaiseTimeoutToMinimum()
    {
        if (!TryReadTimeout(out _))
            TimeoutBox.Text = OpenAiCompatibleProvider.MinTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void TimeoutBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !IsDigits(e.Text);

    private void TimeoutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private void TimeoutBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is not string pasted || !IsDigits(pasted))
            e.CancelCommand();
    }

    private void TimeoutBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTextChrome();
        RestartSave();
    }

    private void TimeoutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RaiseTimeoutToMinimum();

    private static bool IsDigits(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9');
}
