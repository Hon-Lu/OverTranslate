using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Providers;

namespace OverTranslate.Views.Capture;

public partial class ToolbarWindow : Window
{
    private const string SpeakGlyph = Controls.TtsGlyphs.Speak;
    private const string StopGlyph = Controls.TtsGlyphs.Stop;

    public event EventHandler<TranslateRequest>? TranslateRequested;
    public event EventHandler? OpenWindowRequested;
    public event EventHandler<CopyTextRequest>? CopyTextRequested;
    public event EventHandler? CopyScreenshotRequested;
    public event EventHandler? CloseAllRequested;
    public event EventHandler<bool>? BubblesVisibilityChanged;

    /// <summary>The speak button was pressed: start reading, or stop if already reading.</summary>
    public event EventHandler? SpeakToggleRequested;

    /// <summary>標記 was switched on or off.</summary>
    public event EventHandler<bool>? AnnotateModeChanged;

    /// <summary>
    /// Raised when the button that stops playback is about to stop being usable, so whoever owns the
    /// voice can stop it. Without this, switching the source language to 自動 mid-sentence would
    /// leave the text playing with no way to stop it — the stop button is the one being disabled.
    /// </summary>
    public event EventHandler? SpeakStopRequested;

    // Not readonly: the selection can still be moved and resized until translation starts, and this
    // toolbar is anchored to it — see FollowSelection.
    private double _selPhysLeft;
    private double _selPhysTop;
    private double _selPhysWidth;
    private double _selPhysHeight;

    private bool _isBusy        = false;
    private bool _toggleEnabled = false;
    private bool _bubblesVisible = true;
    private bool _hasTranslated;
    private bool _initializingDirection = true;
    private bool _initializingLayoutMode = true;
    private bool _syncingDebug = true;
    private bool _syncingCapture;
    private bool _ignoreMoreClick;

    // Whether there is recognised text to read, and whether it is being read right now. The voice
    // itself lives with the capture session, not here: this window only shows its state.
    private bool _hasSpeakableText;
    private bool _isSpeaking;

