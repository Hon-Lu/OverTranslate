namespace OverTranslate.Models;

/// <summary>What 本機 AI 翻譯 keeps, separately from the OpenAI-compatible service.</summary>
public class LocalAiSettings
{
    /// <summary>The idle times the settings window offers, in minutes.</summary>
    public static readonly IReadOnlyList<int> IdleChoices = [1, 5, 10, 15, 30, 45, 60];

    public const int DefaultIdleMinutes = 10;

    /// <summary>The model in use, by its id in the model list; empty until one has been downloaded.</summary>
    public string ModelId { get; set; } = "";

    /// <summary>Whether a screen goes as few requests as fit rather than a text at a time.</summary>
    public bool Batch { get; set; }

    /// <summary>The optional request about how translations should read.</summary>
    public string Style { get; set; } = "";

    /// <summary>How long the model may go unused before it is closed.</summary>
    public int IdleMinutes { get; set; } = DefaultIdleMinutes;

    /// <summary>How long one request may take once the model is up; null for the default.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>The idle time to use: a value the window does not offer is read as the default.</summary>
    public int EffectiveIdleMinutes => IdleChoices.Contains(IdleMinutes) ? IdleMinutes : DefaultIdleMinutes;
}
