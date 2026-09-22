using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Reads a page of columns a second time at a different detector size, and keeps the better of the
/// two readings of each balloon.
/// </summary>
/// <remarks>
/// <para>WHY THERE IS A SECOND READ AT ALL. The detector's answer depends on the exact size of the
/// image it is handed, in steps of 32 pixels, and not smoothly — see
/// <see cref="OnnxOcrEngine"/>'s remarks on alignment for the mechanism. MEASURED over 15 frames
/// the app itself captured while the user read a comic (the frames under
/// <c>logs/frames</c>, scored against the transcript of the same pages in
/// <c>.ai/test-images/vertical-image-ja2/ground-truth.json</c>): reading them at their native size
/// gets 98 of 132 balloons whole, at 0.95 of native 108, at 0.90 94, at 0.87 107, at 0.77 115, at
/// 0.72 111. The response is not monotone and native is one of the worst points on it, so there is
/// no better single size to move to — the size that is lucky for one frame is unlucky for the
/// next.</para>
///
/// <para>Two sizes unioned is a different and much larger win, and it is robust to which second
/// size is used: every value measured between 0.56 and 0.95 of native takes 132 balloons from 98
/// whole to between 111 and 119, and the characters the transcript asks for from 0.970 to 0.985.
/// Three sizes add one balloon over two. So what earns the second read is asking with different
/// geometry rather than asking at a better size.</para>
///
/// <para>THIS IS WHAT THE USER WAS ALREADY DOING BY HAND. The screenshot flow reads the same
/// pipeline over the same kind of pixels and scores the same — measured on those 15 frames, both
/// flows get 98 whole and 0.970. What made the screenshot flow feel better is that a capture that
/// reads badly gets dragged again, and a slightly different rectangle is a slightly different
/// detector geometry. This does that without being asked.</para>
///
/// <para>It costs a second inference on a page, which is why it is only on the vertical path.
/// Reading columns is a comic-reading feature: the page is still, it is looked at for seconds, and
/// the region is not read again until it changes.</para>
/// </remarks>
internal static class VerticalSecondLook
{
    /// <summary>
    /// The second detector size, as a share of the region's longest side.
    /// </summary>
    /// <remarks>
    /// <para>Which value matters less than there being a second one — every fraction measured
    /// between 0.56 and 0.95 takes the 15 captured frames from 98 balloons whole to between 111 and
    /// 119 as a raw union. So this was set to 0.80, the middle of that band, on the reasoning that a
    /// number picked off the peak of a jumpy curve is a number fitted to fifteen frames.</para>
    ///
    /// <para>Then both were measured through <see cref="Merge"/> rather than as a raw union, and
    /// 0.77 is better on two of the three corpora and level on the third: on the captured frames
    /// 108 balloons whole against 105, 0.939 of them arriving as one group against 0.925, and 0.923
    /// of the output real against 0.910; on the twelve web pages 42 whole against 41; on the comic
    /// files 126 against 129 whole but 0.957 of the output real against 0.950. It is also the value
    /// that reads パーティに付与術士が必要になったから as one sentence on the frame the user
    /// reported it split on. Chosen between two measured options rather than fitted, which is the
    /// difference from picking the peak of the sweep.</para>
    /// </remarks>
    private const double OtherFraction = 0.77;

    /// <summary>
    /// How far apart the two sizes have to be before a second read is worth paying for.
    /// </summary>
    /// <remarks>
    /// Two sizes that land on the same 32-pixel step are the same read; the point of this is that
    /// the geometry differs. A small region is left alone entirely — there the whole page is
    /// already inside the detector's range and the reading does not move.
    /// </remarks>
    private const int LeastUsefulDifference = 64;

    /// <summary>The size to read the page at a second time, or null when once is enough.</summary>
    internal static int? OtherSize(int width, int height, int primary)
    {
        var native = Math.Max(width, height);
        if (native < Realtime.RealtimeDetectorSize.DownscaleMinSide) return null;

        var other = (int)(native * OtherFraction) / 32 * 32;
        return Math.Abs(other - primary) < LeastUsefulDifference ? null : other;
    }

