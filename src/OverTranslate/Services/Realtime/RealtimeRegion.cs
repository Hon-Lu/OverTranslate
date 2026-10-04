using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// One user-drawn watch area, in physical screen pixels — the same coordinate space the screen grab
/// and the OCR boxes live in, so nothing on the recognition path has to know about DPI.
/// </summary>
/// <param name="Id">
/// Stable for as long as the block exists, so a result arriving after the user has re-entered edit
/// mode and rearranged things can be matched to the window it belongs to (or dropped).
/// </param>
/// <param name="Mode">
/// What the user says this block holds. Lives here rather than in the settings file because it
/// belongs to one block of one session: the same user watches a subtitle strip and a game panel at
/// once, and the answer is different for each. Defaults to <see cref="RealtimeBlockMode.Subtitle"/>,
/// which is what the great majority of blocks are.
/// </param>
/// <param name="Orientation">
/// Which way this block's text is written. Lives here for the same reason the mode does — see
/// <see cref="RealtimeTextOrientation"/> — and is independent of it: either kind of block can hold
/// either kind of writing.
/// </param>
public sealed record RealtimeRegion(
    int Id,
    Rectangle Bounds,
    RealtimeBlockMode Mode = RealtimeBlockMode.Subtitle,
    RealtimeTextOrientation Orientation = RealtimeTextOrientation.Horizontal);

/// <summary>
/// One block as the user has it arranged, before a session gives it an id — what edit mode hands
/// back and what the controller keeps between edits, so re-entering edit mode restores the modes
/// along with the rectangles.
/// </summary>
/// <remarks>
/// Whether the framing guidance is folded away is deliberately not here. It is not a property of a
/// block: it says whether this user still needs the instructions, which is the same answer for every
/// block they draw — so it lives in the settings file as
/// <see cref="Models.RealtimeSettings.GuidanceExpanded"/> and outlives the session.
/// </remarks>
/// <param name="CompareOffset">
/// Where 對照顯示 draws this block's translation, as an offset from <paramref name="Bounds"/> in
/// physical pixels — or null while the user has never dragged it, which means "wherever
/// <see cref="RealtimeComparePlacement.Place"/> puts it". Null rather than a computed value written
/// back, so a block that was never placed by hand keeps following the automatic rule as it is moved
/// and resized, and a block that was keeps where the user put it — the edit layer adjusts the
/// offset on a resize so the copy stays on the same side of the block. Carried here with the
/// rectangle because it belongs to the block the same way the mode does, and so it survives the
/// trips between editing and translating, and the switch being turned off and on again.
/// </param>
public sealed record RealtimeBlockPlacement(
    Rectangle Bounds,
    RealtimeBlockMode Mode = RealtimeBlockMode.Subtitle,
    RealtimeTextOrientation Orientation = RealtimeTextOrientation.Horizontal,
    Point? CompareOffset = null);

/// <summary>
/// The translated lines currently showing for one region. An empty list is a real result — it means
/// the region no longer holds any readable text — and clears the overlay rather than leaving the
/// previous subtitle stranded on screen.
/// </summary>
/// <param name="Generation">
/// Which generation of the session's translation cache produced these lines. An update that arrives
/// after the session was paused carries an older one and is dropped rather than painted onto a
/// screen the user has just cleared — see <see cref="RealtimeTranslationCache"/>.
/// </param>
public sealed record RealtimeRegionUpdate(
    int RegionId,
    IReadOnlyList<TranslatedBlock> Lines,
    int Generation);
