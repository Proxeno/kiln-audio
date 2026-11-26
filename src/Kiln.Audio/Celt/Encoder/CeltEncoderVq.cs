// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>Pyramid vector quantization and its entropy coding (RFC 6716 §4.3.4.2).</summary>
/// <remarks>
/// This is the encoding side of <c>alg_quant</c> (RFC 6716 Appendix A, celt/vq.c) and
/// <c>encode_pulses</c> (celt/cwrs.c). The shapes themselves are produced by
/// <see cref="CeltEncoderKernels.PvqSearch"/>; this file rotates the target, writes the codeword and
/// computes the collapse mask.
/// </remarks>
internal static class CeltEncoderVq
{
    /// <summary>Spread rotation factors (RFC 6716 §4.3.4.3, Table 59), indexed by spread minus one.</summary>
    private static ReadOnlySpan<int> SpreadFactor => [15, 10, 5];

    /// <summary>
    /// Encodes one band with the PVQ search followed by index coding (RFC 6716 Appendix A,
    /// celt/vq.c: <c>alg_quant</c>, without the resynthesis step).
    /// </summary>
    /// <param name="x">The band coefficients; reordered in place by the spread rotation.</param>
    /// <param name="iy">Scratch of length at least <paramref name="n"/>.</param>
    /// <param name="signs">Scratch of length at least <paramref name="n"/>.</param>
    /// <param name="y">Scratch of length at least <paramref name="n"/>.</param>
    /// <param name="u">Scratch for the combinatorial U row, at least <c>k+2</c> entries.</param>
    /// <param name="n">The vector dimension, at least 2.</param>
    /// <param name="k">The number of pulses, at least 1.</param>
    /// <param name="blocks">The number of short blocks (<c>B</c>).</param>
    /// <param name="spread">The spread decision, 0 to 3.</param>
    /// <param name="enc">The range encoder.</param>
    /// <returns>The collapse mask for the band (RFC 6716 §4.3.5).</returns>
    internal static int AlgQuant(
        Span<float> x,
        Span<int> iy,
        Span<float> signs,
        Span<float> y,
        Span<uint> u,
        int n,
        int k,
        int blocks,
        int spread,
        ref RangeEncoder enc)
    {
        ExpRotation(x, n, 1, blocks, k, spread);
        CeltEncoderKernels.PvqSearch(x, iy, signs, y, n, k);
        EncodePulses(iy, n, k, u, ref enc);
        return ExtractCollapseMask(iy, n, blocks);
    }

    /// <summary>Applies the spread rotation of RFC 6716 §4.3.4.3 (RFC 6716 Appendix A, celt/vq.c: exp_rotation).</summary>
    /// <param name="x">The band coefficients, rotated in place.</param>
    /// <param name="length">The band length.</param>
    /// <param name="direction">Positive for the encoder, negative for the decoder.</param>
    /// <param name="stride">The number of short blocks.</param>
    /// <param name="k">The pulse count.</param>
    /// <param name="spread">The spread decision, 0 to 3.</param>
    internal static void ExpRotation(Span<float> x, int length, int direction, int stride, int k, int spread)
    {
        if (2 * k >= length || spread == 0)
        {
            return;
        }

        int factor = SpreadFactor[spread - 1];
        float gain = (float)length / (length + (factor * k));
        float theta = 0.5f * (gain * gain);
        float c = MathF.Cos(0.5f * MathF.PI * theta);
        float s = MathF.Cos(0.5f * MathF.PI * (1f - theta));

        int stride2 = 0;
        if (length >= 8 * stride)
        {
            stride2 = 1;
            while ((((stride2 * stride2) + stride2) * stride) + (stride >> 2) < length)
            {
                stride2++;
            }
        }

        int segment = length / stride;
        for (int i = 0; i < stride; i++)
        {
            Span<float> part = x.Slice(i * segment, segment);
            if (direction < 0)
            {
                if (stride2 != 0)
                {
                    ExpRotation1(part, stride2, s, c);
                }

                ExpRotation1(part, 1, c, s);
            }
            else
            {
                ExpRotation1(part, 1, c, -s);
                if (stride2 != 0)
                {
                    ExpRotation1(part, stride2, s, -c);
                }
            }
        }
    }

