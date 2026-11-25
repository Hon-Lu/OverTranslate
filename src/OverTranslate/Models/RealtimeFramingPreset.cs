using System.Drawing;
using System.Text.Json.Serialization;
using OverTranslate.Services.Realtime;

namespace OverTranslate.Models;

/// <summary>
/// One 即時翻譯 framing kept for next time: the blocks the user drew, where they were drawn, and
/// which screen or window they were drawn over.
/// </summary>
/// <remarks>
/// <para>The framing is otherwise a fact about one sitting only — see
/// <see cref="RealtimeBlockPlacement"/>, which the controller keeps in memory and drops when the
/// window closes. That is what makes drawing the same two or three blocks over the same game every
/// time the application starts, which is the whole complaint this answers. A preset is the user
/// saying "keep <em>this</em> arrangement", so unlike everything else about a sitting it is written
/// to the settings file.</para>
///
/// <para>A preset belongs to a <b>target</b>: the screen it was drawn on (device name, position and
/// size), or the application window (process and size), never a bare set of rectangles. A screen
/// preset's blocks are physical pixels on that monitor, so offering them back for a different screen,
/// or the same one at a different resolution, would be blocks the user never drew in places nothing
/// is. A window preset's blocks are kept relative to the window's top-left, so they follow a window
/// that has moved — but only a window of the same size. One target may hold several presets — the
/// subtitle strip and the dialogue box of one game are two framings of the same content — and the one
/// marked <see cref="Selected"/> is the one a session opens on.</para>
///
/// <para>Stored apart from the custom modes (<see cref="RealtimeProfile"/>), which say <em>how</em> a
/// block is read; a preset says <em>where</em> the blocks are and which mode each one is on. A mode
/// changed later reaches the preset only through the blocks it was on when the preset was saved —
/// the id is stored, not the values, so a mode tuned on the page is still tuned for the preset.</para>
/// </remarks>
public sealed class RealtimeFramingPreset
{
    /// <summary>
    /// What a name typed by the user is held to, in characters — the same limit the custom modes'
    /// name box uses, for the same reason: a name is read in one row.
    /// </summary>
    public const int MaxNameLength = 60;

    /// <summary>
    /// Stable for the life of the preset, so renaming and forgetting can name one for certain. A
    /// Guid ("N") given as the preset is made.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The user's own name, kept as typed; empty means the preset is listed under the target's own
    /// name — 螢幕 1 · 2560×1440, or the window's title — computed as the list is drawn.
    /// </summary>
    /// <remarks>
    /// Empty rather than the computed string written at save time, so that the default name follows
    /// the interface language and a monitor's own numbering instead of freezing in the language the
    /// preset was saved in.
    /// </remarks>
    public string Name { get; set; } = "";

    /// <summary>Which of the two capture sources this framing was drawn for.</summary>
    public RealtimeCaptureMode CaptureMode { get; set; } = RealtimeCaptureMode.Screen;

    /// <summary>The monitor's device name, as Windows spells it ("\\.\DISPLAY1").</summary>
    public string ScreenDeviceName { get; set; } = "";

    /// <summary>
    /// The monitor's rectangle in physical pixels when the framing was saved.
    /// </summary>
    /// <remarks>
    /// A screen preset's blocks live in this rectangle, so its position as well as its size is part
    /// of what the preset belongs to: the same resolution somewhere else in the virtual desktop is
    /// another target. On a window preset it is only what the screen was at the time — a window
    /// preset belongs to its application and size, not to a monitor, and follows the window across
    /// screens.
    /// </remarks>
    public RealtimeFramingRect Screen { get; set; } = new();

    /// <summary>The application of the captured window, by process name, as <see cref="RealtimeSettings.CaptureWindowProcess"/> names it.</summary>
    public string WindowProcess { get; set; } = "";

    /// <summary>
    /// What the captured window called itself when the framing was saved.
    /// </summary>
    /// <remarks>
    /// Read as a hint rather than as the identity: a browser retitles itself with every video, so a
    /// preset whose title no longer matches is still that window's — see
    /// <c>RealtimeFramingStore.Match</c>, which prefers the presets saved under the current title and
    /// falls back to the selected one when none matches. It is kept so that two windows of one
    /// application can be told apart, and updated to the current title whenever the same arrangement
    /// is saved again.
    /// </remarks>
    public string WindowTitle { get; set; } = "";

