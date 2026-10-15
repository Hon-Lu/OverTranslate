using System.Drawing;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// A vertical page read with the manga models, turned into translatable blocks. Whether a page comes
/// here at all is <see cref="OcrService"/>'s decision; this is only what happens once it has.
/// </summary>
/// <remarks>
/// <para>Measured on the three transcribed corpora, counting vertical sentences only, the models read
/// 95/97, 150/157 and 137/143 whole against 72, 130 and 116 for the column pipeline, in about a
/// third of the time.</para>
///
/// <para>Not every block is theirs to read. Before anything is read, each block at least as wide as
/// tall is asked which way its lines run (<see cref="MangaLineGeometry"/>), and a block whose lines
/// run the other way from what the shape of its box says — rows in a box too narrow to be a row,
/// columns in one wide enough to be — is read by the column pipeline instead and set the way its
/// lines run. manga-ocr reads a block as one string squeezed into 224×224 and has no lines to give:
/// it cannot read English at all, read the nine-line caption of vertical-image-ja5 as sentences
/// that are not on the page, and turned the last sentences of that page's sixteen-column balloon
/// into hundreds of dots. The column pipeline finds the lines itself and read all of them word for
/// word. Blocks too long for manga-ocr (<see cref="MangaPageLayout.IsLong"/>) go to it as they
/// always have.</para>
///
/// <para>Each block is read once, by one reader, decided before either runs. A page that needs the
/// column pipeline takes one of its slots before manga-ocr reads anything; on a live screen, where
/// nothing waits, a taken slot makes the whole page <see cref="MangaReadOutcome.Busy"/> and the next
/// poll asks again, rather than reading the manga blocks and throwing them away.</para>
/// </remarks>
internal static class MangaVerticalReader
{
    /// <param name="wait">
    /// False never queues: a GPU busy with another page, or no free column slot for a page that
    /// needs one, is <see cref="MangaReadOutcome.Busy"/> — see <see cref="MangaOcrEngine.DetectAsync"/>.
    /// </param>
    /// <param name="columns">The column pipeline's engine, for the blocks the manga models do not read.</param>
    /// <param name="language">The source language, normalised, for the column pipeline.</param>
    /// <param name="maxDetectSize">
    /// The column pipeline's detection size: null for a screenshot, the live size otherwise.
    /// </param>
    /// <param name="detectColumns">
    /// The column detector alone, for which column blocks are tilted (see
    /// <see cref="MangaColumnAngles"/>); null leaves every block upright.
    /// </param>
    /// <param name="detectLines">
    /// The column detector as the column pipeline detects a vertical page, nothing read, for which
    /// way each block's lines run (see <see cref="MangaLineGeometry"/>); null leaves every block to
    /// its shape.
    /// </param>
    /// <returns>The blocks when the outcome is <see cref="MangaReadOutcome.Read"/>; null otherwise.</returns>
    internal static async Task<(MangaReadOutcome Outcome, List<OcrTextBlock>? Blocks)> ReadAsync(
        MangaOcrEngine manga,
        Bitmap bitmap,
        bool wait,
        IOcrEngine columns,
        string language,
        int? maxDetectSize,
        CancellationToken cancellationToken,
        Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? detectColumns = null,
        Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? detectLines = null)
    {
        var (outcome, detection) = await manga.DetectAsync(bitmap, wait, cancellationToken);
        if (detection is null)
            return (outcome, null);

        List<MangaBlock>? read;
        LumaPage luma;
        List<RectangleF> bubbles;
        // Every block the column pipeline reads and the way it is to be set: null for a long block,
        // whose groups keep the directions the column pipeline gives them, as they always have.
        var passOn = new List<(RectangleF Block, bool? Across)>();
        IOcrSlot? slot = null;
        try
        {
            var routes = Routes(bitmap, detection.Text, detectLines);
            var mangaReads = new List<int>();
            for (int i = 0; i < detection.Text.Count; i++)
            {
                if (routes[i] is { } across) passOn.Add((detection.Text[i], across));
                else mangaReads.Add(i);
            }
            passOn.AddRange(detection.Long.Select(block => (block, (bool?)null)));

            if (passOn.Count > 0)
            {
                slot = wait ? await columns.TakeSlotAsync(cancellationToken) : columns.TryTakeSlot();
                if (slot is null) return (MangaReadOutcome.Busy, null);
            }

            read = await detection.ReadAsync(mangaReads, cancellationToken);
            luma = detection.Luma;
            bubbles = detection.Bubbles;
        }
        catch
        {
            slot?.Dispose();
            throw;
        }
        finally
        {
            // The GPU is given back before the column pipeline runs: it has nothing more to do here.
            detection.Dispose();
        }

        var passedOn = new List<OcrTextBlock>();
        using (slot)
        {
            if (read is null) return (MangaReadOutcome.Unavailable, null);

            foreach (var (block, across) in passOn)
            {
                var crop = MangaPageLayout.LongBlockCrop(block, bitmap.Width, bitmap.Height);
                if (crop.Width < 2 || crop.Height < 2) continue;
                using var part = bitmap.Clone(crop, bitmap.PixelFormat);
                var found = await slot!.RecognizeAsync(
                    part, language, maxDetectSize, cancellationToken, verticalText: true);
                var groups = VerticalColumnGrouping.Group(found, part.Width, bitmap: part, language: language);
                passedOn.AddRange(groups.Select(group => Moved(Set(group, across), crop.X, crop.Y)));
            }
        }

        var blocks = MangaPageLayout.Assemble(read, bubbles, passedOn, luma);
        if (detectColumns is not null)
            blocks = MangaColumnAngles.Apply(blocks, read, luma, detectColumns);
        return (MangaReadOutcome.Read, blocks);
    }