    /// <summary>One in-place rotation pass (RFC 6716 Appendix A, celt/vq.c: exp_rotation1).</summary>
    private static void ExpRotation1(Span<float> x, int stride, float c, float s)
    {
        int length = x.Length;
        for (int i = 0; i < length - stride; i++)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) - (s * x2);
        }

        int pointer = length - (2 * stride) - 1;
        for (int i = length - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[pointer];
            float x2 = x[pointer + stride];
            x[pointer + stride] = (c * x2) + (s * x1);
            x[pointer] = (c * x1) - (s * x2);
            pointer--;
        }
    }

    /// <summary>Builds the collapse mask of RFC 6716 §4.3.4 (RFC 6716 Appendix A, celt/vq.c: extract_collapse_mask).</summary>
    private static int ExtractCollapseMask(ReadOnlySpan<int> iy, int n, int blocks)
    {
        if (blocks <= 1)
        {
            return 1;
        }

        int perBlock = n / blocks;
        int mask = 0;
        for (int i = 0; i < blocks; i++)
        {
            for (int j = 0; j < perBlock; j++)
            {
                mask |= (iy[(i * perBlock) + j] != 0 ? 1 : 0) << i;
            }
        }

        return mask;
    }

    /// <summary>
    /// Writes the PVQ codeword for the integer vector (RFC 6716 Appendix A, celt/cwrs.c:
    /// <c>encode_pulses</c>).
    /// </summary>
    /// <param name="y">The signed pulse vector; the sum of absolute values is <paramref name="k"/>.</param>
    /// <param name="n">The dimension.</param>
    /// <param name="k">The pulse count.</param>
    /// <param name="u">Scratch of at least <c>k+2</c> entries.</param>
    /// <param name="enc">The range encoder.</param>
    internal static void EncodePulses(ReadOnlySpan<int> y, int n, int k, Span<uint> u, ref RangeEncoder enc)
    {
        switch (n)
        {
            case 2:
            {
                int kk = k;
                uint index = Icwrs2(y, ref kk);
                enc.EncodeUInt(index, Ncwrs2(kk));
                break;
            }

            case 3:
            {
                int kk = k;
                uint index = Icwrs3(y, ref kk);
                enc.EncodeUInt(index, Ncwrs3(kk));
                break;
            }

            case 4:
            {
                int kk = k;
                uint index = Icwrs4(y, ref kk);
                enc.EncodeUInt(index, Ncwrs4(kk));
                break;
            }

            default:
            {
                uint index = Icwrs(y, n, k, out uint nc, u);
                enc.EncodeUInt(index, nc);
                break;
            }
        }
    }

    /// <summary>Returns the index of a length-1 signed pulse (RFC 6716 Appendix A, celt/cwrs.c: icwrs1).</summary>
    private static uint Icwrs1(ReadOnlySpan<int> y, out int k)
    {
        k = Math.Abs(y[0]);
        return y[0] < 0 ? 1u : 0u;
    }

    private static uint Icwrs2(ReadOnlySpan<int> y, ref int k)
    {
        uint i = Icwrs1(y[1..], out int kk);
        i += kk > 0 ? Ucwrs2(kk) : 0u;
        kk += Math.Abs(y[0]);
        if (y[0] < 0)
        {
            i += Ucwrs2(kk + 1);
        }

        k = kk;
        return i;
    }

    private static uint Icwrs3(ReadOnlySpan<int> y, ref int k)
    {
        uint i = Icwrs2(y[1..], ref k);
        i += k > 0 ? Ucwrs3(k) : 0u;
        k += Math.Abs(y[0]);
        if (y[0] < 0)
        {
            i += Ucwrs3(k + 1);
        }

        return i;
    }

    private static uint Icwrs4(ReadOnlySpan<int> y, ref int k)
    {
        uint i = Icwrs3(y[1..], ref k);
        i += k > 0 ? Ucwrs4(k) : 0u;
        k += Math.Abs(y[0]);
        if (y[0] < 0)
        {
            i += Ucwrs4(k + 1);
        }

        return i;
    }

    private static uint Icwrs(ReadOnlySpan<int> y, int n, int k, out uint nc, Span<uint> u)
    {
        u[0] = 0;
        for (int j = 1; j <= k + 1; j++)
        {
            u[j] = (uint)((j << 1) - 1);
        }

        int acc = Math.Abs(y[n - 1]);
        uint index = y[n - 1] < 0 ? 1u : 0u;
        int position = n - 2;
        index += u[acc];
        acc += Math.Abs(y[position]);
        if (y[position] < 0)
        {
            index += u[acc + 1];
        }

        while (position-- > 0)
        {
            Unext(u, k + 2, 0);
            index += u[acc];
            acc += Math.Abs(y[position]);
            if (y[position] < 0)
            {
                index += u[acc + 1];
            }
        }

        nc = u[acc] + u[acc + 1];
        return index;
    }

    /// <summary>Advances one row of the combinatorial recurrence (RFC 6716 Appendix A, celt/cwrs.c: unext).</summary>
    private static void Unext(Span<uint> u, int length, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = unchecked(u[j] + u[j - 1] + ui0);
            u[j - 1] = ui0;
            ui0 = ui1;
        }
        while (++j < length);

        u[j - 1] = ui0;
    }

    private static uint Ucwrs2(int k) => (uint)(k + (k - 1));

    private static uint Ncwrs2(int k) => (uint)(4 * k);

    private static uint Ucwrs3(int k) => unchecked((uint)(((2 * k) - 2) * k) + 1u);

    private static uint Ncwrs3(int k) => unchecked(2u * ((2u * (uint)k * (uint)k) + 1u));

    private static uint Ucwrs4(int k)
    {
        // (2k * ((2k-3)*k + 4) - 3) / 3, known exact; 64-bit keeps the numerator exact.
        ulong numerator = ((ulong)(2 * k) * (((ulong)((2 * k) - 3) * (ulong)k) + 4)) - 3;
        return (uint)(numerator / 3);
    }

    private static uint Ncwrs4(int k)
    {
        uint kk = (uint)k;
        return ((kk * kk) + 2) * kk / 3u << 3;
    }
}
