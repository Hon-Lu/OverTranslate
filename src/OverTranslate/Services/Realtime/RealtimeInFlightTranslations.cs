namespace OverTranslate.Services.Realtime;

/// <summary>
/// The lines a realtime session has asked for and not yet heard back about, so a later pass waits
/// for a line already on its way instead of asking for it again.
/// </summary>
/// <remarks>
/// <para>A line that is still on screen is read again at every look, and with a model a pass can
/// take seconds — long enough for two or three more passes over the same line to start. Each used
/// to send it again, and a local server that answers one request at a time worked through every
/// copy before the next new line. Here the first pass to need a line sends it, and any pass that
/// needs it before the answer is in takes the same answer once it is in.</para>
///
/// <para>Keyed the way the cache is, and tagged with the cache's generation: after a pause or a
/// change of languages a line is asked for afresh, whatever the pass before is still waiting on —
/// its answer will not be drawn or kept.</para>
///
/// <para>Used from whichever threads the passes run and the answers arrive on.</para>
/// </remarks>
internal sealed class RealtimeInFlightTranslations
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Line> _lines = [];

    /// <summary>
    /// The line for <paramref name="key"/> in this generation: one already on its way, or a new one
    /// that the caller has now taken on to send.
    /// </summary>
    /// <returns>The line, and whether the caller is the one to send it.</returns>
    public (Line Line, bool Send) Claim(string key, int generation)
    {
        lock (_gate)
        {
            if (_lines.TryGetValue(key, out var line) && line.Generation == generation)
                return (line, false);

            line = new Line(this, key, generation);
            _lines[key] = line;
            return (line, true);
        }
    }

    /// <summary>How many lines are on their way.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    private void Remove(Line line)
    {
        lock (_gate)
        {
            if (_lines.TryGetValue(line.Key, out var held) && ReferenceEquals(held, line))
                _lines.Remove(line.Key);
        }
    }

    /// <summary>One line on its way, and when it is done.</summary>
    internal sealed class Line(RealtimeInFlightTranslations owner, string key, int generation)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key { get; } = key;

        public int Generation { get; } = generation;

        /// <summary>
        /// Done once the pass that sent it has finished with it, however that went — by then the
        /// translation, if there is one, is in the cache, which is where the waiting pass reads it.
        /// </summary>
        public Task Done => _done.Task;

        /// <summary>
        /// Ends the line: no longer on its way, so the next pass to need it and not find it in the
        /// cache sends it again.
        /// </summary>
        public void Complete()
        {
            owner.Remove(this);
            _done.TrySetResult();
        }
    }
}
