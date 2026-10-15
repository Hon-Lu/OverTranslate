using System.Drawing;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Hayai OCR v2.5 Nova (JustANormalTinkerer/hayai-ocr-v2.5-nova): a SigLIP2 NaFlex encoder and a
/// twelve-layer decoder, read greedily on DirectML with the decoder's cache kept on the GPU — the
/// alternative to <see cref="MangaTextRecognizer"/> chosen by <see cref="MangaRecognizerChoice"/>.
/// </summary>
/// <remarks>
/// <para>Three fixed-shape fp16 graphs exported for a batch of <see cref="BatchCap"/>: <c>vision</c>
/// (patches to 192 vision tokens), <c>prefill</c> (vision tokens and the start token to the first
/// logits and a cache of <see cref="CacheLength"/> slots per layer) and <c>step</c> (one token in,
/// written into its slot by a one-hot mask). <c>prefill</c> and <c>step</c> share one weight file.
/// Everything whose shape depends on the block is worked out on the host: see
/// <see cref="HayaiInputs"/>.</para>
///
/// <para>MEASURED by the survey on the shared manga-ocr evaluation set (findings-B1), against the
/// current reader: character error rate 1.38% against 1.92% at the page's own size and 1.79%
/// against 3.50% at half, mostly on art lettering, horizontal blocks, Latin letters and small
/// type; worse on clean dialogue at full size (0.91% against 0.41%).</para>
///
/// <para>The cache is two sets of device buffers, read from one and written to the other in turn,
/// so a step uploads only its token, angles and masks and downloads only the logits. A batch is
/// always <see cref="BatchCap"/> rows — the graphs are fixed at that — a short one padded with
/// copies of its last block that are finished before the first step.</para>
/// </remarks>
internal sealed class HayaiTextRecognizer : IMangaTextRecognizer
{
    internal const int BatchCap = 4;
    /// <summary>The most tokens a block is read as; fixed in the step graph's cache.</summary>
    internal const int MaxTokens = 160;
    private const int Prefix = HayaiInputs.MaxVisionTokens + 1;
    internal const int CacheLength = Prefix + MaxTokens;
    private const int Layers = 12;
    private const int KvHeads = 2;
    private const int HeadDim = 64;
    private const int VisionDim = 512;
    private const long PadToken = 16000;
    private const long StartToken = 16001;
    private const long EndToken = 16002;
    // fp16: float.MinValue would be -inf, and a row masked everywhere would be NaN.
    private const float Masked = -1e4f;

    internal static readonly string[] Files =
        ["vision.onnx", "prefill.onnx", "step.onnx", "decoder.weights", "pos_grid.bin", "vocab.txt"];

    private static readonly Float16 Zero = (Float16)0f;
    private static readonly Float16 One = (Float16)1f;
    private static readonly Float16 Off = (Float16)Masked;

    private readonly InferenceSession _vision;
    private readonly InferenceSession _prefill;
    private readonly InferenceSession _step;
    private readonly OrtMemoryInfo _device;
    private readonly OrtAllocator _allocator;
    private readonly OrtValue[][] _cache;
    private readonly string[] _cacheNames;
    private readonly float[] _grid;
    private readonly HayaiVocabulary _vocabulary;
    private readonly Dictionary<(int, int), Float16[]> _positions = [];

