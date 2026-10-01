// Navier-Stokes inpainting (Bertalmio, Bertozzi and Sapiro 2001) for 8-bit BGR images.
//
// Ported from OpenCV 4.13.0, modules/photo/src/inpaint.cpp (icvInpaint, icvNSInpaintFMM,
// FastMarching_solve and CvPriorityQueueFloat): https://github.com/opencv/opencv/blob/4.13.0/modules/photo/src/inpaint.cpp
// OpenCV is licensed under the Apache License, Version 2.0. That file also carries the original
// Intel License Agreement for Open Source Computer Vision Library, which permits redistribution in
// source and binary forms provided its copyright notice, conditions and disclaimer are retained:
//
//   Copyright (C) 2000, Intel Corporation, all rights reserved.
//   Redistribution and use in source and binary forms, with or without modification, are permitted
//   provided that the following conditions are met: redistributions of source code must retain the
//   above copyright notice, this list of conditions and the following disclaimer; redistributions in
//   binary form must reproduce the above copyright notice, this list of conditions and the following
//   disclaimer in the documentation and/or other materials provided with the distribution; the name
//   of Intel Corporation may not be used to endorse or promote products derived from this software
//   without specific prior written permission. This software is provided by the copyright holders and
//   contributors "as is" and any express or implied warranties, including, but not limited to, the
//   implied warranties of merchantability and fitness for a particular purpose are disclaimed. In no
//   event shall the Intel Corporation or contributors be liable for any direct, indirect, incidental,
//   special, exemplary, or consequential damages however caused and on any theory of liability.
//
// Kept exactly as upstream: the order pixels leave the heap (arrival time, then insertion order),
// the uneven km/kp/lm/lp gradient stencil at the frame, float or double at each step, and
// round-half-to-even when writing back. Changed only where it cannot alter a result: what does not
// depend on the colour (the INSIDE tests, the offsets, the distance weight) is computed once per
// neighbour instead of once per channel, a pixel whose whole neighbourhood is two pixels clear of
// the frame takes a path without the frame tests, and the heap is a binary heap over structs.
using System.Buffers;
using System.Runtime.CompilerServices;

namespace OverTranslate.Imaging;

internal static unsafe class Inpaint
{
    private const byte Known = 0, Band = 1, Inside = 2;

