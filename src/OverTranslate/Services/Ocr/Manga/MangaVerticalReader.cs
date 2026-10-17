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
/// <para>Each block is read once, by one reader, decided before either runs — but for a block sent
/// across, which both read and <see cref="ReadsAcross"/> settles between. A page that needs the
/// column pipeline takes one of its slots before manga-ocr reads the blocks that depended on that
/// decision; on a live screen, where nothing waits, a taken slot makes the whole page
/// <see cref="MangaReadOutcome.Busy"/> and the next poll asks again, rather than reading them and
/// throwing them away. Only what <see cref="ReadAhead"/> began while the lines were measured can be
/// lost that way, and only on a page with no long block, whose slot is taken first.</para>
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
        var routes = new MangaRoutes();
        IOcrSlot? slot = null;
        try
        {
            // A long block always goes to the column pipeline, so its slot is taken before anything
            // is read, ahead or not.
            if (detection.Long.Count > 0)
            {
                slot = wait ? await columns.TakeSlotAsync(cancellationToken) : columns.TryTakeSlot();
                if (slot is null) return (MangaReadOutcome.Busy, null);
            }

            var ahead = detectLines is null ? [] : ReadAhead(detection.Text);
            detection.ReadAhead(ahead, cancellationToken);
            var readers = Readers(bitmap, detection.Text, detectLines);
            ReadColumnsCut(readers, detection.Pieces);
            routes = MangaRoutes.Of(readers, detection.Leaning);
            var mangaReads = MangaReads(readers, ahead);
            for (int i = 0; i < detection.Text.Count; i++)
                if (readers[i] != MangaReader.Manga) passOn.Add((detection.Text[i], readers[i]));
            passOn.AddRange(detection.Long.Select(block => (block, MangaReader.Columns)));

            if (passOn.Count > 0 && slot is null)
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
                if (!TakesColumns(reader, groups)) continue;
                // Read by both: the column pipeline's reading wins, so manga-ocr's goes.
                if (reader == MangaReader.ColumnsAcross)
                {
                    read.RemoveAll(mangaRead => mangaRead.Bounds == block);
                    routes = routes with { AcrossKept = routes.AcrossKept + 1 };
                }
                passedOn.AddRange(groups.Select(group => Moved(Set(group, reader), crop.X, crop.Y)));
            }
        }

        detection.LogPage(routes);
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
    /// <item><b>Too long</b> (<see cref="MangaPageLayout.IsLong"/>): a row, or a long column with
    /// writing side by side in it. Decided from the page's pixels as the blocks are found, so such a
    /// block never comes here; the column pipeline's groups keep the directions it gives them, as
    /// they always have.</item>
    /// <item><b>Too many columns</b>: lines that run down in a box <see cref="WideColumnsRatio"/>
    /// times as wide as tall or more, squeezed across. vertical-image-ja5's balloon of sixteen short
    /// columns came back with its last sentences turned into hundreds of dots, and the overlay sized
    /// its type from that many characters — a third of the size of the writing. Set down — unless
    /// CRAFT found its columns, and then it is the manga models' after all, read cut
    /// (<see cref="ReadColumnsCut"/>).</item>
    /// <item><b>Not columns at all</b>: lines that run across in a box not wide enough to be a row
    /// (<see cref="MangaPageLayout.AcrossRatio"/>). It cannot read English, and read the nine-line
    /// caption of vertical-image-ja5 as sentences that are not on the page. Set across — if what the
    /// column pipeline reads out of it is Latin (<see cref="ReadsAcross"/>); manga-ocr's otherwise.</item>
    /// </list>
    ///
    /// <para>Everything else — and everything whose lines say nothing — is manga-ocr's, as it always
    /// was: on the transcribed corpora it reads vertical writing better than the column pipeline does.
    /// The direction the column pipeline is told to set a block in is the one its lines voted for, so
    /// a block sent across stays across in every frame however the column pipeline groups it.</para>
    /// </remarks>
    internal static MangaReader ReaderFor(RectangleF block, MangaLineDirection lines)
    {
        return lines switch
        {
            MangaLineDirection.Across when !MangaPageLayout.RunsAcross(block) => MangaReader.ColumnsAcross,
            MangaLineDirection.Down when block.Width >= WideColumnsRatio * block.Height => MangaReader.ColumnsDown,
            _ => MangaReader.Manga,
        };
    }

    /// <summary>
    /// Hands back to the manga models the blocks of too many columns that CRAFT found the columns of
    /// (<see cref="MangaColumnCuts"/>): they are read in pieces, and set down.
    /// </summary>
    /// <remarks>
    /// MEASURED on real columns set side by side two to four at a time, read whole by CPT-flex and
    /// cut: 21.6% of characters wrong against 3.6%, and on vertical-image-ja5's balloon of sixteen
    /// short columns 26 of 60 against one. Where CRAFT finds no columns the block stays with the
    /// column pipeline, as before.
    /// </remarks>
    internal static void ReadColumnsCut(MangaReader[] readers, IReadOnlyList<IReadOnlyList<RectangleF>?> pieces)
    {
        for (int i = 0; i < readers.Length; i++)
            if (readers[i] == MangaReader.ColumnsDown && pieces[i] is not null) readers[i] = MangaReader.Manga;
    }

    /// <summary>
    /// The blocks manga-ocr reads while the lines of the others are measured: those whose reader the
    /// lines cannot change, as many as fill whole batches.
    /// </summary>
    /// <remarks>
    /// <para>The lines are measured on the CPU and manga-ocr reads on the GPU, so reading these in
    /// the meantime takes the measuring out of the page's time — 20–37ms a pass on the ordinary
    /// vertical pages it applies to and 84 on a crowded one, about all of what it cost them. Nothing
    /// is read that would not have been, and nothing differently: the recognizer reads each row of a
    /// batch on its own, and over the vertical and comic corpora, and 55 framings each of seven
    /// pages, every page came out the same to the bit.</para>
    ///
    /// <para>Whole batches only (<see cref="MangaTextRecognizer.BatchCap"/>), so the page is read in
    /// as many batches as before: the rest, and the wide blocks whose lines leave them with
    /// manga-ocr, go in the last ones. None when no block is wide enough to be asked, since then
    /// there is nothing to wait for.</para>
    /// </remarks>
    internal static IReadOnlyList<int> ReadAhead(IReadOnlyList<RectangleF> blocks)
    {
        if (!blocks.Any(MangaLineGeometry.IsWide)) return [];
        var settled = Enumerable.Range(0, blocks.Count)
            .Where(i => !MangaLineGeometry.IsWide(blocks[i])
                && ReaderFor(blocks[i], MangaLineDirection.Unsure) == MangaReader.Manga)
            .ToList();
        return settled.Take(settled.Count / MangaTextRecognizer.BatchCap * MangaTextRecognizer.BatchCap).ToList();
    }

    /// <summary>
    /// The blocks manga-ocr reads once the lines are measured: its own that were not read ahead, and
    /// those sent across, for when they are not across after all (<see cref="ReadsAcross"/>).
    /// </summary>
    internal static List<int> MangaReads(IReadOnlyList<MangaReader> readers, IReadOnlyList<int> ahead) =>
        [.. Enumerable.Range(0, readers.Count)
            .Where(i => readers[i] is (MangaReader.Manga or MangaReader.ColumnsAcross) && !ahead.Contains(i))];

    /// <summary>
    /// Whether the column pipeline's groups stand for a block it was sent: always, but for a block
    /// sent across, which has to read as writing that runs across (<see cref="ReadsAcross"/>).
    /// </summary>
    internal static bool TakesColumns(MangaReader reader, IEnumerable<OcrTextBlock> groups) =>
        reader != MangaReader.ColumnsAcross || ReadsAcross(groups);

    /// <summary>The fewest Latin letters that let a block sent across be set across.</summary>
    internal const int MinLatinLetters = 3;

    /// <summary>
    /// Whether what the column pipeline read out of a block sent across is writing that runs across:
    /// half its characters or more Latin letters, and at least <see cref="MinLatinLetters"/> of them.
    /// Otherwise the block is manga-ocr's, read down.
    /// </summary>
    /// <remarks>
    /// <para>Vertical writing comes first on a vertical page. The lines vote across, and confidently,
    /// on vertical balloons of short columns with furigana beside them and on balloons the edge of
    /// the screen cuts down to two or four characters a column; set across, the overlay turns the
    /// sentence on its side and the column pipeline drops a column of it. MEASURED on the r4 corpus
    /// at five scales, 55 framings each of twelve pages, 788 frames of 197 pages cut as a scroll cuts
    /// them, and 13 framings of the page reported: the lines alone sent 23 vertical blocks across on
    /// the cut frames and 25 on that page; this, none on any set. It still sets 142 of the corpus's
    /// 185 blocks across that are — English, mostly. Japanese captions written across are read down
    /// again, as they were before the lines were asked.</para>
    ///
    /// <para>A letter or two is noise the recognizer finds in kana; three is a word.</para>
    /// </remarks>
    internal static bool ReadsAcross(IEnumerable<OcrTextBlock> groups)
    {
        int characters = 0, latin = 0;
        foreach (var c in groups.SelectMany(group => group.Text))
        {
            if (char.IsWhiteSpace(c)) continue;
            characters++;
            if (char.IsAsciiLetter(c)) latin++;
        }
        return latin >= MinLatinLetters && latin * 2 >= characters;
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

/// <summary>
/// How many of a page's blocks went to the column pipeline, for each reason but length, for the log:
/// too many columns that CRAFT found none of (<see cref="Wide"/>) or found leaning
/// (<see cref="Leaning"/>), and lines across, of which <see cref="AcrossKept"/> read as Latin.
/// </summary>
internal readonly record struct MangaRoutes(int Wide, int Leaning, int AcrossKept, int AcrossSent)
{
    internal static MangaRoutes Of(IReadOnlyList<MangaReader> readers, IReadOnlySet<int> leaning)
    {
        int wide = 0, leans = 0, across = 0;
        for (int i = 0; i < readers.Count; i++)
        {
            if (readers[i] == MangaReader.ColumnsDown)
            {
                if (leaning.Contains(i)) leans++;
                else wide++;
            }
            else if (readers[i] == MangaReader.ColumnsAcross)
                across++;
        }
        return new MangaRoutes(wide, leans, 0, across);
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
