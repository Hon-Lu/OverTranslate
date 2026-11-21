using System.Text.Json.Serialization;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// What a watched block holds, as the user says it rather than as the block's shape implies it.
/// </summary>
/// <remarks>
/// <para>Since modes (<see cref="ResolvedProfile"/>) this is how a mode's 語句合併 draws and reads —
/// Auto is 自動判斷, Subtitle 整段 and 合併, Panel 逐項 and 不合併 (see
/// <see cref="ResolvedProfile.Layout"/>) — and what each group was read as
/// (<see cref="OcrTextBlock.ReadAs"/>). The history below is why the question is asked at all.</para>
///
/// The two kinds need opposite corrections from the detector — a subtitle strip's glyphs are large
/// and want downscaling, an interface panel's are already small and want none — so something has to
/// say which is which. That used to be the block's width-to-height ratio, and the ratio is a
/// property of how the user happened to drag, not of what they are translating: the same subtitle
/// drawn 8:1 and drawn 3:1 landed on opposite sides of the threshold and got two different sets of
/// behaviour.
///
/// Measured on one session (2026-08-09 18:33, a 1894x269 block over English subtitles) the guess
/// was right and nothing was lost — but only because the block never dropped below 7.0. The same
/// afternoon, blocks drawn over the same video reached 3.33, and there the ratio alone would have
/// switched every pass to the panel fraction, which this type's own measurements put at a third of
/// passes reading nothing. Being right depended on how the user dragged.
///
/// The user knows what they are looking at and the program does not, so this is asked rather than
/// inferred. See <see cref="RealtimeDetectorSize"/> for what each mode is worth in detector size.
///
/// A block's own answer lives with the block and is redrawn every sitting. What the settings file
/// keeps is the mode a newly drawn block starts on — see
/// <see cref="Models.RealtimeSettings.NewBlockProfile"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RealtimeBlockMode
{
    /// <summary>A wide band of dialogue text — film subtitles, captions, a visual novel's text box.</summary>
    Subtitle,

    /// <summary>An interface panel, a tooltip, an item description — smaller text, chunkier block.</summary>
    Panel,

    /// <summary>
    /// Not said: each cluster of text in the block is read as whichever of the two its own content
    /// calls for, frame by frame — see <see cref="RealtimeAutoLayout"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is not the guess the remarks above retired. That one read the block's shape, which
    /// is how the user dragged; this one reads the text, which is what they are translating, and it
    /// answers per cluster rather than per block — so a dialogue box that turns into a list of
    /// choices, or a subtitle over a game's interface, is not one answer stretched over both.</para>
    ///
    /// <para>Last in the enum so the two explicit modes keep their values; it is what 標準, the mode a
    /// new block starts on, reads with — see <see cref="Models.RealtimeSettings.NewBlockProfile"/>.</para>
    /// </remarks>
    Auto,
}
