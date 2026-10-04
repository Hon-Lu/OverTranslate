using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using NLog;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Shape = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Edit mode: a transparent layer over one screen on which the user draws, moves and resizes the
/// areas to watch. Interactive unless framing is switched off — see
/// <see cref="SetCrosshairEnabled"/>; the click-through, drawing half of the feature is
/// <see cref="RealtimeBlockWindow"/>, and the two are never on screen together.
/// </summary>
/// <remarks>
/// The window never takes activation (WS_EX_NOACTIVATE). Dragging a block out over a running game
/// would otherwise pull the foreground away from it, which for a full-screen game means a mode
/// switch and a black screen. Not being activatable also costs it the keyboard, so Esc is handled by
/// the session's <see cref="GlobalEscapeHook"/> rather than here.
/// </remarks>
public partial class RealtimeEditWindow : Window
{
    // Base sizes, in the units of a 100%-scaled display. Everything that draws or measures uses the
    // scaled fields below instead: this window is pinned onto a screen WPF may not have laid it out
    // for, so on a mixed-DPI desktop its own render scale is not the scale the user is looking at.
    private const double BaseMinBlockWidth = 48;
    private const double BaseMinBlockHeight = 22;
    private const double BaseHandleSize = 12;
    private const double BaseRemoveSize = 22;
    private const double BaseRemoveGap = 6;

    /// <summary>
    /// Smallest width of one segment of the mode control. Both segments are the same width whichever
    /// label is longer, because a segmented control whose halves change width as the selection moves
    /// reads as two buttons rather than as one control with two states. A locale whose labels do not
    /// fit widens both segments together — see <see cref="BaseModeLabelPadding"/>.
    /// </summary>
    private const double BaseModeSegmentWidth = 82;

    /// <summary>Space kept either side of the widest mode label before the segment edge.</summary>
    private const double BaseModeLabelPadding = 14;

    /// <summary>
    /// Smallest width of one segment of the direction control — the second, narrower capsule beside
    /// the mode one.
    /// </summary>
    /// <remarks>
    /// Smaller than the mode segments because its words are shorter, not because it is a lesser
    /// control: it carries the same glyph and the same word 截圖翻譯's direction switch does, so the
    /// two features ask this question in one voice. A locale whose words are longer widens both
    /// segments together, exactly as the mode control's do — see <see cref="BaseModeLabelPadding"/>.
    ///
    /// It was built glyph-only first, on the reasoning that direction is the rarer question and the
    /// words could live on a tooltip. What that missed is who is reading it: a tooltip is found by
    /// someone already wondering what a control does, and the reader this is for has not wondered
    /// yet — they are framing a page of vertical Japanese and have no reason to hover over two small
    /// marks. A word that has to be uncovered is a word most people never see.
    /// </remarks>
    private const double BaseDirectionSegmentWidth = 68;

    /// <summary>
    /// Height of the mode control, deliberately larger than the remove button beside it.
    /// </summary>
    /// <remarks>
    /// The two are not peers. The remove button is a single glyph the user aims at and clicks; this
    /// carries two words that have to be read at a glance, off a surface floating over moving
    /// picture, before the user has decided anything. Matching the smaller of the two made it look
    /// like a second piece of window furniture rather than the question it is.
    /// </remarks>
    private const double BaseModeHeight = 30;

    /// <summary>Gap between the mode control's track and the selected pill inside it.</summary>
    private const double BaseModeInset = 2;

    private const double BaseModeFontSize = 13.5;

    /// <summary>
    /// Width of the guidance plate under the mode control, and with it how the sentence wraps.
    /// </summary>
    /// <remarks>
    /// Fixed rather than sized to whichever sentence is showing: the two modes' guidance is a
    /// different length, and a plate that resized as the selection moved would make choosing a mode
    /// look like it had rearranged the screen.
    ///
    /// The number keeps each mode's guidance on a single visual line at
    /// <see cref="BaseHintFontSize"/> — measured, not chosen. Worth re-measuring whenever either
    /// sentence is edited; the text still wraps rather than clips if it outgrows this, so the
    /// failure is a taller plate and not a lost half-sentence, and the Japanese and Korean strings
    /// are already over it and take two visual lines.
    ///
    /// ONE SENTENCE PER MODE, saying when to use it. There used to be a second, saying how to frame
    /// a block of that kind — where to leave room, when to draw two blocks instead of one — and it
    /// was dropped deliberately: it was teaching the user to operate the tool rather than telling
    /// them what the control in front of them does, and the reader is holding a block over a running
    /// game while they read it. What the framing advice was worth (issue #35 measured a subtitle
    /// framed against its own text losing 20 of 39 frames) is now the recogniser's problem to
    /// absorb, not the reader's to pre-empt.
    /// </remarks>
    private const double BaseHintWidth = 680;

    private const double BaseHintFontSize = 13;

