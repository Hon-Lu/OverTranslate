using RapidOcrNet;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// A second look at the full-size kana of a horizontal Japanese line, with room above and below
/// it, so a small っ ゃ ゅ ょ ぁ … the first reading took for its full-size twin can be told apart
/// (#412).
/// </summary>
/// <remarks>
/// <para>A small kana is the same shape as its twin, only smaller and lower. The recogniser scales
/// every crop to one height, so on a line whose box hugs the ink the small one comes out as tall
/// as the rest and the size cue is gone: つ for っ, や for ゃ, イ for ィ.</para>
///
/// <para>MEASURED on 295 Japanese captures (subtitle, game, video, web, interface; five detector
/// sizes each, the realtime ones, 60% and the screenshot one; 15748 lines). Simply giving every
/// crop more room does not work — it changes far more than the kana, and the kana both ways:</para>
/// <code>
///   for every crop                 lines changed   lines lost   made small   made full-size
///   crop 4px more each side             22%            519          46            44
///   crop 15% of the height more         22%            453          52            39
///   background rows, 10% / 20%        22% / 27%     (213 captures)
/// </code>
/// <para>Cropping further brings in strokes of the neighbouring lines (dense web text read as
/// "Tun on to k the orina"), and even plain background rows shrink the glyphs enough to change
/// letters and spacing elsewhere. The Stardew Valley line-initial ゃ of #412 already has room
/// around it and only gets worse with more: it is the line start, not the crop.</para>
///
/// <para>So the room is only consulted for the one thing it is good at. The line is read again
/// with background rows above and below, and the first reading is kept except where the second
/// one says a full-size kana is its small twin. Never the other way: a crop with room has no
/// reason to make a small kana look full-size, a crop without it does. See
/// <see cref="Merge"/> for the numbers this rule produced.</para>
///
/// <para>Only for lines read as Japanese (or with the language left to the app), only for
/// horizontal crops, and only for lines whose first reading has a kana with a small twin in it.
/// The vertical (manga) path has its own recogniser and is not touched. With the language left
/// to the app, 164 English, Korean and Chinese captures read exactly as before.</para>
///
/// <para>COST, MEASURED on 110 Japanese subtitle regions at the realtime size, one process on an
/// idle machine: recognition 62ms → 78ms at the median, 152 → 233ms at the 90th percentile, at
/// most +91ms. There 23% of the lines, 40% of the characters, were read again. Leaving the
/// hiragana vowels out of the trigger would read about half as much, but then はぁ and ぎゃあぁ
/// would be fixed only on lines that happen to hold a つ or a や as well.</para>
/// </remarks>
internal static class SmallKanaReread
{
    // Twins at the same index. ヵ ヶ are small forms of カ ケ in counters (一ヶ月).
    private const string Large = "つやゆよあいうえおわツヤユヨアイウエオワカケ";
    private const string Small = "っゃゅょぁぃぅぇぉゎッャュョァィゥェォヮヵヶ";

    /// <summary>
    /// Background rows added above and below, as a fraction of the crop's height.
    /// </summary>
    /// <remarks>MEASURED on 206 of the captures: 0.10 made 15 kana small, 0.15 and 0.20 made 25,
    /// 0.30 made 19. #398's っていうか needs 0.15 (0.10 read it on 4 of 8 framings). Settable for
    /// OcrHarness.</remarks>
    internal static double MarginRatio { get; set; } = 0.15;

    /// <summary>Off switch for OcrHarness, to read the same session with and without.</summary>
    internal static bool Enabled { get; set; } = true;

    internal static bool AppliesTo(string normalizedLanguage) =>
        Enabled && (normalizedLanguage == "JA" || OcrLanguageRouter.UsesAutomaticLayout(normalizedLanguage));

    /// <summary>
    /// The first <paramref name="count"/> horizontal crops whose reading has a full-size kana with a
    /// small twin, read again with room above and below: at the same index as in
    /// <paramref name="lines"/>, null where a crop was not read again. Null when none was.
    /// </summary>
    /// <remarks>The caller merges with <see cref="Merge"/> once it knows which reading a line keeps:
    /// merging first would let the kana alone make two readings of one line disagree (a grown box
    /// and the detector's own), and the line-end walk would then throw the grown one away.
    /// MEASURED: merging first lost the く of 恐怖映像じゃなく at one detector size.</remarks>
    internal static TextLine?[]? Reread(
        TextRecognizer recognizer, IReadOnlyList<SKBitmap> parts, TextLine[] lines, int count)
    {
        var picked = new List<int>();
        for (var i = 0; i < count && i < parts.Count && i < lines.Length; i++)
            if (parts[i].Width >= parts[i].Height && lines[i].Chars is { } chars && chars.Any(HasSmallTwin))
                picked.Add(i);
        if (picked.Count == 0) return null;

        var padded = picked
            .Select(i => WithMargins(parts[i], Math.Max(1, (int)Math.Round(parts[i].Height * MarginRatio))))
            .ToArray();
        try
        {
            var again = recognizer.GetTextLines(padded);
            var result = new TextLine?[lines.Length];
            for (var k = 0; k < picked.Count; k++)
                result[picked[k]] = again[k];
            return result;
        }
        finally
        {
            foreach (var part in padded) part.Dispose();
        }
    }

