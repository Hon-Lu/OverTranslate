namespace OverTranslate.Services.Providers;

/// <summary>
/// How long translation waits, in one place: a single figure and the two that follow from it.
/// </summary>
/// <remarks>
/// <para>One number rather than three because the three only work in proportion. Change
/// <see cref="Hedge"/> and the others move with it; set them one by one and it is easy to end up
/// with a backup that starts a second before the deadline, or a request abandoned while the ladder
/// is still waiting on it.</para>
///
/// <para>The same for every engine the application runs on these clocks — Google, Microsoft, Bing
/// and DeepL. The OpenAI-compatible provider is the exception and keeps its own: a local model can
/// spend tens of seconds just loading.</para>
/// </remarks>
public static class TranslationTiming
{
    /// <summary>
    /// How long a request may take before the next step of <see cref="ResilientProvider"/>'s ladder
    /// goes up beside it.
    /// </summary>
    /// <remarks>
    /// Past the slow end of normal, so a second request goes out for one that is stuck rather than
    /// one that is merely slow: the slowest ordinary answers measured were 3.5 s (Google RPC) and
    /// 3.3 s (Bing), in <c>.ai/translation-service-analysis/</c>. A failure never waits for this.
    /// </remarks>
    public static readonly TimeSpan Hedge = TimeSpan.FromSeconds(5);

    /// <summary>How long one request may take before it is abandoned as failed.</summary>
    /// <remarks>
    /// Two steps of the ladder: the primary is asked again at <see cref="Hedge"/>, the first backup
    /// goes up at twice that, and a request stuck since the start gives up at the same moment
    /// rather than being waited on by nothing.
    /// </remarks>
    public static readonly TimeSpan Request = Hedge * 2;

    /// <summary>How long a whole batch may take before what is left is shown in its original text.</summary>
    /// <remarks>
    /// Three steps: the primary, the primary again and the first backup start at 0, 1 and 2 hedges,
    /// and the last of them still has a full hedge to answer in.
    /// </remarks>
    public static readonly TimeSpan Deadline = Hedge * 3;
}
