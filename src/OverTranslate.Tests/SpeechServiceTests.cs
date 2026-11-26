using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using Xunit;

namespace OverTranslate.Tests;

// #436: only the service the settings name reads aloud; a failure is said as that service's, told
// apart as unreachable, no voice for the language, or anything else.
public class SpeechServiceTests
{
    private const string Text = "こんにちは";
    private const string Ja = "ja";

    // ---- Which service ----------------------------------------------------------------------

    [Theory]
    [InlineData(TranslationProvider.Google, SpeechProvider.Google)]
    [InlineData(TranslationProvider.Google2, SpeechProvider.Google)]
    [InlineData(TranslationProvider.GoogleChrome, SpeechProvider.Google)]
    [InlineData(TranslationProvider.Microsoft, SpeechProvider.Microsoft)]
    [InlineData(TranslationProvider.Bing, SpeechProvider.Bing)]
    public void Following_ATranslationServiceWithAVoice_ReadsWithIt(TranslationProvider translation, SpeechProvider expected)
    {
        var other = expected == SpeechProvider.Bing ? SpeechProvider.Microsoft : SpeechProvider.Bing;

        Assert.Equal(expected, SpeechProviders.Resolve(translation, follow: true, chosen: other));
    }

    [Theory]
    [InlineData(TranslationProvider.DeepL)]
    [InlineData(TranslationProvider.OpenAI)]
    [InlineData(TranslationProvider.Youdao)]
    [InlineData(TranslationProvider.TranSmart)]
    [InlineData(TranslationProvider.Yandex)]
    [InlineData(TranslationProvider.LocalAI)]
    public void Following_ATranslationServiceWithNoVoice_ReadsWithThePickedOne(TranslationProvider translation)
    {
        Assert.Equal(SpeechProvider.Bing, SpeechProviders.Resolve(translation, follow: true, chosen: SpeechProvider.Bing));
        Assert.Equal(SpeechProvider.Google, SpeechProviders.Resolve(translation, follow: true, chosen: SpeechProvider.Google));
    }

    [Fact]
    public void NotFollowing_ReadsWithThePickedOne_WhateverTranslates()
    {
        Assert.Equal(SpeechProvider.Microsoft,
            SpeechProviders.Resolve(TranslationProvider.Google, follow: false, chosen: SpeechProvider.Microsoft));
        Assert.Equal(SpeechProvider.Google,
            SpeechProviders.Resolve(TranslationProvider.Bing, follow: false, chosen: SpeechProvider.Google));
    }

    // A file from before #436 has neither key: Google, followed by the translation service.
    [Fact]
    public void AnOlderSettingsFile_GetsGoogle_AndFollowsTheTranslationService()
    {
        var settings = SettingsService.Parse("""{ "Provider": "Microsoft" }""");

        Assert.Equal(SpeechProvider.Google, settings.SpeechProvider);
        Assert.True(settings.SpeechFollowsProvider);
        Assert.Equal(SpeechProvider.Microsoft, SpeechProviders.Resolve(settings));
    }

    [Fact]
    public void TheChoice_IsReadBackByName()
    {
        var settings = SettingsService.Parse("""{ "Provider": "DeepL", "SpeechProvider": "Bing", "SpeechFollowsProvider": false }""");

        Assert.Equal(SpeechProvider.Bing, settings.SpeechProvider);
        Assert.False(settings.SpeechFollowsProvider);
    }

    // ---- One service's engines --------------------------------------------------------------

    [Fact]
    public async Task TheFirstEngineAnswers_TheSecondIsNotAsked()
    {
        var rpc = new Fake("Rpc");
        var web = new Fake("Web");

        var (_, engine) = await Google(rpc, web).SynthesizeAsync(Text, Ja);

        Assert.Equal("Rpc", engine);
        Assert.Equal(0, web.Calls);
    }