    /// <summary>
    /// The first reading with every full-size kana the second reading has as its small twin, at the
    /// same position, taken from the second; null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>Position by position and only between readings of the same length, so nothing but the
    /// kana's size can change: a second reading that differs in any other way still contributes its
    /// small kana where the two line up, and nothing else.</para>
    ///
    /// <para>MEASURED on 382 Japanese captures at five detector sizes, 20311 lines: 63 kana made
    /// small, each judged against the image. 60 right (でしよ→でしょ, ぎやああ→ぎゃああぁ,
    /// チヤンネル→チャンネル, ディヴイ→ディヴィ, じやなく→じゃなく, ドテつ腹→ドテっ腹, セーニヤ→セーニャ,
    /// #398's つていうか→っていうか …), 3 wrong: エンディング at the start of a line, where nothing
    /// before it can say, and the ツ of クリスマスツリー twice, where スッ is as good a pair as スツ.
    /// No line read differently in any other way, no line lost. On the auto-probe corpus 7 of 591
    /// readings changed, all of them right.</para>
    /// </remarks>
    internal static (string[] Chars, float[] Scores)? Merge(
        string[]? first, float[]? firstScores, string[]? second, float[]? secondScores)
    {
        if (first is null || second is null || firstScores is null || secondScores is null ||
            first.Length != second.Length || firstScores.Length != first.Length || secondScores.Length != second.Length)
            return null;

        string[]? chars = null;
        float[]? scores = null;
        for (var j = 0; j < first.Length; j++)
        {
            if (first[j].Length != 1 || second[j].Length != 1) continue;
            var at = Large.IndexOf(first[j][0]);
            if (at < 0 || Small[at] != second[j][0]) continue;
            if (j > 0 && !Joins(second[j][0], first[j - 1])) continue;
            chars ??= (string[])first.Clone();
            scores ??= (float[])firstScores.Clone();
            chars[j] = second[j];
            scores[j] = secondScores[j];
        }

        return chars is null ? null : (chars, scores!);
    }

    private static bool HasSmallTwin(string c) => c.Length == 1 && Large.Contains(c[0]);

    /// <summary>
    /// Whether the kana before can take this small kana: ゃ ゅ ょ only join an i-row kana (きゃ, チュ,
    /// and テュ デュ フュ ヴュ in katakana), a small katakana vowel only the pairs loanwords are written
    /// with (ファ, ティ, ウィ, トゥ, シェ …). っ, the hiragana vowels (はぁ, ねぇ) and ゎ ヵ ヶ go anywhere.
    /// </summary>
    /// <remarks>MEASURED: the one wrong change on the auto-probe corpus was ポップアップ read as
    /// ポップァップ, ァ after プ. A rule of how kana are written, not a word list: it names no word,
    /// and every right change measured passes it.</remarks>
    private static bool Joins(char small, string before)
    {
        if (before.Length != 1) return true;
        var b = before[0];
        return small switch
        {
            'ゃ' or 'ゅ' or 'ょ' => "きしちにひみりぎじぢびぴ".Contains(b),
            'ャ' or 'ュ' or 'ョ' => "キシチニヒミリギジヂビピテデフヴ".Contains(b),
            'ァ' => "フヴツクグ".Contains(b),
            'ィ' => "テデフヴウツスズクグキ".Contains(b),
            'ゥ' => "トドクグ".Contains(b),
            'ェ' => "シジチフヴウツイキギニヒミリビピテデ".Contains(b),
            'ォ' => "フヴウツクグ".Contains(b),
            _ => true,
        };
    }

    /// <summary>
    /// A copy of the crop with <paramref name="by"/> rows of its background above and below.
    /// </summary>
    /// <remarks>Rows of background rather than more of the frame: the frame above and below a line
    /// is often the next line, and its strokes change the reading. The background is the
    /// per-channel median of the crop's outermost pixels, which are mostly not ink.</remarks>
    internal static SKBitmap WithMargins(SKBitmap part, int by)
    {
        var result = new SKBitmap(new SKImageInfo(part.Width, part.Height + 2 * by, part.ColorType, part.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(BackgroundOf(part));
        canvas.DrawBitmap(part, 0, by);
        return result;
    }

    private static SKColor BackgroundOf(SKBitmap part)
    {
        var border = new List<SKColor>(2 * (part.Width + part.Height));
        for (var x = 0; x < part.Width; x++)
        {
            border.Add(part.GetPixel(x, 0));
            border.Add(part.GetPixel(x, part.Height - 1));
        }
        for (var y = 0; y < part.Height; y++)
        {
            border.Add(part.GetPixel(0, y));
            border.Add(part.GetPixel(part.Width - 1, y));
        }

        byte Median(Func<SKColor, byte> channel)
        {
            var values = border.Select(channel).OrderBy(v => v).ToList();
            return values[values.Count / 2];
        }

        return new SKColor(Median(c => c.Red), Median(c => c.Green), Median(c => c.Blue));
    }
}
