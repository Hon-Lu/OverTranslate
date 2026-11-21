using OverTranslate.Models;
using OverTranslate.Services;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The writes a mode's editor makes: a value of a custom mode changed, every value put back, a
/// preset split into a mode of the user's own. Shared by the mode card on the page and the running
/// bar's 模式 panel, so a change made in either is stored the same way and reaches the session down
/// the same path — <see cref="SettingsService.UpdateRealtimeProfiles"/>.
/// </summary>
internal static class RealtimeProfileStore
{
    private static RealtimeSettings Realtime => SettingsService.Instance.Current.Realtime;

    /// <summary>
    /// Changes the custom mode <paramref name="profileId"/> and stores the whole list, keeping only
    /// what differs from its preset (so 已修改 means different).
    /// </summary>
    /// <returns>False when there is no such custom mode — deleted elsewhere since it was shown.</returns>
    public static bool Change(string profileId, Action<RealtimeProfile> change)
    {
        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        int index = list.FindIndex(profile => profile.Id == profileId);
        if (index < 0) return false;

        change(list[index]);
        list[index] = RealtimeProfileEditing.WithoutTemplateValues(list[index]);
        SettingsService.Instance.UpdateRealtimeProfiles(list);
        return true;
    }

    /// <summary>Puts every value of <paramref name="profileId"/> back to its preset's.</summary>
    public static bool ResetAll(string profileId) => Change(profileId, profile =>
    {
        foreach (var param in RealtimeProfileEditing.All)
            RealtimeProfileEditing.Clear(profile, param);
    });

    /// <summary>
    /// Whether <paramref name="change"/> on the preset <paramref name="presetId"/> comes to anything
    /// for <paramref name="param"/> — a thumb let go where it was offers no new mode.
    /// </summary>
    public static bool WouldChangePreset(string presetId, ProfileParam param, Action<RealtimeProfile> change)
    {
        var trial = RealtimeProfile.CreateCustom(presetId, "");
        change(trial);
        return RealtimeProfileEditing.IsModified(RealtimeProfileEditing.WithoutTemplateValues(trial), param);
    }

    /// <summary>Whether another custom mode can be made.</summary>
    public static bool IsFull => Realtime.Profiles.Count >= RealtimeSettings.MaxCustomProfiles;

    /// <summary>
    /// Makes a custom mode of the preset <paramref name="presetId"/> — named after it, see
    /// <see cref="RealtimeProfileEditing.ForkName(string, IEnumerable{string})"/> — with
    /// <paramref name="change"/> on it, and moves every block on the preset onto it.
    /// </summary>
    /// <remarks>
    /// The blocks on the preset go with it, and so does the answer new blocks start on where it was
    /// the preset (the user's call): what is changed is meant for what is on screen, and while
    /// translating a change applies at once — true only if the blocks are on this mode.
    /// </remarks>
    /// <returns>The mode made, or null with ten made already.</returns>
    public static RealtimeProfile? Fork(string presetId, Action<RealtimeProfile>? change)
    {
        var list = Realtime.Profiles.Select(profile => profile.Clone()).ToList();
        if (list.Count >= RealtimeSettings.MaxCustomProfiles) return null;

        var created = RealtimeProfile.CreateCustom(presetId,
            RealtimeProfileEditing.ForkName(presetId,
                RealtimeProfileEditing.Choices(Realtime.Profiles).Select(choice => choice.Name)));
        change?.Invoke(created);
        created = RealtimeProfileEditing.WithoutTemplateValues(created);
        list.Add(created);

        var realtime = Realtime;
        if (realtime.NewBlockProfile == presetId) realtime.NewBlockProfile = created.Id;
        SettingsService.Instance.UpdateRealtimeProfiles(list);
        RealtimeSessionController.Instance.FollowProfile(presetId, created.Id);
        return created;
    }
}
