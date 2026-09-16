using System.Buffers;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Layout;

namespace OverTranslate.Views.Overlay;

public partial class OverlayWindow : Window
{
    private const double OverlayPadding = 6;
    private const double BubbleExpand = 2;
    private const double BubbleMinWidth = 30;
    private const double BubbleHorizontalPadding = 6;
    private const double BubbleVerticalPadding = 6;
    private const double SingleLineAbsoluteMinFontSize = 9.5;
    private const double SingleLineReadableMinFontSize = 10.0;
    private const double SingleLineEmergencyMinFontSize = 7.0;
    private const double WrappedAbsoluteMinFontSize = 11.0;
    private const double GroupedEmergencyMinFontSize = 7.0;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    // Pixel rect this window covers. Left/Top/Width/Height cannot stand in for it: they are DIP
    // scaled by one monitor's DPI, which on a mixed-DPI desktop is not the rect the OCR coordinates
    // (physical pixels) are measured in.
    private readonly System.Drawing.Rectangle _physBounds = ScreenGeometry.VirtualDesktopBounds();

    // Debug geometry, drawn only when the setting asks for it. The two layers nest, so they are
    // given different weights as well as different colours: the recogniser's lines are a thin solid
    // box, and the group that owns them a dashed one just outside. Alpha low enough that the
    // capture underneath still reads, which is the whole point of looking at it.
    private const byte DebugFillAlpha = 0x26;
    private static readonly System.Windows.Media.Color OcrLineBoxColor = System.Windows.Media.Color.FromRgb(0x4D, 0xA3, 0xFF);
    private static readonly System.Windows.Media.Color TextGroupBoxColor = System.Windows.Media.Color.FromRgb(0xFF, 0xB4, 0x54);

    private bool _isLoaded;
    private bool _translationVisible;
    private List<TranslatedBlock> _currentBlocks;
    private IReadOnlyList<OcrTextBlock> _currentOcrBlocks;
    private double _currentSelectionScreenX;
    private double _currentSelectionScreenY;
    private double _currentSelectionScreenWidth;
    private double _currentSelectionScreenHeight;
    private string _currentSourceLanguage;
    private string _currentTargetLanguage;
    private bool _currentVerticalText;

    /// <summary>
    /// The repaired capture each bubble is painted from, when there is one. Null leaves every
    /// bubble on the flat sampled colour, which is what this overlay drew before and what it still
    /// draws whenever the repair could not be built.
    /// </summary>
    private CaptureBubbleBackdrop? _backdrop;

    internal OverlayWindow(
        List<TranslatedBlock> blocks,
        IReadOnlyList<OcrTextBlock> ocrBlocks,
        double selectionScreenX,
        double selectionScreenY,
        double selectionScreenWidth,
        double selectionScreenHeight,
        string sourceLanguage,
        string targetLanguage,
        bool verticalText,
        CaptureBubbleBackdrop? backdrop = null)
    {
        InitializeComponent();
        _currentBlocks = blocks;
        _backdrop = backdrop;
        _translationVisible = blocks.Count > 0;
        _currentOcrBlocks = ocrBlocks;
        _currentSelectionScreenX = selectionScreenX;
        _currentSelectionScreenY = selectionScreenY;
        _currentSelectionScreenWidth = selectionScreenWidth;
        _currentSelectionScreenHeight = selectionScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        _currentVerticalText = verticalText;
        SettingsService.Instance.OcrDebugChanged += OnOcrDebugChanged;
        Closed += (_, _) => SettingsService.Instance.OcrDebugChanged -= OnOcrDebugChanged;

        // Provisional: OnSourceInitialized pins the window to _physBounds instead.
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) =>
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                _dpiX = src.CompositionTarget.TransformToDevice.M11;
                _dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            _isLoaded = true;
            ApplyAnnotationBounds();
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Never takes activation, not even while 標記 has handed it the pointer. Topmost is a band,
        // and activating a window moves it to the front of that band — so a single click on the ink
        // surface used to lift this window over both toolbars. Wherever a toolbar overlaps the
        // selection (which is where it is put when there is no room outside it) that buried it: the
        // pointer found the ink surface instead of the buttons, and the only tool still reachable
        // was the one already in hand.
        //
        // Nothing here wants the focus anyway. Every key this feature answers to arrives through a
        // low-level hook precisely because none of these windows take it — see AnnotationShortcutHook.
        WindowStyles.ApplyClickThrough(this, noActivate: true);

        // WS_EX_NOACTIVATE stops this window being activated; it does not stop the click asking for
        // somebody to be activated. DefWindowProc forwards WM_MOUSEACTIVATE to the owner, so a press
        // on the ink surface activated ScreenCaptureWindow instead — and activating an owner raises
        // it together with everything it owns, in an order that is not the one it had. That is how a
        // window carrying NOACTIVATE still ended up in front of both toolbars after one stroke.
        //
        // MA_NOACTIVATE, not MA_NOACTIVATEANDEAT: the click still has to arrive as a stroke.
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(RefuseMouseActivate);

        // Before Loaded reads the DPI: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private static IntPtr RefuseMouseActivate(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;

