using System.Text.RegularExpressions;

namespace OverTranslate.Translation.OpenAi;

/// <summary>Which language the instruction itself is written in.</summary>
/// <remarks>
/// Three, following the interface: the two Chinese wordings for the two Chinese interfaces and
/// English for every other one, which is what the application decided for the wording it shipped
/// before this one and keeps for this one.
/// </remarks>
public enum OpenAiPromptLanguage
{
    English,
    TraditionalChinese,
    SimplifiedChinese,
}

/// <summary>A line already translated: what was on screen, and what it became.</summary>
public sealed record OpenAiTranslationPair(string Source, string Translation);

/// <summary>A line on screen, and what it became when it already has a translation.</summary>
/// <remarks>
/// Only the lines with a translation reach a request, as translations done before — see
/// <see cref="OpenAiPrompt.Build"/>.
/// </remarks>
public sealed record OpenAiScreenLine(string Source, string? Translation = null);

/// <summary>
/// The one user message every request is sent: a template, its <c>{context}</c> and <c>{text}</c>
/// filled in.
/// </summary>
/// <param name="Template">
/// The whole message, the language placeholders already filled in — the application's business.
/// <c>{context}</c> and <c>{text}</c> are left for <see cref="Build"/>.
/// </param>
/// <param name="TargetLabel">What a translation is labelled with in the context: 繁體中文.</param>
/// <param name="IsDefault">
/// Whether <paramref name="Template"/> is the application's own — see <see cref="DefaultTemplate"/>.
/// Only an answer to that one is read for what the model tends to wrap a translation in: what the
/// user's own asks for is theirs to say.
/// </param>
/// <param name="SourceLabel">
/// What a source line is labelled with in the context: 日文 — or null for a source language the user
/// left to be detected, which the wording's own word for the source stands in for, 原文.
/// </param>
/// <remarks>
/// <para>The same message for every model and every caller — a segment of a screen and the whole of
/// what the text window was given alike. All of it in the user turn and no system message: measured,
/// where the instruction sits made no difference to what came back, and a system turn is what some
/// models' templates have no place for.</para>
///
/// <para>One text to a request, answered with the translation alone and no markup. Measured against
/// a screen in one request read back by numbered tags, the tags were most of what made that request
/// slow, and a screen sent a text at a time — each request carrying the screen and the translations
/// already done — named things as consistently and read at least as well.</para>
///
/// <para>The context is translations done before and nothing else — never the text being asked
/// for, nor lines still waiting their turn: under a one-sentence heading, each pair as two lines
/// labelled with its language, <c>日文：…</c> and <c>繁體中文：…</c>, a blank line between pairs.
/// Measured against the same pairs in <c>&lt;s&gt;</c> and <c>&lt;t&gt;</c> tags inside an XML
/// block, the labels were judged better two to one in a blind test, and a source with those tags in
/// it no longer had them answered with. Texts that collide with the labels — a speaker's line,
/// アリス：…, one that opens with 日文： or 原文：, a label with nothing after it — or that hold tags,
/// braces, quotes or backslashes were measured too, as a pair in the context and as the text: the
/// same pairs as escaped XML, or as one JSON object a line, had as many of them go wrong, and the
/// JSON had its own context given back for a text. The labels stay. Two things about the wording were measured to matter too: a
/// short heading, and the instruction last, straight before the text — a longer heading and
/// instruction made a line holding only a speaker's name come back with the next line's translation
/// in it.</para>
///
/// <para>The Traditional Chinese wording is the one that was measured, word for word. The other two
/// say the same thing.</para>
/// </remarks>
public sealed record OpenAiPrompt(
    OpenAiPromptLanguage Language,
    string Template,
    string TargetLabel,
    bool IsDefault = false,
    string? SourceLabel = null)
{
    /// <summary>Where the context goes; a template without it is sent no context at all.</summary>
    public const string ContextPlaceholder = "{context}";

    /// <summary>
    /// Where the text goes, as it is; a template without it has the text written straight after its
    /// end, as the user prompts this replaces did.
    /// </summary>
    public const string TextPlaceholder = "{text}";

    /// <summary>The placeholder the application's templates name the target language with.</summary>
    public const string TargetNamePlaceholder = "{target_name}";

    /// <summary>Whether a request carries any context: whether the template has a place for it.</summary>
    public bool HasContext => Template.Contains(ContextPlaceholder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The application's template in <paramref name="language"/>, the target language still a
    /// placeholder: the context, the instruction, the text.
    /// </summary>
    /// <param name="batch">
    /// The one for several texts in a request — see <see cref="BuildBatch"/> — rather than the one
    /// for a text at a time. The two differ in their words alone; nothing brings the batch's line
    /// saying how to answer but the template. The batch's instruction says English is translated
    /// too: what a batch gave back untranslated was mostly short English — AUTO, SKIP, a title — and
    /// saying so left a third fewer, on two seeds, with no more texts given back whole; saying it
    /// again in the line left a few fewer still but ran more than 5% slower. The line goes after the
    /// texts because, measured
    /// against the same line ahead of them, that left half as many texts untranslated; a line that
    /// counted the texts did no better than one that does not. A blank line sets it apart from the
    /// texts, and it keeps its brackets: reworded as a sentence — with a colon before the example, or
    /// none — or set off by a rule instead, it had batches answered with their first number alone,
    /// on one seed or both, where the bracketed line after a blank line lost none. Half-width
    /// brackets measured no worse than full-width ones.
    /// </param>
    public static string DefaultTemplate(OpenAiPromptLanguage language, bool batch = false)
    {
        var words = Wording.For(language);
        var instruction = batch ? words.BatchInstruction : words.Instruction;
        var template = ContextPlaceholder + "\n\n" + instruction + "\n" + TextPlaceholder;
        return batch ? template + "\n\n" + words.Batch : template;
    }

    /// <summary>The user message for <paramref name="text"/>.</summary>
    /// <param name="earlier">Translations done before this text, oldest first, or null.</param>
    /// <remarks>
    /// <para>The caller puts them in the order that changes least from one request to the next: the
    /// session's history, then the round's — so the next text of a round is the last one's request
    /// with one more translation on the end, and a server that reuses the start of the last request
    /// reuses all but that end.</para>
    ///
    /// <para>A context line sits on one line however many it had. The text keeps its line breaks: it
    /// can be paragraphs.</para>
    ///
    /// <para>No context to give empties <c>{context}</c> and the blank lines around it with it, so
    /// the template reads as if it had never had one; nothing else in it is touched. That is why the
    /// heading says what the translations are for and the instruction never points back at them.
    /// Measured on every text and every screen sent with no context: a batch whose instruction
    /// pointed at a context that was not there — with nothing above it, or under the heading with
    /// （無） or None below it, or reworded — had batches answered with their first number alone,
    /// where one whose instruction says nothing of it lost none.</para>
    /// </remarks>
    public string Build(string text, IReadOnlyList<OpenAiTranslationPair>? earlier = null) =>
        Fill(Template, Context(Wording.For(Language), earlier), Bare(text));

    /// <summary>The user message for texts in one request, read back by number.</summary>
    /// <param name="texts">The texts, in order: numbered from 1. One is a batch of one.</param>
    /// <param name="earlier">Translations done before these texts, oldest first, or null.</param>
    /// <remarks>
    /// <para><c>{text}</c> is the texts, one to a line, each in a tag of its number —
    /// <c>&lt;1&gt;…&lt;/1&gt;</c> — and nothing else: how to answer them is the template's to say,
    /// as the application's batch template does. One that does not say it — written for a text at
    /// a time — is still answered, a text at a time: what comes back without numbers is sent again
    /// as batches of one, see <see cref="OpenAiChatTranslator.TranslateAsync"/>.</para>
    ///
    /// <para>Measured with the application's template: numbered tags with their closing tag came
    /// back as the source again least of the shapes tried. Leaving the closing tag out cost fewer
    /// tokens to answer but more texts given back untranslated.</para>
    /// </remarks>
    public string BuildBatch(IReadOnlyList<string> texts, IReadOnlyList<OpenAiTranslationPair>? earlier = null)
    {
        var body = string.Join("\n", texts.Select((text, i) => $"<{i + 1}>{OneLine(text)}</{i + 1}>"));
        return Fill(Template, Context(Wording.For(Language), earlier), body);
    }

    /// <summary><paramref name="template"/> with <c>{context}</c> and <c>{text}</c> filled in.</summary>
    private static string Fill(string template, string context, string text)
    {
        var hasText = false;

        var message = Placeholders.Replace(template, match =>
        {
            if (!match.Groups["before"].Success)
            {
                hasText = true;
                return text;
            }

            if (context.Length > 0)
                return match.Groups["before"].Value + context + match.Groups["after"].Value;

            // At either end of the template there is nothing to keep apart.
            var atStart = match.Index == 0;
            var atEnd = match.Index + match.Length == template.Length;
            var gap = Math.Min(2, Math.Max(match.Groups["before"].Length, match.Groups["after"].Length));
            return atStart || atEnd ? "" : new string('\n', gap);
        });

        return hasText ? message : message + text;
    }

    /// <summary><c>{context}</c> with the blank lines around it, or <c>{text}</c>.</summary>
    private static readonly Regex Placeholders = new(
        @"(?<before>\n*)\{context\}(?<after>\n*)|\{text\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What <c>{context}</c> becomes: the translations done before under their heading, or nothing.</summary>
    private string Context(Wording words, IReadOnlyList<OpenAiTranslationPair>? earlier)
    {
        if (earlier is not { Count: > 0 }) return "";

        var source = SourceLabel ?? words.AutomaticSource;
        return words.History + "\n" + string.Join("\n\n", earlier.Select(pair =>
            source + words.Colon + OneLine(pair.Source) + "\n" + TargetLabel + words.Colon + OneLine(pair.Translation)));
    }

    /// <summary>Every run of whitespace, line breaks included, as one space.</summary>
    internal static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Every line break as a bare <c>\n</c>.</summary>
    private static string Bare(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>One language's wording. <c>{target_name}</c> is the target language.</summary>
    /// <param name="History">The heading of the translations done before, saying what they are for.</param>
    /// <param name="BatchInstruction"><paramref name="Instruction"/> for a batch.</param>
    /// <param name="Batch">The line saying how to answer a batch, after its texts.</param>
    /// <param name="AutomaticSource">What a source line is labelled with when its language is detected.</param>
    /// <param name="Colon">What follows a label: the full-width colon in Chinese, a colon and a space in English.</param>
    private sealed record Wording(
        string Instruction, string History, string BatchInstruction, string Batch,
        string AutomaticSource, string Colon)
    {
        public static Wording For(OpenAiPromptLanguage language) => language switch
        {
            OpenAiPromptLanguage.TraditionalChinese => TraditionalChinese,
            OpenAiPromptLanguage.SimplifiedChinese => SimplifiedChinese,
            _ => English,
        };

        private static readonly Wording TraditionalChinese = new(
            $"將以下文本翻譯為{TargetNamePlaceholder}，只輸出譯文，不加標籤，不要解釋：",
            "以下是之前的翻譯，翻譯時可參考：",
            $"將以下文本翻譯為{TargetNamePlaceholder}，英文單字與短語也要翻譯，只輸出譯文，不加標籤，不要解釋：",
            "(依編號輸出譯文，例如 <1>譯文</1>，一段一行)",
            "原文",
            "：");

        private static readonly Wording SimplifiedChinese = new(
            $"将以下文本翻译为{TargetNamePlaceholder}，只输出译文，不加标签，不要解释：",
            "以下是之前的翻译，翻译时可参考：",
            $"将以下文本翻译为{TargetNamePlaceholder}，英文单词与短语也要翻译，只输出译文，不加标签，不要解释：",
            "(按编号输出译文，例如 <1>译文</1>，一段一行)",
            "原文",
            "：");

        private static readonly Wording English = new(
            $"Translate the following text into {TargetNamePlaceholder}. Output only the translation, with no tags and no explanation:",
            "Earlier translations, for reference while translating:",
            $"Translate the following text into {TargetNamePlaceholder}, English words and phrases included. Output only the translation, with no tags and no explanation:",
            "(Output each translation by its number, for example <1>translation</1>, one per line.)",
            "Source",
            ": ");
    }
}
