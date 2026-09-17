namespace OverTranslate.Services.Realtime;

/// <summary>One line as it currently stands on screen, and how well it was read.</summary>
/// <remarks>
/// Held per line rather than per pass because that is the granularity a correction arrives at: the
/// recogniser fixes one word in one sentence while the sentence beside it wobbles, and a single
/// score for the whole reading cannot tell those apart.
/// </remarks>
internal readonly record struct RenderedLine(string Text, double Confidence);

/// <summary>
/// What one pass's reading does to what is already on screen, line by line.
/// </summary>
/// <param name="Blocks">The lines to show: this pass's boxes, carrying whichever reading won.</param>
/// <param name="Lines">Those same lines as the new record of what the region shows.</param>
internal readonly record struct ReadingMerge(
    List<OcrTextBlock> Blocks,
    List<RenderedLine> Lines,
    int Improved,
    int Kept,
    int Added,
    int Dropped)
{
    /// <summary>Whether anything the reader can see is different from what is on screen now.</summary>
    public bool Repositioned { get; init; }
    public bool Changed => Improved > 0 || Added > 0 || Dropped > 0 || Repositioned;
}

/// <summary>
/// Decides, sentence by sentence, whether a fresh reading may replace the one on screen.
/// </summary>
/// <remarks>
/// The rule itself — a re-reading of a sentence has to be better read than the one already shown
/// before it may take its place — is not new, and the reason for it has not changed: without it the
/// overlay shows the newest reading rather than the best one, and a line is rewritten several times
/// while saying the same thing, which reads far worse than one wrong character.
///
/// What is new is the granularity. Applied to a whole pass, the rule compared one weighted average
/// against another, so a sentence that had just been read correctly could be thrown away because a
/// second sentence in the same frame happened to score a little lower than last time. Measured on a
/// live subtitle track: <c>"a facility to study that weird guitar..."</c> was read correctly at 0.99
/// and rejected, leaving <c>guitr</c> on screen, because the line above it had wobbled. The
/// correction had no way to land on its own — it had to wait for the whole batch to beat the whole
/// batch, which for that sentence never happened.
///
/// So each line is compared against the line it is a re-reading of, and nothing else. A sentence is
/// only ever rewritten when its <em>own</em> reading improves, which is strictly stronger than the
/// pass-level rule was: that one also let a sentence be overwritten by a worse reading of itself
/// whenever its neighbour improved enough to carry the average.
///
/// The one reading that is taken without improving is the one that is not a re-reading at all: a
/// line that contains the shown one and carries on past it is the same sentence finishing its
/// animation, and no score it could arrive with would say so. See
/// <see cref="TextSimilarity.IsContinuationOf"/>.
///
/// Lines are paired across passes by what they say, not where they are. Position sorts the
/// candidates — the same sentence rarely jumps up the frame between two consecutive polls — but the
/// text decides, because a line appearing above the current one shifts every box below it while the
/// words stay put. A read line that pairs with nothing is new, and a shown line nothing pairs with
/// has gone; both are real changes and neither is held back.
/// </remarks>
internal static class RealtimeReadingMerge
{
    /// <summary>
    /// How much better a re-reading has to score before it may take a sentence's place.
    /// </summary>
    /// <remarks>
    /// Not zero, because the score stops discriminating near the top of its range: measured over
    /// five sessions (3,398 passes that read text), the readings separated by 0.01–0.02 are as often
    /// a step backwards as forwards — <c>"Where have you gone?!"</c> at 0.98 was replaced by
    /// <c>"Where have vou gone?!"</c> at 1.00, <c>"really"</c> by <c>"eally"</c>, <c>"Thank you"</c>
    /// by <c>"Thankyou"</c>. Believing every improvement rewrote a sentence already on screen 176
    /// times over that sample against 66 under the rule this replaces, and most of the difference
    /// was that churn rather than corrections.
    ///
    /// At 0.02 the same sample rewrites 91 times, and what survives is overwhelmingly real:
    /// <c>relly→really</c>, <c>șong→song</c>, <c>WwWil→W-Will</c>, <c>"This is nice!ブハ"→"This is
    /// nice!"</c>. That is still 25 rewrites more than before, roughly one extra every four minutes
    /// of watching, and it is a deliberate trade: the rewrites bought are corrections that land,
    /// while what the old rule bought with its lower count was <c>guitr</c> staying on screen for
    /// the rest of the line's life.
    ///
    /// It cannot go higher without giving that back. The reading this whole issue is about gained
    /// 0.03 (0.96 → 0.99); a threshold of 0.04 brings the count to 59 — below the old rule — and
    /// rejects the correction it was raised for.
    ///
    /// Tried and dropped: refusing to rewrite a sentence already scoring above a ceiling, on the
    /// theory that a 0.99 reading is not worth arguing with. It removed almost nothing on top of
    /// this (89 against 91 at a 0.97 ceiling), because the wobbles that matter are further down.
    /// </remarks>
    public const double MinConfidenceGain = 0.02;

