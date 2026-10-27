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
public sealed record OpenAiScreenLine(string Source, string? Translation = null);

/// <summary>
/// The one user message every request is sent: a template, its <c>{context}</c> and <c>{text}</c>
/// filled in.
/// </summary>
/// <param name="Template">
/// The whole message, the language placeholders already filled in — the application's business.
/// <c>{context}</c> and <c>{text}</c> are left for <see cref="Build"/>.
/// </param>
/// <param name="TargetLabel">The target language as the context's headings name it: 繁體中文.</param>
/// <param name="IsDefault">
/// Whether <paramref name="Template"/> is the application's own — see <see cref="DefaultTemplate"/>.
/// Only an answer to that one is checked and sent again: what the user's own asks for is theirs to
/// say, and a check written for the application's would be wrong about it.
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
/// <para>The context is XML blocks, each under a one-sentence heading, a line to each entry: the
/// source in <c>&lt;s&gt;</c>, its translation after it in <c>&lt;t&gt;</c>. Tags rather than one line
/// for the source and one for the translation, so that two lines on screen alike and both still
/// untranslated cannot be read as a line and its translation. Measured against the labelled pairs
/// this replaced, the application's wording was judged better in a blind test on screenshots and
/// realtime alike. Two things about it were what that took: short headings, and the instruction
/// last — a longer heading and instruction made a line holding only a speaker's name come back with
/// the next line's translation in it, a third of the time.</para>
///
/// <para>The Traditional Chinese wording is the one that was measured, word for word. The other two
/// say the same thing.</para>
/// </remarks>
public sealed record OpenAiPrompt(
    OpenAiPromptLanguage Language,
    string Template,
    string TargetLabel,
    bool IsDefault = false)
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

    private static readonly Regex Tags = new(
        @"</?(?:s|t|page|history|translated)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether a request carries any context: whether the template has a place for it.</summary>
    public bool HasContext => Template.Contains(ContextPlaceholder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The application's template in <paramref name="language"/>, the target language still a
    /// placeholder: the context, the instruction, the text.
    /// </summary>
    public static string DefaultTemplate(OpenAiPromptLanguage language)
    {
        var words = Wording.For(language);
        return ContextPlaceholder + "\n\n" + words.Lead + words.Instruction + "\n" + TextPlaceholder;
    }

    /// <summary>The user message for <paramref name="text"/>.</summary>
    /// <param name="history">Earlier lines of the same session, oldest first, or null.</param>
    /// <param name="screen">
    /// The lines on screen in screen order, <paramref name="text"/> among them, those translated
    /// before this round with their translation; left out when it is <paramref name="text"/> alone.
    /// </param>
    /// <param name="soFar">This round's texts already translated, in the order they were.</param>
    /// <remarks>
    /// <para>The parts that change least come first: the history (which only grows, and is cut in
    /// half when it is cut at all), the screen as the round found it, then the round's translations
    /// so far, which only grow. A server that reuses the start of the last request reuses all but the
    /// end of it that way — the next text of a round is the last one's request with one more
    /// translation on the end. Filling a round's translations into the screen instead was measured a
    /// quarter slower: every request then differs from the last part way through.</para>
    ///
    /// <para>A context line sits on one line however many it had. The text keeps its line breaks: it
    /// can be paragraphs.</para>
    ///
    /// <para>No context to give empties <c>{context}</c> and the blank lines around it with it, so
    /// the template reads as if it had never had one — and the words right after it that point back
    /// at it, 參考上面，, when they are there.</para>
    /// </remarks>
    public string Build(
        string text,
        IReadOnlyList<OpenAiTranslationPair>? history = null,
        IReadOnlyList<OpenAiScreenLine>? screen = null,
        IReadOnlyList<OpenAiTranslationPair>? soFar = null)
    {
        var words = Wording.For(Language);
        var showScreen = screen is { Count: > 0 } && (screen.Count > 1 || OneLine(screen[0].Source) != OneLine(text));
        return Fill(words, Context(words, history, showScreen ? screen : null, soFar), Bare(text));
    }

    /// <summary>The user message for several texts in one request, read back by number.</summary>
    /// <param name="texts">Two or more texts, in order: numbered from 1.</param>
    /// <param name="history">Earlier lines of the same session, oldest first, or null.</param>
    /// <param name="screen">
    /// The lines on screen in screen order, <paramref name="texts"/> among them, those already
    /// translated with their translation; left out when it is <paramref name="texts"/> and nothing
    /// else, which the numbered texts already show.
    /// </param>
    /// <remarks>
    /// <para>The same template as one text at a time, so a template the user wrote works both ways:
    /// <c>{text}</c> brings its own line saying how to answer, then the texts, one to a line, each in
    /// a tag of its number — <c>&lt;1&gt;…&lt;/1&gt;</c>. <c>{context}</c> is the history and the
    /// screen; there is no round's translations so far, the texts being one request.</para>
    ///
    /// <para>Measured with the application's template: numbered tags with their closing tag came
    /// back as the source again least of the shapes tried, and a format line ahead of the texts no
    /// worse than one after them. Leaving the closing tag out cost fewer tokens to answer but more
    /// texts given back untranslated.</para>
    /// </remarks>
    public string BuildBatch(
        IReadOnlyList<string> texts,
        IReadOnlyList<OpenAiTranslationPair>? history = null,
        IReadOnlyList<OpenAiScreenLine>? screen = null)
    {
        var words = Wording.For(Language);
        var asked = texts.Select(OneLine).ToHashSet();
        var showScreen = screen is { Count: > 0 }
            && screen.Any(line => line.Translation is not null || !asked.Contains(OneLine(line.Source)));
        var body = words.Batch.Replace("{n}", texts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + "\n" + string.Join("\n", texts.Select((text, i) => $"<{i + 1}>{OneLine(text)}</{i + 1}>"));
        return Fill(words, Context(words, history, showScreen ? screen : null, null), body);
    }

    /// <summary>The template with <c>{context}</c> and <c>{text}</c> filled in.</summary>
    private string Fill(Wording words, string context, string text)
    {
        var hasText = false;

        var message = words.Placeholders.Replace(Template, match =>
        {
            if (!match.Groups["before"].Success)
            {
                hasText = true;
                return text;
            }

            var before = match.Groups["before"].Length;
            var after = match.Groups["after"].Length;
            if (context.Length > 0)
                return match.Groups["before"].Value + context + match.Groups["after"].Value
                    + match.Groups["lead"].Value + match.Groups["rest"].Value;

            // At either end of the template there is nothing to keep apart.
            var atStart = match.Index == 0;
            var atEnd = match.Index + match.Length == Template.Length;
            var gap = atStart || atEnd ? "" : new string('\n', Math.Min(2, Math.Max(before, after)));
            return match.Groups["lead"].Success ? gap + CapitalFirst(match.Groups["rest"].Value) : gap;
        });

        return hasText ? message : message + text;
    }

    /// <summary>
    /// Whether <paramref name="answer"/> carries more of the context's tags than
    /// <paramref name="source"/> does: a line of the context, or a tag of it, come back with the
    /// translation.
    /// </summary>
    public static bool LeaksTags(string source, string answer) =>
        Tags.Matches(answer).Count > Tags.Matches(source).Count;

    /// <summary>What <c>{context}</c> becomes: every block that has something in it, or nothing.</summary>
    /// <param name="screen">The screen, or null when it is not to be shown.</param>
    private string Context(
        Wording words,
        IReadOnlyList<OpenAiTranslationPair>? history,
        IReadOnlyList<OpenAiScreenLine>? screen,
        IReadOnlyList<OpenAiTranslationPair>? soFar)
    {
        var blocks = new List<string>();
        var hasHistory = history is { Count: > 0 };
        if (hasHistory)
            blocks.Add(Block(Heading(words.History), "history", history!.Select(Pair)));
        if (screen is { Count: > 0 })
            blocks.Add(Block(Heading(hasHistory ? words.Page : words.PageAlone), "page", screen.Select(line =>
                line.Translation is { } translation ? Pair(new(line.Source, translation)) : Source(line.Source))));
        if (soFar is { Count: > 0 })
            blocks.Add(Block(Heading(words.Translated), "translated", soFar.Select(Pair)));

        return string.Join("\n\n", blocks);
    }

    private string Heading(string heading) => heading.Replace(TargetNamePlaceholder, TargetLabel);

    private static string Block(string heading, string name, IEnumerable<string> entries) =>
        heading + $"\n<{name}>\n" + string.Join("\n", entries) + $"\n</{name}>";

    private static string Pair(OpenAiTranslationPair pair) => Source(pair.Source) + $"<t>{OneLine(pair.Translation)}</t>";

    private static string Source(string source) => $"<s>{OneLine(source)}</s>";

    /// <summary>The first character in upper case: the instruction once its lead is taken off.</summary>
    private static string CapitalFirst(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>Every run of whitespace, line breaks included, as one space.</summary>
    internal static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Every line break as a bare <c>\n</c>.</summary>
    private static string Bare(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>One language's wording. <c>{target_name}</c> is the target language.</summary>
    /// <param name="Lead">
    /// What opens the instruction to point back at the context — taken off with it when there is
    /// none, wherever a template has it right after <c>{context}</c>.
    /// </param>
    /// <param name="Page">The screen's heading under a history, which has said what the tags are.</param>
    /// <param name="PageAlone">The screen's heading with no history before it.</param>
    /// <param name="Batch">What heads the texts of a batch; <c>{n}</c> is how many there are.</param>
    private sealed record Wording(
        string Lead, string Instruction, string History, string Page, string PageAlone, string Translated,
        string Batch)
    {
        public static Wording For(OpenAiPromptLanguage language) => language switch
        {
            OpenAiPromptLanguage.TraditionalChinese => TraditionalChinese,
            OpenAiPromptLanguage.SimplifiedChinese => SimplifiedChinese,
            _ => English,
        };

        /// <summary><c>{context}</c> with the blank lines around it and the lead after it, or <c>{text}</c>.</summary>
        public Regex Placeholders { get; } = new(
            @"(?<before>\n*)\{context\}(?<after>\n*)(?:(?<lead>" + Regex.Escape(Lead) + @")(?<rest>.?))?|\{text\}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Wording TraditionalChinese = new(
            "參考上面，",
            $"將以下文本翻譯為{TargetNamePlaceholder}，只輸出譯文，不加標籤，不要解釋：",
            $"之前的翻譯（<s> 原文，<t> {TargetNamePlaceholder}譯文），只供參考：",
            "目前畫面（沒有 <t> 的還沒翻），只供參考，不用翻譯：",
            $"目前畫面（<s> 原文，<t> {TargetNamePlaceholder}譯文，沒有 <t> 的還沒翻），只供參考，不用翻譯：",
            "這個畫面剛翻好的，只供參考：",
            "（共 {n} 段，每段的譯文照原編號放在 <編號></編號> 裡，一段一行）");

        private static readonly Wording SimplifiedChinese = new(
            "参考上面，",
            $"将以下文本翻译为{TargetNamePlaceholder}，只输出译文，不加标签，不要解释：",
            $"之前的翻译（<s> 原文，<t> {TargetNamePlaceholder}译文），仅供参考：",
            "当前画面（没有 <t> 的还没翻），仅供参考，不用翻译：",
            $"当前画面（<s> 原文，<t> {TargetNamePlaceholder}译文，没有 <t> 的还没翻），仅供参考，不用翻译：",
            "这个画面刚翻好的，仅供参考：",
            "（共 {n} 段，每段的译文照原编号放在 <编号></编号> 里，一段一行）");

        private static readonly Wording English = new(
            "Using the above, ",
            $"translate the following text into {TargetNamePlaceholder}. Output only the translation, with no tags and no explanation:",
            $"Earlier translations (<s> source, <t> {TargetNamePlaceholder} translation), for reference only:",
            "Current screen (lines without <t> are not translated yet), for reference only, do not translate:",
            $"Current screen (<s> source, <t> {TargetNamePlaceholder} translation; lines without <t> are not translated yet), for reference only, do not translate:",
            "Just translated on this screen, for reference only:",
            "({n} parts: put each translation in the tag with its number, <number></number>, one per line.)");
    }
}
