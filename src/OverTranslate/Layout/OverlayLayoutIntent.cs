namespace OverTranslate.Layout;

/// <summary>
/// How a translated block is to be drawn, decided by the layer that places it.
/// </summary>
/// <remarks>
/// <para>The renderer needs to know that a block is a whole speech balloon rather than one line of
/// a page, and it must not learn that by asking which capture mode the user picked: the mode is a
/// product word for what is in the picture, and the overlay is the wrong place to translate one
/// into the other. Placement reads the mode; the overlay reads this.</para>
///
/// <para>Nor can the shape of the data stand in for it. The obvious proxy —
/// <c>SourceLineBounds is { Count: &gt; 1 }</c> — already answers a different question by the time
/// it reaches here: the vertical pipeline refills that list with columns, or, for a lone column,
/// with one cell per character. Reading a layout policy out of it would be stacking a third meaning
/// on a field that already carries two.</para>
/// </remarks>
public enum OverlayLayoutIntent
{
    /// <summary>Existing placement: the bubble is sized and centred as it always was.</summary>
    Default,

    /// <summary>
    /// The whole group is re-set inside its own box: top-aligned, left-aligned, and never widened
    /// past it. The box is a speech balloon, so what lies to its right is picture, not free space.
    /// </summary>
    GroupReflow,

    /// <summary>
    /// A vertical source whose translation is set across in its place, because the target is not
    /// written downwards — see <see cref="VerticalTargetLanguages"/> and
    /// <see cref="AcrossInColumnsLayout"/>. The block is still the column group it was read as.
    /// </summary>
    AcrossInColumns,
}
