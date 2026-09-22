using System.Text.Json.Serialization;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// What a watched block holds, as the user says it rather than as the block's shape implies it.
/// </summary>
/// <remarks>
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
/// keeps is the answer a newly drawn block starts on — see
/// <see cref="Models.RealtimeSettings.BlockMode"/> — persisted by name, so a third mode can be added
/// without changing what the file looks like and a name this build cannot read falls back to the
/// property's default.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RealtimeBlockMode
{
    /// <summary>A wide band of dialogue text — film subtitles, captions, a visual novel's text box.</summary>
    Subtitle,

    /// <summary>An interface panel, a tooltip, an item description — smaller text, chunkier block.</summary>
    Panel,
}