    /// <summary>
    /// The size the captured window had when the framing was saved, in physical pixels; empty on a
    /// screen preset.
    /// </summary>
    /// <remarks>
    /// A window preset's blocks are relative to the window's top-left (see <see cref="Blocks"/>), so
    /// the same arrangement goes back wherever the window has moved to. What it cannot survive is a
    /// different size — the blocks would cover a different part of the window — so a window preset
    /// matches only a window of exactly this size, and a resize is reported rather than applied.
    /// </remarks>
    public RealtimeFramingRect WindowSize { get; set; } = new();

    /// <summary>
    /// Which of the target's presets this is, from 1: 螢幕 1 · 2560×1440, then
    /// 螢幕 1 · 2560×1440（2） and on. Kept in the file so the numbers in the list do not move around
    /// as presets are added and forgotten; only ever used to name a preset the user has not named.
    /// </summary>
    /// <remarks>
    /// Assigned as the preset is saved and repaired by <see cref="Clean"/> for a file that has none
    /// (written before several presets per target existed) or two of the same. Numbers are never
    /// reissued after a delete: the entries that are left keep the names the user has been reading.
    /// </remarks>
    public int Ordinal { get; set; }

    /// <summary>
    /// Whether this is the preset its target opens on — see <see cref="RealtimeFramingStore.Match"/>.
    /// At most one preset of a target is selected; saving a framing and picking one in the list both
    /// set it.
    /// </summary>
    /// <remarks>
    /// Stored on the preset rather than as an id in the settings beside the list, so that deleting
    /// the selected preset cannot leave a dangling id behind, and so that a selected preset carries
    /// its state wherever the list is copied.
    /// </remarks>
    public bool Selected { get; set; }

    /// <summary>Every block, in the order they were drawn — the order the edit layer gives them back.</summary>
    /// <remarks>
    /// A screen preset's blocks are in the screen's physical pixels. A window preset's are relative
    /// to the captured window's top-left — the DWM extended frame bounds window capture reads — with
    /// <see cref="WindowSize"/> saying what size they were drawn for. Relative rather than absolute
    /// so that a window that has moved still gets its blocks over the right place.
    /// </remarks>
    public List<RealtimeFramingBlock> Blocks { get; set; } = [];

    /// <summary>
    /// When this framing was last saved, in UTC. Only ever orders the list and settles which of two
    /// presets for one target a hand-edited file means: the newer one.
    /// </summary>
    public DateTime SavedAt { get; set; }

    /// <summary>Whether this preset was drawn over a window rather than over a whole screen.</summary>
    /// <remarks>Derived from <see cref="CaptureMode"/>; not written to the file.</remarks>
    [JsonIgnore]
    public bool IsForWindow => CaptureMode == RealtimeCaptureMode.Window;

    /// <summary>
    /// What makes two presets belong to the same target: the mode and the screen, or the mode and the
    /// application with the size its window had, in one string. Used to number a target's presets, to
    /// keep one of them selected, and to tell which ones a session may open on.
    /// </summary>
    /// <remarks>
    /// <para>The window's title is deliberately not part of it. It identifies one window among an
    /// application's others, but it also changes while the window stays the same — a browser retitles
    /// itself with every video — and a preset saved before the change is still that window's. The
    /// title is compared where it can narrow the field, in <c>RealtimeFramingStore.Match</c>.</para>
    ///
    /// <para>The window's position and the screen it is on are not part of it either: a window
    /// preset's blocks are relative, so the same application at the same size is the same target
    /// wherever the window has moved to.</para>
    /// </remarks>
    [JsonIgnore]
    public string TargetKey
    {
        get
        {
            var screen = Screen ?? new RealtimeFramingRect();
            var window = WindowSize ?? new RealtimeFramingRect();
            return IsForWindow
                ? string.Join('\u001f', "window", WindowProcess ?? "", window.Width, window.Height)
                : string.Join('\u001f', "screen", ScreenDeviceName ?? "",
                    screen.X, screen.Y, screen.Width, screen.Height);
        }
    }