    public string CurrentSourceLang => LanguageData.GetValidOcrSourceCode(SrcLangBox.SelectedValue as string);
    public string CurrentTargetLang => LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);

    /// <summary>
    /// Whether the user has said the text in the selection is written downwards in columns rather
    /// than across in lines.
    /// </summary>
    /// <remarks>
    /// Restored from <see cref="AppSettings.Capture"/> when the toolbar opens and saved on each
    /// explicit switch, so consecutive pages from the same manga or game keep the chosen direction.
    /// </remarks>
    public bool IsVerticalText => VerticalSeg.IsChecked == true;

    /// <summary>
    /// The effective capture mode. The single-mode release always uses General.
    /// </summary>
    /// <remarks>
    /// Keep the selector and persisted preference for future use, but do not let either override
    /// the application's current single-mode policy.
    /// </remarks>
    public CaptureLayoutMode CurrentLayoutMode => CaptureLayoutPolicy.ForApplication(InterfaceModeSeg.IsChecked == true
        ? CaptureLayoutMode.Interface
        : CaptureLayoutMode.General);

    public ToolbarWindow(
        double selPhysLeft, double selPhysTop,
        double selPhysWidth, double selPhysHeight,
        string sourceLang, string targetLang)
    {
        _selPhysLeft   = selPhysLeft;
        _selPhysTop    = selPhysTop;
        _selPhysWidth  = selPhysWidth;
        _selPhysHeight = selPhysHeight;

        InitializeComponent();
        SyncDebugSwitches(this, EventArgs.Empty);
        SyncCaptureSwitches(this, EventArgs.Empty);
        SettingsService.Instance.OcrDebugChanged += SyncDebugSwitches;
        SettingsService.Instance.CaptureOptionsChanged += SyncCaptureSwitches;
        Closed += (_, _) =>
        {
            MorePopup.IsOpen = false;
            SettingsService.Instance.OcrDebugChanged -= SyncDebugSwitches;
            SettingsService.Instance.CaptureOptionsChanged -= SyncCaptureSwitches;
        };
        LocationChanged += (_, _) => MorePopup.IsOpen = false;
        MoreBtn.MouseLeave += (_, _) => _ignoreMoreClick = false;

        bool verticalText = SettingsService.Instance.Current.Capture.VerticalText;
        HorizontalSeg.IsChecked = !verticalText;
        VerticalSeg.IsChecked = verticalText;
        _initializingDirection = false;

        // A mode this build cannot read — a file from a later release, or one still naming a v1
        // mode — has already become General by the time it gets here: the settings reader keeps the
        // property's default when a value will not deserialize, and General is that default.
        // Nothing to guard against a second time; see SettingsService.Apply.
        LayoutModeSelector.Visibility = CaptureLayoutPolicy.IsModeSelectionAvailable
            ? Visibility.Visible : Visibility.Collapsed;
        bool interfaceMode = CaptureLayoutPolicy.ForApplication(
            SettingsService.Instance.Current.Capture.LayoutMode) == CaptureLayoutMode.Interface;
        InterfaceModeSeg.IsChecked = interfaceMode;
        GeneralModeSeg.IsChecked = !interfaceMode;
        _initializingLayoutMode = false;

        InitializeSelectors(sourceLang, targetLang);
        SizeSelectorsToClosedLabels();

        // Attach after initial values are set so initialization doesn't trigger a save
        SrcLangBox.SelectionChanged  += SrcLangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += TgtLangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;

        // The shared columns do not have a width until layout. A remembered vertical choice already
        // checks the right half above; this places the thumb under it on the first rendered frame.
        Loaded += (_, _) =>
        {
            RenderDirectionThumb(animate: false);
            RenderLayoutModeThumb(animate: false);
        };

        RenderSpeakButton();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PositionNearSelection();

        // Re-applied once the window has landed: crossing to a monitor at another scale makes WPF
        // resize the window and Windows offer a replacement position, either of which moves the
        // edge just aligned to the selection. Same inputs, so it is a no-op on a uniform desktop.
        Dispatcher.BeginInvoke(new Action(PositionNearSelection), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-anchors the toolbar to the selection after the user has moved or resized it.
    /// </summary>
    /// <remarks>
    /// The same placement the toolbar opened with, run again: it stays under the box where there is
    /// room and flips above it where there is not, so dragging a selection down to the bottom of the
    /// screen moves the toolbar over the top of it rather than off the desktop.
    /// </remarks>
    public void FollowSelection(Rect physicalSelection)
    {
        _selPhysLeft   = physicalSelection.Left;
        _selPhysTop    = physicalSelection.Top;
        _selPhysWidth  = physicalSelection.Width;
        _selPhysHeight = physicalSelection.Height;

        // Nothing to place onto until the window has a handle; OnSourceInitialized does it then.
        if (IsLoaded) PositionNearSelection();
    }

    private void PositionNearSelection()
    {
        UpdateLayout();

        // All physical pixels, scaled by the monitor the selection is on. Deriving the scale from
        // this window instead reads whichever monitor WPF created it on: a toolbar measured at 96
        // DPI and then placed onto a 144 DPI monitor lands a factor of 1.5 from the selection.
        int centreX = (int)(_selPhysLeft + _selPhysWidth  / 2);
        int centreY = (int)(_selPhysTop  + _selPhysHeight / 2);
        double scale = ScreenGeometry.ScaleAt(centreX, centreY);

        // WPF lays out in DIP regardless of DPI, so the DIP size scales straight to target pixels.
        double tbW = (ActualWidth  > 0 ? ActualWidth  : 1090) * scale;
        double tbH = (ActualHeight > 0 ? ActualHeight : 88)   * scale;

        var wa = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point(centreX, centreY)).WorkingArea;
        double margin = 4 * scale;
        double gap    = 6 * scale;

        // Math.Clamp throws when the toolbar is wider than the monitor it must fit on.
        double minLeft = wa.Left + margin;
        double maxLeft = Math.Max(minLeft, wa.Right - tbW - margin);
        double left = Math.Clamp(_selPhysLeft + (_selPhysWidth - tbW) / 2, minLeft, maxLeft);

        double yBelow = _selPhysTop + _selPhysHeight + gap;
        double yAbove = _selPhysTop - tbH - gap;

        double top;
        if (yBelow + tbH <= wa.Bottom)
            top = yBelow;
        else if (yAbove >= wa.Top)
            top = yAbove;
        else
            top = _selPhysTop + _selPhysHeight - tbH - 2 * scale;

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(left), (int)Math.Round(top));
    }

    private void SrcLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();

        // The source language is what decides whether there is a voice to read with — see
        // RenderSpeakButton.
        RenderSpeakButton();
    }

    private void TgtLangBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentLanguageSelection();
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not TranslationProvider provider) return;
        SaveProviderSelection(provider);
    }

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var srcVal = SrcLangBox.SelectedValue as string;
        var tgtVal = TgtLangBox.SelectedValue as string;

        // Target → Source: use explicit language mapping (e.g. ZH-HANT stays traditional)
        if (tgtVal != null)
        {
            var sourceCode = LanguageData.MapTargetToSourceCode(tgtVal);
            SrcLangBox.SelectedValue = sourceCode;
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Source → Target: use explicit language mapping
        if (srcVal != null)
        {
            var targetCode = LanguageData.MapSourceToTargetCode(srcVal);
            TgtLangBox.SelectedValue = targetCode;
        }
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Commits the choice on the press rather than on the release, so the pill starts moving under
    /// the finger instead of after it.
    /// </summary>
    /// <remarks>
    /// A button that waits for mouse-up is correct for something that acts — you can still slide off
    /// it and change your mind — but this one only moves a marker, and there is nothing to change
    /// your mind about. The release still runs the ordinary click, which finds the option already
    /// chosen and does nothing.
    /// </remarks>
    private void DirectionSegment_PreviewMouseLeftButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton segment) segment.IsChecked = true;
    }

    /// <summary>
    /// Slides the pill onto the half just chosen.
    /// </summary>
    /// <remarks>
    /// <para>The animation carries only a target, no starting value, so it always sets off from
    /// wherever the pill is at that instant. Somebody who changes their mind halfway across gets one
    /// continuous movement back rather than a jump to the far side and a fresh start.</para>
    ///
    /// <para>Eased out and not bounced: nothing was thrown here, it was clicked, and an overshoot on
    /// a marker that merely answers a click reads as the interface being pleased with itself.</para>
    ///
    /// <para>The travel is one column's width, which is the pill's own width because the two columns
    /// share a size — see the tray's ColumnDefinitions.</para>
    /// </remarks>
    private void DirectionSegment_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once while the XAML is still being parsed, for the half that opens checked — at
        // which point the other half does not exist yet and neither does the pill. The constructor
        // applies the stored choice after parsing, and Loaded places the thumb after layout.
        if (DirectionThumb is null || DirectionThumbShift is null || VerticalSeg is null) return;

        if (!_initializingDirection)
            SaveTextDirectionSelection();

        RenderDirectionThumb(animate: IsLoaded);
    }

    /// <inheritdoc cref="DirectionSegment_PreviewMouseLeftButtonDown"/>
    private void LayoutModeSegment_PreviewMouseLeftButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton segment) segment.IsChecked = true;
    }

    /// <inheritdoc cref="DirectionSegment_Checked"/>
    private void LayoutModeSegment_Checked(object sender, RoutedEventArgs e)
    {
        // InterfaceModeSeg, because that is the half CurrentLayoutMode reads. This fires once while
        // the XAML is still being parsed, for whichever half opens checked, and at that moment the
        // other half does not exist yet — so the guard has to name the element the property below
        // will dereference, not merely some sibling. Naming the wrong one was a null reference in
        // the constructor the moment 一般 moved to the left and became the half declared first.
        if (LayoutModeThumb is null || LayoutModeThumbShift is null || InterfaceModeSeg is null) return;

        if (!_initializingLayoutMode)
            SaveLayoutModeSelection();

        RenderLayoutModeThumb(animate: IsLoaded);
    }

    private void RenderLayoutModeThumb(bool animate)
    {
        double target = CurrentLayoutMode == CaptureLayoutMode.Interface
            ? LayoutModeThumb.ActualWidth
            : 0;

        if (!animate || LayoutModeThumb.ActualWidth <= 0)
        {
            LayoutModeThumbShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            LayoutModeThumbShift.X = target;
            return;
        }

        LayoutModeThumbShift.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void RenderDirectionThumb(bool animate)
    {
        double target = IsVerticalText ? DirectionThumb.ActualWidth : 0;

        // Before the tray has been laid out there is no distance to travel and nothing to see; the
        // Loaded callback runs this again once the shared columns have their final width.
        if (!animate || DirectionThumb.ActualWidth <= 0)
        {
            DirectionThumbShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            DirectionThumbShift.X = target;
            return;
        }

        DirectionThumbShift.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void TranslateBtn_Click(object sender, RoutedEventArgs e)
        => RequestTranslate();

    /// <summary>
    /// Fires the same request the 翻譯 button does, so auto-translate goes through the identical
    /// path (current selector values, busy state, re-translate labelling). Ignored while a batch
    /// is already running.
    /// </summary>
    public void RequestTranslate()
    {
        if (_isBusy) return;
        TranslateRequested?.Invoke(this, new TranslateRequest(
            CurrentSourceLang, CurrentTargetLang, IsVerticalText, CurrentLayoutMode));
    }

    private void OpenWindowBtn_Click(object sender, RoutedEventArgs e)
        => OpenWindowRequested?.Invoke(this, EventArgs.Empty);

    private void TtsBtn_Click(object sender, RoutedEventArgs e)
        => SpeakToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Puts the toggle's icon and label on the same side of what pressing it would do.</summary>
    /// <remarks>
    /// The eye is the action, like the label beside it: plain while the translation is on
    /// screen, because pressing reveals the original; struck through while the original is,
    /// because pressing puts it away again. A fixed eye under a label that changed was the
    /// icon disagreeing with the word next to it every second press.
    /// </remarks>
    private void RenderToggleButton()
    {
        ToggleHideSlash.Visibility = _bubblesVisible ? Visibility.Collapsed : Visibility.Visible;
        ToggleLabel.Text = LocalizationService.Get(
            _bubblesVisible ? "S.Toolbar.ShowSource" : "S.Toolbar.ShowTranslation");
        RenderCopyTextButton();
    }

    private void CopyTextBtn_Click(object sender, RoutedEventArgs e)
        => CopyTextRequested?.Invoke(this, new CopyTextRequest(
            ResolveCopyTextKind(_hasTranslated, _bubblesVisible),
            CurrentSourceLang,
            IsVerticalText,
            CurrentLayoutMode));

    private void CopyShotBtn_Click(object sender, RoutedEventArgs e)
        => CopyScreenshotRequested?.Invoke(this, EventArgs.Empty);

    private void AnnotateBtn_Click(object sender, RoutedEventArgs e)
        => AnnotateModeChanged?.Invoke(this, IsAnnotating);

    /// <summary>Whether 標記 is on, so a drag inside the box draws rather than moves it.</summary>
    public bool IsAnnotating => AnnotateBtn.IsChecked == true;

    /// <summary>
    /// Switches 標記 off from outside — the session ending, or another action taking the box back.
    /// </summary>
    /// <remarks>
    /// Deliberately silent: every caller is already doing the thing the event would have told it to
    /// do, and raising it here would have them undo their own work.
    /// </remarks>
    public void ExitAnnotateMode()
    {
        if (!IsAnnotating) return;
        AnnotateBtn.IsChecked = false;
    }

    /// <summary>
    /// The bar as it appears on screen, in physical pixels, for the 標記 panel to sit against.
    /// </summary>
    /// <remarks>
    /// The visible bar, not the window: the window is larger on every side by the margin the shadow
    /// fades out in, and anything placed against its edge would end up that margin away from the bar
    /// the user is actually looking at. Read from the live visual rather than by subtracting the
    /// numbers in the markup, so it cannot drift when those change.
    /// </remarks>
    public (Rect Visible, double Scale) VisiblePhysicalBounds()
    {
        double scale = ScreenGeometry.ScaleAt(
            (int)(_selPhysLeft + _selPhysWidth / 2), (int)(_selPhysTop + _selPhysHeight / 2));

        var topLeft = BarSurface.TranslatePoint(new System.Windows.Point(0, 0), this);
        var bounds  = ScreenGeometry.PhysicalBounds(this);

        return (new Rect(
                    bounds.Left + topLeft.X * scale,
                    bounds.Top  + topLeft.Y * scale,
                    BarSurface.ActualWidth  * scale,
                    BarSurface.ActualHeight * scale),
                scale);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => CloseAllRequested?.Invoke(this, EventArgs.Empty);

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    public void SetBusy(bool busy)
        => SetBusy(busy, "S.Toolbar.Translating");

    public void SetRecognitionBusy(bool busy)
        => SetBusy(busy, "S.Toolbar.Recognising");

    private void SetBusy(bool busy, string busyLabelKey)
    {
        _isBusy = busy;
        if (busy) HideEngineBadge(); // stale badge shouldn't linger while the next batch runs
        TranslateBtn.IsEnabled = !busy;
        TranslateLabel.Text = LocalizationService.Get(
            busy ? busyLabelKey
                 : _hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate");
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        CopyTextBtn.IsEnabled = !busy;
        OpenWindowBtn.IsEnabled = !_isBusy;
    }

    /// <summary>Whether there is text from a translated selection for the speak button to read.</summary>
    public void SetSpeakableText(bool hasText)
    {
        _hasSpeakableText = hasText;
        RenderSpeakButton();
    }

    /// <summary>Reflects whether the voice is currently reading, so the button offers to stop it.</summary>
    public void SetSpeaking(bool speaking)
    {
        _isSpeaking = speaking;
        RenderSpeakButton();
    }

    /// <summary>
    /// Settles the speak button against what there is to read and what there is to read it with.
    /// </summary>
    /// <remarks>
    /// <para>Switched off while the source language is 自動, the same way the translation page's
    /// 原文 speaker is: there is no such thing as an automatic voice. <see cref="TtsService"/> maps
    /// 自動 onto Chinese, so English or Japanese text would be read aloud in a Chinese voice — which
    /// used to happen silently, leaving the user to work out from the sound that a picker three
    /// controls away was the cause. Recognition can run on 自動 because it is choosing between
    /// three scripts it can see; a voice has nothing to look at.</para>
    ///
    /// <para>Also off until a translation is on screen. Recognition alone is not enough: 複製文字
    /// recognises too and hands the box back afterwards, so the text it produced can already be
    /// describing a region the user has since redrawn.</para>
    ///
    /// <para>Not switched off while a translation is in flight, unlike everything else on this bar:
    /// the text being read is the one already recognised, the new batch does not touch it, and the
    /// button is also the only way to stop playback that is already running.</para>
    /// </remarks>
    private void RenderSpeakButton()
    {
        var automatic = LanguageData.IsAutomaticSource(SrcLangBox.SelectedValue as string);

        // Before the button goes dead: it is the only thing that can stop what it started.
        if (automatic && _isSpeaking) SpeakStopRequested?.Invoke(this, EventArgs.Empty);

        TtsBtn.IsEnabled = _hasSpeakableText && !automatic;

        // The glyph is what pressing it does, the way the realtime bar's pause button works.
        TtsGlyph.Text = _isSpeaking ? StopGlyph : SpeakGlyph;

        // The name a screen reader announces. It used to be the label on the button; with the label
        // gone it has to be said here, or the button reaches assistive technology as an unnamed
        // control with a private-use character where its name should be.
        System.Windows.Automation.AutomationProperties.SetName(
            TtsBtn, LocalizationService.Get(_isSpeaking ? "S.Toolbar.SpeakStopLabel" : "S.Toolbar.Speak"));

        // Read through the service rather than bound in XAML, because which of the three applies is
        // a state and not a constant.
        TtsBtn.ToolTip = LocalizationService.Get(
            automatic ? "S.Toolbar.SpeakAutomatic"
                      : !_hasSpeakableText ? "S.Toolbar.SpeakNoText"
                      : _isSpeaking ? "S.Toolbar.SpeakStop"
                      : "S.Toolbar.SpeakHint");
    }

    public void SetTranslationState(bool hasTranslated)
    {
        _hasTranslated = hasTranslated;
        RenderCopyTextButton();
        if (!_isBusy)
            TranslateLabel.Text = LocalizationService.Get(
                hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate");
    }

    private void RenderCopyTextButton()
    {
        bool copiesTranslation =
            ResolveCopyTextKind(_hasTranslated, _bubblesVisible) == CopyTextKind.Translation;

        CopyTextLabel.Text = LocalizationService.Get(
            copiesTranslation
                ? "S.Toolbar.CopyTranslation"
                : "S.Toolbar.CopyText");
        CopySourceMark.Visibility = copiesTranslation ? Visibility.Collapsed : Visibility.Visible;
        CopyTranslationMark.Visibility = copiesTranslation ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static CopyTextKind ResolveCopyTextKind(bool hasTranslated, bool bubblesVisible) =>
        !hasTranslated
            ? CopyTextKind.RecognizeSource
            : bubblesVisible ? CopyTextKind.Translation : CopyTextKind.Source;

    /// <summary>
    /// Shows a compact amber badge with engine details in its tooltip — but only when a
    /// backup engine was used (the user's chosen primary couldn't serve everything). Stays hidden on
    /// normal runs and for providers without fallback (e.g. DeepL), so it never nags during use.
    /// </summary>
    public void SetEngineBadge(EngineUsage? usage)
    {
        if (usage is null || !usage.FallbackUsed)
        {
            HideEngineBadge();
            return;
        }

        EngineBadgeText.Text = LocalizationService.Get("S.Toolbar.BackupBadge");
        EngineBadge.ToolTip = LocalizationService.Format(
            "S.Toolbar.BackupTooltip", usage.Primary, usage.BackupEngine, usage.Summary);
        EngineBadge.Visibility = Visibility.Visible;
    }

    private void HideEngineBadge()
    {
        EngineBadge.Visibility = Visibility.Collapsed;
        EngineBadge.ToolTip    = null;
    }

    public void SetToggleEnabled(bool enabled)
    {
        _toggleEnabled   = enabled;
        _bubblesVisible  = true;
        RenderToggleButton();
        ToggleBtn.IsEnabled = !_isBusy && _toggleEnabled;
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _bubblesVisible = !_bubblesVisible;
        RenderToggleButton();
        BubblesVisibilityChanged?.Invoke(this, _bubblesVisible);
    }

    private void InitializeSelectors(string sourceLang, string targetLang)
    {
        SrcLangBox.ItemsSource  = LanguageData.OcrSourceLanguages;
        TgtLangBox.ItemsSource  = LanguageData.TargetLanguages;
        ProviderBox.ItemsSource = LanguageData.Providers;

        SrcLangBox.SelectedValue  = LanguageData.GetValidOcrSourceCode(sourceLang);
        TgtLangBox.SelectedValue  = LanguageData.GetValidTargetCode(targetLang);
        ProviderBox.SelectedValue = SettingsService.Instance.Current.Provider;
        if (ProviderBox.SelectedValue == null) ProviderBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Gives each picker exactly the width its closed label needs, measured from the longest entry
    /// it could be showing.
    /// </summary>
    /// <remarks>
    /// <para>A ComboBox left to size itself measures every item in its list, which for the language
    /// pickers means a box wide enough for 斯洛文尼亞文 spelled out both ways — so these carried a
    /// number typed into the markup instead. A typed number is a guess about text nobody measured:
    /// 132 was too narrow for the label it was given in Chinese and too wide for the one in English,
    /// and it could only ever be wrong in one of them.</para>
    ///
    /// <para>Measuring the closed labels answers both at once, in whatever language the interface is
    /// in, and it is the narrowest the box can be without clipping anything the user might pick. The
    /// list is unaffected — it opens as wide as its own contents, as it always did.</para>
    ///
    /// <para>Run once, in the constructor: the toolbar lives for one capture session and the
    /// interface language cannot change underneath it.</para>
    /// </remarks>
    private void SizeSelectorsToClosedLabels()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        SrcLangBox.Width  = ClosedWidth(SrcLangBox, LanguageData.OcrSourceLanguages.Select(l => l.ShortName));
        TgtLangBox.Width  = ClosedWidth(TgtLangBox, LanguageData.TargetLanguages.Select(l => l.ShortName));
        ProviderBox.Width = ClosedWidth(ProviderBox, LanguageData.Providers.Select(p => p.ShortName));

        double ClosedWidth(System.Windows.Controls.ComboBox box, IEnumerable<string> labels)
        {
            var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            double widest = labels.Max(label => new FormattedText(
                label,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                box.FontSize,
                System.Windows.Media.Brushes.Black,
                dpi).WidthIncludingTrailingWhitespace);

            // The label sits in ModernComboBox's ContentSite, inset 9 on the left and 28 on the
            // right to clear the arrow, inside a 1px border either side. The last pixel is for
            // rounding: half a pixel short is a whole character replaced by an ellipsis.
            return Math.Ceiling(widest) + 9 + 28 + 2 + 1;
        }
    }

    private void SyncDebugSwitches(object? sender, EventArgs e)
    {
        var debug = SettingsService.Instance.Current.OcrDebug;
        _syncingDebug = true;
        DebugGroupsSwitch.IsChecked = debug.ShowGroupBoxes;
        DebugLinesSwitch.IsChecked = debug.ShowLineBoxes;
        DebugSourceOnly.IsChecked = !debug.ShowOnTranslation;
        DebugSourceAndTranslation.IsChecked = debug.ShowOnTranslation;
        _syncingDebug = false;
        // 顯示範圍 only says where the boxes are drawn; with neither ticked there is nothing for it
        // to act on, so it is switched off rather than left to be chosen for no effect.
        DebugScopePanel.IsEnabled = debug.ShowGroupBoxes || debug.ShowLineBoxes;
    }

    private void DebugGroupsSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingDebug) SettingsService.Instance.UpdateOcrDebug(showGroups: DebugGroupsSwitch.IsChecked == true);
    }

    private void DebugLinesSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingDebug) SettingsService.Instance.UpdateOcrDebug(showLines: DebugLinesSwitch.IsChecked == true);
    }

    private void SyncCaptureSwitches(object? sender, EventArgs e)
    {
        _syncingCapture = true;
        AutoTranslateSwitch.IsChecked = SettingsService.Instance.Current.AutoTranslateAfterSelection;
        SaveScreenshotSwitch.IsChecked = SettingsService.Instance.Current.SaveScreenshotToDisk;
        _syncingCapture = false;
    }

    private void AutoTranslateSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingCapture) SettingsService.Instance.UpdateCaptureOptions(autoTranslate: AutoTranslateSwitch.IsChecked == true);
    }

    private void SaveScreenshotSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingCapture) SettingsService.Instance.UpdateCaptureOptions(saveScreenshot: SaveScreenshotSwitch.IsChecked == true);
    }

    private void MoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ignoreMoreClick) { _ignoreMoreClick = false; return; }
        // Right edge under the button's right edge, so the menu comes out of what opened it.
        var buttonRight = MoreBtn.TranslatePoint(new System.Windows.Point(MoreBtn.ActualWidth, 0), BarSurface).X;
        MorePopup.HorizontalOffset = Math.Max(0, buttonRight - MorePanel.Width);
        MorePopup.IsOpen = !MorePopup.IsOpen;
    }

    private void DebugScope_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingDebug)
            SettingsService.Instance.UpdateOcrDebug(showOnTranslation: ReferenceEquals(sender, DebugSourceAndTranslation));
    }

    private void MorePopup_Opened(object? sender, EventArgs e)
    {
        SyncDebugSwitches(sender, e);
        SyncCaptureSwitches(sender, e);
    }

    private void MorePopup_Closed(object? sender, EventArgs e)
    {
        _ignoreMoreClick = MoreBtn.IsMouseOver &&
            System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
    }

    private void MorePopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        MorePopup.IsOpen = false;
        MoreBtn.Focus();
        e.Handled = true;
    }

    private void SaveCurrentLanguageSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = CurrentSourceLang;
        settings.TargetLanguage = CurrentTargetLang;
        SettingsService.Instance.Save();
    }

    private static void SaveProviderSelection(TranslationProvider provider)
    {
        var settings = SettingsService.Instance.Current;
        settings.Provider = provider;
        SettingsService.Instance.Save();
    }

    private void SaveTextDirectionSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.Capture.VerticalText = IsVerticalText;
        SettingsService.Instance.Save();
    }

    private void SaveLayoutModeSelection()
    {
        var settings = SettingsService.Instance.Current;
        settings.Capture.LayoutMode = CurrentLayoutMode;
        SettingsService.Instance.Save();
    }
}

/// <param name="LayoutMode">
/// What the user says the framed capture holds, read off the 標準 / 漫畫・文章 switch.
/// </param>
/// <remarks>
/// No default. It carried one while the switch did not exist yet, so that the seam could be wired
/// and measured a step before anything could choose; now that something can, a default would mean
/// a call site added later quietly translating a comic as though it were a game menu — with no
/// compiler complaint and nothing on screen to say which mode answered.
/// </remarks>
public record TranslateRequest(
    string SourceLang,
    string TargetLang,
    bool IsVerticalText,
    CaptureLayoutMode LayoutMode);

/// <inheritdoc cref="TranslateRequest" path="/param[@name='LayoutMode']"/>
/// <remarks>
/// Copying reads the region through the same recogniser and fills the same _lastOcrBlocks the
/// translation does, so it has to be grouped the same way. Left off, the text a user copies would
/// come apart differently from the text they just had translated, out of the same capture.
/// </remarks>
public record CopyTextRequest(
    CopyTextKind Kind,
    string SourceLang,
    bool IsVerticalText,
    CaptureLayoutMode LayoutMode);

public enum CopyTextKind
{
    RecognizeSource,
    Source,
    Translation,
}
