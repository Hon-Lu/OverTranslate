using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The retried failure on the running capsule: it stands in the language pair's place in the
/// translation button until it clears. Opening the translation panel is not a way to put it away —
/// the panel is where the fix is picked, and the restart that follows is what clears it.
/// </summary>
public class RealtimeFailureNoticeTests
{
    private const string Refused = "Translation failed: unsupported language pair";

    [Fact]
    public void AFailure_IsKept_UntilItClears()
    {
        var notice = new RealtimeFailureNotice();

        Assert.True(notice.Set(Refused));
        Assert.Equal(Refused, notice.Message);

        Assert.True(notice.Set(null));
        Assert.Null(notice.Message);
    }

    // The session reports a failure once however many retries run into it.
    [Fact]
    public void TheSameFailureAgain_IsNothingNew()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);

        Assert.False(notice.Set(Refused));
    }

    [Fact]
    public void ADifferentFailure_IsNews()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);

        Assert.True(notice.Set("Translation failed: quota exceeded"));
    }

    [Fact]
    public void NothingFailing_IsNothingNew() =>
        Assert.False(new RealtimeFailureNotice().Set(null));

    // ── What the button shows ────────────────────────────────────────────────

    // A failure keeps the pair's place whatever else holds — the panel being open is not an input at
    // all — until it clears; then the button goes back to what the session is doing.
    [Theory]
    [InlineData(false, false, true, RealtimeButtonBody.Message)]
    [InlineData(true, false, true, RealtimeButtonBody.Message)]
    [InlineData(false, true, true, RealtimeButtonBody.Message)]
    [InlineData(false, false, false, RealtimeButtonBody.Pair)]
    public void AMessage_StandsInForThePair_UntilItGoes(bool paused, bool loading, bool message, RealtimeButtonBody expected) =>
        Assert.Equal(expected, RealtimeTranslationButtonRule.For(paused, loading, message, hasLanguages: true));

    // Paused, the pair stays in the button — 已暫停 is beside it — so the wrong service or language
    // can be seen and fixed before 繼續.
    [Fact]
    public void Paused_ShowsThePair_EvenWhileAModelLoads() =>
        Assert.Equal(RealtimeButtonBody.Pair, RealtimeTranslationButtonRule.For(paused: true, loadingModels: true, messageShowing: false, hasLanguages: true));

    [Fact]
    public void LoadingAModel_TakesThePairsPlace() =>
        Assert.Equal(RealtimeButtonBody.LoadingModels, RealtimeTranslationButtonRule.For(false, true, false, true));

    [Fact]
    public void BeforeThePairIsNamed_TheButtonSaysItIsRunning() =>
        Assert.Equal(RealtimeButtonBody.Running, RealtimeTranslationButtonRule.For(false, false, false, false));

    // ── The label card ───────────────────────────────────────────────────────

    [Fact]
    public void AMessageCutShort_IsWholeInTheLabelCard()
    {
        var whole = new string('錯', 80);

        Assert.Equal(whole, RealtimeTranslationButtonRule.Title(whole, "Google 翻譯 (標準)", "日文", "繁體中文"));
    }

    [Fact]
    public void Otherwise_TheLabelCardNamesTheServiceAndThePair() =>
        Assert.Equal("DeepL · 日文 → 繁體中文", RealtimeTranslationButtonRule.Title(null, "DeepL", "日文", "繁體中文"));

    [Fact]
    public void WithNoPairYet_TheSummaryIsTheService() =>
        Assert.Equal("DeepL", RealtimeTranslationButtonRule.Summary("DeepL", "", ""));
}
