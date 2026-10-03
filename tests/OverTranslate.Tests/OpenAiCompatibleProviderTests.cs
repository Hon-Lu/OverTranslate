using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Translation.OpenAi;
using Xunit;

namespace OverTranslate.Tests;

public class OpenAiCompatibleProviderTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/chat/completions")]
    [InlineData("https://example.test/custom/chat/completions", "https://example.test/custom/chat/completions")]
    public void BuildEndpoint_AcceptsBaseOrFullChatCompletionsUrl(string input, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.BuildEndpoint(input).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildEndpoint_FallsBackToTheDefaultServerWhenTheBoxIsEmpty(string input)
    {
        Assert.Equal(
            "http://localhost:11434/v1/chat/completions",
            OpenAiCompatibleProvider.BuildEndpoint(input).AbsoluteUri);
    }

    [Theory]
    [InlineData("localhost:1234")]
    [InlineData("ftp://example.test/v1")]
    public void BuildEndpoint_RejectsInvalidUrl(string input)
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleProvider.BuildEndpoint(input));
    }

    /// <summary>
    /// Runs a test with the interface in a given language, and puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// The interface language lives in the one shared settings instance, so a test that set it and
    /// walked away would decide the answer for whichever test ran next.
    /// </remarks>
    private static void WithInterfaceLanguage(string language, Action assert) =>
        WithInterfaceLanguage(language, () => { assert(); return 0; });

    /// <inheritdoc cref="WithInterfaceLanguage(string, Action)"/>
    /// <remarks>
    /// The overload for reading something out rather than asserting inside: comparing what several
    /// languages produce needs the values in one place, and a captured local written from inside the
    /// Action is a worse way to get them out.
    /// </remarks>
    private static T WithInterfaceLanguage<T>(string language, Func<T> read)
    {
        var settings = SettingsService.Instance.Current;
        var original = settings.UiLanguage;
        try
        {
            settings.UiLanguage = language;
            return read();
        }
        finally
        {
            settings.UiLanguage = original;
        }
    }

    /// <summary>
    /// A phrase from each interface language's built-in wording, naming no language.
    /// </summary>
    /// <remarks>
    /// Fragments rather than the whole sentence, because the whole sentence cannot be asserted for
    /// every language here: the language names inside it follow the interface — see
    /// <see cref="BuildPrompts_NamesLanguagesInTheInterfaceLanguage"/> — and those names come from
    /// the string dictionaries, which fall back to zh-Hant whenever there is no Application to ask.
    /// In this process a Japanese interface therefore names the target in Chinese, which is an
    /// artefact of the test host rather than what ships, and a full-sentence table would be pinning
    /// it.
    ///
    /// So this pins the half that is honest in-process — which wording was chosen — and
    /// <see cref="IntoTraditionalChinese"/> pins whole sentences for the two languages where the
    /// names are truthful here.
    ///
    /// 日本語 and 한국어 share the English fragment on purpose: the model's documentation publishes a
    /// prompt for Chinese and for English and none for those two.
    /// </remarks>
    private static readonly Dictionary<string, string> WordingMarkers = new()
    {
        [LocalizationService.TraditionalChinese] = "注意只需要輸出翻譯後的結果，不要額外解釋：",
        [LocalizationService.SimplifiedChinese]  = "注意只需要输出翻译后的结果，不要额外解释：",
        [LocalizationService.English]            = "without any additional explanation:",
        [LocalizationService.Japanese]           = "without any additional explanation:",
        [LocalizationService.Korean]             = "without any additional explanation:",
    };

    /// <summary>The whole built-in sentence, into Traditional Chinese, for the two interface
    /// languages whose language names resolve truthfully in this process.</summary>
    /// <remarks>
    /// Written out in full rather than assembled from the same constants the provider uses, which
    /// would agree with any change including a wrong one. These are the sentences the model is sent,
    /// down to the colon that ends them — what follows it is the text being translated, so losing it
    /// would run the instruction into the first line on screen.
    ///
    /// Only two entries, for the reason <see cref="WordingMarkers"/> gives: English resolves names
    /// from a plain property rather than the dictionaries, and zh-Hant is what the dictionaries fall
    /// back to, so both are the same here as they are in the running app.
    /// </remarks>
    private static readonly Dictionary<string, string> IntoTraditionalChinese = new()
    {
        [LocalizationService.TraditionalChinese] =
            "將以下文本翻譯為繁體中文，注意只需要輸出翻譯後的結果，不要額外解釋：\n\n",

        [LocalizationService.English] =
            "Translate the following text into Traditional Chinese. Note that you should only " +
            "output the translated result without any additional explanation:\n\n",
    };

    /// <summary>The two messages the built-in setting sends for one case, filled.</summary>
    private static (string System, string User) BuiltIn(
        string sourceLang, string targetLang = "ZH-HANT") =>
        OpenAiCompatibleProvider.BuildPrompts(
            sourceLang,
            targetLang,
            OpenAiCompatibleProvider.BuiltInProfile()
                .PromptsFor(LanguageData.IsAutomaticSource(sourceLang)));

    /// <summary>What one written user prompt reaches the model as.</summary>
    private static string Written(
        string sourceLang, string prompt, string targetLang = "ZH-HANT") =>
        OpenAiCompatibleProvider.BuildPrompts(
            sourceLang, targetLang, new OpenAiPromptPair { UserPrompt = prompt }).User;

    /// <summary>
    /// The setting this ships with sends no system message at all.
    /// </summary>
    /// <remarks>
    /// The decision the storage was reshaped around, so it is pinned rather than left to the
    /// constant: the recommended model is a translation model whose published format is a single
    /// user turn, and a system message it never saw in training is a variable nobody asked for. Both
    /// cases, and every interface language, because the wording table is the thing most likely to be
    /// edited into having one.
    /// </remarks>
    [Theory]
    [InlineData(LocalizationService.TraditionalChinese)]
    [InlineData(LocalizationService.SimplifiedChinese)]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Japanese)]
    [InlineData(LocalizationService.Korean)]
    public void BuiltIn_SendsTheInstructionAsTheUserMessageAndNoSystemMessage(string uiLanguage)
    {
        WithInterfaceLanguage(uiLanguage, () =>
        {
            foreach (var prompts in new[] { BuiltIn("AUTO"), BuiltIn("JA") })
            {
                Assert.Equal("", prompts.System);
                Assert.NotEmpty(prompts.User);
            }
        });
    }

    /// <summary>
    /// The built-in wording follows the interface language, and both cases send the same one.
    /// </summary>
    /// <remarks>
    /// 自動 and a chosen source language are asserted to match because this model is told what to
    /// translate into and detects the rest, so naming the source would be an instruction with
    /// nothing behind it. Pinned so that giving one case its own wording is a decision rather than a
    /// drift.
    /// </remarks>
    [Theory]
    [InlineData(LocalizationService.TraditionalChinese)]
    [InlineData(LocalizationService.SimplifiedChinese)]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Japanese)]
    [InlineData(LocalizationService.Korean)]
    public void BuildPrompts_UsesTheWordingForTheInterfaceLanguage(string uiLanguage)
    {
        WithInterfaceLanguage(uiLanguage, () =>
        {
            var explicitSource = BuiltIn("JA").User;
            var automatic = BuiltIn("AUTO").User;

            Assert.Contains(WordingMarkers[uiLanguage], explicitSource);
            Assert.Equal(explicitSource, automatic);

            if (IntoTraditionalChinese.TryGetValue(uiLanguage, out var whole))
                Assert.Equal(whole, explicitSource);
        });
    }

    /// <summary>
    /// Three wordings for five interfaces: 日本語 and 한국어 are served the English one.
    /// </summary>
    /// <remarks>
    /// The owner's decision, and the kind that looks like an oversight to whoever reads the table
    /// next — so it is pinned from both sides. The three that are meant to differ must differ, which
    /// catches two entries pasted from each other; and the two that are meant to match must match,
    /// so that inventing a Japanese wording is a deliberate edit to this test rather than a silent
    /// guess at a prompt format the model's own documentation does not publish.
    /// </remarks>
    [Fact]
    public void BuildPrompts_ServesJapaneseAndKoreanTheEnglishWording()
    {
        // The unfilled wording, not a filled one: the language names inside come from dictionaries
        // that fall back to zh-Hant in this process, so two identical wordings would come back
        // different — see WordingMarkers.
        static string For(string language) => WithInterfaceLanguage(
            language, () => OpenAiCompatibleProvider.BuiltInProfile().Auto.UserPrompt);

        var english = For(LocalizationService.English);

        Assert.Equal(english, For(LocalizationService.Japanese));
        Assert.Equal(english, For(LocalizationService.Korean));

        Assert.Equal(3, new[]
        {
            english,
            For(LocalizationService.TraditionalChinese),
            For(LocalizationService.SimplifiedChinese),
        }.Distinct().Count());
    }

    /// <summary>
    /// The built-in wording is one line ending in a colon and a blank line.
    /// </summary>
    /// <remarks>
    /// The instruction and the text travel in the same user message with nothing inserted between
    /// them — see <see cref="OpenAiChatTranslator.BuildMessages"/> — so both the colon and the
    /// two line feeds after it are doing work: the colon says the next thing is the material, and
    /// the break is the blank line the model was trained to see there.
    ///
    /// Pinned from both ends because both are invisible to read. Trimming the string closes the
    /// blank line up and runs the instruction into the first line on screen; rewording it into two
    /// lines puts a break where the model expects prose.
    /// </remarks>
    [Theory]
    [InlineData(LocalizationService.TraditionalChinese)]
    [InlineData(LocalizationService.SimplifiedChinese)]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Japanese)]
    [InlineData(LocalizationService.Korean)]
    public void BuildPrompts_EndTheWordingWithTheBreakTheTextStartsAfter(string uiLanguage)
    {
        WithInterfaceLanguage(uiLanguage, () =>
        {
            var prompt = BuiltIn("JA").User;

            Assert.DoesNotContain("\r", prompt);
            Assert.EndsWith("：\n\n", prompt.Replace(":\n\n", "：\n\n"));

            // The instruction itself is one line: the only breaks in it are the two at the end.
            Assert.Equal(2, prompt.Count(c => c == '\n'));
        });
    }

    /// <summary>
    /// The text starts at the character after the wording, with nothing added in between.
    /// </summary>
    /// <remarks>
    /// The whole reason the break is stored in the wording. A separator added at the join would be
    /// this application deciding the shape of a format the model publishes, and there would be no
    /// way to write a prompt whose segment starts on the very next character.
    /// </remarks>
    [Fact]
    public void BuildMessages_AddsNothingBetweenTheWordingAndTheText()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            var prompts = BuiltIn("JA");
            var user = Assert.Single(OpenAiChatTranslator.BuildMessages(prompts, "hello"));

            Assert.Equal(prompts.User + "hello", Content(user));
            Assert.EndsWith("explanation:\n\nhello", Content(user));
        });
    }

    /// <summary>
    /// <c>{TEXT}</c> is still not one of this application's placeholders, and nothing fills it.
    /// </summary>
    /// <remarks>
    /// The built-in wording carried one when this provider was written for TranslateGemma, whose
    /// documentation put the token at the end of the instruction. The recommended model does not,
    /// so no built-in wording has one any more — but a template someone wrote back then still does,
    /// and it has to reach the model as the literal characters they typed.
    ///
    /// Pinned because it reads exactly like a placeholder nobody wired up, and the obvious "fix" is
    /// to start substituting it. That would send the text twice, on top of the user message the text
    /// already travels in.
    /// </remarks>
    [Fact]
    public void BuildPrompts_LeavesTheTextTokenAlone()
    {
        Assert.DoesNotContain("{TEXT}", BuiltIn("JA").User);
        Assert.Equal("keep {TEXT} here", Written("JA", "keep {TEXT} here"));
    }

    /// <summary>
    /// The languages are named in the interface's own language, as the wording around them is.
    /// </summary>
    /// <remarks>
    /// The names followed English regardless of the interface until the built-in wording stopped
    /// doing so. An instruction written in Chinese that names its target in English is a sentence in
    /// two languages, and the recommended model is documented as wanting the language named the way
    /// the instruction around it is written.
    ///
    /// Both directions are asserted. Only checking that a Chinese interface produces 繁體中文 would
    /// still pass if the names had simply been hard-coded to Chinese.
    ///
    /// Two interface languages rather than five, for the reason <see cref="WordingMarkers"/> gives:
    /// the other three resolve their names through dictionaries that are not loaded here.
    /// </remarks>
    [Fact]
    public void BuildPrompts_NamesLanguagesInTheInterfaceLanguage()
    {
        WithInterfaceLanguage(LocalizationService.TraditionalChinese, () =>
        {
            var prompt = BuiltIn("JA").User;

            Assert.Contains("繁體中文", prompt);
            Assert.DoesNotContain("Traditional Chinese", prompt);
        });

        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            var prompt = BuiltIn("JA").User;

            Assert.Contains("Traditional Chinese", prompt);
            Assert.DoesNotContain("繁體中文", prompt);
        });
    }

    /// <summary>Both halves are filled the same way.</summary>
    /// <remarks>
    /// The system half is new, and the substitution is the kind of thing that gets wired to one
    /// argument and not the other — which would reach the model as a literal brace in whichever
    /// half the author was not looking at.
    /// </remarks>
    [Fact]
    public void BuildPrompts_FillsBothHalves()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            var prompts = OpenAiCompatibleProvider.BuildPrompts(
                "JA", "ZH-HANT",
                new OpenAiPromptPair
                {
                    SystemPrompt = "system: {source_name} to {target_name}",
                    UserPrompt = "user: into {target_name}",
                });

            Assert.Equal("system: Japanese to Traditional Chinese", prompts.System);
            Assert.Equal("user: into Traditional Chinese", prompts.User);
        });
    }

    /// <summary>
    /// A half left blank stays blank, and a blank system half means no system message.
    /// </summary>
    /// <remarks>
    /// The contract the whole shape rests on: empty is an answer here rather than a gap to fill in,
    /// because the built-in setting deliberately has one. The built-in wording is resolved into the
    /// options before this is reached — see <see cref="OpenAiCompatibleOptions.PromptsFor"/> — so
    /// substituting anything here would overrule a model's documented format.
    ///
    /// Whitespace counts as blank: a box that looks empty and a box that is empty have to mean the
    /// same thing, or a stray space silently sends the model a space as its entire instruction.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n   ")]
    [InlineData("　")]
    public void BuildPrompts_LeavesABlankHalfBlank(string blank)
    {
        var prompts = OpenAiCompatibleProvider.BuildPrompts(
            "JA", "ZH-HANT",
            new OpenAiPromptPair { SystemPrompt = blank, UserPrompt = blank });

        Assert.Equal("", prompts.System);
        Assert.Equal("", prompts.User);
        Assert.Single(OpenAiChatTranslator.BuildMessages(prompts, "hello"));
    }

    /// <summary>
    /// A prompt someone typed reaches the model with bare <c>\n</c>, however they typed it.
    /// </summary>
    /// <remarks>
    /// A WPF TextBox writes <c>\r\n</c> for Return while the wording loaded into it uses <c>\n</c>,
    /// so an edited prompt ends up holding both — the settings file on the machine this was found on
    /// held "…表達。\r\n\n注意…". Two prompts that read the same were being sent as different
    /// strings, and the preview drew the mixed one with a doubled paragraph gap because the piece
    /// before the break still ended in a \r.
    ///
    /// Normalised where the editor saves as well, so this only has to catch prompts written before
    /// that — which is exactly why it is asserted here rather than only in the editor.
    /// </remarks>
    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    public void BuildPrompts_SendAWrittenPromptWithBareLineFeeds(string written)
    {
        Assert.Equal("a\nb", Written("JA", written));
    }

    /// <summary>
    /// A prompt is sent with the end it was written with, trailing break included.
    /// </summary>
    /// <remarks>
    /// The separator is the writer's to place, so trimming the end of what they wrote would silently
    /// join their instruction to the text — and a prompt that deliberately ends mid-sentence, which
    /// some formats do, would be unwritable.
    /// </remarks>
    [Fact]
    public void BuildPrompts_KeepTheBreakAWrittenPromptEndsWith()
    {
        Assert.Equal("翻成中文：\n\n", Written("JA", "翻成中文：\r\n\r\n"));
        Assert.Equal("翻成中文：", Written("JA", "翻成中文："));
    }

    /// <summary>The mixed break the bug was actually found as survives the round trip as one gap.</summary>
    [Fact]
    public void BuildPrompts_CollapsesAMixedParagraphBreakToOneBlankLine()
    {
        var prompt = Written("JA", "first\r\n\nsecond");

        Assert.Equal("first\n\nsecond", prompt);
        Assert.Equal(2, prompt.Count(c => c == '\n'));
    }

    /// <summary>
    /// A prompt the user wrote is filled the same way, in whatever language the interface is.
    /// </summary>
    /// <remarks>
    /// The cost of the rule above, pinned rather than left to be discovered: someone whose prompt is
    /// in one language while their interface is in another gets language names in the interface's.
    /// The prompt editor's parameter table shows the example in the interface language so the rule
    /// is visible before anything is sent.
    /// </remarks>
    [Fact]
    public void BuildPrompts_NamesLanguagesTheSameWayInAWrittenPrompt()
    {
        WithInterfaceLanguage(LocalizationService.TraditionalChinese, () =>
        {
            Assert.Equal("into 繁體中文", Written("JA", "into {target_name}"));
        });
    }

    /// <summary>
    /// The built-in wording names the target language and nothing else — no source, no tags.
    /// </summary>
    /// <remarks>
    /// The tags went out with the TranslateGemma-shaped wording, and the source language with them:
    /// the recommended model is told what to translate into and detects the rest. Both placeholders
    /// are still offered to prompts users write — see
    /// <see cref="OpenAiCompatibleProvider.SourceCodePlaceholder"/> — so this pins the built-in
    /// wording rather than the substitution behind it.
    /// </remarks>
    [Theory]
    [InlineData("ZH-HANT", "Traditional Chinese")]
    [InlineData("EN-US", "English")]
    public void BuildPrompts_NameOnlyTheTargetLanguage(string targetCode, string targetName)
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            foreach (var prompt in new[]
                     {
                         BuiltIn("AUTO", targetCode).User,
                         BuiltIn("JA", targetCode).User,
                     })
            {
                Assert.Contains($"into {targetName}. Note", prompt);
                Assert.DoesNotContain("any language", prompt);
                Assert.DoesNotContain("Japanese", prompt);
                Assert.DoesNotContain("(zh-Hant)", prompt);
                Assert.DoesNotContain("(ja)", prompt);
            }
        });
    }

    // The prompt belongs to the case it was written for: one profile holds both pairs, and picking a
    // source language must not send the wording the other case was written with.
    [Fact]
    public void BuildPrompts_UseThePairForTheCaseInHand()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            var options = new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", "",
                new OpenAiPromptPair { UserPrompt = "auto: into {target}" },
                new OpenAiPromptPair { UserPrompt = "chosen: {source}->{target}" });

            var automatic = OpenAiCompatibleProvider.BuildPrompts(
                "AUTO", "ZH-HANT", options.PromptsFor(automatic: true));
            var chosen = OpenAiCompatibleProvider.BuildPrompts(
                "JA", "ZH-HANT", options.PromptsFor(automatic: false));

            // The name placeholders still mean the name alone, which is what they meant before the
            // tags existed — a prompt written back then reads the way it was written. One that wants
            // the tag asks for it with {source_code} / {target_code}.
            Assert.Equal("auto: into Traditional Chinese", automatic.User);
            Assert.Equal("chosen: Japanese->Traditional Chinese", chosen.User);
        });
    }

    // The point of splitting the tag out of the name: a prompt can place it wherever its own model
    // expects it, including TranslateGemma's documented wording, which this application does not ship
    // as its default because a longer sentence is a cost paid once per recognised block.
    [Fact]
    public void BuildPrompts_LetAPromptPlaceTheLanguageTagItself()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            Assert.Equal(
                "You are a professional Japanese (ja) to English (en) translator.",
                Written(
                    "JA",
                    "You are a professional {source} ({source_code}) to {target} ({target_code}) translator.",
                    "EN-US"));
        });
    }

    // {source} / {target} were the names before the tags gained placeholders of their own. A prompt
    // written back then is sitting in someone's settings file, and dropping the pair would send the
    // model a literal "{source}" instead of a language.
    [Fact]
    public void BuildPrompts_StillFillThePlaceholderNamesTheyUsedToAdvertise()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            Assert.Equal(
                "from Japanese to English",
                Written("JA", "from {source} to {target}", "EN-US"));
        });
    }

    [Fact]
    public void BuildPrompts_LetAPromptUseTheTagWithoutTheName()
    {
        Assert.Equal("ja->zh-Hant", Written("JA", "{source_code}->{target_code}"));
    }

    // 自動 has no language to name, so it has no tag either. A prompt that asks for one anyway is
    // left with whatever brackets it wrote around it rather than a leaked placeholder.
    [Fact]
    public void BuildPrompts_EmptyTheSourceTagWhenTheSourceIsAutomatic()
    {
        var prompt = Written("AUTO", "[{source_code}]{target_code}");

        Assert.Equal("[]zh-Hant", prompt);
        Assert.DoesNotContain("{source_code}", prompt);
    }

    // A prompt written for a chosen source language, used while the source is 自動, would otherwise
    // send the model a literal "{source}".
    [Fact]
    public void BuildPrompts_FillTheSourcePlaceholderEvenWhenTheSourceIsAutomatic()
    {
        WithInterfaceLanguage(LocalizationService.English, () =>
        {
            var prompt = Written("AUTO", "from {source} into {target}");

            Assert.Equal("from any language into Traditional Chinese", prompt);
            Assert.DoesNotContain("{source}", prompt);
        });
    }

    // How the two halves become a request is OpenAiChatProtocolTests'.

    /// <summary>One message's content, off the anonymous type the payload is built from.</summary>
    private static string? Content(object message) =>
        (string?)message.GetType().GetProperty("content")!.GetValue(message);

    // ── Which setting of the list gets used ──────────────────────────────────

    [Fact]
    public void Profiles_ResolveTheOneTheUserPicked()
    {
        var openAi = new OpenAiSettings();
        openAi.Profiles.Add(new OpenAiModelProfile { Id = "a", Name = "第一個", Model = "model-a" });
        openAi.Profiles.Add(new OpenAiModelProfile { Id = "b", Name = "第二個", Model = "model-b" });
        openAi.SelectedProfileId = "b";

        Assert.Equal("model-b", openAi.SelectedProfile()?.Model);
    }

    /// <summary>
    /// An id naming a profile that is no longer there falls back to the built-in setting.
    /// </summary>
    /// <remarks>
    /// Reachable by hand-editing the settings file, and the alternative is a provider with no model
    /// and no instruction to send at all.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("deleted-profile")]
    public void Profiles_FallBackToTheBuiltInWhenNothingAnswersToTheStoredId(string id)
    {
        var settings = new AppSettings();
        settings.OpenAi.Profiles.Add(new OpenAiModelProfile { Id = "a", Model = "model-a" });
        settings.OpenAi.SelectedProfileId = id;

        Assert.Null(settings.OpenAi.SelectedProfile());

        var options = OpenAiCompatibleProvider.FromSettings(settings);
        var built = OpenAiCompatibleProvider.BuiltInProfile();

        Assert.Equal(built.Model, options.Model);
        Assert.Equal(built.Auto.UserPrompt, options.PromptsFor(automatic: true).UserPrompt);
    }

    /// <summary>
    /// The whole setting travels together: model, temperature and both prompt pairs.
    /// </summary>
    /// <remarks>
    /// The reason the list was reshaped from prompts into profiles. A prompt is written for a model,
    /// at a temperature that model wants, and picking one of the three without the other two is how
    /// a setup comes to disagree with itself.
    /// </remarks>
    [Fact]
    public void Profiles_CarryTheModelAndTheTemperatureWithTheirPrompts()
    {
        var settings = new AppSettings();
        settings.OpenAi.Profiles.Add(new OpenAiModelProfile
        {
            Id = "a",
            Name = "測試",
            Model = "model-a",
            TemperatureEnabled = false,
            Temperature = 0.7,
            TopPEnabled = true,
            TopP = 0.9,
            SeedEnabled = false,
            Seed = 7,
            Auto = new OpenAiPromptPair { SystemPrompt = "auto-system", UserPrompt = "auto-user" },
            Explicit = new OpenAiPromptPair { UserPrompt = "chosen-user" },
        });
        settings.OpenAi.SelectedProfileId = "a";

        var options = OpenAiCompatibleProvider.FromSettings(settings);

        Assert.Equal("model-a", options.Model);
        Assert.False(options.SendTemperature);
        Assert.Equal(0.7, options.Temperature);
        Assert.True(options.SendTopP);
        Assert.Equal(0.9, options.TopP);
        Assert.False(options.SendSeed);
        Assert.Equal(7, options.Seed);
        Assert.Equal("auto-system", options.PromptsFor(automatic: true).SystemPrompt);
        Assert.Equal("auto-user", options.PromptsFor(automatic: true).UserPrompt);
        Assert.Equal("", options.PromptsFor(automatic: false).SystemPrompt);
        Assert.Equal("chosen-user", options.PromptsFor(automatic: false).UserPrompt);
    }

    /// <summary>
    /// The built-in setting names the model the guide tells the user to install.
    /// </summary>
    /// <remarks>
    /// Not the fallback that was removed: that one filled a box nobody could see, and this is drawn
    /// in 目前使用的設定 the moment the built-in row is selected. Pinned because the two names have
    /// gone out of step once already — see <see cref="OpenAiCompatibleProvider.RecommendedModel"/>,
    /// which has to keep naming the same build as docs/guides/OLLAMA_GUIDE.*.md.
    /// </remarks>
    [Fact]
    public void BuiltIn_NamesTheRecommendedModel()
    {
        Assert.Equal(
            OpenAiCompatibleProvider.RecommendedModel,
            OpenAiCompatibleProvider.BuiltInProfile().Model);
    }

    [Fact]
    public async Task TranslateAsync_SendsOneRequestPerBlockAndPreservesOrderAndBounds()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", "secret-key"));
        var blocks = new List<OcrTextBlock>
        {
            new("first", new Rect(1, 2, 30, 40)),
            new("second", new Rect(5, 6, 70, 80)),
        };

        var (translated, detected) = await provider.TranslateAsync(
            blocks, "EN", "ZH-HANT", "ignored-provider-key");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, handler.MaxConcurrentRequests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("http://localhost:1234/v1/chat/completions", request.Url);
            Assert.Equal("Bearer secret-key", request.Authorization);
            using var payload = JsonDocument.Parse(request.Body);
            Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetInt32());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        });
        Assert.Equal(["translated:first", "translated:second"],
            translated.Select(block => block.TranslatedText));
        Assert.Equal(blocks[0].Bounds, translated[0].Bounds);
        Assert.Equal(blocks[1].Bounds, translated[1].Bounds);
        Assert.Equal("EN", detected);
    }

    /// <summary>
    /// An empty model box fails before anything is sent, rather than falling back to a name.
    /// </summary>
    /// <remarks>
    /// This provider talks to whatever server the user pointed it at, so any name shipped here is a
    /// guess about someone else's Ollama. A wrong guess reaches the user as the server's "model not
    /// found" instead of as the field they still have to fill in, which is the worse of the two
    /// errors — it names the model as the problem rather than the setting.
    ///
    /// Asserting no request left as well as the throw: falling back silently and failing at the
    /// server would still throw, and the point is that nothing is sent.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TranslateAsync_RefusesToSendWhenTheModelBoxIsEmpty(string model)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:1234/v1", model));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", ""));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_LeavesTemperatureOutWhenItIsTurnedOff()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", SendTemperature: false, Temperature: 0.7));

        await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(payload.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task TranslateAsync_SendsTheConfiguredTemperature()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", Temperature: 0.7));

        await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(0.7, payload.RootElement.GetProperty("temperature").GetDouble());
    }

    /// <summary>
    /// Each sampling parameter is sent under the name the API gives it, and only when it is on.
    /// </summary>
    /// <remarks>
    /// One test over all three rather than six, because the mistake they are here to catch is
    /// per-parameter rather than per-case: a switch wired to the wrong value, or a field sent under
    /// the wrong key. <c>top_p</c> in particular is easy to write as <c>topP</c> from the C# side,
    /// and a server that does not recognise a field ignores it — the symptom is not an error but a
    /// setting that silently does nothing.
    /// </remarks>
    [Fact]
    public async Task TranslateAsync_SendsEachSamplingParameterOnlyWhileItIsEnabled()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model",
                SendTemperature: true, Temperature: 0.2,
                SendTopP: true, TopP: 0.6,
                SendSeed: true, Seed: 42));

        await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        using var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(0.2, sent.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.6, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(42, sent.RootElement.GetProperty("seed").GetInt32());

        var off = new RecordingHandler();
        using var offHttp = new HttpClient(off);
        var quiet = new OpenAiCompatibleProvider(
            offHttp,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model",
                SendTemperature: false, Temperature: 0.2,
                SendTopP: false, TopP: 0.6,
                SendSeed: false, Seed: 42));

        await quiet.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "");

        using var none = JsonDocument.Parse(Assert.Single(off.Requests).Body);
        Assert.False(none.RootElement.TryGetProperty("temperature", out _));
        Assert.False(none.RootElement.TryGetProperty("top_p", out _));
        Assert.False(none.RootElement.TryGetProperty("seed", out _));

        // Nothing else went with them: the request is still a model, its messages and the stream flag.
        Assert.Equal(3, none.RootElement.EnumerateObject().Count());
    }

    /// <summary>
    /// The built-in setting sends the three numbers a new setting opens on.
    /// </summary>
    /// <remarks>
    /// Two places carry these defaults — the initialisers on the profile and
    /// <see cref="OpenAiCompatibleProvider.BuiltInProfile"/> — and they are the same constants on
    /// purpose. Pinned because the failure is silent: 新增設定 would open on numbers different from
    /// the setting it was copied from, and nobody would be told which of the two the model got.
    /// </remarks>
    [Fact]
    public void BuiltIn_SendsTheSameNumbersANewSettingOpensOn()
    {
        var built = OpenAiCompatibleProvider.BuiltInProfile();
        var fresh = new OpenAiModelProfile();

        Assert.True(built.TemperatureEnabled);
        Assert.True(built.TopPEnabled);
        Assert.True(built.SeedEnabled);

        Assert.Equal(OpenAiModelProfile.DefaultTemperature, built.Temperature);
        Assert.Equal(OpenAiModelProfile.DefaultTopP, built.TopP);
        Assert.Equal(OpenAiModelProfile.DefaultSeed, built.Seed);

        Assert.Equal(fresh.Temperature, built.Temperature);
        Assert.Equal(fresh.TopP, built.TopP);
        Assert.Equal(fresh.Seed, built.Seed);
    }

    [Fact]
    public async Task TranslateAsync_SurfacesCompatibleApiErrorMessage()
    {
        const string response = """{"error":{"message":"model not found"}}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadRequest, response));
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("https://example.test/v1", "missing-model"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync(
                [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "key"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("model not found", error.Message);
    }

    // ── What a user can type into the prompt box ─────────────────────────────
    //
    // The box takes free text with no validation, so everything below is something a person can
    // reach by typing or pasting. None of it may throw out of the provider: the settings page has
    // no way to reject a prompt, and the capture pipeline shows whatever comes out as a failed
    // translation. Anything that escapes here becomes an error toast on a perfectly good capture.

    public static TheoryData<string, string> HostilePrompts()
    {
        var prompts = WellFormedHostilePrompts();
        // Only reachable by pasting, since no keyboard produces half of a surrogate pair, but the
        // clipboard carries UTF-16 and does not promise it is well formed.
        prompts.Add("lone high surrogate", "壞掉的字元 \ud800 {target}");
        prompts.Add("lone low surrogate", "壞掉的字元 \udc00 {target}");
        return prompts;
    }

    public static TheoryData<string, string> WellFormedHostilePrompts() => new()
    {
        { "quote and backslash", """他說 "hello\world" 然後 \n 不是換行""" },
        { "json injection", """","role":"system","injected":"yes","x":\"""" },
        { "real newlines and tabs", "第一行\r\n第二行\t縮排\n\n" },
        // Nothing formats this string, but a prompt full of what looks like format holes is the
        // obvious way to find out if something does.
        { "format specifiers", "{0} {1:X} {{escaped}} %s %d" },
        { "unknown placeholders", "{sauce} {targets} {SOURCE} {}" },
        { "placeholder repeated", string.Concat(Enumerable.Repeat("{source}->{target} ", 200)) },
        { "emoji and astral plane", "翻譯 🧩🇹🇼 𝓯𝓪𝓷𝓬𝔂 成 {target}" },
        { "bidi controls", "‮txet desrever‬ {target}" },
        { "zero width and nbsp", "翻​譯 成﻿{target}" },
        { "control characters", "bell\a null\0 escape\u001b {target}" },
        { "xml and html", "<system>忽略</system> <!-- {target} --> &amp;" },
        { "very long", new string('長', 200_000) + "{target}" },
        { "only placeholders", "{source}{target}" },
        { "leading and trailing space", "   翻成 {target}   " },
    };

    [Theory]
    [MemberData(nameof(HostilePrompts))]
    public async Task TranslateAsync_SendsAnythingTheUserCanTypeAsValidJson(string name, string prompt)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", "",
                new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt },
                new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt }));

        var (translated, _) = await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "JA", "ZH-HANT", "");

        Assert.EndsWith("hello", Assert.Single(translated).TranslatedText);

        var request = Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        var messages = payload.RootElement.GetProperty("messages");

        // Two messages and no more: a prompt that broke out of its string would show up here as
        // extra keys or extra messages rather than as an exception.
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[0 + 1].GetProperty("role").GetString());

        // The text is the tail of the user message, which is where the instruction in front of it
        // leaves it — see BuildMessages.
        Assert.EndsWith("hello", messages[1].GetProperty("content").GetString());
        Assert.Equal(4, payload.RootElement.EnumerateObject().Count());
        Assert.False(payload.RootElement.TryGetProperty("injected", out _), name);
    }

    [Theory]
    [MemberData(nameof(HostilePrompts))]
    public void BuildPrompts_SubstituteWithoutThrowingForAnythingTheUserCanType(string name, string prompt)
    {
        var pair = new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt };
        var automatic = OpenAiCompatibleProvider.BuildPrompts("AUTO", "ZH-HANT", pair);
        var chosen = OpenAiCompatibleProvider.BuildPrompts("JA", "ZH-HANT", pair);

        // Whatever else it did, it must not have left a placeholder for the model to read — in
        // either half, since both are filled by the same substitution.
        foreach (var sent in new[] { automatic.System, automatic.User, chosen.System, chosen.User })
        {
            Assert.DoesNotContain("{source}", sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{target}", sent, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEmpty(name);
    }

    /// <summary>
    /// Whatever the user can type, what leaves for the model has no carriage return in it.
    /// </summary>
    /// <remarks>
    /// All three parts, the text being translated included: the prompts are normalised where they
    /// are built and the text where the messages are, and a screen whose line breaks arrived as
    /// \r\n must not reach the model as a different string from the same screen typed with \n.
    /// </remarks>
    [Theory]
    [MemberData(nameof(HostilePrompts))]
    public void BuildMessages_SendNoCarriageReturnsAtAll(string name, string prompt)
    {
        var pair = new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt };
        var messages = OpenAiChatTranslator.BuildMessages(
            OpenAiCompatibleProvider.BuildPrompts("JA", "ZH-HANT", pair),
            "first line\r\nsecond line\rthird line");

        foreach (var message in messages)
            Assert.DoesNotContain("\r", Content(message));

        Assert.Contains("first line\nsecond line\nthird line", Content(messages[^1]));
        Assert.NotEmpty(name);
    }

    // The prompt goes out to disk as well as over the wire, and a settings file that will not parse
    // costs the user every other setting in it, not just the prompt.
    [Theory]
    [MemberData(nameof(WellFormedHostilePrompts))]
    public void Settings_RoundTripAnythingTheUserCanType(string name, string prompt)
    {
        var written = new AppSettings();
        written.OpenAi.Profiles.Add(new OpenAiModelProfile
        {
            Id = "a",
            Name = name,
            Model = prompt,
            Auto = new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt },
            Explicit = new OpenAiPromptPair { SystemPrompt = prompt, UserPrompt = prompt },
        });

        var json = SettingsService.Serialize(written);
        var read = SettingsService.Parse(json);

        var profile = Assert.Single(read.OpenAi.Profiles);
        Assert.Equal(prompt, profile.Model);
        Assert.Equal(prompt, profile.Auto.SystemPrompt);
        Assert.Equal(prompt, profile.Auto.UserPrompt);
        Assert.Equal(prompt, profile.Explicit.SystemPrompt);
        Assert.Equal(prompt, profile.Explicit.UserPrompt);
        Assert.NotEmpty(name);
    }

    /// <summary>
    /// Half a surrogate pair comes back as the replacement character — lossy exactly there, and
    /// nowhere else in the prompt.
    /// </summary>
    /// <remarks>
    /// Pinned because the alternative is far worse than a mangled character: a writer that threw
    /// here would take the whole settings file with it, and the prompt shares that file with the
    /// API key and the shortcuts. Half a pair cannot be typed, only pasted, and the cost is a
    /// character the user can see and correct on the page they pasted it into.
    ///
    /// How many replacement characters one broken one becomes is the serializer's business, so the
    /// assertions are that the surrounding text survives and that nothing malformed gets through.
    /// </remarks>
    [Theory]
    [InlineData("壞掉的字元 \ud800 尾巴")]
    [InlineData("壞掉的字元 \udc00 尾巴")]
    public void Settings_ReplaceMalformedUtf16RatherThanFailingToSave(string prompt)
    {
        var written = new AppSettings();
        written.OpenAi.Profiles.Add(new OpenAiModelProfile
        {
            Id = "a",
            Name = "壞掉的",
            Auto = new OpenAiPromptPair { UserPrompt = prompt },
        });

        var read = SettingsService.Parse(SettingsService.Serialize(written));

        var template = Assert.Single(read.OpenAi.Profiles).Auto.UserPrompt;
        Assert.StartsWith("壞掉的字元 ", template);
        Assert.EndsWith(" 尾巴", template);
        Assert.Contains('�', template);
        Assert.DoesNotContain(template, char.IsSurrogate);
    }

    // ── What reaches the user when a prompt makes the model answer badly ─────
    //
    // Both callers put ex.Message straight into the text they show — the capture toast and the
    // translation window's status line — so these are the words on screen.

    [Fact]
    public async Task EmptyAnswerSurfacesAsTheNoTranslationMessage()
    {
        const string response = """{"choices":[{"message":{"content":"<think>只想不答</think>"}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));
        var provider = new OpenAiCompatibleProvider(
            http, () => new OpenAiCompatibleOptions("http://localhost:11434/v1", "local-model"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "JA", "ZH-HANT", ""));

        Assert.Equal(LocalizationService.Get("S.Error.OpenAiNoTranslation"), error.Message);
    }

    /// <summary>
    /// One bad block fails the whole capture, and the message still has to name the cause.
    /// </summary>
    /// <remarks>
    /// The blocks go out in parallel, and what that does to an exception on the way out is what
    /// decides whether the toast names the problem or talks about one or more errors occurring.
    /// </remarks>
    [Fact]
    public async Task ABadAnswerInABatchStillNamesItself()
    {
        const string response = """{"choices":[{"message":{"content":""}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));
        var provider = new OpenAiCompatibleProvider(
            http, () => new OpenAiCompatibleOptions("http://localhost:11434/v1", "local-model"));
        var blocks = Enumerable.Range(0, 12)
            .Select(index => new OcrTextBlock($"block-{index}", new Rect()))
            .ToList();

        // These blocks are translated on thread-pool threads, and with no Application in a test run
        // the very first string lookup is what builds the fallback dictionary — a XamlParseException
        // waiting to happen on whichever thread gets there first. The running app always has an
        // Application, so it never takes that path; this stands in for it.
        var expected = LocalizationService.Get("S.Error.OpenAiNoTranslation");

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.TranslateAsync(blocks, "JA", "ZH-HANT", ""));

        // Not wrapped in an aggregate: the toast names the problem instead of reporting that one or
        // more errors occurred.
        Assert.Equal(expected, error.Message);
        Assert.IsType<InvalidOperationException>(error);
    }

    // A prompt long enough to blow the model's context window is rejected by the server, not here,
    // so what the user reads is the status and the server's own words.
    [Fact]
    public async Task ARejectedRequestSurfacesTheServersOwnWords()
    {
        const string response = """{"error":{"message":"input length exceeds context length"}}""";
        using var http = new HttpClient(
            new StaticResponseHandler(HttpStatusCode.BadRequest, response));
        var provider = new OpenAiCompatibleProvider(
            http, () => new OpenAiCompatibleOptions("http://localhost:11434/v1", "local-model"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "JA", "ZH-HANT", ""));

        Assert.Equal(
            LocalizationService.Format("S.Error.OpenAiHttp", 400, "input length exceeds context length"),
            error.Message);
    }

    /// <summary>
    /// A refusal with nothing to quote still says so, in words rather than as an empty gap.
    /// </summary>
    /// <remarks>
    /// The server's words now arrive from the library apart from the status, and null and empty are
    /// its two ways of having none — each has its own message, as before the request moved there.
    /// </remarks>
    [Theory]
    [InlineData("""{"error":{"message":null}}""", "S.Error.UnknownError")]
    [InlineData("", "S.Error.NoErrorContent")]
    public async Task ARefusalWithNothingToQuoteSaysSo(string body, string key)
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadGateway, body));
        var provider = new OpenAiCompatibleProvider(
            http, () => new OpenAiCompatibleOptions("http://localhost:11434/v1", "local-model"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "JA", "ZH-HANT", ""));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(LocalizationService.Format("S.Error.OpenAiHttp", 502, LocalizationService.Get(key)), error.Message);
    }

    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    [Theory]
    [InlineData(null, 60)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(90, 90)]
    public void TimeoutSecondsFor_EmptyIsTheDefaultAndTooShortIsTheMinimum(int? saved, int expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.TimeoutSecondsFor(saved));
    }

    /// <summary>
    /// A server that does not answer in time reaches the user as a timeout naming the seconds, not
    /// as a cancellation — the capture and the realtime loop both swallow those in silence.
    /// </summary>
    [Fact]
    public async Task TranslateAsync_ReportsATimeoutRatherThanACancellation()
    {
        using var http = new HttpClient(new NeverAnsweringHandler());
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:1234/v1", "test-model", TimeoutSeconds: 1));

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            provider.TranslateAsync([new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", ""));

        Assert.Contains("1", error.Message);
        Assert.IsType<OpenAiChatException>(error.InnerException);
    }

    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maxConcurrentRequests;

        public ConcurrentBag<RecordedRequest> Requests { get; } = [];
        public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var activeRequests = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(activeRequests);
            try
            {
                await Task.Delay(10, cancellationToken);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Requests.Add(new RecordedRequest(
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.Authorization?.ToString(),
                    body));

                using var payload = JsonDocument.Parse(body);

                // The last message rather than the second: a setting with no system prompt sends one
                // message, which is what the one this application ships with does.
                var messages = payload.RootElement.GetProperty("messages");
                var userText = messages[messages.GetArrayLength() - 1]
                    .GetProperty("content").GetString();
                var response = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                content = $"<think>hidden</think>translated:{userText}",
                            },
                        },
                    },
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maxConcurrentRequests);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maxConcurrentRequests, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
    }
}