    /// <summary>
    /// The two readings of one page, with each balloon kept as whichever reading said the most
    /// about it.
    /// </summary>
    /// <remarks>
    /// <para>The three cases, all of which turn up on those 15 frames:</para>
    ///
    /// <para>A balloon only the second read found lands on nothing and is added. This is the whole
    /// point — on the frame <c>region0-084148-301</c> the balloon
    /// パーティに付与術士が必要になったから is one 180-pixel box at native, reads as nothing, and
    /// comes back complete at 0.77.</para>
    ///
    /// <para>A balloon both reads found is kept once. Whichever of the two says MORE about the
    /// place wins, which is how a balloon read whole replaces the same balloon read in pieces —
    /// パーティに付与術士が / 必要になったから against
    /// パーティに付与術士が必要になったから.</para>
    ///
    /// <para>A balloon the first read got whole and the second read in pieces keeps the first
    /// reading: a piece does not say more than the sentence it is part of.</para>
    /// </remarks>
    internal static List<OcrTextBlock> Merge(
        List<OcrTextBlock> first, IReadOnlyList<OcrTextBlock> second)
    {
        if (second.Count == 0) return first;

        var blocks = new List<(OcrTextBlock Block, bool FromSecond)>(first.Count + second.Count);
        blocks.AddRange(first.Select(block => (block, false)));
        blocks.AddRange(second.Select(block => (block, true)));

        var kept = new List<OcrTextBlock>(blocks.Count);
        foreach (var place in Places(blocks))
        {
            var mine = place.Where(item => !item.FromSecond).Select(item => item.Block).ToList();
            var theirs = place.Where(item => item.FromSecond).Select(item => item.Block).ToList();
            kept.AddRange(mine.Count == 0 || theirs.Count == 0
                ? mine.Count == 0 ? theirs : mine
                : Better(mine, theirs));
        }

        return kept;
    }

    /// <summary>
    /// Which of the two readings of one balloon to keep.
    /// </summary>
    /// <remarks>
    /// <para>On how much of the balloon came back as ONE block, which is the thing that decides
    /// whether it will be translated as a sentence. Neither of the two obvious scores works on its
    /// own: total characters rewards a reading that picked up a stray mark off the balloon's edge,
    /// and fewest blocks prefers a single group that read half the balloon over two groups that
    /// read all of it.</para>
    ///
    /// <para>The case this is here for is the same eighteen characters either way —
    /// パーティに付与術士が / 必要になったから against パーティに付与術士が必要になったから — where
    /// the only difference is that one of them is a sentence. Total characters then fewer blocks
    /// breaks the remaining ties.</para>
    /// </remarks>
    private static List<OcrTextBlock> Better(List<OcrTextBlock> mine, List<OcrTextBlock> theirs)
    {
        var longest = Longest(mine).CompareTo(Longest(theirs));
        if (longest != 0) return longest > 0 ? mine : theirs;

        var characters = Characters(mine).CompareTo(Characters(theirs));
        if (characters != 0) return characters > 0 ? mine : theirs;

        return theirs.Count < mine.Count ? theirs : mine;
    }

    private static int Longest(List<OcrTextBlock> blocks) =>
        blocks.Max(block => SameWriting.Squeeze(block.Text).Length);

    private static int Characters(List<OcrTextBlock> blocks) =>
        blocks.Sum(block => SameWriting.Squeeze(block.Text).Length);

    /// <summary>
    /// Groups both readings' blocks into the places they are about, by where their boxes lie.
    /// </summary>
    /// <remarks>
    /// Transitively, so that a balloon one read found in three pieces and the other in one is a
    /// single place. Balloons do not overlap each other on a page, so a place is a balloon.
    /// </remarks>
    private static List<List<(OcrTextBlock Block, bool FromSecond)>> Places(
        List<(OcrTextBlock Block, bool FromSecond)> blocks)
    {
        var owner = Enumerable.Range(0, blocks.Count).ToArray();

        int Find(int i)
        {
            while (owner[i] != i) i = owner[i] = owner[owner[i]];
            return i;
        }

        for (var i = 0; i < blocks.Count; i++)
        for (var j = i + 1; j < blocks.Count; j++)
        {
            if (!SharesThePlace(blocks[i].Block.Bounds, blocks[j].Block.Bounds)) continue;
            var a = Find(i);
            var b = Find(j);
            if (a != b) owner[a] = b;
        }

        var places = new Dictionary<int, List<(OcrTextBlock, bool)>>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!places.TryGetValue(Find(i), out var place))
                places[Find(i)] = place = [];
            place.Add(blocks[i]);
        }

        return [.. places.Values];
    }

    /// <summary>How much of the smaller box two readings share before they are the same balloon.</summary>
    private const double InTheSamePlace = 0.3;

    private static bool SharesThePlace(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return false;

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 && shared.Width * shared.Height / smaller > InTheSamePlace;
    }
}
