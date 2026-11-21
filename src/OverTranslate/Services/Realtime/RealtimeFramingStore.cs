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
/// <para>A <b>target</b> is what a framing belongs to, not what it contains: the monitor's device
/// name and rectangle for 螢幕擷取, the application's process and window title for 視窗擷取. One
/// target may hold several presets, and which of them a session opens on is the one marked
/// <see cref="RealtimeFramingPreset.Selected"/> — saving a framing and picking one in the page's list
/// both set it — or the newest it has when nothing is marked.</para>
///
/// <para>For a window the title decides first: a preset saved under the window's current title is
/// that window's, and among those the selected one wins. Only when no title matches — the window has
/// retitled itself since, or these presets belong to other windows of the same application — does the
/// selection speak for all of them, and then a second window cannot be told from the first: with
/// several titles and none selected the honest answer is no framing rather than a guess that would
/// put blocks over the wrong window. The same answer <see cref="Capture.CaptureWindowList.FindStored"/>
/// gives for the same question.</para>
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
    public readonly record struct Target(
        RealtimeCaptureMode Mode,
        Rectangle Screen,
        string DeviceName,
        string WindowProcess = "",
        string WindowTitle = "")
    {
        /// <summary>Whether this target is a window rather than a whole screen.</summary>
        public bool IsWindow => Mode == RealtimeCaptureMode.Window;
    }

    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    /// <summary>
    /// Whether two targets are the same target for the purpose of offering a framing back: the same
    /// mode, monitor and rectangle, and for a window the same application.
    /// </summary>
    /// <remarks>
    /// Looser than <see cref="Match(IReadOnlyList{RealtimeFramingPreset}, Target)"/> in one place: the
    /// title is ignored, because this is asked of the one framing the session remembers from the
    /// sitting before — a window retitling itself between one sitting and the next is still that
    /// window, and there is no second candidate it could be confused with.
    /// </remarks>
    public static bool SameTarget(Target a, Target b) =>
        a.Mode == b.Mode &&
        a.DeviceName == b.DeviceName &&
        a.Screen == b.Screen &&
        (!a.IsWindow || a.WindowProcess == b.WindowProcess);

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

    /// <summary>
    /// The blocks <paramref name="preset"/> is to be started with, held to <paramref name="screen"/>:
    /// a copy of each, clamped to the screen and dropped when nothing of it is left. Never more than
    /// the session can frame at once.
    /// </summary>
    /// <remarks>
    /// A preset is only offered for the screen it was drawn on — see <see cref="Match(Target)"/> — so
    /// nothing here should ever clamp in practice. It is here because this is the one place that
    /// turns stored numbers into rectangles the session reads pixels through, and a settings file is
    /// something a user may edit.
    /// </remarks>
    public static IReadOnlyList<RealtimeBlockPlacement> Placements(
        RealtimeFramingPreset preset, Rectangle screen) =>
        [.. (preset.Blocks ?? []).OfType<RealtimeFramingBlock>()
            .Select(block => block.ToPlacement())
            .Select(placement => placement with { Bounds = ClampInto(placement.Bounds, screen) })
            .Where(placement => placement.Bounds.Width > 0 && placement.Bounds.Height > 0)
            .Take(RealtimeSettings.MaxBlockCount)];

    /// <summary>
    /// Saves <paramref name="blocks"/> as a framing for <paramref name="target"/>, and selects it —
    /// unless the same arrangement is already kept for that target, which is then selected instead of
    /// saved twice.
    /// </summary>
    /// <returns>The preset as stored — the one already kept, or the new one.</returns>
    public static RealtimeFramingPreset Save(Target target, IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        var (presets, saved) = SaveInto(Realtime.FramingPresets, target, blocks, DateTime.UtcNow);
        SettingsService.Instance.UpdateRealtimeFramingPresets(presets);
        return saved;
    }

    /// <summary>
    /// <see cref="Save(Target, IReadOnlyList{RealtimeBlockPlacement})"/> as a list in and a list out,
    /// for a test that has no settings file.
    /// </summary>
    /// <remarks>
    /// A framing whose blocks are the same as one already kept for this target is that preset
    /// selected again, not a copy: pressing 儲存框選 twice on an unchanged screen is one decision. A
    /// layout that differs in any block — moved, resized, another mode, another compare offset — is a
    /// new preset, which is the whole point of keeping several for one target.
    /// </remarks>
    public static (List<RealtimeFramingPreset> Presets, RealtimeFramingPreset Saved) SaveInto(
        IReadOnlyList<RealtimeFramingPreset> current, Target target,
        IReadOnlyList<RealtimeBlockPlacement> blocks, DateTime savedAt)
    {
        var presets = current.Select(preset => preset.Clone()).ToList();
        var targetPresets = presets.Where(preset => IsFor(preset, target)).ToList();

        var saved = targetPresets.FirstOrDefault(preset => SameBlocks(preset, blocks));
        if (saved is null)
        {
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
        saved.Blocks = [.. blocks.Take(RealtimeSettings.MaxBlockCount).Select(RealtimeFramingBlock.From)];

        // One selected preset per target, and this is it: a saved framing is the one the user wants
        // the next session on this target to open on.
        foreach (var other in presets.Where(preset => IsFor(preset, target)))
            other.Selected = other.Id == saved.Id;

        if (presets.Count > RealtimeSettings.MaxFramingPresets)
            presets.RemoveRange(RealtimeSettings.MaxFramingPresets, presets.Count - RealtimeSettings.MaxFramingPresets);

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
    /// Whether <paramref name="preset"/> was drawn on <paramref name="target"/>'s screen — and, for a
    /// window, over the same application. The window's title is compared only where it can narrow the
    /// field; see <see cref="Match(IReadOnlyList{RealtimeFramingPreset}, Target)"/>.
    /// </summary>
    private static bool IsFor(RealtimeFramingPreset preset, Target target) =>
        preset.CaptureMode == target.Mode &&
        preset.ScreenDeviceName == target.DeviceName &&
        (!target.IsWindow || preset.WindowProcess == target.WindowProcess) &&
        preset.Screen is { } screen &&
        screen.Equals(RealtimeFramingRect.From(target.Screen));

    /// <summary>Whether <paramref name="preset"/> holds exactly these blocks.</summary>
    private static bool SameBlocks(
        RealtimeFramingPreset preset, IReadOnlyList<RealtimeBlockPlacement> blocks)
    {
        var stored = preset.Blocks ?? [];
        int count = Math.Min(blocks.Count, RealtimeSettings.MaxBlockCount);
        if (stored.Count != count) return false;

        for (int index = 0; index < count; index++)
        {
            if (stored[index] is not { } kept) return false;
            var now = RealtimeFramingBlock.From(blocks[index]);
            if (kept.X != now.X || kept.Y != now.Y || kept.Width != now.Width || kept.Height != now.Height ||
                kept.Profile != now.Profile || kept.Orientation != now.Orientation ||
                kept.CompareOffsetX != now.CompareOffsetX || kept.CompareOffsetY != now.CompareOffsetY ||
                kept.CompareScale != now.CompareScale || kept.CompareHidden != now.CompareHidden)
                return false;
        }

        return true;
    }

    private static Rectangle ClampInto(Rectangle box, Rectangle screen)
    {
        int left = Math.Max(box.Left, screen.Left);
        int top = Math.Max(box.Top, screen.Top);
        int right = Math.Min(box.Right, screen.Right);
        int bottom = Math.Min(box.Bottom, screen.Bottom);
        return right <= left || bottom <= top ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
    }
}
