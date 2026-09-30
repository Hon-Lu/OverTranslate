namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Which line goes under the source picker about the manga models, if any.</summary>
internal enum MangaModelHint
{
    None,
    /// <summary>Vertical, but not Japanese (or 自動): picking Japanese is what brings the models in.</summary>
    ChooseJapanese,
    /// <summary>Vertical Japanese, and the models are here, switched on and able to run.</summary>
    Applied,
    Downloading,
    /// <summary>Vertical Japanese on a machine that could run them, with nothing downloaded.</summary>
    NotInstalled,
}

internal static class MangaModelHints
{
    /// <summary>
    /// The line under the source picker, from what the bar says and where the models stand.
    /// </summary>
    /// <remarks>
    /// <para>Only ever about vertical text: horizontal never reaches the models, and a line about
    /// them there would be a line about nothing the user asked for.</para>
    ///
    /// <para>Nothing either when the answer is no and there is nothing to do about it — a machine
    /// the models cannot run on, models that failed to load, or the user having switched them off
    /// on purpose. The card on the settings page says why in each case; the bar is not the place to
    /// repeat it on every capture.</para>
    /// </remarks>
    /// <param name="device"><see cref="MangaOcrEngine.DeviceSupport"/>.</param>
    /// <param name="engine"><see cref="MangaOcrEngine.Unavailable"/>.</param>
    internal static MangaModelHint For(
        bool vertical,
        string? sourceLanguage,
        MangaModelState store,
        MangaUnavailable device,
        MangaUnavailable engine,
        bool showHint)
    {
        if (!showHint || !vertical || store == MangaModelState.Unavailable) return MangaModelHint.None;
        if (device != MangaUnavailable.None) return MangaModelHint.None;
        if (engine is MangaUnavailable.LoadFailed or MangaUnavailable.ReadFailed or MangaUnavailable.Disabled)
            return MangaModelHint.None;

        if (!string.Equals(sourceLanguage, "JA", StringComparison.OrdinalIgnoreCase))
            return MangaModelHint.ChooseJapanese;

        return store switch
        {
            MangaModelState.Ready => MangaModelHint.Applied,
            MangaModelState.Downloading => MangaModelHint.Downloading,
            _ => MangaModelHint.NotInstalled,
        };
    }
}