    /// <summary>A copy to edit without touching this one.</summary>
    public RealtimeFramingPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Ordinal = Ordinal,
        Selected = Selected,
        CaptureMode = CaptureMode,
        ScreenDeviceName = ScreenDeviceName,
        Screen = (Screen ?? new RealtimeFramingRect()).Clone(),
        WindowProcess = WindowProcess,
        WindowTitle = WindowTitle,
        WindowSize = (WindowSize ?? new RealtimeFramingRect()).Clone(),
        Blocks = [.. Blocks.Select(block => block.Clone())],
        SavedAt = SavedAt,
    };

    /// <summary>
    /// The presets as the settings file can hold them: newest first, each with an id of its own, a
    /// target this build can place it on, at least one block and no more than <paramref name="maxBlocks"/>
    /// of them, a number of its own among its target's presets, and at most one of a target's selected.
    /// </summary>
    /// <remarks>
    /// <para>Values are not otherwise touched: a mode id this build does not know, an orientation it
    /// cannot place, a block clear off the target — each is dealt with where it is used, so that a
    /// later version can make sense of what this one stored. What is dropped here could not be made
    /// sense of by this build at all. A window preset with no window size is one of those: its
    /// blocks are relative to a window this build cannot ask about, so there is nowhere to put them
    /// and it is dropped.</para>
    ///
    /// <para>The numbers are repaired rather than trusted: a file written before several presets per
    /// target existed has none, and a hand-edited one may repeat a number. A missing or repeated
    /// number takes the next one free for its target, so the entries that are already numbered keep
    /// the names the user has been reading.</para>
    ///
    /// <para>Two presets of one target both marked selected — only reachable by editing the file — is
    /// settled by keeping the newer and clearing the other, the same rule the list itself is in.</para>
    /// </remarks>
    public static List<RealtimeFramingPreset> Clean(
        IEnumerable<RealtimeFramingPreset?>? presets, int maxBlocks, int maxPresets)
    {
        var clean = new List<RealtimeFramingPreset>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var selectedTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var preset in (presets ?? []).OfType<RealtimeFramingPreset>()
                     .OrderByDescending(preset => preset.SavedAt))
        {
            var id = (preset.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id)) continue;

            preset.Id = id;
            preset.Name = CleanName(preset.Name);
            preset.ScreenDeviceName ??= "";
            preset.WindowProcess = (preset.WindowProcess ?? "").Trim();
            preset.WindowTitle = (preset.WindowTitle ?? "").Trim();
            // A hand-edited "Screen": null reads as a preset with no screen, which is dropped below;
            // without this the check itself would throw on the way to reading every other setting.
            preset.Screen ??= new RealtimeFramingRect();
            preset.WindowSize ??= new RealtimeFramingRect();
            if (preset.CaptureMode != RealtimeCaptureMode.Window)
                preset.CaptureMode = RealtimeCaptureMode.Screen;

            preset.Blocks = [.. (preset.Blocks ?? []).OfType<RealtimeFramingBlock>()
                .Where(block => block.Width > 0 && block.Height > 0)
                .Take(maxBlocks)];

            if (preset.Blocks.Count == 0) continue;
            if (preset.IsForWindow)
            {
                if (preset.WindowSize.Width <= 0 || preset.WindowSize.Height <= 0) continue;
            }
            else if (preset.Screen.Width <= 0 || preset.Screen.Height <= 0)
            {
                continue;
            }

            var target = preset.TargetKey;
            if (!ordinals.TryGetValue(target, out var used))
                ordinals[target] = used = [];
            preset.Ordinal = NextOrdinal(used, preset.Ordinal);

            if (preset.Selected && !selectedTargets.Add(target)) preset.Selected = false;

            clean.Add(preset);
            if (clean.Count == maxPresets) break;
        }

        return clean;
    }

    /// <summary>
    /// <paramref name="wanted"/> where it is a number this target has not used yet, otherwise the
    /// smallest number the target does not have.
    /// </summary>
    private static int NextOrdinal(HashSet<int> used, int wanted)
    {
        if (wanted > 0 && used.Add(wanted)) return wanted;

        int next = 1;
        while (!used.Add(next)) next++;
        return next;
    }

    /// <summary>
    /// What typing <paramref name="typed"/> into the name box stores: one line — a line break or tab
    /// pasted in is a space — trimmed, and no longer than <see cref="MaxNameLength"/>.
    /// </summary>
    public static string CleanName(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var chars = typed.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }
}

