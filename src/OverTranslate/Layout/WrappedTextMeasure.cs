using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverTranslate.Layout;

/// <summary>
/// How big wrapped text comes out when a <see cref="TextBlock"/> lays it out, which is how every
/// overlay draws it.
/// </summary>
/// <remarks>
/// <para>Not <see cref="FormattedText"/> with a <c>MaxTextWidth</c>, which is what this replaced.
/// The two agree on every line they can break, and disagree on a run that cannot be broken and is
/// wider than the line: FormattedText keeps it whole and counts one line, TextBlock breaks it
/// anyway and draws two. A curly quote glues its neighbours into such a run (Unicode line breaking
/// gives it no break on either side), so "“如果你知道的話，”嗯……" measured three lines and drew
/// four. The box was built from the three, clipped the fourth, and the translation lost its first
/// line's top and its last line entirely — region-comic-en-4 over Youdao, which writes curly quotes
/// where Microsoft writes corner brackets. Swept over sizes and widths, every disagreement had the
/// TextBlock taller, so the measurement only ever came out short.</para>
///
/// <para>One TextBlock, reused: callers search a size downwards half a pixel at a time, and a new
/// element per step is allocation for nothing. Set and measured on the caller's thread only.</para>
/// </remarks>
internal sealed class WrappedTextMeasure
{
    private readonly TextBlock _block = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.None,
    };

    /// <param name="lineHeight">The drawn TextBlock's own LineHeight, or NaN for the font's.</param>
    public System.Windows.Size Measure(
        string text, Typeface typeface, double fontSize, double width,
        double lineHeight = double.NaN,
        System.Windows.FlowDirection flowDirection = System.Windows.FlowDirection.LeftToRight)
    {
        _block.Text = text;
        _block.FontFamily = typeface.FontFamily;
        _block.FontStyle = typeface.Style;
        _block.FontWeight = typeface.Weight;
        _block.FontStretch = typeface.Stretch;
        _block.FontSize = fontSize;
        _block.LineHeight = lineHeight;
        _block.FlowDirection = flowDirection;
        _block.Measure(new System.Windows.Size(Math.Max(0, width), double.PositiveInfinity));
        return _block.DesiredSize;
    }
}
