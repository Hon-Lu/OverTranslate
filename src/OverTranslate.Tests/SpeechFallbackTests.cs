using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using Xunit;

namespace OverTranslate.Tests;

// #436: a host that cannot be reached is skipped with every engine on it, for a while that grows
// while it stays unreachable; a stuck request gets the next engine beside it.
public class SpeechFallbackTests
{
    private const string Text = "こんにちは";
    private const string Ja = "ja";

    // ---- Unreachable hosts ------------------------------------------------------------------

    [Fact]
    public async Task AnUnreachableHost_SkipsTheOtherEngineOnIt_AndLaterPressesGoStraightPast()
    {
        var rpc = new Fake("GoogleRpc", "google") { Mode = Behaviour.Unreachable };
        var web = new Fake("GoogleWeb", "google");
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([rpc, web, microsoft], time: new Clock());

        Assert.Equal("Microsoft", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
        Assert.Equal("Microsoft", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);

        Assert.Equal(1, rpc.Calls);
        Assert.Equal(0, web.Calls);
        Assert.Equal(2, microsoft.Calls);
    }

    [Fact]
    public async Task ATimeout_CountsAsUnreachable()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.TimedOut };
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([google, microsoft], time: new Clock());

        await synthesizer.SynthesizeAsync(Text, Ja);
        await synthesizer.SynthesizeAsync(Text, Ja);

