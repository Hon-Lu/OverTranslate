using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NLog;
using Velopack;

namespace OverTranslate.Services;

/// <summary>Fetches the notes the update window shows from the repository's GitHub releases.</summary>
/// <remarks>
/// Nothing here throws: the notes are an addition to the window, and a failure to fetch them must
/// never stand between the user and the update. Every failure reads as null, which the window answers
/// with the link it had before.
/// </remarks>
public static class ReleaseNotesService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Enough to reach well past <see cref="ReleaseNotesSelection.MaxShown"/> stable releases with the
    /// betas between them, without pulling the whole history on every update.
    /// </summary>
    private const int PageSize = 30;

    private static readonly HttpClient Http = CreateClient();

    // Failures are not kept, so the next opening of the window tries again.
    private static readonly ConcurrentDictionary<string, Task<ReleaseNotesFeed?>> Requests = new();

    /// <summary>
    /// The releases an update takes in, or null when there are none to show. Null for the beta channel
    /// and for a pre-release target: these notes are written for people on stable releases.
    /// </summary>
    public static Task<ReleaseNotesFeed?> GetForUpdateAsync(UpdateInfo info)
    {
        var source = UpdateService.ResolveFeedSource();
        var target = info.VelopackInfo.TargetFullRelease.Version;

        if (source.IsBeta || target.IsPrerelease)
            return Task.FromResult<ReleaseNotesFeed?>(null);

        return Cached($"update:{target}", async () =>
        {
            var listing = await FetchListingAsync(source);
            return listing is null
                ? null
                : ReleaseNotesSelection.ForUpdate(
                    listing, UpdateService.InstalledVersion(), target, source.SeesPrerelease, PageSize);
        });
    }

    /// <summary>The releases up to the installed one, or null when they cannot be fetched.</summary>
    public static Task<ReleaseNotesFeed?> GetHistoryAsync()
    {
        var source = UpdateService.ResolveFeedSource();
        var current = UpdateService.InstalledVersion();

        return Cached($"history:{current}", async () =>
        {
            var listing = await FetchListingAsync(source);
            return listing is null ? null : ReleaseNotesSelection.History(listing, current);
        });
    }

    /// <summary>Waits for <paramref name="request"/> for up to <paramref name="patience"/>; null past that.</summary>
    public static async Task<ReleaseNotesFeed?> WithinAsync(Task<ReleaseNotesFeed?> request, TimeSpan patience)
    {
        var finished = await Task.WhenAny(request, Task.Delay(patience));
        return finished == request ? await request : null;
    }

    private static Task<ReleaseNotesFeed?> Cached(string key, Func<Task<ReleaseNotesFeed?>> load)
    {
        var request = Requests.GetOrAdd(key, _ => load());

        if (request.IsCompleted && request.Result is null)
        {
            // Compared rather than overwritten, so two callers cannot each throw away the other's retry.
            var retry = load();
            return Requests.TryUpdate(key, retry, request) ? retry : Requests[key];
        }

        return request;
    }

    private static async Task<IReadOnlyList<ReleaseRecord>?> FetchListingAsync(UpdateFeedSource source)
    {
        var url = ListingUrl(source.RepositoryUrl);
        if (url is null)
        {
            Log.Warn("Cannot read a GitHub repository from '{0}'", source.RepositoryUrl);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(source.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // A 403 here is usually GitHub's unauthenticated limit of 60 requests an hour.
                Log.Warn("Fetching release notes returned {0}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return ParseListing(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn(ex, "Fetching release notes failed");
            return null;
        }
    }

    /// <summary>GitHub's release listing for a repository's address, or null when it is not one.</summary>
    public static string? ListingUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0)
            ? $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases?per_page={PageSize}"
            : null;
    }

    public static IReadOnlyList<ReleaseRecord> ParseListing(JsonElement root)
    {
        var records = new List<ReleaseRecord>();
        if (root.ValueKind != JsonValueKind.Array) return records;

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            records.Add(new ReleaseRecord(
                Text(item, "tag_name"),
                Flag(item, "draft"),
                Flag(item, "prerelease"),
                DateTimeOffset.TryParse(Text(item, "published_at"), out var published) ? published : null,
                Text(item, "body"),
                Text(item, "html_url")));
        }

        return records;

        static string Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        static bool Flag(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        // GitHub refuses a request without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OverTranslate");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
