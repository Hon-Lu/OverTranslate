using System.Text.Json.Nodes;

namespace OverTranslate.Models;

/// <summary>
/// Carries the prompts a profile was saved with into <see cref="OpenAiModelProfile.ExtraInstructions"/>.
/// </summary>
/// <remarks>
/// A profile used to hold four prompts — a system and a user message for 自動 and for 指定語言 — and
/// now holds one box that is appended to the built-in rules. What the user wrote is kept rather than
/// dropped, on the owner's call: a box nobody edited is the built-in wording and goes, and anything
/// else is the user's own words and comes along, however redundant it now is beside the rules.
///
/// Read from the file rather than from the profile, because the old properties are not part of the
/// profile any more once the editor stops using them. Done once per profile: a profile that already
/// has the new key was saved by a build that knows it, and is left alone.
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
    /// these are what was shipped, and the provider stops shipping any of them.
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
    /// Fills in <see cref="OpenAiModelProfile.ExtraInstructions"/> for every profile in
    /// <paramref name="openAi"/> that was saved with the old prompts.
    /// </summary>
    /// <param name="stored">The <c>OpenAi</c> group as the file has it, or null when there is none.</param>
    /// <remarks>
    /// Matched by position: <see cref="OpenAiSettings.Profiles"/> was read from this same array, so
    /// the two line up unless the array could not be read at all — and then there is nothing to
    /// match against, which the count check covers.
    /// </remarks>
    public static void Apply(OpenAiSettings openAi, JsonObject? stored)
    {
        if (stored?[nameof(OpenAiSettings.Profiles)] is not JsonArray array
            || array.Count != openAi.Profiles.Count)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject source
                || source.ContainsKey(nameof(OpenAiModelProfile.ExtraInstructions)))
                continue;

            var extra = ExtraInstructionsFrom(
                Prompt(source, "Auto", "SystemPrompt"),
                Prompt(source, "Auto", "UserPrompt"),
                Prompt(source, "Explicit", "SystemPrompt"),
                Prompt(source, "Explicit", "UserPrompt"));

            var profile = openAi.Profiles[i];
            profile.ExtraInstructions = extra;
            profile.ShowMigrationNotice = extra.Length > 0;
        }
    }

    /// <summary>
    /// The old prompts as one block of extra instructions: the built-in ones and the empty ones
    /// dropped, repeats dropped, the rest joined by a blank line.
    /// </summary>
    /// <param name="prompts">
    /// In the order they are carried: 自動 system, 自動 user, 指定語言 system, 指定語言 user.
    /// </param>
    /// <remarks>
    /// Compared with line breaks made bare and the ends trimmed, so a prompt that differs from the
    /// built-in one only in the blank line it ends with counts as the built-in one — that blank line
    /// was there to separate the instruction from the text, and the new format does not need it.
    /// </remarks>
    public static string ExtraInstructionsFrom(params string?[] prompts)
    {
        var kept = new List<string>();
        foreach (var prompt in prompts)
        {
            var text = OpenAiSettings.NormaliseLineBreaks(prompt ?? "").Trim();
            if (text.Length == 0 || RetiredBuiltInPrompts.Contains(text) || kept.Contains(text))
                continue;

            kept.Add(text);
        }

        return string.Join("\n\n", kept);
    }

    /// <summary>One of the old prompt strings, or null when the file does not have it as a string.</summary>
    private static string? Prompt(JsonObject profile, string pair, string half) =>
        profile[pair] is JsonObject prompts
        && prompts[half] is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
