using System.Windows;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Temporal evidence across passes. Both modes follow unchanged words to where they now are;
/// only dialogue also gives a small ending one confirming OCR pass.
/// </summary>
internal sealed class DialogueReadingTracker
{
    private Dictionary<(int Index, string Text), (string Text, Rect Bounds)> _pending = new();
    private OcrTextBlock[] _placed = [];
    private Dictionary<(int Index, string Text), Rect> _pendingPlacement = new();
    private bool _settleRead;
    private int _confirmationReads;
    private bool _confirmationInFlight;
    internal const int MaxConfirmationReads = 3;
    public bool NeedsConfirmation => _confirmationReads < MaxConfirmationReads &&
        (_settleRead || _pending.Count > 0 || _pendingPlacement.Count > 0);

    /// <summary>
    /// Whether a moved or resized box is waiting for a second reading. The only confirmation a
    /// panel ever asks for: it has no sentence endings to settle.
    /// </summary>
    public bool HasPendingPlacement => _pendingPlacement.Count > 0;

    public bool TryTakeConfirmation()
    {
        if (!NeedsConfirmation) return false;
        _confirmationReads++;
        _confirmationInFlight = true;
        return true;
    }

    public void RecognitionUnavailable()
    {
        if (_confirmationInFlight) _confirmationReads--;
        _confirmationInFlight = false;
    }

    // New pixel evidence can earn another bounded confirmation window. Rejected OCR wording
    // alone cannot reset the budget, otherwise alternating false tails would renew it forever.
    public void ObservePixelChange()
    {
        if (_confirmationReads >= MaxConfirmationReads)
        {
            _pending.Clear();
            _pendingPlacement.Clear();
        }
        _confirmationReads = 0;
    }

