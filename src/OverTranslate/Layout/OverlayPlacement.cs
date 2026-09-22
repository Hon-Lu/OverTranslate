using OverTranslate.Services;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Layout;

/// <summary>
/// Turns "what the user said this capture holds" into "how each block is to be drawn".
/// </summary>
/// <remarks>
/// <para>This is the one layer that reads <see cref="CaptureLayoutMode"/> on the way to the screen.
/// Grouping reads it too, but through its own table of thresholds; between them there is no third
/// reader, and the overlay in particular has none — it is handed an
/// <see cref="OverlayLayoutIntent"/> and never learns which mode produced it.</para>
///
/// <para>Vertical text leaves by the first door. Its blocks have been through
/// <c>CombineColumns</c>, which refills <c>SourceLineBounds</c> with columns — or, for a
/// lone column, with one cell per character — so neither the horizontal splitter nor the reflow
/// intent describes anything real about them. They go to the vertical renderer exactly as they
/// arrive, which is what they did before this layer existed.</para>
/// </remarks>
internal static class OverlayPlacement
{
    public static List<TranslatedBlock> Place(
        IReadOnlyList<TranslatedBlock> blocks, CaptureLayoutMode mode, bool verticalText)
    {
        // Not a fast path — a hard condition. See the remarks above.
        if (verticalText)
            return [.. blocks];

        return mode switch
        {
            // Interfaces keep the arrangement they were framed in. Grouping exists so that a
            // wrapped sentence reaches the translator whole; drawing it whole is a separate
            // question, and over a menu the answer is no — the lines go back where the panel had
            // them.
            CaptureLayoutMode.Interface => GroupedTranslationLines.SplitOntoSourceLines(blocks),

            // General, and anything a later release adds that this build does not know. The default
            // catches the unknown one on purpose: a mode nobody here can read has already become
            // General on its way out of the settings file, and answering it the same way twice
            // beats a second table that can drift away from the first.
            _ => [.. blocks.Select(AsGroupReflow)],
        };
    }

    /// <summary>
    /// A group that was read as several lines is drawn as the one block it was translated as.
    /// </summary>
    /// <remarks>
    /// Only a group. A single line has nothing to re-set, and the ordinary single-line path may
    /// widen it to the right where there is room — which is right for a caption or a label, and
    /// exactly what a balloon must not do.
    /// </remarks>
    private static TranslatedBlock AsGroupReflow(TranslatedBlock block) =>
        block.SourceLineBounds is { Count: > 1 }
            ? block with { LayoutIntent = OverlayLayoutIntent.GroupReflow }
            : block;
}
