using System.Drawing;
using OverTranslate.Models;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The kept 即時翻譯 framings: which one belongs to the screen or window a session is about to read,
/// saving the one on screen now, and turning a stored one back into blocks.
/// </summary>
/// <remarks>
/// <para>Sits between <see cref="RealtimeFramingPreset"/> — the shape the settings file holds — and
/// the session, which thinks in <see cref="RealtimeBlockPlacement"/>. The controller never reads the
/// file's shape and the card never sees a session's: the two meet here.</para>
///
/// <para>A <b>target</b> is what a framing belongs to, not what it contains. A screen target is the
/// monitor's device and resolution: its blocks are relative to the monitor's top-left, so the same
/// monitor moved elsewhere in the virtual desktop — another monitor plugged in to its left, the
/// arrangement changed in Windows' settings — is still the same target, while a different monitor,
/// or the same one at another resolution, is another. A window target is the application and the
/// size its window has: its blocks are relative to the window's top-left, so the same arrangement
/// follows the window to another place on the screen, or to another screen, and only a window of the
/// same size is the same target.</para>
///
/// <para>One target may hold several presets, and which of them a session opens on is the one marked
/// <see cref="RealtimeFramingPreset.Selected"/> — saving a framing and picking one in the page's list
/// both set it — or the newest it has when nothing is marked. For a window the title narrows the
/// candidates first (a preset saved under the window's current title is that window's); the title is
/// a preference, not part of the target: a browser retitles itself with every video.</para>
/// </remarks>
internal static class RealtimeFramingStore
{
    /// <summary>
    /// What a framing belongs to: the screen it was drawn on, plus the window when the source is one.
    /// </summary>
    /// <param name="Screen">The screen's rectangle in physical pixels, the session's own.</param>
    /// <param name="DeviceName">The monitor's device name, which survives a rectangle that matches.</param>
    /// <param name="WindowProcess">The application's process name, empty in 螢幕擷取.</param>
    /// <param name="WindowTitle">The window's title as it is now, empty in 螢幕擷取.</param>
    /// <param name="WindowBounds">
    /// The window capture reads the window at, in physical pixels — the DWM extended frame bounds,
    /// the same rectangle <see cref="WindowBoundsOf"/> returns and the frame a region is cut from.
    /// Empty in 螢幕擷取, and when the window has gone.
    /// </param>
    /// <param name="WindowPath">
    /// The full path of the window's executable, empty in 螢幕擷取 and when it cannot be read — see
    /// <see cref="RealtimeFramingPreset.WindowPath"/>.
    /// </param>
    public readonly record struct Target(
        RealtimeCaptureMode Mode,
        Rectangle Screen,
        string DeviceName,
        string WindowProcess = "",
        string WindowTitle = "",
        Rectangle WindowBounds = default,
        string WindowPath = "")
    {
        /// <summary>Whether this target is a window rather than a whole screen.</summary>
        public bool IsWindow => Mode == RealtimeCaptureMode.Window;

        /// <summary>The size the window had when the target was made; empty in 螢幕擷取.</summary>
        public Size WindowSize => WindowBounds.Size;

        /// <summary>
        /// Whether the window is there to be read at all: a handle that has gone, or a window that
        /// cannot be measured, is not a target a framing can be placed on.
        /// </summary>
        public bool HasWindow => !IsWindow || (WindowBounds.Width > 0 && WindowBounds.Height > 0);
    }

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    /// <summary>
    /// The rectangle window capture reads <paramref name="hwnd"/> at: its DWM extended frame bounds,
    /// or its window rectangle where those are unavailable.
    /// </summary>
    /// <remarks>
    /// The same pair <c>WgcWindowCaptureBackend.TryGetWindowBounds</c> takes its frame origin from —
    /// a frame of the extended rectangle — so a block placed relative to this rectangle lands on the
    /// pixels it was drawn over. Kept here rather than asked of that class because this is needed
    /// while only framing, before any capture exists.
    /// </remarks>
    public static Rectangle WindowBoundsOf(IntPtr hwnd) =>
        Capture.CaptureWindowList.CaptureBounds(hwnd);