    public ReadingMerge Merge(IReadOnlyList<RenderedLine> shown, IReadOnlyList<OcrTextBlock> read)
    {
        _confirmationInFlight = false;
        var result = RealtimeReadingMerge.Merge(shown, read);
        var pending = new Dictionary<(int Index, string Text), (string Text, Rect Bounds)>();
        int confirmed = 0;
        for (int i = 0; i < read.Count; i++)
        {
            var old = result.Lines[i];
            var next = read[i];
            if (TextSimilarity.IsSameWording(old.Text, next.Text) || (next.Confidence ?? 1) < 0.8)
                continue;
            // Exact prefix, not fuzzy similarity: do not turn every OCR correction into growth.
            var prefix = Compact(old.Text);
            var full = Compact(next.Text);
            if (prefix.Length == 0 || full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var key = (i, old.Text);
            if (_pending.TryGetValue(key, out var previous) && previous.Text == full &&
                !Moved(previous.Bounds, next.Bounds))
            {
                result.Blocks[i] = next;
                result.Lines[i] = new RenderedLine(next.Text, next.Confidence ?? 1);
                confirmed++;
            }
            else pending[key] = (full, next.Bounds);
        }
        _pending = pending;
        // One final read even if the last few appearing glyphs were too small to change the
        // frame fingerprint. Once an unchanged reading confirms it, the idle path is free again.
        _settleRead = result.Changed || confirmed > 0;
        if (_settleRead) _confirmationReads = 0;
        bool moved = FollowPlacement(result);
        if (result.Changed || confirmed > 0 || moved)
            _placed = result.Blocks.ToArray();
        ExpireSpentConfirmations();
        return result with { Improved = result.Improved + confirmed, Kept = result.Kept - confirmed, Repositioned = moved };
    }

    /// <summary>
    /// The panel's merge: its own per-line reading policy, plus the same placement following as
    /// dialogue. No sentence-ending confirmation — a panel line is not typed out in front of the
    /// reader, and settling reads for it would change the panel's timing for nothing.
    /// </summary>
    public ReadingMerge MergePanel(IReadOnlyList<RenderedLine> shown, IReadOnlyList<OcrTextBlock> read)
    {
        _confirmationInFlight = false;
        _pending.Clear();
        _settleRead = false;
        var result = RealtimeReadingMerge.Merge(shown, read);
        bool moved = FollowPlacement(result);
        if (result.Changed || moved)
            _placed = result.Blocks.ToArray();
        ExpireSpentConfirmations();
        return result with { Repositioned = moved };
    }

    /// <summary>
    /// Holds unchanged words at their accepted geometry, or moves them, and says whether anything moved.
    /// </summary>
    /// <remarks>
    /// Two tolerances, because a block's height is not its line's thickness. A subtitle block is
    /// one line, but a manga balloon is several lines (or, written down the page, columns as long
    /// as the balloon), and a quarter of the whole block let a scroll of 40px on a 300px balloon
    /// count as wobble — the overlay then stayed where the page used to be. A quarter of the
    /// thinnest line is the real bar for "this is somewhere else".
    ///
    /// The wider, old bar stays the bar for following at once. Between the two is exactly the
    /// range a detector's own box wobble lives in on video and game backgrounds that keep being
    /// re-read, so a move there is only taken once a second reading lands in the same place. Wobble
    /// rarely lands twice on the same spot, and the confirming reads it can ask for are bounded by
    /// MaxConfirmationReads like every other confirmation.
    ///
    /// One exception to the wait: a block that clearly moved in this same pass, by the same amount.
    /// That is a scroll, and the pass is redrawing anyway — measured on a manga page, a 40px scroll
    /// moved a two-character balloon at once and left its long neighbours for a confirmation pass,
    /// which redrew the page a second time. Riding along adds no redraw of its own, so a wobble
    /// cannot use it to make one.
    /// </remarks>
    private bool FollowPlacement(ReadingMerge result)
    {
        bool moved = false;
        var placement = new Dictionary<(int Index, string Text), Rect>();
        var clearMoves = new List<Vector>();
        for (int i = 0; _placed.Length == result.Blocks.Count && i < result.Blocks.Count; i++)
        {
            if (!TextSimilarity.IsSameWording(_placed[i].Text, result.Blocks[i].Text)) continue;
            var change = Compare(_placed[i], result.Blocks[i]);
            if (change.Shifted) clearMoves.Add(new Vector(change.Dx, change.Dy));
        }
        for (int i = 0; _placed.Length == result.Blocks.Count && i < result.Blocks.Count; i++)
        {
            var block = result.Blocks[i];
            var previous = _placed[i];
            if (!TextSimilarity.IsSameWording(previous.Text, block.Text)) continue;
            var b = block.Bounds;
            var (dx, dy, lineTolerance, shifted, nudged, resized) = Compare(previous, block);
            var key = (i, block.Text);
            bool hasCandidate = _pendingPlacement.TryGetValue(key, out var candidate);
            // Confirm size independently of position: a moving subtitle can hold a stable new
            // size without ever returning to the same coordinates.
            bool sizeConfirmed = resized && hasCandidate && SameSize(candidate, b);
            // A small move, by contrast, is only believed where it was read twice running.
            bool nudgeConfirmed = !resized && nudged && (hasCandidate && SameSize(candidate, b) &&
                !Shifted(candidate, b, lineTolerance) || clearMoves.Any(v =>
                    Math.Abs(v.X - dx) <= lineTolerance && Math.Abs(v.Y - dy) <= lineTolerance));
            if (sizeConfirmed || (!resized && shifted) || nudgeConfirmed) moved = true;
            else
            {
                if (resized || nudged) placement[key] = b;
                // Keep the accepted size and font metrics, but let the whole layout follow its
                // new center immediately. Frozen child-line coordinates would still misplace text.
                var held = previous with { Text = block.Text, Confidence = block.Confidence };
                if (shifted)
                {
                    held = held with {
                        Bounds = Translate(held.Bounds, dx, dy),
                        LayoutBounds = held.LayoutBounds.IsEmpty || held.LayoutBounds == default
                            ? held.LayoutBounds : Translate(held.LayoutBounds, dx, dy),
                        SourceLineBounds = held.SourceLineBounds?.Select(r => Translate(r, dx, dy)).ToArray(),
                        Tilt = held.Tilt?.Offset(dx, dy),
                    };
                    moved = true;
                }
                result.Blocks[i] = held;
            }
        }
        _pendingPlacement = placement;
        return moved;
    }

    private readonly record struct Change(
        double Dx, double Dy, double LineTolerance, bool Shifted, bool Nudged, bool Resized);

    /// <summary>How a block's box differs from where it was placed — see <see cref="FollowPlacement"/>.</summary>
    /// <remarks>
    /// A centre past the block's bar is only a move when the box moved: both edges along that axis,
    /// the same way, each past the line's bar. One edge moving while the other holds is the detector
    /// cropping or overreaching — measured on a video subtitle, the box lost its first glyph's worth
    /// on one read (493,w554 → 535,w512) and had it back on the next, and taking that at once
    /// redrew the line twice for nothing. It still counts as a small move, so if the detector keeps
    /// reading it that way the confirmation lets it through. Such a box is not a scroll either, so it
    /// is never what other blocks ride along with.
    /// </remarks>
    private static Change Compare(OcrTextBlock previous, OcrTextBlock block)
    {
        var a = previous.Bounds;
        var b = block.Bounds;
        double tolerance = Math.Max(2, Math.Min(a.Height, b.Height) * 0.25);
        double lineTolerance = Math.Min(tolerance,
            Math.Max(2, Math.Min(LineThickness(previous), LineThickness(block)) * 0.25));
        double dx = b.X + b.Width / 2 - a.X - a.Width / 2;
        double dy = b.Y + b.Height / 2 - a.Y - a.Height / 2;
        bool resized = Math.Abs(a.Width - b.Width) > Math.Max(2, a.Width * 0.25) ||
            Math.Abs(a.Height - b.Height) > Math.Max(2, a.Height * 0.25);
        bool shifted = Math.Abs(dx) > tolerance || Math.Abs(dy) > tolerance;
        if (shifted && !resized)
            shifted = Math.Abs(dx) > tolerance && BothEdgesMoved(a.Left, a.Right, b.Left, b.Right, lineTolerance) ||
                Math.Abs(dy) > tolerance && BothEdgesMoved(a.Top, a.Bottom, b.Top, b.Bottom, lineTolerance);
        bool nudged = !shifted && (Math.Abs(dx) > lineTolerance || Math.Abs(dy) > lineTolerance);
        return new Change(dx, dy, lineTolerance, shifted, nudged, resized);
    }

    private static bool BothEdgesMoved(double fromStart, double fromEnd, double toStart, double toEnd, double tolerance)
    {
        double start = toStart - fromStart;
        double end = toEnd - fromEnd;
        return Math.Sign(start) == Math.Sign(end) && Math.Abs(start) > tolerance && Math.Abs(end) > tolerance;
    }

    private void ExpireSpentConfirmations()
    {
        if (_confirmationReads >= MaxConfirmationReads)
        {
            _pending.Clear();
            _pendingPlacement.Clear();
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _pendingPlacement.Clear();
        _settleRead = false;
        _confirmationReads = 0;
        _confirmationInFlight = false;
        _placed = [];
    }

    private static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
    private static Rect Translate(Rect bounds, double dx, double dy)
    {
        bounds.Offset(dx, dy);
        return bounds;
    }

    // The thinnest of the block's own lines (columns, when written down the page), across or down
    // whichever way it is thinner. Falls back to the block itself, which for one line is the same.
    private static double LineThickness(OcrTextBlock block) =>
        block.Lines.DefaultIfEmpty(block.Bounds).Min(r => Math.Min(r.Width, r.Height));

    private static bool Shifted(Rect a, Rect b, double tolerance) =>
        Math.Abs(b.X + b.Width / 2 - a.X - a.Width / 2) > tolerance ||
        Math.Abs(b.Y + b.Height / 2 - a.Y - a.Height / 2) > tolerance;

    private static bool SameSize(Rect a, Rect b)
    {
        double tolerance = Math.Max(2, Math.Min(a.Height, b.Height) * 0.25);
        return Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;
    }

    private static bool Moved(Rect a, Rect b)
    {
        double tolerance = Math.Max(2, Math.Min(a.Height, b.Height) * 0.25);
        return Math.Abs(a.X - b.X) > tolerance || Math.Abs(a.Y - b.Y) > tolerance ||
               Math.Abs(a.Width - b.Width) > tolerance || Math.Abs(a.Height - b.Height) > tolerance;
    }
}
