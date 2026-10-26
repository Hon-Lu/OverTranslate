using System.Text.Json.Nodes;

namespace OverTranslate.Models;

/// <summary>
/// Decides the <see cref="OpenAiModelProfile.Mode"/> of a profile saved before there were modes,
/// from the prompts it was saved with.
/// </summary>
/// <remarks>
/// A profile used to hold four prompts — a system and a user message for 自動 and for 指定語言. One
/// whose prompts are all empty or the built-in wording never said anything of its own, and is put on
/// the built-in instruction. One with anything else is the user's own wording, and keeps it, as it
/// was, on the mode that sends it.
///
/// Read from the file rather than from the profile, because the old properties are not part of the
/// profile. Done once per profile: one that has a mode was saved by a build that knows it.
/// </remarks>
public static class OpenAiPromptMigration
{
    /// <summary>
    /// Every built-in user prompt a stored profile can hold, unfilled.
    /// </summary>
    /// <remarks>
    /// Each interface language's wording, because the built-in prompt followed the interface and a
    /// profile is a copy of whichever one was showing when it was added. Each also in the older
    /// <c>{target}</c> spelling of the placeholder. Frozen here rather than read from the provider:
    /// these are what was shipped.
    /// </remarks>
    internal static readonly IReadOnlyList<string> RetiredBuiltInPrompts =
    [
        .. new[] { "{target_name}", "{target}" }.SelectMany(target => new[]
        {
            $"將以下文本翻譯為{target}，注意只需要輸出翻譯後的結果，不要額外解釋：",
            $"将以下文本翻译为{target}，注意只需要输出翻译后的结果，不要额外解释：",
            $"Translate the following text into {target}. Note that you should only " +
                "output the translated result without any additional explanation:",
        }),
    ];

    /// <summary>
    /// Sets the mode, and the prompts where they are the user's own, for every profile in
    /// <paramref name="openAi"/> that was saved without a mode.
    /// </summary>
    /// <param name="stored">The <c>OpenAi</c> group as the file has it, or null when there is none.</param>
    /// <param name="automaticSource">
    /// Whether the source language is 自動 — which pair to keep when both are the user's own and they
    /// differ: the one that was being sent.
    /// </param>
    /// <remarks>
    /// Matched by position: <see cref="OpenAiSettings.Profiles"/> was read from this same array, so
    /// the two line up unless the array could not be read at all — and then there is nothing to
    /// match against, which the count check covers.
    /// </remarks>
    public static void Apply(OpenAiSettings openAi, JsonObject? stored, bool automaticSource)
    {
        if (stored?[nameof(OpenAiSettings.Profiles)] is not JsonArray array
            || array.Count != openAi.Profiles.Count)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject source || source.ContainsKey(nameof(OpenAiModelProfile.Mode)))
                continue;

            var kept = Kept(Pair(source, "Auto"), Pair(source, "Explicit"), automaticSource);
            var profile = openAi.Profiles[i];
            profile.Mode = kept is null ? OpenAiTranslationMode.Batch : OpenAiTranslationMode.PerSegment;
            profile.Prompts = kept ?? new OpenAiPromptPair();
        }
    }

    /// <summary>
    /// The pair to keep of the two old ones, or null when neither says anything of the user's own.
    /// </summary>
    /// <remarks>
    /// The one that is the user's own when only one is; the one for <paramref name="automaticSource"/>
    /// when both are and they differ. Kept as it is, built-in half and all: it is what was being sent.
    /// </remarks>
    public static OpenAiPromptPair? Kept(OpenAiPromptPair automatic, OpenAiPromptPair chosen, bool automaticSource)
    {
        var autoOwn = IsOwn(automatic);
        var chosenOwn = IsOwn(chosen);
        if (autoOwn && chosenOwn) return automaticSource ? automatic : chosen;
        if (autoOwn) return automatic;
        if (chosenOwn) return chosen;
        return null;
    }

    /// <summary>Whether either half is something other than empty or the built-in wording.</summary>
    /// <remarks>
    /// Compared with line breaks made bare and the ends trimmed, so a prompt that differs from the
    /// built-in one only in the blank line it ends with counts as the built-in one.
    /// </remarks>
    private static bool IsOwn(OpenAiPromptPair pair) =>
        new[] { pair.SystemPrompt, pair.UserPrompt }.Any(prompt =>
        {
            var text = OpenAiSettings.NormaliseLineBreaks(prompt).Trim();
            return text.Length > 0 && !RetiredBuiltInPrompts.Contains(text);
        });

    /// <summary>One of the old pairs as the file has it; a half it does not have as a string is empty.</summary>
    private static OpenAiPromptPair Pair(JsonObject profile, string name) => new()
    {
        SystemPrompt = Prompt(profile, name, "SystemPrompt"),
        UserPrompt = Prompt(profile, name, "UserPrompt"),
    };

    private static string Prompt(JsonObject profile, string pair, string half) =>
        profile[pair] is JsonObject prompts
        && prompts[half] is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : "";
}
