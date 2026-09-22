using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>Where a realtime session reads its pixels from, as the user chose on the page.</summary>
/// <remarks>
/// Asked rather than inferred. <c>SourceWindowResolver</c> used to work this out from where the
/// blocks landed, which is invisible to the user and gives them nothing to correct when it lands
/// somewhere they did not mean — and the two answers are not interchangeable: one reads a window and
/// keeps working when something covers it, the other reads the screen and needs this application's
/// own overlays excluded from capture to be safe at all.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RealtimeCaptureMode
{
    /// <summary>The whole screen, as composited. Needs the overlays excluded — see #94.</summary>
    Screen,

    /// <summary>One window the user named, read directly. Isolated by construction.</summary>
    Window,
}

/// <summary>How the border around a realtime subtitle band picks its colour — 顯示外觀 → 邊框.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RealtimeBorderColorMode
{
    /// <summary>Each translated group gets its own colour, chosen from its source text.</summary>
    Random,

    /// <summary>Every band uses <see cref="RealtimeSettings.BorderColor"/>.</summary>
    Fixed,
}

/// <summary>
/// Everything 即時翻譯 keeps between sittings, under one key.
/// </summary>
/// <remarks>
/// The group that introduced grouped settings, and the shape every later one should copy. The
/// keys left in <see cref="AppSettings"/> are flat because they shipped that way: the file is read
/// by matching a JSON property to a property name, so moving an existing key under a group renames
/// it, and the user who updates silently gets the default back.
///
/// Several of these moved anyway, on the owner's call and knowing the cost: everything 即時翻譯's own
/// page shows is set again in one visit to that page, so the reset is a minute of someone's time
/// against a settings file that stays legible for the life of the product. What did not move is
/// everything set from somewhere else — the shortcuts, which 設定 owns as one set — because there the
/// same reset would be a user hunting through a page they did not touch. That is why the shortcuts
/// stay flat despite having 即時翻譯 in their names: 設定 owns every shortcut on one page and treats
/// them as one set.
/// </remarks>
public class RealtimeSettings
{
    /// <summary>
    /// The language 即時翻譯 reads from.
    /// </summary>
    /// <remarks>
    /// English rather than <see cref="LanguageData.DefaultOcrSourceLanguage"/>, which is 自動: the
    /// realtime picker does not offer that mode, because recognition there gets one look at a frame
    /// and no retry.
    ///
    /// This was deliberately blank once, on the reasoning that a guessed source language is worse
    /// than a question — a blank field asks, a default answers badly. It was changed because the
    /// question had to be asked from somewhere, and the shortcut that opens block framing has no
    /// page to ask from; the alternative was a notification refusing to start, which is friction on
    /// every first use to protect against a setting one trip to the page fixes.
    ///
    /// The cost is real and worth writing down: someone whose content is not English, who never
    /// opens the realtime page and starts from the shortcut, gets English recognition over it and
    /// nothing says why. See <see cref="LanguageData.GetValidRealtimeSourceCode"/>, which is where a
    /// blank from before this default is resolved.
    /// </remarks>
    public string SourceLanguage { get; set; } = LanguageData.DefaultRealtimeSourceLanguage;

    /// <summary>
    /// The language 即時翻譯 writes. Kept apart from the screenshot side's own pair, deliberately:
    /// watching a game and translating a document are different jobs, and the pair that suits one is
    /// not the pair that suits the other.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageData.DefaultTargetLanguage;

    /// <inheritdoc cref="TargetLanguage"/>
    public TranslationProvider Provider { get; set; } = TranslationProvider.Microsoft;

    /// <summary>
    /// Subtitle colours, "#RRGGBB". Kept between sittings, unlike the framing: which screen and how
    /// many blocks belong to one sitting, but a reader who needs yellow on dark blue needs it every
    /// time.
    /// </summary>
    public string TextColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultText;

    /// <inheritdoc cref="TextColor"/>
    public string ScrimColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultScrim;

