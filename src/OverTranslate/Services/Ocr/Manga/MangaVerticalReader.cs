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
/// <para>Blocks too long for manga-ocr are read by the column pipeline from a crop of the page and
/// moved back into page coordinates, and so are blocks the column pipeline finds set against the
/// shape of their box (<see cref="MangaPageLayout.Suspect"/>). They wait for a column engine slot
/// even on the realtime path: they are rare — three pages of 46 in the corpora — and the page they
/// are on has already been read; giving that read up because a slot was taken would cost more than
/// the wait.</para>
/// </remarks>
internal static class MangaVerticalReader
{
    /// <param name="wait">
    /// False turns a GPU busy with another page into <see cref="MangaReadOutcome.Busy"/> — see
    /// <see cref="MangaOcrEngine.ReadAsync"/>.
    /// </param>
    /// <param name="readColumns">The column pipeline, for the long blocks' crops.</param>
    /// <param name="detectColumns">
    /// The column detector alone, for which column blocks are tilted (see
    /// <see cref="MangaColumnAngles"/>); null leaves every block upright.
    /// </param>
    /// <returns>The blocks when the outcome is <see cref="MangaReadOutcome.Read"/>; null otherwise.</returns>
    internal static async Task<(MangaReadOutcome Outcome, List<OcrTextBlock>? Blocks)> ReadAsync(
        MangaOcrEngine manga,
        Bitmap bitmap,
        bool wait,
        Func<Bitmap, CancellationToken, Task<List<OcrTextBlock>>> readColumns,
        CancellationToken cancellationToken,
        Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? detectColumns = null)
    {
        var (outcome, page) = await manga.ReadAsync(bitmap, wait, cancellationToken);
        if (outcome != MangaReadOutcome.Read)
            return (outcome, null);

        var passedOn = new List<OcrTextBlock>();
        foreach (var block in page!.Long)
        {
            var read = await ReadColumnsAsync(block);
            if (read is not null) passedOn.AddRange(read);
        }

        // Read by manga-ocr already, but asked again of the column pipeline when the ink looks set
        // the other way from the box — see MangaPageLayout.Suspect.
        var mangaBlocks = page.Blocks;
        if (page.Luma is { } pageLuma)
        {
            var suspects = page.Blocks.Select(b => MangaPageLayout.Suspect(b.Bounds, pageLuma)).ToList();
            if (suspects.Any(suspect => suspect != MangaWriting.Unsure))
            {
                mangaBlocks = [];
                for (int i = 0; i < page.Blocks.Count; i++)
                {
                    var block = page.Blocks[i];
                    if (suspects[i] != MangaWriting.Unsure &&
                        await ReadColumnsAsync(block.Bounds) is { } read &&
                        MangaPageLayout.ColumnsAgree(suspects[i], read))
                        passedOn.AddRange(read);
                    else
                        mangaBlocks.Add(block);
                }
            }
        }

        var blocks = MangaPageLayout.Assemble(mangaBlocks, page.Bubbles, passedOn, page.Luma);
        if (detectColumns is not null && page.Luma is { } luma)
            blocks = MangaColumnAngles.Apply(blocks, mangaBlocks, luma, detectColumns);
        return (outcome, blocks);

        // The column pipeline's reading of one block, in page coordinates; null when the crop is empty.
        async Task<List<OcrTextBlock>?> ReadColumnsAsync(RectangleF block)
        {
            var crop = MangaPageLayout.LongBlockCrop(block, bitmap.Width, bitmap.Height);
            if (crop.Width < 2 || crop.Height < 2) return null;
            using var part = bitmap.Clone(crop, bitmap.PixelFormat);
            var read = await readColumns(part, cancellationToken);
            return [.. read.Select(found => Moved(found, crop.X, crop.Y))];
        }
    }

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
