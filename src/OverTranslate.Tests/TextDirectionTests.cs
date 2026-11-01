using System.Windows;
using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which way a translation is set (#372): right to left when it is meant to be Persian and has a
/// letter written that way, left to right otherwise — see <see cref="TextDirection"/>.
/// </summary>
public class TextDirectionTests
{
    [Theory]
    [InlineData("در قفل است.")]
    [InlineData("سلام، جهان!")]
    // Opens with an English key name and a number — the sentences a first-strong-letter rule would
    // have set backwards — and is still a Persian sentence.
    [InlineData("F5 را فشار دهید تا بازی خود را ذخیره کنید (نسخه 2.1).")]
    [InlineData("100 اسب بخار بازیابی شد!")]
    [InlineData("Windows را دوباره راه‌اندازی کنید.")]
    public void Persian_IsSetRightToLeft(string text) =>
        Assert.Equal(FlowDirection.RightToLeft, TextDirection.Of(text, "FA"));

    [Theory]
    [InlineData("Steam!")]               // came back untranslated: a name
    [InlineData("The door is locked.")]
    [InlineData("剣は折れてしまった。")]
    [InlineData("說得也是")]
    [InlineData("안녕하세요")]
    [InlineData("Я правда не думал")]
    // Persian digits, the Arabic comma and question mark are not letters, and on their own say
    // nothing about which way a line reads.
    [InlineData("۱۲۳۴")]
    [InlineData("،؟")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_StaysLeftToRight(string? text) =>
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.Of(text, "FA"));

    /// <summary>
    /// Engines name the language in their own codes, the pickers in theirs; Persian is Persian in
    /// either.
    /// </summary>
    [Theory]
    [InlineData("fa")]
    [InlineData("Fa")]
    [InlineData("fa-IR")]
    public void Persian_IsKnownByAnyOfItsCodes(string language) =>
        Assert.Equal(FlowDirection.RightToLeft, TextDirection.Of("در قفل است.", language));

    /// <summary>
    /// Any other language is left to right without its text being looked at: a stray Arabic or
    /// Hebrew letter in a Chinese, Japanese or English translation — or the whole of a Persian one
    /// that came back under the wrong target — cannot turn it round.
    /// </summary>
    [Theory]
    [InlineData("這是 سلام 的意思。", "ZH-HANT")]
    [InlineData("「שלום」は挨拶です。", "JA")]
    [InlineData("He said سلام and left.", "EN-US")]
    [InlineData("در قفل است.", "ZH-HANS")]
    [InlineData("در قفل است.", "AUTO")]
    [InlineData("در قفل است.", "")]
    [InlineData("در قفل است.", null)]
    public void AnotherLanguage_IsLeftToRightWhateverItsText(string text, string? language)
    {
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.Of(text, language));
        Assert.False(TextDirection.IsRightToLeft(text, language));
    }
}
