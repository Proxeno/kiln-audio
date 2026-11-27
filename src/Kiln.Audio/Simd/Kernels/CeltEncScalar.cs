using System.Runtime.InteropServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>Scalar reference implementations of the ENC-CELT-1 analysis kernels (see <c>CeltEncVec&lt;T&gt;</c>).</summary>
internal static class CeltEncScalar
{
    internal static void Dot4(
        int n,
        ReadOnlySpan<float> a0, ReadOnlySpan<float> b0,
        ReadOnlySpan<float> a1, ReadOnlySpan<float> b1,
        ReadOnlySpan<float> a2, ReadOnlySpan<float> b2,
        ReadOnlySpan<float> a3, ReadOnlySpan<float> b3,
        out float s0, out float s1, out float s2, out float s3)
    {
        s0 = Dot(a0, b0, n);
        s1 = Dot(a1, b1, n);
        s2 = Dot(a2, b2, n);
        s3 = Dot(a3, b3, n);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int n)
    {
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    internal static void SpreadCounts(ReadOnlySpan<float> x, int bandSamples, out int t0, out int t1, out int t2)
    {
        int c0 = 0;
        int c1 = 0;
        int c2 = 0;
        for (int j = 0; j < x.Length; j++)
        {
            float value = x[j];
            float x2N = value * value * bandSamples;
            c0 += x2N < 0.25f ? 1 : 0;
            c1 += x2N < 0.0625f ? 1 : 0;
            c2 += x2N < 0.015625f ? 1 : 0;
        }

        t0 = c0;
        t1 = c1;
        t2 = c2;
    }

    internal static void Autocorr5(ReadOnlySpan<float> x, Span<float> ac)
    {
        for (int l = 4; l >= 0; l--)
        {
            float d = 0f;
            for (int i = l; i < x.Length; i++)
            {
                d += x[i] * x[i - l];
            }

            ac[l] = d;
        }
    }

    internal static void Fir4(Span<float> x, float t0, float t1, float t2, float t3)
    {
        float m0 = 0f;
        float m1 = 0f;
        float m2 = 0f;
        float m3 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            float sum = v;
            sum += t0 * m0;
            sum += t1 * m1;
            sum += t2 * m2;
            sum += t3 * m3;
            m3 = m2;
            m2 = m1;
            m1 = m0;
            m0 = v;
            x[i] = sum;
        }
    }

    internal static void Fir1(Span<float> x, float t0)
    {
        float m0 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            x[i] = v + (t0 * m0);
            m0 = v;
        }
    }

    internal static void DecimateLowpass(ReadOnlySpan<float> source, Span<float> low, int count, bool accumulate)
    {
        for (int i = 1; i < count; i++)
        {
            float value = (0.25f * (source[(2 * i) - 1] + source[(2 * i) + 1])) + (0.5f * source[2 * i]);
            low[i] = accumulate ? low[i] + value : value;
        }

        float edge = (0.25f * source[1]) + (0.5f * source[0]);
        low[0] = accumulate ? low[0] + edge : edge;
    }

    internal static void BlockMaxAbs(ReadOnlySpan<float> x, int block, Span<float> bins, int binCount)
    {
        for (int b = 0; b < binCount; b++)
        {
            float maxAbs = 0f;
            int offset = b * block;
            for (int j = 0; j < block; j++)
            {
                float value = x[offset + j];
                float magnitude = value < 0f ? -value : value;
                if (magnitude > maxAbs)
                {
                    maxAbs = magnitude;
                }
            }

            bins[b] = maxAbs;
        }
    }

    internal static void MdctFoldMiddle(
        ReadOnlySpan<float> input, int source1, int source2, Span<float> real, Span<float> imaginary, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            real[i] = input[source2];
            imaginary[i] = input[source1];
            source1 += 2;
            source2 -= 2;
        }
    }

    internal static void MdctPostContiguous(
        Span<float> coefficients,
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count)
    {
        int half = count / 2;
        int last = count - 1;
        for (int i = 0; i < half; i++)
        {
            float re = real[i];
            float im = imaginary[i];
            float cos = rotCos[i];
            float sin = rotSin[i];
            coefficients[2 * i] = (re * cos) + (im * sin);
            coefficients[last - (2 * i)] = (re * sin) - (im * cos);
        }
    }
}