        handled = true;
        return (IntPtr)MA_NOACTIVATE;
    }

    // Shows a centered status card and clears old bubbles so the indicator is unobstructed.
    public void ShowProcessing(double selPhysX, double selPhysY, double selPhysW, double selPhysH, string statusText)
    {
        _currentOcrBlocks = [];
        _translationVisible = false;
        UpdateDebugVisibility();
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();
        DebugCanvas.Children.Clear();

        double winPhysLeft = _physBounds.Left;
        double winPhysTop  = _physBounds.Top;

        ProcessingText.Text = statusText;
        ProcessingBorder.Visibility = Visibility.Hidden;

        // This window spans every monitor and so renders at a single DPI; on a monitor at another
        // scale the card would come out the wrong physical size. Applied before Measure so the
        // desired size below is the transformed one the centring needs.
        double relScale = ScreenGeometry.ScaleAt(
            (int)(selPhysX + selPhysW / 2), (int)(selPhysY + selPhysH / 2)) / _dpiX;
        ProcessingBorder.LayoutTransform = relScale == 1.0
            ? Transform.Identity
            : new ScaleTransform(relScale, relScale);

        ProcessingBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ProcessingBorder.DesiredSize;
        double cx = (selPhysX + selPhysW / 2 - winPhysLeft) / _dpiX - desired.Width  / 2;
        double cy = (selPhysY + selPhysH / 2 - winPhysTop)  / _dpiY - desired.Height / 2;
        Canvas.SetLeft(ProcessingBorder, cx);
        Canvas.SetTop(ProcessingBorder,  cy);
        ProcessingBorder.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Takes the reading of a capture, before any translation of it exists.
    /// </summary>
    /// <remarks>
    /// The debug boxes describe the source, not the answer, so they are shown from the moment the
    /// recogniser has finished — through the translating indicator, and on a capture whose
    /// translation failed or was never asked for.
    /// </remarks>
    public void ShowOcrDebug(
        IReadOnlyList<OcrTextBlock> ocrBlocks, double selScreenX, double selScreenY)
    {
        _currentOcrBlocks = ocrBlocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        if (_isLoaded)
            BuildDebugBoxes(selScreenX, selScreenY);
    }

    internal void UpdateBlocks(
        List<TranslatedBlock> blocks,
        IReadOnlyList<OcrTextBlock> ocrBlocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight,
        string sourceLanguage,
        string targetLanguage,
        bool verticalText,
        CaptureBubbleBackdrop? backdrop = null)
    {
        _currentBlocks = blocks;
        _backdrop = backdrop;
        _currentOcrBlocks = ocrBlocks;
        _currentSelectionScreenX = selScreenX;
        _currentSelectionScreenY = selScreenY;
        _currentSelectionScreenWidth = selScreenWidth;
        _currentSelectionScreenHeight = selScreenHeight;
        _currentSourceLanguage = sourceLanguage;
        _currentTargetLanguage = targetLanguage;
        _currentVerticalText = verticalText;
        ProcessingBorder.Visibility = Visibility.Collapsed;
        SetTranslationLayersVisible(true);
        if (_isLoaded)
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
    }

    public void RestoreIdle(bool hasVisibleBlocks)
    {
        ProcessingBorder.Visibility = Visibility.Collapsed;
        if (hasVisibleBlocks && _isLoaded && BubbleBackgroundCanvas.Children.Count == 0 && _currentBlocks.Count > 0)
            BuildOverlay(
                _currentBlocks,
                _currentSelectionScreenX,
                _currentSelectionScreenY,
                _currentSelectionScreenWidth,
                _currentSelectionScreenHeight);
        SetTranslationLayersVisible(hasVisibleBlocks);
    }

    public void SetBubblesVisible(bool visible) => SetTranslationLayersVisible(visible);

    // Renders everything this window has laid over the capture — the translation bubbles and the
    // marks drawn on top of them — cropped to the given selection region (physical pixels), for the
    // "copy screenshot" feature. Returns null when there is nothing laid over it at all.
    //
    // The three layers are rendered one at a time rather than by rendering the window's content in
    // one go, so that what ends up in the picture is stated here rather than inferred from what
    // happens to be visible. The processing indicator is the reason it matters: it is a piece of
    // chrome saying work is in progress, it is emphatically not part of the capture, and it used to
    // be kept out only as a side effect of the bubble canvases being empty while it showed — which
    // stopped being true the moment a mark could exist before any translation had run.
    public System.Windows.Media.Imaging.BitmapSource? RenderOverlayForSelection(
        double selPhysLeft, double selPhysTop, int selPhysWidth, int selPhysHeight)
    {
        if (!_isLoaded) return null;

        // Debug boxes are on-screen inspection aids, not exported content. Marks still count:
        // someone can draw before translation, or copy the original with their own annotations.
        bool hasBubbles = BubbleBackgroundCanvas.Visibility == Visibility.Visible
            && (BubbleBackgroundCanvas.Children.Count > 0 || BubbleTextCanvas.Children.Count > 0);
        bool hasMarks = AnnotationCanvas.Children.Count > 0 || HasInk;
        if (!hasBubbles && !hasMarks) return null;

        int fullW = Math.Max(1, _physBounds.Width);
        int fullH = Math.Max(1, _physBounds.Height);

        // Every canvas fills the window from its top-left corner, so each one renders into the same
        // bitmap at the same origin, and the calls compose in the order the layers are stacked.
        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
            fullW, fullH, 96 * _dpiX, 96 * _dpiY, System.Windows.Media.PixelFormats.Pbgra32);
        full.Render(BubbleBackgroundCanvas);

        // Deliberately omit DebugCanvas without hiding it in the live window.
        full.Render(BubbleTextCanvas);

        // Drawn here rather than left to a canvas, because the finished marks are shown by the
        // capture window and are not in this window's tree at all — see InkLayer. Clipped to the box
        // for the same reason the layer is on screen: what the box does not let through was never
        // part of the picture.
        var marks = new System.Windows.Media.DrawingVisual();
        using (var dc = marks.RenderOpen())
        {
            dc.PushClip(new System.Windows.Media.RectangleGeometry(InkClip));
            if (InkSource is { } source) dc.DrawImage(source, InkBounds);
            dc.Pop();
        }
        full.Render(marks);

        full.Render(AnnotationCanvas);

        // The overlay window spans the whole virtual screen; the selection sits at this physical
        // offset within it.
        int cropX = Math.Clamp((int)Math.Round(selPhysLeft - _physBounds.Left), 0, fullW - 1);
        int cropY = Math.Clamp((int)Math.Round(selPhysTop  - _physBounds.Top),  0, fullH - 1);
        int cropW = Math.Clamp(selPhysWidth,  1, fullW - cropX);
        int cropH = Math.Clamp(selPhysHeight, 1, fullH - cropY);

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
            full, new Int32Rect(cropX, cropY, cropW, cropH));
        cropped.Freeze();
        return cropped;
    }

    private void BuildOverlay(
        List<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight)
    {
        BubbleBackgroundCanvas.Children.Clear();
        BubbleTextCanvas.Children.Clear();
        BuildDebugBoxes(selScreenX, selScreenY);

        if (_currentVerticalText)
        {
            BuildVerticalOverlay(
                blocks,
                selScreenX,
                selScreenY,
                selScreenWidth,
                selScreenHeight);
            return;
        }

        // Window top-left in physical pixels
        double winPhysLeft = _physBounds.Left;
        double winPhysTop = _physBounds.Top;
        double canvasWidth = BubbleBackgroundCanvas.ActualWidth > 0 ? BubbleBackgroundCanvas.ActualWidth : Width;
        double canvasHeight = BubbleBackgroundCanvas.ActualHeight > 0 ? BubbleBackgroundCanvas.ActualHeight : Height;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            // Physical pixel position on screen
            double physX = selScreenX + block.Bounds.X;
            double physY = selScreenY + block.Bounds.Y;
            double physW = block.Bounds.Width;
            double physH = block.Bounds.Height;

            // Convert to WPF canvas coords (relative to overlay window)
            double canvasX = (physX - winPhysLeft) / _dpiX;
            double canvasY = (physY - winPhysTop) / _dpiY;
            double wpfW = physW / _dpiX;
            double wpfH = physH / _dpiY;
            double sourceFontReferenceHeight = GetSourceFontReferenceHeight(block, wpfH);

            // Expand coverage 2px beyond OCR bounds on every side to eliminate edge bleed
            double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
            double borderH = wpfH + BubbleExpand * 2;

            var bg = block.BackgroundColor.A == 0
                ? Colors.White
                : block.BackgroundColor;

            System.Windows.Media.Color textColor;
            if (block.TextColor.A != 0)
                textColor = block.TextColor;
            else
            {
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                textColor = lum > 0.5 ? Colors.Black : Colors.White;
            }

            bool isSingleLineSource = IsSingleLineSource(block.OriginalText, sourceFontReferenceHeight);
            bool isGroupedMultiLineSource = block.SourceLineBounds is { Count: > 1 };
            bool reflowGroup = block.LayoutIntent == OverlayLayoutIntent.GroupReflow;
            double minFontSize = SourceFontScale.MinFontSize(sourceFontReferenceHeight);
            double fontSize = SourceFontScale.Calculate(sourceFontReferenceHeight, IsLatinSourceToCjkTarget());
            var typeface = new Typeface(
                new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double availableWidth = Math.Max(BubbleMinWidth, canvasWidth - OverlayPadding * 2);
            double targetBorderW = borderW;
            bool preferRightExpansion = false;
            bool wrap = false;
            double? maxWrapBorderHeight = null;

            var measured = MeasureText(block.TranslatedText, typeface, fontSize);
            double innerW = Math.Max(1, borderW - BubbleHorizontalPadding);
            if (isGroupedMultiLineSource)
            {
                wrap = true;
                var sourceLineCount = block.SourceLineBounds!.Count;
                var hasLowerBlock = HasLowerOverlappingBlock(block, blocks);
                var maxLineCount = hasLowerBlock ? sourceLineCount : sourceLineCount + 1;
                // A balloon does not grow. What is to the right of it is the drawing, not room the
                // text may have, so the group's own box is the whole budget and anything that does
                // not fit comes off the font size below.
                if (reflowGroup)
                {
                    targetBorderW = borderW;
                }
                else
                {
                    var rightAvailableW = GetRightExpansionWidth(
                        block,
                        blocks,
                        canvasX,
                        canvasY,
                        wpfH,
                        selScreenX,
                        selScreenWidth,
                        canvasWidth);
                    targetBorderW = Math.Min(
                        availableWidth,
                        Math.Max(borderW, Math.Min(measured.Width + BubbleHorizontalPadding, rightAvailableW)));
                }

                preferRightExpansion = targetBorderW > borderW;
                var maxBorderHeight = GetBottomAvailableHeight(
                    block,
                    blocks,
                    canvasX,
                    canvasY,
                    wpfW,
                    selScreenY,
                    selScreenHeight,
                    canvasHeight);
                maxWrapBorderHeight = maxBorderHeight;
                fontSize = FindLargestGroupedFontSize(
                    block.TranslatedText,
                    typeface,
                    fontSize,
                    SingleLineAbsoluteMinFontSize,
                    Math.Max(1, targetBorderW - BubbleHorizontalPadding),
                    maxLineCount,
                    maxBorderHeight);
            }
            else if (isSingleLineSource)
            {
                double rightAvailableW = GetRightExpansionWidth(
                    block,
                    blocks,
                    canvasX,
                    canvasY,
                    wpfH,
                    selScreenX,
                    selScreenWidth,
                    canvasWidth);
                var layout = SingleLineOverlayLayout.Calculate(new(
                    fontSize,
                    borderW,
                    measured.Width,
                    BubbleHorizontalPadding,
                    rightAvailableW,
                    availableWidth,
                    SingleLineReadableMinFontSize,
                    SingleLineAbsoluteMinFontSize));

                // Kept because the wrapped fallback below starts its search from here rather than
                // from what the single-line attempt narrowed the font to. Wrapping trades width for
                // height, so the size that was too wide for one line is often perfectly fine on two.
                var sourceMatchedFontSize = fontSize;

                fontSize = layout.FontSize;
                targetBorderW = layout.BorderWidth;
                preferRightExpansion = layout.PreferRightExpansion;

                var finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                var singleLineInnerWidth = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
                var stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                // Scaling the size by the width ratio treats text width as exactly proportional to
                // font size, and it is not — hinting and rounding leave the result a fraction over
                // often enough to matter. One pass therefore came back still overflowing by a pixel
                // or two, which was enough for CharacterEllipsis to take the line's last character
                // off a line that had every appearance of fitting. Converging instead keeps it, and
                // keeps it on one line, which the wrapped fallback below would not.
                var emergencyFloor = Math.Min(fontSize, SingleLineEmergencyMinFontSize);
                for (var attempt = 0; attempt < 3 && stillOverflowsSingleLine; attempt++)
                {
                    var fitted = Math.Max(
                        emergencyFloor, fontSize * singleLineInnerWidth / finalSingleLineMeasure.Width);
                    if (fitted >= fontSize) break;

                    fontSize = fitted;
                    finalSingleLineMeasure = MeasureText(block.TranslatedText, typeface, fontSize);
                    stillOverflowsSingleLine = finalSingleLineMeasure.Width > singleLineInnerWidth;
                }

                if (stillOverflowsSingleLine)
                {
                    // Not even the emergency size fits this on one line, so it wraps. That used to
                    // be conditional — nothing overlapping below, at most two lines, and the result
                    // had to fit the gap — and every case that failed a condition fell through to
                    // CharacterEllipsis, which threw the tail of the sentence away. A paragraph read
                    // as separate lines failed the first condition on all but its last line, so the
                    // commonest shape of text on a page was also the one that lost the most.
                    //
                    // Nothing here is worth losing text over. A bubble that grows too far is visible
                    // and the user can re-select; a trimmed one reads as a finished sentence that
                    // happens to say something else, and there is no sign anything went missing.
                    wrap = true;
                    var maxBorderHeight = GetBottomAvailableHeight(
                        block,
                        blocks,
                        canvasX,
                        canvasY,
                        wpfW,
                        selScreenY,
                        selScreenHeight,
                        canvasHeight);
                    maxWrapBorderHeight = maxBorderHeight;

                    // Largest size whose wrapped form still fits the gap. The line count is left
                    // unbounded because the gap is the real constraint: this bubble replaces one
                    // source line, so every extra line is height borrowed from what sits below.
                    fontSize = FindLargestGroupedFontSize(
                        block.TranslatedText,
                        typeface,
                        sourceMatchedFontSize,
                        SingleLineAbsoluteMinFontSize,
                        singleLineInnerWidth,
                        int.MaxValue,
                        maxBorderHeight);
                }
            }
            else
            {
                if (measured.Width > innerW)
                {
                    double scaledFont = fontSize * innerW / measured.Width;
                    if (scaledFont >= minFontSize)
                    {
                        fontSize = scaledFont;

                        // The same non-proportionality the single-line path above converges around,
                        // and this branch never re-measured at all: the size it settled on was taken
                        // on trust, and whatever it was over by came off the end of the line. Wrap
                        // only if converging cannot close the gap.
                        var fitted = MeasureText(block.TranslatedText, typeface, fontSize);
                        for (var attempt = 0; attempt < 3 && fitted.Width > innerW; attempt++)
                        {
                            var next = Math.Max(minFontSize, fontSize * innerW / fitted.Width);
                            if (next >= fontSize) break;

                            fontSize = next;
                            fitted = MeasureText(block.TranslatedText, typeface, fontSize);
                        }

                        if (fitted.Width > innerW) wrap = true;
                    }
                    else
                    {
                        fontSize = Math.Max(WrappedAbsoluteMinFontSize, minFontSize);
                        wrap = true;
                    }
                }
            }

            // A translation can arrive with a line break already in it: a local model that answered
            // over two lines, an engine echoing a break out of the source. NoWrap does not ignore
            // one — the TextBlock still breaks there — and the bubble was only ever built one line
            // tall, so everything after the break was outside it. That is what CharacterEllipsis
            // showed as a "…" at the end of the first line, and it is the shape of the report that
            // opened #73: not a word missing, the rest of the sentence missing.
            //
            // Wrapping is what makes the height below count every line the text really has.
            if (!wrap && HasLineBreak(block.TranslatedText)) wrap = true;

            double actualBorderH = Math.Max(borderH, fontSize + BubbleVerticalPadding);
            double writtenH = fontSize;
            if (wrap)
            {
                innerW = Math.Max(1, targetBorderW - BubbleHorizontalPadding);
                var wrapMeasured = MeasureText(block.TranslatedText, typeface, fontSize, innerW);
                writtenH = wrapMeasured.Height;
                actualBorderH = OverlayBubbleHeight.ForWrapped(
                    borderH,
                    actualBorderH,
                    wrapMeasured.Height + BubbleVerticalPadding,
                    maxWrapBorderHeight);
            }

            double expandedOffsetX = (targetBorderW - borderW) / 2;
            double left = preferRightExpansion
                ? Math.Clamp(canvasX - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, canvasWidth - targetBorderW - OverlayPadding))
                : Math.Clamp(canvasX - BubbleExpand - expandedOffsetX, OverlayPadding, Math.Max(OverlayPadding, canvasWidth - targetBorderW - OverlayPadding));
            double top = Math.Clamp(canvasY - BubbleExpand, OverlayPadding, Math.Max(OverlayPadding, canvasHeight - actualBorderH - OverlayPadding));

            var (backgroundBorder, plateText) = BubbleBackground(
                left, top, targetBorderW, actualBorderH, bg, textColor,
                sourceFontReferenceHeight * _dpiY, writtenH * _dpiY, selScreenX, selScreenY);
            var textBrush = new SolidColorBrush(plateText);

            var textContainer = new Border
            {
                Padding = new Thickness(3, 2, 3, 2),
                Width = targetBorderW,
                Height = actualBorderH,
                ClipToBounds = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = block.TranslatedText,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textBrush,
                    TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    // Never trimmed. Every path that cannot fit the text on one line now wraps, so
                    // reaching here without wrap means it already fits; leaving CharacterEllipsis on
                    // would only mean that a measurement being a pixel out costs the user a word.
                    TextTrimming = TextTrimming.None,
                    // Centred, for a re-set group as much as for anything else. It was top-aligned
                    // while this layout belonged to a mode the user had to go and choose: a comic
                    // was the special case, and a translation shorter than the speech it replaces
                    // reads better pinned to where the eye starts. That mode is now the default —
                    // most of what anyone frames is prose of some kind — so the balance changed with
                    // it: what sits in these boxes is ordinary text on an ordinary page far more
                    // often than it is a balloon, and ordinary text centred in its own box is what
                    // every other bubble on the overlay does.
                    //
                    // Deliberately unconditional. An alignment that depended on how much emptier the
                    // box is than the text would be a second layout heuristic, and the problem it
                    // would exist to solve — a stats page's body drifting down until it looks
                    // attached to the label beneath it — has not been seen since the modes were
                    // swapped. If it comes back, it gets designed then, on a case somebody has
                    // actually looked at.
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Microsoft JhengHei, Segoe UI, Sans-Serif"),
                }
            };

            Canvas.SetLeft(textContainer, left);
            Canvas.SetTop(textContainer, top);
            BubbleBackgroundCanvas.Children.Add(backgroundBorder);
            BubbleTextCanvas.Children.Add(textContainer);
        }
    }

    private void BuildVerticalOverlay(
        IReadOnlyList<TranslatedBlock> blocks,
        double selScreenX,
        double selScreenY,
        double selScreenWidth,
        double selScreenHeight)
    {
        double winPhysLeft = _physBounds.Left;
        double winPhysTop = _physBounds.Top;
        double canvasWidth = BubbleBackgroundCanvas.ActualWidth > 0
            ? BubbleBackgroundCanvas.ActualWidth
            : Width;
        double canvasHeight = BubbleBackgroundCanvas.ActualHeight > 0
            ? BubbleBackgroundCanvas.ActualHeight
            : Height;
        double selectionLeft = (selScreenX - winPhysLeft) / _dpiX;
        double selectionTop = (selScreenY - winPhysTop) / _dpiY;
        double selectionRight = selectionLeft + selScreenWidth / _dpiX;
        double selectionBottom = selectionTop + selScreenHeight / _dpiY;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText))
                continue;

            double canvasX = (selScreenX + block.Bounds.X - winPhysLeft) / _dpiX;
            double canvasY = (selScreenY + block.Bounds.Y - winPhysTop) / _dpiY;
            double wpfW = block.Bounds.Width / _dpiX;
            double wpfH = block.Bounds.Height / _dpiY;
            double borderW = Math.Max(wpfW + BubbleExpand * 2, BubbleMinWidth);
            double borderH = wpfH + BubbleExpand * 2;
            double sourceGlyphSize = GetSourceFontReferenceHeight(block, wpfH);
            string text = new(block.TranslatedText.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var grid = FitVerticalGrid(borderW, borderH, sourceGlyphSize, text.Length);
            double cellSize = grid.CellSize;
            borderH = grid.Height;

            double maxLeft = Math.Min(
                canvasWidth - borderW - OverlayPadding,
                selectionRight - borderW);
            double maxTop = Math.Min(
                canvasHeight - borderH - OverlayPadding,
                selectionBottom - borderH);
            double left = Math.Clamp(
                canvasX - BubbleExpand,
                Math.Max(OverlayPadding, selectionLeft),
                Math.Max(Math.Max(OverlayPadding, selectionLeft), maxLeft));
            double top = Math.Clamp(
                canvasY - BubbleExpand,
                Math.Max(OverlayPadding, selectionTop),
                Math.Max(Math.Max(OverlayPadding, selectionTop), maxTop));

            var background = block.BackgroundColor.A == 0 ? Colors.White : block.BackgroundColor;
            System.Windows.Media.Color textColor;
            if (block.TextColor.A != 0)
            {
                textColor = block.TextColor;
            }
            else
            {
                double luminance =
                    (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
                textColor = luminance > 0.5 ? Colors.Black : Colors.White;
            }
            // Vertical text fills its grid, so the written part is the whole bubble.
            var (backgroundBorder, plateText) = BubbleBackground(
                left, top, borderW, borderH, background, textColor,
                sourceGlyphSize * _dpiY, borderH * _dpiY, selScreenX, selScreenY);
            BubbleBackgroundCanvas.Children.Add(backgroundBorder);
            System.Windows.Media.Brush foreground = new SolidColorBrush(plateText);

            var bubbleBounds = new Rect(left, top, borderW, borderH);
            double fontSize = cellSize * 0.92;
            foreach (var (glyph, cellBounds) in VerticalCells(text, bubbleBounds, cellSize))
            {
                var cell = new TextBlock
                {
                    Text = glyph.ToString(),
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = foreground,
                    TextAlignment = TextAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily(
                        "Microsoft JhengHei, Segoe UI, Sans-Serif"),
                };
                if (RotatesInVerticalText(glyph))
                {
                    cell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                    cell.RenderTransform = new RotateTransform(90);
                }

                PositionVerticalGlyph(cell, cellBounds);
                BubbleTextCanvas.Children.Add(cell);
            }
        }
    }

    internal static void PositionVerticalGlyph(TextBlock glyph, Rect bounds)
    {
        glyph.Width = bounds.Width;
        glyph.Height = bounds.Height;
        glyph.LineHeight = bounds.Height;
        glyph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Canvas.SetLeft(glyph, bounds.X);
        Canvas.SetTop(glyph, bounds.Y);
    }

    private static int VerticalCapacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));

    internal static (double CellSize, double Height) FitVerticalGrid(
        double width,
        double height,
        double preferredCellSize,
        int characterCount)
    {
        int needed = Math.Max(1, characterCount);
        double cellSize = Math.Max(SingleLineAbsoluteMinFontSize, preferredCellSize);
        while (cellSize > SingleLineEmergencyMinFontSize &&
               VerticalCapacity(width, height, cellSize) < needed)
        {
            cellSize = Math.Max(SingleLineEmergencyMinFontSize, cellSize - 0.5);
        }

        int columns = Math.Max(1, (int)Math.Floor(width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling((double)needed / columns));
        return (cellSize, Math.Max(height, rows * cellSize));
    }

    private static readonly SearchValues<char> RotatedVerticalGlyphs = SearchValues.Create(
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        "…⋯‥");

    internal static bool RotatesInVerticalText(char glyph) =>
        RotatedVerticalGlyphs.Contains(glyph);

    /// <summary>Returns cells in vertical reading order: downwards, then one column left.</summary>
    internal static IEnumerable<(char Glyph, Rect Cell)> VerticalCells(
        string text,
        Rect bounds,
        double cellSize)
    {
        int columns = Math.Max(1, (int)Math.Floor(bounds.Width / cellSize));
        int rows = Math.Max(1, (int)Math.Floor((bounds.Height + 0.01) / cellSize));

        for (int i = 0; i < text.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;
            if (column >= columns)
                yield break;

            yield return (text[i], new Rect(
                bounds.Left + bounds.Width - (column + 1) * cellSize,
                bounds.Top + row * cellSize,
                cellSize,
                cellSize));
        }
    }

    private void SetTranslationLayersVisible(bool visible)
    {
        _translationVisible = visible && _currentBlocks.Count > 0;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BubbleBackgroundCanvas.Visibility = visibility;
        BubbleTextCanvas.Visibility = visibility;
        UpdateDebugVisibility();
    }

    private void UpdateDebugVisibility()
    {
        DebugCanvas.Visibility = !_translationVisible || SettingsService.Instance.Current.OcrDebug.ShowOnTranslation
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOcrDebugChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnOcrDebugChanged(sender, e));
            return;
        }
        if (_isLoaded) BuildDebugBoxes(_currentSelectionScreenX, _currentSelectionScreenY);
    }

    /// <summary>Draws the current source lines and groups, with lines on top of group outlines.</summary>
    private void BuildDebugBoxes(double selScreenX, double selScreenY)
    {
        DebugCanvas.Children.Clear();
        UpdateDebugVisibility();

        var debug = SettingsService.Instance.Current.OcrDebug;
        if (_currentOcrBlocks.Count == 0) return;

        if (debug.ShowGroupBoxes)
            foreach (var box in OcrDebugBoxes.GroupBoxes(_currentOcrBlocks))
                DebugCanvas.Children.Add(
                    CreateDebugBox(box, selScreenX, selScreenY, TextGroupBoxColor, dashed: true));

        if (debug.ShowLineBoxes)
            foreach (var box in OcrDebugBoxes.LineBoxes(_currentOcrBlocks))
                DebugCanvas.Children.Add(
                    CreateDebugBox(box, selScreenX, selScreenY, OcrLineBoxColor, dashed: false));
    }

    private System.Windows.Shapes.Rectangle CreateDebugBox(
        Rect box, double selScreenX, double selScreenY, System.Windows.Media.Color color, bool dashed)
    {
        var fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(DebugFillAlpha, color.R, color.G, color.B));
        var stroke = new SolidColorBrush(color);
        fill.Freeze();
        stroke.Freeze();

        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, box.Width / _dpiX),
            Height = Math.Max(1, box.Height / _dpiY),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = dashed ? 1.5 : 1,
            StrokeDashArray = dashed ? new DoubleCollection([4, 3]) : null,
            RadiusX = 3,
            RadiusY = 3,
        };

        Canvas.SetLeft(shape, (selScreenX + box.X - _physBounds.Left) / _dpiX);
        Canvas.SetTop(shape, (selScreenY + box.Y - _physBounds.Top) / _dpiY);
        return shape;
    }

    private static bool HasLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

    private static bool IsSingleLineSource(string originalText, double sourceHeight) =>
        !originalText.Contains('\n') &&
        !originalText.Contains('\r') &&
        sourceHeight <= 28;

    /// <summary>
    /// The element painted behind one bubble, positioned on the background canvas: the repaired
    /// capture where there is a backdrop, and the flat sampled colour where there is not.
    /// </summary>
    /// <remarks>
    /// A plate is deliberately larger than the bubble it backs. Its edges fade out, and the fade
    /// has to fall outside the text rather than across it — see
    /// <see cref="CaptureBubbleBackdrop.Feather"/>. Only this element grows; the text element keeps
    /// the bubble's own size and position, so nothing about the layout moves.
    /// </remarks>
    /// <param name="glyphHeightPixels">Source glyph height in captured pixels, not WPF units: the
    /// backdrop works in the frame's own coordinates because that is where its pixels are.</param>
    /// <param name="writtenHeightPixels">How tall the translation itself is, in the same pixels.</param>
    /// <returns>
    /// The element, and the colour to draw the translation in. The second is not always the colour
    /// that went in: a plate is the surface the bubble really covers, and the sampled colour was
    /// chosen against the ring around the source line, which over a wide bubble on a busy picture
    /// is somewhere else entirely.
    /// </returns>
    private (Border Element, System.Windows.Media.Color Text) BubbleBackground(
        double left,
        double top,
        double width,
        double height,
        System.Windows.Media.Color background,
        System.Windows.Media.Color text,
        double glyphHeightPixels,
        double writtenHeightPixels,
        double selScreenX,
        double selScreenY)
    {
        if (_backdrop is { } backdrop)
        {
            // Where the bubble lands on the capture, which is not where the source line was: the
            // bubble is grown to fit a translation that is routinely longer than what it replaces.
            var bubble = new Rect(
                left * _dpiX + _physBounds.Left - selScreenX,
                top * _dpiY + _physBounds.Top - selScreenY,
                width * _dpiX,
                height * _dpiY);

            double feather = CaptureBubbleBackdrop.Feather(glyphHeightPixels);
            var area = new Rect(
                bubble.X - feather, bubble.Y - feather,
                bubble.Width + feather * 2, bubble.Height + feather * 2);

            if (backdrop.Plate(area, background, text, glyphHeightPixels, writtenHeightPixels) is { } plate)
            {
                var plated = new Border
                {
                    Background = plate.Brush,
                    Width = width + feather * 2 / _dpiX,
                    Height = height + feather * 2 / _dpiY,
                    ClipToBounds = true,
                };
                Canvas.SetLeft(plated, left - feather / _dpiX);
                Canvas.SetTop(plated, top - feather / _dpiY);
                return (plated, plate.Text);
            }

            // No plate: a flat card is right here, but not necessarily in the colour sampled when
            // the capture was read — see CaptureBubbleBackdrop.Card.
            if (backdrop.Card(bubble, text) is { } card)
            {
                background = card.Background;
                text = card.Text;
            }
        }

        var flat = new Border
        {
            Background = new SolidColorBrush(background),
            Padding = new Thickness(3, 2, 3, 2),
            Width = width,
            Height = height,
            ClipToBounds = true,
        };
        Canvas.SetLeft(flat, left);
        Canvas.SetTop(flat, top);
        return (flat, text);
    }

    private double GetSourceFontReferenceHeight(TranslatedBlock block, double fallbackHeight)
    {
        // Latin blocks carry the reduced glyph height separately so the font is not sized from
        // the (much taller) full coverage box. This takes priority over the line-bounds median.
        if (block.RenderGlyphHeight is { } glyphHeight && glyphHeight > 0)
            return glyphHeight / _dpiY;

        if (block.SourceLineBounds is not { Count: > 0 })
            return fallbackHeight;

        var lineHeights = block.SourceLineBounds
            .Select(bounds => bounds.Height / _dpiY)
            .OrderBy(height => height)
            .ToList();
        return lineHeights[lineHeights.Count / 2];
    }

    // No height test of its own — SourceFontScale fades the boost out with height, so gating it
    // here too would put back a step at whatever height the gate used.
    private bool IsLatinSourceToCjkTarget() =>
        _currentSourceLanguage.Equals("EN", StringComparison.OrdinalIgnoreCase) &&
        IsCjkLanguage(_currentTargetLanguage);

    private static bool IsCjkLanguage(string language) =>
        language.Equals("ZH", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ZH-", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("JA", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("KO", StringComparison.OrdinalIgnoreCase);

    private double FindLargestGroupedFontSize(
        string text,
        Typeface typeface,
        double preferredFontSize,
        double minimumFontSize,
        double maxTextWidth,
        int maxLineCount,
        double maxBorderHeight)
    {
        for (double size = preferredFontSize; size >= minimumFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        for (double size = minimumFontSize - 0.5; size >= GroupedEmergencyMinFontSize; size -= 0.5)
        {
            var wrapped = MeasureText(text, typeface, size, maxTextWidth);
            var borderHeight = wrapped.Height + BubbleVerticalPadding;
            if (EstimateWrappedLineCount(text, typeface, size, maxTextWidth) <= maxLineCount &&
                borderHeight <= maxBorderHeight)
                return size;
        }

        return GroupedEmergencyMinFontSize;
    }

    private int EstimateWrappedLineCount(string text, Typeface typeface, double fontSize, double maxTextWidth)
    {
        var singleLine = MeasureText(text, typeface, fontSize);
        if (singleLine.Width <= maxTextWidth)
            return 1;

        var wrapped = MeasureText(text, typeface, fontSize, maxTextWidth);
        var lineHeight = Math.Max(1, MeasureText("Ag", typeface, fontSize).Height);
        return Math.Max(1, (int)Math.Ceiling((wrapped.Height - 0.1) / lineHeight));
    }

    private double GetBottomAvailableHeight(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfW,
        double selScreenY,
        double selScreenHeight,
        double canvasHeight)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentRight = currentLeft + wpfW + BubbleExpand * 2;
        double selectionTop = (selScreenY - _physBounds.Top) / _dpiY;
        double selectionBottom = selectionTop + selScreenHeight / _dpiY;
        double bottomLimit = Math.Min(canvasHeight - OverlayPadding, selectionBottom);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.Y <= current.Bounds.Y)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / _dpiX - BubbleExpand;
            double otherRight = otherLeft + other.Bounds.Width / _dpiX + BubbleExpand * 2;
            bool overlapsHorizontally = otherLeft < currentRight && otherRight > currentLeft;
            if (!overlapsHorizontally)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / _dpiY - OverlayPadding;
            bottomLimit = Math.Min(bottomLimit, otherTop);
        }

        return Math.Max(0, bottomLimit - (canvasY - BubbleExpand));
    }

    private static bool HasLowerOverlappingBlock(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks)
    {
        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.Y <= current.Bounds.Y)
                continue;

            var overlapsHorizontally =
                other.Bounds.Left < current.Bounds.Right &&
                other.Bounds.Right > current.Bounds.Left;
            if (overlapsHorizontally)
                return true;
        }

        return false;
    }

    private double GetRightExpansionWidth(
        TranslatedBlock current,
        IReadOnlyList<TranslatedBlock> blocks,
        double canvasX,
        double canvasY,
        double wpfH,
        double selScreenX,
        double selScreenWidth,
        double canvasWidth)
    {
        double currentLeft = canvasX - BubbleExpand;
        double currentTop = canvasY - BubbleExpand;
        double currentBottom = currentTop + wpfH + BubbleExpand * 2;
        double selectionLeft = (selScreenX - _physBounds.Left) / _dpiX;
        double selectionRight = selectionLeft + selScreenWidth / _dpiX;
        double rightLimit = Math.Min(canvasWidth - OverlayPadding, selectionRight);

        foreach (var other in blocks)
        {
            if (ReferenceEquals(current, other) || other.Bounds.X <= current.Bounds.X)
                continue;

            double otherTop = canvasY + (other.Bounds.Y - current.Bounds.Y) / _dpiY - BubbleExpand;
            double otherBottom = otherTop + other.Bounds.Height / _dpiY + BubbleExpand * 2;
            bool overlapsVertically = otherTop < currentBottom && otherBottom > currentTop;
            if (!overlapsVertically)
                continue;

            double otherLeft = canvasX + (other.Bounds.X - current.Bounds.X) / _dpiX - BubbleExpand;
            rightLimit = Math.Min(rightLimit, otherLeft - OverlayPadding);
        }

        return Math.Max(0, rightLimit - currentLeft);
    }

    private FormattedText MeasureText(string text, Typeface typeface, double fontSize, double? maxTextWidth = null)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.Black,
            _dpiY);

        if (maxTextWidth.HasValue)
            formattedText.MaxTextWidth = maxTextWidth.Value;

        return formattedText;
    }

    // Esc is handled by the session-wide GlobalEscapeHook, not here — the overlay only exists
    // once a selection has been drawn, so hosting the hook would leave Esc dead until then.
    public void CloseOverlay() => Close();
}
