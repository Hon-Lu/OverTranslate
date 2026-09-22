namespace OverTranslate.Services.Ocr;

/// <summary>
/// The one place that decides whether the capture UI offers a choice of
/// <see cref="CaptureLayoutMode"/>. It does not, and has not for some time.
/// </summary>
/// <remarks>
/// <para><b><see cref="CaptureLayoutMode.Interface"/> IS ARCHIVED. IT HAS NO ENTRY POINT AND DOES
/// NOT NEED MAINTAINING OR TUNING.</b> Screenshot translation ships exactly one mode. Every
/// production path that could carry a mode — the toolbar's selector, the persisted setting, the
/// translate request — runs through <see cref="ForApplication"/> first, and that collapses
/// everything to <see cref="CaptureLayoutMode.General"/> while
/// <see cref="IsModeSelectionAvailable"/> is false. The selector itself is collapsed in the
/// toolbar. So <see cref="GroupingProfile.Interface"/> has no caller under <c>src/</c> at all: it
/// is reached only from the tests and from <c>OcrHarness --interface</c>.</para>
///
/// <para>WHAT THAT MEANS FOR ANYONE TUNING GROUPING. Measuring the interface mode proves nothing
/// about what a user gets, and "the user can switch modes" is not an argument that a cost is
/// affordable — there is nothing to switch to. A relaxation that buys speech and charges menus is
/// charging the only mode there is. The profile stays because it is the unrelaxed control the
/// grouping tests are written against, not because anything runs it.</para>
///
/// <para>IT IS NOT THE REALTIME PANEL MODE, whatever the two names suggest. Live translation has
/// its own enum, <see cref="Realtime.RealtimeBlockMode"/>, whose <c>Panel</c> member is chosen per
/// block, is live, and is worth maintaining. It never reads this type, and the profile it lands on
/// is <see cref="GroupingProfile.Realtime"/> — a separate instance that merely holds the same
/// figures today. Deleting this mode by pointing its callers at the live profile, or the other way
/// round, silently unfreezes one of them.</para>
///
/// <para>Kept rather than deleted so that a settings file naming Interface still loads, and so the
/// harness can still replay both grouping and placement paths for offline comparison. Turning the
/// mode back on is a product decision and a fresh round of corpus measurement, not a flag flip.
/// </para>
/// </remarks>
internal static class CaptureLayoutPolicy
{
    public static bool IsModeSelectionAvailable => false;

    public static CaptureLayoutMode ForApplication(CaptureLayoutMode requested) =>
        IsModeSelectionAvailable ? requested : CaptureLayoutMode.General;
}
