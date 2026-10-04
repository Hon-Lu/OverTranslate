using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// The identifier is written once and then read for the rest of the install's life, so what is
// pinned here is the shape agreed on and the judgement of what to do with a value that does not
// match it — the file is hand-editable, and every rejection here costs the user their history
// while every wrong acceptance puts a value that is not an identifier into a report.
public class AppIdentityTests
{
    [Fact]
    public void GeneratedIdentity_IsAcceptedBack()
    {
        Assert.True(AppIdentityService.IsWellFormed(AppIdentityService.Generate(DateTime.Now)));
    }

    [Fact]
    public void GeneratedIdentity_CarriesTheMomentItWasIssued()
    {
        var issued = new DateTime(2026, 8, 22, 12, 41, 3);

        Assert.StartsWith("20260822124103-", AppIdentityService.Generate(issued));
    }

    [Fact]
    public void TwoIdentities_AreNotTheSame()
    {
        var now = DateTime.Now;

        Assert.NotEqual(AppIdentityService.Generate(now), AppIdentityService.Generate(now));
    }

    [Theory]
    // The state every install predating this field is in.
    [InlineData("")]
    // A GUID with no timestamp, and a timestamp with no GUID: each half alone is not the value.
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("20260822124103")]
    // Fourteen digits that are not a date. Pattern-matching the shape would let this through.
    [InlineData("20261332994103-3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    // A GUID in the other formats .NET will happily parse, neither of which is what is written.
    [InlineData("20260822124103-3f2504e04f8941d39a0c0305e82c3301")]
    [InlineData("20260822124103-{3f2504e0-4f89-41d3-9a0c-0305e82c3301}")]
    // Whatever a half-written file or a hand edit leaves behind.
    [InlineData("not-an-identifier")]
    [InlineData("20260822124103-3f2504e0-4f89-41d3-9a0c-0305e82c33")]
    public void AnythingElse_IsReplacedRatherThanKept(string stored)
    {
        Assert.False(AppIdentityService.IsWellFormed(stored));
    }

    [Fact]
    public void NoValueAtAll_IsReplacedRatherThanKept()
    {
        Assert.False(AppIdentityService.IsWellFormed(null));
    }
}