    /// <summary>
    /// Whether two targets are the same target for the purpose of offering a framing back: the same
    /// screen, or the same application with a window of the same size.
    /// </summary>
    /// <remarks>
    /// The position of a window and the screen it is on are not compared: a window preset's blocks
    /// are relative, so the same application at the same size is the same target wherever the window
    /// has been moved to. The title is not compared either, because a window retitles itself between
    /// sittings while staying the same window. The executable is: two games of one process name are
    /// two targets — see <see cref="SameProgram"/>.
    /// </remarks>
    public static bool SameTarget(Target a, Target b) =>
        a.Mode == b.Mode &&
        (a.IsWindow
            ? SameProgram(a.WindowProcess, a.WindowPath, b) && a.WindowSize == b.WindowSize
            : a.DeviceName == b.DeviceName && a.Screen == b.Screen);

    /// <summary>
    /// Whether an application kept as <paramref name="process"/> at <paramref name="path"/> is the
    /// one <paramref name="target"/>'s window belongs to.
    /// </summary>
    /// <remarks>
    /// <para>The process name alone is not enough: every RPG Maker game is Game.exe, and the MV and
    /// MZ ones open at the same 816×624, so one game's framing would be put on another's. Where both
    /// sides know the executable's full path, the paths have to be the same file, compared without
    /// case as Windows does.</para>
    ///
    /// <para>The path is let go of where it cannot answer: a side that could not read it, or a kept
    /// path that is no longer on disk — the game moved to another folder or reinstalled. Those fall
    /// back to the name, so moving a game does not throw away every framing kept for it; the price is
    /// the old rule's, for that game only, until its framings are saved again.</para>
    /// </remarks>
    public static bool SameProgram(string? process, string? path, Target target)
    {
        if ((process ?? "") != (target.WindowProcess ?? "")) return false;

        var kept = (path ?? "").Trim();
        var now = (target.WindowPath ?? "").Trim();
        if (kept.Length == 0 || now.Length == 0) return true;
        if (string.Equals(kept, now, StringComparison.OrdinalIgnoreCase)) return true;

        return !PathExists(kept);
    }