    [Fact]
    public async Task NoConnection_DoesNotTryTheOtherEngine_AndIsUnreachable()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.Unreachable };
        var web = new Fake("Web");

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(() => Google(rpc, web).SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unreachable, ex.Reason);
        Assert.Equal("Google", ex.Service);
        Assert.Equal(0, web.Calls);
    }

    [Fact]
    public async Task NoAnswerInTime_DoesNotTryTheOtherEngine_AndIsTheServicesTrouble()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.TimedOut };
        var web = new Fake("Web");

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(() => Google(rpc, web).SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unavailable, ex.Reason);
        Assert.Equal(0, web.Calls);
    }

    // The RPC call's shape changing is an answer from Google; its other address may still read.
    [Fact]
    public async Task AStatus_TriesTheOtherEngine()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.Refuses };
        var web = new Fake("Web");

        var (_, engine) = await Google(rpc, web).SynthesizeAsync(Text, Ja);

        Assert.Equal("Web", engine);
        Assert.Equal(1, rpc.Calls);
    }

    [Fact]
    public async Task AnUnusableAnswer_TriesTheOtherEngine()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.Unreadable };
        var web = new Fake("Web");

        var (_, engine) = await Google(rpc, web).SynthesizeAsync(Text, Ja);

        Assert.Equal("Web", engine);
    }

    [Fact]
    public async Task BothRefusing_IsTheServicesTrouble()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.Refuses };
        var web = new Fake("Web") { Mode = Behaviour.Refuses };

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(() => Google(rpc, web).SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unavailable, ex.Reason);
        Assert.Equal(1, web.Calls);
    }

    [Fact]
    public async Task ALanguageTheServiceHasNoVoiceFor_IsUnsupported_AndNothingIsSent()
    {
        var rpc = new Fake("Rpc");
        var web = new Fake("Web");

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(() => Google(rpc, web).SynthesizeAsync("Dober dan", "sl"));

        Assert.Equal(SpeechFailure.Unsupported, ex.Reason);
        Assert.Equal(0, rpc.Calls + web.Calls);
    }

    // Stopping, or a newer press, aborts the download and is no failure to report.
    [Fact]
    public async Task Cancelled_AbortsTheRequest_AndAsksNobodyElse()
    {
        var rpc = new Fake("Rpc") { Mode = Behaviour.Hangs };
        var web = new Fake("Web");
        using var cts = new CancellationTokenSource();

        var press = Google(rpc, web).SynthesizeAsync(Text, Ja, cts.Token);
        await rpc.Started();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => press);
        Assert.Equal(1, rpc.Cancelled);
        Assert.Equal(0, web.Calls);
    }

    // ---- The failures, on a real socket -----------------------------------------------------

    [Fact]
    public async Task AHostThatNeverHandshakes_FailsAtTheConnectTimeout_AsUnreachable()
    {
        using var server = new SilentServer();
        using var http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));

        var started = Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<SpeechFailedException>(
            () => Over(http, $"https://127.0.0.1:{server.Port}/").SynthesizeAsync(Text, Ja));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.Equal(SpeechFailure.Unreachable, ex.Reason);
    }

    [Fact]
    public async Task ARefusedConnection_IsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<SpeechFailedException>(
            () => Over(http, $"http://127.0.0.1:{port}/").SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unreachable, ex.Reason);
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsUnreachable()
    {
        using var http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<SpeechFailedException>(
            () => Over(http, "http://speech.invalid/").SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unreachable, ex.Reason);
    }

    // Connected, the text sent, and nothing back before the request timeout: the service's trouble.
    [Fact]
    public async Task AConnectionThatNeverAnswers_RunsOutTheRequestTimeout_AsUnavailable()
    {
        using var server = new SilentServer();
        using var http = EngineHttp.CreateClient(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<SpeechFailedException>(
            () => Over(http, $"http://127.0.0.1:{server.Port}/").SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unavailable, ex.Reason);
    }

    [Fact]
    public async Task AStatus_IsUnavailable()
    {
        using var http = new HttpClient(new Status(HttpStatusCode.TooManyRequests));
        var ex = await Assert.ThrowsAsync<SpeechFailedException>(
            () => Over(http, "https://example.invalid/").SynthesizeAsync(Text, Ja));

        Assert.Equal(SpeechFailure.Unavailable, ex.Reason);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static SpeechSynthesizer Google(params ISpeechEngine[] engines) => new("Google", engines);

    private static SpeechSynthesizer Over(HttpClient http, string url) => new("Test", [new Fetch(http, url)]);

    private enum Behaviour { Answers, Unreachable, TimedOut, Refuses, Unreadable, Hangs }

    /// <summary>A Japanese-only engine behaving as told; the failures look like SpeechEngine's.</summary>
    private sealed class Fake(string name) : ISpeechEngine
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls, _cancelled;

        public string Name => name;
        public Behaviour Mode { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public bool Supports(string language) => language == Ja;

        public Task Started() => _started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            try
            {
                return Mode switch
                {
                    Behaviour.Answers => [0xFF],
                    Behaviour.Unreachable => throw new TranslationEngineException(name, "request failed (ConnectionError)",
                        null, new HttpRequestException(HttpRequestError.ConnectionError, "refused")),
                    // The shape HttpClient.Timeout gives: the request under way, cancelled.
                    Behaviour.TimedOut => throw new TranslationEngineException(name, "timed out",
                        null, new TaskCanceledException("timeout", new TimeoutException("timeout", new TaskCanceledException()))),
                    Behaviour.Refuses => throw new TranslationEngineException(name, "HTTP 429", HttpStatusCode.TooManyRequests),
                    Behaviour.Unreadable => throw new TranslationEngineException(name, "call refused"),
                    _ => await Hang(cancellationToken),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }

        private static async Task<byte[]> Hang(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }

    /// <summary>A speech engine that GETs one address: SpeechEngine's sorting of failures, on a real client.</summary>
    private sealed class Fetch(HttpClient http, string url) : SpeechEngine(http)
    {
        public override string Name => "Fetch";
        public override bool Supports(string language) => true;

        protected override Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken) =>
            ReadBytesAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
    }

    private sealed class Status(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>Accepts connections and says nothing on them: no handshake, no response.</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];

        public SilentServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        lock (_held) _held.Add(client);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _listener.Stop();
            lock (_held) _held.ForEach(client => client.Dispose());
        }
    }
}