    /// <summary>
    /// How opaque the band behind the subtitle is drawn, 0–100 — see
    /// <see cref="Services.Realtime.RealtimeSubtitleColors.MinScrimOpacity"/>.
    /// </summary>
    /// <remarks>
    /// Its own key rather than an alpha channel on <see cref="ScrimColor"/>, which could have carried
    /// one as <c>#AARRGGBB</c>. Two reasons, and the second is the one that decided it: the colour is
    /// picked in a system dialog that has no alpha to give back, so the two values do not arrive
    /// together; and a reader who wants the band lighter over one game and heavier over the next is
    /// changing this and not their colour, which a combined key would make them redo.
    /// </remarks>
    public int ScrimOpacity { get; set; } =
        Services.Realtime.RealtimeSubtitleColors.DefaultScrimOpacity;

    /// <summary>
    /// Whether a solid border is drawn around the band. Off by default; its thickness is not a
    /// setting.
    /// </summary>
    public bool BorderEnabled { get; set; } = false;

    /// <summary>Random until the user asks for one colour — see <see cref="RealtimeBorderColorMode"/>.</summary>
    public RealtimeBorderColorMode BorderColorMode { get; set; } = RealtimeBorderColorMode.Random;

    /// <summary>The colour used in <see cref="RealtimeBorderColorMode.Fixed"/>, "#RRGGBB".</summary>
    public string BorderColor { get; set; } = Services.Realtime.RealtimeSubtitleColors.DefaultBorder;

    /// <summary>Which of the two capture sources the user last chose. Step 1 on the page.</summary>
    public RealtimeCaptureMode CaptureMode { get; set; } = RealtimeCaptureMode.Screen;

    /// <summary>
    /// The monitor last used in <see cref="RealtimeCaptureMode.Screen"/>, by device name.
    /// </summary>
    /// <remarks>
    /// A device name rather than an index, because indices move: unplugging one monitor renumbers
    /// the rest, and a stored 2 would then mean a different screen rather than a missing one. Empty
    /// until the user picks one, which is not the same as a stored name that no longer resolves —
    /// the first falls back to the monitor the window is on, the second to the primary.
    /// </remarks>
    public string CaptureScreenDeviceName { get; set; } = "";

    /// <summary>
    /// The application whose window was last captured, by process name.
    /// </summary>
    /// <remarks>
    /// A window handle cannot be stored — it dies with the window — so what is kept is the pair a
    /// human would use to recognise it again: which application, and what the window called itself.
    /// See <c>CaptureWindowList.FindStored</c> for how loosely that is matched, and why.
    /// </remarks>
    public string CaptureWindowProcess { get; set; } = "";

    /// <inheritdoc cref="CaptureWindowProcess"/>
    public string CaptureWindowTitle { get; set; } = "";

    /// <summary>Fewest and most blocks a session may frame — the page's stepper obeys this pair.</summary>
    public const int MinBlockCount = 1;

    /// <inheritdoc cref="MinBlockCount"/>
    public const int MaxBlockCount = 3;

    /// <summary>
    /// How many blocks a session is set up to frame, 1–3. Step 3 on the page.
    /// </summary>
    /// <remarks>
    /// Kept, unlike the framing itself, because the value is a preference about how the user works
    /// rather than a fact about the picture they were watching.
    ///
    /// One of the values that moved out of a shipped flat key: everyone updating gets 1 back once,
    /// and 1 is both the default and one press away from anything else.
    /// </remarks>
    public int BlockCount { get; set; } = MinBlockCount;

