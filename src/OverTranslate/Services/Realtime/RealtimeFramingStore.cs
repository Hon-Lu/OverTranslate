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
/// monitor's device and rectangle: its blocks are absolute pixels there, so a different screen, or
/// the same one at another resolution, is another target. A window target is the application and the
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
    public readonly record struct Target(
        RealtimeCaptureMode Mode,
        Rectangle Screen,
        string DeviceName,
        string WindowProcess = "",
        string WindowTitle = "",
        Rectangle WindowBounds = default)
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
    /// sittings while staying the same window.
    /// </remarks>
    public static bool SameTarget(Target a, Target b) =>
        a.Mode == b.Mode &&
        (a.IsWindow
            ? a.WindowProcess == b.WindowProcess && a.WindowSize == b.WindowSize
            : a.DeviceName == b.DeviceName && a.Screen == b.Screen);

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
    }

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

        // A preset of the right size is a candidate Match may still have declined — two windows of
        // one title-less application, say; the size is not what stood in the way, so it is not the
        // reason to give.
        if (presets.Any(preset => IsFor(preset, target))) return Miss.None;

        if (target.IsWindow)
        {
            if (!target.HasWindow) return Miss.None;

            var near = presets.Where(preset =>
                preset.IsForWindow && preset.WindowProcess == target.WindowProcess).ToList();
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
    /// <paramref name="target"/>: a copy of each, offset by the window's current top-left on a window
    /// preset, clamped to the screen or window it belongs to and dropped when nothing of it is left.
    /// Never more than the session can frame at once.
    /// </summary>
    /// <remarks>
    /// A window preset whose stored window size is not the size the window has now is not placed at
    /// all: the blocks would cover a different part of the window. <see cref="Explain"/> is what says
    /// so; this returns nothing rather than a guess.
    /// </remarks>
    public static IReadOnlyList<RealtimeBlockPlacement> Placements(
        RealtimeFramingPreset preset, Target target)
    {
        var area = preset.IsForWindow ? target.WindowBounds : target.Screen;
        if (area.Width <= 0 || area.Height <= 0) return [];

        if (preset.IsForWindow)
        {
            var size = preset.WindowSize ?? new RealtimeFramingRect();
            if (size.Width != area.Width || size.Height != area.Height) return [];
        }

        return [.. (preset.Blocks ?? []).OfType<RealtimeFramingBlock>()
            .Select(block => block.ToPlacement())
            .Select(placement => placement with
            {
                Bounds = Offset(placement.Bounds, area, preset.IsForWindow),
            })
            .Select(placement => placement with { Bounds = ClampInto(placement.Bounds, area) })
            .Where(placement => placement.Bounds.Width > 0 && placement.Bounds.Height > 0)
            .Take(RealtimeSettings.MaxBlockCount)];
    }

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
        saved.WindowTitle = target.IsWindow ? target.WindowTitle ?? "" : "";
        saved.WindowSize = target.IsWindow
            ? RealtimeFramingRect.From(target.WindowBounds.Size)
            : new RealtimeFramingRect();
        saved.Blocks = stored;

        // One selected preset per target, and this is it: a saved framing is the one the user wants
        // the next session on this target to open on.
        foreach (var other in presets.Where(preset => preset.TargetKey == saved.TargetKey))
            other.Selected = other.Id == saved.Id;

        return (presets, saved);
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
    /// Whether <paramref name="preset"/> belongs to <paramref name="target"/>: the same screen and
    /// rectangle, or the same application with a window of the same size.
    /// </summary>
    private static bool IsFor(RealtimeFramingPreset preset, Target target)
    {
        if (preset.CaptureMode != target.Mode) return false;

        if (!target.IsWindow)
        {
            return preset.ScreenDeviceName == target.DeviceName &&
                   preset.Screen is { } screen &&
                   screen.Equals(RealtimeFramingRect.From(target.Screen));
        }

        if (!target.HasWindow) return false;

        var size = preset.WindowSize ?? new RealtimeFramingRect();
        return preset.WindowProcess == target.WindowProcess &&
               size.Width == target.WindowBounds.Width &&
               size.Height == target.WindowBounds.Height;
    }

    /// <summary>
    /// <paramref name="blocks"/> as the file keeps them: absolute for a screen preset, and relative
    /// to the window's top-left for a window one.
    /// </summary>
    private static List<RealtimeFramingBlock> Stored(
        IReadOnlyList<RealtimeBlockPlacement> blocks, Target target)
    {
        var origin = target.IsWindow ? target.WindowBounds.Location : Point.Empty;
        return [.. blocks.Take(RealtimeSettings.MaxBlockCount).Select(block =>
            RealtimeFramingBlock.From(target.IsWindow
                ? block with
                {
                    Bounds = new Rectangle(
                        block.Bounds.X - origin.X, block.Bounds.Y - origin.Y,
                        block.Bounds.Width, block.Bounds.Height),
                }
                : block))];
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

    private static Rectangle Offset(Rectangle bounds, Rectangle area, bool relative) =>
        relative
            ? new Rectangle(bounds.X + area.X, bounds.Y + area.Y, bounds.Width, bounds.Height)
            : bounds;

    private static Rectangle ClampInto(Rectangle box, Rectangle area)
    {
        int left = Math.Max(box.Left, area.Left);
        int top = Math.Max(box.Top, area.Top);
        int right = Math.Min(box.Right, area.Right);
        int bottom = Math.Min(box.Bottom, area.Bottom);
        return right <= left || bottom <= top ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
    }
}
