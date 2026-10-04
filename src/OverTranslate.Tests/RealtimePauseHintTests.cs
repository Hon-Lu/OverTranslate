using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Both hints name a key the user can rebind or clear in 設定. Without one, the settings-page line
/// disappears while the clickable pause button still keeps a useful action-only tooltip.
/// </summary>
public class RealtimePauseHintTests
{
    [Fact]
    public void The_control_tooltip_names_the_normalized_bound_shortcut()
    {
        var hint = RealtimePauseHint.ForControlTooltip("  Ctrl+Alt+A  ", paused: false);

        Assert.Equal("暫停即時翻譯 (Ctrl+Alt+A)", hint);
    }

    // The button offers the way out of the state the session is in, so a paused session's tooltip
    // has to name the opposite action from a running one's.
    [Fact]
    public void The_control_tooltip_offers_resuming_while_paused()
    {
        var hint = RealtimePauseHint.ForControlTooltip("Ctrl+Alt+A", paused: true);

        Assert.Equal("繼續即時翻譯 (Ctrl+Alt+A)", hint);
    }

    [Fact]
    public void The_page_hint_names_the_bound_shortcut()
    {
        var hint = RealtimePauseHint.ForSettingsPage("Ctrl+Shift+R");

        Assert.Contains("Ctrl+Shift+R", hint);
    }

    // The settings file is hand-editable and the shortcut can be cleared from 設定, so all three of
    // these can really arrive.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Page_hint_is_hidden_and_control_tooltip_still_names_the_action_without_a_shortcut(string? hotkey)
    {
        Assert.Equal("暫停即時翻譯", RealtimePauseHint.ForControlTooltip(hotkey, paused: false));
        Assert.Equal("繼續即時翻譯", RealtimePauseHint.ForControlTooltip(hotkey, paused: true));
        Assert.Equal("", RealtimePauseHint.ForSettingsPage(hotkey));
    }
}
