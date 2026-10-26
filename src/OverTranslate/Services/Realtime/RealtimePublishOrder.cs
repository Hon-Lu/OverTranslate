namespace OverTranslate.Services.Realtime;

/// <summary>
/// Keeps a region's overlay moving forwards in time while its translations do not.
/// </summary>
/// <remarks>
/// Once translations stopped being awaited in the poll loop, several can be in flight over one
/// region at once and they finish in whatever order the provider answers — a long line sent first
/// can easily come back after the short line that replaced it. Drawing them in arrival order would
/// leave the overlay showing the older of the two, and leave it there until the next change, so
/// each pass is numbered when it is read and a pass is drawn only while nothing later has been.
/// </remarks>
internal sealed class RealtimePublishOrder
{
    private readonly object _gate = new();

    private long _issued;
    private long _published;

    /// <summary>Numbers a pass, in the order the region was read.</summary>
    public long NextPass()
    {
        lock (_gate) return ++_issued;
    }

    /// <summary>
    /// Whether this pass may be drawn now. True at most once per pass, and never after a later
    /// pass has been drawn.
    /// </summary>
    public bool TryClaim(long pass)
    {
        lock (_gate)
        {
            if (pass <= _published) return false;
            _published = pass;
            return true;
        }
    }
}
