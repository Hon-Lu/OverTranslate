namespace OverTranslate.Views.Realtime;

/// <summary>
/// How one block window draws its translation: the 顯示外觀 values as that window takes them.
/// </summary>
/// <remarks>
/// <para>One reading of the request for both ways a window gets its look — built new
/// (RealtimeSessionController.ShowBlockWindows) and restyled in place when the running bar's panel
/// changes it (#355) — so the two cannot come to draw the same settings differently.</para>
///
/// <para>The two 進階選項 looks are off for a 對照顯示 copy, whatever the settings say: see
/// ShowBlockWindows for why a translation drawn beside its source gets the band.</para>
/// </remarks>
/// <param name="BorderColor">The fixed border colour, or null for a colour per group.</param>
/// <param name="FixedFontSize">The size every line is set at, or null for 自動.</param>
public readonly record struct RealtimeBlockAppearance(
    string TextColor,
    string ScrimColor,
    int ScrimOpacity,
    bool NaturalBackground,
    bool SampleTextColor,
    bool Border,
    string? BorderColor,
    double? FixedFontSize)
{
    /// <param name="compared">Whether the window is drawn as a 對照顯示 copy beside its block.</param>
    public static RealtimeBlockAppearance For(RealtimeStartRequest request, bool compared) => new(
        request.TextColor,
        request.ScrimColor,
        request.ScrimOpacity,
        request.NaturalBackground && !compared,
        request.SampleSourceTextColor && !compared,
        request.Border,
        request.FixedBorderColor,
        request.FixedFontSize);
}
