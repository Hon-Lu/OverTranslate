using OpenccNetLib;
using OverTranslate.Models;

namespace OverTranslate.Services;

internal static class DictionaryTraditionalChineseConverter
{
    private static readonly Opencc Converter = new(OpenccConfig.S2Tw);

    internal static DictionaryLookupData Convert(DictionaryLookupData source) =>
        source with
        {
            Source = ConvertText(source.Source),
            Headword = ConvertOptional(source.Headword),
            Groups = source.Groups.Select(group => group with
            {
                Entries = group.Entries.Select(entry => entry with
                {
                    Text = ConvertText(entry.Text),
                    BackTranslations = ConvertAll(entry.BackTranslations),
                }).ToList(),
            }).ToList(),
        };

    private static IReadOnlyList<string> ConvertAll(IReadOnlyList<string> values) =>
        values.Select(ConvertText).ToList();

    private static string? ConvertOptional(string? value) =>
        string.IsNullOrEmpty(value) ? value : ConvertText(value);

    private static string ConvertText(string value) =>
        value.Length == 0 ? value : Converter.Convert(value, punctuation: false);
}
