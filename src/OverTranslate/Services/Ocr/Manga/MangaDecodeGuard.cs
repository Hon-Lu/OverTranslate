namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Stops a greedy reading that has fallen into repeating itself.</summary>
/// <remarks>
/// A reading caught in a loop would otherwise run on to the token cap: every step costs a decoder
/// pass, and what comes out is noise for the translator. It only ends readings that loop that long:
/// a manga-ocr ellipsis is twelve dot tokens, and うんうん, どどどど and ククク are shorter still, so it
/// waits for <see cref="LoopTokens"/>. MEASURED on the shared evaluation set: no block that ends on its
/// own is cut by it.
/// </remarks>
internal static class MangaDecodeGuard
{
    internal const int MaxUnit = 10;
    internal const int LoopTokens = 30;

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
}
