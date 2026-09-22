using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, and System.Drawing arrives with
// it — both carry a type of these names.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// The live subtitle layer for one watched block: a click-through window pinned over the region.
/// In natural mode it captures the application underneath, removes the source glyph rectangle with
/// a lightweight local background repair, then draws the translation back into the same place.
/// </summary>
/// <remarks>
/// The background patch is refreshed while a line is visible. That matters over video/game content:
/// a one-time screenshot would turn the translated line into a frozen rectangular tile while the
/// picture behind it kept moving. The picture it refreshes from comes from the session's capture
/// backend, which composes without this overlay, so every refresh sees the original application
/// rather than recursively photographing its own translation.
///
/// That refresh runs off the dispatcher, and only the assignment of the finished brushes comes back
/// to it. Everything before that — the grab, the repair, the bitmap — is pixel work that does not
/// need the interface thread, and leaving it there coupled how smooth the shell felt to how much this
/// layer happened to be doing. It also declines to do the work at all while the picture underneath is
/// holding still, which over a dialogue box or a menu is nearly all of the time.
/// </remarks>
public partial class RealtimeBlockWindow : Window
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    // The scrim exists to hide the source line underneath, so it is sized from the text and not the
    // block: everything outside these paddings stays untouched picture. The vertical padding is the
    // tighter of the two on purpose — a band reaching above and below a subtitle sits in the middle
    // of what the user is watching, while the same slack to its left and right lands on picture they
    // are not reading anyway.
    private const double ScrimPaddingX = 5;
    private const double ScrimPaddingY = 3;

    // Only the band is rounded — see where it is applied for why a repaired patch is not.
    private const double BandCornerRadius = 3;

    // The OCR box can omit detached punctuation/diacritics (especially Japanese dakuten). The
    // repaired background therefore extends beyond the visible translation band. Pixels in this
    // guard are copied from the original frame unchanged unless the adaptive eraser identifies them
    // as part of the source line, so the larger patch does not look like a larger subtitle panel.
    private const double MinNaturalGuardX = 10;
    private const double MinNaturalGuardY = 12;

    private const double MinFontSize = 8.5;
    private const double LineHeightRatio = 1.22;

    /// <summary>
    /// How much of its square cell one glyph of vertical text is set at.
    /// </summary>
    /// <remarks>
    /// The same figure the screenshot overlay uses, and for the same reason: a cell sized exactly to
    /// the type leaves adjacent characters touching, and vertical writing has no word spaces to
    /// break that up. The remaining sliver is the leading, spent on all four sides at once because
    /// the cell is square.
    /// </remarks>
    private const double VerticalGlyphFill = 0.92;

    /// <summary>
    /// How much taller than the line it replaces a single-line translation may be drawn. The scrim
    /// is sized to whichever is larger, so this is really a cap on the band's height.
    /// </summary>
    private const double MaxHeightOverSource = 1.15;

    // No fade on rebuild. A cross-fade was there to soften the swap, but every repaint took the
    // whole layer to transparent and back, so a line being re-read — which happens several times
    // while one subtitle is on screen — pulsed the text the reader was in the middle of. Swapping
    // outright is the quieter of the two, and the repaints it makes visible are better dealt with
    // by not making them: see RealtimeBlockWindow.SetLines and TextSimilarity.
    private static readonly FontFamily TextFont =
        new("Microsoft JhengHei, Segoe UI Variable Text, Segoe UI, Sans-Serif");

    private readonly System.Drawing.Rectangle _physBounds;
    private readonly bool _latinSourceToCjkTarget;

    // The session's capture backend, reached through the controller so this window never holds one.
    // Everything this window needs to see of the screen comes through here — see
    // CaptureUnderlyingRegion for why nothing may photograph the screen on its own any more.
    private readonly Func<System.Drawing.Rectangle, System.Drawing.Bitmap?> _grabUnderlying;

    // Per session rather than per application: the colours are read once when the session starts and
    // cannot change while it runs, because reaching the page that sets them means the shell window,
    // which a running session has hidden. Frozen for the same reason the fixed brushes were — they
    // are handed to every line of every rebuild and never mutated.
    private readonly SolidColorBrush _scrimBrush;
    private readonly SolidColorBrush _textBrush;

    // 顯示外觀 → 進階選項, both off unless the user asked for them. Held per session for the same
    // reason the colours are: the page that sets them is behind a shell window this session hid.
    //
    // With both off nothing here grabs the screen at all — see Rebuild. That is not merely a saving:
    // the band is the predictable one of the two looks, and a window that does not photograph itself
    // cannot bake its own translation into what it draws either.
    private readonly bool _naturalBackground;
    private readonly bool _sampleTextColor;
    private readonly RealtimeBlockMode _mode;

    // Which way this block's source runs, as the user said in edit mode. It decides how the
    // translation is set here and how the frame was read upstream — see RealtimeTextOrientation.
    private readonly RealtimeTextOrientation _orientation;

    // 顯示外觀 → 邊框, held per session like the colours. No fixed brush with the switch on means
    // each group picks its own colour — see ApplyBorder.
    private readonly bool _border;
    private readonly SolidColorBrush? _fixedBorderBrush;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private bool _isLoaded;

    // Published by Rebuild on the dispatcher and read by the refresh loop on its own thread, so it is
    // replaced wholesale rather than mutated. The generation beside it is what a finished batch of
    // brushes is checked against: a rebuild between the grab and the assignment means those brushes
    // belong to visuals that are no longer on the canvas.
    private NaturalPatchVisual[] _naturalPatches = [];
    private int _patchGeneration;

    private CancellationTokenSource? _naturalRefreshCts;

    // What the patches on screen were painted from. Only the refresh loop reads or writes it, except
    // for Rebuild clearing it to say the comparison has to start again.
    private FrameFingerprint? _lastRefreshPrint;

    private IReadOnlyList<TranslatedBlock> _lines = [];

    /// <param name="grabUnderlying">
    /// Asks the session's capture backend for a screen rectangle. Required rather than optional:
    /// 進階選項 cannot be drawn without it, and a window that quietly fell back to reading the
    /// screen itself would be reading its own subtitles.
    /// </param>
    public RealtimeBlockWindow(
        int regionId,
        System.Drawing.Rectangle physBounds,
        Func<System.Drawing.Rectangle, System.Drawing.Bitmap?> grabUnderlying,
        string sourceLanguage,
        string targetLanguage,
        string textColor,
        string scrimColor,
        int scrimOpacity,
        bool naturalBackground = false,
        bool sampleTextColor = false,
        RealtimeBlockMode mode = RealtimeBlockMode.Subtitle,
        bool border = false,
        string? borderColor = null,
        RealtimeTextOrientation orientation = RealtimeTextOrientation.Horizontal)
    {
        InitializeComponent();
        _mode = mode;
        _orientation = orientation;
        _border = border;
        _fixedBorderBrush = border && borderColor is not null
            ? Freeze(new SolidColorBrush(RealtimeSubtitleColors.Border(borderColor)))
            : null;

        RegionId = regionId;
        _physBounds = physBounds;
        _grabUnderlying = grabUnderlying;
        _naturalBackground = naturalBackground;
        _sampleTextColor = sampleTextColor;
        _latinSourceToCjkTarget = IsLatinToCjk(sourceLanguage, targetLanguage);
        _textBrush = Freeze(new SolidColorBrush(RealtimeSubtitleColors.Text(textColor)));
        _scrimBrush = Freeze(new SolidColorBrush(
            RealtimeSubtitleColors.Scrim(scrimColor, scrimOpacity)));

        Closed += (_, _) => StopNaturalRefresh();

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }
            _isLoaded = true;
            Rebuild();
            UpdateNaturalRefreshTimer();
        };
    }

    public int RegionId { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through so clicks reach the application being watched, NoActivate so appearing over
        // a game never takes its focus — the block has to be furniture, not a window.
        WindowStyles.ApplyClickThrough(this, noActivate: true);

        // Before the DPI is read in Loaded: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);

        // Nothing here asks to be hidden from screen capture. This window used to carry
        // WDA_EXCLUDEFROMCAPTURE so the loop would not read its own translation back; keeping the
        // overlays out of the frame is now the capture backend's job and only the backend's, which is
        // why a session cannot start on a source that has not proved it (#105).
    }

    public void SetLines(IReadOnlyList<TranslatedBlock> lines)
    {
        // A rebuild cross-fades, so repainting an unchanged overlay is a visible flicker for no
        // gain. The session already suppresses re-translation of text that has not really changed;
        // this catches what is left — the same translation arriving with its boxes a pixel or two
        // off, which happens whenever recognition redraws its idea of where the line sits.
        if (_isLoaded && LooksIdentical(_lines, lines)) return;

        _lines = lines;
        if (_isLoaded)
        {
            Rebuild();
            UpdateNaturalRefreshTimer();
        }
    }

    // A pixel of movement is below what anyone can see and well inside recognition's own precision.
    private const double SamePositionTolerance = 2.0;

    private static bool LooksIdentical(IReadOnlyList<TranslatedBlock> current, IReadOnlyList<TranslatedBlock> next)
    {
        if (current.Count != next.Count) return false;

        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].TranslatedText, next[i].TranslatedText, StringComparison.Ordinal))
                return false;

            var a = current[i].Bounds;
            var b = next[i].Bounds;
            if (Math.Abs(a.X - b.X) > SamePositionTolerance ||
                Math.Abs(a.Y - b.Y) > SamePositionTolerance ||
                Math.Abs(a.Width - b.Width) > SamePositionTolerance ||
                Math.Abs(a.Height - b.Height) > SamePositionTolerance)
                return false;
        }

        return true;
    }

    private void Rebuild()
    {
        ScrimCanvas.Children.Clear();
        TextCanvas.Children.Clear();

        // Moved on before anything is drawn, so a batch of brushes already in flight for the previous
        // arrangement can be told it has been overtaken.
        Interlocked.Increment(ref _patchGeneration);
        var patches = new List<NaturalPatchVisual>();

        double canvasWidth = _physBounds.Width / _dpiX;
        double canvasHeight = _physBounds.Height / _dpiY;

        // Grabbed only if something is going to read it. With 進階選項 off this window draws from the
        // two colours alone, exactly as it did before those switches existed.
        using var frame = _naturalBackground || _sampleTextColor
            ? CaptureUnderlyingRegion()
            : null;

        foreach (var visual in BuildVisuals(canvasWidth, canvasHeight, frame))
        {
            TextCanvas.Children.Add(visual.Text);
            if (visual.Background is not { } background) continue;
            ScrimCanvas.Children.Add(background);

            // Only the repaired backgrounds are worth revisiting: a band drawn in a fixed colour has
            // nothing to follow, and recording one here would have the refresh below replace it with
            // a patch the user never asked for.
            if (_naturalBackground)
                patches.Add(new NaturalPatchVisual(background, visual.PatchBounds));
        }

        Volatile.Write(ref _naturalPatches, [.. patches]);

        // A new set of patches has to be painted once before there is anything to compare against.
        Volatile.Write(ref _lastRefreshPrint, null);
    }

    /// <summary>
    /// One line's background patch and translated text, kept in separate layers. A panel row after
    /// the first carries no background: its group's first row draws one patch for all of them.
    /// </summary>
    private readonly record struct LineVisual(
        Border? Background, Border Text, System.Drawing.Rectangle PatchBounds);

    private readonly record struct NaturalPatchVisual(
        Border Surface, System.Drawing.Rectangle PatchBounds);

    /// <summary>Outlines a background patch when 顯示外觀 → 邊框 is on.</summary>
    /// <remarks>
    /// A random colour is keyed on the source text, so a line does not change colour when it is
    /// rebuilt and a panel group's shared patch gets one colour however many rows it holds.
    /// </remarks>
    private void ApplyBorder(Border background, TranslatedBlock line)
    {
        if (!_border) return;
        background.BorderBrush = _fixedBorderBrush ??
            Freeze(new SolidColorBrush(RealtimeSubtitleColors.RandomBorder(line.OriginalText)));
        background.BorderThickness = new Thickness(RealtimeSubtitleColors.BorderThickness);
    }

    private LineVisual? BuildLine(
        TranslatedBlock line, double canvasWidth, double canvasHeight, System.Drawing.Bitmap? frame, System.Drawing.Bitmap? repairedFrame)
    {
        double left = line.Bounds.X / _dpiX;
        double sourceWidth = line.Bounds.Width / _dpiX;
        double sourceHeight = line.Bounds.Height / _dpiY;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        // Grouped sources carry several source lines under one translation, so their text wraps and
        // the scrim has to cover the whole group. A single source line never wraps: it is one line
        // on screen and stays one line.
        bool isGrouped = line.SourceLineBounds is { Count: > 1 };

        double glyphHeight = GetGlyphHeight(line, sourceHeight);
        double fontSize = SourceFontScale.Calculate(glyphHeight, _latinSourceToCjkTarget);

        // How tall the line being replaced actually is, which is not the same as how tall its
        // detection box is. A Latin source arrives with the full box — deliberately, because the
        // screenshot overlay wants it as coverage area — and that box runs about half again as tall
        // as the glyphs in it. A CJK source arrives with its box already shrunk onto the glyphs and
        // recentred, which is why picking Korean by mistake over English subtitles drew a visibly
        // tighter band than picking English did: 56px against 88px over the same 46px of text.
        //
        // The band exists to hide one line of text, and a band twice the height of that line is
        // exactly what this overlay's whole approach is meant to avoid — everything outside it is
        // supposed to stay picture the user is watching.
        double lineHeight = Math.Min(sourceHeight, glyphHeight * LineHeightRatio);

        // The whole block, not just the room to the right of the source's left edge: a band centred
        // on its source grows in both directions, so what bounds it is the block, and RealtimeBandPlacement
        // is what keeps it inside. Leaving the scrim's own padding out means the band fits exactly.
        double maxWidth = Math.Max(20, canvasWidth - ScrimPaddingX * 2);

        // This window is exactly the block the user drew, so a band taller than this is not merely
        // untidy — the part past the edge is not rendered at all. Only the wrapped fallback below
        // needs it; a single line is bounded by its source's own height long before it gets close.
        double maxTextHeight = Math.Max(lineHeight, canvasHeight - ScrimPaddingY * 2);
        var typeface = new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        double textWidth;
        double textHeight;
        // A grouped block wraps by definition; a single source line only wraps when it has run out
        // of every other way to stay whole, which the branch below decides.
        bool wrapped = isGrouped;
        if (isGrouped)
        {
            // Keep the group's own width and let the translation wrap inside it, shrinking only if
            // wrapping alone cannot make it fit the source's height.
            textWidth = Math.Min(maxWidth, Math.Max(sourceWidth, 20));
            fontSize = FitWrapped(line.TranslatedText, typeface, fontSize, textWidth, sourceHeight);
            textHeight = Measure(line.TranslatedText, typeface, fontSize, textWidth).Height;
        }
        else
        {
            // Sized to the line it replaces rather than to whatever the font scale would prefer.
            // The scale exists for the screenshot overlay, where a still is studied and a larger
            // translation is welcome; here the scrim it forces is a band across live content the
            // user is trying to watch, so a translation half again as tall as the line underneath
            // buys legibility with the picture.
            fontSize = Math.Max(MinFontSize, Math.Min(fontSize, lineHeight * MaxHeightOverSource / LineHeightRatio));

            // Kept because the wrapped fallback searches from here, not from whatever the width
            // shrink below left behind: wrapping buys width back, so a size that was too wide for
            // one line is often comfortable across two.
            double sourceMatchedFontSize = fontSize;

            var measured = Measure(line.TranslatedText, typeface, fontSize, null);
            if (measured.Width > maxWidth)
            {
                // Shrink to fit the room to the right of the source, down to the readability floor.
                fontSize = Math.Max(MinFontSize, fontSize * maxWidth / measured.Width);
                measured = Measure(line.TranslatedText, typeface, fontSize, null);
            }

            if (measured.Width > maxWidth)
            {
                // At the readability floor and still wider than the block. This used to be where the
                // line was trimmed, on the reasoning that an unreadable full line helps nobody — but
                // the line that got trimmed was readable, just too long, and what it turned into was
                // a sentence that ends early with nothing to say so. Over live video there is no
                // still to go back to and no way to notice, which makes it the worse of the two.
                //
                // So it wraps instead. The band grows, bounded by the block the user drew.
                wrapped = true;
                fontSize = FitWrapped(
                    line.TranslatedText, typeface, sourceMatchedFontSize, maxWidth, maxTextHeight);

                // The band is only as wide as the widest line the wrap actually produced, not the
                // whole block — the same reason a single line is not stretched to fill it. One pixel
                // of slack so rounding cannot leave the TextBlock a hair narrower than the measure.
                var acrossTheBlock = Measure(line.TranslatedText, typeface, fontSize, maxWidth);
                textWidth = Math.Min(maxWidth, acrossTheBlock.Width + 1);

                // Measured again at the width the TextBlock is actually given. Narrowing the band to
                // the widest line can change where a line breaks, and a height taken from the wider
                // measurement would then be one line short — with the box clipping, that is the
                // trimmed tail back again by another route.
                textHeight = Measure(line.TranslatedText, typeface, fontSize, textWidth).Height;
            }
            else
            {
                textWidth = Math.Min(maxWidth, measured.Width);
                textHeight = measured.Height;
            }
        }

        double scrimWidth = Math.Min(canvasWidth, Math.Max(textWidth, sourceWidth) + ScrimPaddingX * 2);

        // A union of loose Latin detection boxes still includes the first/last row's padding.
        // Compact each row around its own center before taking the vertical span; keep the gap.
        var coverage = isGrouped ? GroupedCoverage(line) : line.Bounds;
        double coverHeight = isGrouped ? coverage.Height / _dpiY : lineHeight;
        double scrimHeight = Math.Max(coverHeight, textHeight) + ScrimPaddingY * 2;

        // The scrim covers the source, so it grows around the source's own centre — horizontally as
        // well as vertically — rather than hanging off its top-left corner.
        double scrimLeft = RealtimeBandPlacement.Left(left, sourceWidth, scrimWidth, canvasWidth);
        double scrimTop = Math.Clamp(
            coverage.Y / _dpiY + coverage.Height / _dpiY / 2 - scrimHeight / 2,
            0, Math.Max(0, canvasHeight - scrimHeight));

        // The band's own geometry, which is also what the repaired patch falls back to. The guard
        // below only exists to hold the repair, so with 進階選項 off these stay as they are and the
        // background is the band this window has always drawn.
        double patchLeft = scrimLeft;
        double patchTop = scrimTop;
        double patchWidth = scrimWidth;
        double patchHeight = scrimHeight;
        ImageBrush? naturalBrush = null;

        if (_naturalBackground)
        {
            double naturalGuardX = Math.Clamp(sourceHeight * 0.20, MinNaturalGuardX, 20);
            double naturalGuardY = Math.Clamp(sourceHeight * 0.40, MinNaturalGuardY, 26);
            double guardLeft = Math.Max(0, scrimLeft - naturalGuardX);
            double guardTop = Math.Max(0, scrimTop - naturalGuardY);
            double guardRight = Math.Min(canvasWidth, scrimLeft + scrimWidth + naturalGuardX);
            double guardBottom = Math.Min(canvasHeight, scrimTop + scrimHeight + naturalGuardY);
            double guardWidth = Math.Max(0, guardRight - guardLeft);
            double guardHeight = Math.Max(0, guardBottom - guardTop);

            naturalBrush = BuildNaturalBrush(
                repairedFrame, ToPhysicalPatchBounds(guardLeft, guardTop, guardWidth, guardHeight));

            // Capture can fail on protected/UAC surfaces. Keep the compact band in that case rather
            // than painting the much larger guard with a flat colour.
            if (naturalBrush is not null)
            {
                patchLeft = guardLeft;
                patchTop = guardTop;
                patchWidth = guardWidth;
                patchHeight = guardHeight;
            }
        }

        var patchBounds = ToPhysicalPatchBounds(patchLeft, patchTop, patchWidth, patchHeight);

        var background = new Border
        {
            Width = patchWidth,
            Height = patchHeight,
            Background = (System.Windows.Media.Brush?)naturalBrush ?? _scrimBrush,
            // A repaired patch has to meet the surrounding picture edge-for-edge: rounded corners
            // would leave four pieces of the source text showing through. A band is the opposite case
            // — it is visibly a band, and the corners are what stop it looking like a crash — so the
            // radius belongs to the kind of background being drawn rather than to the window.
            CornerRadius = new CornerRadius(naturalBrush is null ? BandCornerRadius : 0),
        };
        ApplyBorder(background, line);

        // Sampling is its own switch: with it off the reader's chosen colour is what gets drawn, and
        // with it on that colour is still what an unconvincing sample falls back to.
        var foreground = _textBrush;
        if (_sampleTextColor && frame is not null)
        {
            var sampled = RealtimeNaturalBackground.SampleTextColor(frame, line.Bounds, _textBrush.Color);
            foreground = Freeze(new SolidColorBrush(sampled));
        }

        // Same geometry, no background: the two are stacked in separate layers, so the text has to
        // carry its own box to land in exactly the place the repaired background covers.
        var text = new Border
        {
            Width = scrimWidth,
            Height = scrimHeight,
            Padding = new Thickness(ScrimPaddingX, ScrimPaddingY, ScrimPaddingX, ScrimPaddingY),
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = line.TranslatedText,
                FontFamily = TextFont,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                TextWrapping = wrapped ? TextWrapping.Wrap : TextWrapping.NoWrap,
                // Never trimmed. A line that cannot fit on one line has already been switched to
                // wrapping above, so getting here without it means the text fits; leaving
                // CharacterEllipsis on would only mean a measurement a pixel out costs a word.
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,

                // Centred inside the box, which is what keeps a translation over the middle of the
                // line it replaces. The box is now as wide as the wider of the two — the background
                // has to cover the source, and a translation that came back shorter than its source
                // would otherwise sit against the left edge of a band sized by the source, drifting
                // away from the words it stands in for by exactly the amount RealtimeBandPlacement
                // exists to spend evenly.
                //
                // One line is centred as text; a wrapped one is centred as a block, with its own
                // lines still starting at the same left edge. A paragraph with every line centred is
                // a poster, and this is something the reader has to get through at speed.
                TextAlignment = wrapped ? TextAlignment.Left : TextAlignment.Center,
                HorizontalAlignment = wrapped
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Stretch,
            }
        };

        Canvas.SetLeft(background, patchLeft);
        Canvas.SetTop(background, patchTop);
        Canvas.SetLeft(text, scrimLeft);
        Canvas.SetTop(text, scrimTop);

        return new LineVisual(background, text, patchBounds);
    }

    private IEnumerable<LineVisual> BuildVisuals(
        double canvasWidth, double canvasHeight, System.Drawing.Bitmap? frame)
    {
        using var repairedFrame = _naturalBackground ? RepairNaturalFrame(frame, _lines) : null;
        var typeface = new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var block in _lines)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            // RunsAcross is the reading stage saying this block is horizontal writing that happened
            // to be on a vertical page — a name plate, a caption box, a scene label. It falls
            // through to the branches below, which are the ones that set a line across.
            if (_orientation == RealtimeTextOrientation.Vertical && !block.RunsAcross)
            {
                // Before the mode, and instead of it. Both of the horizontal branches below are
                // about lines that run across — one wraps a paragraph into rows, the other fits a
                // band to the row it replaces — and neither question exists for a column. What the
                // mode still decides for a vertical block is upstream, in how the frame was read.
                if (BuildVerticalLine(block, canvasWidth, canvasHeight, frame, repairedFrame) is { } column)
                    yield return column;
            }
            else if (_mode == RealtimeBlockMode.Panel)
            {
                double fontSize = Math.Max(1, Math.Min(
                    SourceFontScale.Calculate(GetGlyphHeight(block, block.Bounds.Height / _dpiY),
                        _latinSourceToCjkTarget),
                    (block.SourceLineBounds ?? [block.Bounds]).Min(r => r.Height) / _dpiY / LineHeightRatio));
                var rows = RealtimePanelLines.Split(block,
                    text => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        typeface, fontSize, Brushes.Black, _dpiY).WidthIncludingTrailingWhitespace * _dpiX,
                    RealtimePanelLines.AvailableWidths(block, _lines));
                if (rows.Count == 1 && rows[0].SourceLineBounds is { Count: > 1 })
                {
                    if (BuildLine(block, canvasWidth, canvasHeight, frame, repairedFrame) is { } fallback) yield return fallback;
                    continue;
                }
                // Keep one font size across the paragraph, including uneven row widths.
                foreach (var row in rows)
                {
                    var measured = Measure(row.TranslatedText, typeface, fontSize, null);
                    double scale = Math.Min(1, Math.Min(
                        row.Bounds.Width / _dpiX / Math.Max(1, measured.Width),
                        row.Bounds.Height / _dpiY / Math.Max(1, measured.Height)));
                    fontSize *= scale;
                }
                // One patch over the whole group, as the wrapped block drew before rows were split:
                // per-row patches left the gaps between OCR boxes and their ragged widths showing,
                // so a joined group no longer read as one unit.
                var scrim = rows.Select(row => row.Bounds).Aggregate(Rect.Union);
                for (int index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];
                    // Invalid source geometry keeps the original whole-block fallback.
                    var visual = row.SourceLineBounds is { Count: > 1 }
                        ? BuildLine(row, canvasWidth, canvasHeight, frame, repairedFrame)
                        : BuildPanelLine(row, canvasWidth, canvasHeight, frame, fontSize, index == 0 ? scrim : null, repairedFrame);
                    if (visual is { } drawn) yield return drawn;
                }
            }
            else if (BuildLine(block, canvasWidth, canvasHeight, frame, repairedFrame) is { } visual)
                yield return visual;
        }
    }

    /// <summary>
    /// One vertical column group: the translation set downwards in square cells over the source it
    /// replaces, against the top of it and centred across it.
    /// </summary>
    /// <remarks>
    /// The two halves of that anchor are not the same decision, and <see cref="VerticalTextGrid.Cells"/>
    /// is where both are written down — the screenshot overlay sets its bubbles by the same rule.
    /// Along the writing the top is where the source sentence began; across it there is no such
    /// corner, so slack is spent on both sides as the horizontal band spends it (see
    /// <see cref="RealtimeBandPlacement"/>).
    ///
    /// The grid never grows past the block. This window is exactly the rectangle the user drew, so
    /// anything outside it is not rendered — and a sentence ending early with nothing to say so is
    /// the failure the wrapped fallback in <see cref="BuildLine"/> exists to avoid. The cell shrinks
    /// instead; see <see cref="VerticalTextGrid.FitWithin"/>.
    /// </remarks>
    private LineVisual? BuildVerticalLine(
        TranslatedBlock line, double canvasWidth, double canvasHeight,
        System.Drawing.Bitmap? frame, System.Drawing.Bitmap? repairedFrame)
    {
        double left = line.Bounds.X / _dpiX;
        double top = line.Bounds.Y / _dpiY;
        double sourceWidth = line.Bounds.Width / _dpiX;
        double sourceHeight = line.Bounds.Height / _dpiY;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        // Spaces are dropped rather than given a cell of their own. A blank square in the middle of
        // a column does not read as a word break in vertical writing, it reads as the end of the
        // line — and a translation into Chinese or Japanese has no word spaces to lose anyway.
        string glyphs = new([.. line.TranslatedText.Where(character => !char.IsWhiteSpace(character))]);
        if (glyphs.Length == 0) return null;

        // The cell is square and sized on the column's character pitch, kept separate from the
        // detector's padded coverage width — see VerticalOcrGeometry.PrepareBlocks.
        double cellPreferred = Math.Max(MinFontSize, GetGlyphHeight(line, sourceWidth));

        // THE COLUMN IS AS LONG AS THE ONE IT REPLACES, not as long as the block. This is the
        // vertical half of what the band does when it sizes a line to the row underneath it rather
        // than to the whole block — and getting it wrong is not untidy here, it is unreadable: over
        // a comic page the block IS the page, so a grid given the block's height fits every sentence
        // into a single column and runs it from the top of the picture to the bottom, straight
        // across the panels either side of the balloon it came from.
        //
        // The source's own width is handed over for the same reason: a translation too long for the
        // balloon is set smaller inside it before it is allowed to take a column beside it, because
        // the column beside it is picture. Only the block bounds how far that may finally reach.
        double columnLength = Math.Max(cellPreferred, Math.Min(sourceHeight, canvasHeight - ScrimPaddingY * 2));
        double columnRoom = Math.Max(cellPreferred, sourceWidth);
        double maxWidth = Math.Max(columnRoom, canvasWidth - ScrimPaddingX * 2);

        var (cellSize, gridWidth, gridHeight) = VerticalTextGrid.FitWithin(
            columnRoom, columnLength, maxWidth, cellPreferred, MinFontSize, glyphs.Length);

        // Top of the source, centred across it, kept inside the block — the rule
        // VerticalTextGrid.Cells sets a grid by, applied here because the grid handed to it is cut
        // to size and so has no slack of its own left to centre in.
        double gridLeft = Math.Clamp(
            left + (sourceWidth - gridWidth) / 2, 0, Math.Max(0, canvasWidth - gridWidth));
        double gridTop = Math.Clamp(top, 0, Math.Max(0, canvasHeight - gridHeight));

        // The background has to cover both: the source, which is the thing being hidden, and the
        // grid, which may reach further left when the translation needs more columns than the
        // original had.
        double scrimLeft = Math.Max(0, Math.Min(gridLeft, left) - ScrimPaddingX);
        double scrimTop = Math.Max(0, Math.Min(gridTop, top) - ScrimPaddingY);
        double scrimRight = Math.Min(
            canvasWidth, Math.Max(gridLeft + gridWidth, left + sourceWidth) + ScrimPaddingX);
        double scrimBottom = Math.Min(
            canvasHeight, Math.Max(gridTop + gridHeight, top + sourceHeight) + ScrimPaddingY);
        double scrimWidth = Math.Max(0, scrimRight - scrimLeft);
        double scrimHeight = Math.Max(0, scrimBottom - scrimTop);

        double patchLeft = scrimLeft;
        double patchTop = scrimTop;
        double patchWidth = scrimWidth;
        double patchHeight = scrimHeight;
        ImageBrush? naturalBrush = null;

        if (_naturalBackground)
        {
            // The guard is the horizontal one turned with the writing. What it is there for is the
            // detached marks the detection box leaves out, and in vertical Japanese the dakuten sits
            // to the right of its glyph rather than above it — so the generous side is X here, and
            // both are measured from the column's width instead of a line's height.
            double naturalGuardX = Math.Clamp(sourceWidth * 0.40, MinNaturalGuardY, 26);
            double naturalGuardY = Math.Clamp(sourceWidth * 0.20, MinNaturalGuardX, 20);
            double guardLeft = Math.Max(0, scrimLeft - naturalGuardX);
            double guardTop = Math.Max(0, scrimTop - naturalGuardY);
            double guardRight = Math.Min(canvasWidth, scrimLeft + scrimWidth + naturalGuardX);
            double guardBottom = Math.Min(canvasHeight, scrimTop + scrimHeight + naturalGuardY);
            double guardWidth = Math.Max(0, guardRight - guardLeft);
            double guardHeight = Math.Max(0, guardBottom - guardTop);

            naturalBrush = BuildNaturalBrush(
                repairedFrame, ToPhysicalPatchBounds(guardLeft, guardTop, guardWidth, guardHeight));

            if (naturalBrush is not null)
            {
                patchLeft = guardLeft;
                patchTop = guardTop;
                patchWidth = guardWidth;
                patchHeight = guardHeight;
            }
        }

        var patchBounds = ToPhysicalPatchBounds(patchLeft, patchTop, patchWidth, patchHeight);

        var background = new Border
        {
            Width = patchWidth,
            Height = patchHeight,
            Background = (System.Windows.Media.Brush?)naturalBrush ?? _scrimBrush,
            CornerRadius = new CornerRadius(naturalBrush is null ? BandCornerRadius : 0),
        };
        ApplyBorder(background, line);

        var foreground = _textBrush;
        if (_sampleTextColor && frame is not null)
        {
            var sampled = RealtimeNaturalBackground.SampleTextColor(frame, line.Bounds, _textBrush.Color);
            foreground = Freeze(new SolidColorBrush(sampled));
        }

        // One element per glyph, positioned by hand. A TextBlock cannot set type downwards, and the
        // alternatives — a rotated horizontal line, or a font feature — either turn every character
        // on its side or are unavailable on the fallback faces this has to survive on.
        var cells = new Canvas { Width = gridWidth, Height = gridHeight };
        foreach (var (glyph, cellBounds) in
                 VerticalTextGrid.Cells(glyphs, new Rect(0, 0, gridWidth, gridHeight), cellSize))
        {
            var cell = new TextBlock
            {
                Text = glyph.ToString(),
                FontFamily = TextFont,
                FontSize = cellSize * VerticalGlyphFill,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                TextAlignment = TextAlignment.Center,
            };

            // Brackets and dashes are drawn lying down in horizontal text and standing up in
            // vertical: the glyph is the same, the orientation is not.
            if (VerticalTextGrid.RotatesGlyph(glyph))
            {
                cell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                cell.RenderTransform = new RotateTransform(90);
            }

            VerticalTextGrid.PositionGlyph(cell, cellBounds);
            cells.Children.Add(cell);
        }

        var textLayer = new Border
        {
            Width = gridWidth,
            Height = gridHeight,
            ClipToBounds = true,
            Child = cells,
        };

        Canvas.SetLeft(background, patchLeft);
        Canvas.SetTop(background, patchTop);
        Canvas.SetLeft(textLayer, gridLeft);
        Canvas.SetTop(textLayer, gridTop);

        return new LineVisual(background, textLayer, patchBounds);
    }

    private LineVisual? BuildPanelLine(
        TranslatedBlock line, double canvasWidth, double canvasHeight, System.Drawing.Bitmap? frame,
        double fontSize, Rect? scrim, System.Drawing.Bitmap? repairedFrame)
    {
        var canvas = new Rect(0, 0, canvasWidth * _dpiX, canvasHeight * _dpiY);
        var cell = Rect.Intersect(line.Bounds, canvas);
        if (cell.IsEmpty || cell.Width <= 0 || cell.Height <= 0) return null;
        double left = cell.X / _dpiX, top = cell.Y / _dpiY;
        double width = cell.Width / _dpiX, height = cell.Height / _dpiY;
        var foreground = _textBrush;
        if (_sampleTextColor && frame is not null)
            foreground = Freeze(new SolidColorBrush(
                RealtimeNaturalBackground.SampleTextColor(frame, line.Bounds, _textBrush.Color)));
        Border? background = null;
        var patchBounds = default(System.Drawing.Rectangle);
        if (scrim is { } scrimBounds && Rect.Intersect(scrimBounds, canvas) is { IsEmpty: false } patch)
        {
            double patchLeft = patch.X / _dpiX, patchTop = patch.Y / _dpiY;
            double patchWidth = patch.Width / _dpiX, patchHeight = patch.Height / _dpiY;
            patchBounds = ToPhysicalPatchBounds(patchLeft, patchTop, patchWidth, patchHeight);
            var naturalBrush = _naturalBackground ? BuildNaturalBrush(repairedFrame, patchBounds) : null;
            background = new Border
            {
                Width = patchWidth, Height = patchHeight,
                Background = (System.Windows.Media.Brush?)naturalBrush ?? _scrimBrush,
            };
            ApplyBorder(background, line);
            Canvas.SetLeft(background, patchLeft);
            Canvas.SetTop(background, patchTop);
        }
        // A segment already owns a source row. Do not wrap it a second time, expand
        // into neighbouring columns, or truncate a long word that could not be split.
        var text = new Border
        {
            Width = width, Height = height, ClipToBounds = true,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = line.TranslatedText, FontFamily = TextFont, FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold, Foreground = foreground,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
                },
            },
        };
        Canvas.SetLeft(text, left);
        Canvas.SetTop(text, top);
        return new LineVisual(background, text, patchBounds);
    }

    private void UpdateNaturalRefreshTimer()
    {
        // Never runs with the switch off: the patches it would refresh are only recorded when the
        // repair is what drew them.
        if (!_naturalBackground || !_isLoaded || Volatile.Read(ref _naturalPatches).Length == 0)
            StopNaturalRefresh();
        else
            StartNaturalRefresh();
    }

    private void StartNaturalRefresh()
    {
        if (_naturalRefreshCts is not null) return;

        var cts = new CancellationTokenSource();
        _naturalRefreshCts = cts;
        _ = Task.Run(() => RefreshNaturalBackgroundsAsync(cts.Token), cts.Token);
    }

    private void StopNaturalRefresh()
    {
        _naturalRefreshCts?.Cancel();
        _naturalRefreshCts?.Dispose();
        _naturalRefreshCts = null;
    }

    /// <summary>
    /// Keeps the repaired backgrounds following the picture underneath, off the dispatcher.
    /// </summary>
    /// <remarks>
    /// The only step here that needs the interface thread is the last one, so it is the only step that
    /// goes back to it: the grab is a BitBlt, the repair is pixel arithmetic, and the brush is frozen
    /// before it leaves this thread. What used to be a DispatcherTimer doing all of it is why a
    /// session with three blocks made the shell feel heavy.
    ///
    /// A tick that finds the picture holding still stops after the fingerprint. That is the ordinary
    /// case for the content this mode suits best — a dialogue panel, a menu, a paused scene — and the
    /// patch on screen is still correct for it, so there is nothing to pay for.
    /// </remarks>
    private async Task RefreshNaturalBackgroundsAsync(CancellationToken token)
    {
        try
        {
            var rest = RealtimeBackgroundCadence.TargetInterval;
            while (true)
            {
                await Task.Delay(rest, token);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var generation = Volatile.Read(ref _patchGeneration);
                    var patches = Volatile.Read(ref _naturalPatches);
                    var blocks = _lines;
                    if (patches.Length == 0) continue;

                    using var frame = CaptureUnderlyingRegion();
                    if (frame is null) continue;

                    // Summarised over the patch rectangles rather than the whole block: a change in a
                    // corner of the region the band does not cover is not a reason to repaint it.
                    var print = FrameFingerprint.Capture(
                        frame, [.. patches.Select(patch => patch.PatchBounds)]);
                    if (print.StillLooksLike(Volatile.Read(ref _lastRefreshPrint))) continue;

                    using var repairedFrame = RepairNaturalFrame(frame, blocks, token);
                    if (repairedFrame is null) continue;
                    var repainted = new List<(Border Surface, ImageBrush Brush)>(patches.Length);
                    foreach (var patch in patches)
                    {
                        token.ThrowIfCancellationRequested();
                        if (BuildNaturalBrush(repairedFrame, patch.PatchBounds) is { } brush)
                            repainted.Add((patch.Surface, brush));
                    }

                    if (repainted.Count == 0) continue;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        // A rebuild while this batch was being painted means these surfaces are no longer
                        // the ones on the canvas.
                        if (token.IsCancellationRequested || Volatile.Read(ref _patchGeneration) != generation) return;

                        foreach (var (surface, brush) in repainted)
                            surface.Background = brush;
                        Volatile.Write(ref _lastRefreshPrint, print);
                    });
                }
                finally
                {
                    // Rest after completion, including skipped/failed captures; never queue timer ticks.
                    rest = RealtimeBackgroundCadence.RestAfter(Stopwatch.GetElapsedTime(started));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing or the arrangement changed; nothing to report.
        }
        catch (Exception ex)
        {
            // Nothing above is expected to throw, and this loop is nobody's awaited task — so an
            // exception here would end the refresh silently and leave the patches frozen on the last
            // picture they saw, which looks like the repair itself having failed.
            Log.Warn(ex, "Realtime block {Region} stopped refreshing its repaired background", RegionId);
        }
    }

    /// <summary>
    /// The picture the repair is built from: this window's own rectangle, as the session's capture
    /// backend sees it — which is to say, without this window in it.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>CopyFromScreen</c> of the same rectangle, and it read whatever was on
    /// screen, this overlay included. What kept the overlay out of it was
    /// <c>WDA_EXCLUDEFROMCAPTURE</c> on this window, which fails on every Windows before 11 24H2 —
    /// so there the repair photographed the translation it had drawn a moment earlier and baked it
    /// into the "restored" background, on every poll, on top of itself (#99). Asking the backend
    /// instead makes that impossible rather than unlikely: the frame comes from a source that either
    /// never contained this window (a captured application window) or was composed without it (a
    /// monitor capture with the session's exclusion list), and there is no arrangement of Windows
    /// versions in which it contains our own subtitles.
    ///
    /// It also settles #98. The backend reads one frame back off the GPU and every caller crops out
    /// of that, so the region under a block is no longer captured once for recognition and again for
    /// the repair — the second grab is a copy out of a bitmap that already exists.
    ///
    /// Null is ordinary and means only "not this tick": the backend is between frames, the exclusion
    /// list has just changed and the frame composed under the old one was dropped, or this block does
    /// not lie over the captured window at all. Every caller falls back to the band, which is the
    /// same thing they did when the grab failed on a protected surface.
    /// </remarks>
    private System.Drawing.Bitmap? CaptureUnderlyingRegion()
    {
        if (_physBounds.Width <= 0 || _physBounds.Height <= 0) return null;

        try
        {
            return _grabUnderlying(_physBounds);
        }
        catch (Exception ex)
        {
            // The backend is being torn down under a refresh tick — going back to edit mode disposes
            // it before these windows close. Nothing to repair this tick, and nothing to report four
            // times a second.
            Log.Debug(ex, "Realtime block {Region} could not read the picture under it", RegionId);
            return null;
        }
    }

    private System.Drawing.Rectangle ToPhysicalPatchBounds(
        double left, double top, double width, double height)
    {
        int x1 = Math.Clamp((int)Math.Floor(left * _dpiX), 0, _physBounds.Width);
        int y1 = Math.Clamp((int)Math.Floor(top * _dpiY), 0, _physBounds.Height);
        int x2 = Math.Clamp((int)Math.Ceiling((left + width) * _dpiX), 0, _physBounds.Width);
        int y2 = Math.Clamp((int)Math.Ceiling((top + height) * _dpiY), 0, _physBounds.Height);
        return System.Drawing.Rectangle.FromLTRB(x1, y1, x2, y2);
    }

    private static System.Drawing.Bitmap? RepairNaturalFrame(
        System.Drawing.Bitmap? frame, IReadOnlyList<TranslatedBlock> blocks, CancellationToken token = default)
    {
        if (frame is null) return null;
        try { return RealtimeCpuBackground.Repair(frame, blocks, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not repair realtime CPU background");
            return null;
        }
    }

    /// <summary>Crops a shared CPU-repaired frame into a frozen overlay brush.</summary>
    private static ImageBrush? BuildNaturalBrush(
        System.Drawing.Bitmap? frame,
        System.Drawing.Rectangle patchBounds)
    {
        if (frame is null || patchBounds.Width <= 0 || patchBounds.Height <= 0) return null;

        var clipped = System.Drawing.Rectangle.Intersect(new(0, 0, frame.Width, frame.Height), patchBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return null;
        using var patch = frame.Clone(clipped, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        var image = BitmapInterop.ToBitmapSource(patch);
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    // Largest size at which the wrapped translation still fits the height it is allowed. For a
    // grouped block that is the height its source occupied, so its scrim does not push over
    // whatever sits below; for a single line forced to wrap it is the whole block, which is this
    // window, so anything taller is cut off by the window edge.
    //
    // Returning MinFontSize when nothing fits is deliberate. The floor is where text stops being
    // readable, and going under it to win back a few pixels trades one unreadable result for
    // another; a band that overflows a block drawn too tight is at least visibly that.
    private double FitWrapped(
        string text, Typeface typeface, double preferredFontSize, double width, double maxHeight)
    {
        for (double size = preferredFontSize; size >= MinFontSize; size -= 0.5)
            if (Measure(text, typeface, size, width).Height <= maxHeight)
                return size;

        return MinFontSize;
    }

    private static Rect GroupedCoverage(TranslatedBlock line)
    {
        // CJK-normalized boxes already carry compact coverage. Do not shrink them a second time.
        if (line.RenderGlyphHeight is not { } glyphHeight || !double.IsFinite(glyphHeight) || glyphHeight <= 0 ||
            line.SourceLineBounds is not { Count: > 1 } rows)
            return line.Bounds;

        double top = double.PositiveInfinity;
        double bottom = double.NegativeInfinity;
        foreach (var row in rows)
        {
            if (row.IsEmpty || row.Height <= 0 || !double.IsFinite(row.Y) || !double.IsFinite(row.Height))
                return line.Bounds;
            double height = Math.Min(row.Height, glyphHeight * LineHeightRatio);
            double center = row.Y + row.Height / 2;
            top = Math.Min(top, center - height / 2);
            bottom = Math.Max(bottom, center + height / 2);
        }
        return new Rect(line.Bounds.X, top, line.Bounds.Width, bottom - top);
    }

    private double GetGlyphHeight(TranslatedBlock line, double fallbackHeight)
    {
        // Latin sources carry the real glyph height separately: their detection box is much taller
        // than the text in it, and sizing the font from the box would render the translation far
        // larger than what it replaces.
        if (line.RenderGlyphHeight is { } glyphHeight && glyphHeight > 0)
            return glyphHeight / _dpiY;

        if (line.SourceLineBounds is not { Count: > 0 } lineBounds)
            return fallbackHeight;

        var heights = lineBounds.Select(bounds => bounds.Height / _dpiY).OrderBy(height => height).ToList();
        return heights[heights.Count / 2];
    }

    private Size Measure(string text, Typeface typeface, double fontSize, double? maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            _dpiY);

        if (maxWidth.HasValue) formatted.MaxTextWidth = maxWidth.Value;

        // FormattedText reports the ink height, which for a single line of CJK sits noticeably
        // below the line box the TextBlock will actually lay out. Sizing the scrim from the ink
        // would clip descenders on the very first line.
        return new Size(formatted.Width, Math.Max(formatted.Height, fontSize * LineHeightRatio));

    }

    private static bool IsLatinToCjk(string sourceLanguage, string targetLanguage) =>
        sourceLanguage.Equals("EN", StringComparison.OrdinalIgnoreCase) &&
        (targetLanguage.StartsWith("ZH", StringComparison.OrdinalIgnoreCase) ||
         targetLanguage.Equals("JA", StringComparison.OrdinalIgnoreCase) ||
         targetLanguage.Equals("KO", StringComparison.OrdinalIgnoreCase));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
