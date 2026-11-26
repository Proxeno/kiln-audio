using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd;

/// <summary>
/// The mixed-radix butterflies of the CELT FFT, with a scalar reference path and a portable
/// <see cref="Vector128{T}"/> path.
/// </summary>
/// <remarks>
/// <para>
/// The transform is stored as separate real and imaginary arrays (structure of arrays), which is
/// what makes the vector path a straight sequence of loads, multiplies and stores with no lane
/// shuffles. Each combine receives the <c>radix</c> contiguous sub-transform results
/// <c>Z_s[p]</c> for <c>p</c> in <c>[0, half)</c> and produces the twiddled combination
/// <c>X[p + half*q] = sum_s W_n^(s*p) W_r^(s*q) Z_s[p]</c>.
/// </para>
/// <para>
/// The twiddle tables passed in are already conjugated for an inverse transform, so the twiddle
/// multiply itself is identical on both paths; only the small DFT kernel changes sign. Each
/// radix is a standard decimation-in-time butterfly: radices 2, 3, 4 and 5 are specialised with
/// additions and the fixed small-DFT constants instead of a generic <c>r * r</c> multiply loop.
/// </para>
/// </remarks>
internal static class FftKernels
{
    private static readonly float Sqrt3Over2 = MathF.Sqrt(3f) / 2f;
    private const float Cos2PiOver5 = 0.3090169943749474f;
    private const float Cos4PiOver5 = -0.8090169943749475f;
    private const float Sin2PiOver5 = 0.9510565162951535f;
    private const float Sin4PiOver5 = 0.5877852522924731f;

    /// <summary>Combines one node of the mixed-radix transform, choosing the vector path when enabled.</summary>
    public static void Combine(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        int offset,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse) =>
        Combine(radix, real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, SimdConfig.UseSimd);

