namespace OverTranslate.Views.Realtime;

/// <summary>What the translation button shows after its dot and the service's mark.</summary>
public enum RealtimeButtonBody
{
    /// <summary>The language pair.</summary>
    Pair,
    /// <summary>即時翻譯中, before the session has named the pair.</summary>
    Running,
    /// <summary>載入模型中….</summary>
    LoadingModels,
    /// <summary>A failure being retried, or a passing message.</summary>
    Message
}

/// <summary>
/// The rules of the running capsule's translation button, kept apart from the window so they can be
/// checked without one.
/// </summary>
internal static class RealtimeTranslationButtonRule
{
    /// <summary>
    /// What stands after the mark. A message first — it is the answer to something that just
    /// happened, or a failure that has not cleared, and opening the panel does not put it away: it goes
    /// when it clears, which a change of service or language is the usual way to make happen. Paused,
    /// the pair, with 已暫停 beside the button: a paused session is where the wrong service or language
    /// is most likely to be fixed. Then 載入模型中… for as long as the load lasts.
    /// </summary>
    public static RealtimeButtonBody For(bool paused, bool loadingModels, bool messageShowing, bool hasLanguages) =>
        messageShowing ? RealtimeButtonBody.Message
        : paused ? RealtimeButtonBody.Pair
        : loadingModels ? RealtimeButtonBody.LoadingModels
        : hasLanguages ? RealtimeButtonBody.Pair
        : RealtimeButtonBody.Running;

    /// <summary>The service and the pair in a line, for the label card and the screen reader.</summary>
    public static string Summary(string provider, string source, string target) =>
        source.Length == 0 && target.Length == 0 ? provider : $"{provider} · {source} → {target}";

    /// <summary>
    /// The button's label card: the whole message when the button had to cut it, since the card is the
    /// only place left to read it; otherwise the service and the pair, which a message in their place
    /// or a glance at a mark may leave unsaid.
    /// </summary>
    public static string Title(string? cutMessage, string provider, string source, string target) =>
        string.IsNullOrWhiteSpace(cutMessage) ? Summary(provider, source, target) : cutMessage;
}
