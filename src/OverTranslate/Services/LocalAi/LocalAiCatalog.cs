using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverTranslate.Services.LocalAi;

/// <summary>The llama.cpp build the models run on: one zip, and where to get it.</summary>
/// <param name="Urls">Tried in order: the official release first, the application's own copy behind it.</param>
internal sealed record LocalAiRuntime(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("urls")] IReadOnlyList<string> Urls);

/// <summary>One model of the list.</summary>
/// <param name="ShortName">What the service menus and the 設定 card call it.</param>
/// <param name="VramMiB">What llama-fit-params projects at the app's launch parameters — see tools/LocalAiCatalog.</param>
/// <param name="Format">Which outer prompt format it wants — see <see cref="LocalAiPromptFormat"/>.</param>
/// <param name="DescriptionKey">The string the list describes it with; null for a model listed by name alone.</param>
internal sealed record LocalAiModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shortName")] string ShortName,
    [property: JsonPropertyName("recommended")] bool Recommended,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("vramMiB")] int VramMiB,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("descriptionKey")] string? DescriptionKey);

/// <summary>
/// The models 本機 AI 翻譯 offers and the llama.cpp build that runs them, as shipped beside the app.
/// </summary>
/// <remarks>
/// A file rather than code so that a model's address or hash changes without touching a line of it.
/// Every URL is pinned to a commit, and every file to a SHA-256: what is downloaded is what was
/// measured, whatever the repository it came from does later.
/// </remarks>
internal sealed record LocalAiCatalog(
    [property: JsonPropertyName("runtime")] LocalAiRuntime Runtime,
    [property: JsonPropertyName("models")] IReadOnlyList<LocalAiModel> Models)
{
    /// <summary>The descriptions an entry may name: a mistyped key would show up as the key itself.</summary>
    private static readonly HashSet<string> DescriptionKeys =
    [
        "S.LocalAi.ModelDescHy7B",
        "S.LocalAi.ModelDescGemmaE4B",
    ];

    internal static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "localai", "models.json");

    internal IEnumerable<LocalAiModel> Recommended => Models.Where(model => model.Recommended);

    /// <summary>The rest, smallest VRAM first.</summary>
    internal IEnumerable<LocalAiModel> Others => Models.Where(model => !model.Recommended).OrderBy(model => model.VramMiB);

    internal LocalAiModel? Find(string? id) => Models.FirstOrDefault(model => model.Id == id);

    internal static LocalAiCatalog Load(string path) => Parse(File.ReadAllText(path));

    internal static LocalAiCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<LocalAiCatalog>(json)
                      ?? throw new InvalidDataException("empty local AI model list");
        Validate(catalog);
        return catalog;
    }

    private static void Validate(LocalAiCatalog catalog)
    {
        if (catalog.Runtime is not { } runtime || string.IsNullOrWhiteSpace(runtime.Version) ||
            runtime.Version.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0 || runtime.Version is "." or ".." ||
            runtime.Size <= 0 || !IsHash(runtime.Sha256) || runtime.Urls is not { Count: > 0 } || !runtime.Urls.All(IsHttps))
            throw new InvalidDataException("the runtime entry of the local AI model list is malformed");

        if (catalog.Models is not { Count: > 0 })
            throw new InvalidDataException("the local AI model list has no models");

        foreach (var model in catalog.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !IsSafeName(model.Id) || !IsSafeName(model.File) ||
                string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.ShortName) ||
                model.Size <= 0 || model.VramMiB <= 0 || !IsHash(model.Sha256) || !IsHttps(model.Url) ||
                (model.DescriptionKey is not null && !DescriptionKeys.Contains(model.DescriptionKey)) || !LocalAiPromptFormat.IsKnown(model.Format))
                throw new InvalidDataException($"local AI model entry is malformed: {model.Id}");
        }

        if (catalog.Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Models.Count)
            throw new InvalidDataException("the local AI model list names a model twice");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    // A folder or file name, used as one: nothing that could climb out of the models folder.
    private static bool IsSafeName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0;
}
