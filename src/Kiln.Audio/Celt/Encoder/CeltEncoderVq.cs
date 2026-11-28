// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    /// <param name="n">The vector dimension, at least 2.</param>
    /// <param name="k">The number of pulses, at least 1.</param>
    /// <param name="blocks">The number of short blocks (<c>B</c>).</param>
    /// <param name="spread">The spread decision, 0 to 3.</param>
    /// <param name="enc">The range encoder.</param>
    /// <returns>The collapse mask for the band (RFC 6716 §4.3.5).</returns>
    internal static int AlgQuant<TK>(
        Span<float> x,
        Span<int> iy,
        Span<float> signs,
        Span<float> y,
        int n,
        int k,
        int blocks,
        int spread,
        ref RangeEncoder enc)
        where TK : struct, IEncoderKernels
    {
        EncProf.Mark(30, "  qb: before pvq (split/theta/bookkeeping)");
        ExpRotation<TK>(x, n, 1, blocks, k, spread);
        EncProf.Mark(31, "  qb: spread rotation");
        CeltEncoderKernels.PvqSearch(x, iy, signs, y, n, k);

        EncProf.Mark(32, "  qb: pvq search");
        EncodePulses(iy, n, k, ref enc);
        EncProf.Mark(33, "  qb: pulse index + entropy");
        return ExtractCollapseMask(iy, n, blocks);
    }

    /// <summary>Applies the spread rotation of RFC 6716 §4.3.4.3 (RFC 6716 Appendix A, celt/vq.c: exp_rotation).</summary>
    /// <param name="x">The band coefficients, rotated in place.</param>
    /// <param name="length">The band length.</param>
    /// <param name="direction">Positive for the encoder, negative for the decoder.</param>
    /// <param name="stride">The number of short blocks.</param>
    /// <param name="k">The pulse count.</param>
    /// <param name="spread">The spread decision, 0 to 3.</param>
    internal static void ExpRotation<TK>(Span<float> x, int length, int direction, int stride, int k, int spread)
        where TK : struct, IEncoderKernels
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
                    TK.Rotate(part, part.Length, stride2, s, c);
                }

                TK.Rotate(part, part.Length, 1, c, s);
            }
            else
            {
                TK.Rotate(part, part.Length, 1, c, -s);
                if (stride2 != 0)
                {
                    TK.Rotate(part, part.Length, stride2, s, -c);
                }
            }
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
        ref int pulses = ref MemoryMarshal.GetReference(iy);
        int mask = 0;
        for (int i = 0; i < blocks; i++)
        {
            int index = i * perBlock;
            for (int j = 0; j < perBlock; j++)
            {
                mask |= (Unsafe.Add(ref pulses, index + j) != 0 ? 1 : 0) << i;
            }
        }

        return mask;
    }

    /// <summary>
    /// Writes the PVQ codeword for the integer vector (RFC 6716 Appendix A, celt/cwrs.c:
    /// <c>encode_pulses</c>).
    /// </summary>
    /// <remarks>
    /// ENC-CELT-1: the index is the exact inverse of the decoder's table-driven <c>Cwrsi</c> walk, so it
    /// is O(n) table reads (the decoder's <c>PvqCwrs</c> cells) instead of the O(n*k) recurrence.
    /// </remarks>
    /// <param name="y">The signed pulse vector; the sum of absolute values is <paramref name="k"/>.</param>
    /// <param name="n">The dimension.</param>
    /// <param name="k">The pulse count.</param>
    /// <param name="enc">The range encoder.</param>
    internal static void EncodePulses(ReadOnlySpan<int> y, int n, int k, ref RangeEncoder enc)
    {
        uint index = PulseIndex(y, n, k, out uint nc);
        enc.EncodeUInt(index, nc);
    }

    /// <summary>The codeword index of the signed pulse vector and the codeword count <c>V(n,k)</c>.</summary>
    internal static uint PulseIndex(ReadOnlySpan<int> y, int n, int k, out uint nc)
    {
        ref CeltDecoder.PvqEntry table = ref MemoryMarshal.GetArrayDataReference(CeltDecoder.PvqCwrs);
        ref int yr = ref MemoryMarshal.GetReference(y);
        uint index = 0;
        int left = k;
        for (int j = 0; j < n && left > 0; j++)
        {
            int remaining = n - j;
            int value = Unsafe.Add(ref yr, j);
            if (remaining == 1)
            {
                index += value < 0 ? 1u : 0u;
                break;
            }

            ref CeltDecoder.PvqEntry row = ref Unsafe.Add(ref table, remaining * CeltDecoder.PvqMaxK);
            if (value == 0)
            {
                index += Unsafe.Add(ref row, left).Zero;
                continue;
            }

            int magnitude = value < 0 ? -value : value;
            int next = left - magnitude;
            index += Unsafe.Add(ref row, next).Zero;
            if (value < 0)
            {
                CeltDecoder.PvqEntry e = Unsafe.Add(ref row, left);
                index += e.Zero + e.Below;
            }

            left = next;
        }

        nc = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(CeltDecoder.PvqTable), (n * CeltDecoder.PvqMaxK) + k);
        return index;
    }
}
