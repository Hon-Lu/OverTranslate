namespace OverTranslate.Services.Realtime;

/// <summary>
/// 譯文對齊 as a block draws it — <see cref="Models.RealtimeProfile.Align"/> resolved. See
/// <see cref="Layout.RealtimeSourceAlignment.For"/> for what each comes to on a group.
/// </summary>
public enum RealtimeTextAlign
{
    /// <summary>The source's own alignment where its rows show one, else as the group was read.</summary>
    Auto,

    /// <summary>Every group centred on its source.</summary>
    Center,

    /// <summary>Every group from its source's left edge.</summary>
    Left,
}
