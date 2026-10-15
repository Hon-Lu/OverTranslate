using System.Drawing;
using System.IO;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Reads the text of a page's blocks; one implementation per recognition model.</summary>
internal interface IMangaTextRecognizer : IDisposable
{
    /// <summary>Reads each block of the page, in the order given.</summary>
    /// <param name="rgb">The whole page as packed RGB (see <see cref="PillowImage.Rgb"/>).</param>
    List<MangaReading> Read(byte[] rgb, int width, int height, IReadOnlyList<Rectangle> blocks);
}

/// <summary>
/// Which model reads vertical manga blocks: manga-ocr, as shipped, unless the environment asks for
/// Hayai and its files are in place.
/// </summary>
/// <remarks>
/// An experiment, kept out of the interface on purpose: set <c>OVERTRANSLATE_MANGA_RECOGNIZER=hayai</c>
/// for the process (for example <c>setx OVERTRANSLATE_MANGA_RECOGNIZER hayai</c> and start the app
/// again) and put the Hayai files in <see cref="HayaiFolder"/>. Anything else, or missing files,
/// reads with manga-ocr. Asked when the models load, so a change takes effect the next time they do.
/// </remarks>
internal static class MangaRecognizerChoice
{
    internal const string Variable = "OVERTRANSLATE_MANGA_RECOGNIZER";

    internal static string HayaiFolder => Path.Combine(AppDataPaths.Models, "manga-hayai", "v1");

    /// <summary>Whether the environment asks for Hayai, whether or not its files are there.</summary>
    internal static bool AsksForHayai(Func<string, string?>? environment = null) =>
        string.Equals((environment ?? Environment.GetEnvironmentVariable)(Variable)?.Trim(), "hayai",
            StringComparison.OrdinalIgnoreCase);
}
