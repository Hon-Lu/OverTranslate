using System.Text;
using FlowDirection = System.Windows.FlowDirection;

namespace OverTranslate.Layout;

/// <summary>
/// Which way a translation is read: right to left when it is written in a right-to-left script,
/// left to right otherwise.
/// </summary>
/// <remarks>
/// <para>Persian (#372) is the first target written right to left, and every place a translation is
/// drawn used to be fixed left to right. WPF already puts the letters of a Persian word in the right
/// order on its own; what the paragraph direction decides is everything around them — which end a
/// sentence starts at, where its full stop and question mark go, which side of the Persian an English
/// word or a number lands on, and which edge a wrapped paragraph's lines start from. Left to right,
/// 「F5 را فشار دهید.」 comes out with its full stop at the wrong end and F5 at the end of the
/// sentence instead of its start.</para>
///
/// <para>Decided by the text rather than by the target language alone, and by whether it has any
/// right-to-left letter rather than by its first one. A translation into Persian that came back
/// untranslated — a name, a product, 「Steam」 — is English and is set as English; were it set right
/// to left, 「Hello!」 would read 「!Hello」. And a Persian sentence very often opens with an English
/// word or a key name, so the first-strong-letter rule (HTML's <c>dir="auto"</c>) would set exactly
/// those sentences backwards. None of the other languages this application translates into is
/// written with these letters, so for every one of them this is left to right, as before.</para>
///
/// <para>The box a translation sits in does not move or change size. Only where the text starts
/// inside it follows the reading direction: a TextBlock set right to left mirrors its own
/// <c>TextAlignment.Left</c>, so a paragraph that started at the left edge starts at the right one,
/// and a centred one stays centred.</para>
/// </remarks>
internal static class TextDirection
{
    /// <summary>The direction to set <paramref name="text"/> in.</summary>
    public static FlowDirection Of(string? text) =>
        IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Whether <paramref name="text"/> has a letter of a right-to-left script.</summary>
    /// <remarks>
    /// Letters only. The Arabic block also holds its own digits, comma and question mark, and
    /// those take the direction of what is around them like any other digit or punctuation mark —
    /// 「۱۲۳」 on its own is a number, not Persian.
    /// </remarks>
    public static bool IsRightToLeft(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var rune in text.EnumerateRunes())
            if (IsRightToLeftScript(rune.Value) && Rune.IsLetter(rune))
                return true;

        return false;
    }

    private static bool IsRightToLeftScript(int v) =>
        v is >= 0x0590 and <= 0x08FF or    // Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan, Mandaic, Arabic Extended
            >= 0xFB1D and <= 0xFDFF or     // Hebrew and Arabic presentation forms
            >= 0xFE70 and <= 0xFEFF or     // Arabic presentation forms-B
            >= 0x10800 and <= 0x10FFF or   // Historic right-to-left scripts
            >= 0x1E800 and <= 0x1EFFF;     // Mende Kikakui, Adlam, Arabic mathematical letters
}
