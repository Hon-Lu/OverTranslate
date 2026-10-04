using System.Drawing;
using Xunit;

namespace OverTranslate.Tests;

// Real-pixel OCR regressions remain available without shipping screenshots in Fixtures.
internal static class ExternalScreenshot
{
    internal const string DirectoryVariable = "OVERTRANSLATE_TEST_IMAGES";

    internal static string? MissingReason(string[] names)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return $"Set {DirectoryVariable} to run external screenshot regressions.";
        var missing = names.Where(name => !File.Exists(Path.Combine(directory, name))).ToArray();
        return missing.Length == 0 ? null : $"Missing external screenshots: {string.Join(", ", missing)}";
    }

    internal static Bitmap Load(string name) => new(Path.Combine(
        Environment.GetEnvironmentVariable(DirectoryVariable)
            ?? throw new InvalidOperationException($"{DirectoryVariable} is not configured."), name));
}

public sealed class ScreenshotFactAttribute : FactAttribute
{
    public ScreenshotFactAttribute(params string[] files) => Skip = ExternalScreenshot.MissingReason(files);
}

public sealed class ScreenshotTheoryAttribute : TheoryAttribute
{
    public ScreenshotTheoryAttribute(params string[] files) => Skip = ExternalScreenshot.MissingReason(files);
}
