using System.Net;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// The upload endpoint is open to anything on the internet, so the response it returns cannot be
// trusted to be the response the worker would have returned. What is pinned here is the pair of
// decisions that follow from that: what counts as a code worth showing the user, and which failures
// they get told apart. Everything else about the upload needs a network and belongs in a manual
// pass.
public class DiagnosticUploadTests
{
    [Fact]
    public void WellFormedCode_IsAccepted()
    {
        Assert.Equal("A3F-7K2", DiagnosticUploadService.ParseCode("""{"code":"A3F-7K2"}"""));
    }

    [Theory]
    // A captive portal answering 200 with its own page, and a proxy answering with an error object:
    // both are "success" as far as HTTP is concerned, and neither carries a code.
    [InlineData("<html><body>Sign in to continue</body></html>")]
    [InlineData("""{"error":"rate_limited"}""")]
    [InlineData("")]
    public void ResponseWithoutACode_IsRejectedRatherThanShown(string body)
    {
        Assert.Null(DiagnosticUploadService.ParseCode(body));
    }

    [Theory]
    // The shape is fixed on purpose: six characters, a dash in the middle, and only from the
    // alphabet the worker draws on. A user is told to copy this into a public post, so a string
    // that is not a code has to fail here rather than be pasted somewhere as though it were one.
    [InlineData("A3F7K2")]            // no separator
    [InlineData("A3F-7K")]            // too short
    [InlineData("A3F-7K2X")]          // too long
    [InlineData("a3f-7k2")]           // lower case
    [InlineData("A3I-7K2")]           // I, L, O and U are excluded to keep it readable aloud
    [InlineData("A3F-7K2 or click here")]
    public void MisshapenCode_IsRejected(string code)
    {
        Assert.Null(DiagnosticUploadService.ParseCode($$"""{"code":"{{code}}"}"""));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, DiagnosticUploadFailure.TooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, DiagnosticUploadFailure.RateLimited)]
    // Everything else collapses into one line, because the difference between a 500 and a 415
    // changes nothing the user can do — and what they are told to do next, attach the file that is
    // still on their disk, is the same either way.
    [InlineData(HttpStatusCode.InternalServerError, DiagnosticUploadFailure.Rejected)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, DiagnosticUploadFailure.Rejected)]
    [InlineData(HttpStatusCode.NotFound, DiagnosticUploadFailure.Rejected)]
    public void RefusalIsTranslatedIntoSomethingTheUserCanActOn(
        HttpStatusCode status, DiagnosticUploadFailure expected)
    {
        Assert.Equal(expected, DiagnosticUploadService.Classify(status));
    }

    [Theory]
    // "off" is the documented way to switch uploading off. The other two are what a mistyped or
    // half-edited endpoint looks like, and they have to fail the same way: an address that is not an
    // address must turn the feature off, never send a log full of someone's screen to whatever it
    // resolves to.
    [InlineData("off")]
    [InlineData("overtranslate-diag.example.workers.dev/v1/bundle")]
    [InlineData("ftp://example.com/v1/bundle")]
    public async Task WithNoUsableEndpoint_NothingIsSentAndTheCallerIsToldWhy(string endpoint)
    {
        var saved = Environment.GetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT");
        Environment.SetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT", endpoint);
        try
        {
            Assert.False(DiagnosticUploadService.IsConfigured);

            // A path that does not exist: reaching a FileNotFoundException would mean the endpoint
            // check happens after the file is opened, and by then a build with no endpoint would be
            // one line away from posting to an empty address.
            var ex = await Assert.ThrowsAsync<DiagnosticUploadException>(
                () => DiagnosticUploadService.UploadAsync("C:/nowhere/no-such-bundle.zip"));

            Assert.Equal(DiagnosticUploadFailure.NotConfigured, ex.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT", saved);
        }
    }

    [Fact]
    public void TheEndpointCompiledIn_IsAnAddressThatCanBeUploadedTo()
    {
        var saved = Environment.GetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT");
        Environment.SetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT", null);
        try
        {
            // Not that it answers — that needs a network and belongs in a manual pass — but that a
            // shipped build would try. A typo here turns the whole feature off silently.
            Assert.True(DiagnosticUploadService.IsConfigured);
            Assert.StartsWith("https://", DiagnosticUploadService.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OVERTRANSLATE_DIAG_ENDPOINT", saved);
        }
    }
}