    /// <summary>
    /// What a newly drawn block is set to read, and which way its text runs: whatever the user last
    /// chose on a block, restored for the next one and for the next sitting.
    /// </summary>
    /// <remarks>
    /// <para>The pair the edit layer's two trays set. They are remembered for the same reason
    /// <see cref="GuidanceExpanded"/> is, and stored the same way — the last tray pressed wins,
    /// written on the press rather than at the end of a session — but they are worth more, because
    /// the framing itself is not kept: every sitting begins by drawing the blocks again, and without
    /// this every one of them begins on the same two answers however many times the user has
    /// corrected them.</para>
    ///
    /// <para>The defaults stay what the draw site used to hard-code, so a settings file written
    /// before these existed reads as the behaviour it had. 字幕 because nearly every block is one,
    /// and the other mode's fraction is the first fallback either way — a panel left on 字幕 costs
    /// one extra inference rather than a block that reads nothing. 橫排 because all but a handful of
    /// blocks are, and a vertical block left on it reads as a stack of one-character lines, which is
    /// visibly wrong rather than quietly wrong.</para>
    ///
    /// <para>Deliberately not on the settings page, like <see cref="GuidanceExpanded"/>: this is a
    /// state the two trays set by being used, not a preference anyone would go looking for.</para>
    /// </remarks>
    public Services.Realtime.RealtimeBlockMode BlockMode { get; set; } =
        Services.Realtime.RealtimeBlockMode.Subtitle;

    /// <inheritdoc cref="BlockMode"/>
    public Services.Realtime.RealtimeTextOrientation TextOrientation { get; set; } =
        Services.Realtime.RealtimeTextOrientation.Horizontal;

    /// <summary>
    /// Whether the per-block framing guidance on the edit layer is unfolded. Expanded on a first run,
    /// because the guidance is what stops a badly framed block.
    /// </summary>
    /// <remarks>
    /// One value for the whole feature rather than one per block: a user who has read the guidance has
    /// read it, and folding it away on every block of every sitting to say so is the same instruction
    /// being dismissed over and over. Whichever block's chevron is pressed last writes here, and the
    /// next edit layer opens every block that way. Deliberately not shown on the settings page — it is
    /// a state the control sets by being used, not a preference anyone would go looking for.
    ///
    /// The one value in this group that is not a choice the user came to make. It moves with them
    /// anyway: it is read and written by 即時翻譯 and nothing else, and a settings file where the rule
    /// is "which feature owns it" stays sortable, while one where the rule is "which feature owns it,
    /// except the states" needs the exception explained every time.
    /// </remarks>
    public bool GuidanceExpanded { get; set; } = true;

    /// <summary>
    /// Whether the band behind a subtitle is replaced by a repair of the picture underneath: the
    /// source line is erased and filled in from the pixels around it, and the translation is drawn
    /// back where it was.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately not because it is new. The repair interpolates from the edges
    /// of the line it erases, so it is close to invisible over a subtitle's own dark strip or a
    /// dialogue panel and visibly a smeared patch over texture, a face or a grid — a higher ceiling
    /// than the band and a lower floor. Which of the two is better is a judgement about the picture
    /// the user is watching, and only they can see it, so the honest default is the one whose result
    /// is predictable. It also costs a screen grab several times a second, which the band does not:
    /// see <see cref="SampleSourceTextColor"/> for the other half of the same trade.
    /// </remarks>
    public bool NaturalBackgroundEnabled { get; set; } = false;

    /// <summary>
    /// Whether the translation is drawn in a colour sampled from the source line rather than in
    /// <see cref="AppSettings.RealtimeTextColor"/>.
    /// </summary>
    /// <remarks>
    /// Its own key rather than travelling with <see cref="NaturalBackgroundEnabled"/>, because the
    /// two fail in different places and a reader may well want one without the other: "repair the
    /// background, but keep my high-contrast yellow" is a reasonable arrangement, and sampling is
    /// exactly what stops being reliable on the busy pictures where that yellow earns its keep.
    ///
    /// Sampling averages the pixels that stand out from the local background, so it has no outline,
    /// shadow or gradient to give back. Source text that depends on one of those to stay readable
    /// comes back as a colour that does not, which is what the readability check falls back to this
    /// setting for.
    /// </remarks>
    public bool SampleSourceTextColor { get; set; } = false;
}
