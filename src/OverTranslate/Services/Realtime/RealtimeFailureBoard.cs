namespace OverTranslate.Services.Realtime;

/// <summary>Which stage of a region's work a failure is about.</summary>
internal enum RealtimeFailureStage
{
    /// <summary>Recognition, cleared by the region's next pass that gets through.</summary>
    Read,
    /// <summary>Translation, cleared by the region's next result that reaches the screen.</summary>
    Translate,
}

/// <summary>
/// The failure a session is still retrying, if any: what the bar says while it lasts.
/// </summary>
/// <remarks>
/// A failed read is retried after a wait of up to sixteen seconds (see
/// <see cref="RealtimeRegionState.FirstWaitAfterFailure"/>), and a failed translation is retried
/// at the region's next poll or later.
/// A message that timed out after a couple of seconds left the bar looking healthy for most of that
/// wait, so the failure is kept until the stage that failed gets through again — for every region it
/// failed in, since one region recovering says nothing about another still failing. Read and
/// translation are cleared separately: a read that gets through says nothing about the provider.
/// </remarks>
internal sealed class RealtimeFailureBoard
{
    private readonly object _gate = new();
    private readonly HashSet<(int Region, RealtimeFailureStage Stage)> _failing = [];
    private string? _message;

    /// <summary>The message to show, or null when nothing is failing.</summary>
    public string? Message
    {
        get
        {
            lock (_gate) return _message;
        }
    }

    /// <summary>Records a failure; the latest message is the one shown.</summary>
    /// <returns>Whether <see cref="Message"/> changed.</returns>
    public bool Report(int region, RealtimeFailureStage stage, string message)
    {
        lock (_gate)
        {
            _failing.Add((region, stage));
            if (_message == message) return false;
            _message = message;
            return true;
        }
    }

    /// <summary>Records that a stage got through in a region.</summary>
    /// <returns>Whether <see cref="Message"/> changed — true only when nothing is failing any more.</returns>
    public bool Recovered(int region, RealtimeFailureStage stage)
    {
        lock (_gate)
        {
            if (!_failing.Remove((region, stage)) || _failing.Count > 0) return false;
            _message = null;
            return true;
        }
    }

    /// <summary>Forgets a region that has stopped, whichever stages it was failing in.</summary>
    /// <returns>Whether <see cref="Message"/> changed.</returns>
    public bool Forget(int region)
    {
        lock (_gate)
        {
            if (_failing.RemoveWhere(entry => entry.Region == region) == 0 || _failing.Count > 0) return false;
            _message = null;
            return true;
        }
    }

    /// <summary>Forgets everything, for a session that has stopped or paused.</summary>
    /// <returns>Whether <see cref="Message"/> changed.</returns>
    public bool Clear()
    {
        lock (_gate)
        {
            _failing.Clear();
            if (_message is null) return false;
            _message = null;
            return true;
        }
    }
}
