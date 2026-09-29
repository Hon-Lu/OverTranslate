using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// manga-ocr (kha-white/manga-ocr-base): a ViT encoder and a two-layer BERT decoder, read greedily
/// on DirectML with the decoder's key/value cache kept on the GPU between steps.
/// </summary>
/// <remarks>
/// <para>The decoder is our own export in two graphs. <c>decoder_cross</c> turns the encoder output
/// into both layers' cross-attention keys and values once per batch; <c>decoder_step</c> takes one
/// token, the past self-attention cache and that cross cache, and returns the next logits and the grown
/// cache. No published manga-ocr export has a past input — the ones labelled "merged" re-run the whole
/// prefix every step — and this one reads the same text as the uncached decoder on all 442 blocks of
/// the three transcribed corpora.</para>
///
/// <para>The cache stays on the device through IOBinding, and only the logits come back each step.
/// Without that, DirectML uploads the cross cache from main memory on every step and a step costs
/// more than it does on the CPU; with it, recognition is 25–30% faster than the uncached decoder.</para>
///
/// <para>Batches of at most <see cref="BatchCap"/>. A batch waits for its longest block, so a big
/// batch spends steps on blocks that have already finished; measured per page, 4 was both the fastest
/// cap and all but the smallest in video memory (773MB at the peak; no cap 1133MB, 2 was 70MB less
/// and 15–35% slower). Greedy rather than beam: beam 2 and 4 changed only punctuation and took 1.5 to
/// 2.9 times as long.</para>
///
/// <para>Every batch is exactly <see cref="BatchCap"/> rows, a short one padded with rows that are
/// finished before the first step. A DirectML session is fast only at the batch size it first ran:
/// MEASURED on this machine, a session that first saw 4 rows took 3.6ms a decoder step and 13ms for the
/// encoder at 4 rows, and 13ms and 45–50ms at 1, 2 or 3; one that first saw 1 row was fast at 1 and
/// slow at 4, and the slow path stays slow for the life of the session. The Python bench this was
/// measured on happened to open with a batch of 4. Here a first page with fewer blocks pinned the fast
/// path to the wrong size and every page after it ran at twice the time. Padding costs the rows'
/// compute on the last batch of a page and makes every batch the fast one.</para>
/// </remarks>
internal sealed class MangaTextRecognizer : IDisposable
{
    internal const int BatchCap = 4;
    private const int InputSize = 224;
    private const int MaxTokens = 300;
    private const long StartToken = 2;
    private const long EndToken = 3;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _cross;
    private readonly InferenceSession _step;
    private readonly OrtMemoryInfo _device;
    private readonly string[] _vocabulary;

