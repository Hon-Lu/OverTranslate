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
/// <param name="CompareScale">
/// How large 對照顯示 draws this block's translation, as a fraction of <paramref name="Bounds"/> on
/// both axes: <see cref="RealtimeComparePlacement.MinScale"/> to 1.0, and 1.0 — never null — for
/// "the block's own size", which is what every block starts at and what the double-click on the
/// copy's label goes back to. A plain number rather than a nullable one because, unlike the offset,
/// there is no automatic rule for it to fall back on: 1.0 is the rule. Kept with the offset for the
/// same reasons, and like it only ever set by the user's hand, so a copy smaller than its block is
/// always one with an offset too.
/// </param>
/// <param name="CompareHidden">
/// Whether the user has turned 對照顯示 off for this block alone: its translation is then drawn over
/// the block, as it is with the switch off, while the other blocks keep their copies. Off for every
/// new block, because turning the switch on means "compare", and the exception is the block the
/// user picks out. Turning a copy off also lets go of it: the offset goes back to null and the
/// scale to 1.0, as the double-click on its label does, so a copy brought back starts again where
/// and as large as the automatic placement would have it. Carried here with the rest for the same
/// reasons.
/// </param>
public sealed record RealtimeBlockPlacement(
    Rectangle Bounds,
    RealtimeBlockMode Mode = RealtimeBlockMode.Subtitle,
    RealtimeTextOrientation Orientation = RealtimeTextOrientation.Horizontal,
    Point? CompareOffset = null,
    double CompareScale = 1.0,
    bool CompareHidden = false);

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
