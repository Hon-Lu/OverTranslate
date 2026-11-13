using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Assets;
using OverTranslate.Services.Ocr;
using OverTranslate.Views.Shell;

namespace OverTranslate.Views.Settings;

/// <summary>模型存放位置, the first row of 進階設定.</summary>
public partial class SettingsPage
{
    /// <summary>What the row last said had just happened, until the dialog is opened again.</summary>
    private string? _assetsFlashKey;

    private void SubscribeAssets()
    {
        AssetsRelocation.Changed += OnAssetsRelocated;
        AppServices.LocalAi.Store.Changed += OnAssetsStoreChanged;
        AppServices.MangaModels.Changed += OnAssetsStoreChanged;
        foreach (var pack in OcrLanguagePacks.All) pack.Changed += OnAssetsStoreChanged;
        RefreshAssets();
    }

    private void UnsubscribeAssets()
    {
        AssetsRelocation.Changed -= OnAssetsRelocated;
        AppServices.LocalAi.Store.Changed -= OnAssetsStoreChanged;
        AppServices.MangaModels.Changed -= OnAssetsStoreChanged;
        foreach (var pack in OcrLanguagePacks.All) pack.Changed -= OnAssetsStoreChanged;
    }

    private bool _assetsRefreshQueued;

    // Raised per chunk during a download, from any thread: one refresh per dispatch is plenty.
    private void OnAssetsStoreChanged(object? sender, EventArgs e)
    {
        if (_assetsRefreshQueued) return;
        _assetsRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _assetsRefreshQueued = false;
            RefreshAssets();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnAssetsRelocated(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => FlashAssets("S.Assets.Changed"));

    /// <summary>Says under the path what just happened: changed in place, or moved and restarted.</summary>
    internal void FlashAssets(string key)
    {
        _assetsFlashKey = key;
        RefreshAssets();
    }

    private void RefreshAssets()
    {
        var current = AssetsRelocation.Current;
        AssetsPathText.Text = current;

        var volume = SystemAssetsMachine.Instance.Volume(current);
        var reachable = volume is not null;
        var used = reachable ? AssetsUsage.UsedBytes(current) : 0;

        AssetsUsageText.Visibility = reachable ? Visibility.Visible : Visibility.Collapsed;
        if (volume is { } v)
            AssetsUsageText.Text = used > 0
                ? LocalizationService.Format("S.Assets.Used",
                    AssetsLocationOverlay.OneDecimal(used), v.Drive, AssetsLocationOverlay.Whole(v.FreeBytes))
                : LocalizationService.Format("S.Assets.Free", v.Drive, AssetsLocationOverlay.Whole(v.FreeBytes));

        var downloading = AssetsRelocation.AnyDownloading;
        var (status, brush) =
            !reachable ? ("S.Assets.Unreachable", "AppError")
            : downloading ? ("S.Assets.WaitDownload", "AppTextSecondary")
            : _assetsFlashKey is { } flash ? (flash, "AppSuccess")
            : ((string?)null, "AppTextSecondary");
        AssetsStatusText.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is not null)
        {
            AssetsStatusText.Text = LocalizationService.Get(status);
            AssetsStatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }

        AssetsOpenBtn.Visibility = reachable ? Visibility.Visible : Visibility.Collapsed;
        AssetsResetBtn.Visibility = !reachable ? Visibility.Visible : Visibility.Collapsed;
        AssetsChangeBtn.IsEnabled = !downloading;
    }

    private void AssetsOpenBtn_Click(object sender, RoutedEventArgs e)
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

    private void AssetsChangeBtn_Click(object sender, RoutedEventArgs e)
    {
        _assetsFlashKey = null;
        RefreshAssets();
        ShellWindow.Current?.OpenAssetsLocation();
    }

    // The drive is gone: nothing there can be moved, so this only points back at the default.
    private void AssetsResetBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AssetsRelocation.SwitchWithoutMoving(AssetsRelocation.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }
}