    private static readonly SolidColorBrush FrameStroke = Freeze(Color.FromArgb(0xE6, 0x1E, 0x90, 0xD5));
    private static readonly SolidColorBrush FrameFill = Freeze(Color.FromArgb(0x1C, 0x99, 0xC8, 0xF0));
    private static readonly SolidColorBrush HandleFill = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RemoveForeground = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    // 對照顯示's copy of a block: the block's own blue, dashed and nearly empty, because it is not a
    // second area being read — it is where this one's translation will be drawn. Fainter than the
    // block's fill so the two never read as a pair of equal blocks.
    private static readonly SolidColorBrush CopyFill = Freeze(Color.FromArgb(0x10, 0x99, 0xC8, 0xF0));
    private static readonly SolidColorBrush CopyLabelFill = Freeze(Color.FromArgb(0xB8, 0x1E, 0x90, 0xD5));
    // The ✕ on the label: the label's blue a step darker (about 12% less bright) and about 85% where
    // the label is about 72%, and its glyph about 90%. The same hue, so it still reads as the label.
    private static readonly SolidColorBrush CopyCloseFill = Freeze(Color.FromArgb(0xD9, 0x1A, 0x7F, 0xBB));
    private static readonly SolidColorBrush CopyCloseForeground = Freeze(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    // 對照顯示's glyph, the same path as RealtimeControlWindow's RealtimeCompareGlyph resource: the
    // button that brings one block's copy back wears the bar button's face for that reason. Kept
    // as a copy here because this layer is built in code and has no reach into that window's
    // resources; change the two together.
    private static readonly Geometry CompareGlyph = FreezeGeometry(Geometry.Parse(
        "M1.841,7.912 L4.256,1.875 L6.671,7.912 M2.704,5.842 L5.809,5.842 M9.863,3.772 L16.762,3.772 " +
        "M9.863,6.791 L14.175,6.791 M4.256,10.069 L4.256,11.147 M1.237,11.535 L7.275,11.535 " +
        "M2.316,12.139 C2.876,13.907 4.472,15.287 7.275,16.106 M6.197,12.139 C5.636,13.907 4.041,15.287 1.237,16.106 " +
        "M9.863,11.794 L16.762,11.794 M9.863,14.812 L14.175,14.812"));

    // The mode control floats over whatever is playing underneath, so its own surface has to carry
    // the contrast: a near-opaque dark track, and a hairline along the top edge in place of the
    // light a real material would catch. Anything lighter stops being legible over a bright scene.
    private static readonly SolidColorBrush ModeTrack = Freeze(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush ModeTrackEdge = Freeze(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ModeIdleForeground = Freeze(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly System.Drawing.Rectangle _physBounds;
    private readonly IReadOnlyList<RealtimeBlockPlacement> _initialBlocks;
    private readonly int _maxBlocks;
    private readonly List<BlockVisual> _blocks = [];

    /// <summary>
    /// Whether a block drawn from here on opens with its guidance unfolded — the answer the layer
    /// started with, then whatever the user's last chevron said. Persisting it is the caller's job:
    /// see <see cref="GuidanceExpandedChanged"/>.
    /// </summary>
    private bool _guidanceExpanded;

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    // Target monitor scale relative to this window's render scale — 1.0 on a uniform desktop.
    private double _uiScale = 1.0;
    private double _minBlockWidth = BaseMinBlockWidth;
    private double _minBlockHeight = BaseMinBlockHeight;
    private double _handleSize = BaseHandleSize;
    private double _removeSize = BaseRemoveSize;
    private double _removeGap = BaseRemoveGap;
    private double _modeSegmentWidth = BaseModeSegmentWidth;
    private double _directionSegmentWidth = BaseDirectionSegmentWidth;
    private double _modeHeight = BaseModeHeight;
    private double _modeInset = BaseModeInset;
    private double _hintWidth = BaseHintWidth;

    private Point _drawOrigin;
    private Shape? _drawPreview;

    /// <summary>
    /// Whether the layer is taking the mouse. See <see cref="SetCrosshairEnabled"/>.
    /// </summary>
    private bool _crosshairEnabled = true;

    /// <summary>
    /// What the next block drawn is set to. The two trays move these as they are used — see
    /// <see cref="BlockDefaultsChanged"/>.
    /// </summary>
    private RealtimeBlockMode _blockMode;

    /// <inheritdoc cref="_blockMode"/>
    private RealtimeTextOrientation _textOrientation;

    /// <summary>
    /// Whether 對照顯示 is on, and with it whether every block shows the copy its translation will be
    /// drawn in. See <see cref="SetCompareDisplay"/>.
    /// </summary>
    private bool _compareDisplay;

    public RealtimeEditWindow(
        System.Drawing.Rectangle physBounds,
        IReadOnlyList<RealtimeBlockPlacement> initialBlocks,
        int maxBlocks,
        bool guidanceExpanded,
        RealtimeBlockMode blockMode,
        RealtimeTextOrientation textOrientation,
        bool compareDisplay = false)
    {
        InitializeComponent();

        _physBounds = physBounds;
        _initialBlocks = initialBlocks;
        _maxBlocks = maxBlocks;
        _guidanceExpanded = guidanceExpanded;
        _blockMode = blockMode;
        _textOrientation = textOrientation;
        _compareDisplay = compareDisplay;

        Loaded += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }

            ApplyScreenScale();

            // Every block opens the way the user last left a chevron, wherever they left it: the
            // guidance answers "how do I frame this?", and that answer does not differ block by block.
            foreach (var block in _initialBlocks)
                AddBlock(
                    ToCanvas(block.Bounds), block.Mode, block.Orientation, _guidanceExpanded,
                    notify: false,
                    block.CompareOffset is { } offset
                        ? new Vector(offset.X / _dpiX, offset.Y / _dpiY)
                        : null,
                    block.CompareScale,
                    block.CompareHidden);

            RaiseBlocksChanged();
        };
    }

    /// <summary>
    /// Turns framing off without leaving edit mode: no crosshair, no drawing, and every click lands
    /// on whatever is playing underneath instead of on this layer.
    /// </summary>
    /// <remarks>
    /// Done with the window style rather than by swapping the cursor, because the cursor was never
    /// the whole complaint. This layer covers the entire screen and swallows every click on it, so
    /// while it is up the user cannot touch the thing they are framing — pause the video, scrub back
    /// to the line they want, answer the game. The only way out was to start translating and come
    /// back, which throws away nothing but costs a round trip through both other modes.
    ///
    /// The blocks stay on screen while it is off: they are what the user is coming back to adjust,
    /// and a layer that emptied itself would read as having lost them. Their own handles go inert
    /// along with everything else, which is the point — nothing on this layer answers the mouse
    /// until framing is turned back on.
    ///
    /// The control bar is unaffected: it is its own window, owned by this one rather than drawn on
    /// it, so it keeps taking clicks and stays the way back.
    /// </remarks>
    public void SetCrosshairEnabled(bool enabled)
    {
        if (_crosshairEnabled == enabled) return;

        _crosshairEnabled = enabled;

        // A drag cannot be in flight — the press that got here landed on the bar — but a capture
        // left behind by a lost mouse-up owns every click on the screen, and handing the mouse to
        // the application underneath while still holding one is the one state worth not entering.
        AbandonDraw();

        BlockCanvas.Cursor = enabled ? Cursors.Cross : Cursors.Arrow;
        WindowStyles.SetClickThrough(this, !enabled);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Log.Debug("Realtime edit layer framing {State} exstyle={ExStyle:X8} visible={Visible}",
            enabled ? "on" : "off", WindowZOrderDiagnostics.ExStyle(hwnd), WindowZOrderDiagnostics.IsVisible(hwnd));
    }

    /// <summary>
    /// Shows or hides the copy beside every block that 對照顯示 draws the translation in.
    /// </summary>
    /// <remarks>
    /// <para>The copies are on screen while framing because where the translation will appear is a
    /// framing question: a copy above a subtitle can land on the HUD, a copy left of a column on the
    /// next balloon, and the user has to see that before starting to fix it. Each one can be dragged,
    /// and shrunk in proportion from its corners — it is the block's own shape by definition, see
    /// <see cref="RealtimeComparePlacement"/> — and has no remove button and no trays, because
    /// everything about it except its position and size belongs to its block.</para>
    ///
    /// <para>Hidden rather than removed when the switch goes off, and the offsets and scales kept:
    /// switching it back on should bring every copy back where, and as large as, the user had
    /// it.</para>
    /// </remarks>
    public void SetCompareDisplay(bool enabled)
    {
        if (_compareDisplay == enabled) return;

        _compareDisplay = enabled;
        ApplyAll();
    }

    /// <summary>Raised whenever a block is added, removed, moved or resized.</summary>
    public event EventHandler? BlocksChanged;

    /// <summary>Raised when a drag is refused because the block limit is already reached.</summary>
    public event EventHandler? LimitReached;

    /// <summary>
    /// Raised with the new state whenever the user folds the guidance away or brings it back, so the
    /// caller can keep it. One setting for the whole feature, written by whichever block was pressed
    /// last — the layer itself keeps nothing beyond its own lifetime.
    /// </summary>
    public event EventHandler<bool>? GuidanceExpandedChanged;

    /// <summary>
    /// Raised with what a block was just set to, which is what the next block drawn starts on.
    /// </summary>
    /// <remarks>
    /// The block itself travels with <see cref="BlocksChanged"/> like any other edit; this says the
    /// same press should outlive the block, and the session records it. Both trays raise it, because
    /// the user answering one of the two questions is the same kind of event as answering the other.
    /// </remarks>
    public event EventHandler<(RealtimeBlockMode Mode, RealtimeTextOrientation Orientation)>?
        BlockDefaultsChanged;

    public int BlockCount => _blocks.Count;

    /// <summary>The current blocks in physical screen pixels, ready to be watched.</summary>
    public IReadOnlyList<RealtimeBlockPlacement> GetPhysicalBlocks() =>
        [.. _blocks.Select(block => new RealtimeBlockPlacement(
            ToPhysical(block.Bounds), block.ModeControl.Value, block.ModeControl.TextOrientation,
            ToPhysical(block.CompareOffset), block.CompareScale, block.CompareHidden))];

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowStyles.ApplyNoActivate(this);

        // Before the DPI is read in Loaded: pinning settles which monitor the window belongs to.
        ScreenGeometry.PinPhysicalBounds(this, _physBounds);
    }

    /// <summary>
    /// Rescales the handles, the remove button and the minimum block size for the monitor this
    /// window is actually pinned to. The block rectangles themselves need no correction — they are
    /// converted through this window's own render scale, which is the one WPF lays out with.
    /// </summary>
    private void ApplyScreenScale()
    {
        double targetScale = ScreenGeometry.ScaleAt(
            _physBounds.Left + _physBounds.Width / 2,
            _physBounds.Top + _physBounds.Height / 2);

        _uiScale = targetScale / _dpiX;
        _minBlockWidth = BaseMinBlockWidth * _uiScale;
        _minBlockHeight = BaseMinBlockHeight * _uiScale;
        _handleSize = BaseHandleSize * _uiScale;
        _removeSize = BaseRemoveSize * _uiScale;
        _removeGap = BaseRemoveGap * _uiScale;
        _modeSegmentWidth = BaseModeSegmentWidth * _uiScale;
        _directionSegmentWidth = BaseDirectionSegmentWidth * _uiScale;
        _modeHeight = BaseModeHeight * _uiScale;
        _modeInset = BaseModeInset * _uiScale;
        _hintWidth = BaseHintWidth * _uiScale;
    }

    // ── Drawing a new block ──────────────────────────────────────────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_blocks.Count >= _maxBlocks)
        {
            // Refuse the drag rather than letting the user draw a block that will be thrown away on
            // release. The control bar says why.
            LimitReached?.Invoke(this, EventArgs.Empty);
            return;
        }

        _drawOrigin = e.GetPosition(BlockCanvas);
        BlockCanvas.CaptureMouse();

        // Feedback on press, not on release: the box is visibly being drawn from the first pixel.
        _drawPreview = new Shape
        {
            Stroke = FrameStroke,
            StrokeThickness = 2 * _uiScale,
            StrokeDashArray = [4, 3],
            Fill = FrameFill,
            RadiusX = 3 * _uiScale,
            RadiusY = 3 * _uiScale,
        };
        Canvas.SetLeft(_drawPreview, _drawOrigin.X);
        Canvas.SetTop(_drawPreview, _drawOrigin.Y);
        BlockCanvas.Children.Add(_drawPreview);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drawPreview is null) return;

        // A mouse-up can go missing — another window grabs the capture, the session is torn down
        // mid-drag, the button is released while the pointer is off the desktop. The cost of not
        // noticing is severe and easy to mistake for the bar being broken: a canvas that still holds
        // the capture owns the cursor and every click across the whole screen, so the crosshair
        // follows the pointer over the control bar and none of its buttons respond. The button state
        // on the next move is the one signal that is always available.
        if (e.LeftButton == MouseButtonState.Released)
        {
            AbandonDraw();
            return;
        }