    /// <summary>Combines one node, forcing the vector path (<paramref name="useVector"/> = true) or the scalar path.</summary>
    public static void Combine(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        int offset,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse,
        bool useVector)
    {
        // The vector paths need at least one full vector of p values; below that the scalar
        // loop, which handles its own tail, is always used and is never slower.
        bool vector = useVector && half >= 4;
        switch (radix)
        {
            case 2:
                if (vector)
                {
                    Combine2Vector(real, imaginary, offset, half, twiddleReal, twiddleImaginary);
                }
                else
                {
                    Combine2Scalar(real, imaginary, offset, half, twiddleReal, twiddleImaginary);
                }

                break;

            case 3:
                if (vector)
                {
                    Combine3Vector(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }
                else
                {
                    Combine3Scalar(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }

                break;

            case 4:
                if (vector)
                {
                    Combine4Vector(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }
                else
                {
                    Combine4Scalar(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }

                break;

            case 5:
                if (vector)
                {
                    Combine5Vector(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }
                else
                {
                    Combine5Scalar(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse);
                }

                break;

            default:
                throw new NotSupportedException($"FFT radix {radix} is not supported.");
        }
    }

    /// <summary>
    /// Combines every node of one level. The radix dispatch happens once per level rather than
    /// once per node, which matters at the small half-sizes where the node count is largest.
    /// </summary>
    public static void CombineMany(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse,
        bool useVector)
    {
        bool vector = useVector && half >= 4;
        switch (radix)
        {
            case 2:
                for (int node = 0; node < offsets.Length; node++)
                {
                    if (vector)
                    {
                        Combine2Vector(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary);
                    }
                    else
                    {
                        Combine2Scalar(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary);
                    }
                }

                break;

            case 3:
                for (int node = 0; node < offsets.Length; node++)
                {
                    if (vector)
                    {
                        Combine3Vector(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                    else
                    {
                        Combine3Scalar(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                }

                break;

            case 4:
                for (int node = 0; node < offsets.Length; node++)
                {
                    if (vector)
                    {
                        Combine4Vector(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                    else
                    {
                        Combine4Scalar(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                }

                break;

            case 5:
                for (int node = 0; node < offsets.Length; node++)
                {
                    if (vector)
                    {
                        Combine5Vector(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                    else
                    {
                        Combine5Scalar(real, imaginary, offsets[node], half, twiddleReal, twiddleImaginary, inverse);
                    }
                }

                break;

            default:
                throw new NotSupportedException($"FFT radix {radix} is not supported.");
        }
    }

    /// <summary>Scalar radix-2 combine.</summary>
    public static void Combine2Scalar(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary) =>
        Combine2ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, 0);

    private static void Combine2ScalarRange(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, int from)
    {
        for (int p = from; p < half; p++)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            float ar = real[i0];
            float ai = imaginary[i0];
            float cr = twiddleReal[half + p];
            float ci = twiddleImaginary[half + p];
            float br = real[i1];
            float bi = imaginary[i1];
            float tr = (br * cr) - (bi * ci);
            float ti = (br * ci) + (bi * cr);
            real[i0] = ar + tr;
            imaginary[i0] = ai + ti;
            real[i1] = ar - tr;
            imaginary[i1] = ai - ti;
        }
    }

    /// <summary>Vector radix-2 combine.</summary>
    public static void Combine2Vector(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary)
    {
        int p = 0;
        for (; p + 4 <= half; p += 4)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            Vector128<float> ar = Load(real, i0);
            Vector128<float> ai = Load(imaginary, i0);
            Vector128<float> cr = Load(twiddleReal, half + p);
            Vector128<float> ci = Load(twiddleImaginary, half + p);
            Vector128<float> br = Load(real, i1);
            Vector128<float> bi = Load(imaginary, i1);
            Vector128<float> tr = (br * cr) - (bi * ci);
            Vector128<float> ti = (br * ci) + (bi * cr);
            Store(real, i0, ar + tr);
            Store(imaginary, i0, ai + ti);
            Store(real, i1, ar - tr);
            Store(imaginary, i1, ai - ti);
        }

        Combine2ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, p);
    }

    /// <summary>Scalar radix-3 combine.</summary>
    public static void Combine3Scalar(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse) =>
        Combine3ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, 0);

    private static void Combine3ScalarRange(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse, int from)
    {
        float sign = inverse ? -1f : 1f;
        for (int p = from; p < half; p++)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            int i2 = offset + (2 * half) + p;

            float cr = twiddleReal[half + p];
            float ci = twiddleImaginary[half + p];
            float br = real[i1];
            float bi = imaginary[i1];
            float v1r = (br * cr) - (bi * ci);
            float v1i = (br * ci) + (bi * cr);

            cr = twiddleReal[(2 * half) + p];
            ci = twiddleImaginary[(2 * half) + p];
            br = real[i2];
            bi = imaginary[i2];
            float v2r = (br * cr) - (bi * ci);
            float v2i = (br * ci) + (bi * cr);

            float v0r = real[i0];
            float v0i = imaginary[i0];
            float sr = v1r + v2r;
            float si = v1i + v2i;
            float dr = v1r - v2r;
            float di = v1i - v2i;
            float baseR = v0r - (0.5f * sr);
            float baseI = v0i - (0.5f * si);
            float kr = Sqrt3Over2 * di * sign;
            float ki = Sqrt3Over2 * dr * sign;

            real[i0] = v0r + sr;
            imaginary[i0] = v0i + si;
            real[i1] = baseR + kr;
            imaginary[i1] = baseI - ki;
            real[i2] = baseR - kr;
            imaginary[i2] = baseI + ki;
        }
    }

    /// <summary>Vector radix-3 combine.</summary>
    public static void Combine3Vector(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse)
    {
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        Vector128<float> halfVector = Vector128.Create(0.5f);
        Vector128<float> kVector = Vector128.Create(Sqrt3Over2);
        int p = 0;
        for (; p + 4 <= half; p += 4)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            int i2 = offset + (2 * half) + p;

            Vector128<float> cr = Load(twiddleReal, half + p);
            Vector128<float> ci = Load(twiddleImaginary, half + p);
            Vector128<float> br = Load(real, i1);
            Vector128<float> bi = Load(imaginary, i1);
            Vector128<float> v1r = (br * cr) - (bi * ci);
            Vector128<float> v1i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (2 * half) + p);
            ci = Load(twiddleImaginary, (2 * half) + p);
            br = Load(real, i2);
            bi = Load(imaginary, i2);
            Vector128<float> v2r = (br * cr) - (bi * ci);
            Vector128<float> v2i = (br * ci) + (bi * cr);

            Vector128<float> v0r = Load(real, i0);
            Vector128<float> v0i = Load(imaginary, i0);
            Vector128<float> sr = v1r + v2r;
            Vector128<float> si = v1i + v2i;
            Vector128<float> dr = v1r - v2r;
            Vector128<float> di = v1i - v2i;
            Vector128<float> baseR = v0r - (halfVector * sr);
            Vector128<float> baseI = v0i - (halfVector * si);
            Vector128<float> kr = kVector * di * sign;
            Vector128<float> ki = kVector * dr * sign;

            Store(real, i0, v0r + sr);
            Store(imaginary, i0, v0i + si);
            Store(real, i1, baseR + kr);
            Store(imaginary, i1, baseI - ki);
            Store(real, i2, baseR - kr);
            Store(imaginary, i2, baseI + ki);
        }

        Combine3ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, p);
    }

    /// <summary>Scalar radix-4 combine.</summary>
    public static void Combine4Scalar(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse) =>
        Combine4ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, 0);

    private static void Combine4ScalarRange(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse, int from)
    {
        float sign = inverse ? -1f : 1f;
        for (int p = from; p < half; p++)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            int i2 = offset + (2 * half) + p;
            int i3 = offset + (3 * half) + p;

            float v0r = real[i0];
            float v0i = imaginary[i0];

            float cr = twiddleReal[half + p];
            float ci = twiddleImaginary[half + p];
            float br = real[i1];
            float bi = imaginary[i1];
            float v1r = (br * cr) - (bi * ci);
            float v1i = (br * ci) + (bi * cr);

            cr = twiddleReal[(2 * half) + p];
            ci = twiddleImaginary[(2 * half) + p];
            br = real[i2];
            bi = imaginary[i2];
            float v2r = (br * cr) - (bi * ci);
            float v2i = (br * ci) + (bi * cr);

            cr = twiddleReal[(3 * half) + p];
            ci = twiddleImaginary[(3 * half) + p];
            br = real[i3];
            bi = imaginary[i3];
            float v3r = (br * cr) - (bi * ci);
            float v3i = (br * ci) + (bi * cr);

            float t0r = v0r + v2r;
            float t0i = v0i + v2i;
            float t1r = v0r - v2r;
            float t1i = v0i - v2i;
            float t2r = v1r + v3r;
            float t2i = v1i + v3i;
            float t3r = (v1r - v3r) * sign;
            float t3i = (v1i - v3i) * sign;

            real[i0] = t0r + t2r;
            imaginary[i0] = t0i + t2i;
            real[i1] = t1r + t3i;
            imaginary[i1] = t1i - t3r;
            real[i2] = t0r - t2r;
            imaginary[i2] = t0i - t2i;
            real[i3] = t1r - t3i;
            imaginary[i3] = t1i + t3r;
        }
    }

    /// <summary>Vector radix-4 combine.</summary>
    public static void Combine4Vector(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse)
    {
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        int p = 0;
        for (; p + 4 <= half; p += 4)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            int i2 = offset + (2 * half) + p;
            int i3 = offset + (3 * half) + p;

            Vector128<float> v0r = Load(real, i0);
            Vector128<float> v0i = Load(imaginary, i0);

            Vector128<float> cr = Load(twiddleReal, half + p);
            Vector128<float> ci = Load(twiddleImaginary, half + p);
            Vector128<float> br = Load(real, i1);
            Vector128<float> bi = Load(imaginary, i1);
            Vector128<float> v1r = (br * cr) - (bi * ci);
            Vector128<float> v1i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (2 * half) + p);
            ci = Load(twiddleImaginary, (2 * half) + p);
            br = Load(real, i2);
            bi = Load(imaginary, i2);
            Vector128<float> v2r = (br * cr) - (bi * ci);
            Vector128<float> v2i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (3 * half) + p);
            ci = Load(twiddleImaginary, (3 * half) + p);
            br = Load(real, i3);
            bi = Load(imaginary, i3);
            Vector128<float> v3r = (br * cr) - (bi * ci);
            Vector128<float> v3i = (br * ci) + (bi * cr);

            Vector128<float> t0r = v0r + v2r;
            Vector128<float> t0i = v0i + v2i;
            Vector128<float> t1r = v0r - v2r;
            Vector128<float> t1i = v0i - v2i;
            Vector128<float> t2r = v1r + v3r;
            Vector128<float> t2i = v1i + v3i;
            Vector128<float> t3r = (v1r - v3r) * sign;
            Vector128<float> t3i = (v1i - v3i) * sign;

            Store(real, i0, t0r + t2r);
            Store(imaginary, i0, t0i + t2i);
            Store(real, i1, t1r + t3i);
            Store(imaginary, i1, t1i - t3r);
            Store(real, i2, t0r - t2r);
            Store(imaginary, i2, t0i - t2i);
            Store(real, i3, t1r - t3i);
            Store(imaginary, i3, t1i + t3r);
        }

        Combine4ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, p);
    }

