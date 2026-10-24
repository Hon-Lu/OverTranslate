using System.IO;
using System.Linq;
using System.Xml.Linq;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

// The realtime bar's label card reads 對照顯示's line from the button's tooltip, which opens with
// the switch's own name — under a card headed by that name it would say it twice. What comes off is
// the name and its separator, and only that: if a translation ever stops opening with the name, the
// whole tooltip is still a better line than half of one.
public class RealtimeControlLabelTests
{
    [Theory]
    [InlineData("原文對照：關閉時將翻譯後譯文直接覆蓋原文畫面", "原文對照", "關閉時將翻譯後譯文直接覆蓋原文畫面")]
    [InlineData("Compare with original: turn off to show it", "Compare with original", "Turn off to show it")]
    [InlineData("원문 대조: 끄면 표시됩니다", "원문 대조", "끄면 표시됩니다")]
    [InlineData("Something else entirely", "Compare with original", "Something else entirely")]
    [InlineData("原文對照", "原文對照", null)]
    [InlineData(null, "原文對照", null)]
    public void TheNameComesOffTheFrontAndNothingElse(string? tooltip, string name, string? expected) =>
        Assert.Equal(expected, RealtimeControlWindow.StripLeadingName(tooltip, name));

    /// <summary>Every shipped language still opens both hints with the name, so the strip applies.</summary>
    [Fact]
    public void BothCompareHintsOpenWithTheNameInEveryLanguage()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in StringsParityTests.AllFiles)
        {
            var strings = XDocument.Load(Path.Combine(ResourcesDirectory, file))
                .Descendants()
                .Where(element => element.Name.LocalName == "String")
                .ToDictionary(element => (string?)element.Attribute(xaml + "Key") ?? "", element => element.Value);

            var name = strings["S.Realtime.CompareDisplay"];
            foreach (var key in new[] { "S.Realtime.CompareDisplayOnHint", "S.Realtime.CompareDisplayOffHint" })
            {
                var line = RealtimeControlWindow.StripLeadingName(strings[key], name);
                Assert.NotNull(line);
                Assert.NotEqual(strings[key], line);
            }
        }
    }

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