        var box = NormalizeToCanvas(_drawOrigin, e.GetPosition(BlockCanvas));
        Canvas.SetLeft(_drawPreview, box.X);
        Canvas.SetTop(_drawPreview, box.Y);
        _drawPreview.Width = box.Width;
        _drawPreview.Height = box.Height;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawPreview is null) return;

        // Read the box before tearing the drag down: AbandonDraw drops the capture, which raises
        // LostMouseCapture and clears _drawPreview underneath us.
        var box = NormalizeToCanvas(_drawOrigin, e.GetPosition(BlockCanvas));
        AbandonDraw();

        // A click, or a slip of the hand, should not leave a useless sliver behind.
        if (box.Width < _minBlockWidth || box.Height < _minBlockHeight) return;

        // Whatever the user last set a block to. The two answers they arrive at are about the thing
        // they are watching, and they are watching one thing — so the second block of a sitting, and
        // the first of the next one, should not make them say it again. What these start at when
        // nobody has said anything yet is RealtimeSettings.BlockMode, which keeps the reasoning.
        AddBlock(box, _blockMode, _textOrientation, _guidanceExpanded, notify: true);
    }

    // Capture lost to something else entirely (an Alt+Tab, another window taking it). The drag is
    // over whether we like it or not, so drop the half-drawn box rather than leave it on the canvas.
    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e) => AbandonDraw();

    /// <summary>Ends the in-progress drag without creating a block, leaving no capture behind.</summary>
    private void AbandonDraw()
    {
        if (_drawPreview is not null)
        {
            BlockCanvas.Children.Remove(_drawPreview);
            _drawPreview = null;
        }

        // Re-entrant by design: this raises LostMouseCapture, which calls back in — harmless, since
        // the preview is already gone by then.
        if (BlockCanvas.IsMouseCaptured) BlockCanvas.ReleaseMouseCapture();
    }

    private Rect NormalizeToCanvas(Point a, Point b)
    {
        double x = Math.Max(0, Math.Min(a.X, b.X));
        double y = Math.Max(0, Math.Min(a.Y, b.Y));
        double right = Math.Min(BlockCanvas.ActualWidth, Math.Max(a.X, b.X));
        double bottom = Math.Min(BlockCanvas.ActualHeight, Math.Max(a.Y, b.Y));
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    // ── Blocks ───────────────────────────────────────────────────────────────────────────────────

    private void AddBlock(
        Rect bounds,
        RealtimeBlockMode mode,
        RealtimeTextOrientation orientation,
        bool guidanceExpanded,
        bool notify,
        Vector? compareOffset = null,
        double compareScale = 1.0,
        bool compareHidden = false)
    {
        var visual = new BlockVisual(
            bounds, mode, orientation, guidanceExpanded, _handleSize, _removeSize,
            _modeSegmentWidth, _directionSegmentWidth, _modeHeight, _modeInset, _hintWidth,
            _removeGap, _uiScale)
        {
            CompareOffset = compareOffset,
            CompareScale = compareScale,
            CompareHidden = compareHidden,
        };

        visual.Body.DragDelta += (_, e) => Move(visual, e.HorizontalChange, e.VerticalChange);
        visual.Copy.DragDelta += (_, e) => MoveCopy(visual, e.HorizontalChange, e.VerticalChange);
        visual.CopyGrip.DragDelta += (_, e) => MoveCopy(visual, e.HorizontalChange, e.VerticalChange);
        visual.CopyGrip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;

            // Handled, so the second press does not also start a drag of the copy it just moved.
            e.Handled = true;
            ResetCopy(visual);
        };
        visual.Remove.Click += (_, e) =>
        {
            e.Handled = true;   // must not fall through and start drawing a new block underneath
            RemoveBlock(visual);
        };
        visual.CopyClose.Click += (_, e) =>
        {
            e.Handled = true;
            SetCompareHidden(visual, true);
        };
        visual.Restore.Click += (_, e) =>
        {
            e.Handled = true;
            SetCompareHidden(visual, false);
        };
        visual.ModeControl.SelectionChanged += (_, tray) =>
        {
            // The direction decides which side an automatic copy goes, so it may have just moved.
            if (tray == ModeSegments.BlockTray.Direction) ApplyAll();
            RaiseBlocksChanged();

            // The tray last pressed is the one that counts, on whichever block it was pressed — and
            // only that tray, so a block that is 遊戲 for its own reasons does not make 遊戲 the
            // answer for the next block just because its direction was corrected. The blocks already
            // drawn are left alone either way: correcting one says nothing about the rest.
            if (tray == ModeSegments.BlockTray.Mode) _blockMode = visual.ModeControl.Value;
            else _textOrientation = visual.ModeControl.TextOrientation;
            BlockDefaultsChanged?.Invoke(this, (_blockMode, _textOrientation));
        };
        visual.ModeControl.ExpansionChanged += (_, expanded) =>
        {
            Apply(visual, animateMode: true);

            // The last chevron pressed is the one that counts, for the blocks drawn after it and for
            // the next sitting alike. The blocks already on screen are left as they are: folding one
            // away should not make the others move under the pointer.
            _guidanceExpanded = expanded;
            GuidanceExpandedChanged?.Invoke(this, expanded);
        };

        for (int corner = 0; corner < visual.Corners.Length; corner++)
        {
            int index = corner;
            visual.Corners[index].DragDelta += (_, e) => Resize(visual, index, e.HorizontalChange, e.VerticalChange);
            visual.CopyCorners[index].DragDelta += (_, e) => ScaleCopy(visual, index, e.HorizontalChange, e.VerticalChange);
        }

        _blocks.Add(visual);
        RebuildCanvas();
        Apply(visual);

        if (notify) RaiseBlocksChanged();
    }

    private void RemoveBlock(BlockVisual visual)
    {
        _blocks.Remove(visual);
        RebuildCanvas();
        RaiseBlocksChanged();
    }

    // Three layers, bottom to top, and what is drawn on top is also what a click lands on:
    //
    //   1. the block frames, which are also how a block is dragged;
    //   2. the 對照顯示 copies, with their lines, labels and corner handles;
    //   3. every block's handles, remove and restore buttons, and trays.
    //
    // A copy over its block is there because the user put it there, so a press on the overlap takes
    // the copy; the block is dragged by whatever of it is still showing. The controls that only the
    // block has — resizing, removing, its trays — stay on top of everything, so a block buried under
    // a copy can still be resized or removed directly. And the handles of all blocks go above all
    // frames, so a handle on the edge between two overlapping blocks stays grabbable whichever block
    // was drawn last. A copy's own corner handles sit under every block's for the same reason: where
    // a copy's corner lands on its block's, the press resizes the block, and the copy is still
    // reachable from its other three.
    private void RebuildCanvas()
    {
        BlockCanvas.Children.Clear();

        foreach (var block in _blocks)
            BlockCanvas.Children.Add(block.Body);

        foreach (var block in _blocks)
        {
            BlockCanvas.Children.Add(block.Link);
            BlockCanvas.Children.Add(block.Copy);
            BlockCanvas.Children.Add(block.CopyLabel);
            foreach (var corner in block.CopyCorners)
                BlockCanvas.Children.Add(corner);
        }

        foreach (var block in _blocks)
        {
            foreach (var corner in block.Corners)
                BlockCanvas.Children.Add(corner);
            BlockCanvas.Children.Add(block.Remove);
            BlockCanvas.Children.Add(block.Restore);
            BlockCanvas.Children.Add(block.ModeControl);
        }

        foreach (var block in _blocks)
            Apply(block);
    }

    private void Move(BlockVisual visual, double dx, double dy)
    {
        var bounds = visual.Bounds;
        // Clamped to the screen rather than rubber-banded: this rectangle is a capture area, and a
        // part of it hanging off the screen would be a region the loop can never read.
        double x = Math.Clamp(bounds.X + dx, 0, Math.Max(0, BlockCanvas.ActualWidth - bounds.Width));
        double y = Math.Clamp(bounds.Y + dy, 0, Math.Max(0, BlockCanvas.ActualHeight - bounds.Height));
        visual.Bounds = new Rect(x, y, bounds.Width, bounds.Height);
        ApplyChanged(visual);
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Drags a block's 對照顯示 copy, anywhere on the screen, blocks included — see
    /// <see cref="RealtimeComparePlacement.Drag"/>.
    /// </summary>
    /// <remarks>
    /// The first drag is what turns a copy from automatic into the user's: from then on moving its
    /// block carries the copy along at the same offset, resizing it keeps the copy on the same side
    /// at the same gap (see <see cref="Resize"/>), and only a double-click on its label hands it back
    /// to the automatic placement.
    /// </remarks>
    private void MoveCopy(BlockVisual visual, double dx, double dy)
    {
        if (CopyBounds(visual) is not { } current) return;

        var moved = RealtimeComparePlacement.Drag(
            current, new Vector(dx, dy), new Rect(0, 0, BlockCanvas.ActualWidth, BlockCanvas.ActualHeight));

        visual.CompareOffset = moved.TopLeft - visual.Bounds.TopLeft;
        ApplyChanged(visual);
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Shrinks or grows a block's 對照顯示 copy from one of its corners, in proportion — see
    /// <see cref="RealtimeComparePlacement.ScaleFromCorner"/>.
    /// </summary>
    /// <remarks>
    /// Counts as a drag: the copy is the user's from then on, at the offset this leaves it at, the
    /// same as if they had moved it there. An automatic copy that changed size would otherwise have
    /// the automatic placement put it somewhere new on every step of the pull.
    /// </remarks>
    private void ScaleCopy(BlockVisual visual, int corner, double dx, double dy)
    {
        if (CopyBounds(visual) is not { } current) return;

        var (box, scale) = RealtimeComparePlacement.ScaleFromCorner(
            current, visual.Bounds.Size, corner, new Vector(dx, dy),
            new Rect(0, 0, BlockCanvas.ActualWidth, BlockCanvas.ActualHeight));

        visual.CompareOffset = box.TopLeft - visual.Bounds.TopLeft;
        visual.CompareScale = scale;
        ApplyChanged(visual);
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Hands a copy back to the automatic placement at its block's full size — a double-click on its
    /// label.
    /// </summary>
    /// <remarks>
    /// The way back from a drag or a resize the user regrets, and the only one: there is no other
    /// control on a copy, and dragging it by hand to exactly where the program would have put it is
    /// not a thing anyone can do. Both go back together, because a copy is either the program's or
    /// the user's — and an automatic one is the block's size. Its block's other copies may move in
    /// answer, as they do whenever one copy moves, because the automatic ones steer clear of each
    /// other.
    /// </remarks>
    private void ResetCopy(BlockVisual visual)
    {
        if (visual.CompareOffset is null && visual.CompareScale == 1.0) return;

        visual.CompareOffset = null;
        visual.CompareScale = 1.0;
        ApplyChanged(visual);
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Turns one block's copy off — the ✕ on its label — or back on — the button under its remove
    /// button.
    /// </summary>
    /// <remarks>
    /// Turning it off lets go of it as well, the same as a double-click on its label: where it was
    /// dragged and how far it was shrunk are dropped, so bringing it back gives the automatic
    /// placement at the block's size — a fresh copy, not the one that was dismissed. Every block is
    /// laid out again rather than this one: a copy turned off frees the room it took, and an
    /// automatic copy elsewhere may now go there — or, coming back, have to make room.
    /// </remarks>
    private void SetCompareHidden(BlockVisual visual, bool hidden)
    {
        if (visual.CompareHidden == hidden) return;

        if (hidden)
        {
            visual.CompareOffset = null;
            visual.CompareScale = 1.0;
        }

        visual.CompareHidden = hidden;
        ApplyAll();
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Where a block's 對照顯示 copy sits on the canvas, or null while the switch is off or the
    /// user has turned this block's copy off.
    /// </summary>
    /// <remarks>
    /// Automatic copies are placed by the same function, on the same physical rectangles, that the
    /// running overlays will be — so what the user sees here is where the translation will be. A copy
    /// the user has dragged is placed straight from its offset in this window's own units, kept on
    /// the screen as the running side keeps it; going through whole pixels on every mouse move would
    /// make a slow drag step.
    /// </remarks>
    private Rect? CopyBounds(BlockVisual visual)
    {
        if (!_compareDisplay || visual.CompareHidden) return null;

        var screen = new Rect(0, 0, BlockCanvas.ActualWidth, BlockCanvas.ActualHeight);
        if (visual.CompareOffset is { } offset)
        {
            var box = new Rect(
                visual.Bounds.TopLeft + offset,
                new Size(visual.Bounds.Width * visual.CompareScale, visual.Bounds.Height * visual.CompareScale));
            return new Rect(
                Math.Clamp(box.X, 0, Math.Max(0, screen.Width - box.Width)),
                Math.Clamp(box.Y, 0, Math.Max(0, screen.Height - box.Height)),
                box.Width, box.Height);
        }

        var slots = _blocks
            .Select(block => new RealtimeCompareSlot(
                ToPhysical(block.Bounds), block.ModeControl.TextOrientation, ToPhysical(block.CompareOffset),
                block.CompareScale, block.CompareHidden))
            .ToList();
        int index = _blocks.IndexOf(visual);
        var placed = RealtimeComparePlacement.Displace(
            slots[index].Bounds, RealtimeComparePlacement.Place(slots, _physBounds)[index], slots[index].Scale);
        return ToCanvas(placed);
    }

    private void ApplyAll()
    {
        foreach (var block in _blocks)
            Apply(block);
    }

    /// <summary>
    /// Re-lays out the block being dragged, and any other block only if its automatic copy has
    /// actually moved because of it.
    /// </summary>
    /// <remarks>
    /// <para>Runs on every mouse move of a drag, so it lays out only what the move can have changed.
    /// That keeps the layout work small; it is not what a drag costs the GPU. Measured, dragging one
    /// block cost the same as dragging three, and laying out every block on every move against only
    /// the changed ones made under half a point of difference: the area that changes is not the
    /// cost. What is, is that this is a full-screen layered window, handed to the system whole on
    /// every frame it changes, and that the frame's shadow is blurred again on every one of those
    /// frames.</para>
    ///
    /// <para>An automatic copy can move when another block moves, because it steers clear of the
    /// blocks and of the other copies, so those are placed again and compared with where they are
    /// shown. A copy the user has dragged follows its own block alone and is never affected.</para>
    /// </remarks>
    private void ApplyChanged(BlockVisual visual)
    {
        Apply(visual);

        foreach (var block in _blocks)
        {
            if (ReferenceEquals(block, visual) || block.CompareOffset is not null) continue;
            if (CopyBounds(block) != block.ShownCopy) Apply(block);
        }
    }

    // Corner order: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.
    private void Resize(BlockVisual visual, int corner, double dx, double dy)
    {
        var bounds = visual.Bounds;
        double left = bounds.Left;
        double top = bounds.Top;
        double right = bounds.Right;
        double bottom = bounds.Bottom;

        bool movesLeft = corner is 0 or 2;
        bool movesTop = corner is 0 or 1;

        if (movesLeft) left = Math.Clamp(left + dx, 0, right - _minBlockWidth);
        else right = Math.Clamp(right + dx, left + _minBlockWidth, BlockCanvas.ActualWidth);

        if (movesTop) top = Math.Clamp(top + dy, 0, bottom - _minBlockHeight);
        else bottom = Math.Clamp(bottom + dy, top + _minBlockHeight, BlockCanvas.ActualHeight);

        var resized = new Rect(left, top, right - left, bottom - top);

        // A copy the user placed keeps its relation to the block rather than its offset from the
        // corner — see RealtimeComparePlacement.Resized. An automatic one is simply placed again.
        if (visual.CompareOffset is { } offset)
        {
            double scale = visual.CompareScale;
            var kept = RealtimeComparePlacement.Resized(
                bounds, new Rect(bounds.TopLeft + offset, new Size(bounds.Width * scale, bounds.Height * scale)),
                resized, new Rect(0, 0, BlockCanvas.ActualWidth, BlockCanvas.ActualHeight), scale);
            visual.CompareOffset = kept.TopLeft - resized.TopLeft;
        }

        visual.Bounds = resized;
        ApplyChanged(visual);
        RaiseBlocksChanged();
    }

    private void Apply(BlockVisual visual, bool animateMode = false)
    {
        var bounds = visual.Bounds;

        visual.Body.Width = bounds.Width;
        visual.Body.Height = bounds.Height;
        Canvas.SetLeft(visual.Body, bounds.X);
        Canvas.SetTop(visual.Body, bounds.Y);

        PlaceHandle(visual.Corners[0], bounds.Left, bounds.Top);
        PlaceHandle(visual.Corners[1], bounds.Right, bounds.Top);
        PlaceHandle(visual.Corners[2], bounds.Left, bounds.Bottom);
        PlaceHandle(visual.Corners[3], bounds.Right, bounds.Bottom);

        var copy = CopyBounds(visual);
        PlaceCopy(visual, copy);

        // Outside the top-right corner by preference, so it never covers the content being framed;
        // tucked inside when the block is against the screen edge and there is no room out there.
        double removeLeft = bounds.Right + _removeGap;
        if (removeLeft + _removeSize > BlockCanvas.ActualWidth)
            removeLeft = bounds.Right - _removeSize - _removeGap;
        Canvas.SetLeft(visual.Remove, removeLeft);
        Canvas.SetTop(visual.Remove, Math.Max(0, bounds.Top));

        // Straight under the remove button, inside or outside the block with it: one column of the
        // block's own buttons. Shown only while there is a copy to bring back.
        Canvas.SetLeft(visual.Restore, removeLeft);
        Canvas.SetTop(visual.Restore, Math.Max(0, bounds.Top) + _removeSize + _removeGap);
        visual.Restore.Visibility = _compareDisplay && visual.CompareHidden
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Kept whole rather than allowed to run off the side: this is the control that says what the
        // block is and how to draw it, and half of it off-screen says neither.
        var mode = visual.ModeControl;
        double modeLeft = Math.Clamp(
            bounds.Left, 0, Math.Max(0, BlockCanvas.ActualWidth - mode.TotalWidth));
        double modeTop = ModeControlTop(
            bounds, new Size(mode.TotalWidth, mode.TotalHeight), modeLeft, copy,
            BlockCanvas.ActualHeight, _removeGap);

        // Snapped to whole device pixels, because this one carries text. The block itself is placed
        // wherever the pointer left it and is right to be; a plate of 13-point CJK starting half way
        // across a pixel is soft for the whole time it is on screen, and UseLayoutRounding inside the
        // control cannot correct an origin that is already on a half pixel.
        PlaceModeControl(
            visual.ModeControl,
            SnapToPixels(modeLeft, _dpiX),
            SnapToPixels(Math.Max(0, modeTop), _dpiY),
            animateMode);
    }

    private static void PlaceModeControl(ModeSegments mode, double left, double top, bool animate)
    {
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            mode.BeginAnimation(Canvas.LeftProperty, null);
            mode.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(mode, left);
            Canvas.SetTop(mode, top);
            return;
        }

        mode.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(Canvas.GetLeft(mode), left, ModeSegments.GuidanceDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        mode.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(Canvas.GetTop(mode), top, ModeSegments.GuidanceDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // Canvas coordinates are this window's device-independent units; a device pixel is 1/dpi of one.
    private static double SnapToPixels(double position, double dpi) =>
        dpi > 0 ? Math.Round(position * dpi) / dpi : position;

    /// <summary>
    /// Where a block's trays go vertically: above the block, or tucked inside its top edge.
    /// </summary>
    /// <remarks>
    /// <para>Above the block's top-left corner by preference: it reads as a label on the block
    /// without covering the content being framed, and it is the far corner from the remove button —
    /// the two are one click apart otherwise, and one of them destroys the block.</para>
    ///
    /// <para>Inside the top edge otherwise, never below the block. Below reads as belonging to
    /// whatever is under the block, and below is also where a copy often sits. Inside is chosen when
    /// there is no room above — the block is against the top of the screen, which is where a
    /// subtitle strip often is — and when the trays up there would overlap this block's own 對照顯示
    /// copy. The copy shows where the translation will be drawn while running, so it is not moved
    /// for the trays; the trays give way instead, and stay on the block rather than stepping off to
    /// somewhere else. Only the block's own copy counts, not other blocks', so where the trays end
    /// up depends on the one block and its copy and nothing else.</para>
    ///
    /// <para>Placed by the block and its copy, never by a copy alone. They used to step round a copy
    /// sitting above or below, and that made the trays follow the copy rather than the block: drag a
    /// copy well clear and its block's trays went with it, out of reach of the block they set.
    /// Tucked inside with the guidance open, they cover part of what the user is framing; that is
    /// the accepted cost, and the guidance folds away.</para>
    /// </remarks>
    internal static double ModeControlTop(
        Rect block, Size control, double controlLeft, Rect? copy, double screenHeight, double gap)
    {
        double above = block.Top - control.Height - gap;
        if (above >= 0)
        {
            var tray = new Rect(controlLeft, above, control.Width, control.Height);
            if (copy is not { } box || !Overlaps(tray, box)) return above;
        }

        return Math.Max(0, Math.Min(block.Top + gap, screenHeight - control.Height));

        // Strictly: Rect.IntersectsWith counts two rectangles that only touch.
        static bool Overlaps(Rect a, Rect b) =>
            a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    }

    /// <summary>
    /// Puts a block's 對照顯示 copy where <see cref="CopyBounds"/> says, with a line back to the block
    /// — or takes both off the canvas while the switch is off.
    /// </summary>
    /// <remarks>
    /// The line is what says which block a copy belongs to once there are three of each on screen.
    /// It runs between the facing edges rather than the centres, so it never crosses the content the
    /// block is framing, and it goes away when the two touch or overlap: there is then nothing to
    /// connect, and a line drawn inside them would be one more stroke over the picture.
    /// </remarks>
    private static void PlaceCopy(BlockVisual visual, Rect? copy)
    {
        visual.ShownCopy = copy;
        if (copy is not { } box)
        {
            visual.Copy.Visibility = Visibility.Collapsed;
            visual.CopyLabel.Visibility = Visibility.Collapsed;
            visual.Link.Visibility = Visibility.Collapsed;
            foreach (var corner in visual.CopyCorners)
                corner.Visibility = Visibility.Collapsed;
            return;
        }

        visual.Copy.Visibility = Visibility.Visible;
        visual.Copy.Width = box.Width;
        visual.Copy.Height = box.Height;
        Canvas.SetLeft(visual.Copy, box.X);
        Canvas.SetTop(visual.Copy, box.Y);

        visual.CopyLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(visual.CopyLabel, box.X + visual.GripInset);
        Canvas.SetTop(visual.CopyLabel, box.Y + visual.GripInset);

        for (int i = 0; i < visual.CopyCorners.Length; i++)
        {
            var corner = visual.CopyCorners[i];
            double half = corner.Width / 2;
            corner.Visibility = Visibility.Visible;
            Canvas.SetLeft(corner, (i is 0 or 2 ? box.Left : box.Right) - half);
            Canvas.SetTop(corner, (i is 0 or 1 ? box.Top : box.Bottom) - half);
        }

        var bounds = visual.Bounds;
        var from = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var to = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var start = EdgeToward(bounds, from, to);
        var end = EdgeToward(box, to, from);

        // Pointing the same way as centre to centre means the two edge points are in order, i.e. the
        // rectangles are apart; reversed or coincident means they touch or overlap.
        bool apart = (end - start) * (to - from) > 0.5;
        visual.Link.Visibility = apart ? Visibility.Visible : Visibility.Collapsed;
        visual.Link.X1 = start.X;
        visual.Link.Y1 = start.Y;
        visual.Link.X2 = end.X;
        visual.Link.Y2 = end.Y;
    }

    /// <summary>Where a line from a rectangle's centre toward a point leaves the rectangle.</summary>
    private static Point EdgeToward(Rect rect, Point centre, Point target)
    {
        var direction = target - centre;
        if (direction.Length < 0.001) return centre;

        double scaleX = Math.Abs(direction.X) > 0.001 ? rect.Width / 2 / Math.Abs(direction.X) : double.PositiveInfinity;
        double scaleY = Math.Abs(direction.Y) > 0.001 ? rect.Height / 2 / Math.Abs(direction.Y) : double.PositiveInfinity;
        return centre + direction * Math.Min(scaleX, scaleY);
    }

    private void PlaceHandle(Thumb handle, double centreX, double centreY)
    {
        Canvas.SetLeft(handle, centreX - _handleSize / 2);
        Canvas.SetTop(handle, centreY - _handleSize / 2);
    }

    private void RaiseBlocksChanged() => BlocksChanged?.Invoke(this, EventArgs.Empty);

    // ── Coordinates ──────────────────────────────────────────────────────────────────────────────

    private Rect ToCanvas(System.Drawing.Rectangle physical) => new(
        (physical.Left - _physBounds.Left) / _dpiX,
        (physical.Top - _physBounds.Top) / _dpiY,
        physical.Width / _dpiX,
        physical.Height / _dpiY);

    private System.Drawing.Rectangle ToPhysical(Rect canvas) => new(
        (int)Math.Round(_physBounds.Left + canvas.X * _dpiX),
        (int)Math.Round(_physBounds.Top + canvas.Y * _dpiY),
        Math.Max(1, (int)Math.Round(canvas.Width * _dpiX)),
        Math.Max(1, (int)Math.Round(canvas.Height * _dpiY)));

    // An offset, not a position: no screen origin to add.
    private System.Drawing.Point? ToPhysical(Vector? offset) => offset is { } value
        ? new System.Drawing.Point((int)Math.Round(value.X * _dpiX), (int)Math.Round(value.Y * _dpiY))
        : null;

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry FreezeGeometry(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The elements that make up one block on the canvas. They are separate children rather than a
    /// single composed control so the corner handles and the remove button are not clipped by — and
    /// do not have to be hit-tested through — the frame itself.
    /// </summary>
    private sealed class BlockVisual
    {
        private static readonly Cursor[] CornerCursors =
            [Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNESW, Cursors.SizeNWSE];

        public BlockVisual(
            Rect bounds,
            RealtimeBlockMode mode,
            RealtimeTextOrientation orientation,
            bool guidanceExpanded,
            double handleSize,
            double removeSize,
            double modeSegmentWidth,
            double directionSegmentWidth,
            double modeHeight,
            double modeInset,
            double hintWidth,
            double gap,
            double uiScale)
        {
            Bounds = bounds;

            Body = new Thumb
            {
                Cursor = Cursors.SizeAll,
                Template = BuildFrameTemplate(uiScale),
            };

            Corners = [.. CornerCursors.Select(cursor => new Thumb
            {
                Width = handleSize,
                Height = handleSize,
                Cursor = cursor,
                Template = BuildHandleTemplate(handleSize, uiScale),
            })];

            Remove = new Button
            {
                Width = removeSize,
                Height = removeSize,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("S.Realtime.RemoveBlock"),
                Template = BuildRemoveTemplate(removeSize, uiScale),
            };

            ModeControl = new ModeSegments(
                mode, orientation, guidanceExpanded, modeHeight, modeSegmentWidth,
                directionSegmentWidth, modeInset, hintWidth, gap, uiScale);

            Copy = new Thumb
            {
                Cursor = Cursors.SizeAll,
                Template = BuildCopyTemplate(uiScale),
                ToolTip = LocalizationService.Get("S.Realtime.CompareCopyHint"),
                Visibility = Visibility.Collapsed,
            };

            GripInset = 4 * uiScale;
            CopyGrip = new Thumb
            {
                Cursor = Cursors.SizeAll,
                Template = BuildGripTemplate(uiScale),
                ToolTip = LocalizationService.Get("S.Realtime.CompareCopyHint"),
            };

            CopyClose = new Button
            {
                Cursor = Cursors.Hand,
                Margin = new Thickness(1 * uiScale, 0, 0, 0),
                ToolTip = LocalizationService.Get("S.Realtime.CompareHideBlock"),
                Template = BuildCopyCloseTemplate(uiScale),
            };

            // Side by side and touching, so the two read as one label with an end that can be
            // pressed. Siblings rather than the button inside the label's template, so a press on ✕
            // never reaches the label: no drag of the copy, and no double-click putting it back.
            CopyLabel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Visibility = Visibility.Collapsed,
            };
            CopyLabel.Children.Add(CopyGrip);
            CopyLabel.Children.Add(CopyClose);

            Restore = new Button
            {
                Width = removeSize,
                Height = removeSize,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("S.Realtime.CompareShowBlock"),
                Template = BuildRestoreTemplate(removeSize, uiScale),
                Visibility = Visibility.Collapsed,
            };

            // A little smaller than the block's, and filled in the label's colour rather than white:
            // the block's handles are the primary ones, and these have to read as the copy's at a
            // glance where the two sit close together.
            double copyHandleSize = handleSize * 10 / 12;
            CopyCorners = [.. CornerCursors.Select(cursor => new Thumb
            {
                Width = copyHandleSize,
                Height = copyHandleSize,
                Cursor = cursor,
                Template = BuildCopyHandleTemplate(copyHandleSize, uiScale),
                Visibility = Visibility.Collapsed,
            })];

            Link = new System.Windows.Shapes.Line
            {
                Stroke = FrameStroke,
                StrokeThickness = 1.5 * uiScale,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
        }

        public Rect Bounds { get; set; }
        public Thumb Body { get; }
        public Thumb[] Corners { get; }
        public Button Remove { get; }

        /// <summary>
        /// Where 對照顯示 draws this block's translation, as an offset from <see cref="Bounds"/> in
        /// canvas units — null until the user drags <see cref="Copy"/>, which means "placed
        /// automatically". See <see cref="RealtimeBlockPlacement.CompareOffset"/>.
        /// </summary>
        public Vector? CompareOffset { get; set; }

        /// <summary>
        /// How large the copy is, as a fraction of <see cref="Bounds"/> — 1.0 until the user pulls
        /// one of <see cref="CopyCorners"/>. See <see cref="RealtimeBlockPlacement.CompareScale"/>.
        /// </summary>
        public double CompareScale { get; set; } = 1.0;

        /// <summary>
        /// This block's 對照顯示 copy: the block's shape, at <see cref="CompareScale"/> of its size,
        /// collapsed while the switch is off.
        /// </summary>
        public Thumb Copy { get; }

        /// <summary>The copy's corner handles, in the same order as <see cref="Corners"/>.</summary>
        public Thumb[] CopyCorners { get; }

        /// <summary>
        /// The copy's label, which is also its handle: it drags the copy, and a double-click on it
        /// puts the copy back where the automatic placement would. A separate element from the
        /// outline only so the double-click has something of its own to land on.
        /// </summary>
        public Thumb CopyGrip { get; }

        /// <summary>
        /// The ✕ at the end of the label: turns this block's copy off and leaves the others alone.
        /// See <see cref="CompareHidden"/>.
        /// </summary>
        public Button CopyClose { get; }

        /// <summary><see cref="CopyGrip"/> and <see cref="CopyClose"/>, placed on the canvas as one.</summary>
        public StackPanel CopyLabel { get; }

        /// <summary>
        /// Whether the user has turned this block's copy off — see
        /// <see cref="RealtimeBlockPlacement.CompareHidden"/>.
        /// </summary>
        public bool CompareHidden { get; set; }

        /// <summary>
        /// Brings a turned-off copy back. Under <see cref="Remove"/>, and only there while the copy is
        /// off and 對照顯示 is on — the one moment it has anything to do.
        /// </summary>
        public Button Restore { get; }

        /// <summary>How far the label sits in from the copy's top-left corner.</summary>
        public double GripInset { get; }

        /// <summary>
        /// Where <see cref="Copy"/> was last put, or null while it is hidden — what a drag elsewhere
        /// compares against to tell whether this block needs laying out again.
        /// </summary>
        public Rect? ShownCopy { get; set; }

        /// <summary>The line from the block to its copy.</summary>
        public System.Windows.Shapes.Line Link { get; }

        /// <summary>What the user says this block holds — see <see cref="RealtimeBlockMode"/>.</summary>
        public ModeSegments ModeControl { get; }

        private static ControlTemplate BuildFrameTemplate(double uiScale)
        {
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BorderBrushProperty, FrameStroke);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(2 * uiScale));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(3 * uiScale));
            frame.SetValue(Border.BackgroundProperty, FrameFill);
            // The frame floats over unpredictable content — a shadow is what keeps the edge legible
            // over a bright scene as well as a dark one.
            frame.SetValue(UIElement.EffectProperty, new DropShadowEffect
            {
                BlurRadius = 10 * uiScale, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black
            });
            return new ControlTemplate(typeof(Thumb)) { VisualTree = frame };
        }

        /// <summary>
        /// A dashed outline. The label in its corner is a separate element — see
        /// <see cref="BuildGripTemplate"/> — because it has to be drawn above the blocks while the
        /// outline is drawn below them.
        /// </summary>
        private static ControlTemplate BuildCopyTemplate(double uiScale)
        {
            var outline = new FrameworkElementFactory(typeof(Shape));
            outline.SetValue(System.Windows.Shapes.Shape.StrokeProperty, FrameStroke);
            outline.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5 * uiScale);
            outline.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection([4, 3]));
            outline.SetValue(System.Windows.Shapes.Shape.FillProperty, CopyFill);
            outline.SetValue(Shape.RadiusXProperty, 3 * uiScale);
            outline.SetValue(Shape.RadiusYProperty, 3 * uiScale);
            // No drop shadow, unlike the block's frame. A blur over a box the size of the block is
            // re-run on every frame the copy moves, and the copy is the thing being dragged; the
            // dashes and the label keep it legible without one.
            return new ControlTemplate(typeof(Thumb)) { VisualTree = outline };
        }

        /// <summary>
        /// The small label in a copy's corner. It is what keeps the copy from reading as a block still
        /// being drawn — the drag preview is dashed too — and it names the switch the user would turn
        /// off to make it go away.
        /// </summary>
        private static ControlTemplate BuildGripTemplate(double uiScale)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, LocalizationService.Get("S.Realtime.CompareDisplay"));
            // 13 rather than the 11 of the block's other small glyphs: at 11 it was the one piece of
            // text over a busy scene that had to be squinted at. The padding grows with it.
            text.SetValue(TextBlock.FontSizeProperty, 13.0 * uiScale);
            text.SetValue(TextBlock.ForegroundProperty, RemoveForeground);
            text.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);

            var label = new FrameworkElementFactory(typeof(Border));
            label.SetValue(Border.BackgroundProperty, CopyLabelFill);
            // Square on the right, where the ✕ continues it.
            label.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.5 * uiScale, 0, 0, 3.5 * uiScale));
            label.SetValue(Border.PaddingProperty, new Thickness(6 * uiScale, 1.2 * uiScale, 6 * uiScale, 2.4 * uiScale));
            label.AppendChild(text);

            return new ControlTemplate(typeof(Thumb)) { VisualTree = label };
        }

        /// <summary>
        /// The ✕ that ends the label. Stronger than the label it ends, so it reads as the one part of
        /// it that can be pressed, but still quieter than the block's remove button — translucent
        /// rather than solid, a smaller glyph, no round chip — because this one only lets go of a
        /// copy and the other destroys a block, and the two must not be mistaken for each other.
        /// </summary>
        private static ControlTemplate BuildCopyCloseTemplate(double uiScale)
        {
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "✕");
            glyph.SetValue(TextBlock.FontSizeProperty, 10.0 * uiScale);
            glyph.SetValue(TextBlock.ForegroundProperty, CopyCloseForeground);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, CopyCloseFill);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(0, 3.5 * uiScale, 3.5 * uiScale, 0));
            chip.SetValue(Border.PaddingProperty, new Thickness(5 * uiScale, 0, 5 * uiScale, 0));
            chip.AppendChild(glyph);

            return new ControlTemplate(typeof(Button)) { VisualTree = chip };
        }

        // The remove button's chip with 對照顯示's own glyph in it: it belongs to the block like the
        // remove button does, and it brings back what the bar's 原文對照 button stands for.
        private static ControlTemplate BuildRestoreTemplate(double removeSize, double uiScale)
        {
            var glyph = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            glyph.SetValue(System.Windows.Shapes.Path.DataProperty, CompareGlyph);
            glyph.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
            glyph.SetValue(FrameworkElement.WidthProperty, removeSize * 0.6);
            glyph.SetValue(FrameworkElement.HeightProperty, removeSize * 0.6);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeProperty, RemoveForeground);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.1 * uiScale);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeLineJoinProperty, PenLineJoin.Round);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, FrameStroke);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(removeSize / 2));
            chip.AppendChild(glyph);

            return new ControlTemplate(typeof(Button)) { VisualTree = chip };
        }

        private static ControlTemplate BuildHandleTemplate(double handleSize, double uiScale)
        {
            var handle = new FrameworkElementFactory(typeof(Border));
            handle.SetValue(Border.BackgroundProperty, HandleFill);
            handle.SetValue(Border.BorderBrushProperty, FrameStroke);
            handle.SetValue(Border.BorderThicknessProperty, new Thickness(2 * uiScale));
            handle.SetValue(Border.CornerRadiusProperty, new CornerRadius(handleSize / 2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = handle };
        }

        // The block's handle inverted: the label's fill inside, a white ring round it.
        private static ControlTemplate BuildCopyHandleTemplate(double handleSize, double uiScale)
        {
            var handle = new FrameworkElementFactory(typeof(Border));
            handle.SetValue(Border.BackgroundProperty, CopyLabelFill);
            handle.SetValue(Border.BorderBrushProperty, HandleFill);
            handle.SetValue(Border.BorderThicknessProperty, new Thickness(1.5 * uiScale));
            handle.SetValue(Border.CornerRadiusProperty, new CornerRadius(handleSize / 2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = handle };
        }

        private static ControlTemplate BuildRemoveTemplate(double removeSize, double uiScale)
        {
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "✕");
            glyph.SetValue(TextBlock.FontSizeProperty, 11.0 * uiScale);
            glyph.SetValue(TextBlock.ForegroundProperty, RemoveForeground);
            glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, FrameStroke);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(removeSize / 2));
            // No drop shadow, like the trays — see Plate in ModeSegments for why they went.
            chip.AppendChild(glyph);

            return new ControlTemplate(typeof(Button)) { VisualTree = chip };
        }
    }


    /// <summary>
    /// The per-block control: both choices of each question always on screen, the selected one
    /// filled in, and under them a line saying what that kind of block is.
    /// </summary>
    /// <remarks>
    /// A two-state chip that flips when clicked would be smaller, and it was tried first. It reads
    /// badly for this job in two ways: a chip labelled only with its state reads equally well as a
    /// state and as an action, and the two readings are opposites; and with several blocks on screen
    /// there is no way to see that a choice exists at all, let alone what the other option is. Both
    /// segments being visible answers "what is this block?" and "what else could it be?" at a glance,
    /// and switching is one click rather than read-then-flip.
    ///
    /// THE SECOND CAPSULE. Direction is a separate question from what the block holds — either kind
    /// of block can hold either kind of writing — so it is a separate control rather than four
    /// segments on one track, which would read as one four-way choice until the reader noticed two
    /// pills on it. It is built out of the same parts as the first (same plate, same height, same
    /// pill, same press) so the row reads as one piece of furniture, and its segments carry the same
    /// glyph and word pairing 截圖翻譯's direction switch does. It sits after the mode control
    /// because the mode is the question every block has to answer and this one is the question few
    /// do.
    ///
    /// The guidance sits under the control rather than beside it, both hard against the same left
    /// edge, so the pair reads as one column starting at the block's corner. It answers "which of
    /// these two is the thing in front of me?" and nothing else — see <see cref="BaseHintWidth"/>
    /// for the second sentence that used to be there and why it went.
    ///
    /// Everything here is built in code rather than as a template because the whole edit layer is —
    /// see <see cref="BlockVisual"/> — and because the pill has to be animated by hand: the control
    /// floats over content the user is still watching, so it has to settle rather than jump.
    ///
    /// ON THE TEXT LOOKING SOFT. This window is layered (<c>AllowsTransparency</c>), and WPF turns
    /// ClearType off for the whole of a layered window — every glyph here is greyscale antialiased
    /// and no setting changes that. What is left is worth doing and is done below: no ancestor of the
    /// text carries an Effect, because an Effect pushes everything beneath it through an intermediate
    /// surface and the text comes back softer; the text is formatted in Display mode,
    /// which snaps stems to whole pixels at the small sizes used here; and the control rounds its own
    /// layout, while the canvas rounds the position it is placed at, so the first glyph starts on the
    /// pixel grid instead of half way across one — the same fix AboutOverlay's card carries, for the
    /// same reason.
    /// </remarks>
    private sealed class ModeSegments : StackPanel
    {
        // Response, not duration, in the sense the motion is designed for: long enough to be seen
        // as one thing moving rather than two things swapping, short enough that a second click
        // never queues up behind it. Eased out with no overshoot — nothing was thrown here, a
        // button was pressed, and a bounce would be motion the gesture did not pay for.
        private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(260));

        // The guidance does not move when the mode changes, it is replaced — so it crosses over
        // rather than sliding, and faster than the pill, because a sentence that is still fading
        // while the user starts reading it is worse than one that was simply there.
        private static readonly Duration HintFadeDuration = new(TimeSpan.FromMilliseconds(140));

        // A small disclosure should feel attached to the control, not staged like a panel reveal.
        // Critically damped in character: one short ease-out, no overshoot, and reversible from the
        // value currently on screen when the user changes their mind mid-transition.
        internal static readonly Duration GuidanceDuration = new(TimeSpan.FromMilliseconds(180));

        // Feedback on press has to be immediate or the control feels dead, so this is short enough
        // to read as instant while still being a movement rather than a jump.
        private static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(100));

        // Pressing lights the segment rather than shrinking the control, and that is a rendering
        // decision as much as a design one. A scale on the track is a transform over text, and WPF
        // drops pixel snapping the moment it believes text is animating, then ramps it back over
        // about a second — so every press would leave both labels soft for a beat afterwards (see
        // ShellWindow.AnimateContentIn, which pays a bitmap cache to avoid exactly that). Lighting
        // the segment also says which of the two is being pressed, which a scale of the whole
        // control cannot.
        private static readonly SolidColorBrush PressHighlight =
            Freeze(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

        /// <summary>
        /// One step up from regular, for the guidance — a paragraph of small text on a translucent
        /// surface over moving picture, which needs the weight to stay readable.
        /// </summary>
        /// <remarks>
        /// Deliberately not Medium, which is what "a little heavier" would normally mean. This text
        /// is Chinese and falls back to Microsoft JhengHei UI, which ships Regular and Bold and
        /// nothing between: measured on this sentence at 13pt, Medium renders identically to Regular
        /// (17.47% ink) because 500 matches back to the 400 face, while SemiBold renders identically
        /// to Bold (21.97%) because 600 matches forward to the 700 one. There is no half step to
        /// pick, and this is the same one the rest of the application uses to emphasise (see
        /// SectionHeader in SharedStyles).
        ///
        /// The two mode labels do NOT take it, and the same measurement is why. Weight buys
        /// readability by thickening strokes, and it costs the gaps between them — which is a good
        /// trade over a sentence of ordinary characters and a bad one over 字幕 / 對話 and 遊戲 / UI,
        /// where 遊, 戲 and 幕 carry twelve to seventeen strokes each and close up into a blot at this
        /// size. Those two words are also not the ones needing help to be found: one sits in white
        /// on a saturated pill and the other is the only other thing on the track.
        /// </remarks>
        private static readonly FontWeight HeavierOverPicture = FontWeights.SemiBold;

        /// <summary>
        /// How to draw a block of each kind. Named for what the user does, not for what the
        /// recogniser then does with it: the reasons live in <see cref="RealtimeDetectorSize"/> and
        /// <see cref="CollapsedDetection"/>, and neither is something to explain over a paused game.
        /// </summary>
        private static string SubtitleHint =>
            LocalizationService.Get("S.Realtime.ModeSubtitleGuidance");

        private static string PanelHint =>
            LocalizationService.Get("S.Realtime.ModeGameUiGuidance");

        private readonly TranslateTransform _pillOffset = new();
        private readonly TranslateTransform _directionPillOffset = new();
        private readonly Border[] _segments;
        private readonly Border[] _directionSegments;
        private readonly Border[] _highlights;
        private readonly Border[] _directionHighlights;
        private readonly System.Windows.Shapes.Path[] _directionGlyphs;
        private readonly TextBlock[] _directionLabels;
        private readonly TextBlock[] _labels;
        private readonly TextBlock[] _hints;
        private readonly Grid _hintHost;
        private readonly Border _hintPlate;
        private readonly Border _guidanceToggle;
        private readonly Border _guidanceToggleHighlight;
        private readonly RotateTransform _guidanceChevronRotation = new();
        private readonly double _segmentWidth;
        private readonly double _directionSegmentWidth;
        private readonly double _expandedWidth;
        private readonly double _expandedHeight;
        private readonly double _collapsedWidth;
        private readonly double _collapsedHeight;
        private readonly double _expandedHintHeight;

        // Which segment the pointer went down on, or -1. The click is committed on release and only
        // if the pointer is still over that segment, so a press the user thought better of can be
        // taken back by sliding off it — the same forgiveness every other button on the desktop has.
        private int _pressedSegment = -1;
        private int _pressedDirection = -1;
        private bool _guidanceTogglePressed;
        private bool _guidanceExpanded;
        private int _guidanceTransition;

        public ModeSegments(
            RealtimeBlockMode mode,
            RealtimeTextOrientation orientation,
            bool guidanceExpanded,
            double height,
            double segmentWidth,
            double directionSegmentWidth,
            double inset,
            double hintWidth,
            double gap,
            double uiScale)
        {
            Value = mode;
            TextOrientation = orientation;
            _guidanceExpanded = guidanceExpanded;

            Orientation = System.Windows.Controls.Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Left;

            // Sizes here are base units times a monitor scale, so most of them land on fractions of
            // a pixel. Rounded, the plate edges and the text inside them start on whole pixels.
            UseLayoutRounding = true;

            _labels =
            [
                BuildLabel(LocalizationService.Get("S.Realtime.ModeSubtitle"), uiScale),
                BuildLabel(LocalizationService.Get("S.Realtime.ModeGameUi"), uiScale),
            ];

            // The base width suits labels of a word or two; a locale that needs more room gets it,
            // and both segments take the wider figure so the halves stay equal.
            foreach (var label in _labels)
                label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            segmentWidth = Math.Max(
                segmentWidth,
                _labels.Max(label => label.DesiredSize.Width) + BaseModeLabelPadding * 2 * uiScale);
            _segmentWidth = segmentWidth;

            var trackWidth = segmentWidth * 2 + inset * 2;
            var radius = height / 2;

            var pill = new Border
            {
                Width = segmentWidth,
                Height = height - inset * 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(inset, 0, 0, 0),
                CornerRadius = new CornerRadius((height - inset * 2) / 2),
                Background = FrameStroke,
                RenderTransform = _pillOffset,
            };

            // Rounded on the outer end only, so a press on either half stays inside the capsule.
            _highlights =
            [
                BuildHighlight(new CornerRadius(radius, 0, 0, radius), inset),
                BuildHighlight(new CornerRadius(0, radius, radius, 0), inset),
            ];

            _segments =
            [
                BuildSegment(_labels[0], _highlights[0], segmentWidth,
                    LocalizationService.Get("S.Realtime.ModeSubtitleSummary")),
                BuildSegment(_labels[1], _highlights[1], segmentWidth,
                    LocalizationService.Get("S.Realtime.ModeGameUiSummary")),
            ];

            var row = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(inset, 0, inset, 0),
            };
            foreach (var segment in _segments) row.Children.Add(segment);

            // The pill goes in first so the labels sit on top of it; a label the pill slid over
            // would otherwise disappear underneath it halfway through the move.
            var trackContent = new Grid();
            trackContent.Children.Add(pill);
            trackContent.Children.Add(row);

            var track = Plate(trackWidth, new CornerRadius(radius), trackContent, uiScale);
            track.HorizontalAlignment = HorizontalAlignment.Left;
            track.Height = height;

            // Same parts, narrower: one capsule, two segments, one pill that slides between them.
            _directionGlyphs =
            [
                BuildDirectionGlyph("M2,3.5 H12 M2,7 H12 M2,10.5 H8", uiScale),
                BuildDirectionGlyph("M10.5,2 V12 M7,2 V12 M3.5,2 V8", uiScale),
            ];

            _directionLabels =
            [
                BuildLabel(LocalizationService.Get("S.Toolbar.DirectionHorizontal"), uiScale),
                BuildLabel(LocalizationService.Get("S.Toolbar.DirectionVertical"), uiScale),
            ];

            var directionContents = new[]
            {
                BuildDirectionContent(_directionGlyphs[0], _directionLabels[0], uiScale),
                BuildDirectionContent(_directionGlyphs[1], _directionLabels[1], uiScale),
            };

            // Measured and widened the way the mode segments are: the pair has to stay equal halves
            // for the pill to travel one fixed distance, and "橫排" and "Horizontal" are nothing like
            // each other in width.
            foreach (var content in directionContents)
                content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            directionSegmentWidth = Math.Max(
                directionSegmentWidth,
                directionContents.Max(content => content.DesiredSize.Width) +
                    BaseModeLabelPadding * 2 * uiScale);
            _directionSegmentWidth = directionSegmentWidth;

            var directionPill = new Border
            {
                Width = directionSegmentWidth,
                Height = height - inset * 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(inset, 0, 0, 0),
                CornerRadius = new CornerRadius((height - inset * 2) / 2),
                Background = FrameStroke,
                RenderTransform = _directionPillOffset,
            };

            _directionHighlights =
            [
                BuildHighlight(new CornerRadius(radius, 0, 0, radius), inset),
                BuildHighlight(new CornerRadius(0, radius, radius, 0), inset),
            ];

            // The tooltip says what each answer MEANS, which is the half the label cannot carry —
            // the same division, and the same sentences, the screenshot side's switch uses.
            _directionSegments =
            [
                BuildSegment(directionContents[0], _directionHighlights[0], directionSegmentWidth,
                    LocalizationService.Get("S.Toolbar.DirectionHorizontalHint")),
                BuildSegment(directionContents[1], _directionHighlights[1], directionSegmentWidth,
                    LocalizationService.Get("S.Toolbar.DirectionVerticalHint")),
            ];

            var directionRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(inset, 0, inset, 0),
            };
            foreach (var segment in _directionSegments) directionRow.Children.Add(segment);

            var directionContent = new Grid();
            directionContent.Children.Add(directionPill);
            directionContent.Children.Add(directionRow);

            var directionTrack = Plate(
                directionSegmentWidth * 2 + inset * 2,
                new CornerRadius(radius),
                directionContent,
                uiScale);
            directionTrack.HorizontalAlignment = HorizontalAlignment.Left;
            directionTrack.Height = height;
            directionTrack.Margin = new Thickness(gap, 0, 0, 0);

            // Named for the question, not for either answer: with no words on it this is all a
            // screen reader has to go on.
            System.Windows.Automation.AutomationProperties.SetName(
                directionTrack, LocalizationService.Get("S.Toolbar.Direction"));

            var chevron = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 1,7 L 6,2 L 11,7"),
                Width = 12 * uiScale,
                Height = 9 * uiScale,
                Stretch = Stretch.Fill,
                Stroke = RemoveForeground,
                StrokeThickness = 1.5 * uiScale,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _guidanceChevronRotation,
                IsHitTestVisible = false,
            };

            _guidanceToggleHighlight = new Border
            {
                Background = PressHighlight,
                CornerRadius = new CornerRadius(radius),
                Margin = new Thickness(inset),
                Opacity = 0,
                IsHitTestVisible = false,
            };

            var toggleContent = new Grid();
            toggleContent.Children.Add(_guidanceToggleHighlight);
            toggleContent.Children.Add(chevron);

            _guidanceToggle = Plate(height, new CornerRadius(radius), toggleContent, uiScale);
            _guidanceToggle.Width = height;
            _guidanceToggle.Height = height;
            _guidanceToggle.Margin = new Thickness(gap, 0, 0, 0);
            _guidanceToggle.Background = System.Windows.Media.Brushes.Transparent;
            _guidanceToggle.Cursor = Cursors.Hand;
            UpdateGuidanceToggleLabel();

            var header = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            header.Children.Add(track);
            header.Children.Add(directionTrack);
            header.Children.Add(_guidanceToggle);

            _hints = [BuildHint(SubtitleHint, uiScale), BuildHint(PanelHint, uiScale)];

            // Both sentences are laid out at once and only one is opaque, so the plate is as tall as
            // the longer of them from the start and choosing a mode cannot change its size.
            var hintContent = new Grid
            {
                Margin = new Thickness(14 * uiScale, 8 * uiScale, 14 * uiScale, 8 * uiScale),
            };
            foreach (var hint in _hints) hintContent.Children.Add(hint);

            _hintPlate = Plate(hintWidth, new CornerRadius(8 * uiScale), hintContent, uiScale);
            _hintPlate.Margin = new Thickness(0, gap, 0, 0);

            // Reads, never clicked. Left hit-testable it would swallow the drag that starts a new
            // block on the picture behind it, for no gain.
            _hintPlate.IsHitTestVisible = false;

            _hintHost = new Grid { ClipToBounds = true };
            _hintHost.Children.Add(_hintPlate);

            Children.Add(header);
            Children.Add(_hintHost);

            for (var index = 0; index < _segments.Length; index++)
            {
                var segment = _segments[index];
                var picked = index;

                segment.MouseLeftButtonDown += (_, e) =>
                {
                    // Must not fall through to the canvas underneath, which would take this as the
                    // start of a new block being drawn.
                    e.Handled = true;
                    _pressedSegment = picked;
                    segment.CaptureMouse();
                    SetPressed(picked, true);
                };
                segment.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var commit = _pressedSegment == picked && segment.IsMouseOver;
                    _pressedSegment = -1;
                    segment.ReleaseMouseCapture();
                    SetPressed(picked, false);
                    if (commit) Select(picked == 0 ? RealtimeBlockMode.Subtitle : RealtimeBlockMode.Panel);
                };

                // Dragged off and back on again while held: the press follows the pointer, so the
                // control keeps saying what releasing right now would do.
                segment.MouseEnter += (_, _) => { if (_pressedSegment == picked) SetPressed(picked, true); };
                segment.MouseLeave += (_, _) => { if (_pressedSegment == picked) SetPressed(picked, false); };
            }

            for (var index = 0; index < _directionSegments.Length; index++)
            {
                var segment = _directionSegments[index];
                var picked = index;

                segment.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    _pressedDirection = picked;
                    segment.CaptureMouse();
                    SetDirectionPressed(picked, true);
                };
                segment.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var commit = _pressedDirection == picked && segment.IsMouseOver;
                    _pressedDirection = -1;
                    segment.ReleaseMouseCapture();
                    SetDirectionPressed(picked, false);
                    if (commit)
                        Select(picked == 0
                            ? RealtimeTextOrientation.Horizontal
                            : RealtimeTextOrientation.Vertical);
                };

                segment.MouseEnter += (_, _) => { if (_pressedDirection == picked) SetDirectionPressed(picked, true); };
                segment.MouseLeave += (_, _) => { if (_pressedDirection == picked) SetDirectionPressed(picked, false); };
            }

            _guidanceToggle.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _guidanceTogglePressed = true;
                _guidanceToggle.CaptureMouse();
                SetGuidanceTogglePressed(true);
            };
            _guidanceToggle.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var commit = _guidanceTogglePressed && _guidanceToggle.IsMouseOver;
                _guidanceTogglePressed = false;
                _guidanceToggle.ReleaseMouseCapture();
                SetGuidanceTogglePressed(false);
                if (commit) ToggleGuidance();
            };
            _guidanceToggle.MouseEnter += (_, _) =>
            {
                if (_guidanceTogglePressed) SetGuidanceTogglePressed(true);
            };
            _guidanceToggle.MouseLeave += (_, _) =>
            {
                if (_guidanceTogglePressed) SetGuidanceTogglePressed(false);
            };

            ApplySelection(animate: false);
            ApplyDirection(animate: false);

            // Both resting sizes are measured up front because the canvas positions this by hand
            // before layout has run. Toggling then only chooses between known expanded and collapsed
            // targets, so neither state depends on a half-finished animation's DesiredSize.
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            _expandedWidth = DesiredSize.Width;
            _expandedHeight = DesiredSize.Height;
            _collapsedWidth = header.DesiredSize.Width;
            _collapsedHeight = header.DesiredSize.Height;
            _expandedHintHeight = _hintHost.DesiredSize.Height;
            _hintHost.Height = _expandedHintHeight;
            ApplyGuidanceState(animate: false);
        }

        /// <summary>Which of the block's two trays the user moved.</summary>
        public enum BlockTray
        {
            /// <summary>字幕 or 遊戲 — what the block holds.</summary>
            Mode,

            /// <summary>橫排 or 直排 — which way its text runs.</summary>
            Direction,
        }

        /// <summary>
        /// Raised with the tray that moved, when the user picks the answer this block is not
        /// already on.
        /// </summary>
        /// <remarks>
        /// One event for both trays, because taking the blocks back is what most of the answer is
        /// for either way. It says WHICH tray because one caller does care: the window carries the
        /// press forward as what the next block starts on, and a press on one tray is not a
        /// statement about the other — pressing 直排 on a 遊戲 block would otherwise quietly make
        /// 遊戲 the default too.
        /// </remarks>
        public event EventHandler<BlockTray>? SelectionChanged;

        /// <summary>
        /// Raised with the new state when the guidance changes size, so the canvas can keep it beside
        /// its block and the window can record what the user asked for.
        /// </summary>
        public event EventHandler<bool>? ExpansionChanged;

        public RealtimeBlockMode Value { get; private set; }

        /// <summary>
        /// Which way this block's text runs. Not called Orientation: this is a StackPanel, and that
        /// name is taken by the one that says which way its own children stack.
        /// </summary>
        public RealtimeTextOrientation TextOrientation { get; private set; }

        /// <summary>Current size, so the caller can keep the visible surface on screen.</summary>
        public double TotalWidth => _guidanceExpanded ? _expandedWidth : _collapsedWidth;

        public double TotalHeight => _guidanceExpanded ? _expandedHeight : _collapsedHeight;

        private void Select(RealtimeBlockMode mode)
        {
            if (mode == Value) return;

            Value = mode;
            ApplySelection(animate: true);
            SelectionChanged?.Invoke(this, BlockTray.Mode);
        }

        private void ApplySelection(bool animate)
        {
            var selected = Value == RealtimeBlockMode.Subtitle ? 0 : 1;

            for (var index = 0; index < _labels.Length; index++)
                _labels[index].Foreground = index == selected ? RemoveForeground : ModeIdleForeground;

            Move(_pillOffset, TranslateTransform.XProperty, selected * _segmentWidth, SlideDuration, animate);

            for (var index = 0; index < _hints.Length; index++)
                Fade(_hints[index], index == selected ? 1.0 : 0.0, HintFadeDuration, animate);
        }

        private void Select(RealtimeTextOrientation orientation)
        {
            if (orientation == TextOrientation) return;

            TextOrientation = orientation;
            ApplyDirection(animate: true);
            SelectionChanged?.Invoke(this, BlockTray.Direction);
        }

        private void ApplyDirection(bool animate)
        {
            var selected = TextOrientation == RealtimeTextOrientation.Horizontal ? 0 : 1;

            for (var index = 0; index < _directionGlyphs.Length; index++)
            {
                var foreground = index == selected ? RemoveForeground : ModeIdleForeground;
                _directionGlyphs[index].Stroke = foreground;
                _directionLabels[index].Foreground = foreground;
            }

            Move(
                _directionPillOffset, TranslateTransform.XProperty,
                selected * _directionSegmentWidth, SlideDuration, animate);
        }

        private void SetPressed(int segment, bool pressed) =>
            Fade(_highlights[segment], pressed ? 1.0 : 0.0, PressDuration, animate: true);

        private void SetDirectionPressed(int segment, bool pressed) =>
            Fade(_directionHighlights[segment], pressed ? 1.0 : 0.0, PressDuration, animate: true);

        private void SetGuidanceTogglePressed(bool pressed) =>
            Fade(_guidanceToggleHighlight, pressed ? 1.0 : 0.0, PressDuration, animate: true);

        private void ToggleGuidance()
        {
            _guidanceExpanded = !_guidanceExpanded;
            UpdateGuidanceToggleLabel();
            ApplyGuidanceState(animate: true);
            ExpansionChanged?.Invoke(this, _guidanceExpanded);
        }

        private void UpdateGuidanceToggleLabel()
        {
            var label = LocalizationService.Get(
                _guidanceExpanded ? "S.Realtime.CollapseGuidance" : "S.Realtime.ExpandGuidance");
            _guidanceToggle.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(_guidanceToggle, label);
        }

        private void ApplyGuidanceState(bool animate)
        {
            var expanded = _guidanceExpanded;
            var targetHeight = expanded ? _expandedHintHeight : 0;
            var targetOpacity = expanded ? 1.0 : 0.0;
            var targetAngle = expanded ? 0.0 : 180.0;
            var transition = ++_guidanceTransition;

            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                _hintHost.BeginAnimation(HeightProperty, null);
                _hintPlate.BeginAnimation(OpacityProperty, null);
                _guidanceChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                _hintHost.Height = targetHeight;
                _hintPlate.Opacity = targetOpacity;
                _guidanceChevronRotation.Angle = targetAngle;
                _hintHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (expanded) _hintHost.Visibility = Visibility.Visible;

            var heightAnimation = new DoubleAnimation(_hintHost.ActualHeight, targetHeight, GuidanceDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            heightAnimation.Completed += (_, _) =>
            {
                if (transition != _guidanceTransition) return;
                _hintHost.BeginAnimation(HeightProperty, null);
                _hintHost.Height = targetHeight;
                if (!expanded) _hintHost.Visibility = Visibility.Collapsed;
            };
            _hintHost.BeginAnimation(HeightProperty, heightAnimation);

            _hintPlate.BeginAnimation(
                OpacityProperty,
                Transition(_hintPlate.Opacity, targetOpacity, GuidanceDuration));
            _guidanceChevronRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                Transition(_guidanceChevronRotation.Angle, targetAngle, GuidanceDuration));
        }

        /// <summary>
        /// A dark surface with a hairline along its top edge, holding the content.
        /// </summary>
        /// <remarks>
        /// <para>No drop shadow. Each plate had one, and with four plates and the remove button they
        /// were five blurs re-run on every frame of a drag: measured under a GPU-bound game, dropping
        /// them took the drag from 19 to 55 frames a second, and over bright and dark scenes alike
        /// the near-opaque plates look no different without them.</para>
        ///
        /// <para>The hairline is a border nested inside the fill rather than the fill's own border:
        /// a Border paints its background inside its border, so on one element the hairline would
        /// sit over whatever is behind the plate instead of over the plate. The fill is itself inside
        /// the Border returned, which carries no fill of its own: callers size and style that one,
        /// and the guidance toggle sets its background to make the whole circle clickable.</para>
        /// </remarks>
        private static Border Plate(double width, CornerRadius corner, UIElement content, double uiScale) => new()
        {
            Width = width,
            Child = new Border
            {
                CornerRadius = corner,
                Background = ModeTrack,
                Child = new Border
                {
                    CornerRadius = corner,
                    BorderBrush = ModeTrackEdge,
                    BorderThickness = new Thickness(0, 1 * uiScale, 0, 0),
                    Child = content,
                },
            },
        };

        /// <summary>
        /// Animates one transform property to a new value, from wherever it is on screen right now.
        /// </summary>
        /// <remarks>
        /// The animation is given a target and no start, which is what makes a second click part way
        /// through the first one's movement continue from where the pill actually is rather than
        /// jumping back to where it started. Skipped entirely when the desktop has animation turned
        /// off, and then the property is cleared first — an animation left in place would otherwise
        /// hold the value it finished on and ignore everything set afterwards.
        /// </remarks>
        private static void Move(
            Transform transform, DependencyProperty property, double to, Duration duration, bool animate)
        {
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                transform.BeginAnimation(property, null);
                transform.SetValue(property, to);
                return;
            }

            transform.BeginAnimation(property, new DoubleAnimation(to, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private static DoubleAnimation Transition(double from, double to, Duration duration) => new(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        /// <summary>Cross-fades a layer, from its current opacity — see <see cref="Move"/>.</summary>
        private static void Fade(UIElement element, double to, Duration duration, bool animate)
        {
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                return;
            }

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private static TextBlock BuildLabel(string text, double uiScale) =>
            Sharpen(new TextBlock
            {
                Text = text,
                FontSize = BaseModeFontSize * uiScale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Leading a little looser than the default: this is dense CJK read once, off a translucent
        // surface, over moving picture, and the extra air is what stops the lines running together.
        private static TextBlock BuildHint(string text, double uiScale) =>
            Sharpen(new TextBlock
            {
                Text = text,
                FontSize = BaseHintFontSize * uiScale,
                FontWeight = HeavierOverPicture,
                LineHeight = BaseHintFontSize * 1.6 * uiScale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Foreground = RemoveForeground,
                VerticalAlignment = VerticalAlignment.Top,
            });

        // Display formatting rounds glyph widths and positions onto whole pixels, which at these
        // sizes is the difference between a stem one pixel wide and a stem smeared across two. WPF's
        // default (Ideal) keeps the typographic metrics instead, which is right for large text and
        // wrong for small interface text — and this is small interface text over moving picture.
        private static TextBlock Sharpen(TextBlock text)
        {
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.ClearType);
            return text;
        }

        private static Border BuildHighlight(CornerRadius corner, double inset) => new()
        {
            Background = PressHighlight,
            CornerRadius = corner,
            Margin = new Thickness(0, inset, 0, inset),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        /// <summary>
        /// One direction glyph, drawn rather than set in type: three rules running the way the text
        /// does, the last of them short so the pair reads as writing and not as a table.
        /// </summary>
        /// <remarks>
        /// Stretched into a square box so the horizontal and vertical marks are each other turned,
        /// which is the whole of what they have to say. The stroke is not stretched with them — a
        /// Shape draws its pen after the stretch — so both keep the hairline weight the chevron
        /// beside them has.
        /// </remarks>
        private static System.Windows.Shapes.Path BuildDirectionGlyph(string data, double uiScale) => new()
        {
            Data = Geometry.Parse(data),
            Width = 13 * uiScale,
            Height = 13 * uiScale,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5 * uiScale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        /// <summary>
        /// One direction segment's contents: the mark, then the word for it.
        /// </summary>
        /// <remarks>
        /// The gap is the one the screenshot toolbar leaves between the same two things, so a reader
        /// who knows that switch meets the same object here rather than a near-copy of it.
        /// </remarks>
        private static StackPanel BuildDirectionContent(
            System.Windows.Shapes.Path glyph, TextBlock label, double uiScale)
        {
            glyph.Margin = new Thickness(0, 0, 6 * uiScale, 0);

            var content = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyph);
            content.Children.Add(label);
            return content;
        }

        // Transparent rather than unset: a null background is not hit-testable, and the segment is
        // the thing being clicked.
        private static Border BuildSegment(UIElement label, Border highlight, double width, string tip)
        {
            var content = new Grid();
            content.Children.Add(highlight);
            content.Children.Add(label);

            return new Border
            {
                Width = width,
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = content,
            };
        }
    }
}
