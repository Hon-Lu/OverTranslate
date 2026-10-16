namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Keeps a greedy reading from repeating itself.</summary>
/// <remarks>
/// MEASURED on the shared evaluation set (findings-A3): Hayai read 聞くに堪えないな as
/// ききききききききたたたた at both sizes, every き chosen at under 0.62; weighing the token just
/// chosen down by <see cref="RepeatPenalty"/> reads it right, and changes no other block from 1.05 up
/// to 1.2 — うんうん, どどどど, ククク and the like keep their repeats. manga-ocr is unchanged by it,
/// so only Hayai uses it. The loop stop only ends readings that would run to the token cap anyway:
/// a manga-ocr ellipsis is twelve dot tokens, so it waits for <see cref="LoopTokens"/>.
/// </remarks>
internal static class MangaDecodeGuard
{
    internal const int MaxUnit = 10;
    internal const int LoopTokens = 30;
    internal const float RepeatPenalty = 1.1f;

    /// <summary>
    /// Whether <paramref name="tokens"/> end in one unit of up to <see cref="MaxUnit"/> tokens
    /// repeated over at least <see cref="LoopTokens"/> tokens, and at least three times.
    /// </summary>
    internal static bool EndsInLoop(IReadOnlyList<long> tokens)
    {
        int count = tokens.Count;
        for (int unit = 1; unit <= MaxUnit; unit++)
        {
            int copies = Math.Max(3, (LoopTokens + unit - 1) / unit);
            int span = unit * copies;
            if (span > count) continue;
            bool loop = true;
            for (int k = count - span; k < count - unit && loop; k++)
                loop = tokens[k] == tokens[k + unit];
            if (loop) return true;
        }

        return false;
    }

    /// <summary>Weighs the token just chosen down, the way Hugging Face's repetition penalty does.</summary>
    internal static void PenalizeRepeat(Span<float> logits, IReadOnlyList<long> tokens)
    {
        if (tokens.Count == 0) return;
        ref float logit = ref logits[(int)tokens[^1]];
        logit = logit > 0 ? logit / RepeatPenalty : logit * RepeatPenalty;
    }
}
