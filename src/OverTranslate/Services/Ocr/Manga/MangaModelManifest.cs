using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// The manga model files this build reads, where they can be downloaded from, and what they must
/// hash to.
/// </summary>
/// <remarks>
/// <para>Shipped next to the app as <c>ocrmodels/manga-vertical.json</c> rather than compiled in, so
/// that where the files are hosted can change without touching code. The version belongs to the
/// build: the files' inputs and outputs are what <see cref="MangaTextDetector"/> and
/// <see cref="MangaTextRecognizer"/> are written against, so a build only ever reads the version it
/// shipped with, and a new version is a new download.</para>
///
/// <para>A file can come from several places, tried in the order <c>sources</c> lists them (see
/// <see cref="MangaModelStore"/> for when it moves on). Each source is a URL with <c>{name}</c> where
/// the file name goes, and optionally the files it has — the models live in separate repositories,
/// so one host can take more than one entry. A source with no list has every file.</para>
///
/// <para><c>OVERTRANSLATE_MANGA_MODELS_URL</c> replaces every source with the one it names, for trying
/// a download against a local server before the files are published: a URL with <c>{name}</c> in it,
/// or a base URL the file name is appended to. Nothing else is tried while it is set.</para>
/// </remarks>
internal sealed record MangaModelManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sources")] IReadOnlyList<MangaModelSource> Sources,
    [property: JsonPropertyName("files")] IReadOnlyList<MangaModelFile> Files)
{
    internal const string SourceOverride = "OVERTRANSLATE_MANGA_MODELS_URL";
    private const string NamePlaceholder = "{name}";

    internal static string ShippedPath =>
        Path.Combine(AppContext.BaseDirectory, "ocrmodels", "manga-vertical.json");

    internal long TotalBytes => Files.Sum(file => file.Size);

    internal static MangaModelManifest Load(string path)
    {
        var manifest = Parse(System.IO.File.ReadAllText(path));
        return Environment.GetEnvironmentVariable(SourceOverride) is { Length: > 0 } url
            ? manifest.WithOnlySource(url)
            : manifest;
    }

    /// <summary>This manifest with <paramref name="url"/> as its one source, for every file.</summary>
    internal MangaModelManifest WithOnlySource(string url) => this with
    {
        Sources = [new MangaModelSource("override",
            url.Contains(NamePlaceholder, StringComparison.Ordinal) ? url : url.TrimEnd('/') + "/" + NamePlaceholder,
            null)],
    };

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

        if (manifest.Sources is not { Count: > 0 })
            throw new InvalidDataException("manga model manifest has no sources");
        foreach (var source in manifest.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Url) || !source.Url.Contains(NamePlaceholder, StringComparison.Ordinal) ||
                !Uri.TryCreate(source.Url.Replace(NamePlaceholder, "x"), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http"))
                throw new InvalidDataException($"manga model source is malformed: {source.Url}");
            if (source.Files?.FirstOrDefault(name => manifest.Files.All(file => file.Name != name)) is { } unknown)
                throw new InvalidDataException($"manga model source {source.Name} names a file the manifest has not: {unknown}");
        }

        if (manifest.Files.FirstOrDefault(file => manifest.SourcesOf(file).Count == 0) is { } orphan)
            throw new InvalidDataException($"no manga model source has {orphan.Name}");

        return manifest;
    }

    /// <summary>Where <paramref name="file"/> can be fetched from, in the order to try them.</summary>
    internal IReadOnlyList<(MangaModelSource Source, Uri Url)> SourcesOf(MangaModelFile file) =>
        [.. Sources
            .Where(source => source.Files is null || source.Files.Contains(file.Name))
            .Select(source => (source, new Uri(source.Url.Replace(NamePlaceholder, Uri.EscapeDataString(file.Name)))))];

    internal MangaModelFile File(string role) =>
        Files.FirstOrDefault(file => file.Role == role)
        ?? throw new InvalidDataException($"manga model manifest has no {role} file");
}

/// <param name="Name">For the log: which source a file came from, or why it was left.</param>
/// <param name="Url">The file's address, with <c>{name}</c> where its name goes.</param>
/// <param name="Files">The files this source has; null for all of them.</param>
internal sealed record MangaModelSource(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("files")] IReadOnlyList<string>? Files);

/// <param name="Role">What the file is to the pipeline: detector, encoder, decoder-cross, decoder-step, vocabulary.</param>
internal sealed record MangaModelFile(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);