    /// <summary>
    /// Fills the pixels of <paramref name="source"/> that <paramref name="mask"/> marks, from the
    /// outside in, each from the known pixels within <paramref name="radius"/> of it weighted along
    /// the picture's isophotes.
    /// </summary>
    public static ImageBuffer NavierStokes(ImageBuffer source, ImageBuffer mask, double radius)
    {
        if (source.Type != PixelType.U8C3) throw new NotSupportedException($"Inpainting {source.Type}.");
        ImageBuffer.RequireMask(mask, source.Size);
        var target = source.Clone();
        try
        {
            if (!source.IsEmpty) Run(target, mask, radius);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private static void Run(ImageBuffer target, ImageBuffer mask, double radius)
    {
        int range = Math.Clamp(Saturate.ToInt(radius), 1, 100);
        int rows = target.Height, cols = target.Width, erows = rows + 2, ecols = cols + 2;
        int cells = erows * ecols;
        using var stateBuffer = new Scratch<byte>(cells);
        using var timeBuffer = new Scratch<float>(cells);
        byte* states = stateBuffer.Pointer;
        float* times = timeBuffer.Pointer;
        var heap = new Heap(Math.Max(16, rows * cols / 4));
        try
        {
            new Span<byte>(states, cells).Clear();
            new Span<float>(times, cells).Fill(1.0e6f);
            for (int i = 0; i < rows; i++)
            {
                byte* m = mask.Row(i);
                for (int j = 0; j < cols; j++)
                    if (m[j] != 0) states[(i + 1) * ecols + j + 1] = Inside;
            }
            // The narrow band: known pixels four-adjacent to the hole, queued in raster order at
            // time zero. Upstream's dilation also reaches the frame row and column, but those are
            // cleared before anything is queued and keep their 1e6.
            for (int i = 1; i < erows - 1; i++)
            for (int j = 1; j < ecols - 1; j++)
            {
                int p = i * ecols + j;
                if (states[p] != 0) continue;
                if (states[p - ecols] != 0 || states[p + ecols] != 0 || states[p - 1] != 0 || states[p + 1] != 0)
                {
                    heap.Push(i, j, 0);
                    times[p] = 0;
                }
            }
            March(states, times, target.Data, target.Stride, erows, ecols, range, heap, Neighbourhood.For(range));
        }
        finally { heap.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Solve(int i1, int j1, int i2, int j2, byte* f, float* t, int ecols)
    {
        double sol, a11 = t[i1 * ecols + j1], a22 = t[i2 * ecols + j2], m12 = Math.Min(a11, a22);
        if (f[i1 * ecols + j1] != Inside)
            if (f[i2 * ecols + j2] != Inside)
                if (Math.Abs(a11 - a22) >= 1.0) sol = 1 + m12;
                else sol = (a11 + a22 + Math.Sqrt(2 - (a11 - a22) * (a11 - a22))) * 0.5;
            else sol = 1 + a11;
        else if (f[i2 * ecols + j2] != Inside) sol = 1 + a22;
        else sol = 1 + m12;
        return (float)sol;
    }

    private static void March(byte* f, float* t, byte* out0, long ostep, int erows, int ecols, int range, Heap heap, Neighbourhood table)
    {
        float* Ia = stackalloc float[3];
        float* s = stackalloc float[3];
        int r2 = range * range;
        while (heap.Pop(out int ii, out int jj))
        {
            f[ii * ecols + jj] = Known;
            for (int q = 0; q < 4; q++)
            {
                int i, j;
                if (q == 0) { i = ii - 1; j = jj; }
                else if (q == 1) { i = ii; j = jj - 1; }
                else if (q == 2) { i = ii + 1; j = jj; }
                else { i = ii; j = jj + 1; }
                if (i <= 0 || j <= 0 || i > erows - 1 || j > ecols - 1) continue;
                if (f[i * ecols + j] != Inside) continue;

                float a = Solve(i - 1, j, i, j - 1, f, t, ecols), b = Solve(i + 1, j, i, j - 1, f, t, ecols);
                float c = Solve(i - 1, j, i, j + 1, f, t, ecols), d = Solve(i + 1, j, i, j + 1, f, t, ecols);
                a = Math.Min(a, b);
                c = Math.Min(c, d);
                float dist = Math.Min(a, c);
                t[i * ecols + j] = dist;

                Ia[0] = Ia[1] = Ia[2] = 0;
                s[0] = s[1] = s[2] = 1.0e-20f;
                if (i - range >= 2 && i + range <= erows - 3 && j - range >= 2 && j + range <= ecols - 3)
                    Interior(f, out0, ostep, ecols, i, j, Ia, s, table);
                else
                    for (int k = i - range; k <= i + range; k++)
                    {
                        int km = k - 1 + (k == 1 ? 1 : 0), kp = k - 1 - (k == erows - 2 ? 1 : 0);
                        for (int l = j - range; l <= j + range; l++)
                        {
                            int lm = l - 1 + (l == 1 ? 1 : 0), lp = l - 1 - (l == ecols - 2 ? 1 : 0);
                            if (!(k > 0 && l > 0 && k < erows - 1 && l < ecols - 1)) continue;
                            if (f[k * ecols + l] == Inside || (l - j) * (l - j) + (k - i) * (k - i) > r2) continue;
                            float ry = k - i, rx = l - j;
                            float rlen = rx * rx + ry * ry;
                            float dst = 1 / (rlen * rlen + 1);
                            bool down = f[(k + 1) * ecols + l] != Inside, up = f[(k - 1) * ecols + l] != Inside;
                            bool right = f[k * ecols + l + 1] != Inside, left = f[k * ecols + l - 1] != Inside;
                            byte* rowKp1 = out0 + (kp + 1) * ostep, rowKp = out0 + kp * ostep, rowKm1 = out0 + (km - 1) * ostep;
                            byte* rowKm = out0 + km * ostep, rowK = out0 + (k - 1) * ostep;
                            for (int color = 0; color < 3; color++)
                            {
                                float gx, gy;
                                if (down)
                                {
                                    if (up) gx = Math.Abs(rowKp1[lm * 3 + color] - rowKp[lm * 3 + color]) + Math.Abs(rowKp[lm * 3 + color] - rowKm1[lm * 3 + color]);
                                    else gx = Math.Abs(rowKp1[lm * 3 + color] - rowKp[lm * 3 + color]) * 2.0f;
                                }
                                else
                                {
                                    if (up) gx = Math.Abs(rowKp[lm * 3 + color] - rowKm1[lm * 3 + color]) * 2.0f;
                                    else gx = 0;
                                }
                                if (right)
                                {
                                    if (left) gy = Math.Abs(rowKm[(lp + 1) * 3 + color] - rowKm[lm * 3 + color]) + Math.Abs(rowKm[lm * 3 + color] - rowKm[(lm - 1) * 3 + color]);
                                    else gy = Math.Abs(rowKm[(lp + 1) * 3 + color] - rowKm[lm * 3 + color]) * 2.0f;
                                }
                                else
                                {
                                    if (left) gy = Math.Abs(rowKm[lm * 3 + color] - rowKm[(lm - 1) * 3 + color]) * 2.0f;
                                    else gy = 0;
                                }
                                gx = -gx;
                                float dir = rx * gx + ry * gy;
                                if (Math.Abs(dir) <= 0.01) dir = 0.000001f;
                                else dir = MathF.Abs((rx * gx + ry * gy) / MathF.Sqrt(rlen * (gx * gx + gy * gy)));
                                float w = dst * dir;
                                Ia[color] += w * (float)rowK[(l - 1) * 3 + color];
                                s[color] += w;
                            }
                        }
                    }
                byte* px = out0 + (i - 1) * ostep + (j - 1) * 3;
                for (int color = 0; color < 3; color++) px[color] = Saturate.ToByte((double)Ia[color] / s[color]);
                f[i * ecols + j] = Band;
                heap.Push(i, j, dist);
            }
        }
    }

    /// <summary>
    /// The same sum as the general loop for a pixel whose whole neighbourhood is at least two pixels
    /// clear of the frame: there km = kp = k-1 and lm = lp = l-1, so the stencil is the plain centred one.
    /// </summary>
    private static void Interior(byte* f, byte* out0, long ostep, int ecols, int i, int j, float* Ia, float* s, Neighbourhood table)
    {
        int count = table.Count;
        fixed (int* dkp = table.Dk, dlp = table.Dl)
        fixed (float* rxp = table.Rx, ryp = table.Ry, rlp = table.Length, dsp = table.Weight)
            for (int n = 0; n < count; n++)
            {
                int k = i + dkp[n], l = j + dlp[n];
                byte* fp = f + k * ecols + l;
                if (*fp == Inside) continue;
                bool down = fp[ecols] != Inside, up = fp[-ecols] != Inside, right = fp[1] != Inside, left = fp[-1] != Inside;
                byte* p = out0 + (k - 1) * ostep + (l - 1) * 3;
                float rx = rxp[n], ry = ryp[n], rlen = rlp[n], dst = dsp[n];
                for (int color = 0; color < 3; color++)
                {
                    int c0 = p[color];
                    int below = p[ostep + color], above = p[-ostep + color], next = p[3 + color], prev = p[-3 + color];
                    float gx, gy;
                    if (down) gx = up ? Abs(below - c0) + Abs(c0 - above) : Abs(below - c0) * 2.0f;
                    else gx = up ? Abs(c0 - above) * 2.0f : 0;
                    if (right) gy = left ? Abs(next - c0) + Abs(c0 - prev) : Abs(next - c0) * 2.0f;
                    else gy = left ? Abs(c0 - prev) * 2.0f : 0;
                    gx = -gx;
                    float dir = rx * gx + ry * gy;
                    if (Math.Abs(dir) <= 0.01) dir = 0.000001f;
                    else dir = MathF.Abs((rx * gx + ry * gy) / MathF.Sqrt(rlen * (gx * gx + gy * gy)));
                    float w = dst * dir;
                    Ia[color] += w * (float)c0;
                    s[color] += w;
                }
            }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int v)
    {
        int m = v >> 31;
        return (v ^ m) - m;
    }

    /// <summary>The disc of offsets, in upstream's k-then-l order, with what does not depend on the pixel.</summary>
    private sealed class Neighbourhood
    {
        private static readonly Neighbourhood?[] Cache = new Neighbourhood?[101];

        private Neighbourhood(int range)
        {
            var dk = new List<int>();
            var dl = new List<int>();
            for (int k = -range; k <= range; k++)
            for (int l = -range; l <= range; l++)
                if (l * l + k * k <= range * range)
                {
                    dk.Add(k);
                    dl.Add(l);
                }
            Dk = [.. dk];
            Dl = [.. dl];
            Count = Dk.Length;
            Rx = new float[Count];
            Ry = new float[Count];
            Length = new float[Count];
            Weight = new float[Count];
            for (int n = 0; n < Count; n++)
            {
                Ry[n] = Dk[n];
                Rx[n] = Dl[n];
                Length[n] = Rx[n] * Rx[n] + Ry[n] * Ry[n];
                Weight[n] = 1 / (Length[n] * Length[n] + 1);
            }
        }

        public int Count { get; }
        public int[] Dk { get; }
        public int[] Dl { get; }
        public float[] Rx { get; }
        public float[] Ry { get; }
        public float[] Length { get; }
        public float[] Weight { get; }

        public static Neighbourhood For(int range) => Volatile.Read(ref Cache[range]) ?? (Cache[range] = new Neighbourhood(range));
    }

    /// <summary>A min-heap on (arrival time, insertion order): the same total order as upstream's priority queue.</summary>
    private sealed class Heap(int capacity) : IDisposable
    {
        private struct Node
        {
            public float T;
            public int Order, I, J;
        }

        private Node[] _items = ArrayPool<Node>.Shared.Rent(capacity);
        private int _count, _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Less(in Node a, in Node b) => a.T < b.T || (a.T == b.T && a.Order < b.Order);

        public void Push(int i, int j, float t)
        {
            if (_count == _items.Length)
            {
                var larger = ArrayPool<Node>.Shared.Rent(_items.Length * 2);
                Array.Copy(_items, larger, _count);
                ArrayPool<Node>.Shared.Return(_items);
                _items = larger;
            }
            var node = new Node { T = t, Order = _next++, I = i, J = j };
            int at = _count++;
            while (at > 0)
            {
                int parent = (at - 1) >> 1;
                if (!Less(node, _items[parent])) break;
                _items[at] = _items[parent];
                at = parent;
            }
            _items[at] = node;
        }

        public bool Pop(out int i, out int j)
        {
            if (_count == 0)
            {
                i = j = 0;
                return false;
            }
            i = _items[0].I;
            j = _items[0].J;
            var last = _items[--_count];
            int at = 0;
            while (true)
            {
                int child = at * 2 + 1;
                if (child >= _count) break;
                if (child + 1 < _count && Less(_items[child + 1], _items[child])) child++;
                if (!Less(_items[child], last)) break;
                _items[at] = _items[child];
                at = child;
            }
            if (_count > 0) _items[at] = last;
            return true;
        }

        public void Dispose() => ArrayPool<Node>.Shared.Return(_items);
    }
}