    internal HayaiTextRecognizer(string folder, Func<SessionOptions> options, int deviceId)
    {
        var tokens = File.ReadAllText(Path.Combine(folder, "vocab.txt")).Split('\n');
        _vocabulary = new HayaiVocabulary(tokens, [0, 1, 2, 3, 16000, 16001, 16002, 16003]);
        var raw = File.ReadAllBytes(Path.Combine(folder, "pos_grid.bin"));
        _grid = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, _grid, 0, raw.Length);
        _cacheNames = [.. Enumerable.Range(0, Layers).Select(i => $"k{i}"), .. Enumerable.Range(0, Layers).Select(i => $"v{i}")];
        try
        {
            _vision = Open(Path.Combine(folder, "vision.onnx"), options);
            _prefill = Open(Path.Combine(folder, "prefill.onnx"), options);
            _step = Open(Path.Combine(folder, "step.onnx"), options);
            _device = new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, deviceId, OrtMemType.Default);
            _allocator = new OrtAllocator(_prefill, _device);
            long[] shape = [BatchCap, KvHeads, CacheLength, HeadDim];
            _cache =
            [
                [.. _cacheNames.Select(_ => OrtValue.CreateAllocatedTensorValue(_allocator, TensorElementType.Float16, shape))],
                [.. _cacheNames.Select(_ => OrtValue.CreateAllocatedTensorValue(_allocator, TensorElementType.Float16, shape))],
            ];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether every file the recognizer needs is in <paramref name="folder"/>.</summary>
    internal static bool IsComplete(string folder) => Files.All(name => File.Exists(Path.Combine(folder, name)));

    private static InferenceSession Open(string path, Func<SessionOptions> options)
    {
        using var opened = options();
        return new InferenceSession(path, opened);
    }

    public void Dispose()
    {
        if (_cache is not null)
            foreach (var set in _cache)
                foreach (var value in set ?? [])
                    value?.Dispose();
        _allocator?.Dispose();
        _vision?.Dispose();
        _prefill?.Dispose();
        _step?.Dispose();
        _device?.Dispose();
    }

    public List<MangaReading> Read(byte[] rgb, int width, int height, IReadOnlyList<Rectangle> blocks)
    {
        var readings = new List<MangaReading>(blocks.Count);
        for (int first = 0; first < blocks.Count; first += BatchCap)
            readings.AddRange(ReadBatch(rgb, width, blocks.Skip(first).Take(BatchCap).ToList()));
        return readings;
    }

