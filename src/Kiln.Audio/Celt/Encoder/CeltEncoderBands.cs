// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln.Audio.Entropy;
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// The recursive band quantizer of the CELT encoder (RFC 6716 §4.3.4; RFC 6716 Appendix A,
/// celt/bands.c: <c>quant_all_bands</c> and <c>quant_band</c>).
/// </summary>
/// <remarks>
/// <para>
/// The encoder does not resynthesize its output, so the folding, collapse-mask and low-band
/// machinery of the reference is irrelevant to the bitstream: every symbol is decided from the
/// band's own coefficients and its bit budget. This class therefore performs the reference's
/// pre-quantization transforms and symbol writes only, producing the same bitstream as the
/// reference while doing less work. Stereo bands use the reference's mid/side angle split; dual
/// stereo quantizes both channels independently.
/// </para>
/// <para>
/// The instance owns its scratch buffers and allocates nothing during encoding.
/// </para>
/// </remarks>
internal sealed class CeltEncoderBands
{
    private const int BandCount = CeltTables.BandCount;
    private const int BitRes = CeltTables.BitRes;
    private const int QThetaOffset = 4;
    private const int QThetaOffsetTwoPhase = 16;

    private readonly int[] _iy = new int[CeltTables.MaxFrameSamples];
    private readonly float[] _signs = new float[CeltTables.MaxFrameSamples];
    private readonly float[] _y = new float[CeltTables.MaxFrameSamples];
    private readonly float[] _hadamardScratch = new float[512];

    private readonly int[] _pulses = new int[BandCount];
    private readonly int[] _tfResolution = new int[BandCount];
    private int _band;
    private int _spread;
    private int _intensity;

