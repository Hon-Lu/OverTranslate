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
/// <para>Not every block is theirs to read: see <see cref="ReaderFor"/>, which decides it for each
/// block before anything is read, from the block's box and which way its lines run
/// (<see cref="MangaLineGeometry"/>).</para>
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
        // Every block the column pipeline reads, and which way it is to be set.
        var passOn = new List<(RectangleF Block, MangaReader Reader)>();
        IOcrSlot? slot = null;
        try
        {
            var readers = Readers(bitmap, detection.Text, detectLines);
            var mangaReads = new List<int>();
            for (int i = 0; i < detection.Text.Count; i++)
            {
                if (readers[i] == MangaReader.Manga) mangaReads.Add(i);
                else passOn.Add((detection.Text[i], readers[i]));
            }
            passOn.AddRange(detection.Long.Select(block => (block, ReaderFor(block, MangaLineDirection.Unsure))));

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

            foreach (var (block, reader) in passOn)
            {
                var crop = MangaPageLayout.LongBlockCrop(block, bitmap.Width, bitmap.Height);
                if (crop.Width < 2 || crop.Height < 2) continue;
                using var part = bitmap.Clone(crop, bitmap.PixelFormat);
                var found = await slot!.RecognizeAsync(
                    part, language, maxDetectSize, cancellationToken, verticalText: true);
                var groups = VerticalColumnGrouping.Group(found, part.Width, bitmap: part, language: language);
                passedOn.AddRange(groups.Select(group => Moved(Set(group, reader), crop.X, crop.Y)));
            }
        }

        var blocks = MangaPageLayout.Assemble(read, bubbles, passedOn, luma);
        if (detectColumns is not null)
            blocks = MangaColumnAngles.Apply(blocks, read, luma, detectColumns);
        return (MangaReadOutcome.Read, blocks);
    }

    /// <summary>
    /// A block wider than this many times its height, whose lines run down the page, is more columns
    /// than manga-ocr can read at once — see <see cref="ReaderFor"/>.
    /// </summary>
    /// <remarks>
    /// MEASURED in the first round on the vertical and comic corpora: the widest block of three
    /// columns or more that manga-ocr read correctly is 1.57 times as wide as tall
    /// (計画は順調に進んでいる。), and the narrowest that it squeezed past reading is
    /// vertical-image-ja5's balloon at 2.48 (464×182 framed one way, up to 2.88 framed others). No
    /// block of columns on any corpus lies between the two; 2 sits in that gap, a fifth from either.
    /// </remarks>
    internal const double WideColumnsRatio = 2;

    /// <summary>
    /// Whether manga-ocr can read a block, and if not, how the column pipeline is to set it.
    /// </summary>
    /// <remarks>
    /// <para>manga-ocr reads a block as one string, the whole box squeezed into 224×224. That gives it
    /// one way of reading and three ways for a block to be out of its reach, and every one of them
    /// goes to the column pipeline, which finds the lines itself:</para>
    ///
    /// <list type="bullet">
    /// <item><b>Too long</b> (<see cref="MangaPageLayout.IsLong"/>): a column of more characters than
    /// it reads, squeezed along its length. The column pipeline's groups keep the directions it gives
    /// them, as they always have.</item>
    /// <item><b>Too many columns</b>: lines that run down in a box <see cref="WideColumnsRatio"/>
    /// times as wide as tall or more, squeezed across. vertical-image-ja5's balloon of sixteen short
    /// columns came back with its last sentences turned into hundreds of dots, and the overlay sized
    /// its type from that many characters — a third of the size of the writing. Set down.</item>
    /// <item><b>Not columns at all</b>: lines that run across in a box not wide enough to be a row
    /// (<see cref="MangaPageLayout.AcrossRatio"/>). It cannot read English, and read the nine-line
    /// caption of vertical-image-ja5 as sentences that are not on the page. Set across.</item>
    /// </list>
    ///
    /// <para>Everything else — and everything whose lines say nothing — is manga-ocr's, as it always
    /// was: on the transcribed corpora it reads vertical writing better than the column pipeline does.
    /// The direction the column pipeline is told to set a block in is the one its lines voted for, so
    /// a block sent across stays across in every frame however the column pipeline groups it.</para>
    /// </remarks>
    internal static MangaReader ReaderFor(RectangleF block, MangaLineDirection lines)
    {
        if (MangaPageLayout.IsLong(block)) return MangaReader.Columns;
        return lines switch
        {
            MangaLineDirection.Across when !MangaPageLayout.RunsAcross(block) => MangaReader.ColumnsAcross,
            MangaLineDirection.Down when block.Width >= WideColumnsRatio * block.Height => MangaReader.ColumnsDown,
            _ => MangaReader.Manga,
        };
    }

    /// <summary><see cref="ReaderFor"/> for each block, its lines measured all together.</summary>
    internal static MangaReader[] Readers(
        Bitmap page, IReadOnlyList<RectangleF> blocks, Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? detectLines)
    {
        var directions = detectLines is null || blocks.Count == 0
            ? new MangaLineDirection[blocks.Count]
            : MangaLineGeometry.Directions(page, blocks, detectLines);
        return [.. blocks.Select((block, i) => ReaderFor(block, directions[i]))];
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
    private static OcrTextBlock Set(OcrTextBlock group, MangaReader reader) => reader switch
    {
        MangaReader.ColumnsAcross when !group.RunsAcross => group with { RunsAcross = true },
        MangaReader.ColumnsDown when group.RunsAcross => group with { RunsAcross = false },
        _ => group,
    };

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

/// <summary>Which reader a manga block goes to — see <see cref="MangaVerticalReader.ReaderFor"/>.</summary>
internal enum MangaReader
{
    Manga,
    /// <summary>The column pipeline, its groups set as it sets them.</summary>
    Columns,
    /// <summary>The column pipeline, every group set across.</summary>
    ColumnsAcross,
    /// <summary>The column pipeline, every group set down.</summary>
    ColumnsDown,
}
