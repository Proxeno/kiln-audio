// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>Bit allocation for the CELT encoder (RFC 6716 §4.3.3; RFC 6716 Appendix A, celt/rate.c).</summary>
internal static class CeltEncoderAllocation
{
    private const int BandCount = CeltTables.BandCount;
    private const int AllocSteps = CeltTables.AllocSteps;
    private const int BitRes = CeltTables.BitRes;

    /// <summary>
    /// Mirrors the decoder's allocation, choosing the skip decisions and writing them
    /// (RFC 6716 Appendix A, celt/rate.c: <c>compute_allocation</c> with <c>encode=1</c>).
    /// </summary>
    internal static int ComputeAllocation(
        ref RangeEncoder enc,
        Span<int> bits1,
        Span<int> bits2,
        Span<int> threshold,
        Span<int> trimOffset,
        int start,
        int end,
        int lm,
        int c,
        int total,
        int allocTrim,
        ref int intensity,
        ref int dualStereo,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<int> cap,
        Span<int> pulses,
        Span<int> fineQuant,
        Span<int> finePriority,
        out int balance,
        int previousCodedBands)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<byte> allocVectors = CeltTables.BandAllocation;

        total = Math.Max(total, 0);
        int skipStart = start;
        int skipReserve = total >= 1 << BitRes ? 1 << BitRes : 0;
        total -= skipReserve;

        int intensityReserve = 0;
        int dualStereoReserve = 0;
        if (c == 2)
        {
            intensityReserve = CeltTables.Log2FracTable[end - start];
            if (intensityReserve > total)
            {
                intensityReserve = 0;
            }
            else
            {
                total -= intensityReserve;
                dualStereoReserve = total >= 1 << BitRes ? 1 << BitRes : 0;
                total -= dualStereoReserve;
            }
        }