    /// <summary>Scalar radix-5 combine.</summary>
    public static void Combine5Scalar(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse) =>
        Combine5ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, 0);

    private static void Combine5ScalarRange(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse, int from)
    {
        float sign = inverse ? -1f : 1f;
        for (int p = from; p < half; p++)
        {
            int i0 = offset + p;

            float v0r = real[i0];
            float v0i = imaginary[i0];

            float v1r = Twiddle(real, imaginary, offset + half + p, twiddleReal, twiddleImaginary, half + p, out float v1i);
            float v2r = Twiddle(real, imaginary, offset + (2 * half) + p, twiddleReal, twiddleImaginary, (2 * half) + p, out float v2i);
            float v3r = Twiddle(real, imaginary, offset + (3 * half) + p, twiddleReal, twiddleImaginary, (3 * half) + p, out float v3i);
            float v4r = Twiddle(real, imaginary, offset + (4 * half) + p, twiddleReal, twiddleImaginary, (4 * half) + p, out float v4i);

            float t1r = v1r + v4r;
            float t1i = v1i + v4i;
            float t2r = v2r + v3r;
            float t2i = v2i + v3i;
            float t3r = v1r - v4r;
            float t3i = v1i - v4i;
            float t4r = v2r - v3r;
            float t4i = v2i - v3i;

            float m1r = v0r + (Cos2PiOver5 * t1r) + (Cos4PiOver5 * t2r);
            float m1i = v0i + (Cos2PiOver5 * t1i) + (Cos4PiOver5 * t2i);
            float m2r = v0r + (Cos4PiOver5 * t1r) + (Cos2PiOver5 * t2r);
            float m2i = v0i + (Cos4PiOver5 * t1i) + (Cos2PiOver5 * t2i);

            float q1r = (Sin2PiOver5 * t3r) + (Sin4PiOver5 * t4r);
            float q1i = (Sin2PiOver5 * t3i) + (Sin4PiOver5 * t4i);
            float q2r = (Sin4PiOver5 * t3r) - (Sin2PiOver5 * t4r);
            float q2i = (Sin4PiOver5 * t3i) - (Sin2PiOver5 * t4i);

            real[i0] = v0r + t1r + t2r;
            imaginary[i0] = v0i + t1i + t2i;
            real[i0 + half] = m1r + (sign * q1i);
            imaginary[i0 + half] = m1i - (sign * q1r);
            real[i0 + (2 * half)] = m2r + (sign * q2i);
            imaginary[i0 + (2 * half)] = m2i - (sign * q2r);
            real[i0 + (3 * half)] = m2r - (sign * q2i);
            imaginary[i0 + (3 * half)] = m2i + (sign * q2r);
            real[i0 + (4 * half)] = m1r - (sign * q1i);
            imaginary[i0 + (4 * half)] = m1i + (sign * q1r);
        }
    }

    /// <summary>Vector radix-5 combine.</summary>
    public static void Combine5Vector(
        Span<float> real, Span<float> imaginary, int offset, int half,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, bool inverse)
    {
        Vector128<float> sign = Vector128.Create(inverse ? -1f : 1f);
        Vector128<float> c1 = Vector128.Create(Cos2PiOver5);
        Vector128<float> c2 = Vector128.Create(Cos4PiOver5);
        Vector128<float> s1 = Vector128.Create(Sin2PiOver5);
        Vector128<float> s2 = Vector128.Create(Sin4PiOver5);
        int p = 0;
        for (; p + 4 <= half; p += 4)
        {
            int i0 = offset + p;
            int i1 = offset + half + p;
            int i2 = offset + (2 * half) + p;
            int i3 = offset + (3 * half) + p;
            int i4 = offset + (4 * half) + p;

            Vector128<float> v0r = Load(real, i0);
            Vector128<float> v0i = Load(imaginary, i0);

            Vector128<float> cr = Load(twiddleReal, half + p);
            Vector128<float> ci = Load(twiddleImaginary, half + p);
            Vector128<float> br = Load(real, i1);
            Vector128<float> bi = Load(imaginary, i1);
            Vector128<float> v1r = (br * cr) - (bi * ci);
            Vector128<float> v1i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (2 * half) + p);
            ci = Load(twiddleImaginary, (2 * half) + p);
            br = Load(real, i2);
            bi = Load(imaginary, i2);
            Vector128<float> v2r = (br * cr) - (bi * ci);
            Vector128<float> v2i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (3 * half) + p);
            ci = Load(twiddleImaginary, (3 * half) + p);
            br = Load(real, i3);
            bi = Load(imaginary, i3);
            Vector128<float> v3r = (br * cr) - (bi * ci);
            Vector128<float> v3i = (br * ci) + (bi * cr);

            cr = Load(twiddleReal, (4 * half) + p);
            ci = Load(twiddleImaginary, (4 * half) + p);
            br = Load(real, i4);
            bi = Load(imaginary, i4);
            Vector128<float> v4r = (br * cr) - (bi * ci);
            Vector128<float> v4i = (br * ci) + (bi * cr);

            Vector128<float> t1r = v1r + v4r;
            Vector128<float> t1i = v1i + v4i;
            Vector128<float> t2r = v2r + v3r;
            Vector128<float> t2i = v2i + v3i;
            Vector128<float> t3r = v1r - v4r;
            Vector128<float> t3i = v1i - v4i;
            Vector128<float> t4r = v2r - v3r;
            Vector128<float> t4i = v2i - v3i;

            Vector128<float> m1r = v0r + (c1 * t1r) + (c2 * t2r);
            Vector128<float> m1i = v0i + (c1 * t1i) + (c2 * t2i);
            Vector128<float> m2r = v0r + (c2 * t1r) + (c1 * t2r);
            Vector128<float> m2i = v0i + (c2 * t1i) + (c1 * t2i);

            Vector128<float> q1r = (s1 * t3r) + (s2 * t4r);
            Vector128<float> q1i = (s1 * t3i) + (s2 * t4i);
            Vector128<float> q2r = (s2 * t3r) - (s1 * t4r);
            Vector128<float> q2i = (s2 * t3i) - (s1 * t4i);

            Store(real, i0, v0r + t1r + t2r);
            Store(imaginary, i0, v0i + t1i + t2i);
            Store(real, i1, m1r + (sign * q1i));
            Store(imaginary, i1, m1i - (sign * q1r));
            Store(real, i2, m2r + (sign * q2i));
            Store(imaginary, i2, m2i - (sign * q2r));
            Store(real, i3, m2r - (sign * q2i));
            Store(imaginary, i3, m2i + (sign * q2r));
            Store(real, i4, m1r - (sign * q1i));
            Store(imaginary, i4, m1i + (sign * q1r));
        }

        Combine5ScalarRange(real, imaginary, offset, half, twiddleReal, twiddleImaginary, inverse, p);
    }

    private static float Twiddle(
        ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, int index,
        ReadOnlySpan<float> twiddleReal, ReadOnlySpan<float> twiddleImaginary, int twiddleIndex,
        out float resultImaginary)
    {
        float cr = twiddleReal[twiddleIndex];
        float ci = twiddleImaginary[twiddleIndex];
        float br = real[index];
        float bi = imaginary[index];
        resultImaginary = (br * ci) + (bi * cr);
        return (br * cr) - (bi * ci);
    }

    private static Vector128<float> Load(ReadOnlySpan<float> values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);

    private static void Store(Span<float> values, int index, Vector128<float> value) =>
        value.StoreUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);
}
