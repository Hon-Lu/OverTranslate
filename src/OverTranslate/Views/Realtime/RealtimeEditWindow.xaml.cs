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
    /// The narrowest and widest the mode button is, before the widest name it may have to show is
    /// measured into it — see <see cref="ModeSegments"/>.
    /// </summary>
    /// <remarks>
    /// Fixed for the life of the layer, at the widest of every mode it can be set to, so choosing a
    /// mode never changes its width: a control that grows and shrinks with its answer reads as the
    /// screen being rearranged. The cap is for the custom names, which are the user's own text; a
    /// custom name past it is cut with an ellipsis and shown whole on the tooltip. The presets'
    /// names are never cut — the button grows past the cap for one of those instead — because they
    /// are the words the list is read by.
    /// </remarks>
    private const double BaseModeButtonMinWidth = 160;

    /// <inheritdoc cref="BaseModeButtonMinWidth"/>
    private const double BaseModeButtonMaxWidth = 240;

    /// <summary>
    /// Widest the open list of modes is: as wide as the button, or as its longest name, up to this.
    /// </summary>
    private const double BaseModeMenuMaxWidth = 280;

    /// <summary>Space kept before a mode's name inside the button, and either side of each direction label.</summary>
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

    // The copy's hint as a fraction of the copy's height — see PlaceCopyHint.
    private const double CopyHintHeightRatio = 0.2;

    private static readonly SolidColorBrush FrameStroke = Freeze(Color.FromArgb(0xE6, 0x1E, 0x90, 0xD5));
    private static readonly SolidColorBrush FrameFill = Freeze(Color.FromArgb(0x1C, 0x99, 0xC8, 0xF0));
    private static readonly SolidColorBrush HandleFill = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RemoveForeground = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    // 對照顯示's copy of a block, and everything that belongs to it — outline, label, ✕, corner
    // handles, the line back to the block — in amber rather than the block's blue. The two are
    // different things: the block is the area being read, the copy is where its translation will be
    // drawn, and in the block's own blue, however faint, the copy was easy to miss and easy to take
    // for a second block. A colour of its own is found at a glance and says which is which. Amber
    // because it is far from the blue and holds up over both a bright and a dark scene. The block's
    // own buttons — remove, and restore on the trays — stay blue: they act on the block.
    private static readonly SolidColorBrush CopyStroke = Freeze(Color.FromArgb(0xF2, 0xF0, 0xA0, 0x28));
    private static readonly SolidColorBrush CopyFill = Freeze(Color.FromArgb(0x24, 0xF0, 0xA0, 0x28));
    // 「譯文顯示位置」 in the middle of the copy: white text with a dark rim round every glyph, which
    // keeps it legible over a bright scene and a dark one alike.
    private static readonly SolidColorBrush CopyHintLetters = Freeze(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CopyHintRim = Freeze(Color.FromArgb(0xA6, 0x00, 0x00, 0x00));
    // A thin dark rim either side of the dashes, so the amber line still has an edge over a pale
    // scene. Two hairlines rather than a shadow, for the reason the outline has none.
    private static readonly SolidColorBrush CopyRimOuter = Freeze(Color.FromArgb(0x8C, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush CopyRimInner = Freeze(Color.FromArgb(0x59, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush CopyLabelFill = Freeze(Color.FromArgb(0xE6, 0xF0, 0xA0, 0x28));
    private static readonly SolidColorBrush CopyLabelForeground = Freeze(Color.FromRgb(0x2A, 0x1A, 0x00));
    // The copy's ✕: the label's amber, its glyph about 90%, drawn as two strokes heavier than the
    // label's text. A darker step of the amber was tried and read as too dark.
    private static readonly SolidColorBrush CopyCloseFill = CopyLabelFill;
    private static readonly SolidColorBrush CopyCloseForeground = Freeze(Color.FromArgb(0xE6, 0x2A, 0x1A, 0x00));

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
    // The restore tray: the block's blue at the trays' opacity.
    private static readonly SolidColorBrush RestoreFill = Freeze(Color.FromArgb(0xD8, 0x1E, 0x90, 0xD5));
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
    private double _directionSegmentWidth = BaseDirectionSegmentWidth;
    private double _modeHeight = BaseModeHeight;
    private double _modeInset = BaseModeInset;
    private double _hintWidth = BaseHintWidth;

    // How wide the copy's hint is per unit of font size — measured once, since the text is fixed for
    // the layer's lifetime. See PlaceCopyHint.
    private double _copyHintUnitWidth;

    private Point _drawOrigin;
    private Shape? _drawPreview;

    /// <summary>
    /// Whether the layer is taking the mouse. See <see cref="SetCrosshairEnabled"/>.
    /// </summary>
    private bool _crosshairEnabled = true;

    /// <summary>
    /// What the next block drawn is set to: a mode, by id, and a direction. The two trays move these
    /// as they are used — see <see cref="BlockDefaultsChanged"/>.
    /// </summary>
    private string _blockProfile;

    /// <inheritdoc cref="_blockProfile"/>
    private RealtimeTextOrientation _textOrientation;

    /// <summary>
    /// Whether 對照顯示 is on, and with it whether every block shows the copy its translation will be
    /// drawn in. See <see cref="SetCompareDisplay"/>.
    /// </summary>
    private bool _compareDisplay;

    /// <summary>
    /// Every mode a block can be set to, presets first, as the controller read them from the
    /// settings file — this layer never reads the file itself. Replaced whole by
    /// <see cref="SetProfileChoices"/>.
    /// </summary>
    private IReadOnlyList<ProfileChoice> _choices;

    /// <summary>The list of modes while it is open, and the block it was opened from.</summary>
    private ModeMenu? _menu;
    private BlockVisual? _menuOwner;

    /// <summary>
    /// The whole canvas under the open list, above everything else: the press that closes the list
    /// lands here and goes no further — see <see cref="OpenMenu"/>.
    /// </summary>
    private Border? _menuCatcher;

    public RealtimeEditWindow(
        System.Drawing.Rectangle physBounds,
        IReadOnlyList<RealtimeBlockPlacement> initialBlocks,
        int maxBlocks,
        bool guidanceExpanded,
        string blockProfile,
        RealtimeTextOrientation textOrientation,
        IReadOnlyList<ProfileChoice> choices,
        bool compareDisplay = false)
    {
        InitializeComponent();

        _physBounds = physBounds;
        _initialBlocks = initialBlocks;
        _maxBlocks = maxBlocks;
        _guidanceExpanded = guidanceExpanded;
        _choices = choices;
        _blockProfile = KnownProfile(blockProfile);
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
                    ToCanvas(block.Bounds), block.ProfileId, block.Orientation, _guidanceExpanded,
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
    public event EventHandler<(string Profile, RealtimeTextOrientation Orientation)>?
        BlockDefaultsChanged;

    /// <summary>
    /// Raised with the mode of the block whose list asked for 管理模式…: the caller ends the sitting
    /// and opens the page on that mode. Nothing on the block changes.
    /// </summary>
    public event EventHandler<string>? ManageProfilesRequested;

    public int BlockCount => _blocks.Count;

    /// <summary>
    /// Puts every block on the modes as they are now — after one was added, renamed or deleted on
    /// the page while this layer was up.
    /// </summary>
    /// <remarks>
    /// Each block's trays are built again, because the button's width, the list and the guidance
    /// plate's height are all measured from the names. A block on a mode that is gone goes to 標準,
    /// and so does the answer the next block starts on; the blocks are then reported as changed, so
    /// the session holds what they show. Nothing is rebuilt when only a value of a mode changed:
    /// nothing this layer shows depends on one.
    /// </remarks>
    public void SetProfileChoices(IReadOnlyList<ProfileChoice> choices)
    {
        if (choices.SequenceEqual(_choices)) return;

        CloseMenu(animate: false);
        _choices = choices;
        _blockProfile = KnownProfile(_blockProfile);

        foreach (var visual in _blocks)
        {
            var old = visual.ModeControl;
            visual.ModeControl = BuildModeControl(
                KnownProfile(visual.ProfileId), old.TextOrientation, old.GuidanceExpanded);
            visual.ProfileId = visual.ModeControl.ProfileId;
            WireModeControl(visual);
        }

        RebuildCanvas();
        RaiseBlocksChanged();
    }

    /// <summary>
    /// Closes the open list of modes, if there is one. Esc asks this first, so a press that only
    /// meant to put the list away does not end the sitting.
    /// </summary>
    /// <returns>Whether a list was open.</returns>
    public bool TryDismissMenu()
    {
        if (_menu is null) return false;
        CloseMenu(animate: true);
        return true;
    }

    private string KnownProfile(string? id) =>
        _choices.Any(choice => choice.Id == id) ? id! : Models.RealtimePresets.StandardId;

    /// <summary>The current blocks in physical screen pixels, ready to be watched.</summary>
    public IReadOnlyList<RealtimeBlockPlacement> GetPhysicalBlocks() =>
        [.. _blocks.Select(block => new RealtimeBlockPlacement(
            ToPhysical(block.Bounds), block.ProfileId, block.ModeControl.TextOrientation,
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
        // nobody has said anything yet is RealtimeSettings.NewBlockProfile, which keeps the reasoning.
        AddBlock(box, _blockProfile, _textOrientation, _guidanceExpanded, notify: true);
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

    private ModeSegments BuildModeControl(
        string profileId, RealtimeTextOrientation orientation, bool guidanceExpanded) =>
        new(profileId, _choices, orientation, guidanceExpanded, _modeHeight, _directionSegmentWidth,
            _modeInset, _hintWidth, _removeGap, _uiScale);

    private void AddBlock(
        Rect bounds,
        string profileId,
        RealtimeTextOrientation orientation,
        bool guidanceExpanded,
        bool notify,
        Vector? compareOffset = null,
        double compareScale = 1.0,
        bool compareHidden = false)
    {
        profileId = KnownProfile(profileId);
        var visual = new BlockVisual(
            bounds, BuildModeControl(profileId, orientation, guidanceExpanded), _handleSize, _removeSize,
            _uiScale)
        {
            ProfileId = profileId,
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
        WireModeControl(visual);

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

    /// <summary>
    /// Hooks a block's trays up to the layer — once when the block is made, and again whenever
    /// <see cref="SetProfileChoices"/> gives it new ones.
    /// </summary>
    private void WireModeControl(BlockVisual visual)
    {
        var mode = visual.ModeControl;

        mode.Restore.Click += (_, e) =>
        {
            e.Handled = true;
            SetCompareHidden(visual, false);
        };
        mode.SelectionChanged += (_, tray) =>
        {
            // The direction decides which side an automatic copy goes, so it may have just moved.
            if (tray == ModeSegments.BlockTray.Direction) ApplyAll();
            else visual.ProfileId = mode.ProfileId;
            RaiseBlocksChanged();

            // The tray last pressed is the one that counts, on whichever block it was pressed — and
            // only that tray, so a block that is 遊戲 for its own reasons does not make 遊戲 the
            // answer for the next block just because its direction was corrected. The blocks already
            // drawn are left alone either way: correcting one says nothing about the rest.
            if (tray == ModeSegments.BlockTray.Mode) _blockProfile = visual.ProfileId;
            else _textOrientation = mode.TextOrientation;
            BlockDefaultsChanged?.Invoke(this, (_blockProfile, _textOrientation));
        };
        mode.ExpansionChanged += (_, expanded) =>
        {
            Apply(visual, animateMode: true);

            // The last chevron pressed is the one that counts, for the blocks drawn after it and for
            // the next sitting alike. The blocks already on screen are left as they are: folding one
            // away should not make the others move under the pointer.
            _guidanceExpanded = expanded;
            GuidanceExpandedChanged?.Invoke(this, expanded);
        };
        mode.MenuRequested += (_, _) =>
        {
            // A second press on the button that opened the list closes it — though the catcher
            // over everything takes that press first, so this is the keyboard-free path only.
            if (ReferenceEquals(_menuOwner, visual)) CloseMenu(animate: true);
            else OpenMenu(visual);
        };
    }

    private void RemoveBlock(BlockVisual visual)
    {
        _blocks.Remove(visual);
        RebuildCanvas();
        RaiseBlocksChanged();
    }

    // ── The list of modes ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the list of modes under a block's mode button — or over it, where there is no room
    /// below — on this canvas rather than in a popup or a window of its own.
    /// </summary>
    /// <remarks>
    /// <para>Drawn here because this layer never takes activation: a WPF popup is a window of its
    /// own, and one owned by a window that cannot be active has no keyboard and, where the
    /// foreground is refused, closes the moment it opens (#358, see RealtimeLanguageMenu). A list
    /// on the canvas needs neither — it is just more of the layer.</para>
    ///
    /// <para>A catcher covers the whole canvas under it while it is open. A press anywhere off the
    /// list closes it and goes no further: on a game screen, "click somewhere empty to put the menu
    /// away" would otherwise also start drawing a new block, or press whatever control of another
    /// block happened to be there.</para>
    ///
    /// <para>Mouse only, and Esc through <see cref="TryDismissMenu"/>: without activation there are
    /// no arrow keys to give it.</para>
    /// </remarks>
    private void OpenMenu(BlockVisual visual)
    {
        CloseMenu(animate: false);

        var button = visual.ModeControl.MenuButton;
        var origin = button.TranslatePoint(new Point(0, 0), BlockCanvas);
        double buttonHeight = button.ActualHeight > 0 ? button.ActualHeight : _modeHeight;

        var menu = new ModeMenu(
            _choices, visual.ModeControl.ProfileId, button.ActualWidth > 0 ? button.ActualWidth : button.Width,
            BaseModeMenuMaxWidth * _uiScale, _modeHeight, _uiScale);
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double height = menu.DesiredSize.Height;
        double width = menu.DesiredSize.Width;

        // Below by preference, where a list is expected; above where it does not fit; and where it
        // fits neither way, on the roomier side with the custom modes scrolled.
        double canvasHeight = BlockCanvas.ActualHeight;
        double belowTop = origin.Y + buttonHeight + _removeGap;
        double below = canvasHeight - belowTop;
        double above = origin.Y - _removeGap;
        double top;
        if (height <= below) top = belowTop;
        else if (height <= above) top = above - height;
        else if (below >= above)
        {
            height = menu.FitHeight(below);
            top = belowTop;
        }
        else
        {
            height = menu.FitHeight(above);
            top = above - height;
        }

        // Pushed back in from the right edge rather than cut off.
        double left = Math.Clamp(origin.X, 0, Math.Max(0, BlockCanvas.ActualWidth - width));

        var catcher = new Border { Background = System.Windows.Media.Brushes.Transparent };
        catcher.Width = BlockCanvas.ActualWidth;
        catcher.Height = BlockCanvas.ActualHeight;
        catcher.MouseDown += (_, e) =>
        {
            e.Handled = true;
            CloseMenu(animate: true);
        };
        catcher.MouseUp += (_, e) => e.Handled = true;
        System.Windows.Controls.Panel.SetZIndex(catcher, 1000);
        System.Windows.Controls.Panel.SetZIndex(menu, 1001);

        Canvas.SetLeft(menu, SnapToPixels(left, _dpiX));
        Canvas.SetTop(menu, SnapToPixels(Math.Max(0, top), _dpiY));

        menu.Picked += (_, id) =>
        {
            CloseMenu(animate: true);
            visual.ModeControl.Pick(id);
        };
        menu.ManageRequested += (_, _) =>
        {
            CloseMenu(animate: false);
            ManageProfilesRequested?.Invoke(this, visual.ModeControl.ProfileId);
        };

        BlockCanvas.Children.Add(catcher);
        BlockCanvas.Children.Add(menu);
        _menu = menu;
        _menuOwner = visual;
        _menuCatcher = catcher;
        visual.ModeControl.SetMenuOpen(true);
        menu.FadeIn();
    }

    /// <summary>
    /// Takes the list off the canvas, fading it when <paramref name="animate"/>. The catcher goes at
    /// once either way, so the next press already reaches whatever is under it.
    /// </summary>
    private void CloseMenu(bool animate)
    {
        if (_menu is not { } menu) return;

        _menuOwner?.ModeControl.SetMenuOpen(false);
        if (_menuCatcher is not null) BlockCanvas.Children.Remove(_menuCatcher);
        _menu = null;
        _menuOwner = null;
        _menuCatcher = null;

        menu.FadeOut(animate, () => BlockCanvas.Children.Remove(menu));
    }

    // Three layers, bottom to top, and what is drawn on top is also what a click lands on:
    //
    //   1. the block frames, which are also how a block is dragged;
    //   2. the 對照顯示 copies, with their lines, labels, corner handles and ✕;
    //   3. every block's handles, remove buttons, and trays (the restore button is one of the trays).
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
        // A block added or removed puts the list away: it was opened for a layout that has changed.
        CloseMenu(animate: false);
        BlockCanvas.Children.Clear();

        foreach (var block in _blocks)
            BlockCanvas.Children.Add(block.Body);

        foreach (var block in _blocks)
        {
            BlockCanvas.Children.Add(block.Link);
            BlockCanvas.Children.Add(block.Copy);
            BlockCanvas.Children.Add(block.CopyHint);
            BlockCanvas.Children.Add(block.CopyGrip);
            foreach (var corner in block.CopyCorners)
                BlockCanvas.Children.Add(corner);
            BlockCanvas.Children.Add(block.CopyClose);
        }

        foreach (var block in _blocks)
        {
            foreach (var corner in block.Corners)
                BlockCanvas.Children.Add(corner);
            BlockCanvas.Children.Add(block.Remove);
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
    /// Turns one block's copy off — the ✕ at its corner — or back on — the button under its remove
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
        PlaceCopyHint(visual, copy);

        // Outside the top-right corner by preference, so it never covers the content being framed;
        // tucked inside when the block is against the screen edge and there is no room out there.
        double removeLeft = bounds.Right + _removeGap;
        if (removeLeft + _removeSize > BlockCanvas.ActualWidth)
            removeLeft = bounds.Right - _removeSize - _removeGap;
        Canvas.SetLeft(visual.Remove, removeLeft);
        Canvas.SetTop(visual.Remove, Math.Max(0, bounds.Top));

        // At the end of the trays, shown only while there is a copy to bring back. Set before the
        // trays are placed, because it changes how wide they are.
        visual.ModeControl.SetRestoreShown(_compareDisplay && visual.CompareHidden);

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
    /// Sizes the hint to a block's copy and centres it there; takes it off the canvas with the copy.
    /// </summary>
    /// <remarks>
    /// <para>Always in the middle and always shown, at a size taken from the copy: a fifth of its
    /// height, or whatever keeps the line within 80% of its width if that is less. So it grows and
    /// shrinks with the copy as either the copy or its block is resized — never hidden, never moved
    /// off centre, and with no smallest size, because a copy that small is one the user pulled down
    /// on purpose. A fifth because over a subtitle-sized block that is clearly larger than the
    /// layer's interface text, yet small enough to leave the corners to the label and handles.</para>
    ///
    /// <para>The outline is rebuilt only when that size changes, on half-unit steps; moving the
    /// copy only moves it.</para>
    /// </remarks>
    private void PlaceCopyHint(BlockVisual visual, Rect? copy)
    {
        var hint = visual.CopyHint;
        if (copy is not { } box)
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }

        if (_copyHintUnitWidth <= 0)
            _copyHintUnitWidth = CopyHintText(1.0).WidthIncludingTrailingWhitespace;

        double size = Math.Min(box.Height * CopyHintHeightRatio, box.Width * 0.8 / _copyHintUnitWidth);
        size = Math.Max(0.5, Math.Round(size * 2) / 2);

        if (size != visual.CopyHintSize)
        {
            var glyphs = CopyHintText(size).BuildGeometry(new Point(0, 0));
            glyphs.Freeze();
            visual.CopyHintFill.Data = glyphs;
            visual.CopyHintOutline.Data = glyphs;
            // Twice the rim wanted: the stroke is centred on the glyph's edge and the fill drawn over
            // it covers the inner half.
            visual.CopyHintOutline.StrokeThickness = Math.Max(1.5 * _uiScale, size * 0.16);
            visual.CopyHintSize = size;
        }

        // By the ink, not the line box, so the text sits in the true middle whatever the font's
        // ascent and descent.
        var ink = visual.CopyHintFill.Data.Bounds;
        Canvas.SetLeft(hint, box.X + box.Width / 2 - (ink.X + ink.Width / 2));
        Canvas.SetTop(hint, box.Y + box.Height / 2 - (ink.Y + ink.Height / 2));
        hint.Visibility = Visibility.Visible;
    }

    private FormattedText CopyHintText(double size) => new(
        LocalizationService.Get("S.Realtime.CompareCopyCenter"),
        System.Globalization.CultureInfo.CurrentUICulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        size,
        System.Windows.Media.Brushes.White,
        _dpiY);

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
    private void PlaceCopy(BlockVisual visual, Rect? copy)
    {
        visual.ShownCopy = copy;
        if (copy is not { } box)
        {
            visual.Copy.Visibility = Visibility.Collapsed;
            visual.CopyGrip.Visibility = Visibility.Collapsed;
            visual.CopyClose.Visibility = Visibility.Collapsed;
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

        visual.CopyGrip.Visibility = Visibility.Visible;
        Canvas.SetLeft(visual.CopyGrip, box.X + visual.GripInset);
        Canvas.SetTop(visual.CopyGrip, box.Y + visual.GripInset);

        // Where the block's remove button goes on the block, for the same reasons.
        double closeLeft = box.Right + _removeGap;
        if (closeLeft + _removeSize > BlockCanvas.ActualWidth)
            closeLeft = box.Right - _removeSize - _removeGap;
        visual.CopyClose.Visibility = Visibility.Visible;
        Canvas.SetLeft(visual.CopyClose, closeLeft);
        Canvas.SetTop(visual.CopyClose, Math.Max(0, box.Top));

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
            ModeSegments modeControl,
            double handleSize,
            double removeSize,
            double uiScale)
        {
            ModeControl = modeControl;
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
                Visibility = Visibility.Collapsed,
            };

            // The copy's own remove button, in the same place and of the same size as the block's,
            // so the two are found the same way.
            CopyClose = new Button
            {
                Width = removeSize,
                Height = removeSize,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("S.Realtime.CompareHideBlock"),
                Template = BuildCopyCloseTemplate(removeSize, uiScale),
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

            // The rim and the letters as two paths on one geometry, rim underneath: an outline
            // drawn without an Effect, which would be re-rendered on every frame of a drag. Neither
            // takes the mouse, so a press on the hint drags the copy.
            CopyHintOutline = new System.Windows.Shapes.Path
            {
                Stroke = CopyHintRim,
                StrokeLineJoin = PenLineJoin.Round,
            };
            CopyHintFill = new System.Windows.Shapes.Path { Fill = CopyHintLetters };
            CopyHint = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            CopyHint.Children.Add(CopyHintOutline);
            CopyHint.Children.Add(CopyHintFill);

            Link = new System.Windows.Shapes.Line
            {
                Stroke = CopyStroke,
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
        /// The ✕ outside the copy's top-right corner: turns this block's copy off and leaves the
        /// others alone. See <see cref="CompareHidden"/>.
        /// </summary>
        public Button CopyClose { get; }

        /// <summary>
        /// Whether the user has turned this block's copy off — see
        /// <see cref="RealtimeBlockPlacement.CompareHidden"/>.
        /// </summary>
        public bool CompareHidden { get; set; }

        /// <summary>
        /// Brings a turned-off copy back. One of the trays — see <see cref="ModeSegments.Restore"/>.
        /// </summary>
        public Button Restore => ModeControl.Restore;

        /// <summary>
        /// 「譯文顯示位置」 in the middle of the copy, sized to it — see
        /// <see cref="RealtimeEditWindow.PlaceCopyHint"/>.
        /// </summary>
        public Canvas CopyHint { get; }

        /// <summary>The hint's letters.</summary>
        public System.Windows.Shapes.Path CopyHintFill { get; }

        /// <summary>The dark rim under <see cref="CopyHintFill"/>.</summary>
        public System.Windows.Shapes.Path CopyHintOutline { get; }

        /// <summary>The font size the hint's outline was last built at, or 0 before the first.</summary>
        public double CopyHintSize { get; set; }

        /// <summary>How far the label sits in from the copy's top-left corner.</summary>
        public double GripInset { get; }

        /// <summary>
        /// Where <see cref="Copy"/> was last put, or null while it is hidden — what a drag elsewhere
        /// compares against to tell whether this block needs laying out again.
        /// </summary>
        public Rect? ShownCopy { get; set; }

        /// <summary>The line from the block to its copy.</summary>
        public System.Windows.Shapes.Line Link { get; }

        /// <summary>
        /// The block's trays: which mode it reads with and which way its text runs. Replaced when
        /// the modes on offer change — see <see cref="RealtimeEditWindow.SetProfileChoices"/>.
        /// </summary>
        public ModeSegments ModeControl { get; set; }

        /// <summary>The mode this block is on, by id — see <see cref="RealtimeBlockPlacement.ProfileId"/>.</summary>
        public string ProfileId { get; set; } = Models.RealtimePresets.StandardId;

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
        /// A dashed amber outline. The label in its corner and the hint in its middle are separate
        /// elements — see <see cref="BuildGripTemplate"/> — because the label has to be drawn above
        /// the blocks while the outline is drawn below them, and the hint is resized with the copy.
        /// </summary>
        private static ControlTemplate BuildCopyTemplate(double uiScale)
        {
            var outline = new FrameworkElementFactory(typeof(Shape));
            outline.SetValue(System.Windows.Shapes.Shape.StrokeProperty, CopyStroke);
            outline.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2 * uiScale);
            outline.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection([4, 3]));
            outline.SetValue(System.Windows.Shapes.Shape.FillProperty, CopyFill);
            outline.SetValue(Shape.RadiusXProperty, 3 * uiScale);
            outline.SetValue(Shape.RadiusYProperty, 3 * uiScale);

            // The rim: a hairline just outside the dashes and a fainter one just inside. A shape's
            // stroke is drawn inside its bounds, so the outer one sits on a box a pixel larger and
            // the inner one on a box inset by the width of the dashes.
            var outer = new FrameworkElementFactory(typeof(Shape));
            outer.SetValue(System.Windows.Shapes.Shape.StrokeProperty, CopyRimOuter);
            outer.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1 * uiScale);
            outer.SetValue(FrameworkElement.MarginProperty, new Thickness(-1 * uiScale));
            outer.SetValue(Shape.RadiusXProperty, 4 * uiScale);
            outer.SetValue(Shape.RadiusYProperty, 4 * uiScale);

            var inner = new FrameworkElementFactory(typeof(Shape));
            inner.SetValue(System.Windows.Shapes.Shape.StrokeProperty, CopyRimInner);
            inner.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1 * uiScale);
            inner.SetValue(FrameworkElement.MarginProperty, new Thickness(2 * uiScale));
            inner.SetValue(Shape.RadiusXProperty, 1 * uiScale);
            inner.SetValue(Shape.RadiusYProperty, 1 * uiScale);

            var layers = new FrameworkElementFactory(typeof(Grid));
            layers.AppendChild(outer);
            layers.AppendChild(outline);
            layers.AppendChild(inner);

            // No drop shadow, unlike the block's frame. A blur over a box the size of the block is
            // re-run on every frame the copy moves, and the copy is the thing being dragged; the
            // amber, its rim and the label keep it legible without one.
            return new ControlTemplate(typeof(Thumb)) { VisualTree = layers };
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
            // Dark on the amber: white on it is too little contrast to read.
            text.SetValue(TextBlock.ForegroundProperty, CopyLabelForeground);
            text.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);

            var label = new FrameworkElementFactory(typeof(Border));
            label.SetValue(Border.BackgroundProperty, CopyLabelFill);
            label.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.5 * uiScale));
            label.SetValue(Border.PaddingProperty, new Thickness(6 * uiScale, 1.2 * uiScale, 6 * uiScale, 2.4 * uiScale));
            label.AppendChild(text);

            return new ControlTemplate(typeof(Thumb)) { VisualTree = label };
        }

        /// <summary>
        /// The copy's ✕: the block's remove button in the copy's amber rather than the block's blue,
        /// because this one only lets go of a copy and the other destroys a block, and the two must
        /// not be mistaken for each other.
        /// </summary>
        private static ControlTemplate BuildCopyCloseTemplate(double removeSize, double uiScale)
        {
            // Two strokes rather than the ✕ character: a font's cross is a hairline at this size and
            // no weight of it is much heavier, where a drawn one can be given the weight it needs.
            var glyph = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            glyph.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M0,0 L1,1 M1,0 L0,1"));
            glyph.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Fill);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeProperty, CopyCloseForeground);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.6 * uiScale);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            glyph.SetValue(FrameworkElement.WidthProperty, 8.0 * uiScale);
            glyph.SetValue(FrameworkElement.HeightProperty, 8.0 * uiScale);
            glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, CopyCloseFill);
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

        // The block's handle inverted: the outline's amber inside, a white ring round it.
        private static ControlTemplate BuildCopyHandleTemplate(double handleSize, double uiScale)
        {
            var handle = new FrameworkElementFactory(typeof(Border));
            handle.SetValue(Border.BackgroundProperty, CopyStroke);
            handle.SetValue(Border.BorderBrushProperty, HandleFill);
            handle.SetValue(Border.BorderThicknessProperty, new Thickness(1.5 * uiScale));
            handle.SetValue(Border.CornerRadiusProperty, new CornerRadius(handleSize / 2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = handle };
        }

        private static ControlTemplate BuildRemoveTemplate(double removeSize, double uiScale)
        {
            // E8BB rather than a typed ✕, which sits 0.71px below the chip's centre at this size;
            // E8BB is exactly on it, and 8 keeps the old glyph's 8px of ink.
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "");
            glyph.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
            glyph.SetValue(TextBlock.FontSizeProperty, 8.0 * uiScale);
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
    /// The per-block control: the block's mode as a button that opens the list of modes, both
    /// directions always on screen with the selected one filled in, and under them a line saying
    /// what that kind of block is.
    /// </summary>
    /// <remarks>
    /// THE MODE BUTTON. The mode used to be three segments on one track, like the direction beside
    /// it. Custom modes ended that: up to thirteen answers do not fit on a track, so the mode shows
    /// only its current answer and the rest come up in a list — see <see cref="ModeMenu"/>. The
    /// button is that answer drawn the way the segments drew one, a blue pill on the dark track, so
    /// the two controls on the row still read alike. It does not grey out for a vertical block: the
    /// timing values of a mode apply to either direction.
    ///
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
        internal static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(100));

        // Pressing lights the segment rather than shrinking the control, and that is a rendering
        // decision as much as a design one. A scale on the track is a transform over text, and WPF
        // drops pixel snapping the moment it believes text is animating, then ramps it back over
        // about a second — so every press would leave both labels soft for a beat afterwards (see
        // ShellWindow.AnimateContentIn, which pays a bitmap cache to avoid exactly that). Lighting
        // the segment also says which of the two is being pressed, which a scale of the whole
        // control cannot.
        internal static readonly SolidColorBrush PressHighlight =
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
        /// The key of the guidance shown for a mode: a preset's own, or — for a custom mode — the
        /// one that says which preset it is made from. Custom modes share three sentences between
        /// them, because what the plate says about one is its preset's guidance; its name is on the
        /// button above.
        /// </summary>
        private static string HintKey(ProfileChoice choice) =>
            choice.IsPreset ? choice.Id
            : choice.Modified > 0 ? "custom:" + choice.Id
            : "custom:" + choice.BasedOn;

        private readonly IReadOnlyList<ProfileChoice> _choices;
        private readonly TranslateTransform _directionPillOffset = new();
        private readonly Border _menuButtonHighlight;
        private readonly TextBlock _menuName;
        private readonly RotateTransform _menuChevronRotation = new();
        private readonly Border[] _directionSegments;
        private readonly Border[] _directionHighlights;
        private readonly System.Windows.Shapes.Path[] _directionGlyphs;
        private readonly TextBlock[] _directionLabels;
        private readonly Dictionary<string, TextBlock> _hints = [];
        private readonly Grid _hintHost;
        private readonly Border _hintPlate;
        private readonly Border _guidanceToggle;
        private readonly Border _guidanceToggleHighlight;
        private readonly RotateTransform _guidanceChevronRotation = new();
        private readonly double _directionSegmentWidth;
        private readonly double _expandedWidth;
        private readonly double _expandedHeight;
        private readonly double _collapsedWidth;
        private readonly double _collapsedHeight;
        private readonly double _expandedHintHeight;

        // What the restore button adds to the trays' width while it is showing: it is measured out
        // with the rest, collapsed, so the widths above do not include it.
        private readonly double _restoreWidth;
        private bool _restoreShown;

        // Which segment the pointer went down on, or -1. The click is committed on release and only
        // if the pointer is still over that segment, so a press the user thought better of can be
        // taken back by sliding off it — the same forgiveness every other button on the desktop has.
        // The mode button is held to the same rule.
        private bool _menuButtonPressed;
        private int _pressedDirection = -1;
        private bool _guidanceTogglePressed;
        private bool _guidanceExpanded;
        private int _guidanceTransition;

        public ModeSegments(
            string profileId,
            IReadOnlyList<ProfileChoice> choices,
            RealtimeTextOrientation orientation,
            bool guidanceExpanded,
            double height,
            double directionSegmentWidth,
            double inset,
            double hintWidth,
            double gap,
            double uiScale)
        {
            _choices = choices;
            ProfileId = choices.Any(choice => choice.Id == profileId) ? profileId : Models.RealtimePresets.StandardId;
            TextOrientation = orientation;
            _guidanceExpanded = guidanceExpanded;

            Orientation = System.Windows.Controls.Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Left;

            // Sizes here are base units times a monitor scale, so most of them land on fractions of
            // a pixel. Rounded, the plate edges and the text inside them start on whole pixels.
            UseLayoutRounding = true;

            var radius = height / 2;
            var pillHeight = height - inset * 2;

            // The mode button: the current answer as the blue pill the segments used to slide, its
            // name and a chevron on it — see the class remarks.
            _menuName = BuildLabel("", uiScale);
            _menuName.HorizontalAlignment = HorizontalAlignment.Left;
            _menuName.TextTrimming = TextTrimming.CharacterEllipsis;
            _menuName.Foreground = RemoveForeground;
            _menuName.Margin = new Thickness(BaseModeLabelPadding * uiScale, 0, 0, 0);

            var menuChevron = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 1,2 L 6,7 L 11,2"),
                Width = 12 * uiScale,
                Height = 8 * uiScale,
                Stretch = Stretch.Fill,
                Stroke = RemoveForeground,
                StrokeThickness = 1.5 * uiScale,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8 * uiScale, 0, 12 * uiScale, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _menuChevronRotation,
                IsHitTestVisible = false,
            };

            var menuContent = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(menuChevron, Dock.Right);
            menuContent.Children.Add(menuChevron);
            menuContent.Children.Add(_menuName);

            _menuButtonHighlight = new Border
            {
                Background = PressHighlight,
                CornerRadius = new CornerRadius(pillHeight / 2),
                Opacity = 0,
                IsHitTestVisible = false,
            };

            var pillContent = new Grid();
            pillContent.Children.Add(_menuButtonHighlight);
            pillContent.Children.Add(menuContent);

            var pill = new Border
            {
                Height = pillHeight,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(inset, 0, inset, 0),
                CornerRadius = new CornerRadius(pillHeight / 2),
                Background = FrameStroke,
                Child = pillContent,
            };

            MenuButtonWidth = ButtonWidth(choices, menuChevron.Width + menuChevron.Margin.Left + menuChevron.Margin.Right + inset * 2, uiScale);
            var track = Plate(MenuButtonWidth, new CornerRadius(radius), pill, uiScale);
            track.HorizontalAlignment = HorizontalAlignment.Left;
            track.Height = height;
            track.Cursor = Cursors.Hand;
            System.Windows.Automation.AutomationProperties.SetName(
                track, LocalizationService.Get("S.Realtime.ProfileMenu"));
            MenuButton = track;

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

            // Last in the row and the size of the toggle beside it, in the block's blue rather than
            // the trays' dark: it acts on the block, and it is the one tray that is not always there.
            Restore = new Button
            {
                Width = height,
                Height = height,
                Margin = new Thickness(gap, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("S.Realtime.CompareShowBlock"),
                Template = BuildRestoreTemplate(height, uiScale),
                Visibility = Visibility.Collapsed,
            };
            header.Children.Add(Restore);
            _restoreWidth = height + gap;

            // One sentence per preset, and — once there are custom modes — one per preset a custom
            // mode can be made from: the 自訂 tag, which preset, and that preset's sentence.
            foreach (var choice in choices.Where(choice => choice.IsPreset))
                _hints[HintKey(choice)] = BuildHint(RealtimeProfileEditing.PresetGuidance(choice.Id), uiScale);
            // A custom mode that changed values has one of its own: it says how many.
            if (choices.Any(choice => !choice.IsPreset))
                foreach (var preset in choices.Where(choice => choice.IsPreset))
                    _hints[HintKey(preset with { IsPreset = false })] = BuildCustomHint(preset with { IsPreset = false }, uiScale);
            foreach (var custom in choices.Where(choice => !choice.IsPreset && choice.Modified > 0))
                _hints[HintKey(custom)] = BuildCustomHint(custom, uiScale);

            // Every sentence is laid out at once and only one is opaque, so the plate is as tall as
            // the longest of them from the start and choosing a mode cannot change its size.
            var hintContent = new Grid
            {
                Margin = new Thickness(14 * uiScale, 8 * uiScale, 14 * uiScale, 8 * uiScale),
            };
            foreach (var hint in _hints.Values) hintContent.Children.Add(hint);

            _hintPlate = Plate(hintWidth, new CornerRadius(8 * uiScale), hintContent, uiScale);
            _hintPlate.Margin = new Thickness(0, gap, 0, 0);

            // Reads, never clicked. Left hit-testable it would swallow the drag that starts a new
            // block on the picture behind it, for no gain.
            _hintPlate.IsHitTestVisible = false;

            _hintHost = new Grid { ClipToBounds = true };
            _hintHost.Children.Add(_hintPlate);

            Children.Add(header);
            Children.Add(_hintHost);

            track.MouseLeftButtonDown += (_, e) =>
            {
                // Must not fall through to the canvas underneath, which would take this as the
                // start of a new block being drawn.
                e.Handled = true;
                _menuButtonPressed = true;
                track.CaptureMouse();
                SetMenuButtonPressed(true);
            };
            track.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var commit = _menuButtonPressed && track.IsMouseOver;
                _menuButtonPressed = false;
                track.ReleaseMouseCapture();
                SetMenuButtonPressed(false);
                if (commit) MenuRequested?.Invoke(this, EventArgs.Empty);
            };

            // Dragged off and back on again while held: the press follows the pointer, so the
            // control keeps saying what releasing right now would do.
            track.MouseEnter += (_, _) => { if (_menuButtonPressed) SetMenuButtonPressed(true); };
            track.MouseLeave += (_, _) => { if (_menuButtonPressed) SetMenuButtonPressed(false); };

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
            /// <summary>The mode the block reads with.</summary>
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

        /// <summary>
        /// Raised when the mode button is clicked: the list belongs to the layer, which opens it
        /// over everything else — see <see cref="ModeMenu"/>.
        /// </summary>
        public event EventHandler? MenuRequested;

        /// <summary>The mode this block is on, by id — one of the choices it was built with.</summary>
        public string ProfileId { get; private set; }

        /// <summary>The mode button, which the open list is placed against.</summary>
        public FrameworkElement MenuButton { get; }

        /// <summary>How wide the mode button is: the same whichever mode is chosen.</summary>
        public double MenuButtonWidth { get; }

        /// <summary>Whether the guidance is unfolded — carried over when the trays are rebuilt.</summary>
        public bool GuidanceExpanded => _guidanceExpanded;

        /// <summary>Puts the block on <paramref name="profileId"/>, as a pick from the open list.</summary>
        /// <remarks>Picking the mode it is already on changes nothing and says nothing.</remarks>
        public void Pick(string profileId)
        {
            if (profileId == ProfileId || _choices.All(choice => choice.Id != profileId)) return;

            ProfileId = profileId;
            ApplySelection(animate: true);
            SelectionChanged?.Invoke(this, BlockTray.Mode);
        }

        /// <summary>Turns the button's chevron to say whether its list is open.</summary>
        public void SetMenuOpen(bool open)
        {
            var target = open ? 180.0 : 0.0;
            if (!SystemParameters.ClientAreaAnimation)
            {
                _menuChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                _menuChevronRotation.Angle = target;
                return;
            }

            _menuChevronRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                Transition(_menuChevronRotation.Angle, target, GuidanceDuration));
        }

        /// <summary>
        /// The width the mode button is built at: room for the widest name it may show, held
        /// between <see cref="BaseModeButtonMinWidth"/> and <see cref="BaseModeButtonMaxWidth"/> —
        /// except that a preset's name always fits whole.
        /// </summary>
        private static double ButtonWidth(IReadOnlyList<ProfileChoice> choices, double chrome, double uiScale)
        {
            double Fit(string text)
            {
                var label = BuildLabel(text, uiScale);
                label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return label.DesiredSize.Width + BaseModeLabelPadding * uiScale + chrome;
            }

            double widest = choices.Max(choice => Fit(choice.Name));
            double widestPreset = choices.Where(choice => choice.IsPreset).Select(choice => Fit(choice.Name)).DefaultIfEmpty(0).Max();
            double width = Math.Clamp(widest, BaseModeButtonMinWidth * uiScale, BaseModeButtonMaxWidth * uiScale);
            return Math.Ceiling(Math.Max(width, widestPreset));
        }

        /// <summary>
        /// Which way this block's text runs. Not called Orientation: this is a StackPanel, and that
        /// name is taken by the one that says which way its own children stack.
        /// </summary>
        public RealtimeTextOrientation TextOrientation { get; private set; }

        /// <summary>Current size, so the caller can keep the visible surface on screen.</summary>
        public double TotalWidth
        {
            get
            {
                double header = _collapsedWidth + (_restoreShown ? _restoreWidth : 0);
                return _guidanceExpanded ? Math.Max(_expandedWidth, header) : header;
            }
        }

        /// <summary>
        /// Brings the block's turned-off 對照顯示 copy back. Shown by <see cref="SetRestoreShown"/>
        /// only while there is a copy to bring back.
        /// </summary>
        public Button Restore { get; }

        public void SetRestoreShown(bool shown)
        {
            _restoreShown = shown;
            Restore.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        // 對照顯示's glyph on the block's blue, a little translucent like the trays beside it.
        private static ControlTemplate BuildRestoreTemplate(double size, double uiScale)
        {
            var glyph = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            glyph.SetValue(System.Windows.Shapes.Path.DataProperty, CompareGlyph);
            glyph.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
            glyph.SetValue(FrameworkElement.WidthProperty, size * 0.55);
            glyph.SetValue(FrameworkElement.HeightProperty, size * 0.55);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeProperty, RemoveForeground);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.2 * uiScale);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            glyph.SetValue(System.Windows.Shapes.Shape.StrokeLineJoinProperty, PenLineJoin.Round);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.BackgroundProperty, RestoreFill);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
            chip.AppendChild(glyph);

            return new ControlTemplate(typeof(Button)) { VisualTree = chip };
        }

        public double TotalHeight => _guidanceExpanded ? _expandedHeight : _collapsedHeight;

        /// <summary>
        /// Shows the chosen mode: its name on the button — cut short with the whole of it on the
        /// tooltip, or a preset's summary there — and its sentence on the plate.
        /// </summary>
        private void ApplySelection(bool animate)
        {
            var choice = _choices.FirstOrDefault(choice => choice.Id == ProfileId) ?? _choices[0];

            _menuName.Text = choice.Name;
            MenuButton.ToolTip = choice.IsPreset ? SummaryTip(RealtimeProfileEditing.PresetSummary(choice.Id)) : choice.Name;
            System.Windows.Automation.AutomationProperties.SetItemStatus(MenuButton, choice.Name);

            var shown = HintKey(choice);
            foreach (var (key, hint) in _hints)
                Fade(hint, key == shown ? 1.0 : 0.0, HintFadeDuration, animate);
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

        private void SetMenuButtonPressed(bool pressed) =>
            Fade(_menuButtonHighlight, pressed ? 1.0 : 0.0, PressDuration, animate: true);

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
        internal static Border Plate(double width, CornerRadius corner, UIElement content, double uiScale) => new()
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

        internal static DoubleAnimation Transition(double from, double to, Duration duration) => new(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        /// <summary>Cross-fades a layer, from its current opacity — see <see cref="Move"/>.</summary>
        internal static void Fade(UIElement element, double to, Duration duration, bool animate)
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

        internal static TextBlock BuildLabel(string text, double uiScale) =>
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

        /// <summary>
        /// The guidance for a custom mode — <paramref name="custom"/>, or any made from a preset and
        /// left as it is: a 自訂 tag, the preset it follows and how many values it changed, then
        /// that preset's own sentence, as one paragraph.
        /// </summary>
        /// <remarks>
        /// The preset's sentence rather than anything about the custom mode itself, because what the
        /// mode was made for is the preset's case; what the user changed in it is theirs to know, and
        /// its name is already on the button. The tag sits on the line like a word, centred on it, in
        /// the blue of the button at a third of its strength — a label, not a second button.
        /// </remarks>
        private static TextBlock BuildCustomHint(ProfileChoice custom, double uiScale)
        {
            var hint = BuildHint("", uiScale);

            var tagText = Sharpen(new TextBlock
            {
                Text = LocalizationService.Get("S.Realtime.ProfileCustomTag"),
                FontSize = 11.5 * uiScale,
                Foreground = RemoveForeground,
            });
            var tag = new Border
            {
                Background = CustomTagFill,
                CornerRadius = new CornerRadius(4 * uiScale),
                Padding = new Thickness(6 * uiScale, 0, 6 * uiScale, 1 * uiScale),
                Margin = new Thickness(0, 0, 6 * uiScale, 0),
                Child = tagText,
            };

            var basedOn = RealtimeProfileEditing.BasedOnSentence(custom);
            hint.Inlines.Add(new System.Windows.Documents.InlineUIContainer(tag) { BaselineAlignment = BaselineAlignment.Center });
            hint.Inlines.Add(new System.Windows.Documents.Run(basedOn));
            // A space between the two sentences only where the language spaces its sentences: a
            // full stop of its own (。) closes a CJK sentence and the next one follows straight on.
            if (basedOn.Length > 0 && basedOn[^1] < 0x2000)
                hint.Inlines.Add(new System.Windows.Documents.Run(" "));
            hint.Inlines.Add(new System.Windows.Documents.Run(RealtimeProfileEditing.PresetGuidance(custom.BasedOn)));
            return hint;
        }

        // The block's blue at about a third of its strength, under the 自訂 tag.
        private static readonly SolidColorBrush CustomTagFill = Freeze(Color.FromArgb(0x59, 0x1E, 0x90, 0xD5));

        /// <summary>A preset's summary as a tooltip, a little wider than the app's own.</summary>
        /// <remarks>
        /// At the app's 340 the Chinese summary of 標準 (25 characters at 12) ran a few pixels over
        /// and left its last character alone on a second line; at 400 it is one line, and the
        /// longer languages still wrap to two.
        /// </remarks>
        internal static System.Windows.Controls.ToolTip SummaryTip(string summary) => new() { Content = summary, MaxWidth = 400 };

        // Display formatting rounds glyph widths and positions onto whole pixels, which at these
        // sizes is the difference between a stem one pixel wide and a stem smeared across two. WPF's
        // default (Ideal) keeps the typographic metrics instead, which is right for large text and
        // wrong for small interface text — and this is small interface text over moving picture.
        internal static TextBlock Sharpen(TextBlock text)
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

    /// <summary>
    /// The open list of modes under a block's mode button: the presets, the custom modes, and
    /// 管理模式…, on the trays' dark plate.
    /// </summary>
    /// <remarks>
    /// <para>Built of the trays' own parts — the plate, the blue pill for the chosen answer, the
    /// press light for the pointer — so it reads as the button opened out rather than as a menu from
    /// somewhere else. No tick: the pill is what says "chosen" on every control of this layer.</para>
    ///
    /// <para>Fades in and out and never slides: everything on it is text, and WPF leaves text it
    /// sees moving soft for about a second afterwards (see ShellWindow.AnimateContentIn).</para>
    ///
    /// <para>A choice is committed on release, on whichever row the pointer is over then — pressing
    /// one row and letting go on another picks the other, as menus do; letting go off every row
    /// picks nothing and leaves the list open.</para>
    ///
    /// <para>Thirteen rows at most fit any ordinary screen. Where they do not, the presets and
    /// 管理模式… stay put and only the custom modes scroll, a row per wheel notch, faded at whichever
    /// edge has more beyond it.</para>
    /// </remarks>
    private sealed class ModeMenu : Border
    {
        private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(120));
        private static readonly Duration CloseDuration = new(TimeSpan.FromMilliseconds(80));
        private static readonly SolidColorBrush MenuFill = Freeze(Color.FromArgb(0xF5, 0x1C, 0x1C, 0x1E));

        private readonly double _rowHeight;
        private readonly double _uiScale;
        private readonly Border? _customViewport;
        private readonly TranslateTransform _customScroll = new();
        private readonly double _customHeight;
        private double _scrollRange;

        public ModeMenu(
            IReadOnlyList<ProfileChoice> choices,
            string selectedId,
            double minWidth,
            double maxWidth,
            double rowHeight,
            double uiScale)
        {
            _rowHeight = rowHeight;
            _uiScale = uiScale;
            UseLayoutRounding = true;
            Opacity = 0;

            var labels = new List<TextBlock>();
            var rows = new StackPanel { Margin = new Thickness(4 * uiScale) };

            foreach (var choice in choices.Where(choice => choice.IsPreset))
                rows.Children.Add(Row(choice.Name, ModeSegments.SummaryTip(RealtimeProfileEditing.PresetSummary(choice.Id)),
                    choice.Id == selectedId, () => Picked?.Invoke(this, choice.Id), labels));

            var custom = choices.Where(choice => !choice.IsPreset).ToList();
            if (custom.Count > 0)
            {
                rows.Children.Add(Separator());
                var customRows = new StackPanel { RenderTransform = _customScroll };
                foreach (var choice in custom)
                    customRows.Children.Add(Row(choice.Name, choice.Name,
                        choice.Id == selectedId, () => Picked?.Invoke(this, choice.Id), labels));
                _customHeight = custom.Count * rowHeight;
                _customViewport = new Border { ClipToBounds = true, Child = customRows };
                rows.Children.Add(_customViewport);
            }

            rows.Children.Add(Separator());
            rows.Children.Add(Row(LocalizationService.Get("S.Realtime.ProfileManage"),
                LocalizationService.Get("S.Realtime.ProfileManageTip"),
                selected: false, () => ManageRequested?.Invoke(this, EventArgs.Empty), labels));

            // As wide as the button at least, as its longest name at most up to the cap; past that
            // a custom name is cut and its tooltip carries it whole.
            double widest = labels.Max(label =>
            {
                label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return label.DesiredSize.Width;
            });
            double width = Math.Ceiling(Math.Max(minWidth, Math.Min(maxWidth, widest + 24 * uiScale + 8 * uiScale)));

            Width = width;
            var plate = ModeSegments.Plate(width, new CornerRadius(8 * uiScale), rows, uiScale);
            // Nearly opaque, unlike the trays: a list is read row by row over whatever the block is
            // framing, and at the trays' strength the text underneath showed through between the rows.
            ((Border)plate.Child).Background = MenuFill;
            Child = plate;

            // Handled here so a press anywhere on the list — a row, a separator, the padding — never
            // reaches the canvas, which would take it for the start of a new block.
            MouseLeftButtonDown += (_, e) => e.Handled = true;
            MouseLeftButtonUp += (_, e) => e.Handled = true;
            MouseDown += (_, e) => e.Handled = true;
            MouseWheel += (_, e) =>
            {
                e.Handled = true;
                Scroll(e.Delta > 0 ? 1 : -1);
            };
        }

        /// <summary>Raised with the id of the mode picked.</summary>
        public event EventHandler<string>? Picked;

        /// <summary>Raised when 管理模式… is picked.</summary>
        public event EventHandler? ManageRequested;

        /// <summary>
        /// Shortens the list to <paramref name="available"/> by letting the custom modes scroll,
        /// keeping at least one of them in view.
        /// </summary>
        /// <returns>The list's height now.</returns>
        public double FitHeight(double available)
        {
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = DesiredSize.Height;
            if (_customViewport is null || natural <= available) return natural;

            double fixedPart = natural - _customHeight;
            double viewport = Math.Max(_rowHeight, available - fixedPart);
            _customViewport.Height = viewport;
            _scrollRange = Math.Max(0, _customHeight - viewport);
            UpdateScrollMask();
            return fixedPart + viewport;
        }

        public void FadeIn()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                Opacity = 1;
                return;
            }
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, OpenDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        /// <summary>Fades the list out and then hands it to <paramref name="removed"/> to take away.</summary>
        public void FadeOut(bool animate, Action removed)
        {
            IsHitTestVisible = false;
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                removed();
                return;
            }

            var fade = new DoubleAnimation(Opacity, 0, CloseDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) => removed();
            BeginAnimation(OpacityProperty, fade);
        }

        // A row per wheel notch, set rather than animated: a scroll is the user moving the list,
        // not the list moving on its own.
        private void Scroll(int rows)
        {
            if (_scrollRange <= 0) return;
            _customScroll.Y = Math.Clamp(_customScroll.Y + rows * _rowHeight, -_scrollRange, 0);
            UpdateScrollMask();
        }

        // Fades the edge of the custom modes that has more beyond it, so a list cut short says so.
        private void UpdateScrollMask()
        {
            if (_customViewport is null || _scrollRange <= 0)
            {
                if (_customViewport is not null) _customViewport.OpacityMask = null;
                return;
            }

            double height = _customViewport.Height;
            double fade = Math.Min(0.5, 12 * _uiScale / height);
            var top = _customScroll.Y < 0 ? Colors.Transparent : Colors.Black;
            var bottom = _customScroll.Y > -_scrollRange ? Colors.Transparent : Colors.Black;
            _customViewport.OpacityMask = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(top, 0),
                    new(Colors.Black, fade),
                    new(Colors.Black, 1 - fade),
                    new(bottom, 1),
                },
                new Point(0, 0),
                new Point(0, 1));
        }

        private Border Separator() => new()
        {
            Height = Math.Max(1, _uiScale),
            Background = ModeTrackEdge,
            Margin = new Thickness(0, 4 * _uiScale, 0, 4 * _uiScale),
            IsHitTestVisible = false,
        };

        /// <summary>
        /// One row: the chosen one on the blue pill in white, the rest dimmer until the pointer is
        /// over them. Committed on release over it — see the class remarks.
        /// </summary>
        private Border Row(string text, object? tip, bool selected, Action picked, List<TextBlock> labels)
        {
            var label = ModeSegments.Sharpen(new TextBlock
            {
                Text = text,
                FontSize = BaseModeFontSize * _uiScale,
                Foreground = selected ? RemoveForeground : ModeIdleForeground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12 * _uiScale, 0, 12 * _uiScale, 0),
            });
            labels.Add(label);

            var light = new Border
            {
                Background = ModeSegments.PressHighlight,
                CornerRadius = new CornerRadius(6 * _uiScale),
                Opacity = 0,
                IsHitTestVisible = false,
            };

            var content = new Grid();
            content.Children.Add(light);
            content.Children.Add(label);

            var row = new Border
            {
                Height = _rowHeight,
                CornerRadius = new CornerRadius(6 * _uiScale),
                Background = selected ? FrameStroke : System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = content,
            };

            row.MouseEnter += (_, _) =>
            {
                if (selected) return;
                label.Foreground = RemoveForeground;
                ModeSegments.Fade(light, 1.0, ModeSegments.PressDuration, animate: true);
            };
            row.MouseLeave += (_, _) =>
            {
                if (selected) return;
                label.Foreground = ModeIdleForeground;
                ModeSegments.Fade(light, 0.0, ModeSegments.PressDuration, animate: true);
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                picked();
            };
            return row;
        }
    }
}