    private List<MangaReading> ReadBatch(byte[] rgb, int width, List<Rectangle> blocks)
    {
        int real = blocks.Count, n = BatchCap;
        const int pixelRow = HayaiInputs.MaxPatches * HayaiInputs.PatchValues;
        const int angles = HayaiInputs.RopeDim;
        var pixels = new Float16[n * pixelRow];
        var pixelMask = new long[n * HayaiInputs.MaxPatches];
        var positions = new Float16[n * pixelRow];
        var gather = new long[n * HayaiInputs.MaxVisionTokens * 4];
        var cos = new Float16[n * Prefix * angles];
        var sin = new Float16[n * Prefix * angles];
        var mask = new Float16[n * Prefix * Prefix];
        var stepMask = new Float16[n * CacheLength];
        var scratch = new float[pixelRow];
        Span<double> angle = stackalloc double[angles];

        for (int b = 0; b < n; b++)
        {
            // Padding rows read the last block again: a copy of a real block keeps every row's
            // attention well defined, and its answer is never used.
            var block = blocks[Math.Min(b, real - 1)];
            var (th, tw) = HayaiInputs.Size(block.Height, block.Width);
            var resized = PillowImage.Resize(
                MangaTextRecognizer.Crop(rgb, width, block), block.Width, block.Height, 3, tw, th);
            int rows = th / HayaiInputs.PatchSize, columns = tw / HayaiInputs.PatchSize;
            HayaiInputs.Patches(resized, th, tw, scratch);
            for (int i = 0; i < pixelRow; i++) pixels[b * pixelRow + i] = (Float16)scratch[i];
            for (int p = 0; p < rows * columns; p++) pixelMask[b * HayaiInputs.MaxPatches + p] = 1;
            Array.Copy(PositionsFor(rows, columns), 0, positions, b * pixelRow, pixelRow);
            Array.Copy(HayaiInputs.Gather(rows, columns), 0, gather, b * HayaiInputs.MaxVisionTokens * 4,
                HayaiInputs.MaxVisionTokens * 4);

            var (mergedRows, mergedColumns) = HayaiInputs.Merged(rows, columns);
            int vision = mergedRows * mergedColumns;
            int ro = b * Prefix * angles;
            cos.AsSpan(ro, Prefix * angles).Fill(One);
            sin.AsSpan(ro, Prefix * angles).Fill(Zero);
            for (int t = 0; t < vision; t++)
            {
                HayaiInputs.VisionAngles(t, mergedColumns, angle);
                for (int k = 0; k < angles; k++)
                {
                    cos[ro + t * angles + k] = (Float16)(float)Math.Cos(angle[k]);
                    sin[ro + t * angles + k] = (Float16)(float)Math.Sin(angle[k]);
                }
            }

            // The start token sits at the last prefix row with angle 0: cos 1, sin 0, as filled.
            int mo = b * Prefix * Prefix;
            mask.AsSpan(mo, Prefix * Prefix).Fill(Off);
            for (int i = 0; i < vision; i++)
                mask.AsSpan(mo + i * Prefix, vision).Fill(Zero);
            int start = HayaiInputs.MaxVisionTokens;
            mask.AsSpan(mo + start * Prefix, vision).Fill(Zero);
            mask[mo + start * Prefix + start] = Zero;
            // Unused vision rows see only themselves, so none is masked everywhere.
            for (int j = vision; j < start; j++) mask[mo + j * Prefix + j] = Zero;

            int so = b * CacheLength;
            stepMask.AsSpan(so, CacheLength).Fill(Off);
            stepMask.AsSpan(so, vision).Fill(Zero);
            stepMask[so + start] = Zero;
        }

        using var run = new RunOptions();
        using var pixelValue = OrtValue.CreateTensorValueFromMemory(pixels, [n, HayaiInputs.MaxPatches, HayaiInputs.PatchValues]);
        using var pixelMaskValue = OrtValue.CreateTensorValueFromMemory(pixelMask, [n, HayaiInputs.MaxPatches]);
        using var positionValue = OrtValue.CreateTensorValueFromMemory(positions, [n, HayaiInputs.MaxPatches, HayaiInputs.Width]);
        using var gatherValue = OrtValue.CreateTensorValueFromMemory(gather, [n, HayaiInputs.MaxVisionTokens * 4]);
        using var visionBinding = _vision.CreateIoBinding();
        visionBinding.BindInput("pixel_values", pixelValue);
        visionBinding.BindInput("pixel_mask", pixelMaskValue);
        visionBinding.BindInput("pos", positionValue);
        visionBinding.BindInput("gidx", gatherValue);
        visionBinding.BindOutputToDevice("vis_emb", _device);
        _vision.RunWithBinding(run, visionBinding);
        using var visionOut = visionBinding.GetOutputValues();

        var ids = new long[n];
        ids.AsSpan().Fill(StartToken);
        using var startValue = OrtValue.CreateTensorValueFromMemory(ids, [n, 1]);
        using var cosValue = OrtValue.CreateTensorValueFromMemory(cos, [n, Prefix, angles]);
        using var sinValue = OrtValue.CreateTensorValueFromMemory(sin, [n, Prefix, angles]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [n, 1, Prefix, Prefix]);
        using var prefillBinding = _prefill.CreateIoBinding();
        prefillBinding.BindInput("vis_emb", visionOut[0]);
        prefillBinding.BindInput("bos", startValue);
        prefillBinding.BindInput("cos", cosValue);
        prefillBinding.BindInput("sin", sinValue);
        prefillBinding.BindInput("mask", maskValue);
        prefillBinding.BindOutputToDevice("logits", OrtMemoryInfo.DefaultInstance);
        for (int i = 0; i < _cacheNames.Length; i++) prefillBinding.BindOutput(_cacheNames[i], _cache[0][i]);
        _prefill.RunWithBinding(run, prefillBinding);
        var logits = prefillBinding.GetOutputValues();

        var done = new bool[n];
        for (int i = real; i < n; i++) done[i] = true;
        var tokens = Enumerable.Range(0, n).Select(_ => new List<long>()).ToArray();
        var certainty = new double[n];
        var next = new long[n];
        var stepCos = new Float16[n * angles];
        var stepSin = new Float16[n * angles];
        var oneHot = new Float16[n * CacheLength];
        int current = 0;
        try
        {
            for (int step = 1; ; step++)
            {
                var span = logits[0].GetTensorDataAsSpan<float>();
                int vocabulary = span.Length / n;
                bool allDone = true;
                for (int i = 0; i < n; i++)
                {
                    var row = span.Slice(i * vocabulary, vocabulary);
                    long best = 0;
                    float top = float.MinValue;
                    for (int v = 0; v < row.Length; v++)
                    {
                        if (row[v] > top)
                        {
                            top = row[v];
                            best = v;
                        }
                    }

                    next[i] = best;
                    if (done[i]) continue;
                    double total = 0;
                    for (int v = 0; v < row.Length; v++) total += Math.Exp(row[v] - top);
                    certainty[i] += 1.0 / total;
                    if (best is EndToken or PadToken) done[i] = true;
                    else tokens[i].Add(best);
                    allDone &= done[i];
                }

                logits.Dispose();
                logits = null!;
                if (allDone || step == MaxTokens) break;

                int slot = Prefix + step - 1;
                HayaiInputs.TextAngles(step, angle);
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < angles; k++)
                    {
                        stepCos[i * angles + k] = (Float16)(float)Math.Cos(angle[k]);
                        stepSin[i * angles + k] = (Float16)(float)Math.Sin(angle[k]);
                    }

                    stepMask[i * CacheLength + slot] = Zero;
                    oneHot.AsSpan(i * CacheLength, CacheLength).Fill(Zero);
                    oneHot[i * CacheLength + slot] = One;
                }

                using var tokenValue = OrtValue.CreateTensorValueFromMemory(next, [n, 1]);
                using var stepCosValue = OrtValue.CreateTensorValueFromMemory(stepCos, [n, 1, angles]);
                using var stepSinValue = OrtValue.CreateTensorValueFromMemory(stepSin, [n, 1, angles]);
                using var stepMaskValue = OrtValue.CreateTensorValueFromMemory(stepMask, [n, 1, 1, CacheLength]);
                using var oneHotValue = OrtValue.CreateTensorValueFromMemory(oneHot, [n, 1, CacheLength, 1]);
                using var binding = _step.CreateIoBinding();
                binding.BindInput("tok", tokenValue);
                binding.BindInput("cos", stepCosValue);
                binding.BindInput("sin", stepSinValue);
                binding.BindInput("mask", stepMaskValue);
                binding.BindInput("onehot", oneHotValue);
                // Logits first: the outputs come back in the order they were bound.
                binding.BindOutputToDevice("logits", OrtMemoryInfo.DefaultInstance);
                for (int i = 0; i < _cacheNames.Length; i++)
                {
                    binding.BindInput("p" + _cacheNames[i], _cache[current][i]);
                    binding.BindOutput(_cacheNames[i], _cache[1 - current][i]);
                }

                _step.RunWithBinding(run, binding);
                logits = binding.GetOutputValues();
                current = 1 - current;
            }
        }
        finally
        {
            logits?.Dispose();
        }

        return tokens
            .Take(real)
            .Select((row, i) => new MangaReading(
                MangaTextRecognizer.PostProcess(_vocabulary.Decode(row)),
                certainty[i] / (row.Count + (done[i] ? 1 : 0))))
            .ToList();
    }

    private Float16[] PositionsFor(int rows, int columns)
    {
        if (!_positions.TryGetValue((rows, columns), out var cached))
        {
            var exact = HayaiInputs.Positions(_grid, rows, columns);
            cached = new Float16[exact.Length];
            for (int i = 0; i < exact.Length; i++) cached[i] = (Float16)exact[i];
            _positions[(rows, columns)] = cached;
        }

        return cached;
    }
}
