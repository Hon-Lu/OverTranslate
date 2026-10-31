using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using OverTranslate.Views.Controls;
using OverTranslate.Views.Settings;
using Xunit;
using Kind = OverTranslate.Views.Settings.ServiceSettingsOverlay.ProfileTagKind;

namespace OverTranslate.Tests;

/// <summary>
/// What the OpenAI-compatible panel says about a setting without opening it: the tags after a row's
/// name and the line of parameters under its model, the folds the editor opens on, and which braces
/// in a template are painted as placeholders.
/// </summary>
public class OpenAiSettingsPanelTests
{
    // ── Tags ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheBuiltInRow_IsTaggedReadOnlyAndStandard()
    {
        Assert.Equal([Kind.ReadOnly, Kind.Segment], ServiceSettingsOverlay.TagsFor(OpenAiCompatibleProvider.BuiltInProfile(), builtIn: true));
    }

    [Fact]
    public void ASavedRowOnTheDefaults_IsTaggedStandardAndNothingElse()
    {
        Assert.Equal([Kind.Segment], ServiceSettingsOverlay.TagsFor(new OpenAiModelProfile { Id = "a", Model = "m" }, builtIn: false));
    }

    [Fact]
    public void ABatchRowWithItsOwnTemplate_IsTaggedBoth_InThatOrder()
    {
        var profile = new OpenAiModelProfile { Id = "a", Model = "m", Batch = true, Template = "翻成{target_name}：{text}" };

        Assert.Equal([Kind.Batch, Kind.CustomPrompt], ServiceSettingsOverlay.TagsFor(profile, builtIn: false));
    }

    /// <summary>A template of blanks is stored as the default, and is not the user's own.</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("  \n ", false)]
    [InlineData("x", true)]
    public void TheCustomPromptTag_FollowsWhetherATemplateIsStored(string template, bool custom)
    {
        var tags = ServiceSettingsOverlay.TagsFor(new OpenAiModelProfile { Id = "a", Template = template }, builtIn: false);

        Assert.Equal(custom, tags.Contains(Kind.CustomPrompt));
    }

    // ── Parameter line ─────────────────────────────────────────────────────

    private static string Label(string key) => key switch
    {
        "S.Settings.ThinkingOff" => "關閉思考",
        "S.Settings.Temperature" => "Temperature",
        "S.Settings.TopP" => "Top P",
        "S.Settings.Seed" => "Seed",
        _ => throw new ArgumentException(key),
    };

    [Fact]
    public void TheBuiltInSetting_SendsTemperatureTopPAndSeed()
    {
        Assert.Equal("Temperature 0.7 · Top P 0.6 · Seed 42",
            ServiceSettingsOverlay.ParameterSummary(OpenAiCompatibleProvider.BuiltInProfile(), Label));
    }

    [Fact]
    public void ThinkingOff_ComesFirst_AsItDoesInTheEditor()
    {
        var profile = new OpenAiModelProfile { ThinkingOff = true, Temperature = 0.25, TopPEnabled = false, Seed = 7 };

        Assert.Equal("關閉思考 · Temperature 0.25 · Seed 7", ServiceSettingsOverlay.ParameterSummary(profile, Label));
    }

    [Fact]
    public void AParameterSwitchedOff_IsLeftOut()
    {
        var profile = new OpenAiModelProfile { TemperatureEnabled = false, TopPEnabled = true, TopP = 1, SeedEnabled = false };

        Assert.Equal("Top P 1", ServiceSettingsOverlay.ParameterSummary(profile, Label));
    }

    [Fact]
    public void NothingSent_IsNoLineAtAll()
    {
        var profile = new OpenAiModelProfile { TemperatureEnabled = false, TopPEnabled = false, SeedEnabled = false };

        Assert.Equal("", ServiceSettingsOverlay.ParameterSummary(profile, Label));
        Assert.Equal(Visibility.Collapsed, new ServiceSettingsOverlay.PromptPresetRow { Parameters = "" }.ParametersVisibility);
    }

    /// <summary>Written as it is sent, whatever the machine's number format.</summary>
    [Fact]
    public void Numbers_AreWrittenTheInvariantWay()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            var profile = new OpenAiModelProfile { Temperature = 0.75, TopPEnabled = false, SeedEnabled = false };

            Assert.Equal("Temperature 0.75", ServiceSettingsOverlay.ParameterSummary(profile, Label));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    // ── 進階 opens shut ────────────────────────────────────────────────────

    /// <summary>
    /// The editor shuts 進階 each time it opens, through this: a fold left open — mid-animation
    /// included — comes back shut, unfocusable and with its chevron down.
    /// </summary>
    [Fact]
    public void ShutFold_ClosesAFoldLeftOpen()
    {
        OnUiThread(() =>
        {
            var host = new Border { Height = double.NaN, Opacity = 1, IsEnabled = true };
            var chevron = new RotateTransform(180);
            host.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, 120, TimeSpan.FromSeconds(5)));
            chevron.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 180, TimeSpan.FromSeconds(5)));

            PromptEditOverlay.ShutFold(host, chevron);

            Assert.Equal(0, host.Height);
            Assert.Equal(0, host.Opacity);
            Assert.False(host.IsEnabled);
            Assert.Equal(0, chevron.Angle);
            Assert.False(host.HasAnimatedProperties);
            Assert.False(chevron.HasAnimatedProperties);
        });
    }

    // ── Placeholders ───────────────────────────────────────────────────────

    [Fact]
    public void OnlyKnownPlaceholders_ArePainted()
    {
        const string text = "{context}\n翻成{target_name}（{target_code}），{unknown} 與 {Text} {text";

        var found = PlaceholderLayer.Find(text, OpenAiCompatibleProvider.Placeholders)
            .Select(m => text.Substring(m.Start, m.Length)).ToList();

        Assert.Equal(["{context}", "{target_name}", "{target_code}", "{Text}"], found);
    }

    [Fact]
    public void TheOldSpellings_ArePaintedToo_SinceTheyAreStillFilledIn()
    {
        const string text = "{source} → {target}";

        Assert.Equal(2, PlaceholderLayer.Find(text, OpenAiCompatibleProvider.Placeholders).Count());
    }

    [Fact]
    public void TheDefaultTemplates_HaveTheirPlaceholdersPainted()
    {
        foreach (var batch in new[] { false, true })
        {
            var template = OpenAiCompatibleProvider.DefaultTemplate(batch);
            var found = PlaceholderLayer.Find(template, OpenAiCompatibleProvider.Placeholders)
                .Select(m => template.Substring(m.Start, m.Length)).ToList();

            Assert.Contains("{context}", found);
            Assert.Contains("{text}", found);
            Assert.Contains("{target_name}", found);
        }
    }

    /// <summary>WPF elements may only be built on an STA thread, and xunit runs its tests on MTA ones.</summary>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
