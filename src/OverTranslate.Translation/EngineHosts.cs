using NLog;

namespace OverTranslate.Translation;

/// <summary>
/// The hosts one service answers on, which of them requests go to, and moving every request on
/// together when that one cannot be reached.
/// </summary>
/// <remarks>
/// <para>For a service its owner runs under more than one name, each reachable from some places
/// and not others — TranSmart's two, measured 2026-11-21 (<c>.ai/endpoint-health/</c>): mainland DNS
/// sends transmart.qq.com to a server with a valid certificate and everywhere else to one whose
/// certificate expired on 2026-10-11, while yi.qq.com, the same site under the same certificate
/// since 2020, answers outside China and in parts of it resolves to an address that cannot be
/// checked from outside. No one host is right for everyone, and which works can change without
/// notice, so the order is a starting point, not a rule.</para>
///
/// <para>Only a request that got no answer at all moves on: a handshake refused, a connection that
/// failed. A status, or an error inside a 200, came from the service, and every host is the same
/// service, so the next would only say the same again.</para>
///
/// <para>One state for every request. The first to fail moves the rest on at once, so requests
/// sent after it go straight to the next host instead of each failing the same handshake first.
/// Requests already on their way when it failed are not held back to wait for it: tried
/// 2026-11-21 from Taiwan, holding a detected TranSmart request's other scripts until the first
/// had its answer put them in a queue behind a whole round trip — 2.8 s for the first translation
/// against 1.75–2.1 s letting each find out, and 2.1 s against 1.4 s on a host that worked — to
/// save, at most, one failed handshake run alongside the first.</para>
///
/// <para>The host that answered is handed to <c>save</c> whenever it changes, and the next run
/// starts there: otherwise every start would pay one failed handshake again — 0.4 s, measured —
/// before its first translation. Nothing is lost by remembering a host that later stops working:
/// it fails like any other, and the next one is tried.</para>
/// </remarks>
internal sealed class EngineHosts
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _engine;
    private readonly string[] _hosts;
    private readonly Action<string>? _save;

    // Index into _hosts of the one requests go to.
    private int _current;

    // The host last handed to _save, or the one this run started at.
    private string _saved;


    /// <param name="engine">Whose hosts these are, for the log.</param>
    /// <param name="hosts">In the order a run with nothing saved tries them.</param>
    /// <param name="saved">The host a previous run last reached; ignored when it is not one of these.</param>
    /// <param name="save">Told the host whenever a different one answers.</param>
    public EngineHosts(string engine, IReadOnlyList<string> hosts, string? saved = null, Action<string>? save = null)
    {
        if (hosts.Count == 0) throw new ArgumentException("No hosts.", nameof(hosts));

        _engine = engine;
        _hosts = [.. hosts];
        _save = save;
        _current = Math.Max(0, Array.IndexOf(_hosts, saved));

        // Nothing written while the host a run starts at keeps answering.
        _saved = _hosts[_current];
    }

    /// <summary>The host the next request goes to.</summary>
    public string Current => _hosts[Volatile.Read(ref _current)];

    /// <summary>
    /// Sends with <paramref name="send"/> to the current host and, when it cannot be reached, to
    /// each of the others in turn, until one answers or every one has failed.
    /// </summary>
    /// <exception cref="HttpRequestException">No host could be reached; the last host's failure.</exception>
    public async Task<T> SendAsync<T>(Func<string, Task<T>> send, CancellationToken cancellationToken)
    {
        var start = Volatile.Read(ref _current);
        for (var tried = 0; ; tried++)
        {
            var index = (start + tried) % _hosts.Length;
            try
            {
                var answer = await send(_hosts[index]);
                Answered(index);
                return answer;
            }
            catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested && tried + 1 < _hosts.Length)
            {
                var next = (index + 1) % _hosts.Length;

                // Only the first request to find this host unreachable moves everyone on; the ones
                // already on their way there fail with it and follow without moving anyone again.
                if (Interlocked.CompareExchange(ref _current, next, index) == index)
                    Log.Info("{Engine}：{Host} 連不上（{Error}），改用 {Next}",
                        _engine, _hosts[index], ex.HttpRequestError, _hosts[next]);
            }
        }
    }

    private void Answered(int index)
    {
        // A host that answered is where everyone goes, even when another request moved them on in
        // the meantime: an answer is better evidence than a failure.
        Volatile.Write(ref _current, index);

        var host = _hosts[index];
        if (_save is null || Interlocked.Exchange(ref _saved, host) == host) return;

        try
        {
            _save(host);
        }
        catch (Exception ex)
        {
            // Remembering is a saving of time, not something a translation should fail over.
            Log.Warn(ex, "{Engine}：記住主機 {Host} 失敗", _engine, host);
        }
    }
}
