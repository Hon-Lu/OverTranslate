using OverTranslate.Services.Providers;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// What a realtime session has translated so far, as the model is shown it: the last few rounds of
/// lines and what they became, for keeping a name or a subject the way the session started it.
/// </summary>
/// <remarks>
/// <para>A round is one request's lines that came back translated. A line the provider could not
/// translate is never written: it would be shown to the model as its own translation, which reads
/// as "leave this one as it is".</para>
///
/// <para>Measured, the history is where the realtime gain comes from — far more than the other
/// lines on screen — and eight rounds was enough of it. Only ever added to at the end, and cut in
/// half rather than by one when it is full: a server that reuses the start of the last request it
/// read can then reuse all of the history but the newest round, which it could not if the oldest
/// round went every time a new one came. Cut in half again while it is still past
/// <see cref="TokenLimit"/>, so a session of long lines does not crowd the screen out of the
/// request.</para>
///
/// <para>Shared by every region of a session, and used from whichever threads their translations
/// finish on.</para>
/// </remarks>
internal sealed class RealtimeTranslationHistory
{
    /// <summary>How many rounds are kept before the oldest half goes.</summary>
    internal const int MaxRounds = 8;

    /// <summary>
    /// Roughly how many tokens the kept rounds may come to — see <see cref="EstimateTokens"/>. Half
    /// of the context a model is assumed to have when nothing says otherwise, which leaves the other
    /// half for the instruction, the screen and the answer.
    /// </summary>
    internal const int TokenLimit = 2000;

    private readonly object _gate = new();
    private List<IReadOnlyList<TranslationPair>> _rounds = [];

    /// <summary>Every kept line, oldest first, as one list.</summary>
    public IReadOnlyList<TranslationPair> Lines
    {
        get
        {
            lock (_gate) return [.. _rounds.SelectMany(round => round)];
        }
    }

    /// <summary>How many rounds are kept.</summary>
    public int Rounds
    {
        get
        {
            lock (_gate) return _rounds.Count;
        }
    }

    /// <summary>Adds one request's translated lines as a round; nothing when there are none.</summary>
    /// <param name="stillCurrent">
    /// Whether the request still belongs to the session as it is now, asked under the same lock as
    /// <see cref="Clear"/> takes: an answer for the language pair just left, landing a moment after
    /// the history was cleared for the new one, is then refused rather than kept.
    /// </param>
    public void Add(IReadOnlyList<TranslationPair> round, Func<bool>? stillCurrent = null)
    {
        if (round.Count == 0) return;

        lock (_gate)
        {
            if (stillCurrent is not null && !stillCurrent()) return;
            _rounds.Add([.. round]);
            if (_rounds.Count > MaxRounds)
                _rounds = _rounds[^(MaxRounds / 2)..];
            while (_rounds.Count > 1 && EstimateTokens(_rounds.SelectMany(kept => kept)) > TokenLimit)
                _rounds = _rounds[(_rounds.Count / 2)..];
        }
    }

    /// <summary>Forgets every round: the session has ended, or now translates into something else.</summary>
    public void Clear()
    {
        lock (_gate) _rounds = [];
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
