using System.Diagnostics;
using System.Drawing;
using NLog;

namespace OverTranslate.Services.Realtime;

/// <summary>What the picture as a whole is doing, as far as reading it is concerned.</summary>
internal enum ScrollPace
{
    /// <summary>Not being scrolled, or slowly enough to be read as it goes.</summary>
    Readable,

    /// <summary>
    /// Most of it is moving, or has only just stopped: a reading begun now would be of a frame
    /// that is gone before the reading is finished.
    /// </summary>
    Scrolling,
}

/// <summary>
/// Keeps one region's translations on the text they translate while that text is being moved —
/// a page scrolled, a chat pushed up by a new line — and off the screen while it cannot.
/// </summary>
/// <remarks>
/// <para>A translation is drawn where its text was read. Reading takes a few hundred milliseconds
/// and translating as long again, so when the text moves, the only thing that used to put the
/// translation right was the next reading — and until it arrived the translation sat where the
/// text had been, over whatever had come to be there instead. Over a page being scrolled that is
/// every moment of the scroll: the overlays trail the page and drag across the artwork (#390).</para>
///
/// <para>Moving is the one change that needs no reading to follow. <see cref="ScrollPrint"/> says
/// the picture moved and by how much, at every poll and for next to nothing, and
/// <see cref="TextAnchor"/> says of each translation whether its own text went with it. A
/// translation whose text moved is drawn where the text now is. One whose text goes on moving,
/// faster than it could be read on the way, is taken off the screen, carried along while the text
/// keeps moving, and drawn again where the text has come to rest — or dropped, if it has left the
/// block or been lost sight of, for the next reading to bring back.</para>
///
/// <para>Everything here acts on proof that a particular text moved, and does nothing without it.
/// A translation whose text is still where it was stays, whatever the picture around it does. One
/// whose text has changed, or gone, or cannot be found, also stays: that is a question about
/// words, it belongs to the reading, and the reading answers it as it always has. So a subtitle
/// over a video, a line of dialogue being typed out and a panel in a game are not treated any
/// differently than before — none of them is ever the same pixels somewhere else.</para>
///
/// <para>Passes are followed from the moment they are read, not from when they are drawn: the
/// translation of a line read just before a scroll arrives during it, and has to be told where
/// that line is by then.</para>
/// </remarks>
internal sealed class RealtimeScrollFollow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// How long the picture has to have been unchanged before text that was moving is taken to
    /// have stopped.
    /// </summary>
    /// <remarks>
    /// Longer than the capture backend's limit on reading frames back (120ms), because two polls
    /// closer together than that are handed the same frame — which would read as a scroll having
    /// stopped in the middle of it, and draw every translation for one poll at a place the page is
    /// only passing through.
    /// </remarks>
    internal static readonly TimeSpan RestAfter = TimeSpan.FromMilliseconds(140);

    // The share of a text's edges that have to be found for it to be there.
    private const double Present = 0.75;

    // How much better the text has to be found at the new place than at the old one. Lines of
    // text look alike, and a scroll of one line pitch leaves the next line where this one was.
    private const double ClearlyBetter = 0.1;

    /// <summary>
    /// How far text may move in one poll interval, as a share of its line's thickness, and still
    /// be followed on the screen rather than hidden.
    /// </summary>
    /// <remarks>
    /// A translation is drawn where its text was at the last poll, so it trails by about what the
    /// text covers in one. At a third of a line it still sits on its text and can be read as it
    /// goes — credits rolling, a page crept along. Past that it is on the next line's text as
    /// often as its own, which is the thing this exists to stop.
    /// </remarks>
    private const double FollowedStep = 0.35;

    // How close to the edge of the frame counts as touching it.
    private const int EdgeMargin = 3;

    // How many passes may be waiting on their translation and still be followed. More than the
    // session lets be in flight at once; past it something is stuck, not slow.
    private const int MaxWaiting = 8;

    private readonly object _sync = new();
    private readonly int _region;
    private readonly Action<IReadOnlyList<TranslatedBlock>, int> _draw;
    private readonly List<Pass> _waiting = [];

    private Pass? _shown;

    // A pass whose translation has arrived while its text is still being looked for. What is on
    // the screen stays there until it is found, rather than going blank for the poll in between.
    private Pass? _arriving;

    // Whether the last frame that differed from the one before it carried text with it, or was
    // most of a picture going past with nothing in it standing still.
    private bool _scrolling;
    private ScrollPrint? _before;
    private ScrollPrint? _latest;
    private IReadOnlyList<ScrollShift> _lastShifts = [];
    private long _observedAt;
    private long _changedAt;

    // How far the picture has moved in all, so a reading can be told how far it moved meanwhile.
    private int _travelX;
    private int _travelY;

    // Set when a translation was moved or dropped here: what the reading remembers of where its
    // lines are is no longer where they are.
    private bool _readAfresh;

    /// <param name="draw">
    /// Puts lines on the screen, with the cache generation they were translated under. Called
    /// while this is locked, so what reaches the screen is in the order it was decided.
    /// </param>
    public RealtimeScrollFollow(int region, Action<IReadOnlyList<TranslatedBlock>, int> draw)
    {
        _region = region;
        _draw = draw;
    }

    private sealed class Pass(long number, Followed[] texts)
    {
        public long Number { get; } = number;
        public Followed[] Texts { get; } = texts;
        public IReadOnlyList<TranslatedBlock>? Lines { get; set; }
        public int Generation { get; set; }
    }

    private sealed class Followed(Rectangle bounds, TextAnchor anchor, double thickness)
    {
        public Rectangle Bounds { get; } = bounds;
        public TextAnchor Anchor { get; } = anchor;
        public double Thickness { get; } = thickness;
        public int Dx { get; set; }
        public int Dy { get; set; }

        /// <summary>Off the screen until its text stops.</summary>
        public bool Moving { get; set; }

        /// <summary>Whether it moved in the last frame that differed from the one before.</summary>
        public bool Pushed { get; set; }

        /// <summary>Out of the block, or lost: not drawn again until it is read again.</summary>
        public bool Gone { get; set; }

        /// <summary>
        /// Where else it may be, as an offset, when where it is has only been worked out: text read
        /// while the picture moved is taken to have moved with it, and may be a caption that did not.
        /// </summary>
        public Point? Otherwise { get; set; }

        /// <summary>Read from a moving picture while touching the edge of the frame.</summary>
        public bool CutOff { get; set; }

        public Rectangle Now => new(Bounds.X + Dx, Bounds.Y + Dy, Bounds.Width, Bounds.Height);
    }

    /// <summary>
    /// Looks at this poll's frame: moves, hides or brings back whatever is being followed, and
    /// says whether the picture is being scrolled too fast to be worth reading.
    /// </summary>
    /// <param name="now">A <see cref="Stopwatch"/> timestamp for when the frame was taken.</param>
    public ScrollPace Observe(Bitmap frame, long now)
    {
        using var pixels = FramePixels.Lock(frame);
        lock (_sync)
        {
            var before = _latest;
            var print = ScrollPrint.Capture(pixels, before);
            if (before is null || before.Width != print.Width || before.Height != print.Height)
            {
                (_before, _latest, _lastShifts) = (null, print, []);
                _observedAt = _changedAt = now;
                return ScrollPace.Readable;
            }

            if (print.SameAs(before))
            {
                // Possibly the very frame the last poll was handed — see RestAfter. Nothing is read
                // either until that is known: a reading would hold up the poll that finds out.
                if (Stopwatch.GetElapsedTime(_changedAt, now) < RestAfter)
                    return _scrolling || AnyMoving() ? ScrollPace.Scrolling : ScrollPace.Readable;

                _lastShifts = [];
                _observedAt = now;
                _scrolling = false;
                var settled = Settle(pixels);
                if (Arrive() || settled) Draw();
                return ScrollPace.Readable;
            }

            var elapsed = Stopwatch.GetElapsedTime(_observedAt, now);
            (_before, _latest) = (before, print);
            _observedAt = _changedAt = now;
            _lastShifts = ScrollPrint.FindShifts(before, print);
            if (_lastShifts.Count > 0)
            {
                _travelX += _lastShifts[0].Dx;
                _travelY += _lastShifts[0].Dy;
            }

            // How many poll intervals the picture had to move in: a poll that waited on a reading
            // sees several intervals' worth of movement, and that is not the text going faster.
            var polls = Math.Max(1, elapsed / RealtimeRegionState.PollInterval);
            bool redraw = false, carried = false;
            int hidden = 0, followed = 0, lost = 0;
            foreach (var pass in Passes())
            foreach (var text in pass.Texts)
            {
                if (text.Gone) continue;
                var shown = ReferenceEquals(pass, _shown);
                var (wasMoving, wasAtX, wasAtY) = (text.Moving, text.Dx, text.Dy);

                Follow(text, pixels, before, print, polls);

                var moved = text.Gone || text.Dx != wasAtX || text.Dy != wasAtY;
                carried |= moved;
                _readAfresh |= moved;
                if (!shown) continue;
                if (text.Gone) lost++;
                else if (text.Moving && !wasMoving) hidden++;
                else if (!text.Moving && (text.Dx != wasAtX || text.Dy != wasAtY)) followed++;
                redraw |= text.Gone || text.Moving != wasMoving || (!text.Moving && (text.Dx != wasAtX || text.Dy != wasAtY));
            }

            if (redraw)
                Log.Debug(
                    "Realtime region {Region} moved by {Shifts}: {Hidden} hidden, {Followed} followed, {Lost} dropped",
                    _region, Describe(_lastShifts), hidden, followed, lost);
            if (Arrive() || redraw) Draw();

            // Not read while text is on the move, however slowly: it is kept where it belongs from
            // the pixels, and a reading of it would be of where it no longer is. Nor while most of
            // the picture is going past with no text standing still in it — a page being scrolled
            // through, where a reading would be of lines cut off at the edge they are crossing. A
            // picture that moves behind text which does not is read as it always was.
            _scrolling = carried ||
                (_lastShifts.Count > 0 && _lastShifts[0].Share >= 0.5 && !AnyStanding());
            return _scrolling || AnyMoving() ? ScrollPace.Scrolling : ScrollPace.Readable;
        }
    }

    private bool AnyMoving() => Passes().Any(pass => pass.Texts.Any(text => text.Moving && !text.Gone));

    private bool AnyStanding() => Passes().Any(pass => pass.Texts.Any(text => !text.Moving && !text.Gone));

    // Puts the pass that was waiting for its text to be found in place of what is shown, once
    // every text in it has been found or given up. Says whether it did.
    private bool Arrive()
    {
        if (_arriving is not { } arriving || arriving.Texts.Any(text => text.Moving && !text.Gone)) return false;
        (_shown, _arriving) = (arriving, null);
        return true;
    }

    /// <summary>
    /// Whether translations were moved or dropped since this was last asked, so that the region
    /// should be read as if for the first time rather than against where its lines used to be.
    /// </summary>
    public bool TakeReadAfresh()
    {
        lock (_sync)
        {
            var afresh = _readAfresh;
            _readAfresh = false;
            return afresh;
        }
    }

    /// <summary>Where the picture had got to when a reading began — see <see cref="Track"/>.</summary>
    internal readonly record struct ReadStart(int TravelX, int TravelY, bool Moving);

    /// <summary>Marks the frame a reading is about to be made from.</summary>
    public ReadStart BeginRead()
    {
        lock (_sync) return new ReadStart(_travelX, _travelY, _lastShifts.Count > 0);
    }

    // One followed text against this poll's frame.
    private void Follow(Followed text, FramePixels pixels, ScrollPrint before, ScrollPrint print, double polls)
    {
        var here = text.Anchor.PresenceAt(pixels, text.Dx, text.Dy);

        foreach (var shift in _lastShifts)
        {
            var there = text.Anchor.PresenceAt(pixels, text.Dx + shift.Dx, text.Dy + shift.Dy);
            var moved = there is { } found
                ? found >= Present && found > (here ?? 0) + ClearlyBetter
                // Nothing to recognise it by, or its new place is out of frame: the picture it
                // sits in is the only witness left.
                : !(here >= Present) &&
                  (text.Anchor.CanJudge ? before.CarriedBy(shift, text.Now) : ScrollPrint.Moved(before, print, text.Now, shift));
            if (!moved) continue;

            text.Dx += shift.Dx;
            text.Dy += shift.Dy;
            if (!Inside(text.Now, print))
            {
                text.Gone = true;
                return;
            }

            // Hidden once it is moving too fast to be read on the way — and was already moving a
            // frame ago. The first move is followed whatever its size: a chat pushed up by a new
            // line has moved once and stopped, and taking its translations away for the frame it
            // takes to find that out would make every new line a blink. A page being scrolled is
            // still moving at the next frame, and that is when they go. Once hidden it stays
            // hidden until it stops, however slowly it finishes.
            text.Moving |= text.Pushed && shift.Distance / polls > FollowedStep * text.Thickness;
            text.Pushed = true;
            return;
        }

        text.Pushed = false;

        // It was moving and did not move with the picture this time. Either it has stopped, or
        // the picture moved in a way that could not be followed and there is no saying where it is.
        if (text.Moving) Stop(text, pixels, here);
    }

    // A text that was moving is taken to have arrived: kept if it is where it was followed to, or
    // where it may have stayed all along, and dropped if it is in neither place.
    private void Stop(Followed text, FramePixels pixels, double? here)
    {
        text.Moving = false;

        // Read off a picture that was moving, at the edge it was crossing: only part of the text
        // was in view, and what was read is that part. The next reading has the whole of it.
        if (text.CutOff)
        {
            text.Gone = true;
            _readAfresh = true;
            return;
        }

        if (here is null or >= Present)
        {
            text.Otherwise = null;
            return;
        }

        if (text.Otherwise is { } other && text.Anchor.PresenceAt(pixels, other.X, other.Y) >= Present)
            (text.Dx, text.Dy, text.Otherwise) = (other.X, other.Y, null);
        else
            text.Gone = true;
        _readAfresh = true;
    }

    // The picture has stopped changing: what was moving has arrived, if it is where it should be.
    private bool Settle(FramePixels pixels)
    {
        var redraw = false;
        foreach (var pass in Passes())
        foreach (var text in pass.Texts)
        {
            text.Pushed = false;
            if (!text.Moving || text.Gone) continue;
            Stop(text, pixels, text.Anchor.PresenceAt(pixels, text.Dx, text.Dy));
            redraw |= ReferenceEquals(pass, _shown);
        }
        if (redraw) Log.Debug("Realtime region {Region} came to rest; translations drawn where their text is", _region);
        return redraw;
    }

    /// <summary>
    /// Starts following the text a pass has just read, in the frame it was read from, so that its
    /// translation can be drawn where the text is by the time it arrives.
    /// </summary>
    /// <param name="start">What <see cref="BeginRead"/> gave when that frame was taken.</param>
    public void Track(long pass, IReadOnlyList<OcrTextBlock> blocks, Bitmap frame, ReadStart start)
    {
        using var pixels = FramePixels.Lock(frame);
        var texts = new Followed[blocks.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            var bounds = Rectangle.Round(new RectangleF(
                (float)blocks[i].Bounds.X, (float)blocks[i].Bounds.Y,
                (float)blocks[i].Bounds.Width, (float)blocks[i].Bounds.Height));
            var thickness = blocks[i].Lines.Min(line => Math.Min(line.Width, line.Height));
            texts[i] = new Followed(bounds, TextAnchor.Capture(pixels, bounds), thickness);
        }

        lock (_sync)
        {
            // Read from a frame the picture was moving in, or one it has moved on from since: the
            // text is taken to have gone with the picture, and its translation stays off the screen
            // until the picture stops and the text is found — there, or where it was read.
            int dx = _travelX - start.TravelX, dy = _travelY - start.TravelY;
            if (start.Moving || dx != 0 || dy != 0)
                foreach (var text in texts)
                {
                    (text.Dx, text.Dy, text.Moving, text.Otherwise) = (dx, dy, true, new Point(0, 0));
                    text.CutOff = start.Moving && !Rectangle.Inflate(text.Bounds, EdgeMargin, EdgeMargin)
                        .Equals(Rectangle.Intersect(
                            Rectangle.Inflate(text.Bounds, EdgeMargin, EdgeMargin), new Rectangle(0, 0, frame.Width, frame.Height)));
                }

            _waiting.Add(new Pass(pass, texts));
            if (_waiting.Count > MaxWaiting) _waiting.RemoveAt(0);
        }
    }

    /// <summary>
    /// A pass's translated lines, to be drawn where their text now is: at once, when the text has
    /// not moved since it was read, which is nearly always.
    /// </summary>
    public void Publish(long pass, IReadOnlyList<TranslatedBlock> lines, int generation)
    {
        lock (_sync)
        {
            var tracked = _waiting.Find(waiting => waiting.Number == pass);
            _waiting.RemoveAll(waiting => waiting.Number <= pass);

            // A pass that was never followed — the empty one that clears a region — is drawn as it is.
            var arrived = tracked is not null && tracked.Texts.Length == lines.Count ? tracked : new Pass(pass, []);
            arrived.Lines = lines;
            arrived.Generation = generation;

            // Its text is still being looked for, and something is on the screen: that stays until
            // the next frame says where the text is, instead of the region blinking empty.
            if (arrived.Texts.Any(text => text.Moving && !text.Gone) && _shown is not null)
            {
                _arriving = arrived;
                return;
            }

            (_shown, _arriving) = (arrived, null);
            Draw();
        }
    }

    private IEnumerable<Pass> Passes()
    {
        if (_shown is { } shown) yield return shown;
        if (_arriving is { } arriving) yield return arriving;
        foreach (var waiting in _waiting) yield return waiting;
    }

    private void Draw()
    {
        if (_shown is not { Lines: { } lines } shown) return;

        var drawn = new List<TranslatedBlock>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (i >= shown.Texts.Length)
            {
                drawn.Add(lines[i]);
                continue;
            }

            var text = shown.Texts[i];
            if (text.Gone || text.Moving) continue;
            drawn.Add(text.Dx == 0 && text.Dy == 0 ? lines[i] : MovedBy(lines[i], text.Dx, text.Dy));
        }
        _draw(drawn, shown.Generation);
    }

    private static TranslatedBlock MovedBy(TranslatedBlock line, int dx, int dy)
    {
        System.Windows.Rect Move(System.Windows.Rect bounds)
        {
            bounds.Offset(dx, dy);
            return bounds;
        }

        return line with
        {
            Bounds = Move(line.Bounds),
            SourceLineBounds = line.SourceLineBounds?.Select(Move).ToArray(),
            Tilt = line.Tilt?.Offset(dx, dy),
        };
    }

    private static bool Inside(Rectangle box, ScrollPrint frame) =>
        box.Left >= 0 && box.Top >= 0 && box.Right <= frame.Width && box.Bottom <= frame.Height;

    private static string Describe(IReadOnlyList<ScrollShift> shifts) =>
        shifts.Count == 0 ? "nothing" : string.Join(", ", shifts.Select(shift => $"({shift.Dx},{shift.Dy}) {shift.Share:0%}"));
}
