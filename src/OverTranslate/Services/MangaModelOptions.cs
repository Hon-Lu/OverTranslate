using System.Windows;
using OverTranslate.Services.Ocr.Manga;

namespace OverTranslate.Services;

/// <summary>
/// Keeps 使用漫畫直排模型 in the settings file and the engine that obeys it in step, and tells the
/// hints under the source pickers when anything they depend on has moved.
/// </summary>
/// <remarks>
/// <para>The engine holds the switch at run time and the settings file keeps it between runs. Either
/// can move it: the card writes the setting, and a delete or a new download puts the engine's back
/// on (<see cref="MangaOcrEngine.Enabled"/>). Each is copied to the other, and each copy stops at the
/// first side that already agrees.</para>
///
/// <para>Attached once, at app start, like <see cref="MangaModelStore.DiscardIncomplete"/>: a second
/// launch must not write the running instance's settings.</para>
/// </remarks>
internal static class MangaModelOptions
{
    private static MangaModelState? _lastState;

    /// <summary>
    /// Raised on the UI thread when a hint could read differently: the models arrived, went, or
    /// started downloading; or the switch or 顯示模型提示 moved.
    /// </summary>
    public static event EventHandler? Changed;

    public static void Attach()
    {
        var settings = SettingsService.Instance;
        var store = AppServices.MangaModels;
        var manga = AppServices.Ocr.Manga;
        _lastState = store.State;

        if (manga is not null)
        {
            // A switch left off for models no longer here — deleted by hand, or by an older build —
            // is back on, as a delete from the card would have left it.
            if (_lastState == MangaModelState.Ready)
                manga.Enabled = settings.Current.UseMangaModels;
            else
                settings.UpdateMangaModelOptions(useModels: true);

            manga.EnabledChanged += (_, _) => OnUi(() => settings.UpdateMangaModelOptions(useModels: manga.Enabled));
        }

        settings.MangaModelOptionsChanged += (_, _) =>
        {
            if (manga is not null) manga.Enabled = settings.Current.UseMangaModels;
            Raise();
        };

        // Many times a second while downloading; the hints only care when the state itself moves.
        store.Changed += (_, _) =>
        {
            var state = store.State;
            if (state == _lastState) return;
            _lastState = state;
            OnUi(Raise);
        };
    }

    /// <summary>The hint for a picker showing <paramref name="sourceLanguage"/> over text written this way.</summary>
    /// <param name="followsCaptureSwitch">
    /// Whether 顯示模型提示 applies: the capture toolbar's switch, which the realtime page ignores.
    /// </param>
    internal static MangaModelHint HintFor(bool vertical, string? sourceLanguage, bool followsCaptureSwitch)
    {
        var manga = AppServices.Ocr.Manga;
        return MangaModelHints.For(
            vertical,
            sourceLanguage,
            AppServices.MangaModels.State,
            manga?.DeviceSupport ?? MangaUnavailable.None,
            manga?.Unavailable ?? MangaUnavailable.None,
            !followsCaptureSwitch || SettingsService.Instance.Current.Capture.ShowModelHint);
    }

    /// <summary>The line under the realtime page's source picker; see <see cref="MangaModelHints.ForRealtimePage"/>.</summary>
    internal static MangaModelHint RealtimePageHintFor(string? sourceLanguage)
    {
        var manga = AppServices.Ocr.Manga;
        return MangaModelHints.ForRealtimePage(
            sourceLanguage,
            AppServices.MangaModels.State,
            manga?.DeviceSupport ?? MangaUnavailable.None,
            manga?.Unavailable ?? MangaUnavailable.None);
    }

    private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
