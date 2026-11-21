using System.Net.Http;
using OverTranslate.Translation;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// <see cref="EngineHosts"/>: which host requests go to, moving every request on together when it
/// cannot be reached, and what is kept for the next run.
/// </summary>
public class EngineHostsTests
{
    private static readonly string[] Hosts = ["first.example", "second.example", "third.example"];

    private static HttpRequestException Unreachable() =>
        new(HttpRequestError.SecureConnectionError, "unreachable");

    [Fact]
    public async Task AnUnreachableHost_MovesEveryRequestOn_AndTheOneThatAnsweredIsSaved()
    {
        var saved = new List<string>();
        var hosts = new EngineHosts("Test", Hosts, saved: null, save: saved.Add);
        var tried = new List<string>();

        var first = await hosts.SendAsync(host =>
        {
            tried.Add(host);
            return host == "first.example" ? throw Unreachable() : Task.FromResult(host);
        }, CancellationToken.None);
        var second = await hosts.SendAsync(host => { tried.Add(host); return Task.FromResult(host); }, CancellationToken.None);

        Assert.Equal(("second.example", "second.example"), (first, second));
        Assert.Equal(["first.example", "second.example", "second.example"], tried);
        Assert.Equal(["second.example"], saved);
        Assert.Equal("second.example", hosts.Current);
    }

    // Requests already on their way to the failing host fail with it and follow the first one on,
    // one host along, not one each; the ones sent after go straight to where it moved.
    [Fact]
    public async Task RequestsInFlightFollowTheFirstFailure_AndLaterOnesNeverTryTheFailingHost()
    {
        var hosts = new EngineHosts("Test", Hosts);
        var release = new TaskCompletionSource();
        var tried = new List<string>();

        async Task<string> Send(string host)
        {
            lock (tried) tried.Add(host);
            if (host != "first.example") return host;
            await release.Task;
            throw Unreachable();
        }

        var inFlight = Enumerable.Range(0, 3).Select(_ => hosts.SendAsync(Send, CancellationToken.None)).ToList();
        release.SetResult();
        var answers = await Task.WhenAll(inFlight);
        var later = await hosts.SendAsync(Send, CancellationToken.None);

        Assert.All(answers, answer => Assert.Equal("second.example", answer));
        Assert.Equal("second.example", later);
        Assert.Equal(3, tried.Count(host => host == "first.example"));
        Assert.Equal(4, tried.Count(host => host == "second.example"));
        Assert.DoesNotContain("third.example", tried);
    }

    [Fact]
    public async Task WhenNoHostAnswers_TheLastFailureIsThrown_AndTheNextRequestTriesThemAgain()
    {
        var hosts = new EngineHosts("Test", Hosts);
        var reachable = false;
        var tried = new List<string>();

        Task<string> Send(string host)
        {
            tried.Add(host);
            return reachable && host == "first.example" ? Task.FromResult(host) : throw Unreachable();
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => hosts.SendAsync(Send, CancellationToken.None));
        reachable = true;
        var answer = await hosts.SendAsync(Send, CancellationToken.None);

        Assert.Equal("first.example", answer);
        Assert.Equal(["first.example", "second.example", "third.example", "third.example", "first.example"], tried);
    }

    [Fact]
    public async Task ARunStartsAtTheSavedHost_AndIgnoresOneItDoesNotKnow()
    {
        var known = new EngineHosts("Test", Hosts, saved: "third.example");
        var unknown = new EngineHosts("Test", Hosts, saved: "retired.example");

        Assert.Equal("third.example", await known.SendAsync(Task.FromResult, CancellationToken.None));
        Assert.Equal("first.example", await unknown.SendAsync(Task.FromResult, CancellationToken.None));
    }

    [Fact]
    public async Task NothingIsSaved_WhileTheHostARunStartedAtKeepsAnswering()
    {
        var saved = new List<string>();
        var hosts = new EngineHosts("Test", Hosts, saved: "second.example", save: saved.Add);

        await hosts.SendAsync(Task.FromResult, CancellationToken.None);
        await hosts.SendAsync(Task.FromResult, CancellationToken.None);

        Assert.Empty(saved);
    }

    // Anything but an unreachable host is the service's own answer, and every host is the same service.
    [Fact]
    public async Task AFailureThatIsNotAnUnreachableHost_IsNotTriedElsewhere()
    {
        var hosts = new EngineHosts("Test", Hosts);
        var tried = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.SendAsync<string>(host =>
        {
            tried.Add(host);
            throw new InvalidOperationException();
        }, CancellationToken.None));

        Assert.Equal(["first.example"], tried);
        Assert.Equal("first.example", hosts.Current);
    }

    [Fact]
    public async Task AHostThatCannotBeSaved_StillAnswers()
    {
        var hosts = new EngineHosts("Test", Hosts, save: _ => throw new IOException("disk full"));

        var answer = await hosts.SendAsync(
            host => host == "first.example" ? throw Unreachable() : Task.FromResult(host), CancellationToken.None);

        Assert.Equal("second.example", answer);
    }
}
