using OverTranslate.Services.Providers;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// What a realtime session has translated so far, as the model is shown it: the last few pairs of a
/// line and what it became, for keeping a name or a subject the way the session started it.
/// </summary>
/// <remarks>
/// <para>Each request adds the lines it got back translated. A line the provider could not
/// translate is never written: it would be shown to the model as its own translation, which reads
/// as "leave this one as it is".</para>
///
/// <para>Measured, the history is where the realtime gain comes from — far more than the other
/// lines on screen — and sixteen pairs, cut to the newest eight, was enough of it: eight cut to four
/// named things less consistently. Counted in pairs rather than requests, so a screen of twenty new
/// lines and a subtitle of one weigh what they carry. Only ever added to at the end, and cut in half
/// rather than by one when it is full: a server that reuses the start of the last request it read
/// can then reuse all of the history but the newest lines, which it could not if the oldest went
/// every time a new one came. Cut in half again while it is still past <see cref="TokenLimit"/>, so
/// a session of long lines does not crowd the screen out of the request.</para>
///
/// <para>Shared by every region of a session, and used from whichever threads their translations
/// finish on.</para>
/// </remarks>
internal sealed class RealtimeTranslationHistory
{
    /// <summary>How many pairs are kept before all but the newest half go.</summary>
    internal const int MaxPairs = 16;

    /// <summary>
    /// Roughly how many tokens the kept pairs may come to — see <see cref="EstimateTokens"/>. Half
    /// of the context a model is assumed to have when nothing says otherwise, which leaves the other
    /// half for the instruction, the screen and the answer.
    /// </summary>
    internal const int TokenLimit = 2000;

    private readonly object _gate = new();
    private List<TranslationPair> _lines = [];

    /// <summary>Every kept line, oldest first.</summary>
    public IReadOnlyList<TranslationPair> Lines
    {
        get
        {
            lock (_gate) return [.. _lines];
        }
    }

    /// <summary>Adds one request's translated lines at the end; nothing when there are none.</summary>
    /// <param name="stillCurrent">
    /// Whether the request still belongs to the session as it is now, asked under the same lock as
    /// <see cref="Clear"/> takes: an answer for the language pair just left, landing a moment after
    /// the history was cleared for the new one, is then refused rather than kept.
    /// </param>
    public void Add(IReadOnlyList<TranslationPair> lines, Func<bool>? stillCurrent = null)
    {
        if (lines.Count == 0) return;

        lock (_gate)
        {
            if (stillCurrent is not null && !stillCurrent()) return;
            _lines.AddRange(lines);
            if (_lines.Count > MaxPairs)
                _lines = _lines[^(MaxPairs / 2)..];
            while (_lines.Count > 1 && EstimateTokens(_lines) > TokenLimit)
                _lines = _lines[(_lines.Count / 2)..];
        }
    }

    /// <summary>Forgets every line: the session has ended, or now translates into something else.</summary>
    public void Clear()
    {
        lock (_gate) _lines = [];
    }

    /// <summary>
    /// A rough token count for <paramref name="lines"/>, both sides of each: one per CJK or kana or
    /// Hangul character, one per four of anything else.
    /// </summary>
    /// <remarks>
    /// No tokenizer — there is no saying which model is on the other end — and close enough to keep
    /// the history to a share of the request, which is all it is for.
    /// </remarks>
    internal static int EstimateTokens(IEnumerable<TranslationPair> lines)
    {
        int wide = 0, other = 0;
        foreach (var line in lines)
            foreach (var c in line.Source + line.Translation)
            {
                if (c is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯') wide++;
                else other++;
            }
        return wide + other / 4;
    }
}