    /// <summary>
    /// For each block, null when manga-ocr reads it; otherwise the column pipeline does, and the
    /// value is whether it runs across.
    /// </summary>
    /// <remarks>
    /// A block goes to the column pipeline only when its lines and its shape disagree. Where they
    /// agree, or the lines say nothing, it is read as it always was.
    /// </remarks>
    internal static bool?[] Routes(
        Bitmap page, IReadOnlyList<RectangleF> blocks, Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? detectLines)
    {
        var routes = new bool?[blocks.Count];
        if (detectLines is null || blocks.Count == 0) return routes;

        var directions = MangaLineGeometry.Directions(page, blocks, detectLines);
        for (int i = 0; i < blocks.Count; i++)
        {
            bool shapeAcross = MangaPageLayout.RunsAcross(blocks[i]);
            routes[i] = directions[i] switch
            {
                MangaLineDirection.Across when !shapeAcross => true,
                MangaLineDirection.Down when shapeAcross => false,
                _ => null,
            };
        }
        return routes;
    }

    /// <summary>
    /// A group the column pipeline read out of a block sent to it, set the way the block's lines run.
    /// </summary>
    /// <remarks>
    /// The direction was decided for the block as a whole, from all of its lines, and it is what the
    /// block was sent for; a group the column pipeline sets the other way — a sound effect beside a
    /// balloon's columns, a two-character line it could not tell — would otherwise turn the block's
    /// overlay half across and half down, and differently from frame to frame.
    /// </remarks>
    private static OcrTextBlock Set(OcrTextBlock group, bool? across) =>
        across is { } runsAcross && group.RunsAcross != runsAcross ? group with { RunsAcross = runsAcross } : group;

    private static OcrTextBlock Moved(OcrTextBlock block, double dx, double dy)
    {
        static System.Windows.Rect Shift(System.Windows.Rect r, double dx, double dy) =>
            r.IsEmpty ? r : new System.Windows.Rect(r.X + dx, r.Y + dy, r.Width, r.Height);

        return block with
        {
            Bounds = Shift(block.Bounds, dx, dy),
            SourceLineBounds = block.SourceLineBounds?.Select(r => Shift(r, dx, dy)).ToList(),
            LayoutBounds = Shift(block.LayoutBounds, dx, dy),
            Tilt = block.Tilt?.Offset(dx, dy),
        };
    }
}
