namespace OverTranslate.Services.Ocr;

/// <summary>
/// The thresholds one grouping pass runs on. Decided by the capture mode, never by the source
/// language.
/// </summary>
/// <remarks>
/// <para>The grouper is handed this rather than a <see cref="CaptureLayoutMode"/> so that no rule
/// inside it ever asks which mode is on. It only ever knows what its limits are this time, which
/// keeps the product meaning of the modes at the seam where the user chose one.</para>
///
/// <para>All three fields sit on the set-solid path — the strictest geometry in the file, a pair
/// already through the leading and shared-edge tests. Two of them relax what counts as evidence for
/// such a pair. The third relaxes the leading itself, which is a boundary this type did not cross
/// before, and it is written down as one: a mode may move the limit that decides whether a pair is
/// set solid, and may still not move the shared-edge test that decides whether it is aligned.</para>
///
/// <para>THREE FIELDS, ON PURPOSE. Every one added has to arrive with a measured case from the
/// image corpus behind it — one was designed and then dropped again when the rule it would have
/// relaxed turned out never to fire on any image measured, and the third arrived only after the
/// version without it was built, measured, and found to relax the interface mode as well. A profile
/// with a dozen knobs is the mode quietly becoming a second source language, which is the thing
/// this whole seam exists to prevent. Nothing in the type stops a fourth being added; this is a
/// review contract, and a locked constructor would only produce a test-only factory to get around
/// it.</para>
/// </remarks>
/// <param name="TightlySetMinTextSizeRatio">
/// How close in text size two set-solid lines have to be. Only consulted for a pair that is already
/// tightly set; everything else is judged on the ordinary size gate.
/// </param>
/// <param name="WaiveLengthTestWhenSetSolid">
/// Whether a set-solid pair may join even when the line above is too short to have plausibly
/// wrapped. Speech bubbles open on one or two words, which no length test can call a wrap.
/// </param>
/// <param name="SolidLineAdvanceWhenWrapped">
/// <para>How far apart two set-solid lines may be, in line heights, when the line above was long
/// enough to have run out of room. A shorter line above is held to
/// <see cref="OcrTextBlockGrouper.SolidLineAdvance"/> whatever this says.</para>
///
/// <para>MEASURED, NOT CHOSEN. Six long-form web pages were annotated by hand before any threshold
/// moved, and their pairs sort into two populations with a gap between: a paragraph's seams run up
/// to <c>1.45</c>, and the list entries that must stay apart start at <c>1.68</c> — 1.68, 1.72,
/// 1.76 and 1.79 for the four held by geometry alone. The relaxed figure sits on the lower edge of
/// that gap. Before it, the general mode joined 24 of 55 paragraph seams and refused 31, every one
/// of the refusals wrong; after it, the six pages go from 140 groups to 113.</para>
///
/// <para><b>THE MARGIN IS NOT 0.23 EVERYWHERE.</b> That gap was measured on bulleted lists in
/// running text. A game settings panel's checkbox entries are a different population and sit at
/// <c>1.47</c> and above — <b>0.02 line heights</b> above the relaxed figure, which is the narrowest
/// margin anywhere in this rule. The interface mode is the answer to that (it keeps 1.20, so its
/// margin is 0.27), but the general mode is the default, so a user who frames a settings panel
/// without switching modes is standing on the 0.02. Recorded as a known cost rather than closed.
/// See <c>region-panel-en/game-menu-en.png</c>.</para>
///
/// <para><b>It does nothing for a news portal's headline list, and no larger figure would.</b>
/// Those are set <i>tighter</i> than running prose — 1.22 and 1.23 against a paragraph's 1.30 to
/// 1.40 (see <c>OcrTextBlockGrouper.StartsWithListBullet</c>) — so on this axis the two populations
/// are ordered the wrong way round, and every leading limit reaches the headlines before it reaches
/// the paragraphs. Raising this number buys strung-together headlines, not paragraphs. It is why
/// only one mode takes the relaxation at all.</para>
/// </param>
/// <param name="RefuseSpeakerLineStarts">
/// <para>Whether a line opening like a chat entry — timestamp, channel tag, @handle, name and colon —
/// refuses to continue the line above, and whether a line holding only such a speaker refuses to be
/// continued. See <see cref="SpeakerLineStart"/>.</para>
///
/// <para>MEASURED CASE: the 22 chat-room captures under <c>.ai/test-images/chat-room</c>, five of
/// them hand-marked. Chat entries sit at 0.62 to 1.16 line heights, one edge, one size — inside every
/// set-solid limit — so the live panel path joined 11 pairs of separate messages across the five, all
/// nine rows of one English log into a single string. Only the live panel path takes it: the
/// screenshot modes have no chat corpus behind them and the interface mode's figures are its v1
/// behaviour.</para>
/// </param>
internal sealed record GroupingProfile(
    double TightlySetMinTextSizeRatio,
    bool WaiveLengthTestWhenSetSolid,
    double SolidLineAdvanceWhenWrapped,
    bool RefuseSpeakerLineStarts)
{
    /// <summary>
    /// Interfaces: game UI, menus, multi-column panels. The conservative half of the pair, and what
    /// every capture was grouped on before modes existed.
    /// </summary>
    /// <remarks>
    /// This was called Standard while it was the default. It is neither standard nor the default in
    /// v2 — the material people actually point this at is prose — but the figures have not moved,
    /// which is what makes the swap provable: an Interface run reproduces every corpus output saved
    /// under the old name byte for byte.
    /// </remarks>
    public static GroupingProfile Interface { get; } = new(
        TightlySetMinTextSizeRatio: OrdinaryMinTextSizeRatio,
        WaiveLengthTestWhenSetSolid: false,
        SolidLineAdvanceWhenWrapped: UnrelaxedSolidLineAdvance,
        RefuseSpeakerLineStarts: false);

    /// <summary>
    /// The default: articles, comics, dialogue, and most of what anyone frames. Relaxed only where a
    /// pair has already passed the set-solid geometry.
    /// </summary>
    /// <remarks>
    /// <para>Waives the length test, and only that. A speech bubble opens on one or two words —
    /// "WHY ARE", "HOW", "THE" — and no length test can call those a wrap, because as a statement
    /// about text in general it is right: a line that short did not run out of room. What makes it
    /// wrong here is the container. Inside a bubble the line ends where the bubble does, so a short
    /// opening line is the normal shape of speech rather than evidence that nothing follows.</para>
    ///
    /// <para>The user is the one who knows a bubble is a bubble, which is why this hangs off their
    /// declaration rather than off anything measured. It stays on the strictest path in the file —
    /// the pair must already be set solid, sharing an edge to within a third of a line height — so
    /// what is waived is one piece of evidence, never the geometry.</para>
    ///
    /// <para>Halving the length test for everyone was measured as the alternative and rejected: it
    /// buys the same eight comic pairs and charges ten wrong joins to game panels for them, which
    /// is precisely the cost a declared mode exists to avoid. See
    /// <c>OcrTextBlockGrouper.IsSetSolidUnder</c>.</para>
    ///
    /// <para>The leading limit is relaxed too, and that one is not free. It buys paragraphs — 27
    /// groups across six annotated web pages, 34 more across the older corpus, all of them prose
    /// that was being cut into half-sentences — and it charges 16 wrong joins for them, news
    /// headlines and wiki timeline entries strung together. That trade was accepted for this mode
    /// and refused for <see cref="Interface"/>, which is the whole reason the figure sits on the
    /// profile: measured with it applied to both, the interface mode took 15 of those 16 wrong
    /// joins as well, and a mode nobody can escape to is not a mode.</para>
    ///
    /// <para>The size ratio is lowered as well, and 0.80 is where the measurement put it. Hand
    /// lettering is uneven — the same sentence's lines come back 0.83 to 0.88 of each other across
    /// the ten comic pages — so the ordinary figure refuses speech that is plainly one size. Going
    /// further was tried: 0.70 joins the last hand-marked group (a two-line bubble whose lines read
    /// 0.71) and takes the corpus from 23 groups to the hand-marked 22, but it also adds nineteen
    /// wrong joins across the other seven image sets when this mode is pointed at them — a
    /// breadcrumb over its page title, a stat row over the row below it, three pairs of identical
    /// level labels. One right answer for nineteen wrong ones is the wrong trade, so the last group
    /// stays apart and 23 is the number this corpus reaches.</para>
    /// </remarks>
    public static GroupingProfile General { get; } = new(
        TightlySetMinTextSizeRatio: 0.80,
        WaiveLengthTestWhenSetSolid: true,
        SolidLineAdvanceWhenWrapped: 1.45,
        RefuseSpeakerLineStarts: false);

    /// <summary>
    /// The live-screen path's own profile. It is not <see cref="Interface"/>, it merely holds the
    /// same figures today.
    /// </summary>
    /// <remarks>
    /// <para>Its leading limit stays unrelaxed, and that is a measurement rather than caution about
    /// a path nobody looked at: across 251 subtitle and HUD captures the relaxed figure changed not
    /// one verdict, and the two lines of a single subtitle read 0.74 to 0.98 line heights apart —
    /// a quarter of a line below even the unrelaxed limit. Relaxing here would buy nothing and
    /// would put the live path's behaviour on a figure measured on web pages.</para>
    ///
    /// <para>Realtime has no <see cref="CaptureLayoutMode"/>: there is no toolbar in front of a running
    /// video and nobody to answer for a frame that arrives every quarter second. It gets its own
    /// named profile so that the day it is tuned for speech versus interface, the screenshot side
    /// is not tuned with it by accident — which is what sharing <see cref="Interface"/> would set
    /// up, silently, the first time either number moved.</para>
    /// </remarks>
    public static GroupingProfile Realtime { get; } = new(
        TightlySetMinTextSizeRatio: OrdinaryMinTextSizeRatio,
        WaiveLengthTestWhenSetSolid: false,
        SolidLineAdvanceWhenWrapped: UnrelaxedSolidLineAdvance,
        RefuseSpeakerLineStarts: true);

    /// <summary>
    /// The vertical pipeline's own profile. Like <see cref="Realtime"/>, it is not
    /// <see cref="Interface"/> — it merely holds the same figures today.
    /// </summary>
    /// <remarks>
    /// <para>The capture mode does not reach vertical text at all, and the reason is geometric. A
    /// vertical capture is turned 270° before it is read, so every column of the original arrives at
    /// the detector as a row — which means the first pass's "does this line continue on the next
    /// one" question is being asked of column against column. That is the same geometry the column
    /// merge was denied a profile over, and the same answer follows: none of these thresholds were
    /// measured on it.</para>
    ///
    /// <para>Measured, and not merely argued. Pointing the relaxed profile at
    /// <c>vertical-image-ja</c> joined speech balloons rather than the lines inside them — two
    /// characters' dialogue in one group on <c>image.jpg</c>, a question merged with its answer on
    /// <c>manga-comigram</c> — which is what waiving a length test does when the "lines" it is
    /// judging are whole balloons.</para>
    ///
    /// <para>Named rather than borrowed for the reason <see cref="Realtime"/> is: tightening
    /// <see cref="Interface"/> is a step of its own, and vertical text must not move with it by
    /// accident. Its thresholds have never been measured on vertical material, so the day they
    /// change ought to be a day somebody decided to change them.</para>
    /// </remarks>
    public static GroupingProfile Vertical { get; } = new(
        TightlySetMinTextSizeRatio: OrdinaryMinTextSizeRatio,
        WaiveLengthTestWhenSetSolid: false,
        SolidLineAdvanceWhenWrapped: UnrelaxedSolidLineAdvance,
        RefuseSpeakerLineStarts: false);

    /// <summary>The thresholds for a capture the user has declared the kind of.</summary>
    /// <remarks>
    /// The only place a mode becomes numbers. Anything else reading the mode to pick a threshold is
    /// a second copy of this table waiting to disagree with it.
    /// </remarks>
    public static GroupingProfile For(CaptureLayoutMode mode) => mode switch
    {
        CaptureLayoutMode.Interface => Interface,

        // General, and any mode a later release adds that this build does not know. The default
        // catches the unknown one deliberately: a settings file naming a mode this build cannot
        // read has already fallen back to General by the time it reaches here, and answering the
        // same way twice is cheaper than a second table that can disagree with the first.
        _ => General,
    };

    /// <summary>
    /// The size ratio every pair is held to, set solid or not. Taken from the grouper rather than
    /// repeated, so "the ordinary figure" cannot drift away from the figure itself.
    /// </summary>
    private const double OrdinaryMinTextSizeRatio = OcrTextBlockGrouper.MinTextSizeRatio;

    /// <summary>
    /// The set-solid leading limit a mode that relaxes nothing hands to a wrapped line: the same
    /// one every other pair is held to. Taken from the grouper rather than repeated, for the same
    /// reason as above.
    /// </summary>
    private const double UnrelaxedSolidLineAdvance = OcrTextBlockGrouper.SolidLineAdvance;
}
