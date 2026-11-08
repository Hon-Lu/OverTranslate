namespace OverTranslate.Services.LocalAi;

/// <summary>
/// The one thing that differs between models: the conversation markers around the message.
/// </summary>
/// <remarks>
/// <para>The text goes to <c>/completion</c> with the markers written here rather than through a
/// model's own chat template, because the templates are what cannot be relied on: the official
/// Hy-MT2 1.8B one drops the user's text altogether. Each marker was confirmed to come out as a
/// single token through <c>/tokenize</c>, with one BOS at the start.</para>
///
/// <para>Which format a model wants is written in the model list, by kind, so nothing here or
/// anywhere else branches on a model's name.</para>
/// </remarks>
internal static class LocalAiPromptFormat
{
    private const string Message = "{msg}";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        // The model turn is opened with nothing in it, which leaves thinking off: thinking is what
        // a <|think|> in the system turn asks for, and there is no system turn.
        ["gemma-4"] = "<|turn>user\n" + Message + "<turn|>\n<|turn>model\n",
        ["hy-mt2-7b"] = "<|startoftext|>" + Message + "<|extra_0|>",
        ["hy-mt2-1.8b"] = "<｜hy_begin▁of▁sentence｜><｜hy_User｜>" + Message + "<｜hy_Assistant｜>",
    };

    internal static bool IsKnown(string? format) => format is not null && Formats.ContainsKey(format);

    /// <summary><paramref name="message"/> as <paramref name="format"/> wraps it.</summary>
    internal static string Wrap(string format, string message)
    {
        var template = Formats[format];
        var at = template.IndexOf(Message, StringComparison.Ordinal);
        return string.Concat(template.AsSpan(0, at), message, template.AsSpan(at + Message.Length));
    }
}
