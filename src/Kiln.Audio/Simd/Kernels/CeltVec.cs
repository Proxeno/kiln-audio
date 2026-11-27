using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The 128-bit vector kernels shared by the <see cref="Vector128Kernels"/>, <see cref="AdvSimdArm64Kernels"/>
/// and <see cref="Avx2FmaKernels"/> tiers. <typeparamref name="T"/> supplies the tier's own
/// multiply-add and shuffle instructions, so the body is written once and compiled three times.
/// </summary>
internal static class CeltVec<T>
    where T : struct, IVecOps
{
    private const float Cos2PiOver5 = 0.3090169943749474f;
    private const float Cos4PiOver5 = -0.8090169943749475f;
    private const float Sin2PiOver5 = 0.9510565162951535f;
    private const float Sin4PiOver5 = 0.5877852522924731f;

    // ---- elementwise ----

    internal static float SumSquares(ReadOnlySpan<float> x)
    {
        ref float r = ref MemoryMarshal.GetReference(x);
        int n = x.Length;
        int i = 0;
        Vector128<float> a0 = Vector128<float>.Zero;
        Vector128<float> a1 = Vector128<float>.Zero;
        for (; i + 8 <= n; i += 8)
        {
            Vector128<float> v0 = Vector128.LoadUnsafe(ref r, (nuint)i);
            Vector128<float> v1 = Vector128.LoadUnsafe(ref r, (nuint)(i + 4));
            a0 = T.MulAdd(a0, v0, v0);
            a1 = T.MulAdd(a1, v1, v1);
        }

        if (i + 4 <= n)
        {
            Vector128<float> v0 = Vector128.LoadUnsafe(ref r, (nuint)i);
            a0 = T.MulAdd(a0, v0, v0);
            i += 4;
        }

        float sum = Vector128.Sum(a0 + a1);
        for (; i < n; i++)
        {
            float v = Unsafe.Add(ref r, i);
            sum = T.MulAdd(sum, v, v);
        }

        return sum;
    }

    /// <summary>
    /// The dot product of two vectors: one 4-lane accumulator, lanes summed at the end, scalar tail.
    /// This is the encoder's prefilter and input-resampler kernel; it deliberately keeps the
    /// accumulation structure of the code it replaced so encoder output does not change.
    /// </summary>
    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length;
        if (b.Length < n)
        {
            throw new ArgumentException("The second vector is shorter than the first.", nameof(b));
        }

        ref float ar = ref MemoryMarshal.GetReference(a);
        ref float br = ref MemoryMarshal.GetReference(b);
        int i = 0;
        Vector128<float> acc = Vector128<float>.Zero;
        for (; i + 4 <= n; i += 4)
        {
            acc = T.MulAdd(acc, Vector128.LoadUnsafe(ref ar, (nuint)i), Vector128.LoadUnsafe(ref br, (nuint)i));
        }

        float sum = Vector128.Sum(acc);
        for (; i < n; i++)
        {
            sum += Unsafe.Add(ref ar, i) * Unsafe.Add(ref br, i);
        }

        return sum;
    }

    internal static void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy)
    {
        int n = x.Length;
        if (y.Length < n)
        {
            throw new ArgumentException("The second vector is shorter than the first.", nameof(y));
        }

        ref float xr = ref MemoryMarshal.GetReference(x);
        ref float yr = ref MemoryMarshal.GetReference(y);
        int i = 0;
        Vector128<float> a = Vector128<float>.Zero;
        Vector128<float> b = Vector128<float>.Zero;
        for (; i + 4 <= n; i += 4)
        {
            Vector128<float> xv = Vector128.LoadUnsafe(ref xr, (nuint)i);
            Vector128<float> yv = Vector128.LoadUnsafe(ref yr, (nuint)i);
            a = T.MulAdd(a, yv, xv);
            b = T.MulAdd(b, yv, yv);
        }

        float sa = Vector128.Sum(a);
        float sb = Vector128.Sum(b);
        for (; i < n; i++)
        {
            float yv = Unsafe.Add(ref yr, i);
            sa = T.MulAdd(sa, yv, Unsafe.Add(ref xr, i));
            sb = T.MulAdd(sb, yv, yv);
        }

        xy = sa;
        yy = sb;
    }

    internal static void Rotate(Span<float> x, int length, int stride, float c, float s)
    {
        if (length <= stride)
        {
            return;
        }

        if (x.Length < length)
        {
            throw new ArgumentException("The vector is shorter than length.", nameof(x));
        }

        if (stride == 1)
        {
            CeltScalar.RotateStride1<T>(x, length, c, s);
            return;
        }

        ref float r = ref MemoryMarshal.GetReference(x);
        int i = 0;
        Vector128<float> cv = Vector128.Create(c);
        Vector128<float> sv = Vector128.Create(s);
        if (stride >= 4)
        {
            for (; i + 4 <= length - stride; i += 4)
            {
                Vector128<float> x1 = Vector128.LoadUnsafe(ref r, (nuint)i);
                Vector128<float> x2 = Vector128.LoadUnsafe(ref r, (nuint)(i + stride));
                T.MulAdd(cv * x2, sv, x1).StoreUnsafe(ref r, (nuint)(i + stride));
                T.MulSub(cv * x1, sv, x2).StoreUnsafe(ref r, (nuint)i);
            }
        }

        for (; i < length - stride; i++)
        {
            float x1 = Unsafe.Add(ref r, i);
            float x2 = Unsafe.Add(ref r, i + stride);
            Unsafe.Add(ref r, i + stride) = T.MulAdd(c * x2, s, x1);
            Unsafe.Add(ref r, i) = T.MulAdd(c * x1, -s, x2);
        }

        i = length - (2 * stride) - 1;
        if (stride >= 4)
        {
            for (; i >= 3; i -= 4)
            {
                int b = i - 3;
                Vector128<float> x1 = Vector128.LoadUnsafe(ref r, (nuint)b);
                Vector128<float> x2 = Vector128.LoadUnsafe(ref r, (nuint)(b + stride));
                T.MulAdd(cv * x2, sv, x1).StoreUnsafe(ref r, (nuint)(b + stride));
                T.MulSub(cv * x1, sv, x2).StoreUnsafe(ref r, (nuint)b);
            }
        }

        for (; i >= 0; i--)
        {
            float x1 = Unsafe.Add(ref r, i);
            float x2 = Unsafe.Add(ref r, i + stride);
            Unsafe.Add(ref r, i + stride) = T.MulAdd(c * x2, s, x1);
            Unsafe.Add(ref r, i) = T.MulAdd(c * x1, -s, x2);
        }
    }

    internal static void Haar(Span<float> x, int n0, int stride)
    {
        int pairs = n0 >> 1;
        ref float r = ref MemoryMarshal.GetReference(x);
        if (x.Length < stride * 2 * pairs)
        {
            throw new ArgumentException("The vector is too short.", nameof(x));
        }

        Vector128<float> h = Vector128.Create(0.70710678f);
        if (stride == 1)
        {
            int j = 0;
            for (; j + 4 <= pairs; j += 4)
            {
                Vector128<float> v0 = Vector128.LoadUnsafe(ref r, (nuint)(2 * j));
                Vector128<float> v1 = Vector128.LoadUnsafe(ref r, (nuint)((2 * j) + 4));
                Vector128<float> t1 = h * T.UnzipEven(v0, v1);
                Vector128<float> t2 = h * T.UnzipOdd(v0, v1);
                Vector128<float> sum = t1 + t2;
                Vector128<float> diff = t1 - t2;
                T.ZipLow(sum, diff).StoreUnsafe(ref r, (nuint)(2 * j));
                T.ZipHigh(sum, diff).StoreUnsafe(ref r, (nuint)((2 * j) + 4));
            }

            for (; j < pairs; j++)
            {
                float a = 0.70710678f * Unsafe.Add(ref r, 2 * j);
                float b = 0.70710678f * Unsafe.Add(ref r, (2 * j) + 1);
                Unsafe.Add(ref r, 2 * j) = a + b;
                Unsafe.Add(ref r, (2 * j) + 1) = a - b;
            }

            return;
        }

        int i = 0;
        if (stride >= 4)
        {
            for (; i + 4 <= stride; i += 4)
            {
                for (int j = 0; j < pairs; j++)
                {
                    int lo = (stride * 2 * j) + i;
                    Vector128<float> t1 = h * Vector128.LoadUnsafe(ref r, (nuint)lo);
                    Vector128<float> t2 = h * Vector128.LoadUnsafe(ref r, (nuint)(lo + stride));
                    (t1 + t2).StoreUnsafe(ref r, (nuint)lo);
                    (t1 - t2).StoreUnsafe(ref r, (nuint)(lo + stride));
                }
            }
        }

        for (; i < stride; i++)
        {
            for (int j = 0; j < pairs; j++)
            {
                int lo = (stride * 2 * j) + i;
                float a = 0.70710678f * Unsafe.Add(ref r, lo);
                float b = 0.70710678f * Unsafe.Add(ref r, lo + stride);
                Unsafe.Add(ref r, lo) = a + b;
                Unsafe.Add(ref r, lo + stride) = a - b;
            }
        }
    }

    internal static void AddInPlace(Span<float> destination, ReadOnlySpan<float> source)
    {
        int n = destination.Length;
        if (source.Length < n)
        {
            throw new ArgumentException("The source is shorter than the destination.", nameof(source));
        }

        ref float d = ref MemoryMarshal.GetReference(destination);
        ref float s = ref MemoryMarshal.GetReference(source);
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < n; i++)
        {
            Unsafe.Add(ref d, i) += Unsafe.Add(ref s, i);
        }
    }

    internal static void Average(Span<float> x, ReadOnlySpan<float> y)
    {
        int n = x.Length;
        if (y.Length < n)
        {
            throw new ArgumentException("The second channel is shorter than the first.", nameof(y));
        }

        ref float d = ref MemoryMarshal.GetReference(x);
        ref float s = ref MemoryMarshal.GetReference(y);
        Vector128<float> half = Vector128.Create(0.5f);
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            (half * (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i))).StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < n; i++)
        {
            Unsafe.Add(ref d, i) = 0.5f * (Unsafe.Add(ref d, i) + Unsafe.Add(ref s, i));
        }
    }

    // ---- FFT ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tw(
        Vector128<float> br, Vector128<float> bi, Vector128<float> cr, Vector128<float> ci,
        out Vector128<float> tr, out Vector128<float> ti)
    {
        tr = T.MulSub(br * cr, bi, ci);
        ti = T.MulAdd(br * ci, bi, cr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Ld(ref float p, int index) => Vector128.LoadUnsafe(ref p, (nuint)index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St(ref float p, int index, Vector128<float> value, Vector128<float> old, Vector128<float> mask, bool masked)
    {
        if (masked)
        {
            value = Vector128.ConditionalSelect(mask, value, old);
        }

        value.StoreUnsafe(ref p, (nuint)index);
    }

    private static Vector128<float> TailMask(int half) =>
        Vector128.GreaterThanOrEqual(Vector128.Create(0, 1, 2, 3), Vector128.Create(4 - (half & 3))).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Bfly2(
        Vector128<float> v0r, Vector128<float> v0i, Vector128<float> v1r, Vector128<float> v1i,
        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i)
    {
        y0r = v0r + v1r;
        y0i = v0i + v1i;
        y1r = v0r - v1r;
        y1i = v0i - v1i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Bfly3(
        Vector128<float> v0r, Vector128<float> v0i, Vector128<float> v1r, Vector128<float> v1i,
        Vector128<float> v2r, Vector128<float> v2i, Vector128<float> ksign,
        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
        out Vector128<float> y2r, out Vector128<float> y2i)
    {
        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<float> sr = v1r + v2r;
        Vector128<float> si = v1i + v2i;
        Vector128<float> dr = v1r - v2r;
        Vector128<float> di = v1i - v2i;
        Vector128<float> baseR = T.MulSub(v0r, half, sr);
        Vector128<float> baseI = T.MulSub(v0i, half, si);
        y0r = v0r + sr;
        y0i = v0i + si;
        y1r = T.MulAdd(baseR, ksign, di);
        y1i = T.MulSub(baseI, ksign, dr);
        y2r = T.MulSub(baseR, ksign, di);
        y2i = T.MulAdd(baseI, ksign, dr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Bfly4(
        Vector128<float> v0r, Vector128<float> v0i, Vector128<float> v1r, Vector128<float> v1i,
        Vector128<float> v2r, Vector128<float> v2i, Vector128<float> v3r, Vector128<float> v3i, Vector128<float> sign,
        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
        out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i)
    {
        Vector128<float> t0r = v0r + v2r;
        Vector128<float> t0i = v0i + v2i;
        Vector128<float> t1r = v0r - v2r;
        Vector128<float> t1i = v0i - v2i;
        Vector128<float> t2r = v1r + v3r;
        Vector128<float> t2i = v1i + v3i;
        Vector128<float> t3r = (v1r - v3r) * sign;
        Vector128<float> t3i = (v1i - v3i) * sign;
        y0r = t0r + t2r;
        y0i = t0i + t2i;
        y1r = t1r + t3i;
        y1i = t1i - t3r;
        y2r = t0r - t2r;
        y2i = t0i - t2i;
        y3r = t1r - t3i;
        y3i = t1i + t3r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Bfly5(
        Vector128<float> v0r, Vector128<float> v0i, Vector128<float> v1r, Vector128<float> v1i,
        Vector128<float> v2r, Vector128<float> v2i, Vector128<float> v3r, Vector128<float> v3i,
        Vector128<float> v4r, Vector128<float> v4i, Vector128<float> sign,
        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
        out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i,
        out Vector128<float> y4r, out Vector128<float> y4i)
    {
        Vector128<float> c1 = Vector128.Create(Cos2PiOver5);
        Vector128<float> c2 = Vector128.Create(Cos4PiOver5);
        Vector128<float> s1 = Vector128.Create(Sin2PiOver5);
        Vector128<float> s2 = Vector128.Create(Sin4PiOver5);
        Vector128<float> t1r = v1r + v4r;
        Vector128<float> t1i = v1i + v4i;
        Vector128<float> t2r = v2r + v3r;
        Vector128<float> t2i = v2i + v3i;
        Vector128<float> t3r = v1r - v4r;
        Vector128<float> t3i = v1i - v4i;
        Vector128<float> t4r = v2r - v3r;
        Vector128<float> t4i = v2i - v3i;

        Vector128<float> m1r = T.MulAdd(T.MulAdd(v0r, c1, t1r), c2, t2r);
        Vector128<float> m1i = T.MulAdd(T.MulAdd(v0i, c1, t1i), c2, t2i);
        Vector128<float> m2r = T.MulAdd(T.MulAdd(v0r, c2, t1r), c1, t2r);
        Vector128<float> m2i = T.MulAdd(T.MulAdd(v0i, c2, t1i), c1, t2i);

        Vector128<float> q1r = T.MulAdd(s1 * t3r, s2, t4r);
        Vector128<float> q1i = T.MulAdd(s1 * t3i, s2, t4i);
        Vector128<float> q2r = T.MulSub(s2 * t3r, s1, t4r);
        Vector128<float> q2i = T.MulSub(s2 * t3i, s1, t4i);

        y0r = v0r + t1r + t2r;
        y0i = v0i + t1i + t2i;
        y1r = T.MulAdd(m1r, sign, q1i);
        y1i = T.MulSub(m1i, sign, q1r);
        y2r = T.MulAdd(m2r, sign, q2i);
        y2i = T.MulSub(m2i, sign, q2r);
        y3r = T.MulSub(m2r, sign, q2i);
        y3i = T.MulAdd(m2i, sign, q2r);
        y4r = T.MulSub(m1r, sign, q1i);
        y4i = T.MulAdd(m1i, sign, q1r);
    }

    internal static void FftCombine(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse)
    {
        int total = offsets.Length * radix * half;
        if (half < 4 || real.Length < total || imaginary.Length < total
            || twiddleReal.Length < radix * half || twiddleImaginary.Length < radix * half)
        {
            if (half >= 4)
            {
                throw new ArgumentException("An FFT buffer is too short.");
            }

            FftKernels.CombineMany(radix, real, imaginary, offsets, half, twiddleReal, twiddleImaginary, inverse, useVector: false);
            return;
        }

        ref float re = ref MemoryMarshal.GetReference(real);
        ref float im = ref MemoryMarshal.GetReference(imaginary);
        ref float twr = ref MemoryMarshal.GetReference(twiddleReal);
        ref float twi = ref MemoryMarshal.GetReference(twiddleImaginary);
        switch (radix)
        {
            case 2:
                Combine2(ref re, ref im, offsets, half, ref twr, ref twi);
                break;
            case 3:
                Combine3(ref re, ref im, offsets, half, ref twr, ref twi, inverse);
                break;
            case 4:
                Combine4(ref re, ref im, offsets, half, ref twr, ref twi, inverse);
                break;
            case 5:
                Combine5(ref re, ref im, offsets, half, ref twr, ref twi, inverse);
                break;
            default:
                throw new NotSupportedException($"FFT radix {radix} is not supported.");
        }
    }

    private static void Combine2(ref float re, ref float im, ReadOnlySpan<int> offsets, int half, ref float twr, ref float twi)
    {
        Vector128<float> mask = TailMask(half);
        bool hasTail = (half & 3) != 0;
        for (int node = 0; node < offsets.Length; node++)
        {
            int off = offsets[node];
            int p = 0;
            for (; p + 4 <= half; p += 4)
            {
                Block2(ref re, ref im, off, half, ref twr, ref twi, p, mask, masked: false);
            }

            if (hasTail)
            {
                Block2(ref re, ref im, off, half, ref twr, ref twi, half - 4, mask, masked: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block2(
        ref float re, ref float im, int off, int half, ref float twr, ref float twi, int p, Vector128<float> mask, bool masked)
    {
        int i0 = off + p;
        int i1 = i0 + half;
        Vector128<float> ar = Ld(ref re, i0);
        Vector128<float> ai = Ld(ref im, i0);
        Vector128<float> br = Ld(ref re, i1);
        Vector128<float> bi = Ld(ref im, i1);
        Tw(br, bi, Ld(ref twr, half + p), Ld(ref twi, half + p), out Vector128<float> vr, out Vector128<float> vi);
        Bfly2(ar, ai, vr, vi, out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i);
        St(ref re, i0, y0r, ar, mask, masked);
        St(ref im, i0, y0i, ai, mask, masked);
        St(ref re, i1, y1r, br, mask, masked);
        St(ref im, i1, y1i, bi, mask, masked);
    }

    private static void Combine3(
        ref float re, ref float im, ReadOnlySpan<int> offsets, int half, ref float twr, ref float twi, bool inverse)
    {
        Vector128<float> mask = TailMask(half);
        Vector128<float> ksign = Vector128.Create((MathF.Sqrt(3f) / 2f) * (inverse ? -1f : 1f));
        bool hasTail = (half & 3) != 0;
        for (int node = 0; node < offsets.Length; node++)
        {
            int off = offsets[node];
            int p = 0;
            for (; p + 4 <= half; p += 4)
            {
                Block3(ref re, ref im, off, half, ref twr, ref twi, p, ksign, mask, masked: false);
            }

            if (hasTail)
            {
                Block3(ref re, ref im, off, half, ref twr, ref twi, half - 4, ksign, mask, masked: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block3(
        ref float re, ref float im, int off, int half, ref float twr, ref float twi, int p,
        Vector128<float> ksign, Vector128<float> mask, bool masked)
    {
        int i0 = off + p;
        int i1 = i0 + half;
        int i2 = i1 + half;
        Vector128<float> ar = Ld(ref re, i0);
        Vector128<float> ai = Ld(ref im, i0);
        Vector128<float> br = Ld(ref re, i1);
        Vector128<float> bi = Ld(ref im, i1);
        Vector128<float> cr = Ld(ref re, i2);
        Vector128<float> ci = Ld(ref im, i2);
        Tw(br, bi, Ld(ref twr, half + p), Ld(ref twi, half + p), out Vector128<float> v1r, out Vector128<float> v1i);
        Tw(cr, ci, Ld(ref twr, (2 * half) + p), Ld(ref twi, (2 * half) + p), out Vector128<float> v2r, out Vector128<float> v2i);
        Bfly3(
            ar, ai, v1r, v1i, v2r, v2i, ksign,
            out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
            out Vector128<float> y2r, out Vector128<float> y2i);
        St(ref re, i0, y0r, ar, mask, masked);
        St(ref im, i0, y0i, ai, mask, masked);
        St(ref re, i1, y1r, br, mask, masked);
        St(ref im, i1, y1i, bi, mask, masked);
        St(ref re, i2, y2r, cr, mask, masked);
        St(ref im, i2, y2i, ci, mask, masked);
    }

    private static void Combine4(
        ref float re, ref float im, ReadOnlySpan<int> offsets, int half, ref float twr, ref float twi, bool inverse)
    {
        Vector128<float> mask = TailMask(half);
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        bool hasTail = (half & 3) != 0;
        for (int node = 0; node < offsets.Length; node++)
        {
            int off = offsets[node];
            int p = 0;
            for (; p + 4 <= half; p += 4)
            {
                Block4(ref re, ref im, off, half, ref twr, ref twi, p, sign, mask, masked: false);
            }

            if (hasTail)
            {
                Block4(ref re, ref im, off, half, ref twr, ref twi, half - 4, sign, mask, masked: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block4(
        ref float re, ref float im, int off, int half, ref float twr, ref float twi, int p,
        Vector128<float> sign, Vector128<float> mask, bool masked)
    {
        int i0 = off + p;
        int i1 = i0 + half;
        int i2 = i1 + half;
        int i3 = i2 + half;
        Vector128<float> ar = Ld(ref re, i0);
        Vector128<float> ai = Ld(ref im, i0);
        Vector128<float> br = Ld(ref re, i1);
        Vector128<float> bi = Ld(ref im, i1);
        Vector128<float> cr = Ld(ref re, i2);
        Vector128<float> ci = Ld(ref im, i2);
        Vector128<float> dr = Ld(ref re, i3);
        Vector128<float> di = Ld(ref im, i3);
        Tw(br, bi, Ld(ref twr, half + p), Ld(ref twi, half + p), out Vector128<float> v1r, out Vector128<float> v1i);
        Tw(cr, ci, Ld(ref twr, (2 * half) + p), Ld(ref twi, (2 * half) + p), out Vector128<float> v2r, out Vector128<float> v2i);
        Tw(dr, di, Ld(ref twr, (3 * half) + p), Ld(ref twi, (3 * half) + p), out Vector128<float> v3r, out Vector128<float> v3i);
        Bfly4(
            ar, ai, v1r, v1i, v2r, v2i, v3r, v3i, sign,
            out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
            out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i);
        St(ref re, i0, y0r, ar, mask, masked);
        St(ref im, i0, y0i, ai, mask, masked);
        St(ref re, i1, y1r, br, mask, masked);
        St(ref im, i1, y1i, bi, mask, masked);
        St(ref re, i2, y2r, cr, mask, masked);
        St(ref im, i2, y2i, ci, mask, masked);
        St(ref re, i3, y3r, dr, mask, masked);
        St(ref im, i3, y3i, di, mask, masked);
    }

    private static void Combine5(
        ref float re, ref float im, ReadOnlySpan<int> offsets, int half, ref float twr, ref float twi, bool inverse)
    {
        Vector128<float> mask = TailMask(half);
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        bool hasTail = (half & 3) != 0;
        for (int node = 0; node < offsets.Length; node++)
        {
            int off = offsets[node];
            int p = 0;
            for (; p + 4 <= half; p += 4)
            {
                Block5(ref re, ref im, off, half, ref twr, ref twi, p, sign, mask, masked: false);
            }

            if (hasTail)
            {
                Block5(ref re, ref im, off, half, ref twr, ref twi, half - 4, sign, mask, masked: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block5(
        ref float re, ref float im, int off, int half, ref float twr, ref float twi, int p,
        Vector128<float> sign, Vector128<float> mask, bool masked)
    {
        int i0 = off + p;
        int i1 = i0 + half;
        int i2 = i1 + half;
        int i3 = i2 + half;
        int i4 = i3 + half;
        Vector128<float> ar = Ld(ref re, i0);
        Vector128<float> ai = Ld(ref im, i0);
        Vector128<float> br = Ld(ref re, i1);
        Vector128<float> bi = Ld(ref im, i1);
        Vector128<float> cr = Ld(ref re, i2);
        Vector128<float> ci = Ld(ref im, i2);
        Vector128<float> dr = Ld(ref re, i3);
        Vector128<float> di = Ld(ref im, i3);
        Vector128<float> er = Ld(ref re, i4);
        Vector128<float> ei = Ld(ref im, i4);
        Tw(br, bi, Ld(ref twr, half + p), Ld(ref twi, half + p), out Vector128<float> v1r, out Vector128<float> v1i);
        Tw(cr, ci, Ld(ref twr, (2 * half) + p), Ld(ref twi, (2 * half) + p), out Vector128<float> v2r, out Vector128<float> v2i);
        Tw(dr, di, Ld(ref twr, (3 * half) + p), Ld(ref twi, (3 * half) + p), out Vector128<float> v3r, out Vector128<float> v3i);
        Tw(er, ei, Ld(ref twr, (4 * half) + p), Ld(ref twi, (4 * half) + p), out Vector128<float> v4r, out Vector128<float> v4i);
        Bfly5(
            ar, ai, v1r, v1i, v2r, v2i, v3r, v3i, v4r, v4i, sign,
            out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
            out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i,
            out Vector128<float> y4r, out Vector128<float> y4i);
        St(ref re, i0, y0r, ar, mask, masked);
        St(ref im, i0, y0i, ai, mask, masked);
        St(ref re, i1, y1r, br, mask, masked);
        St(ref im, i1, y1i, bi, mask, masked);
        St(ref re, i2, y2r, cr, mask, masked);
        St(ref im, i2, y2i, ci, mask, masked);
        St(ref re, i3, y3r, dr, mask, masked);
        St(ref im, i3, y3i, di, mask, masked);
        St(ref re, i4, y4r, er, mask, masked);
        St(ref im, i4, y4i, ei, mask, masked);
    }

    /// <summary>
    /// The leaf level with the decimation permutation fused in. Four leaf nodes are transformed per
    /// step: their inputs are contiguous loads (no twiddles, they are all 1), and only the stores
    /// scatter, because consecutive leaf nodes land in non-adjacent output blocks.
    /// </summary>
    internal static void FftLeaf(
        int radix,
        ReadOnlySpan<float> sourceReal,
        ReadOnlySpan<float> sourceImaginary,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse)
    {
        int nodes = offsets.Length;
        int total = nodes * radix;
        if (sourceReal.Length < total || sourceImaginary.Length < total || real.Length < total || imaginary.Length < total)
        {
            throw new ArgumentException("An FFT buffer is too short.");
        }

        ref float sr = ref MemoryMarshal.GetReference(sourceReal);
        ref float si = ref MemoryMarshal.GetReference(sourceImaginary);
        ref float re = ref MemoryMarshal.GetReference(real);
        ref float im = ref MemoryMarshal.GetReference(imaginary);
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        Vector128<float> ksign = Vector128.Create((MathF.Sqrt(3f) / 2f) * (inverse ? -1f : 1f));
        int node = 0;
        for (; node + 4 <= nodes; node += 4)
        {
            int b0 = offsets[node];
            int b1 = offsets[node + 1];
            int b2 = offsets[node + 2];
            int b3 = offsets[node + 3];
            switch (radix)
            {
                case 2:
                {
                    Bfly2(
                        Ld(ref sr, node), Ld(ref si, node),
                        Ld(ref sr, node + nodes), Ld(ref si, node + nodes),
                        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 0, y0r, y0i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 1, y1r, y1i);
                    break;
                }

                case 3:
                {
                    Bfly3(
                        Ld(ref sr, node), Ld(ref si, node),
                        Ld(ref sr, node + nodes), Ld(ref si, node + nodes),
                        Ld(ref sr, node + (2 * nodes)), Ld(ref si, node + (2 * nodes)), ksign,
                        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
                        out Vector128<float> y2r, out Vector128<float> y2i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 0, y0r, y0i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 1, y1r, y1i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 2, y2r, y2i);
                    break;
                }

                case 4:
                {
                    Bfly4(
                        Ld(ref sr, node), Ld(ref si, node),
                        Ld(ref sr, node + nodes), Ld(ref si, node + nodes),
                        Ld(ref sr, node + (2 * nodes)), Ld(ref si, node + (2 * nodes)),
                        Ld(ref sr, node + (3 * nodes)), Ld(ref si, node + (3 * nodes)), sign,
                        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
                        out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 0, y0r, y0i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 1, y1r, y1i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 2, y2r, y2i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 3, y3r, y3i);
                    break;
                }

                case 5:
                {
                    Bfly5(
                        Ld(ref sr, node), Ld(ref si, node),
                        Ld(ref sr, node + nodes), Ld(ref si, node + nodes),
                        Ld(ref sr, node + (2 * nodes)), Ld(ref si, node + (2 * nodes)),
                        Ld(ref sr, node + (3 * nodes)), Ld(ref si, node + (3 * nodes)),
                        Ld(ref sr, node + (4 * nodes)), Ld(ref si, node + (4 * nodes)), sign,
                        out Vector128<float> y0r, out Vector128<float> y0i, out Vector128<float> y1r, out Vector128<float> y1i,
                        out Vector128<float> y2r, out Vector128<float> y2i, out Vector128<float> y3r, out Vector128<float> y3i,
                        out Vector128<float> y4r, out Vector128<float> y4i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 0, y0r, y0i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 1, y1r, y1i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 2, y2r, y2i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 3, y3r, y3i);
                    Scatter(ref re, ref im, b0, b1, b2, b3, 4, y4r, y4i);
                    break;
                }

                default:
                    throw new NotSupportedException($"FFT radix {radix} is not supported.");
            }
        }

        // Remaining nodes (fewer than four): permute, then the scalar one-node butterfly.
        for (; node < nodes; node++)
        {
            int destination = offsets[node];
            for (int s = 0; s < radix; s++)
            {
                real[destination + s] = sourceReal[node + (s * nodes)];
                imaginary[destination + s] = sourceImaginary[node + (s * nodes)];
            }

            FftKernels.Combine(radix, real, imaginary, destination, 1, twiddleReal, twiddleImaginary, inverse, useVector: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Scatter(
        ref float re, ref float im, int b0, int b1, int b2, int b3, int k, Vector128<float> vr, Vector128<float> vi)
    {
        Unsafe.Add(ref re, b0 + k) = vr.GetElement(0);
        Unsafe.Add(ref re, b1 + k) = vr.GetElement(1);
        Unsafe.Add(ref re, b2 + k) = vr.GetElement(2);
        Unsafe.Add(ref re, b3 + k) = vr.GetElement(3);
        Unsafe.Add(ref im, b0 + k) = vi.GetElement(0);
        Unsafe.Add(ref im, b1 + k) = vi.GetElement(1);
        Unsafe.Add(ref im, b2 + k) = vi.GetElement(2);
        Unsafe.Add(ref im, b3 + k) = vi.GetElement(3);
    }

    // ---- IMDCT ----

    internal static void ImdctPre(
        ReadOnlySpan<float> coefficients,
        int stride,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        int half)
    {
        int p = 0;
        if (stride == 1)
        {
            if (coefficients.Length < count || real.Length < half || imaginary.Length < half
                || rotCos.Length < half || rotSin.Length < half)
            {
                throw new ArgumentException("Buffer too short.");
            }

            ref float c = ref MemoryMarshal.GetReference(coefficients);
            ref float re = ref MemoryMarshal.GetReference(real);
            ref float im = ref MemoryMarshal.GetReference(imaginary);
            ref float rc = ref MemoryMarshal.GetReference(rotCos);
            ref float rs = ref MemoryMarshal.GetReference(rotSin);
            for (; p + 4 <= half; p += 4)
            {
                Vector128<float> a = T.UnzipEven(Ld(ref c, 2 * p), Ld(ref c, (2 * p) + 4));

                // The odd partners sit at count-1-2p-2q: window [count-8-2p, count-2p), reversed.
                Vector128<float> b = T.Reverse(T.UnzipOdd(Ld(ref c, count - 8 - (2 * p)), Ld(ref c, count - 4 - (2 * p))));
                Vector128<float> cos = Ld(ref rc, p);
                Vector128<float> sin = Ld(ref rs, p);
                T.MulSub(a * sin, b, cos).StoreUnsafe(ref re, (nuint)p);
                (-T.MulAdd(a * cos, b, sin)).StoreUnsafe(ref im, (nuint)p);
            }
        }

        int even = 2 * p;
        int odd = count - 1 - (2 * p);
        for (; p < half; p++)
        {
            float a = coefficients[even * stride];
            float b = coefficients[odd * stride];
            float cos = rotCos[p];
            float sin = rotSin[p];
            real[p] = (a * sin) - (b * cos);
            imaginary[p] = -((a * cos) + (b * sin));
            even += 2;
            odd -= 2;
        }
    }

    internal static void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half)
    {
        if (real.Length < half || imaginary.Length < half || rotCos.Length < half || rotSin.Length < half
            || deshuffled.Length < 2 * half)
        {
            throw new ArgumentException("Buffer too short.");
        }

        ref float re = ref MemoryMarshal.GetReference(real);
        ref float im = ref MemoryMarshal.GetReference(imaginary);
        ref float rc = ref MemoryMarshal.GetReference(rotCos);
        ref float rs = ref MemoryMarshal.GetReference(rotSin);
        ref float d = ref MemoryMarshal.GetReference(deshuffled);
        int p = 0;
        for (; p + 4 <= half; p += 4)
        {
            // Even outputs: -(re*cos - im*sin) = im*sin - re*cos.
            Vector128<float> even = T.MulSub(Ld(ref im, p) * Ld(ref rs, p), Ld(ref re, p), Ld(ref rc, p));

            // Odd outputs come from the mirrored index: re*sin + im*cos over the reversed block.
            int q = half - 4 - p;
            Vector128<float> oddReversed = T.MulAdd(Ld(ref re, q) * Ld(ref rs, q), Ld(ref im, q), Ld(ref rc, q));
            Vector128<float> odd = T.Reverse(oddReversed);
            T.ZipLow(even, odd).StoreUnsafe(ref d, (nuint)(2 * p));
            T.ZipHigh(even, odd).StoreUnsafe(ref d, (nuint)((2 * p) + 4));
        }

        for (; p < half; p++)
        {
            float r = real[p];
            float i = imaginary[p];
            deshuffled[2 * p] = -((r * rotCos[p]) - (i * rotSin[p]));
            int q = half - 1 - p;
            deshuffled[(2 * p) + 1] = (real[q] * rotSin[q]) + (imaginary[q] * rotCos[q]);
        }
    }

    internal static void ImdctMirror(
        ReadOnlySpan<float> d,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap)
    {
        int halfOverlap = overlap / 2;
        if (d.Length < count || window.Length < overlap || windowReversed.Length < overlap || output.Length < count + overlap)
        {
            throw new ArgumentException("Buffer too short.");
        }

        d.Slice(halfOverlap, count - overlap).CopyTo(output.Slice(overlap));
        int m = 0;
        if ((halfOverlap & 3) == 0)
        {
            ref float dr = ref MemoryMarshal.GetReference(d);
            ref float w = ref MemoryMarshal.GetReference(window);
            ref float wr = ref MemoryMarshal.GetReference(windowReversed);
            ref float o = ref MemoryMarshal.GetReference(output);
            for (; m + 4 <= halfOverlap; m += 4)
            {
                // A: output[m] = -window[m] * d[halfOverlap-1-m]
                (-(Ld(ref w, m) * T.Reverse(Ld(ref dr, halfOverlap - 4 - m)))).StoreUnsafe(ref o, (nuint)m);

                // B: output[halfOverlap+m] = window[halfOverlap+m] * d[m]
                (Ld(ref w, halfOverlap + m) * Ld(ref dr, m)).StoreUnsafe(ref o, (nuint)(halfOverlap + m));

                // C and D share the tail samples of d.
                Vector128<float> tail = Ld(ref dr, count - halfOverlap + m);
                (Ld(ref wr, m) * tail).StoreUnsafe(ref o, (nuint)(count + m));
                T.Reverse(Ld(ref w, m) * tail).StoreUnsafe(ref o, (nuint)(count + overlap - 4 - m));
            }
        }

        CeltScalar.MirrorRamps(d, window, windowReversed, output, count, overlap, m);
    }

    internal static void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap)
    {
        if (raw.Length < count + overlap || state.Length < overlap)
        {
            throw new ArgumentException("Buffer too short.");
        }

        ref float r = ref MemoryMarshal.GetReference(raw);
        ref float s = ref MemoryMarshal.GetReference(state);
        int j = 0;
        for (; j + 4 <= overlap; j += 4)
        {
            (Ld(ref r, j) + Ld(ref s, j)).StoreUnsafe(ref r, (nuint)j);
            Ld(ref r, count + j).StoreUnsafe(ref s, (nuint)j);
        }

        for (; j < overlap; j++)
        {
            Unsafe.Add(ref r, j) += Unsafe.Add(ref s, j);
            Unsafe.Add(ref s, j) = Unsafe.Add(ref r, count + j);
        }
    }

    // ---- synthesis ----

    internal static void Deemphasis(
        ReadOnlySpan<float> source0,
        ReadOnlySpan<float> source1,
        Span<float> destination,
        int samples,
        int channels,
        bool duplicate,
        ref float memory0,
        ref float memory1,
        float scale)
    {
        bool two = channels == 2;
        bool separate = two && !duplicate;
        if (destination.Length < samples * channels || source0.Length < samples || (separate && source1.Length < samples))
        {
            throw new ArgumentException("The PCM buffers are too short.", nameof(destination));
        }

        const float a = CeltTables.DeemphasisCoefficient;
        const float a2 = a * a;
        const float a3 = a2 * a;
        const float a4 = a2 * a2;
        Vector128<float> av = Vector128.Create(a);
        Vector128<float> a2v = Vector128.Create(a2);
        Vector128<float> a4v = Vector128.Create(a4);
        Vector128<float> pw = Vector128.Create(1f, a, a2, a3);
        Vector128<float> sv = Vector128.Create(scale);
        Vector128<float> m0 = Vector128.Create(memory0);
        Vector128<float> m1 = Vector128.Create(memory1);
        ref float s0 = ref MemoryMarshal.GetReference(source0);
        ref float s1 = ref MemoryMarshal.GetReference(source1);
        ref float d = ref MemoryMarshal.GetReference(destination);
        int j = 0;
        for (; j + 4 <= samples; j += 4)
        {
            Vector128<float> x0 = Ld(ref s0, j);
            Vector128<float> p0 = T.MulAdd(x0, av, T.ShiftUp1(x0));
            Vector128<float> q0 = T.MulAdd(p0, a2v, T.ShiftUp2(p0));
            Vector128<float> y0 = T.MulAdd(q0, m0, pw) * sv;
            m0 = T.MulAdd(av * T.Broadcast3(q0), m0, a4v);
            if (!two)
            {
                y0.StoreUnsafe(ref d, (nuint)j);
            }
            else if (duplicate)
            {
                T.ZipLow(y0, y0).StoreUnsafe(ref d, (nuint)(2 * j));
                T.ZipHigh(y0, y0).StoreUnsafe(ref d, (nuint)((2 * j) + 4));
            }
            else
            {
                Vector128<float> x1 = Ld(ref s1, j);
                Vector128<float> p1 = T.MulAdd(x1, av, T.ShiftUp1(x1));
                Vector128<float> q1 = T.MulAdd(p1, a2v, T.ShiftUp2(p1));
                Vector128<float> y1 = T.MulAdd(q1, m1, pw) * sv;
                m1 = T.MulAdd(av * T.Broadcast3(q1), m1, a4v);
                T.ZipLow(y0, y1).StoreUnsafe(ref d, (nuint)(2 * j));
                T.ZipHigh(y0, y1).StoreUnsafe(ref d, (nuint)((2 * j) + 4));
            }
        }

        float mem0 = m0.ToScalar();
        float mem1 = m1.ToScalar();
        for (; j < samples; j++)
        {
            float v0 = Unsafe.Add(ref s0, j) + mem0;
            mem0 = a * v0;
            if (!two)
            {
                Unsafe.Add(ref d, j) = v0 * scale;
            }
            else if (duplicate)
            {
                Unsafe.Add(ref d, 2 * j) = v0 * scale;
                Unsafe.Add(ref d, (2 * j) + 1) = v0 * scale;
            }
            else
            {
                float v1 = Unsafe.Add(ref s1, j) + mem1;
                mem1 = a * v1;
                Unsafe.Add(ref d, 2 * j) = v0 * scale;
                Unsafe.Add(ref d, (2 * j) + 1) = v1 * scale;
            }
        }

        memory0 = mem0;
        if (separate)
        {
            memory1 = mem1;
        }
    }
}

/// <summary>The CELT encoder analysis kernels, written once over the tier's multiply-add.</summary>
/// <typeparam name="T">The tier's vector operations.</typeparam>
internal static class CeltEncVec<T>
    where T : struct, IVecOps
{
    /// <summary>The multi-lag cross-correlation; each lag accumulates exactly like <c>CeltVec&lt;T&gt;.Dot</c>.</summary>
    internal static void XcorrLags(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int lagCount, int len, Span<float> output)
    {
        if (len > x.Length || (long)lagCount - 1 + len > y.Length || output.Length < lagCount)
        {
            throw new ArgumentOutOfRangeException(nameof(len));
        }

        ref float xr = ref MemoryMarshal.GetReference(x);
        ref float yr = ref MemoryMarshal.GetReference(y);
        int i = 0;
        for (; i + 4 <= lagCount; i += 4)
        {
            Vector128<float> a0 = Vector128<float>.Zero;
            Vector128<float> a1 = Vector128<float>.Zero;
            Vector128<float> a2 = Vector128<float>.Zero;
            Vector128<float> a3 = Vector128<float>.Zero;
            int n = 0;
            for (; n + 4 <= len; n += 4)
            {
                Vector128<float> xv = Vector128.LoadUnsafe(ref xr, (nuint)n);
                a0 = T.MulAdd(a0, xv, Vector128.LoadUnsafe(ref yr, (nuint)(i + n)));
                a1 = T.MulAdd(a1, xv, Vector128.LoadUnsafe(ref yr, (nuint)(i + 1 + n)));
                a2 = T.MulAdd(a2, xv, Vector128.LoadUnsafe(ref yr, (nuint)(i + 2 + n)));
                a3 = T.MulAdd(a3, xv, Vector128.LoadUnsafe(ref yr, (nuint)(i + 3 + n)));
            }

            float s0 = Vector128.Sum(a0);
            float s1 = Vector128.Sum(a1);
            float s2 = Vector128.Sum(a2);
            float s3 = Vector128.Sum(a3);
            for (; n < len; n++)
            {
                float xv = Unsafe.Add(ref xr, n);
                s0 += xv * Unsafe.Add(ref yr, i + n);
                s1 += xv * Unsafe.Add(ref yr, i + 1 + n);
                s2 += xv * Unsafe.Add(ref yr, i + 2 + n);
                s3 += xv * Unsafe.Add(ref yr, i + 3 + n);
            }

            output[i] = s0;
            output[i + 1] = s1;
            output[i + 2] = s2;
            output[i + 3] = s3;
        }

        for (; i < lagCount; i++)
        {
            output[i] = CeltVec<T>.Dot(x[..len], y.Slice(i, len));
        }
    }
}
