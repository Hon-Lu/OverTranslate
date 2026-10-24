using System.Text.RegularExpressions;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Whether a line opens the way a chat or log entry does: a timestamp, a channel tag, an @handle,
/// or a speaker's name and colon.
/// </summary>
/// <remarks>
/// Content evidence, for the reason <c>OcrTextBlockGrouper.StartsWithListBullet</c> is: a chat log
/// sets its entries at the leading and edge of a wrapped paragraph, so the geometry reads every
/// entry as the previous one continuing. Nothing continuing a sentence opens with its own speaker.
/// </remarks>
internal static class SpeakerLineStart
{
    private static readonly Regex Timestamp = new(
        @"^\s*[\[【(]\s*\d{1,2}\s*[:：]\s*\d{2}\s*[\]】)]", RegexOptions.CultureInvariant);

    // OCR reads a channel's brackets as 1, I, l, t or | about as often as it reads the brackets.
    private static readonly Regex Channel = new(
        @"^\s*(?:[\[【][^\s:：]{2,18}?[\]】)1|]|[IlIt][A-Z]{3,12}[1)\]])\s*[^:：;]{1,40}[:：;]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Handle = new(
        @"^\s*@[^\s:：]{1,40}\s*[:：]", RegexOptions.CultureInvariant);

    // Not a URL scheme ("https://") or a code declaration ("flex: 1;"): search results and
    // documentation align those on one edge too.
    private static readonly Regex Name = new(
        @"^\s*[\p{L}\p{N}_][^\s:：,，。!?！？]{0,31}\s*[:：](?!//)(?!.*;\s*$)", RegexOptions.CultureInvariant);

    private static readonly Regex[] Patterns = [Timestamp, Channel, Handle, Name];

    // The strict forms of a bare name, for the dialogue grouper, where a colon inside a wrapped
    // subtitle is common and cutting the sentence there is worse than missing a chat entry. A Latin
    // name is one capitalised Latin word ("this: never give up" is a sentence); a CJK one is a
    // run of up to eight letters that does not end on a verb of saying or a particle, so
    // 「他回頭對我說：」 and 「也就是說：」 are sentences and 「雲中日：」 is a name.
    private static readonly Regex LatinName = new(
        @"^\s*[A-ZÀ-Þ][A-Za-zÀ-ɏ0-9_.'\-]{0,31}\s*[:：](?!//)(?!.*;\s*$)",
        RegexOptions.CultureInvariant);

    private static readonly Regex CjkName = new(
        @"^\s*(?=[^:：]*[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}])" +
        @"[\p{L}\p{N}_ー・]{1,8}(?<![說说道講讲問问答曰云言是はがをにでともの])\s*[:：]",
        RegexOptions.CultureInvariant);

    private static readonly Regex[] StrictPatterns = [Timestamp, Channel, Handle, LatinName, CjkName];

    public static bool Opens(string text) => Patterns.Any(pattern => pattern.IsMatch(text));

    /// <summary>As <see cref="Opens"/>, but a bare name has to look like a name, not a clause.</summary>
    public static bool OpensUnmistakably(string text) => StrictPatterns.Any(pattern => pattern.IsMatch(text));

    /// <summary>A line that is nothing but the speaker, with the message on the rows below it.</summary>
    public static bool IsBareLabel(string text) => IsBareLabel(text, Patterns);

    /// <summary>As <see cref="IsBareLabel(string)"/>, but a bare name has to look like a name, not a clause.</summary>
    public static bool IsUnmistakablyBareLabel(string text) => IsBareLabel(text, StrictPatterns);

    private static bool IsBareLabel(string text, Regex[] patterns)
    {
        var trimmed = text.TrimEnd();
        return patterns.Select(pattern => pattern.Match(trimmed))
            .Any(match => match.Success && match.Index + match.Length == trimmed.Length);
    }
}
