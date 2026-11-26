// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

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
/// reference while doing less work.
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

    private readonly int[] _iy = new int[CeltTables.MaxFrameSamples];
    private readonly float[] _signs = new float[CeltTables.MaxFrameSamples];
    private readonly float[] _y = new float[CeltTables.MaxFrameSamples];
    private readonly uint[] _u = new uint[256];
    private readonly float[] _hadamardScratch = new float[512];

    private readonly int[] _pulses = new int[BandCount];
    private readonly int[] _tfResolution = new int[BandCount];
    private int _band;
    private int _spread;

    /// <summary>
    /// Quantizes every coded band and writes the PVQ symbols (RFC 6716 Appendix A,
    /// celt/bands.c: <c>quant_all_bands</c>).
    /// </summary>
    internal void QuantAllBands(
        ref RangeEncoder enc,
        ReadOnlySpan<short> eBands,
        int start,
        int end,
        Span<float> spectrum,
        int lm,
        bool shortBlocks,
        int spread,
        int codedBands,
        int totalBits,
        int balance,
        ReadOnlySpan<int> pulses,
        ReadOnlySpan<int> tfResolution)
    {
        int m = 1 << lm;
        int blocks = shortBlocks ? m : 1;
        _spread = spread;
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

            QuantBand(ref enc, x, bandSamples, b, blocks, tfResolution[i], lm, ref remainingBits, 0);
            balance += pulses[i] + tell;
        }
    }

    private void QuantBand(
        ref RangeEncoder enc,
        Span<float> x,
        int n,
        int b,
        int blocks,
        int tfChange,
        int lm,
        ref int remainingBits,
        int level)
    {
        int n0 = n;
        int nB = n / blocks;
        int b0 = blocks;
        int timeDivide = 0;
        int recombine = 0;
        bool longBlocks = blocks == 1;

        if (n == 1)
        {
            if (remainingBits >= 1 << BitRes)
            {
                int sign = x[0] < 0f ? 1 : 0;
                enc.EncodeBits((uint)sign, 1);
                remainingBits -= 1 << BitRes;
            }

            return;
        }

        if (level == 0)
        {
            if (tfChange > 0)
            {
                recombine = tfChange;
            }

            for (int k = 0; k < recombine; k++)
            {
                Haar1(x, n >> k, 1 << k);
            }

            blocks >>= recombine;
            nB <<= recombine;

            while ((nB & 1) == 0 && tfChange < 0)
            {
                Haar1(x, nB, blocks);
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

        int cacheRow = lm != -1 ? CeltTables.CacheIndex[((lm + 1) * BandCount) + _band] : -1;
        bool split = false;
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

        if (!split)
        {
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
                CeltEncoderVq.AlgQuant(
                    x, _iy.AsSpan(0, n), _signs.AsSpan(0, n), _y.AsSpan(0, n), _u, n, k, blocks, _spread, ref enc);
            }

            return;
        }

        // Spectral split: encode the angle between the two halves.
        int pulseCap = CeltTables.LogN[_band] + (lm << BitRes);
        int offset = (pulseCap >> 1) - QThetaOffset;
        int qn = ComputeQn(n, b, offset, pulseCap, stereo: false);

        int itheta = 0;
        int tell = (int)enc.TellFrac();
        if (qn != 1)
        {
            Span<float> lower = x.Slice(0, n);
            Span<float> upper = x.Slice(n, n);
            itheta = StereoItheta(lower, upper);
            itheta = (itheta * qn + 8192) >> 14;
            if (b0 > 1)
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
        }

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

        float mid = imid * (1f / 32768f);
        float side = iside * (1f / 32768f);

        if (b0 > 1 && (itheta & 0x3fff) != 0)
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

        Span<float> midSpan = x.Slice(0, n);
        Span<float> sideSpan = x.Slice(n, n);
        int rebalance = remainingBits;
        if (mbits >= sbits)
        {
            QuantBand(ref enc, midSpan, n, mbits, blocks, tfChange, lm, ref remainingBits, level + 1);
            rebalance = mbits - (rebalance - remainingBits);
            if (rebalance > 3 << BitRes && itheta != 0)
            {
                sbits += rebalance - (3 << BitRes);
            }

            QuantBand(ref enc, sideSpan, n, sbits, blocks, tfChange, lm, ref remainingBits, level + 1);
        }
        else
        {
            QuantBand(ref enc, sideSpan, n, sbits, blocks, tfChange, lm, ref remainingBits, level + 1);
            rebalance = sbits - (rebalance - remainingBits);
            if (rebalance > 3 << BitRes && itheta != 16384)
            {
                mbits += rebalance - (3 << BitRes);
            }

            QuantBand(ref enc, midSpan, n, mbits, blocks, tfChange, lm, ref remainingBits, level + 1);
        }

        _ = n0;
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

    /// <summary>Returns the angle between two halves (RFC 6716 Appendix A, celt/vq.c: stereo_itheta, mono).</summary>
    private static int StereoItheta(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        float emid = 1e-15f;
        float eside = 1e-15f;
        for (int i = 0; i < x.Length; i++)
        {
            float m = x[i];
            float s = y[i];
            emid += m * m;
            eside += s * s;
        }

        double mid = Math.Sqrt(emid);
        double side = Math.Sqrt(eside);
        return (int)Math.Floor((0.5 + (16384.0 * 0.63662 * Math.Atan2(side, mid))));
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

    /// <summary>The Haar transform used by the time/frequency reshaping (RFC 6716 Appendix A, celt/bands.c: haar1).</summary>
    private static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float first = 0.70710678f * x[(stride * 2 * j) + i];
                float second = 0.70710678f * x[(stride * ((2 * j) + 1)) + i];
                x[(stride * 2 * j) + i] = first + second;
                x[(stride * ((2 * j) + 1)) + i] = first - second;
            }
        }
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = _hadamardScratch.AsSpan(0, n);
        if (hadamard)
        {
            ReadOnlySpan<int> ordery = CeltTables.OrderyTable.Slice(stride - 2);
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(ordery[i] * n0) + j] = x[(j * stride) + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(i * n0) + j] = x[(j * stride) + i];
                }
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
