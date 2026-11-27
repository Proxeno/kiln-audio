using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// ENC-CELT-1: the CELT encoder analysis loops (pitch pre-filter, transient and spreading analysis), written once
/// over the tier's shuffles. Every kernel here uses separately rounded multiplies and adds in the exact per-output
/// order of the scalar loop it replaced, so encoder output is the same on every tier; <typeparamref name="T"/>
/// only supplies the native shuffles.
/// </summary>
internal static partial class CeltEncVec<T>
    where T : struct, IVecOps
{
    /// <summary>
    /// Four independent dot products in one pass: <c>s_k = dot(a_k[..n], b_k[..n])</c>. Each accumulates exactly like
    /// <c>CeltVec&lt;PortableOps&gt;.Dot</c> (one 4-lane accumulator, lanes summed, sequential scalar tail).
    /// </summary>
    internal static void Dot4(
        int n,
        ReadOnlySpan<float> a0, ReadOnlySpan<float> b0,
        ReadOnlySpan<float> a1, ReadOnlySpan<float> b1,
        ReadOnlySpan<float> a2, ReadOnlySpan<float> b2,
        ReadOnlySpan<float> a3, ReadOnlySpan<float> b3,
        out float s0, out float s1, out float s2, out float s3)
    {
        if (a0.Length < n || b0.Length < n || a1.Length < n || b1.Length < n
            || a2.Length < n || b2.Length < n || a3.Length < n || b3.Length < n)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        ref float a0r = ref MemoryMarshal.GetReference(a0);
        ref float b0r = ref MemoryMarshal.GetReference(b0);
        ref float a1r = ref MemoryMarshal.GetReference(a1);
        ref float b1r = ref MemoryMarshal.GetReference(b1);
        ref float a2r = ref MemoryMarshal.GetReference(a2);
        ref float b2r = ref MemoryMarshal.GetReference(b2);
        ref float a3r = ref MemoryMarshal.GetReference(a3);
        ref float b3r = ref MemoryMarshal.GetReference(b3);
        Vector128<float> c0 = Vector128<float>.Zero;
        Vector128<float> c1 = Vector128<float>.Zero;
        Vector128<float> c2 = Vector128<float>.Zero;
        Vector128<float> c3 = Vector128<float>.Zero;
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            nuint o = (nuint)i;
            c0 += Vector128.LoadUnsafe(ref a0r, o) * Vector128.LoadUnsafe(ref b0r, o);
            c1 += Vector128.LoadUnsafe(ref a1r, o) * Vector128.LoadUnsafe(ref b1r, o);
            c2 += Vector128.LoadUnsafe(ref a2r, o) * Vector128.LoadUnsafe(ref b2r, o);
            c3 += Vector128.LoadUnsafe(ref a3r, o) * Vector128.LoadUnsafe(ref b3r, o);
        }

        float r0 = Vector128.Sum(c0);
        float r1 = Vector128.Sum(c1);
        float r2 = Vector128.Sum(c2);
        float r3 = Vector128.Sum(c3);
        for (; i < n; i++)
        {
            r0 += Unsafe.Add(ref a0r, i) * Unsafe.Add(ref b0r, i);
            r1 += Unsafe.Add(ref a1r, i) * Unsafe.Add(ref b1r, i);
            r2 += Unsafe.Add(ref a2r, i) * Unsafe.Add(ref b2r, i);
            r3 += Unsafe.Add(ref a3r, i) * Unsafe.Add(ref b3r, i);
        }

        s0 = r0;
        s1 = r1;
        s2 = r2;
        s3 = r3;
    }

    /// <summary>Counts the bins with <c>x*x*n</c> below 0.25, 0.0625 and 0.015625 (the spreading decision).</summary>
    internal static void SpreadCounts(ReadOnlySpan<float> x, int bandSamples, out int t0, out int t1, out int t2)
    {
        ref float r = ref MemoryMarshal.GetReference(x);
        int n = x.Length;
        Vector128<float> nv = Vector128.Create((float)bandSamples);
        Vector128<float> l0 = Vector128.Create(0.25f);
        Vector128<float> l1 = Vector128.Create(0.0625f);
        Vector128<float> l2 = Vector128.Create(0.015625f);
        Vector128<int> a0 = Vector128<int>.Zero;
        Vector128<int> a1 = Vector128<int>.Zero;
        Vector128<int> a2 = Vector128<int>.Zero;
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            Vector128<float> v = Vector128.LoadUnsafe(ref r, (nuint)i);
            Vector128<float> x2n = v * v * nv;
            a0 -= Vector128.LessThan(x2n, l0).AsInt32();
            a1 -= Vector128.LessThan(x2n, l1).AsInt32();
            a2 -= Vector128.LessThan(x2n, l2).AsInt32();
        }

        int c0 = Vector128.Sum(a0);
        int c1 = Vector128.Sum(a1);
        int c2 = Vector128.Sum(a2);
        float nf = bandSamples;
        for (; i < n; i++)
        {
            float v = Unsafe.Add(ref r, i);
            float x2n = v * v * nf;
            c0 += x2n < 0.25f ? 1 : 0;
            c1 += x2n < 0.0625f ? 1 : 0;
            c2 += x2n < 0.015625f ? 1 : 0;
        }

        t0 = c0;
        t1 = c1;
        t2 = c2;
    }

    /// <summary>
    /// <c>ac[l] = sum over i of x[i]*x[i-l]</c> for <c>l = 0..4</c>, every lag accumulated in index order exactly like
    /// the single-lag loop; the five serial chains run side by side (four lanes plus one scalar).
    /// </summary>
    internal static void Autocorr5(ReadOnlySpan<float> x, Span<float> ac)
    {
        int n = x.Length;
        if (n < 8 || ac.Length < 5)
        {
            CeltEncScalar.Autocorr5(x, ac);
            return;
        }

        ref float r = ref MemoryMarshal.GetReference(x);
        float d0 = 0f;
        float d1 = 0f;
        float d2 = 0f;
        float d3 = 0f;
        float d4 = 0f;
        for (int i = 0; i < 4; i++)
        {
            float v = Unsafe.Add(ref r, i);
            d0 += v * v;
            if (i >= 1)
            {
                d1 += v * Unsafe.Add(ref r, i - 1);
            }

            if (i >= 2)
            {
                d2 += v * Unsafe.Add(ref r, i - 2);
            }

            if (i >= 3)
            {
                d3 += v * Unsafe.Add(ref r, i - 3);
            }
        }

        Vector128<float> acc = Vector128.Create(d0, d1, d2, d3);
        for (int i = 4; i < n; i++)
        {
            float v = Unsafe.Add(ref r, i);
            Vector128<float> history = T.Reverse(Vector128.LoadUnsafe(ref r, (nuint)(i - 3)));
            acc += Vector128.Create(v) * history;
            d4 += v * Unsafe.Add(ref r, i - 4);
        }

        ac[0] = acc.GetElement(0);
        ac[1] = acc.GetElement(1);
        ac[2] = acc.GetElement(2);
        ac[3] = acc.GetElement(3);
        ac[4] = d4;
    }

    /// <summary>
    /// In-place 4-tap FIR with zero history: <c>y[i] = x[i] + t0*x[i-1] + t1*x[i-2] + t2*x[i-3] + t3*x[i-4]</c>, summed
    /// left to right. Samples are independent (the taps read the inputs), so four run per step, from the top down so
    /// the inputs a block reads have not been overwritten yet.
    /// </summary>
    internal static void Fir4(Span<float> x, float t0, float t1, float t2, float t3)
    {
        int n = x.Length;
        ref float r = ref MemoryMarshal.GetReference(x);
        if (n < 8)
        {
            CeltEncScalar.Fir4(x, t0, t1, t2, t3);
            return;
        }

        int rest = n & 3;
        for (int i = n - 1; i >= n - rest; i--)
        {
            Unsafe.Add(ref r, i) = Fir4Sample(ref r, i, t0, t1, t2, t3);
        }

        Vector128<float> v0 = Vector128.Create(t0);
        Vector128<float> v1 = Vector128.Create(t1);
        Vector128<float> v2 = Vector128.Create(t2);
        Vector128<float> v3 = Vector128.Create(t3);
        int b = n - rest - 4;
        for (; b >= 4; b -= 4)
        {
            Vector128<float> sum = Vector128.LoadUnsafe(ref r, (nuint)b);
            sum += v0 * Vector128.LoadUnsafe(ref r, (nuint)(b - 1));
            sum += v1 * Vector128.LoadUnsafe(ref r, (nuint)(b - 2));
            sum += v2 * Vector128.LoadUnsafe(ref r, (nuint)(b - 3));
            sum += v3 * Vector128.LoadUnsafe(ref r, (nuint)(b - 4));
            sum.StoreUnsafe(ref r, (nuint)b);
        }

        for (int i = b + 3; i >= 0; i--)
        {
            Unsafe.Add(ref r, i) = Fir4Sample(ref r, i, t0, t1, t2, t3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fir4Sample(ref float r, int i, float t0, float t1, float t2, float t3)
    {
        float sum = Unsafe.Add(ref r, i);
        sum += t0 * (i >= 1 ? Unsafe.Add(ref r, i - 1) : 0f);
        sum += t1 * (i >= 2 ? Unsafe.Add(ref r, i - 2) : 0f);
        sum += t2 * (i >= 3 ? Unsafe.Add(ref r, i - 3) : 0f);
        sum += t3 * (i >= 4 ? Unsafe.Add(ref r, i - 4) : 0f);
        return sum;
    }

    /// <summary>In-place 1-tap FIR with zero history: <c>y[i] = x[i] + t0*x[i-1]</c>.</summary>
    internal static void Fir1(Span<float> x, float t0)
    {
        int n = x.Length;
        ref float r = ref MemoryMarshal.GetReference(x);
        int rest = n & 3;
        int i = n - 1;
        for (; i >= n - rest && i >= 1; i--)
        {
            Unsafe.Add(ref r, i) += t0 * Unsafe.Add(ref r, i - 1);
        }

        Vector128<float> v0 = Vector128.Create(t0);
        int b = n - rest - 4;
        for (; b >= 1; b -= 4)
        {
            Vector128<float> sum = Vector128.LoadUnsafe(ref r, (nuint)b);
            sum += v0 * Vector128.LoadUnsafe(ref r, (nuint)(b - 1));
            sum.StoreUnsafe(ref r, (nuint)b);
        }

        for (i = b + 3; i >= 1; i--)
        {
            Unsafe.Add(ref r, i) += t0 * Unsafe.Add(ref r, i - 1);
        }
    }

    /// <summary>
    /// <c>low[i] (+)= 0.25*(c[2i-1]+c[2i+1]) + 0.5*c[2i]</c> for <c>i = 1..count-1</c> and the edge value for
    /// <c>i = 0</c>: the pitch search's low-pass and decimate-by-two. <paramref name="accumulate"/> adds a further
    /// channel instead of overwriting.
    /// </summary>
    internal static void DecimateLowpass(ReadOnlySpan<float> source, Span<float> low, int count, bool accumulate)
    {
        if (source.Length < (2 * count) || low.Length < count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ref float s = ref MemoryMarshal.GetReference(source);
        ref float l = ref MemoryMarshal.GetReference(low);
        float edge = (0.25f * Unsafe.Add(ref s, 1)) + (0.5f * Unsafe.Add(ref s, 0));
        Vector128<float> quarter = Vector128.Create(0.25f);
        Vector128<float> half = Vector128.Create(0.5f);
        int i = 1;
        for (; (2 * i) + 9 <= source.Length && i + 4 <= count; i += 4)
        {
            int p = 2 * i;
            Vector128<float> a = Vector128.LoadUnsafe(ref s, (nuint)(p - 1));
            Vector128<float> b = Vector128.LoadUnsafe(ref s, (nuint)(p + 3));
            Vector128<float> c = Vector128.LoadUnsafe(ref s, (nuint)(p + 1));
            Vector128<float> d = Vector128.LoadUnsafe(ref s, (nuint)(p + 5));
            Vector128<float> left = T.UnzipEven(a, b);
            Vector128<float> centre = T.UnzipOdd(a, b);
            Vector128<float> right = T.UnzipEven(c, d);
            Vector128<float> value = (quarter * (left + right)) + (half * centre);
            if (accumulate)
            {
                value = Vector128.LoadUnsafe(ref l, (nuint)i) + value;
            }

            value.StoreUnsafe(ref l, (nuint)i);
        }

        for (; i < count; i++)
        {
            float value = (0.25f * (Unsafe.Add(ref s, (2 * i) - 1) + Unsafe.Add(ref s, (2 * i) + 1)))
                + (0.5f * Unsafe.Add(ref s, 2 * i));
            Unsafe.Add(ref l, i) = accumulate ? Unsafe.Add(ref l, i) + value : value;
        }

        Unsafe.Add(ref l, 0) = accumulate ? Unsafe.Add(ref l, 0) + edge : edge;
    }

    /// <summary>The maximum absolute value of each <paramref name="block"/>-sample block into <paramref name="bins"/>.</summary>
    internal static void BlockMaxAbs(ReadOnlySpan<float> x, int block, Span<float> bins, int binCount)
    {
        ref float r = ref MemoryMarshal.GetReference(x);
        for (int b = 0; b < binCount; b++)
        {
            int offset = b * block;
            int j = 0;
            Vector128<float> m = Vector128<float>.Zero;
            for (; j + 4 <= block; j += 4)
            {
                m = Vector128.Max(m, Vector128.Abs(Vector128.LoadUnsafe(ref r, (nuint)(offset + j))));
            }

            float maxAbs = Math.Max(Math.Max(m.GetElement(0), m.GetElement(1)), Math.Max(m.GetElement(2), m.GetElement(3)));
            for (; j < block; j++)
            {
                float v = Math.Abs(Unsafe.Add(ref r, offset + j));
                if (v > maxAbs)
                {
                    maxAbs = v;
                }
            }

            bins[b] = maxAbs;
        }
    }

    /// <summary>
    /// The encoder's three-tap comb pre-filter (RFC 6716 Appendix A, celt/celt.c: comb_filter), every output computed
    /// from separately rounded products and a left-to-right sum, four outputs per step.
    /// </summary>
    internal static void CombFilter(
        Span<float> y,
        ReadOnlySpan<float> x,
        int xStart,
        int t0,
        int t1,
        int n,
        float g0,
        float g1,
        ReadOnlySpan<float> gains0,
        ReadOnlySpan<float> gains1,
        ReadOnlySpan<float> window,
        int overlap)
    {
        float g00 = g0 * gains0[0];
        float g01 = g0 * gains0[1];
        float g02 = g0 * gains0[2];
        float g10 = g1 * gains1[0];
        float g11 = g1 * gains1[1];
        float g12 = g1 * gains1[2];

        int fade = Math.Min(overlap, n);
        if (xStart - t0 - 2 < 0 || xStart - t1 - 2 < 0 || xStart + n > x.Length || y.Length < n || window.Length < fade)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        ref float xr = ref MemoryMarshal.GetReference(x);
        ref float yr = ref MemoryMarshal.GetReference(y);
        ref float wr = ref MemoryMarshal.GetReference(window);
        Vector128<float> one = Vector128.Create(1f);
        Vector128<float> vg00 = Vector128.Create(g00);
        Vector128<float> vg01 = Vector128.Create(g01);
        Vector128<float> vg02 = Vector128.Create(g02);
        Vector128<float> vg10 = Vector128.Create(g10);
        Vector128<float> vg11 = Vector128.Create(g11);
        Vector128<float> vg12 = Vector128.Create(g12);

        int i = 0;
        for (; i + 4 <= fade; i += 4)
        {
            Vector128<float> w = Vector128.LoadUnsafe(ref wr, (nuint)i);
            Vector128<float> f = w * w;
            Vector128<float> om = one - f;
            int p = xStart + i;
            int q0 = p - t0;
            int q1 = p - t1;
            Vector128<float> previous = om * vg00 * Vector128.LoadUnsafe(ref xr, (nuint)q0);
            previous += om * vg01 * Vector128.LoadUnsafe(ref xr, (nuint)(q0 - 1));
            previous += om * vg01 * Vector128.LoadUnsafe(ref xr, (nuint)(q0 + 1));
            previous += om * vg02 * Vector128.LoadUnsafe(ref xr, (nuint)(q0 - 2));
            previous += om * vg02 * Vector128.LoadUnsafe(ref xr, (nuint)(q0 + 2));
            Vector128<float> current = f * vg10 * Vector128.LoadUnsafe(ref xr, (nuint)q1);
            current += f * vg11 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 - 1));
            current += f * vg11 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 + 1));
            current += f * vg12 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 - 2));
            current += f * vg12 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 + 2));
            (Vector128.LoadUnsafe(ref xr, (nuint)p) + previous + current).StoreUnsafe(ref yr, (nuint)i);
        }

        for (; i < fade; i++)
        {
            float f = Unsafe.Add(ref wr, i) * Unsafe.Add(ref wr, i);
            int p = xStart + i;
            float previous = (1f - f) * g00 * Unsafe.Add(ref xr, p - t0);
            previous += (1f - f) * g01 * Unsafe.Add(ref xr, p - t0 - 1);
            previous += (1f - f) * g01 * Unsafe.Add(ref xr, p - t0 + 1);
            previous += (1f - f) * g02 * Unsafe.Add(ref xr, p - t0 - 2);
            previous += (1f - f) * g02 * Unsafe.Add(ref xr, p - t0 + 2);
            float current = f * g10 * Unsafe.Add(ref xr, p - t1);
            current += f * g11 * Unsafe.Add(ref xr, p - t1 - 1);
            current += f * g11 * Unsafe.Add(ref xr, p - t1 + 1);
            current += f * g12 * Unsafe.Add(ref xr, p - t1 - 2);
            current += f * g12 * Unsafe.Add(ref xr, p - t1 + 2);
            Unsafe.Add(ref yr, i) = Unsafe.Add(ref xr, p) + previous + current;
        }

        if (g1 == 0f)
        {
            // Zero gain: every tap term is +-0, so the output is the input.
            x.Slice(xStart + fade, n - fade).CopyTo(y.Slice(fade));
            return;
        }

        for (; i + 4 <= n; i += 4)
        {
            int p = xStart + i;
            int q1 = p - t1;
            Vector128<float> sum = Vector128.LoadUnsafe(ref xr, (nuint)p);
            sum += vg10 * Vector128.LoadUnsafe(ref xr, (nuint)q1);
            sum += vg11 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 - 1));
            sum += vg11 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 + 1));
            sum += vg12 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 - 2));
            sum += vg12 * Vector128.LoadUnsafe(ref xr, (nuint)(q1 + 2));
            sum.StoreUnsafe(ref yr, (nuint)i);
        }

        for (; i < n; i++)
        {
            int p = xStart + i;
            Unsafe.Add(ref yr, i) = Unsafe.Add(ref xr, p)
                + (g10 * Unsafe.Add(ref xr, p - t1))
                + (g11 * Unsafe.Add(ref xr, p - t1 - 1))
                + (g11 * Unsafe.Add(ref xr, p - t1 + 1))
                + (g12 * Unsafe.Add(ref xr, p - t1 - 2))
                + (g12 * Unsafe.Add(ref xr, p - t1 + 2));
        }
    }

    /// <summary>
    /// The forward MDCT's un-windowed middle fold: <c>real[i] = input[source2 - 2*(i-start)]</c> and
    /// <c>imag[i] = input[source1 + 2*(i-start)]</c> for <c>i = start..end-1</c>, four per step with two unzips.
    /// </summary>
    internal static void MdctFoldMiddle(
        ReadOnlySpan<float> input, int source1, int source2, Span<float> real, Span<float> imaginary, int start, int end)
    {
        if (start < 0 || end > real.Length || end > imaginary.Length
            || end > start && (source1 + (2 * (end - start - 1)) + 1 >= input.Length || source2 - (2 * (end - start - 1)) < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        ref float inr = ref MemoryMarshal.GetReference(input);
        ref float rer = ref MemoryMarshal.GetReference(real);
        ref float imr = ref MemoryMarshal.GetReference(imaginary);
        int i = start;
        int s1 = source1;
        int s2 = source2;
        for (; i + 4 <= end && s1 + 8 <= input.Length && s2 - 7 >= 0; i += 4, s1 += 8, s2 -= 8)
        {
            Vector128<float> a = Vector128.LoadUnsafe(ref inr, (nuint)s1);
            Vector128<float> b = Vector128.LoadUnsafe(ref inr, (nuint)(s1 + 4));
            T.UnzipEven(a, b).StoreUnsafe(ref imr, (nuint)i);
            Vector128<float> c = Vector128.LoadUnsafe(ref inr, (nuint)(s2 - 7));
            Vector128<float> d = Vector128.LoadUnsafe(ref inr, (nuint)(s2 - 3));
            T.Reverse(T.UnzipOdd(c, d)).StoreUnsafe(ref rer, (nuint)i);
        }

        for (; i < end; i++, s1 += 2, s2 -= 2)
        {
            Unsafe.Add(ref rer, i) = Unsafe.Add(ref inr, s2);
            Unsafe.Add(ref imr, i) = Unsafe.Add(ref inr, s1);
        }
    }

    /// <summary>
    /// The forward MDCT post-rotation for a contiguous block: <c>out[2i] = re*cos + im*sin</c> and
    /// <c>out[count-1-2i] = re*sin - im*cos</c>, written as zipped vectors (a block and its mirror per step).
    /// </summary>
    internal static void MdctPostContiguous(
        Span<float> coefficients,
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count)
    {
        int half = count / 2;
        if (coefficients.Length < count || real.Length < half || imaginary.Length < half || rotCos.Length < half || rotSin.Length < half)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ref float outr = ref MemoryMarshal.GetReference(coefficients);
        ref float rer = ref MemoryMarshal.GetReference(real);
        ref float imr = ref MemoryMarshal.GetReference(imaginary);
        ref float cr = ref MemoryMarshal.GetReference(rotCos);
        ref float sr = ref MemoryMarshal.GetReference(rotSin);
        if ((half & 7) != 0)
        {
            throw new ArgumentException("The fast post-rotation needs half to be a multiple of 8.", nameof(count));
        }

        for (int a = 0, b = half - 4; a < b; a += 4, b -= 4)
        {
            Vector128<float> ea = Even(ref rer, ref imr, ref cr, ref sr, a, out Vector128<float> oa);
            Vector128<float> eb = Even(ref rer, ref imr, ref cr, ref sr, b, out Vector128<float> ob);
            Vector128<float> ra = T.Reverse(oa);
            Vector128<float> rb = T.Reverse(ob);
            // Output positions 2a.. take the evens of block a and the (reversed) odds of block b.
            T.ZipLow(ea, rb).StoreUnsafe(ref outr, (nuint)(2 * a));
            T.ZipHigh(ea, rb).StoreUnsafe(ref outr, (nuint)((2 * a) + 4));
            T.ZipLow(eb, ra).StoreUnsafe(ref outr, (nuint)(2 * b));
            T.ZipHigh(eb, ra).StoreUnsafe(ref outr, (nuint)((2 * b) + 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Even(
        ref float rer, ref float imr, ref float cr, ref float sr, int i, out Vector128<float> odd)
    {
        Vector128<float> re = Vector128.LoadUnsafe(ref rer, (nuint)i);
        Vector128<float> im = Vector128.LoadUnsafe(ref imr, (nuint)i);
        Vector128<float> cos = Vector128.LoadUnsafe(ref cr, (nuint)i);
        Vector128<float> sin = Vector128.LoadUnsafe(ref sr, (nuint)i);
        odd = (re * sin) - (im * cos);
        return (re * cos) + (im * sin);
    }
}
