using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <summary>
/// Between the codes the application stores (DeepL's: EN, ZH-HANT, PT-BR) and the ones the free
/// engines are spoken to in (Google's: en, zh-TW, pt).
/// </summary>
/// <remarks>
/// One table for every free engine, because <see cref="OverTranslate.Translation"/> takes Google's codes
/// from all of its callers — translation, speech and dictionary lookups alike — and translates them
/// for each engine itself.
/// </remarks>
internal static class EngineLanguage
{
    private static readonly Dictionary<string, string> ToEngineCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "EN",      "en"    }, { "EN-US",   "en"    }, { "EN-GB",   "en"    },
        { "ZH",      "zh-CN" }, { "ZH-HANS", "zh-CN" }, { "ZH-HANT", "zh-TW" },
        { "PT",      "pt"    }, { "PT-BR",   "pt"    }, { "PT-PT",   "pt"    },
        { "DE",      "de"    }, { "FR",      "fr"    }, { "ES",      "es"    },
        { "IT",      "it"    }, { "NL",      "nl"    }, { "PL",      "pl"    },
        { "RU",      "ru"    }, { "JA",      "ja"    }, { "KO",      "ko"    },
        { "CS",      "cs"    }, { "DA",      "da"    }, { "EL",      "el"    },
        { "ET",      "et"    }, { "FI",      "fi"    }, { "HU",      "hu"    },
        { "ID",      "id"    }, { "LT",      "lt"    }, { "LV",      "lv"    },
        { "NB",      "no"    }, { "RO",      "ro"    }, { "SK",      "sk"    },
        { "SL",      "sl"    }, { "SV",      "sv"    }, { "TR",      "tr"    },
        { "UK",      "uk"    }, { "BG",      "bg"    },
    };

    /// <summary>An application code as the engines want it.</summary>
    public static string ToEngine(string code) =>
        ToEngineCodes.TryGetValue(code, out var mapped) ? mapped : code.ToLowerInvariant().Split('-')[0];

    /// <summary>The source language to send, or null for 自動 — which is sent as nothing at all.</summary>
    public static string? SourceToEngine(string code) =>
        LanguageData.IsAutomaticSource(code) ? null : ToEngine(code);

    /// <summary>
    /// A language an engine detected, as one of the application's own source codes.
    /// </summary>
    /// <remarks>
    /// Chinese comes back as the two codes the source list actually holds — ZH and ZH-HANT — rather
    /// than as ZH-CN and ZH-TW, which is what GTranslate's results used to turn into here and which
    /// neither the language lists nor speech knew. Every engine reports its Chinese as simplified
    /// unless it saw otherwise, so ZH is what most of it will be.
    /// </remarks>
    public static string FromEngine(string detected)
    {
        if (string.IsNullOrEmpty(detected)) return "";

        return detected.ToLowerInvariant() switch
        {
            "zh-tw" or "zh-hant" or "zh-hk" => "ZH-HANT",
            var zh when zh == "zh" || zh.StartsWith("zh-", StringComparison.Ordinal) => "ZH",
            "no" or "nb" or "nn" => "NB",
            var other => other.Split('-')[0].ToUpperInvariant(),
        };
    }
}
