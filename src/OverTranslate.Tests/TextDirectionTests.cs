using System.Windows;
using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which way a translation is set (#372): right to left as soon as it has a letter written that
/// way, left to right otherwise — see <see cref="TextDirection"/>.
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
        Assert.Equal(FlowDirection.RightToLeft, TextDirection.Of(text));

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
        Assert.Equal(FlowDirection.LeftToRight, TextDirection.Of(text));
}
