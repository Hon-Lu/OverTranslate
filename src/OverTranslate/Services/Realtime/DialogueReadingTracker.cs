using System.Windows;

namespace OverTranslate.Services.Realtime;

/// <summary>Temporal evidence for dialogue only. A small ending gets one confirming OCR pass.</summary>
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
        bool moved = false;
        var placement = new Dictionary<(int Index, string Text), Rect>();
        for (int i = 0; _placed.Length == read.Count && i < read.Count; i++)
        {
            var block = result.Blocks[i];
            var previous = _placed[i];
            if (!TextSimilarity.IsSameWording(previous.Text, block.Text)) continue;
            var a = previous.Bounds;
            var b = block.Bounds;
            double tolerance = Math.Max(2, Math.Min(a.Height, b.Height) * 0.25);
            double dx = b.X + b.Width / 2 - a.X - a.Width / 2;
            double dy = b.Y + b.Height / 2 - a.Y - a.Height / 2;
            bool shifted = Math.Abs(dx) > tolerance || Math.Abs(dy) > tolerance;
            bool resized = Math.Abs(a.Width - b.Width) > Math.Max(2, a.Width * 0.25) ||
                Math.Abs(a.Height - b.Height) > Math.Max(2, a.Height * 0.25);
            var key = (i, block.Text);
            // Confirm size independently of position: a moving subtitle can hold a stable new
            // size without ever returning to the same coordinates.
            bool sizeConfirmed = resized && _pendingPlacement.TryGetValue(key, out var candidate) &&
                SameSize(candidate, b);
            if (sizeConfirmed || (!resized && shifted)) moved = true;
            else
            {
                if (resized) placement[key] = b;
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
        if (result.Changed || confirmed > 0 || moved)
            _placed = result.Blocks.ToArray();
        if (_confirmationReads >= MaxConfirmationReads)
        {
            _pending.Clear();
            _pendingPlacement.Clear();
        }
        return result with { Improved = result.Improved + confirmed, Kept = result.Kept - confirmed, Repositioned = moved };
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
