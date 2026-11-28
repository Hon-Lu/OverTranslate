using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Services;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The translate pill: 翻譯 before anything is on screen, and once a translation is, 重新翻譯 with
/// 編輯框選範圍 grown on its right.
/// </summary>
/// <remarks>
/// <para>The second half is how the box is reached again after translating. It is on this button
/// because this is where the eye is when the bubbles land, and on a button rather than on the
/// screen because the translated capture has to stay clean: people compare it against the
/// original with other screenshot tools, and a frame or handles left over it got in the way and
/// were easy to catch by accident.</para>
///
/// <para>The shape follows whether a translation is on screen and nothing else — not whether one is
/// running. Re-translating after a change of language keeps both halves and dims them; collapsing
/// for the run and growing back after it would flicker the bar twice for one press.</para>
/// </remarks>
public partial class ToolbarWindow
{
    /// <summary>編輯框選範圍 was pressed: hand the box back to be moved or resized.</summary>
    public event EventHandler? EditSelectionRequested;

    internal enum TranslateGlyphKind { Translate, Again }

    /// <summary>How the pill looks in a given state; see <see cref="ResolveTranslateButton"/>.</summary>
    internal readonly record struct TranslateButtonLook(
        bool Split, string LabelKey, TranslateGlyphKind Glyph, bool Spinning);

    /// <summary>
    /// The pill for a capture that has, or has not, a translation on screen, and is or is not busy.
    /// </summary>
    /// <remarks>
    /// Busy shows the looping arrow turning, the first run included: the arrow is what says "working",
    /// and 文A turning would read as a broken icon.
    /// </remarks>
    internal static TranslateButtonLook ResolveTranslateButton(bool hasTranslated, bool busy, string busyLabelKey) =>
        new(Split: hasTranslated,
            LabelKey: busy ? busyLabelKey : hasTranslated ? "S.Toolbar.Retranslate" : "S.Toolbar.Translate",
            Glyph: busy || hasTranslated ? TranslateGlyphKind.Again : TranslateGlyphKind.Translate,
            Spinning: busy);

    // 16-grid strokes, the same grid and caps as the bar's action icons.
    private static readonly Geometry TranslatePath = Frozen(
        "M5,1.6 V2.8 M1.6,3.6 H8.4 M2.8,4.4 C3.4,6.6 5.2,8.3 8.4,9.2 M7.2,4.4 C6.6,6.6 4.8,8.3 1.6,9.2 " +
        "M8.6,14.4 L11.6,6.8 L14.6,14.4 M9.7,11.6 H13.5");
    private static readonly Geometry AgainPath = Frozen(
        "M12.68,10.7 A5.4,5.4 0 1 1 12.68,5.3 M13.15,2.74 L12.68,5.3 L10.26,4.41");

    /// <summary>Width of the right half, its rule included.</summary>
    private const double EditSelectionWidth = 33;

    /// <summary>
    /// Slower than the bar's other transitions on purpose: the half is new, and growing in at the
    /// pace of a hover would go unseen.
    /// </summary>
    private static readonly TimeSpan GrowDuration = TimeSpan.FromMilliseconds(320);

    private static readonly TimeSpan InkFade = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan InkDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan SpinPeriod = TimeSpan.FromMilliseconds(900);

    private string _busyLabelKey = "S.Toolbar.Translating";
    private bool? _renderedSplit;
    private bool _spinning;

    private void RenderTranslateButton()
    {
        var look = ResolveTranslateButton(_hasTranslated, _isBusy, _busyLabelKey);

        TranslateLabel.Text = LocalizationService.Get(look.LabelKey);
        TranslateGlyph.Data = look.Glyph == TranslateGlyphKind.Again ? AgainPath : TranslatePath;
        SetSpinning(look.Spinning);

        // Dimmed as one: each half dimming itself would leave the glow between them at full strength.
        TranslatePill.Opacity = _isBusy ? 0.45 : 1;
        TranslateBtn.IsEnabled = !_isBusy;
        EditSelectionBtn.IsEnabled = !_isBusy && look.Split;
        EditSelectionBtn.IsTabStop = look.Split;

        if (_renderedSplit != look.Split)
        {
            // No animation for the state the bar opens in: nothing has changed to draw the eye to.
            GrowEditSelection(look.Split, animate: _renderedSplit is not null && IsLoaded);
            _renderedSplit = look.Split;
        }
    }

    /// <summary>
    /// Grows the right half in, or takes it away, by its layout width — so the label beside it never
    /// moves — with the rule and the icon fading in behind the blue.
    /// </summary>
    private void GrowEditSelection(bool split, bool animate)
    {
        // Square on the side it meets the right half for as long as any of that half is showing:
        // set now when growing, and only once the half is gone when shrinking. The half itself is
        // collapsed once gone, not merely clipped to nothing: clipped, it keeps its 33 of layout and
        // the label card, which goes by layout bounds, would name it over the next button along.
        if (split)
        {
            TranslateBtn.Tag = "split";
            EditSelectionBtn.Visibility = Visibility.Visible;
        }

        double width = split ? EditSelectionWidth : 0;
        double ink = split ? 1 : 0;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            EditSelectionSlot.BeginAnimation(WidthProperty, null);
            EditSelectionInk.BeginAnimation(OpacityProperty, null);
            EditSelectionSlot.Width = width;
            EditSelectionInk.Opacity = ink;
            if (!split) Collapsed();
            return;
        }

        var grow = new DoubleAnimation(width, GrowDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (!split)
        {
            grow.Completed += (_, _) =>
            {
                // A grow started in the meantime owns the corner now.
                if (_renderedSplit == false) Collapsed();
            };
        }
        EditSelectionSlot.BeginAnimation(WidthProperty, grow);

        EditSelectionInk.BeginAnimation(OpacityProperty, new DoubleAnimation(ink, InkFade)
        {
            BeginTime = split ? InkDelay : TimeSpan.Zero,
        });

        void Collapsed()
        {
            TranslateBtn.Tag = null;
            EditSelectionBtn.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Turns the arrow while busy; still when the system has animation switched off.</summary>
    private void SetSpinning(bool spinning)
    {
        spinning &= SystemParameters.ClientAreaAnimation;
        if (spinning == _spinning) return;
        _spinning = spinning;

        if (!spinning)
        {
            TranslateGlyphSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            TranslateGlyphSpin.Angle = 0;
            return;
        }

        // A RenderTransform, which blurs text in motion — but this is a drawing with no text in it.
        TranslateGlyphSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, SpinPeriod) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void EditSelectionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_hasTranslated) return;
        EditSelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