        for (int j = start; j < end; j++)
        {
            int width = eBands[j + 1] - eBands[j];
            threshold[j] = Math.Max(c << BitRes, (3 * width << lm << BitRes) >> 4);
            trimOffset[j] = c * width * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + BitRes)) >> 6;
            if (width << lm == 1)
            {
                trimOffset[j] -= c << BitRes;
            }
        }

        int lo = 1;
        int hi = CeltTables.AllocVectorCount - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int bitsj = c * (eBands[j + 1] - eBands[j]) * allocVectors[(mid * BandCount) + j] << lm >> 2;
                if (bitsj > 0)
                {
                    bitsj = Math.Max(0, bitsj + trimOffset[j]);
                }

                bitsj += offsets[j];
                if (bitsj >= threshold[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= c << BitRes)
                {
                    psum += c << BitRes;
                }
            }

            if (psum > total)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        while (lo <= hi);

        hi = lo--;
        for (int j = start; j < end; j++)
        {
            int width = eBands[j + 1] - eBands[j];
            int bits1j = c * width * allocVectors[(lo * BandCount) + j] << lm >> 2;
            int bits2j = hi >= CeltTables.AllocVectorCount
                ? cap[j]
                : c * width * allocVectors[(hi * BandCount) + j] << lm >> 2;
            if (bits1j > 0)
            {
                bits1j = Math.Max(0, bits1j + trimOffset[j]);
            }

            if (bits2j > 0)
            {
                bits2j = Math.Max(0, bits2j + trimOffset[j]);
            }

            if (lo > 0)
            {
                bits1j += offsets[j];
            }

            bits2j += offsets[j];
            if (offsets[j] > 0)
            {
                skipStart = j;
            }

            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }

        return Interpolate(
            ref enc,
            bits1,
            bits2,
            threshold,
            cap,
            start,
            end,
            skipStart,
            total,
            skipReserve,
            ref intensity,
            intensityReserve,
            ref dualStereo,
            dualStereoReserve,
            pulses,
            fineQuant,
            finePriority,
            c,
            lm,
            out balance,
            previousCodedBands);
    }

    private static int Interpolate(
        ref RangeEncoder enc,
        ReadOnlySpan<int> bits1,
        ReadOnlySpan<int> bits2,
        ReadOnlySpan<int> threshold,
        ReadOnlySpan<int> cap,
        int start,
        int end,
        int skipStart,
        int total,
        int skipReserve,
        ref int intensity,
        int intensityReserve,
        ref int dualStereo,
        int dualStereoReserve,
        Span<int> pulses,
        Span<int> fineQuant,
        Span<int> finePriority,
        int c,
        int lm,
        out int balance,
        int previousCodedBands)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int allocFloor = c << BitRes;
        int stereo = c > 1 ? 1 : 0;
        int logM = lm << BitRes;
        int codedBands;

        int lo = 0;
        int hi = 1 << AllocSteps;
        for (int step = 0; step < AllocSteps; step++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + ((mid * bits2[j]) >> AllocSteps);
                if (tmp >= threshold[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }

            if (psum > total)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        int totalUsed = 0;
        bool finalDone = false;
        for (int j = end; j-- > start;)
        {
            int tmp = bits1[j] + ((lo * bits2[j]) >> AllocSteps);
            if (tmp < threshold[j] && !finalDone)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else
            {
                finalDone = true;
            }

            tmp = Math.Min(tmp, cap[j]);
            pulses[j] = tmp;
            totalUsed += tmp;
        }

        for (codedBands = end; ; codedBands--)
        {
            int j = codedBands - 1;
            if (j <= skipStart)
            {
                total += skipReserve;
                break;
            }

            int left = total - totalUsed;
            int bandStart = eBands[start];
            int perCoefficient = left / (eBands[codedBands] - bandStart);
            left -= (eBands[codedBands] - bandStart) * perCoefficient;
            int remainder = Math.Max(left - (eBands[j] - bandStart), 0);
            int bandWidth = eBands[codedBands] - eBands[j];
            int bandBits = pulses[j] + (perCoefficient * bandWidth) + remainder;
            if (bandBits >= Math.Max(threshold[j], allocFloor + (1 << BitRes)))
            {
                // This is the one bitstream decision that is not mandatory: any skipped band must
                // be signalled. A hysteresis threshold keeps bands from flip-flopping.
                int hysteretic = ((j < previousCodedBands ? 7 : 9) * bandWidth << lm << BitRes) >> 4;
                if (bandBits > hysteretic)
                {
                    enc.EncodeBitLogp(1, 1);
                    break;
                }

                enc.EncodeBitLogp(0, 1);
                totalUsed += 1 << BitRes;
                bandBits -= 1 << BitRes;
            }

            totalUsed -= pulses[j] + intensityReserve;
            if (intensityReserve > 0)
            {
                intensityReserve = CeltTables.Log2FracTable[j - start];
            }

            totalUsed += intensityReserve;
            if (bandBits >= allocFloor)
            {
                totalUsed += allocFloor;
                pulses[j] = allocFloor;
            }
            else
            {
                pulses[j] = 0;
            }
        }

        if (intensityReserve > 0)
        {
            intensity = Math.Min(intensity, codedBands);
            enc.EncodeUInt((uint)(intensity - start), (uint)(codedBands + 1 - start));
        }
        else
        {
            intensity = 0;
        }

        if (intensity <= start)
        {
            total += dualStereoReserve;
            dualStereoReserve = 0;
        }

        if (dualStereoReserve > 0)
        {
            enc.EncodeBitLogp(dualStereo, 1);
        }
        else
        {
            dualStereo = 0;
        }

        int left2 = total - totalUsed;
        int perCoefficient2 = left2 / (eBands[codedBands] - eBands[start]);
        left2 -= (eBands[codedBands] - eBands[start]) * perCoefficient2;
        for (int j = start; j < codedBands; j++)
        {
            pulses[j] += perCoefficient2 * (eBands[j + 1] - eBands[j]);
        }

        for (int j = start; j < codedBands; j++)
        {
            int extra = Math.Min(left2, eBands[j + 1] - eBands[j]);
            pulses[j] += extra;
            left2 -= extra;
        }

        int excessBalance = 0;
        int jj = start;
        for (; jj < codedBands; jj++)
        {
            int width = eBands[jj + 1] - eBands[jj];
            int samples = width << lm;
            int allocated = pulses[jj] + excessBalance;
            int excess;
            if (samples > 1)
            {
                excess = Math.Max(allocated - cap[jj], 0);
                pulses[jj] = allocated - excess;
                int den = c * samples
                    + ((c == 2 && samples > 2 && dualStereo == 0 && jj < intensity) ? 1 : 0);
                int ncLogN = den * (CeltTables.LogN[jj] + logM);
                int offset = (ncLogN >> 1) - (den * 21);
                if (samples == 2)
                {
                    offset += (den << BitRes) >> 2;
                }

                if (pulses[jj] + offset < (den * 2) << BitRes)
                {
                    offset += ncLogN >> 2;
                }
                else if (pulses[jj] + offset < (den * 3) << BitRes)
                {
                    offset += ncLogN >> 3;
                }

                int ebits = Math.Max(
                    0,
                    (pulses[jj] + offset + (den << (BitRes - 1))) / (den << BitRes));
                if (c * ebits > pulses[jj] >> BitRes)
                {
                    ebits = pulses[jj] >> stereo >> BitRes;
                }

                ebits = Math.Min(ebits, CeltTables.MaxFineBits);
                finePriority[jj] = ebits * (den << BitRes) >= pulses[jj] + offset ? 1 : 0;
                pulses[jj] -= c * ebits << BitRes;
                fineQuant[jj] = ebits;
            }
            else
            {
                excess = Math.Max(0, allocated - (c << BitRes));
                pulses[jj] = allocated - excess;
                fineQuant[jj] = 0;
                finePriority[jj] = 1;
            }

            if (excess > 0)
            {
                int extraFine = Math.Min(excess >> (stereo + BitRes), CeltTables.MaxFineBits - fineQuant[jj]);
                fineQuant[jj] += extraFine;
                int extraBits = extraFine * c << BitRes;
                finePriority[jj] = extraBits >= excess - excessBalance ? 1 : 0;
                excess -= extraBits;
            }

            excessBalance = excess;
        }

        balance = excessBalance;

        for (; jj < end; jj++)
        {
            fineQuant[jj] = pulses[jj] >> stereo >> BitRes;
            pulses[jj] = 0;
            finePriority[jj] = fineQuant[jj] < 1 ? 1 : 0;
        }

        return codedBands;
    }
}
