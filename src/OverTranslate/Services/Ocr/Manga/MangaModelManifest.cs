using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// The manga model files this build reads, where they are downloaded from, and what they must hash to.
/// </summary>
/// <remarks>
/// <para>Shipped next to the app as <c>ocrmodels/manga-vertical.json</c> rather than compiled in, so
/// that where the files are hosted can change without touching code. The version belongs to the
/// build: the files' inputs and outputs are what <see cref="MangaTextDetector"/> and
/// <see cref="MangaTextRecognizer"/> are written against, so a build only ever reads the version it
/// shipped with, and a new version is a new download.</para>
///
/// <para><c>OVERTRANSLATE_MANGA_MODELS_URL</c> replaces the base URL, for trying a download against a
/// local server before the files are published.</para>
/// </remarks>
internal sealed record MangaModelManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("files")] IReadOnlyList<MangaModelFile> Files)
{
    internal const string BaseUrlOverride = "OVERTRANSLATE_MANGA_MODELS_URL";

    internal static string ShippedPath =>
        Path.Combine(AppContext.BaseDirectory, "ocrmodels", "manga-vertical.json");

    internal long TotalBytes => Files.Sum(file => file.Size);

    internal static MangaModelManifest Load(string path)
    {
        var manifest = Parse(System.IO.File.ReadAllText(path));
        return Environment.GetEnvironmentVariable(BaseUrlOverride) is { Length: > 0 } url
            ? manifest with { BaseUrl = url }
            : manifest;
    }

    internal static MangaModelManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<MangaModelManifest>(json)
                       ?? throw new InvalidDataException("empty manga model manifest");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is not { Count: > 0 })
            throw new InvalidDataException("manga model manifest has no version or no files");
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Name) || file.Name.IndexOfAny(['/', '\\', ':']) >= 0 ||
                file.Size <= 0 || file.Sha256 is not { Length: 64 })
                throw new InvalidDataException($"manga model manifest entry is malformed: {file.Name}");
        }

        return manifest;
    }

    internal Uri UrlOf(MangaModelFile file) =>
        new(new Uri(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/"), Uri.EscapeDataString(file.Name));

    internal MangaModelFile File(string role) =>
        Files.FirstOrDefault(file => file.Role == role)
        ?? throw new InvalidDataException($"manga model manifest has no {role} file");
}

/// <param name="Role">What the file is to the pipeline: detector, encoder, decoder-cross, decoder-step, vocabulary.</param>
internal sealed record MangaModelFile(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);