    internal MangaTextRecognizer(
        string encoderPath, string crossPath, string stepPath, string vocabularyPath,
        Func<SessionOptions> options, int deviceId)
    {
        _vocabulary = File.ReadAllLines(vocabularyPath);
        try
        {
            _encoder = Open(encoderPath, options);
            _cross = Open(crossPath, options);
            _step = Open(stepPath, options);
            _device = new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, deviceId, OrtMemType.Default);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static InferenceSession Open(string path, Func<SessionOptions> options)
    {
        using var opened = options();
        return new InferenceSession(path, opened);
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _cross?.Dispose();
        _step?.Dispose();
        _device?.Dispose();
    }

    /// <summary>Reads each block of the page, in the order given.</summary>
    /// <param name="rgb">The whole page as packed RGB (see <see cref="PillowImage.Rgb"/>).</param>
    internal List<MangaReading> Read(byte[] rgb, int width, int height, IReadOnlyList<Rectangle> blocks)
    {
        var readings = new List<MangaReading>(blocks.Count);
        for (int first = 0; first < blocks.Count; first += BatchCap)
            readings.AddRange(ReadBatch(rgb, width, height, blocks.Skip(first).Take(BatchCap).ToList()));
        return readings;
    }

    private List<MangaReading> ReadBatch(byte[] rgb, int width, int height, List<Rectangle> blocks)
    {
        int real = blocks.Count;
        int n = BatchCap;
        int plane = InputSize * InputSize;
        // Padding rows stay 0: mid-grey after normalisation, and never read.
        var pixels = new float[n * 3 * plane];
        for (int i = 0; i < real; i++)
        {
            // Grey first and then resized, as manga-ocr's own preprocessing does; the three channels
            // the encoder takes are the same grey three times.
            var grey = PillowImage.Resize(
                PillowImage.Luma(Crop(rgb, width, blocks[i])), blocks[i].Width, blocks[i].Height, 1,
                InputSize, InputSize);
            int offset = i * 3 * plane;
            for (int p = 0; p < plane; p++)
            {
                float v = (grey[p] / 255f - 0.5f) / 0.5f;
                pixels[offset + p] = v;
                pixels[offset + plane + p] = v;
                pixels[offset + 2 * plane + p] = v;
            }
        }

        using var run = new RunOptions();
        using var pixelValues = OrtValue.CreateTensorValueFromMemory(pixels, [n, 3, InputSize, InputSize]);
        using var encoded = _encoder.Run(run, ["pixel_values"], [pixelValues], _encoder.OutputNames);

        using var crossBinding = _cross.CreateIoBinding();
        crossBinding.BindInput("encoder_hidden_states", encoded[0]);
        crossBinding.BindOutputToDevice("cross_k", _device);
        crossBinding.BindOutputToDevice("cross_v", _device);
        _cross.RunWithBinding(run, crossBinding);
        using var crossCache = crossBinding.GetOutputValues();
        var crossKeys = crossCache[0];
        var crossValues = crossCache[1];

        // [layers, batch, heads, 0, head size]: the first step has no past, and DirectML runs it.
        var shape = crossKeys.GetTensorTypeAndShape().Shape;
        long[] emptyShape = [shape[0], n, shape[2], 0, shape[4]];
        OrtValue pastKeys = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), emptyShape);
        OrtValue pastValues = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), emptyShape);
        IDisposableReadOnlyCollection<OrtValue>? previous = null;

        var ids = new long[n];
        var positions = new long[n];
        var done = new bool[n];
        var tokens = Enumerable.Range(0, n).Select(_ => new List<long>()).ToArray();
        var certainty = new double[n];
        ids.AsSpan().Fill(StartToken);
        for (int i = real; i < n; i++)
        {
            done[i] = true;
            ids[i] = 0;
        }
        try
        {
            for (int step = 0; step < MaxTokens; step++)
            {
                positions.AsSpan().Fill(step);
                using var idValue = OrtValue.CreateTensorValueFromMemory(ids, [n, 1]);
                using var positionValue = OrtValue.CreateTensorValueFromMemory(positions, [n, 1]);
                using var binding = _step.CreateIoBinding();
                binding.BindInput("input_ids", idValue);
                binding.BindInput("position_ids", positionValue);
                binding.BindInput("past_k", pastKeys);
                binding.BindInput("past_v", pastValues);
                binding.BindInput("cross_k", crossKeys);
                binding.BindInput("cross_v", crossValues);
                binding.BindOutputToDevice("logits", OrtMemoryInfo.DefaultInstance);
                binding.BindOutputToDevice("present_k", _device);
                binding.BindOutputToDevice("present_v", _device);
                _step.RunWithBinding(run, binding);

                var outputs = binding.GetOutputValues();
                if (previous is null)
                {
                    pastKeys.Dispose();
                    pastValues.Dispose();
                }
                else
                {
                    previous.Dispose();
                }

                previous = outputs;
                pastKeys = outputs[1];
                pastValues = outputs[2];

                var logits = outputs[0].GetTensorDataAsSpan<float>();
                int vocabulary = logits.Length / n;
                bool allDone = true;
                for (int i = 0; i < n; i++)
                {
                    long best = 0;
                    if (!done[i])
                    {
                        var row = logits.Slice(i * vocabulary, vocabulary);
                        float top = float.MinValue;
                        for (int v = 0; v < row.Length; v++)
                        {
                            if (row[v] > top)
                            {
                                top = row[v];
                                best = v;
                            }
                        }

                        double total = 0;
                        for (int v = 0; v < row.Length; v++)
                            total += Math.Exp(row[v] - top);
                        certainty[i] += 1.0 / total;

                        if (best == EndToken) done[i] = true;
                        else tokens[i].Add(best);
                    }

                    ids[i] = done[i] ? 0 : best;
                    allDone &= done[i];
                }

                if (allDone) break;
            }
        }
        finally
        {
            if (previous is null)
            {
                pastKeys.Dispose();
                pastValues.Dispose();
            }
            else
            {
                previous.Dispose();
            }
        }

        return tokens
            .Take(real)
            .Select((row, i) => new MangaReading(Decode(row), certainty[i] / (row.Count + (done[i] ? 1 : 0))))
            .ToList();
    }

    private string Decode(List<long> row)
    {
        var text = new StringBuilder();
        foreach (var id in row)
        {
            if (id == 0) break;
            var token = _vocabulary[id];
            if (token.StartsWith('[') && token.EndsWith(']')) continue;
            text.Append(token);
        }

        return PostProcess(text.Replace("##", "").ToString());
    }

    /// <summary>manga-ocr's own <c>post_process</c>.</summary>
    internal static string PostProcess(string text)
    {
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        text = text.Replace("…", "...");
        text = DotRuns.Replace(text, match => new string('.', match.Length));
        return text.Normalize(NormalizationForm.FormKC);
    }

    private static byte[] Crop(byte[] rgb, int width, Rectangle block)
    {
        var crop = new byte[block.Width * block.Height * 3];
        for (int y = 0; y < block.Height; y++)
            Buffer.BlockCopy(rgb, ((block.Y + y) * width + block.X) * 3, crop, y * block.Width * 3, block.Width * 3);
        return crop;
    }

    private static readonly Regex DotRuns = new("[・.]{2,}", RegexOptions.CultureInvariant);
}

/// <summary>One block's text, and the mean probability the decoder gave the tokens it chose.</summary>
internal readonly record struct MangaReading(string Text, double Confidence);