    /// <summary>
    /// Works out what the region should show, given what it shows now and what has just been read.
    /// </summary>
    public static ReadingMerge Merge(IReadOnlyList<RenderedLine> shown, IReadOnlyList<OcrTextBlock> read)
    {
        var pairedTo = Pair(shown, read);

        var blocks = new List<OcrTextBlock>(read.Count);
        var lines = new List<RenderedLine>(read.Count);
        int improved = 0, kept = 0, added = 0, paired = 0;

        for (var i = 0; i < read.Count; i++)
        {
            var block = read[i];
            // Null scores count as perfectly read, matching the filters upstream, which let a block
            // through when there is nothing to judge it by.
            var confidence = block.Confidence ?? 1.0;
            var partner = pairedTo[i];

            if (partner < 0)
            {
                added++;
                blocks.Add(block);
                lines.Add(new RenderedLine(block.Text, confidence));
                continue;
            }

            paired++;
            var current = shown[partner];

            // The same sentence, now with the rest of itself after it: the line was read while it
            // was still being typed out or faded in, and this is it finished. Taken without any
            // argument about the score, because the score is not what is being argued about — the
            // half-drawn reading was correct about the half it could see and scored accordingly,
            // and there is no confidence gain to be had from characters that were not on screen
            // yet. Left to the rule below, a subtitle caught mid-animation keeps its truncated
            // ending for the whole time it is up. See TextSimilarity.IsContinuationOf for what
            // separates this from a line whose end merely wobbled.
            if (TextSimilarity.IsContinuationOf(block.Text, current.Text))
            {
                improved++;
                blocks.Add(block);
                lines.Add(new RenderedLine(block.Text, confidence));
                continue;
            }

            // Better read than what is on screen, so the correction lands — this is the whole point.
            // Any difference in the words counts, not only one big enough to clear the noise
            // tolerance: "guitr." against "guitar..." is three characters in forty, and it is also
            // the whole sentence from the reader's side. What has to clear a bar is the score, by
            // MinConfidenceGain — which is also what bounds how often one sentence can be redrawn,
            // since each redraw has to climb.
            if (!TextSimilarity.IsSameWording(block.Text, current.Text) &&
                confidence > current.Confidence + MinConfidenceGain)
            {
                improved++;
                blocks.Add(block);
                lines.Add(new RenderedLine(block.Text, confidence));
                continue;
            }

            // The same sentence, read no better: the words stay as they were rendered, and so does
            // the score they are defended with. Anchoring both at what actually reached the screen is
            // what stops a line drifting away one tolerated character at a time. The box, though, is
            // this pass's — the sentence has not moved for the wrong reason just because its reading
            // was not worth taking.
            kept++;
            blocks.Add(block with { Text = current.Text });
            lines.Add(current);
        }

        return new ReadingMerge(blocks, lines, improved, kept, added, shown.Count - paired);
    }

    /// <summary>
    /// Pairs each read line with the shown line it is a re-reading of, or -1 where it is new.
    /// </summary>
    /// <remarks>
    /// Two rounds, because an exact re-reading is stronger evidence of a pairing than a similar one:
    /// with a line duplicated on screen — the same shout twice, a menu entry repeated — matching on
    /// similarity alone could consume the exact partner and leave the exact reading paired with the
    /// wrong copy.
    /// </remarks>
    private static int[] Pair(IReadOnlyList<RenderedLine> shown, IReadOnlyList<OcrTextBlock> read)
    {
        var pairedTo = new int[read.Count];
        Array.Fill(pairedTo, -1);

        if (shown.Count == 0) return pairedTo;

        var taken = new bool[shown.Count];

        for (var i = 0; i < read.Count; i++)
            pairedTo[i] = Claim(i, taken, shown, read[i].Text, TextSimilarity.IsSameContent);

        for (var i = 0; i < read.Count; i++)
            if (pairedTo[i] < 0)
                pairedTo[i] = Claim(i, taken, shown, read[i].Text, TextSimilarity.IsSameSentence);

        return pairedTo;
    }

    /// <summary>
    /// Takes the nearest unclaimed shown line this reading matches, measuring nearness by position in
    /// the reading order — which is top to bottom, so it stands in for how far the line has moved.
    /// </summary>
    private static int Claim(
        int index,
        bool[] taken,
        IReadOnlyList<RenderedLine> shown,
        string text,
        Func<string, string, bool> matches)
    {
        var best = -1;
        var bestDistance = int.MaxValue;

        for (var j = 0; j < shown.Count; j++)
        {
            if (taken[j]) continue;

            var distance = Math.Abs(index - j);
            if (distance >= bestDistance) continue;
            if (!matches(text, shown[j].Text)) continue;

            best = j;
            bestDistance = distance;
        }

        if (best >= 0) taken[best] = true;
        return best;
    }
}
