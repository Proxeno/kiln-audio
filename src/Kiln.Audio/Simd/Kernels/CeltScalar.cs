using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The scalar reference implementation of every <see cref="ICeltKernels"/> member. It uses no
/// vector type and no fused multiply-add, so it is the oracle the other tiers are compared with
/// and the path <c>KILN_AUDIO_DISABLE_SIMD</c> selects.
/// </summary>
internal static class CeltScalar
{
    internal static float SumSquares(ReadOnlySpan<float> x)
    {
        float sum = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return sum;
    }

    internal static void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy)
    {
        float a = 0f;
        float b = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            a += y[i] * x[i];
            b += y[i] * y[i];
        }

        xy = a;
        yy = b;
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
            RotateStride1<NoFmaScalar>(x, length, c, s);
            return;
        }

        for (int i = 0; i < length - stride; i++)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) - (s * x2);
        }

        for (int i = length - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) - (s * x2);
        }
    }

    /// <summary>The stride-1 rotation: two serial recurrences. Generic over scalar multiply-add.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RotateStride1<TF>(Span<float> x, int length, float c, float s)
        where TF : struct, IScalarFma
    {
        ref float r = ref MemoryMarshal.GetReference(x);
        float negS = -s;
        float carry = r;
        int i;
        for (i = 0; i < length - 1; i++)
        {
            float x2 = Unsafe.Add(ref r, i + 1);
            Unsafe.Add(ref r, i) = TF.MulAdd(c * carry, negS, x2);
            carry = TF.MulAdd(c * x2, s, carry);
        }

        Unsafe.Add(ref r, length - 1) = carry;
        if (length >= 3)
        {
            carry = Unsafe.Add(ref r, length - 2);
            for (i = length - 3; i >= 0; i--)
            {
                float x1 = Unsafe.Add(ref r, i);
                Unsafe.Add(ref r, i + 1) = TF.MulAdd(c * carry, s, x1);
                carry = TF.MulAdd(c * x1, negS, carry);
            }

            r = carry;
        }
    }

    internal static void Haar(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float tmp1 = 0.70710678f * x[(stride * (2 * j)) + i];
                float tmp2 = 0.70710678f * x[(stride * ((2 * j) + 1)) + i];
                x[(stride * (2 * j)) + i] = tmp1 + tmp2;
                x[(stride * ((2 * j) + 1)) + i] = tmp1 - tmp2;
            }
        }
    }

    internal static void AddInPlace(Span<float> destination, ReadOnlySpan<float> source)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] += source[i];
        }
    }

    internal static void Average(Span<float> x, ReadOnlySpan<float> y)
    {
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = 0.5f * (x[i] + y[i]);
        }
    }

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
        for (int n = 0; n < nodes; n++)
        {
            int destination = offsets[n];
            for (int s = 0; s < radix; s++)
            {
                real[destination + s] = sourceReal[n + (s * nodes)];
                imaginary[destination + s] = sourceImaginary[n + (s * nodes)];
            }
        }

        FftKernels.CombineMany(radix, real, imaginary, offsets, 1, twiddleReal, twiddleImaginary, inverse, useVector: false);
    }

    internal static void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half)
    {
        for (int p = 0; p < half; p++)
        {
            float re = real[p];
            float im = imaginary[p];
            deshuffled[2 * p] = -((re * rotCos[p]) - (im * rotSin[p]));
            int q = half - 1 - p;
            deshuffled[(2 * p) + 1] = (real[q] * rotSin[q]) + (imaginary[q] * rotCos[q]);
        }
    }

    /// <summary>The scalar window ramps of the TDAC mirror, shared by the vector tiers for odd sizes.</summary>
    internal static void MirrorRamps(
        ReadOnlySpan<float> d,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap,
        int from)
    {
        int halfOverlap = overlap / 2;
        for (int m = from; m < halfOverlap; m++)
        {
            output[m] = -window[m] * d[halfOverlap - 1 - m];
            output[halfOverlap + m] = window[halfOverlap + m] * d[m];
            output[count + m] = windowReversed[m] * d[count - halfOverlap + m];
            output[count + overlap - 1 - m] = window[m] * d[count - halfOverlap + m];
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
        d.Slice(halfOverlap, count - overlap).CopyTo(output.Slice(overlap));
        MirrorRamps(d, window, windowReversed, output, count, overlap, 0);
    }

    internal static void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap)
    {
        for (int j = 0; j < overlap; j++)
        {
            raw[j] += state[j];
            state[j] = raw[count + j];
        }
    }

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
        if (destination.Length < samples * channels)
        {
            throw new ArgumentException("The PCM destination is too short.", nameof(destination));
        }

        float m0 = memory0;
        for (int j = 0; j < samples; j++)
        {
            float value = source0[j] + m0;
            m0 = CeltTables.DeemphasisCoefficient * value;
            destination[j * channels] = value * scale;
            if (duplicate)
            {
                destination[(j * channels) + 1] = value * scale;
            }
        }

        memory0 = m0;
        if (channels == 2 && !duplicate)
        {
            float m1 = memory1;
            for (int j = 0; j < samples; j++)
            {
                float value = source1[j] + m1;
                m1 = CeltTables.DeemphasisCoefficient * value;
                destination[(j * 2) + 1] = value * scale;
            }

            memory1 = m1;
        }
    }
}

/// <summary>Scalar multiply-add: separately rounded or fused.</summary>
internal interface IScalarFma
{
    /// <summary>Computes <c>acc + (a * b)</c>.</summary>
    static abstract float MulAdd(float acc, float a, float b);
}

/// <summary>Separately rounded scalar multiply-add.</summary>
internal readonly struct NoFmaScalar : IScalarFma
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MulAdd(float acc, float a, float b) => acc + (a * b);
}
