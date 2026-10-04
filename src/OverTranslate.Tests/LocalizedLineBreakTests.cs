using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

// XAML collapses whitespace in element content, and a character reference is resolved by the XML
// reader before XAML ever looks at it — so a &#x0a; written into a string arrives as a line feed and
// leaves as a space. It shipped that way once: two sentences meant to sit on their own lines ran
// together in the settings card, and nothing failed to say so. xml:space="preserve" is what keeps
// the break, and it is easy to drop while editing the wording around it.
//
// Read as XML rather than through XamlReader, which would want an STA thread for what is really a
// question about the source file: a string that asks for a line break has to be marked to keep one.
//
// Only the strings that are written as two lines on purpose. Four older strings elsewhere have the
// same problem and are deliberately not covered — fixing them changes screens this had no business
// touching, and a test that fails for something nobody is fixing is a test people learn to ignore.
public class LocalizedLineBreakTests
{
    // The two result-panel hints were written as two lines and have since been rewritten as one
    // paragraph each — deliberately, as the wording changed around the export and upload being
    // separate presses. They are out because a break is no longer what they are asking for; the
    // card's own hint still is, and is what this guards.
    private static readonly string[] TwoLineKeys =
    {
        "S.Settings.DiagnosticsUploadHint",
    };

    /// <summary>Every shipped dictionary, so a new language cannot lose the break quietly.</summary>
    public static TheoryData<string> Dictionaries()
    {
        var data = new TheoryData<string>();
        foreach (var file in StringsParityTests.AllFiles) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Dictionaries))]
    public void TheTwoLineHints_KeepTheirLineBreak(string file)
    {
        XNamespace xml = "http://www.w3.org/XML/1998/namespace";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var strings = XDocument
            .Load(Path.Combine(ResourcesDirectory, file))
            .Descendants()
            .Where(element => element.Name.LocalName == "String")
            .ToDictionary(element => (string?)element.Attribute(xaml + "Key") ?? "", element => element);

        foreach (var key in TwoLineKeys)
        {
            var element = Assert.Contains(key, strings);

            // Two sentences, one break, and nothing hanging off either end — a stray space either
            // side of the newline is the other way this goes wrong, and it looks fine in the source.
            var lines = element.Value.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.Equal(line.Trim(), line));

            Assert.Equal("preserve", (string?)element.Attribute(xml + "space"));
        }
    }

    /// <summary>
    /// Walked up from the test binaries rather than hard-coded, so this keeps working from whichever
    /// configuration and framework folder the run happens to be in.
    /// </summary>
    private static string ResourcesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null &&
                   !Directory.Exists(Path.Combine(directory.FullName, "src", "OverTranslate")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "OverTranslate", "Resources");
        }
    }
}