        Assert.Equal(1, google.Calls);
    }

    [Fact]
    public async Task TheCooldownRunsOut_TheHostIsTriedAgain_AndEachFailureDoublesIt()
    {
        var clock = new Clock();
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")], time: clock);

        await synthesizer.SynthesizeAsync(Text, Ja);                     // 1st failure: 2 minutes
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal(1, google.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        await synthesizer.SynthesizeAsync(Text, Ja);                     // 2nd failure: 4 minutes
        Assert.Equal(2, google.Calls);

        clock.Advance(TimeSpan.FromMinutes(3));
        await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal(2, google.Calls);

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal(3, google.Calls);
    }

    [Fact]
    public async Task TheCooldownNeverExceedsHalfAnHour()
    {
        var clock = new Clock();
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")], time: clock);

        for (var i = 0; i < 10; i++)
        {
            await synthesizer.SynthesizeAsync(Text, Ja);
            clock.Advance(SpeechSynthesizer.LongestCooldown + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(10, google.Calls);
    }

    [Fact]
    public async Task AnAnswer_EndsTheCooldown_AndTheNextFailureStartsOverAtTwoMinutes()
    {
        var clock = new Clock();
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")], time: clock);

        await synthesizer.SynthesizeAsync(Text, Ja);
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);                     // 2nd failure: 4 minutes
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));

        google.Mode = Behaviour.Answers;
        Assert.Equal("Google", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);

        google.Mode = Behaviour.Unreachable;
        await synthesizer.SynthesizeAsync(Text, Ja);                     // first failure again
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);

        Assert.Equal(5, google.Calls);
    }

    // An error with a status came from the service: it is there, and the next press may succeed.
    [Fact]
    public async Task AnErrorFromTheService_DoesNotSkipTheHost()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.Refuses };
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([google, microsoft], time: new Clock());

        await synthesizer.SynthesizeAsync(Text, Ja);
        await synthesizer.SynthesizeAsync(Text, Ja);

        Assert.Equal(2, google.Calls);
    }

    [Fact]
    public async Task AnErrorFromTheService_StillTriesTheOtherEngineOnTheSameHost()
    {
        var rpc = new Fake("GoogleRpc", "google") { Mode = Behaviour.Refuses };
        var web = new Fake("GoogleWeb", "google");
        var synthesizer = new SpeechSynthesizer([rpc, web], time: new Clock());

        Assert.Equal("GoogleWeb", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
    }

    [Fact]
    public async Task EveryHostCoolingDown_IsStillAsked_RatherThanReadingNothing()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var microsoft = new Fake("Microsoft", "microsoft") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, microsoft], time: new Clock());

        await Assert.ThrowsAsync<TranslationEngineException>(() => synthesizer.SynthesizeAsync(Text, Ja));
        microsoft.Mode = Behaviour.Answers;

        Assert.Equal("Microsoft", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
        Assert.Equal(2, google.Calls);
    }

    // Many windows speak through one synthesizer at once.
    [Fact]
    public async Task PressesFromManyWindowsAtOnce_AgreeOnTheCooldown()
    {
        var clock = new Clock();
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")], time: clock);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => synthesizer.SynthesizeAsync(Text, Ja))));
        var calls = google.Calls;

        // However many of them failed at once, that was one failure: two minutes, not more.
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal(calls + 1, google.Calls);
    }

    // ---- A request that does not answer -----------------------------------------------------

    [Fact]
    public async Task AStuckRequest_GetsTheNextEngineBesideIt_AndTheFirstAnswerIsPlayed()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.Hangs };
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([google, microsoft], hedge: TimeSpan.FromMilliseconds(50));

        var started = Stopwatch.GetTimestamp();
        var (_, engine) = await synthesizer.SynthesizeAsync(Text, Ja);

        Assert.Equal("Microsoft", engine);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AStuckRequest_IsNeverJoinedByAnotherOnTheSameHost()
    {
        var rpc = new Fake("GoogleRpc", "google") { Mode = Behaviour.Hangs };
        var web = new Fake("GoogleWeb", "google");
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([rpc, web, microsoft], hedge: TimeSpan.FromMilliseconds(50));

        Assert.Equal("Microsoft", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
        Assert.Equal(0, web.Calls);
    }

    [Fact]
    public async Task AnAnswerQuickerThanTheHedge_AsksNobodyElse()
    {
        var google = new Fake("Google", "google") { Delay = TimeSpan.FromMilliseconds(20) };
        var microsoft = new Fake("Microsoft", "microsoft");
        var synthesizer = new SpeechSynthesizer([google, microsoft], hedge: TimeSpan.FromMilliseconds(500));

        Assert.Equal("Google", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
        Assert.Equal(0, microsoft.Calls);
    }

    // Left running behind the answer that was played: when it answers after all, its host is
    // reachable again — and it is not handed to anyone to play.
    [Fact]
    public async Task ARequestLeftBehind_ThatAnswersLate_EndsItsHostsCooldown()
    {
        var clock = new Clock();
        var google = new Fake("Google", "google") { Mode = Behaviour.Unreachable };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")],
            hedge: TimeSpan.FromMilliseconds(50), time: clock);

        await synthesizer.SynthesizeAsync(Text, Ja);                     // 1st failure: 2 minutes
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        google.Mode = Behaviour.Answers;
        google.Delay = TimeSpan.FromMilliseconds(300);
        var (_, engine) = await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal("Microsoft", engine);
        await google.Finished(2);

        // Had the late answer not counted, this failure would be the second in a row: 4 minutes.
        google.Mode = Behaviour.Unreachable;
        google.Delay = TimeSpan.Zero;
        await synthesizer.SynthesizeAsync(Text, Ja);
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await synthesizer.SynthesizeAsync(Text, Ja);
        Assert.Equal(4, google.Calls);
    }

    [Fact]
    public async Task StoppingAfterTheHedge_AbortsEveryRequest_AndCoolsNothing()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.Hangs };
        var microsoft = new Fake("Microsoft", "microsoft") { Mode = Behaviour.Hangs };
        var synthesizer = new SpeechSynthesizer([google, microsoft],
            hedge: TimeSpan.FromMilliseconds(50), time: new Clock());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synthesizer.SynthesizeAsync(Text, Ja, cts.Token));
        await google.Finished(1);
        await microsoft.Finished(1);
        Assert.Equal(1, google.Cancelled);
        Assert.Equal(1, microsoft.Cancelled);

        google.Mode = Behaviour.Answers;
        Assert.Equal("Google", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
    }

    // A press replaced by a newer one cancels what the old press left running, and that is not a
    // failure of its host either.
    [Fact]
    public async Task ARequestLeftBehind_ThatIsStoppedLater_CoolsNothing()
    {
        var google = new Fake("Google", "google") { Mode = Behaviour.Hangs };
        var synthesizer = new SpeechSynthesizer([google, new Fake("Microsoft", "microsoft")],
            hedge: TimeSpan.FromMilliseconds(50), time: new Clock());
        using var cts = new CancellationTokenSource();

        Assert.Equal("Microsoft", (await synthesizer.SynthesizeAsync(Text, Ja, cts.Token)).Engine);
        cts.Cancel();
        await google.Finished(1);

        google.Mode = Behaviour.Answers;
        Assert.Equal("Google", (await synthesizer.SynthesizeAsync(Text, Ja)).Engine);
    }

    // ---- What counts as no answer, on a real socket -----------------------------------------

    [Fact]
    public async Task AHostThatNeverHandshakes_FailsAtTheConnectTimeout_AsNoAnswer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try { while (true) held.Add(await listener.AcceptTcpClientAsync()); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        try
        {
            using var http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));
            var engine = new Fetch(http, $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

            var started = Stopwatch.GetTimestamp();
            var ex = await Assert.ThrowsAsync<TranslationEngineException>(() => engine.SynthesizeAsync(Text, Ja));

            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            Assert.True(SpeechSynthesizer.GotNoAnswer(ex), ex.ToString());
        }
        finally
        {
            listener.Stop();
            held.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public async Task ARefusedConnection_IsNoAnswer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<TranslationEngineException>(
            () => new Fetch(http, $"http://127.0.0.1:{port}/").SynthesizeAsync(Text, Ja));

        Assert.True(SpeechSynthesizer.GotNoAnswer(ex), ex.ToString());
    }

    [Fact]
    public async Task AStatus_IsAnAnswer()
    {
        using var http = new HttpClient(new Status(HttpStatusCode.TooManyRequests));
        var ex = await Assert.ThrowsAsync<TranslationEngineException>(
            () => new Fetch(http, "https://example.invalid/").SynthesizeAsync(Text, Ja));

        Assert.False(SpeechSynthesizer.GotNoAnswer(ex));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private enum Behaviour { Answers, Unreachable, TimedOut, Refuses, Hangs }

    /// <summary>An engine on a host, behaving as told; the failures look like SpeechEngine's.</summary>
    private sealed class Fake(string name, string host) : ISpeechEngine
    {
        private int _calls, _finished, _cancelled;

        public string Name => name;
        public string Host => host;
        public Behaviour Mode { get; set; }
        public TimeSpan Delay { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public bool Supports(string language) => true;

        public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var mode = Mode;
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                return mode switch
                {
                    Behaviour.Answers => [0xFF],
                    Behaviour.Unreachable => throw new TranslationEngineException(name, "request failed (ConnectionError)",
                        null, new HttpRequestException(HttpRequestError.ConnectionError, "refused")),
                    Behaviour.TimedOut => throw new TranslationEngineException(name, "timed out",
                        null, new TaskCanceledException("timeout")),
                    Behaviour.Refuses => throw new TranslationEngineException(name, "HTTP 429", HttpStatusCode.TooManyRequests),
                    _ => await Hang(cancellationToken),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                Interlocked.Increment(ref _finished);
            }
        }

        private static async Task<byte[]> Hang(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        /// <summary>Waits until <paramref name="count"/> requests have ended, however they ended.</summary>
        public async Task Finished(int count)
        {
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (Volatile.Read(ref _finished) < count && Stopwatch.GetTimestamp() < until) await Task.Delay(10);
            Assert.True(Volatile.Read(ref _finished) >= count, $"{name}: {_finished} of {count} requests ended");
            await Task.Delay(20);   // the synthesizer records the outcome in a continuation of its own
        }
    }

    /// <summary>A speech engine that GETs one address: SpeechEngine's sorting of failures, on a real client.</summary>
    private sealed class Fetch(HttpClient http, string url) : SpeechEngine(http)
    {
        public override string Name => "Fetch";
        public override string Host => new Uri(url).Host;
        public override bool Supports(string language) => true;

        protected override Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken) =>
            ReadBytesAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
    }

    private sealed class Status(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>A clock that only moves when told; timers still run on real time.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