/// <summary>
/// One block of a <see cref="RealtimeFramingPreset"/>: where it is, which mode it is on and what its
/// 對照顯示 copy looks like.
/// </summary>
/// <remarks>
/// Written out rather than reusing <see cref="RealtimeBlockPlacement"/> so that the settings file
/// holds four plain integers — <c>"X"</c>, <c>"Y"</c>, <c>"Width"</c>, <c>"Height"</c> — instead of
/// the library type's <c>{"X":…,"Location":{…},"Size":{…}}</c>, and so that a change to that type
/// (another block field, a different coordinate type) cannot silently change what an old file means.
/// </remarks>
public sealed class RealtimeFramingBlock
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The mode this block reads with, by id — a preset's or a custom mode's.</summary>
    /// <remarks>
    /// The id rather than its values, as on <see cref="RealtimeBlockPlacement.ProfileId"/>: a mode
    /// changed on the page is what the preset reads with next time. An id this build no longer knows
    /// is resolved to 標準 where the block is used.
    /// </remarks>
    public string Profile { get; set; } = RealtimePresets.StandardId;

    /// <summary>Which way this block's text is written — see <see cref="RealtimeTextOrientation"/>.</summary>
    public RealtimeTextOrientation Orientation { get; set; } = RealtimeTextOrientation.Horizontal;

    /// <summary>Where 對照顯示 draws this block's copy from, as an offset from <see cref="X"/> and
    /// <see cref="Y"/>, or null for the automatic placement — see
    /// <see cref="RealtimeBlockPlacement.CompareOffset"/>.</summary>
    public int? CompareOffsetX { get; set; }

    /// <inheritdoc cref="CompareOffsetX"/>
    public int? CompareOffsetY { get; set; }

    /// <summary>How large the copy is drawn, as a fraction of the block — see
    /// <see cref="RealtimeBlockPlacement.CompareScale"/>.</summary>
    public double CompareScale { get; set; } = 1.0;

    /// <summary>Whether the copy was turned off for this block alone — see
    /// <see cref="RealtimeBlockPlacement.CompareHidden"/>.</summary>
    public bool CompareHidden { get; set; }

    /// <summary>The stored block <paramref name="placement"/> is written as.</summary>
    public static RealtimeFramingBlock From(RealtimeBlockPlacement placement) => new()
    {
        X = placement.Bounds.X,
        Y = placement.Bounds.Y,
        Width = placement.Bounds.Width,
        Height = placement.Bounds.Height,
        Profile = placement.ProfileId,
        Orientation = placement.Orientation,
        CompareOffsetX = placement.CompareOffset?.X,
        CompareOffsetY = placement.CompareOffset?.Y,
        CompareScale = placement.CompareScale,
        CompareHidden = placement.CompareHidden,
    };

    /// <summary>
    /// This block as the edit layer and the session take it, with the values this build cannot use
    /// brought back into range: a direction it does not know is 橫排, a copy scale outside the range
    /// the edit layer can produce is held to it, and half an offset is no offset.
    /// </summary>
    public RealtimeBlockPlacement ToPlacement() => new(
        new Rectangle(X, Y, Width, Height),
        Profile ?? RealtimePresets.StandardId,
        Enum.IsDefined(Orientation) ? Orientation : RealtimeTextOrientation.Horizontal,
        CompareOffsetX is { } x && CompareOffsetY is { } y ? new Point(x, y) : null,
        double.IsFinite(CompareScale)
            ? Math.Clamp(CompareScale, RealtimeComparePlacement.MinScale, 1.0)
            : 1.0,
        CompareHidden);

    /// <summary>A copy to edit without touching this one.</summary>
    public RealtimeFramingBlock Clone() => (RealtimeFramingBlock)MemberwiseClone();
}

/// <summary>
/// A rectangle of the screen in physical pixels, written into the settings file as four integers.
/// </summary>
/// <remarks>
/// <see cref="System.Drawing.Rectangle"/> serializes with its derived properties as well — Location,
/// Size — which reads as the same two numbers twice in a file a user may open. It also keeps the
/// preset's shape tied to a library type; four plain integers do not.
/// </remarks>
public sealed class RealtimeFramingRect : IEquatable<RealtimeFramingRect>
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The rectangle itself, in the coordinate space the screen and the blocks live in.</summary>
    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    /// <summary>The rectangle <paramref name="bounds"/> writes into the file.</summary>
    public static RealtimeFramingRect From(Rectangle bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = bounds.Width,
        Height = bounds.Height,
    };

    /// <summary>A size, with no position — what a window's <see cref="RealtimeFramingPreset.WindowSize"/> holds.</summary>
    public static RealtimeFramingRect From(Size size) => new()
    {
        Width = size.Width,
        Height = size.Height,
    };

    /// <summary>Whether this rectangle is on the screen at all — the one check cleaning makes.</summary>
    /// <remarks>Derived from the four numbers; not written to the file.</remarks>
    [JsonIgnore]
    public bool IsUsable => Width > 0 && Height > 0;

    /// <summary>A copy to edit without touching this one.</summary>
    public RealtimeFramingRect Clone() => (RealtimeFramingRect)MemberwiseClone();

    /// <summary>Two rectangles are the same rectangle.</summary>
    public bool Equals(RealtimeFramingRect? other) =>
        other is not null && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RealtimeFramingRect other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
}