    /// <summary>
    /// Quantizes every coded band and writes the PVQ symbols (RFC 6716 Appendix A,
    /// celt/bands.c: <c>quant_all_bands</c>).
    /// </summary>
    internal void QuantAllBands<TK>(
        ref RangeEncoder enc,
        ReadOnlySpan<short> eBands,
        int start,
        int end,
        Span<float> spectrum,
        int channels,
        int channelStride,
        int lm,
        bool shortBlocks,
        int spread,
        int codedBands,
        int totalBits,
        int balance,
        ReadOnlySpan<int> pulses,
        ReadOnlySpan<int> tfResolution,
        int intensity,
        int dualStereo,
        ReadOnlySpan<float> bandEnergy)
        where TK : struct, IEncoderKernels
    {
        int m = 1 << lm;
        int blocks = shortBlocks ? m : 1;
        _spread = spread;
        _intensity = intensity;
        pulses.CopyTo(_pulses);
        tfResolution.CopyTo(_tfResolution);

        for (int i = start; i < end; i++)
        {
            _band = i;
            int tell = (int)enc.TellFrac();
            if (i != start)
            {
                balance -= tell;
            }

            int remainingBits = totalBits - tell - 1;
            int b = i <= codedBands - 1
                ? Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, pulses[i] + (balance / Math.Min(3, codedBands - i)))))
                : 0;

            int bandStart = m * eBands[i];
            int bandSamples = m * (eBands[i + 1] - eBands[i]);
            Span<float> x = spectrum.Slice(bandStart, bandSamples);

            if (dualStereo != 0 && i == intensity)
            {
                // Switch off dual stereo for the intensity bands; the normal merge of the
                // per-channel folding history is a decoder/resynthesis concern only.
                dualStereo = 0;
            }

            if (dualStereo != 0)
            {
                Span<float> y = spectrum.Slice(channelStride + bandStart, bandSamples);
                QuantBand<TK>(ref enc, x, default, bandSamples, b / 2, blocks, tfResolution[i], lm, ref remainingBits, 0, bandEnergy);
                QuantBand<TK>(ref enc, y, default, bandSamples, b / 2, blocks, tfResolution[i], lm, ref remainingBits, 0, bandEnergy);
            }
            else if (channels == 2)
            {
                Span<float> y = spectrum.Slice(channelStride + bandStart, bandSamples);
                QuantBand<TK>(ref enc, x, y, bandSamples, b, blocks, tfResolution[i], lm, ref remainingBits, 0, bandEnergy);
            }
            else
            {
                QuantBand<TK>(ref enc, x, default, bandSamples, b, blocks, tfResolution[i], lm, ref remainingBits, 0, bandEnergy);
            }

            balance += pulses[i] + tell;
        }
    }

    private void QuantBand<TK>(
        ref RangeEncoder enc,
        Span<float> x,
        Span<float> y,
        int n,
        int b,
        int blocks,
        int tfChange,
        int lm,
        ref int remainingBits,
        int level,
        ReadOnlySpan<float> bandEnergy)
        where TK : struct, IEncoderKernels
    {
        bool stereo = !y.IsEmpty;
        bool longBlocks = blocks == 1;
        int nB = n / blocks;
        int b0 = blocks;
        int timeDivide = 0;
        int recombine = 0;

        if (n == 1)
        {
            for (int c = 0; c < (stereo ? 2 : 1); c++)
            {
                Span<float> channel = c == 0 ? x : y;
                if (remainingBits >= 1 << BitRes)
                {
                    int sign = channel[0] < 0f ? 1 : 0;
                    enc.EncodeBits((uint)sign, 1);
                    remainingBits -= 1 << BitRes;
                }
            }

            return;
        }

        if (!stereo && level == 0)
        {
            if (tfChange > 0)
            {
                recombine = tfChange;
            }

            for (int k = 0; k < recombine; k++)
            {
                TK.Haar(x, n >> k, 1 << k);
            }

            blocks >>= recombine;
            nB <<= recombine;

            while ((nB & 1) == 0 && tfChange < 0)
            {
                TK.Haar(x, nB, blocks);
                blocks <<= 1;
                nB >>= 1;
                timeDivide++;
                tfChange++;
            }

            b0 = blocks;

            if (b0 > 1)
            {
                DeinterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
            }
        }

        EncProf.Mark(34, "  qb: tf haar/hadamard");
        bool split = stereo;
        if (!stereo)
        {
            int cacheRow = lm != -1 ? CeltTables.CacheIndex[((lm + 1) * BandCount) + _band] : -1;
            if (cacheRow >= 0 && n > 2)
            {
                int maxPulses = CeltTables.CacheBits[cacheRow];
                if (b > CeltTables.CacheBits[cacheRow + maxPulses] + 12)
                {
                    n >>= 1;
                    split = true;
                    lm--;
                    blocks = (blocks + 1) >> 1;
                }
            }
        }

        if (split)
        {
            Span<float> lower = x.Slice(0, n);
            Span<float> upper = stereo ? y.Slice(0, n) : x.Slice(n, n);

            int pulseCap = CeltTables.LogN[_band] + (lm << BitRes);
            int offset = (pulseCap >> 1) - (stereo && n == 2 ? QThetaOffsetTwoPhase : QThetaOffset);
            int qn = ComputeQn(n, b, offset, pulseCap, stereo);
            if (stereo && _band >= _intensity)
            {
                qn = 1;
            }

            int itheta = 0;
            int inv = 0;
            int tell = (int)enc.TellFrac();
            if (qn != 1)
            {
                itheta = StereoItheta(lower, upper, stereo);
                itheta = (itheta * qn + 8192) >> 14;

                if (stereo && n > 2)
                {
                    const int p0 = 3;
                    int x0 = qn / 2;
                    int ft = (p0 * (x0 + 1)) + x0;
                    int fl = itheta <= x0 ? p0 * itheta : (itheta - 1 - x0) + ((x0 + 1) * p0);
                    int fh = itheta <= x0 ? p0 * (itheta + 1) : (itheta - x0) + ((x0 + 1) * p0);
                    enc.Encode((uint)fl, (uint)fh, (uint)ft);
                }
                else if (b0 > 1 || stereo)
                {
                    enc.EncodeUInt((uint)itheta, (uint)(qn + 1));
                }
                else
                {
                    int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                    int fs = itheta <= (qn >> 1) ? itheta + 1 : qn + 1 - itheta;
                    int fl = itheta <= (qn >> 1)
                        ? itheta * (itheta + 1) >> 1
                        : ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    enc.Encode((uint)fl, (uint)(fl + fs), (uint)ft);
                }

                itheta = itheta * 16384 / qn;
                if (stereo)
                {
                    if (itheta == 0)
                    {
                        IntensityStereo(lower, upper, bandEnergy);
                    }
                    else
                    {
                        StereoSplit(lower, upper);
                    }
                }
            }
            else if (stereo)
            {
                // Had the angle not been zero, the side would have been phase-inverted.
                itheta = StereoItheta(lower, upper, true);
                inv = itheta > 8192 ? 1 : 0;
                if (inv != 0)
                {
                    for (int j = 0; j < n; j++)
                    {
                        upper[j] = -upper[j];
                    }
                }

                IntensityStereo(lower, upper, bandEnergy);
                if (b > 2 << BitRes && remainingBits > 2 << BitRes)
                {
                    enc.EncodeBitLogp(inv, 2);
                }
                else
                {
                    inv = 0;
                }

                itheta = 0;
            }

            EncProf.Mark(35, "  qb: theta (itheta, intensity/split)");
            int qalloc = (int)enc.TellFrac() - tell;
            b -= qalloc;

            int imid;
            int iside;
            int delta;
            if (itheta == 0)
            {
                imid = 32767;
                iside = 0;
                delta = -16384;
            }
            else if (itheta == 16384)
            {
                imid = 0;
                iside = 32767;
                delta = 16384;
            }
            else
            {
                imid = BitexactCos(itheta);
                iside = BitexactCos(16384 - itheta);
                delta = FracMul16((n - 1) << 7, BitexactLog2Tan(iside, imid));
            }

            if (n == 2 && stereo)
            {
                int mbits = b;
                int sbits = itheta != 0 && itheta != 16384 ? 1 << BitRes : 0;
                mbits -= sbits;
                bool useY = itheta > 8192;
                remainingBits -= qalloc + sbits;

                Span<float> x2 = useY ? upper : lower;
                Span<float> y2 = useY ? lower : upper;
                int sign = 0;
                if (sbits != 0)
                {
                    sign = (x2[0] * y2[1]) - (x2[1] * y2[0]) < 0f ? 1 : 0;
                    enc.EncodeBits((uint)sign, 1);
                }

                sign = 1 - (2 * sign);
                QuantBand<TK>(ref enc, x2, default, n, mbits, blocks, tfChange, lm, ref remainingBits, level, bandEnergy);
                y2[0] = -sign * x2[1];
                y2[1] = sign * x2[0];
            }
            else
            {
                if (b0 > 1 && !stereo && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192)
                    {
                        delta -= delta >> (4 - lm);
                    }
                    else
                    {
                        delta = Math.Min(0, delta + (n << BitRes >> (5 - lm)));
                    }
                }

                int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                int sbits = b - mbits;
                remainingBits -= qalloc;

                int nextLevel = stereo ? 0 : level + 1;
                int rebalance = remainingBits;
                if (mbits >= sbits)
                {
                    QuantBand<TK>(ref enc, lower, default, n, mbits, blocks, tfChange, lm, ref remainingBits, nextLevel, bandEnergy);
                    rebalance = mbits - (rebalance - remainingBits);
                    if (rebalance > 3 << BitRes && itheta != 0)
                    {
                        sbits += rebalance - (3 << BitRes);
                    }

                    QuantBand<TK>(ref enc, upper, default, n, sbits, blocks, tfChange, lm, ref remainingBits, nextLevel, bandEnergy);
                }
                else
                {
                    QuantBand<TK>(ref enc, upper, default, n, sbits, blocks, tfChange, lm, ref remainingBits, nextLevel, bandEnergy);
                    rebalance = sbits - (rebalance - remainingBits);
                    if (rebalance > 3 << BitRes && itheta != 16384)
                    {
                        mbits += rebalance - (3 << BitRes);
                    }

                    QuantBand<TK>(ref enc, lower, default, n, mbits, blocks, tfChange, lm, ref remainingBits, nextLevel, bandEnergy);
                }
            }

            _ = inv;
            return;
        }

        int pulses = Bits2Pulses(lm, b);
        int currentBits = Pulses2Bits(lm, pulses);
        remainingBits -= currentBits;
        while (remainingBits < 0 && pulses > 0)
        {
            remainingBits += currentBits;
            pulses--;
            currentBits = Pulses2Bits(lm, pulses);
            remainingBits -= currentBits;
        }

        if (pulses != 0)
        {
            int k = CeltTables.GetPulses(pulses);
            CeltEncoderVq.AlgQuant<TK>(
                x, _iy.AsSpan(0, n), _signs.AsSpan(0, n), _y.AsSpan(0, n), n, k, blocks, _spread, ref enc);
        }

        _ = timeDivide;
    }

    /// <summary>Chooses the split-angle quantizer (RFC 6716 Appendix A, celt/bands.c: compute_qn).</summary>
    private static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = (2 * n) - 1;
        if (stereo && n == 2)
        {
            n2--;
        }

        int qb = Math.Min(b - pulseCap - (4 << BitRes), (b + (n2 * offset)) / n2);
        qb = Math.Min(8 << BitRes, qb);
        if (qb < 1 << BitRes >> 1)
        {
            return 1;
        }

        int qn = CeltTables.Exp2Table8[qb & 0x7] >> (14 - (qb >> BitRes));
        return (qn + 1) >> 1 << 1;
    }

    /// <summary>
    /// Returns the angle between the mid and side signals (RFC 6716 Appendix A, celt/vq.c:
    /// <c>stereo_itheta</c>). When <paramref name="stereo"/> is false the two inputs are the
    /// lower and upper halves of a mono band.
    /// </summary>
    private static int StereoItheta(ReadOnlySpan<float> x, ReadOnlySpan<float> y, bool stereo)
    {
        float emid = 1e-15f;
        float eside = 1e-15f;
        if (stereo)
        {
            for (int i = 0; i < x.Length; i++)
            {
                float m = x[i] + y[i];
                float s = x[i] - y[i];
                emid += m * m;
                eside += s * s;
            }
        }
        else
        {
            for (int i = 0; i < x.Length; i++)
            {
                float m = x[i];
                float s = y[i];
                emid += m * m;
                eside += s * s;
            }
        }

        float mid = MathF.Sqrt(emid);
        float side = MathF.Sqrt(eside);
        return (int)Math.Floor(0.5 + (16384.0 * 0.63662 * Math.Atan2(side, mid)));
    }

    /// <summary>
    /// Replaces the mid channel with the intensity-stereo mixture (RFC 6716 Appendix A,
    /// celt/bands.c: <c>intensity_stereo</c>).
    /// </summary>
    private void IntensityStereo(Span<float> x, Span<float> y, ReadOnlySpan<float> bandEnergy)
    {
        float left = bandEnergy[_band];
        float right = bandEnergy[_band + BandCount];
        float norm = 1e-15f + MathF.Sqrt(1e-15f + (left * left) + (right * right));
        float a1 = left / norm;
        float a2 = right / norm;
        for (int j = 0; j < x.Length; j++)
        {
            x[j] = (a1 * x[j]) + (a2 * y[j]);
        }
    }

    /// <summary>Applies the mid/side rotation (RFC 6716 Appendix A, celt/bands.c: <c>stereo_split</c>).</summary>
    private static void StereoSplit(Span<float> x, Span<float> y)
    {
        for (int j = 0; j < x.Length; j++)
        {
            float l = 0.70710678f * x[j];
            float r = 0.70710678f * y[j];
            x[j] = l + r;
            y[j] = r - l;
        }
    }

    private int Bits2Pulses(int lm, int bits)
    {
        int row = CeltTables.CacheIndex[((lm + 1) * BandCount) + _band];
        ReadOnlySpan<byte> cache = CeltTables.CacheBits.Slice(row);
        int lo = 0;
        int hi = cache[0];
        bits--;
        for (int i = 0; i < CeltTables.LogMaxPseudo; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[mid] >= bits)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return bits - (lo == 0 ? -1 : cache[lo]) <= cache[hi] - bits ? lo : hi;
    }

    private int Pulses2Bits(int lm, int pulses)
    {
        if (pulses == 0)
        {
            return 0;
        }

        int row = CeltTables.CacheIndex[((lm + 1) * BandCount) + _band];
        return CeltTables.CacheBits[row + pulses] + 1;
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = _hadamardScratch.AsSpan(0, n);
        ref float source = ref MemoryMarshal.GetReference(x);
        ref float destination = ref MemoryMarshal.GetReference(tmp);
        ReadOnlySpan<int> ordery = hadamard ? CeltTables.OrderyTable.Slice(stride - 2) : default;
        for (int i = 0; i < stride; i++)
        {
            int target = (hadamard ? ordery[i] : i) * n0;
            for (int j = 0; j < n0; j++)
            {
                Unsafe.Add(ref destination, target + j) = Unsafe.Add(ref source, (j * stride) + i);
            }
        }

        tmp.CopyTo(x);
    }

    private static int BitexactCos(int x)
    {
        int tmp = (4096 + (x * x)) >> 13;
        short x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return 1 + x2;
    }

    private static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = CeltTables.Ilog(icos);
        int ls = CeltTables.Ilog(isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return ((ls - lc) * (1 << 11))
            + FracMul16(isin, FracMul16(isin, -2597) + 7932)
            - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    private static int FracMul16(int a, int b) => (16384 + ((short)a * (short)b)) >> 15;
}