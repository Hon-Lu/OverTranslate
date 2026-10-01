using System.IO;
using System.Text.Json.Nodes;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// The settings file goes into a zip the user is expected to hand to a stranger — a forum thread, an
// issue, eventually an upload. A credential that survives that trip is the one mistake this feature
// can make that the user cannot undo, so the rule that decides what gets hidden is pinned here
// rather than left to be re-read out of the implementation.
public class DiagnosticRedactionTests
{
    [Fact]
    public void ApiKeys_AreReplacedByTheirLength()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"ApiKey":"secret-value","OpenAiApiKey":"sk-0123456789"}""");

        var obj = JsonNode.Parse(json)!.AsObject();

        // The length rather than a blank: whether a key is set at all, and whether it is the length
        // a key of that kind should be, answers a good share of the reports this file is collected
        // for — and neither question can be asked of an empty string.
        Assert.Equal("<redacted:12>", (string?)obj["ApiKey"]);
        Assert.Equal("<redacted:13>", (string?)obj["OpenAiApiKey"]);
    }

    [Fact]
    public void GoogleChromeKey_IsRedactedLikeTheUsersOwn()
    {
        // Put together at run time: written out whole, it is a key GitHub's secret scanning reports.
        var key = "AI" + "za" + new string('0', 35);
        var json = DiagnosticBundleService.RedactSettings($$"""{"GoogleChromeApiKey":"{{key}}"}""");

        // Not the user's, and Google's to begin with, but a key in a bundle is still a key handed to
        // whoever reads it.
        Assert.Equal("<redacted:39>", (string?)JsonNode.Parse(json)!.AsObject()["GoogleChromeApiKey"]);
    }

    [Fact]
    public void UnsetKey_StaysEmptyRatherThanLookingSet()
    {
        var json = DiagnosticBundleService.RedactSettings("""{"ApiKey":""}""");

        // "<redacted:0>" would say a key was hidden when there was none, and send whoever reads the
        // bundle looking for an authentication problem that cannot exist.
        Assert.Equal("", (string?)JsonNode.Parse(json)!.AsObject()["ApiKey"]);
    }

    [Fact]
    public void HotkeyFields_SurviveRedaction()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"HotkeyVirtualKey":65,"HotkeyDisplay":"Ctrl+Alt+A"}""");

        var obj = JsonNode.Parse(json)!.AsObject();

        // The reason the rule matches "ApiKey" and not "Key". Which key a shortcut is bound to is
        // one of the more useful things in this file, and every hotkey field ends in Key.
        Assert.Equal(65, (int?)obj["HotkeyVirtualKey"]);
        Assert.Equal("Ctrl+Alt+A", (string?)obj["HotkeyDisplay"]);
    }

    [Fact]
    public void NestedRealtimeSettings_AreRedactedToo()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"Realtime":{"OpenAiApiKey":"sk-abcdef","TargetLanguage":"JA"}}""");

        var realtime = JsonNode.Parse(json)!.AsObject()["Realtime"]!.AsObject();

        // Realtime keeps its own copy of the provider settings, so a walk that only looked at the
        // top level would leak the key from the half of the app that has two of them.
        Assert.Equal("<redacted:9>", (string?)realtime["OpenAiApiKey"]);
        Assert.Equal("JA", (string?)realtime["TargetLanguage"]);
    }

    [Fact]
    public void BaseUrl_KeepsTheAddressAndDropsTheCredentials()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"OpenAiBaseUrl":"https://user:pw@example.com/v1?token=abc"}""");

        var url = (string?)JsonNode.Parse(json)!.AsObject()["OpenAiBaseUrl"];

        // The host is most of the diagnosis when an OpenAI-compatible provider misbehaves, so it
        // stays; the user info and query string are where self-hosted front ends put tokens.
        Assert.NotNull(url);
        Assert.Contains("example.com/v1", url);
        Assert.DoesNotContain("pw", url);
        Assert.DoesNotContain("abc", url);
    }

    [Fact]
    public void PlainBaseUrl_IsLeftAlone()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"OpenAiBaseUrl":"http://localhost:11434/v1"}""");

        // Pointed at a local model or at a hosted one is the first thing to establish, and rewriting
        // an address that had nothing to hide only invites doubt about what else was rewritten.
        Assert.Equal(
            "http://localhost:11434/v1",
            (string?)JsonNode.Parse(json)!.AsObject()["OpenAiBaseUrl"]);
    }

    [Fact]
    public void FileThatIsNotJson_IsReturnedUnchanged()
    {
        const string broken = "{ this was truncated by a power cut";

        // A file in this shape cannot hold a key in a field we would recognise, and its shape is
        // itself the bug being reported — so it goes into the bundle exactly as found.
        Assert.Equal(broken, DiagnosticBundleService.RedactSettings(broken));
    }

    // The settings file is not the only thing in the bundle that carried something the user did not
    // mean to hand over. environment.txt named four folders, and on most machines the account those
    // folders belong to is the person's actual name.
    [Fact]
    public void UserFolders_AreNamedByTheirVariableRatherThanTheAccount()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var collapsed = DiagnosticBundleService.CollapseUserPaths(
            Path.Combine(appData, "OverTranslate", "logs"));

        Assert.StartsWith("%APPDATA%", collapsed);
        Assert.EndsWith(Path.Combine("OverTranslate", "logs"), collapsed);
        Assert.DoesNotContain(Environment.UserName, collapsed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalAppData_IsNotMistakenForTheProfileRoot()
    {
        // Both roots are under the profile. Matching the shortest one first would turn every path
        // into %USERPROFILE%\AppData\..., which puts the folder layout back in the wrong terms.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(
            "%LOCALAPPDATA%",
            DiagnosticBundleService.CollapseUserPaths(Path.Combine(local, "OverTranslate")));
    }

    [Fact]
    public void PathOutsideTheProfile_IsLeftAlone()
    {
        // Being somewhere unexpected is the condition worth seeing — a log redirected onto another
        // drive is exactly the environment whose log we would otherwise fail to explain. And a path
        // that was never under the account's folder has no account name in it to hide.
        const string elsewhere = @"D:	ools\OverTranslate\logs";

        Assert.Equal(elsewhere, DiagnosticBundleService.CollapseUserPaths(elsewhere));
    }

    // The exported zips are kept for the same thirty days the uploaded copy gets, so that the
    // interface can say "30 days" once without having to explain which copy it means. That makes
    // this a deletion loop running inside the user's own data folder, which is worth pinning down.
    [Fact]
    public void ExportsPastTheirThirtyDays_AreDeleted()
    {
        var directory = NewTempFolder();
        try
        {
            var old = WriteBundle(directory, "OverTranslate-diagnostics-20250101-120000.zip",
                DateTime.UtcNow - TimeSpan.FromDays(31));
            var fresh = WriteBundle(directory, "OverTranslate-diagnostics-20260101-120000.zip",
                DateTime.UtcNow - TimeSpan.FromDays(29));

            Assert.Equal(1, DiagnosticBundleService.PruneOldExports(directory, DateTime.UtcNow));

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FilesTheExportDidNotWrite_AreLeftAloneHoweverOld()
    {
        var directory = NewTempFolder();
        try
        {
            // Someone who renamed a bundle to remember which one it was, and someone who kept a note
            // beside it, both put those there on purpose. A cleanup that takes files it did not
            // write is a bug that destroys data, so the name it wrote is the only licence it has.
            var renamed = WriteBundle(directory, "the one with the crash.zip", DateTime.UtcNow.AddYears(-2));
            var note = WriteBundle(directory, "notes.txt", DateTime.UtcNow.AddYears(-2));

            Assert.Equal(0, DiagnosticBundleService.PruneOldExports(directory, DateTime.UtcNow));

            Assert.True(File.Exists(renamed));
            Assert.True(File.Exists(note));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AFolderThatWasNeverExportedTo_IsNotAnError()
    {
        // The first press on a fresh machine prunes before it writes, so this runs against a folder
        // that does not exist yet every single time.
        Assert.Equal(0, DiagnosticBundleService.PruneOldExports(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), DateTime.UtcNow));
    }

    private static string NewTempFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ot-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteBundle(string directory, string name, DateTime writtenUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "not really a zip");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
