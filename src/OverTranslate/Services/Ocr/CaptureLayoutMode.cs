using System.Text.Json.Serialization;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// What the user says this capture holds, chosen on the toolbar before translating.
/// </summary>
/// <remarks>
/// <para>Not the same thing as <see cref="OcrLayoutScript"/>, and the two must not be derived from
/// one another. The script is a property of the text the detector read; this is a property of the
/// capture the user framed, and it is the user who knows whether the box is a page of prose or a
/// game menu. It decides two things and nothing else: how far the grouping thresholds are relaxed,
/// and whether the translation is laid back onto each source line or set as one block.</para>
///
/// <para>Deliberately not carried on <see cref="OcrTextBlock"/>. A block is not general or an
/// interface — the capture is. Putting it there is how one field ends up meaning four things, which
/// is what the layout-metric split was spent undoing.</para>
///
/// <para>Persisted by name, so that a third mode can be added without changing what the settings
/// file looks like — see <see cref="Models.CaptureSettings.LayoutMode"/>. A name this build does not
/// know fails to deserialize, which is what makes the reader keep the property's default, and the
/// default is <see cref="General"/>.</para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureLayoutMode
{
    /// <summary>
    /// The default: articles, comics, dialogue — read in order, merged a little more readily, and
    /// drawn as one re-set block per group.
    /// </summary>
    /// <remarks>
    /// First, and therefore what an unset field reads as. That is not tidiness: most of what people
    /// frame is prose of some kind, and v1 had the two the other way round, which meant the app's
    /// out-of-the-box answer was the one for the rarer case.
    /// </remarks>
    General,

    /// <summary>
    /// Game UI, menus, multi-column panels: leave the arrangement as it is and hold the merge tests
    /// a little harder.
    /// </summary>
    /// <remarks>
    /// <b>Archived: nothing in the app selects this.</b> <see cref="CaptureLayoutPolicy"/> turns
    /// every requested mode into <see cref="General"/> before it reaches the pipeline, so this
    /// member survives only to keep old settings files loading and to give the harness and the
    /// grouping tests an unrelaxed control. It is not the live realtime panel mode — that is
    /// <see cref="Realtime.RealtimeBlockMode.Panel"/>, a different type. Read
    /// <see cref="CaptureLayoutPolicy"/> before spending any effort here.
    /// </remarks>
    Interface,
}
