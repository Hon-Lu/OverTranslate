using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace OverTranslate.Views.Controls;

/// <summary>
/// Paints the known placeholders of a template — <c>{text}</c>, <c>{context}</c> and the language
/// names — in the accent colour over the TextBox they are typed in. Laid on top of the box in the
/// same cell, and invisible to the pointer.
/// </summary>
/// <remarks>
/// <para>The box keeps drawing every character itself, and this only repaints the stretches that are
/// placeholders, on a fill that covers the box's own glyphs underneath. Everything that makes a box a
/// box — the caret, the selection, the input method's composition underline, undo, paste — stays the
/// TextBox's, untouched. Two other ways were tried first and both cost one of those: a box with
/// transparent text over a coloured copy loses the composition underline, which is drawn in the
/// text's own colour, and a RichTextBox has to be kept from recording its own colouring as edits,
/// which means an undo history of its own in place of the box's.</para>
///
/// <para>Where to paint is read off the box — <see cref="TextBox.GetRectFromCharacterIndex(int, bool)"/>
/// for every character of every placeholder — never worked out from the text, so a line the box
/// wraps or scrolls is where the colour goes too. The glyphs are set on the baseline a TextBlock of
/// the box's font and line height measures, which is where the box sets its own.</para>
///
/// <para>Only the names <see cref="Placeholders"/> lists are painted, matched as the provider fills
/// them in: a brace around anything else is sent as typed, and colouring it would say otherwise.</para>
/// </remarks>
public sealed class PlaceholderLayer : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(PlaceholderLayer),
        new PropertyMetadata(null, (d, e) => ((PlaceholderLayer)d).Attach(e.OldValue as TextBox, e.NewValue as TextBox)));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PlaceholderLayer),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The fill behind a placeholder. Opaque: it is what hides the box's own glyphs.</summary>
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(Brush), typeof(PlaceholderLayer),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The placeholders that are painted, as the template spells them.</summary>
    public static readonly DependencyProperty PlaceholdersProperty = DependencyProperty.Register(
        nameof(Placeholders), typeof(IReadOnlyList<string>), typeof(PlaceholderLayer),
        new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public TextBox? Target { get => (TextBox?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public IReadOnlyList<string> Placeholders { get => (IReadOnlyList<string>)GetValue(PlaceholdersProperty); set => SetValue(PlaceholdersProperty, value); }

    private ScrollViewer? _scroller;

    /// <summary>The box's baseline within a line, measured once per font; NaN until then.</summary>
    private double _baseline = double.NaN;

    public PlaceholderLayer()
    {
        IsHitTestVisible = false;
    }

    private void Attach(TextBox? old, TextBox? box)
    {
        if (old is not null)
        {
            old.TextChanged -= Box_Changed;
            old.SizeChanged -= Box_Changed;
            old.Loaded -= Box_Loaded;
        }
        if (box is null) return;

        box.TextChanged += Box_Changed;
        box.SizeChanged += Box_Changed;
        box.Loaded += Box_Loaded;
        if (box.IsLoaded) Box_Loaded(box, new RoutedEventArgs());
    }

    private void Box_Loaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is not null) _scroller.ScrollChanged -= Scroller_ScrollChanged;
        _scroller = Target?.Template?.FindName("PART_ContentHost", Target) as ScrollViewer;
        if (_scroller is not null) _scroller.ScrollChanged += Scroller_ScrollChanged;
        _baseline = double.NaN;
        Repaint();
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private void Box_Changed(object sender, EventArgs e) => Repaint();

    /// <summary>
    /// After the box has laid the change out: asked straight away, it would still report where the
    /// characters were before the edit.
    /// </summary>
    private void Repaint() => Dispatcher.BeginInvoke(InvalidateVisual, DispatcherPriority.Render);

    protected override void OnRender(DrawingContext dc)
    {
        var box = Target;
        if (box is null || _scroller?.Content is not FrameworkElement view || Placeholders.Count == 0) return;

        // Clipped where the box clips its own text: the view inside the scroller, not the scroller.
        var clip = new Rect(view.TranslatePoint(new Point(0, 0), this), view.RenderSize);
        clip.Intersect(new Rect(_scroller.TranslatePoint(new Point(0, 0), this),
            new Size(_scroller.ViewportWidth, _scroller.ViewportHeight)));
        if (clip.IsEmpty) return;
        dc.PushClip(new RectangleGeometry(clip));

        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var baseline = Baseline(box);
        var text = box.Text;

        foreach (var (start, length) in Find(text, Placeholders))
        {
            // One stretch per drawn line: a placeholder too long for what is left of a line wraps.
            var at = start;
            while (at < start + length)
            {
                var first = box.GetRectFromCharacterIndex(at);
                if (first.IsEmpty) break;
                var end = at + 1;
                while (end < start + length && Math.Abs(box.GetRectFromCharacterIndex(end).Top - first.Top) < 0.5) end++;
                var last = box.GetRectFromCharacterIndex(end - 1, trailingEdge: true);

                var stretch = new Rect(box.TranslatePoint(first.TopLeft, this), box.TranslatePoint(last.BottomRight, this));
                dc.DrawRoundedRectangle(Highlight, null,
                    new Rect(stretch.X - 1, stretch.Y + 1, stretch.Width + 2, Math.Max(0, stretch.Height - 2)), 3, 3);

                var glyphs = new FormattedText(text[at..end], CultureInfo.CurrentUICulture, box.FlowDirection,
                    typeface, box.FontSize, Foreground, pixelsPerDip);
                dc.DrawText(glyphs, new Point(stretch.X, stretch.Y + baseline - glyphs.Baseline));
                at = end;
            }
        }

        dc.Pop();
    }

    /// <summary>Where a TextBlock in the box's font and line height sets its baseline.</summary>
    private double Baseline(TextBox box)
    {
        if (!double.IsNaN(_baseline)) return _baseline;

        var probe = new TextBlock
        {
            Text = "Ag國",
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            LineHeight = TextBlock.GetLineHeight(box),
            LineStackingStrategy = TextBlock.GetLineStackingStrategy(box),
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));
        return _baseline = probe.BaselineOffset;
    }

    /// <summary>Forgets the measured baseline when the font changes — a change of interface language.</summary>
    internal void FontChanged()
    {
        _baseline = double.NaN;
        InvalidateVisual();
    }

    /// <summary>Every occurrence of the given placeholders, case-insensitively as they are filled in.</summary>
    internal static IEnumerable<(int Start, int Length)> Find(string text, IReadOnlyList<string> placeholders)
    {
        for (var i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
        {
            foreach (var name in placeholders)
            {
                if (string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                yield return (i, name.Length);
                break;
            }
        }
    }
}