    /// <summary>Whether a kept executable is still on disk.</summary>
    private static bool PathExists(string path)
    {
        try { return System.IO.File.Exists(path); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The blocks remembered from <paramref name="from"/>, moved to where <paramref name="to"/>'s
    /// window is now — for a target <see cref="SameTarget"/> says is the same one.
    /// </summary>
    /// <remarks>
    /// Remembered blocks are absolute screen pixels, but a window target ignores where the window is,
    /// so a window moved between sittings would get its blocks over the place it was. They are moved
    /// by as much as the window was. A screen target, or a window either side could not measure, is
    /// left as it is.
    /// </remarks>
    public static IReadOnlyList<RealtimeBlockPlacement> FollowWindow(
        Target from, Target to, IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        if (!from.IsWindow || !to.IsWindow ||
            from.WindowBounds.IsEmpty || to.WindowBounds.IsEmpty)
            return blocks;

        int dx = to.WindowBounds.X - from.WindowBounds.X;
        int dy = to.WindowBounds.Y - from.WindowBounds.Y;
        if (dx == 0 && dy == 0) return blocks;

        return [.. blocks.Select(block => block with
        {
            Bounds = new Rectangle(
                block.Bounds.X + dx, block.Bounds.Y + dy, block.Bounds.Width, block.Bounds.Height),
        })];
    }

    /// <summary>The preset kept for <paramref name="target"/>, or null when nothing is kept for it.</summary>
    public static RealtimeFramingPreset? Match(Target target) => Match(Realtime.FramingPresets, target);

    /// <summary>
    /// Every preset kept for <paramref name="target"/>, newest first — what the framing bar's
    /// 翻譯區域預設 lists for the screen or window it is framing over.
    /// </summary>
    public static IReadOnlyList<RealtimeFramingPreset> ForTarget(Target target) =>
        ForTarget(Realtime.FramingPresets, target);

    /// <summary><see cref="ForTarget(Target)"/> over a list rather than the settings file.</summary>
    public static IReadOnlyList<RealtimeFramingPreset> ForTarget(
        IReadOnlyList<RealtimeFramingPreset> presets, Target target) =>
        [.. presets.Where(preset => IsFor(preset, target))];

    /// <summary>
    /// Which of the presets kept for <paramref name="target"/> a session opens on: the selected one
    /// where the user picked one, otherwise the newest — and for a window, the ones saved under its
    /// current title first.
    /// </summary>
    public static RealtimeFramingPreset? Match(
        IReadOnlyList<RealtimeFramingPreset> presets, Target target)
    {
        var candidates = presets.Where(preset => IsFor(preset, target)).ToList();
        if (candidates.Count == 0) return null;

        if (target.IsWindow)
        {
            var titled = candidates.Where(preset => preset.WindowTitle == target.WindowTitle).ToList();
            if (titled.Count > 0) return titled.FirstOrDefault(preset => preset.Selected) ?? titled[0];

            // No title matches. The user's own pick is still an answer; without one, a single title
            // across the target means it can only be that window retitled, and several titles leave
            // the windows indistinguishable.
            if (candidates.FirstOrDefault(preset => preset.Selected) is { } selected) return selected;
            return candidates.Select(preset => preset.WindowTitle).Distinct().Count() == 1
                ? candidates[0]
                : null;
        }

        return candidates.FirstOrDefault(preset => preset.Selected) ?? candidates[0];
    }

    /// <summary>Why no preset was applied to a target that has some kept for it.</summary>
    internal enum Miss
    {
        /// <summary>Nothing is kept for this target at all; there is nothing to explain.</summary>
        None,

        /// <summary>Presets are kept for this application, but at another window size.</summary>
        WindowSize,

        /// <summary>Presets are kept for this monitor, but at another resolution.</summary>
        ScreenSize,

        /// <summary>
        /// The preset fits the window, but the window reaches past the session's screen and some of
        /// the blocks would land on the part of it that is on another monitor.
        /// </summary>
        OffScreen,
    }

    /// <summary>
    /// Whether <paramref name="preset"/>, kept for <paramref name="target"/>, can be put on the
    /// screen now — <see cref="Miss.None"/> — or what stands in the way.
    /// </summary>
    /// <remarks>
    /// <para>A preset of another target is not this question's: the caller has already matched it.
    /// Only the size and the screen are checked here.</para>
    ///
    /// <para>A window preset follows its window, and the window may reach past the screen the
    /// session frames on: a session takes the monitor the window covers most of, and the framing
    /// layer and its bar cover that monitor alone. A block that lands even partly on another monitor
    /// cannot be seen whole, moved or removed there, so the preset is not placed at all rather than
    /// cut to the part that shows — the same answer a window of another size gets.</para>
    /// </remarks>
    public static Miss FitOf(RealtimeFramingPreset preset, Target target)
    {
        if (!preset.IsForWindow)
        {
            var screen = preset.Screen ?? new RealtimeFramingRect();
            return screen.Width == target.Screen.Width && screen.Height == target.Screen.Height
                ? Miss.None
                : Miss.ScreenSize;
        }

        var size = preset.WindowSize ?? new RealtimeFramingRect();
        if (!target.HasWindow ||
            size.Width != target.WindowBounds.Width || size.Height != target.WindowBounds.Height)
            return Miss.WindowSize;

        return OnScreen(Offsets(preset, target.WindowBounds), target.Screen) ? Miss.None : Miss.OffScreen;
    }

    /// <summary>
    /// Whether every one of <paramref name="blocks"/> lies wholly on <paramref name="screen"/> — the
    /// one monitor the framing layer covers.
    /// </summary>
    public static bool OnScreen(IEnumerable<RealtimeBlockPlacement> blocks, Rectangle screen) =>
        blocks.All(block => screen.Contains(block.Bounds));

    /// <summary>
    /// Why <paramref name="target"/> opens on no preset, when presets are kept for it in every way
    /// but the size — the reason a session that was started with a preset in reach says on the bar.
    /// </summary>
    /// <param name="nearest">
    /// The preset the reason is about: the selected one for this target, or the newest, so its size
    /// can be named. Null when nothing is kept.
    /// </param>
    public static Miss Explain(Target target, out RealtimeFramingPreset? nearest) =>
        Explain(Realtime.FramingPresets, target, out nearest);

    /// <summary><see cref="Explain(Target, out RealtimeFramingPreset?)"/> over a list.</summary>
    public static Miss Explain(
        IReadOnlyList<RealtimeFramingPreset> presets, Target target, out RealtimeFramingPreset? nearest)
    {
        nearest = null;

        // The preset this target opens on fits its size but not the screen: that is the reason.
        if (Match(presets, target) is { } matched)
        {
            var fit = FitOf(matched, target);
            if (fit != Miss.None) nearest = matched;
            return fit;
        }

        // A preset of the right size is a candidate Match may still have declined — two windows of
        // one title-less application, say; the size is not what stood in the way, so it is not the
        // reason to give.
        if (presets.Any(preset => IsFor(preset, target))) return Miss.None;

        if (target.IsWindow)
        {
            if (!target.HasWindow) return Miss.None;

            var near = presets.Where(preset =>
                preset.IsForWindow && SameProgram(preset.WindowProcess, preset.WindowPath, target)).ToList();
            if (near.Count == 0) return Miss.None;

            nearest = near.FirstOrDefault(preset => preset.Selected) ?? near[0];
            return Miss.WindowSize;
        }

        var sameMonitor = presets.Where(preset =>
            !preset.IsForWindow && preset.ScreenDeviceName == target.DeviceName).ToList();
        if (sameMonitor.Count == 0) return Miss.None;

        nearest = sameMonitor.FirstOrDefault(preset => preset.Selected) ?? sameMonitor[0];
        return Miss.ScreenSize;
    }

    /// <summary>
    /// The blocks <paramref name="preset"/> is to be started with, placed on
    /// <paramref name="target"/>: a copy of each, offset by the screen's or the window's current
    /// top-left, clamped to the screen or window it belongs to and dropped when nothing of it is left.
    /// Never more than the session can frame at once.
    /// </summary>
    /// <remarks>
    /// A window preset whose stored window size is not the size the window has now is not placed at
    /// all: the blocks would cover a different part of the window. Nor is one whose blocks would
    /// reach past the session's screen — see <see cref="FitOf"/>. <see cref="Explain"/> is what says
    /// so; this returns nothing rather than a guess.
    /// </remarks>
    public static IReadOnlyList<RealtimeBlockPlacement> Placements(
        RealtimeFramingPreset preset, Target target)
    {
        var area = preset.IsForWindow ? target.WindowBounds : target.Screen;
        if (area.Width <= 0 || area.Height <= 0) return [];

        if (preset.IsForWindow && FitOf(preset, target) != Miss.None) return [];

        return [.. Offsets(preset, area)
            .Select(placement => placement with { Bounds = ClampInto(placement.Bounds, area) })
            .Where(placement => placement.Bounds.Width > 0 && placement.Bounds.Height > 0)
            .Take(RealtimeSettings.MaxBlockCount)];
    }

    /// <summary>
    /// <paramref name="preset"/>'s blocks as the session takes them, moved onto
    /// <paramref name="area"/> — the screen's or the window's rectangle, which the blocks are kept
    /// relative to — and not yet clamped to anything.
    /// </summary>
    private static IEnumerable<RealtimeBlockPlacement> Offsets(RealtimeFramingPreset preset, Rectangle area) =>
        (preset.Blocks ?? []).OfType<RealtimeFramingBlock>()
            .Select(block => block.ToPlacement())
            .Select(placement => placement with
            {
                Bounds = new Rectangle(
                    placement.Bounds.X + area.X, placement.Bounds.Y + area.Y,
                    placement.Bounds.Width, placement.Bounds.Height),
            });

    /// <summary>
    /// Saves <paramref name="blocks"/> as a framing for <paramref name="target"/>, and selects it —
    /// unless the same arrangement is already kept for that target, which is then selected instead of
    /// saved twice, or the list is full, in which case nothing is saved and nothing is taken away.
    /// </summary>
    /// <returns>The preset as stored, or null when the list is full.</returns>
    public static RealtimeFramingPreset? Save(Target target, IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        var (presets, saved) = SaveInto(Realtime.FramingPresets, target, blocks, DateTime.UtcNow);
        if (saved is null) return null;

        SettingsService.Instance.UpdateRealtimeFramingPresets(presets);
        return saved;
    }

    /// <summary>
    /// <see cref="Save(Target, IReadOnlyList{RealtimeBlockPlacement})"/> as a list in and a list out,
    /// for a test that has no settings file.
    /// </summary>
    /// <remarks>
    /// <para>A framing whose blocks are the same as one already kept for this target is that preset
    /// selected again, not a copy: pressing 儲存框選 twice on an unchanged screen is one decision. A
    /// layout that differs in any block — moved, resized, another mode, another compare offset — is a
    /// new preset, which is the whole point of keeping several for one target.</para>
    ///
    /// <para>At <see cref="RealtimeSettings.MaxFramingPresets"/> a new framing is refused: the list
    /// is returned exactly as it came and <c>Saved</c> is null. Nothing is dropped to make room — a
    /// preset the user named, or the one a target opens on, is not the program's to throw away.</para>
    /// </remarks>
    public static (List<RealtimeFramingPreset> Presets, RealtimeFramingPreset? Saved) SaveInto(
        IReadOnlyList<RealtimeFramingPreset> current, Target target,
        IReadOnlyList<RealtimeBlockPlacement> blocks, DateTime savedAt)
    {
        var presets = current.Select(preset => preset.Clone()).ToList();
        var targetPresets = presets.Where(preset => IsFor(preset, target)).ToList();
        var stored = Stored(blocks, target);

        var saved = targetPresets.FirstOrDefault(preset => SameBlocks(preset, stored));
        if (saved is null)
        {
            if (presets.Count >= RealtimeSettings.MaxFramingPresets) return (presets, null);

            saved = new RealtimeFramingPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "",
                Ordinal = targetPresets.Count == 0 ? 1 : targetPresets.Max(preset => Math.Max(1, preset.Ordinal)) + 1,
                SavedAt = savedAt,
            };
            presets.Insert(0, saved);
        }

        saved.CaptureMode = target.Mode;
        saved.ScreenDeviceName = target.DeviceName ?? "";
        saved.Screen = RealtimeFramingRect.From(target.Screen);
        saved.WindowProcess = target.IsWindow ? target.WindowProcess ?? "" : "";
        saved.WindowPath = target.IsWindow ? target.WindowPath ?? "" : "";
        saved.WindowTitle = target.IsWindow ? target.WindowTitle ?? "" : "";
        saved.WindowSize = target.IsWindow
            ? RealtimeFramingRect.From(target.WindowBounds.Size)
            : new RealtimeFramingRect();
        saved.Blocks = stored;

        // One selected preset per target, and this is it: a saved framing is the one the user wants
        // the next session on this target to open on. A preset kept under a path that is gone is
        // still this target's (see SameProgram), so it is unmarked too.
        foreach (var other in presets.Where(preset =>
                     preset.TargetKey == saved.TargetKey || IsFor(preset, target)))
            other.Selected = other.Id == saved.Id;

        return (presets, saved);
    }

    /// <summary>
    /// Puts <paramref name="blocks"/> into the preset <paramref name="id"/> in place of its own — the
    /// list's 更新 on a preset that was applied and then changed. Its name, number, selection and
    /// place in the list stay as they were.
    /// </summary>
    /// <returns>The preset as stored, or null when no preset goes by that id any more.</returns>
    public static RealtimeFramingPreset? Update(
        string id, Target target, IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        var (presets, updated) = UpdateInto(Realtime.FramingPresets, id, target, blocks);
        if (updated is null) return null;

        SettingsService.Instance.UpdateRealtimeFramingPresets(presets);
        return updated;
    }

    /// <summary><see cref="Update"/> as a list in and a list out, for a test that has no settings file.</summary>
    /// <remarks>
    /// Never refused at <see cref="RealtimeSettings.MaxFramingPresets"/>: an update adds nothing.
    /// The window's size, title and screen are taken from <paramref name="target"/> as a save
    /// takes them, so the preset describes where its blocks were last drawn.
    /// </remarks>
    public static (List<RealtimeFramingPreset> Presets, RealtimeFramingPreset? Updated) UpdateInto(
        IReadOnlyList<RealtimeFramingPreset> current, string id, Target target,
        IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        var presets = current.Select(preset => preset.Clone()).ToList();
        var updated = presets.FirstOrDefault(preset => preset.Id == id);
        if (updated is null || blocks.Count == 0) return (presets, null);

        updated.CaptureMode = target.Mode;
        updated.ScreenDeviceName = target.DeviceName ?? "";
        updated.Screen = RealtimeFramingRect.From(target.Screen);
        updated.WindowProcess = target.IsWindow ? target.WindowProcess ?? "" : "";
        updated.WindowPath = target.IsWindow ? target.WindowPath ?? "" : "";
        updated.WindowTitle = target.IsWindow ? target.WindowTitle ?? "" : "";
        updated.WindowSize = target.IsWindow
            ? RealtimeFramingRect.From(target.WindowBounds.Size)
            : new RealtimeFramingRect();
        updated.Blocks = Stored(blocks, target);
        return (presets, updated);
    }

    /// <summary>What one row of the bar's 翻譯區域預設 list is.</summary>
    internal enum RowState
    {
        /// <summary>Another framing for this target: pressing it puts it on the screen.</summary>
        Ready,

        /// <summary>The framing on the screen now, exactly as kept: ticked, and pressing it does nothing.</summary>
        Current,

        /// <summary>The framing applied last and changed since: 還原 and 更新 instead of a tick.</summary>
        Modified,

        /// <summary>Kept for this monitor or application, but it cannot be placed now: greyed, with the reason.</summary>
        Unusable,
    }

    /// <summary>What the 儲存目前框選 row at the bottom of the list can do now.</summary>
    internal enum SaveState
    {
        /// <summary>The blocks on screen can be kept as a new preset.</summary>
        Ready,

        /// <summary>They are already kept, as <see cref="FramingList.SameAs"/>: nothing to save.</summary>
        Same,

        /// <summary>The list is at <see cref="RealtimeSettings.MaxFramingPresets"/>.</summary>
        Full,

        /// <summary>No block is drawn.</summary>
        NoBlocks,
    }

    /// <summary>One row: the preset, what it is now, and — when it cannot be placed — why.</summary>
    internal sealed record FramingRow(RealtimeFramingPreset Preset, RowState State, Miss Why);

    /// <summary>The whole list the bar opens: its rows, and its 儲存目前框選 row.</summary>
    internal sealed record FramingList(
        IReadOnlyList<FramingRow> Rows, SaveState Save, RealtimeFramingPreset? SameAs);

    /// <summary>
    /// The bar's 翻譯區域預設 for <paramref name="target"/>, with <paramref name="blocks"/> on the
    /// screen and <paramref name="appliedId"/> the preset they were last put there from (null when
    /// the user drew them, or that preset has been deleted since).
    /// </summary>
    /// <remarks>
    /// <para>Every preset of this monitor, or of this application, is listed — those that can be
    /// placed first, newest first, then those that cannot, greyed with the reason. A preset that is
    /// kept but not listed reads as lost; one listed and greyed says what to change.</para>
    ///
    /// <para>A row is <see cref="RowState.Current"/> when the blocks on the screen are exactly its
    /// own, whoever put them there, and <see cref="RowState.Modified"/> when it was the one applied
    /// and they have changed since. The save row says <see cref="SaveState.Same"/> for the same
    /// match: a second preset with the same blocks is never kept.</para>
    /// </remarks>
    public static FramingList ListFor(
        IReadOnlyList<RealtimeFramingPreset> presets, Target target,
        IReadOnlyList<RealtimeBlockPlacement> blocks, string? appliedId)
    {
        var stored = Stored(blocks, target);
        var usable = new List<FramingRow>();
        var unusable = new List<FramingRow>();
        RealtimeFramingPreset? sameAs = null;

        foreach (var preset in presets.Where(preset => IsRelated(preset, target)))
        {
            var fit = FitOf(preset, target);
            if (fit != Miss.None)
            {
                unusable.Add(new FramingRow(preset, RowState.Unusable, fit));
                continue;
            }

            bool same = stored.Count > 0 && SameBlocks(preset, stored);
            if (same) sameAs ??= preset;

            var state = same ? RowState.Current
                : preset.Id == appliedId ? RowState.Modified
                : RowState.Ready;
            usable.Add(new FramingRow(preset, state, Miss.None));
        }

        var save = blocks.Count == 0 ? SaveState.NoBlocks
            : sameAs is not null ? SaveState.Same
            : presets.Count >= RealtimeSettings.MaxFramingPresets ? SaveState.Full
            : SaveState.Ready;

        return new FramingList([.. usable, .. unusable], save, sameAs);
    }

    /// <summary>
    /// Whether switching away from <paramref name="blocks"/> loses work: there are blocks on the
    /// screen and no preset of <paramref name="target"/> keeps exactly them — a preset changed after
    /// it was applied, or blocks the user drew and never saved.
    /// </summary>
    public static bool HasUnsavedChanges(
        IReadOnlyList<RealtimeFramingPreset> presets, Target target,
        IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        if (blocks.Count == 0) return false;

        var stored = Stored(blocks, target);
        return !presets.Any(preset => IsFor(preset, target) && SameBlocks(preset, stored));
    }

    /// <summary>
    /// Whether <paramref name="preset"/> is about this monitor or this application at all, at any
    /// size — what the list shows, usable or not.
    /// </summary>
    private static bool IsRelated(RealtimeFramingPreset preset, Target target)
    {
        if (preset.CaptureMode != target.Mode) return false;

        return target.IsWindow
            ? target.HasWindow && SameProgram(preset.WindowProcess, preset.WindowPath, target)
            : preset.ScreenDeviceName == target.DeviceName;
    }

    /// <summary>
    /// Picks <paramref name="id"/> as its target's preset, unmarking whatever else that target had
    /// selected.
    /// </summary>
    /// <returns>False when no preset goes by that id any more.</returns>
    public static bool Select(string id)
    {
        var chosen = Realtime.FramingPresets.FirstOrDefault(preset => preset.Id == id);
        if (chosen is null) return false;

        // Nothing to write when it is already the only one selected for its target.
        if (chosen.Selected && Realtime.FramingPresets.Count(preset =>
                preset.TargetKey == chosen.TargetKey && preset.Selected) == 1) return true;

        SettingsService.Instance.UpdateRealtimeFramingPresets(SelectInto(Realtime.FramingPresets, id));
        return true;
    }

    /// <summary>The list with <paramref name="id"/> selected and its target's other presets unselected.</summary>
    public static List<RealtimeFramingPreset> SelectInto(IReadOnlyList<RealtimeFramingPreset> current, string id)
    {
        var presets = current.Select(preset => preset.Clone()).ToList();
        var chosen = presets.FirstOrDefault(preset => preset.Id == id);
        if (chosen is null) return presets;

        foreach (var preset in presets.Where(preset => preset.TargetKey == chosen.TargetKey))
            preset.Selected = preset.Id == id;

        return presets;
    }

    /// <summary>Gives a preset the user's own name; an empty name puts it back under the target's.</summary>
    /// <returns>False when no preset goes by <paramref name="id"/> any more.</returns>
    public static bool Rename(string id, string name)
    {
        var presets = Realtime.FramingPresets.Select(preset => preset.Clone()).ToList();
        var found = presets.FirstOrDefault(preset => preset.Id == id);
        if (found is null) return false;

        found.Name = RealtimeFramingPreset.CleanName(name);
        SettingsService.Instance.UpdateRealtimeFramingPresets(presets);
        return true;
    }

    /// <summary>Drops the preset <paramref name="id"/> — that framing is no longer kept.</summary>
    /// <returns>False when no preset went by that id.</returns>
    public static bool Forget(string id)
    {
        var presets = Realtime.FramingPresets.Where(preset => preset.Id != id).ToList();
        if (presets.Count == Realtime.FramingPresets.Count) return false;

        SettingsService.Instance.UpdateRealtimeFramingPresets(presets);
        return true;
    }

    /// <summary>The preset <paramref name="id"/> names, or null when it has been forgotten since.</summary>
    public static RealtimeFramingPreset? ForId(string id) =>
        Realtime.FramingPresets.FirstOrDefault(preset => preset.Id == id);

    /// <summary>
    /// Whether <paramref name="preset"/> belongs to <paramref name="target"/>: the same monitor at the
    /// same resolution — wherever it sits in the virtual desktop — or the same application with a
    /// window of the same size.
    /// </summary>
    private static bool IsFor(RealtimeFramingPreset preset, Target target)
    {
        if (preset.CaptureMode != target.Mode) return false;

        if (!target.IsWindow)
        {
            return preset.ScreenDeviceName == target.DeviceName &&
                   preset.Screen is { } screen &&
                   screen.Width == target.Screen.Width && screen.Height == target.Screen.Height;
        }

        if (!target.HasWindow) return false;

        var size = preset.WindowSize ?? new RealtimeFramingRect();
        return SameProgram(preset.WindowProcess, preset.WindowPath, target) &&
               size.Width == target.WindowBounds.Width &&
               size.Height == target.WindowBounds.Height;
    }

    /// <summary>
    /// <paramref name="blocks"/> as the file keeps them: relative to the monitor's top-left for a
    /// screen preset, and to the window's for a window one.
    /// </summary>
    private static List<RealtimeFramingBlock> Stored(
        IReadOnlyList<RealtimeBlockPlacement> blocks, Target target)
    {
        var origin = target.IsWindow ? target.WindowBounds.Location : target.Screen.Location;
        return [.. blocks.Take(RealtimeSettings.MaxBlockCount).Select(block =>
            RealtimeFramingBlock.From(block with
            {
                Bounds = new Rectangle(
                    block.Bounds.X - origin.X, block.Bounds.Y - origin.Y,
                    block.Bounds.Width, block.Bounds.Height),
            }))];
    }

    /// <summary>Whether <paramref name="preset"/> holds exactly these stored blocks.</summary>
    private static bool SameBlocks(
        RealtimeFramingPreset preset, IReadOnlyList<RealtimeFramingBlock> stored)
    {
        var kept = preset.Blocks ?? [];
        if (kept.Count != stored.Count) return false;

        for (int index = 0; index < kept.Count; index++)
        {
            if (kept[index] is not { } a) return false;
            var b = stored[index];
            if (a.X != b.X || a.Y != b.Y || a.Width != b.Width || a.Height != b.Height ||
                a.Profile != b.Profile || a.Orientation != b.Orientation ||
                a.CompareOffsetX != b.CompareOffsetX || a.CompareOffsetY != b.CompareOffsetY ||
                a.CompareScale != b.CompareScale || a.CompareHidden != b.CompareHidden)
                return false;
        }

        return true;
    }

    private static Rectangle ClampInto(Rectangle box, Rectangle area)
    {
        int left = Math.Max(box.Left, area.Left);
        int top = Math.Max(box.Top, area.Top);
        int right = Math.Min(box.Right, area.Right);
        int bottom = Math.Min(box.Bottom, area.Bottom);
        return right <= left || bottom <= top ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
    }
}
