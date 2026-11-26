// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// Reconstructs normalized LSFs and converts them to LPC coefficients
/// (RFC 6716 §4.2.7.5.2 through §4.2.7.5.8).
/// </summary>
/// <remarks>
/// The reference decoder guarantees a stable reconstruction through fixed-point arithmetic, so
/// these routines are straight ports of the Appendix A functions
/// <c>silk_NLSF_decode</c>, <c>silk_NLSF_stabilize</c>, <c>silk_NLSF2A</c>,
/// <c>silk_bwexpander_32</c> and <c>silk_LPC_inverse_pred_gain</c>. All scratch storage is
/// <c>stackalloc</c>, so synthesizing a frame allocates nothing.
/// </remarks>
internal static class SilkNlsf
{
    /// <summary>Maximum LPC order, and the longest NLSF vector (WB).</summary>
    internal const int MaxOrder = 16;

    /// <summary>Size of the piecewise-linear cosine table, minus one (RFC 6716 §4.2.7.5.6).</summary>
    internal const int CosTableSize = 128;

    /// <summary>Q-domain of the LSF weights in the Laroia weighting (RFC 6716 §4.2.7.5.5).</summary>
    private const int NlsfWQ = 2;

    /// <summary>The Q10 constant 0.1, the stage-2 residual level adjustment (RFC 6716 §4.2.7.5.2).</summary>
    private const int NlsfQuantLevelAdjustQ10 = 102;

    /// <summary>Maximum rounds of range limiting and bandwidth expansion (RFC 6716 §4.2.7.5.7-8).</summary>
    private const int MaxStabilizeIterations = 16;

    /// <summary>The Q24 constant 0.99975 (RFC 6716 §4.2.7.5.8).</summary>
    private const int ALimitQ24 = 16773022;

    /// <summary>The Q30 constant 1/10000 (RFC 6716 §4.2.7.5.8).</summary>
    private const int MinInverseGainQ30 = 107374;

    /// <summary>The Q16 constant 0.999 (RFC 6716 §4.2.7.5.7).</summary>
    private const int ChirpQ16 = 65470;

    private static readonly byte[] Ordering16 =
        [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];

    private static readonly byte[] Ordering10 = [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];

    /// <summary>
    /// Reconstructs the normalized LSF vector of one frame from the stage-1 and stage-2 indices
    /// (RFC 6716 §4.2.7.5.2 through §4.2.7.5.4).
    /// </summary>
    /// <param name="bandwidthIndex">Bandwidth index: 0 NB, 1 MB, 2 WB.</param>
    /// <param name="stage1Index">The stage-1 index, 0 to 31.</param>
    /// <param name="stage2Indices">The stage-2 residual indices, -10 to 10, one per coefficient.</param>
    /// <param name="nlsfQ15">Receives the stabilized normalized LSFs, Q15.</param>
    internal static void Decode(
        int bandwidthIndex, int stage1Index, ReadOnlySpan<int> stage2Indices, Span<short> nlsfQ15)
    {
        int order = SilkSynthesisTables.NlsfOrder[bandwidthIndex];
        bool wideband = bandwidthIndex == 2;
        ReadOnlySpan<byte> codebook = wideband
            ? SilkSynthesisTables.NlsfCb1WbQ8
            : SilkSynthesisTables.NlsfCb1NbMbQ8;
        ReadOnlySpan<byte> predTable = wideband
            ? SilkSynthesisTables.NlsfPredWbQ8
            : SilkSynthesisTables.NlsfPredNbMbQ8;
        string[] predSelect = wideband
            ? SilkSynthesisTables.NlsfPredSelectWb
            : SilkSynthesisTables.NlsfPredSelectNbMb;
        int quantStepSizeQ16 = SilkSynthesisTables.NlsfQuantStepSizeQ16[bandwidthIndex];
        char firstLetter = wideband ? 'C' : 'A';
        string selection = predSelect[stage1Index];

        // Stage-1 codebook vector.
        int codebookOffset = stage1Index * order;
        for (int i = 0; i < order; i++)
        {
            nlsfQ15[i] = (short)SilkFixedPoint.Lshift(codebook[codebookOffset + i], 7);
        }

        // Undo the backwards prediction of the stage-2 residual (RFC 6716 §4.2.7.5.2).
        // pred_Q8[order - 1] is never used because out_Q10 starts at zero.
        Span<short> residualQ10 = stackalloc short[MaxOrder];
        int outQ10 = 0;
        for (int i = order - 1; i >= 0; i--)
        {
            int predQ8 = i < order - 1 && selection[i] != firstLetter
                ? predTable[i + (order - 1)]
                : i < order - 1 ? predTable[i] : 0;
            int predQ10 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(outQ10, predQ8), 8);

            outQ10 = SilkFixedPoint.Lshift(stage2Indices[i], 10);
            if (outQ10 > 0)
            {
                outQ10 = SilkFixedPoint.Sub(outQ10, NlsfQuantLevelAdjustQ10);
            }
            else if (outQ10 < 0)
            {
                outQ10 = SilkFixedPoint.Add(outQ10, NlsfQuantLevelAdjustQ10);
            }

            outQ10 = SilkFixedPoint.Smlawb(predQ10, outQ10, quantStepSizeQ16);
            residualQ10[i] = (short)outQ10;
        }

        // Laroia low-complexity weights, derived from the stage-1 vector (RFC 6716 §4.2.7.5.3).
        Span<short> weightsQw = stackalloc short[MaxOrder];
        ComputeWeights(weightsQw, nlsfQ15, order);

        // Apply the inverse-square-rooted weights and add the residual.
        for (int i = 0; i < order; i++)
        {
            int weightQ9 = SilkFixedPoint.SqrtApprox(
                SilkFixedPoint.Lshift(weightsQw[i], 18 - NlsfWQ));
            int value = SilkFixedPoint.Add(
                nlsfQ15[i],
                SilkFixedPoint.Lshift(residualQ10[i], 14) / weightQ9);
            nlsfQ15[i] = (short)SilkFixedPoint.Limit(value, 0, 32767);
        }

        Stabilize(nlsfQ15, wideband
            ? SilkSynthesisTables.NlsfDeltaMinWbQ15
            : SilkSynthesisTables.NlsfDeltaMinNbMbQ15, order);
    }

    /// <summary>
    /// Enforces the minimum spacing between consecutive normalized LSFs (RFC 6716 §4.2.7.5.4,
    /// with the RFC 8251 §7 saturating addition in the fallback).
    /// </summary>
    internal static void Stabilize(Span<short> nlsfQ15, ReadOnlySpan<short> deltaMinQ15, int order)
    {
        const int MaxLoops = 20;
        int loops;
        for (loops = 0; loops < MaxLoops; loops++)
        {
            int minDiff = nlsfQ15[0] - deltaMinQ15[0];
            int index = 0;
            for (int i = 1; i <= order - 1; i++)
            {
                int diff = nlsfQ15[i] - (nlsfQ15[i - 1] + deltaMinQ15[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    index = i;
                }
            }

            int lastDiff = (1 << 15) - (nlsfQ15[order - 1] + deltaMinQ15[order]);
            if (lastDiff < minDiff)
            {
                minDiff = lastDiff;
                index = order;
            }

            if (minDiff >= 0)
            {
                return;
            }

            if (index == 0)
            {
                nlsfQ15[0] = deltaMinQ15[0];
            }
            else if (index == order)
            {
                nlsfQ15[order - 1] = (short)((1 << 15) - deltaMinQ15[order]);
            }
            else
            {
                int minCenter = 0;
                for (int k = 0; k < index; k++)
                {
                    minCenter += deltaMinQ15[k];
                }

                minCenter += SilkFixedPoint.Rshift(deltaMinQ15[index], 1);

                int maxCenter = 1 << 15;
                for (int k = order; k > index; k--)
                {
                    maxCenter -= deltaMinQ15[k];
                }

                maxCenter -= SilkFixedPoint.Rshift(deltaMinQ15[index], 1);

                int center = SilkFixedPoint.Limit(
                    SilkFixedPoint.RshiftRound(
                        (int)nlsfQ15[index - 1] + nlsfQ15[index], 1),
                    minCenter,
                    maxCenter);
                nlsfQ15[index - 1] = (short)(center - SilkFixedPoint.Rshift(deltaMinQ15[index], 1));
                nlsfQ15[index] = (short)(nlsfQ15[index - 1] + deltaMinQ15[index]);
            }
        }

        // Fallback: sort, then push apart in both directions.
        SortIncreasing(nlsfQ15, order);
        nlsfQ15[0] = (short)SilkFixedPoint.MaxInt(nlsfQ15[0], deltaMinQ15[0]);
        for (int i = 1; i < order; i++)
        {
            nlsfQ15[i] = (short)SilkFixedPoint.MaxInt(
                nlsfQ15[i], SilkFixedPoint.AddSat16(nlsfQ15[i - 1], deltaMinQ15[i]));
        }

        nlsfQ15[order - 1] = (short)SilkFixedPoint.MinInt(
            nlsfQ15[order - 1], (1 << 15) - deltaMinQ15[order]);
        for (int i = order - 2; i >= 0; i--)
        {
            nlsfQ15[i] = (short)SilkFixedPoint.MinInt(
                nlsfQ15[i], nlsfQ15[i + 1] - deltaMinQ15[i + 1]);
        }
    }

    /// <summary>
    /// Converts normalized LSFs to Q12 LPC coefficients, limiting their range and prediction gain
    /// (RFC 6716 §4.2.7.5.6 through §4.2.7.5.8).
    /// </summary>
    /// <param name="aQ12">Receives the LPC coefficients, Q12; must hold <paramref name="order"/> entries.</param>
    /// <param name="nlsfQ15">The normalized LSFs, Q15.</param>
    /// <param name="order">The filter order: 10 or 16.</param>
    internal static void ConvertToLpc(Span<short> aQ12, ReadOnlySpan<short> nlsfQ15, int order)
    {
        const int QA = 16;
        ReadOnlySpan<byte> ordering = order == 16 ? Ordering16 : Ordering10;
        Span<int> cosLsfQa = stackalloc int[MaxOrder];
        Span<int> p = stackalloc int[(MaxOrder / 2) + 1];
        Span<int> q = stackalloc int[(MaxOrder / 2) + 1];
        Span<int> a32Qa1 = stackalloc int[MaxOrder];

        for (int k = 0; k < order; k++)
        {
            int fInt = SilkFixedPoint.Rshift(nlsfQ15[k], 15 - 7);
            int fFrac = nlsfQ15[k] - SilkFixedPoint.Lshift(fInt, 15 - 7);
            int cosVal = SilkSynthesisTables.LsfCosQ12[fInt];
            int delta = SilkSynthesisTables.LsfCosQ12[fInt + 1] - cosVal;
            cosLsfQa[ordering[k]] = SilkFixedPoint.RshiftRound(
                SilkFixedPoint.Lshift(cosVal, 8) + SilkFixedPoint.Mul(delta, fFrac), 20 - QA);
        }

        int half = order >> 1;
        FindPoly(p, cosLsfQa, half);
        FindPoly(q, cosLsfQa[1..], half);

        for (int k = 0; k < half; k++)
        {
            int pTmp = p[k + 1] + p[k];
            int qTmp = q[k + 1] - q[k];
            a32Qa1[k] = -qTmp - pTmp;
            a32Qa1[order - k - 1] = qTmp - pTmp;
        }

        // Limit the range of the coefficients to fit in 16 bits (RFC 6716 §4.2.7.5.7).
        int round;
        for (round = 0; round < 10; round++)
        {
            int maxAbs = 0;
            int idx = 0;
            for (int k = 0; k < order; k++)
            {
                int absValue = SilkFixedPoint.Abs(a32Qa1[k]);
                if (absValue > maxAbs)
                {
                    maxAbs = absValue;
                    idx = k;
                }
            }

            maxAbs = SilkFixedPoint.RshiftRound(maxAbs, QA + 1 - 12);
            if (maxAbs <= SilkFixedPoint.Int16Max)
            {
                break;
            }

            maxAbs = SilkFixedPoint.MinInt(maxAbs, 163838);
            int scQ16 = ChirpQ16 - (
                SilkFixedPoint.Lshift(maxAbs - SilkFixedPoint.Int16Max, 14)
                / SilkFixedPoint.Rshift(SilkFixedPoint.Mul(maxAbs, idx + 1), 2));
            Bwexpander32(a32Qa1, order, scQ16);
        }

        if (round == 10)
        {
            for (int k = 0; k < order; k++)
            {
                aQ12[k] = (short)SilkFixedPoint.Sat16(
                    SilkFixedPoint.RshiftRound(a32Qa1[k], QA + 1 - 12));
                a32Qa1[k] = SilkFixedPoint.Lshift(aQ12[k], QA + 1 - 12);
            }
        }
        else
        {
            for (int k = 0; k < order; k++)
            {
                aQ12[k] = (short)SilkFixedPoint.RshiftRound(a32Qa1[k], QA + 1 - 12);
            }
        }

        // Limit the prediction gain (RFC 6716 §4.2.7.5.8).
        for (int i = 0; i < MaxStabilizeIterations; i++)
        {
            if (LpcInversePredGain(aQ12, order) < MinInverseGainQ30)
            {
                Bwexpander32(a32Qa1, order, 65536 - SilkFixedPoint.Lshift(2, i));
                for (int k = 0; k < order; k++)
                {
                    aQ12[k] = (short)SilkFixedPoint.RshiftRound(a32Qa1[k], QA + 1 - 12);
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Computes the Laroia weights from the stage-1 codebook vector (RFC 6716 §4.2.7.5.3).</summary>
    private static void ComputeWeights(Span<short> weights, ReadOnlySpan<short> nlsfQ15, int order)
    {
        int tmp1 = SilkFixedPoint.MaxInt(nlsfQ15[0], 1);
        tmp1 = (1 << (15 + NlsfWQ)) / tmp1;
        int tmp2 = SilkFixedPoint.MaxInt(nlsfQ15[1] - nlsfQ15[0], 1);
        tmp2 = (1 << (15 + NlsfWQ)) / tmp2;
        weights[0] = (short)SilkFixedPoint.MinInt(tmp1 + tmp2, SilkFixedPoint.Int16Max);

        for (int k = 1; k < order - 1; k += 2)
        {
            tmp1 = SilkFixedPoint.MaxInt(nlsfQ15[k + 1] - nlsfQ15[k], 1);
            tmp1 = (1 << (15 + NlsfWQ)) / tmp1;
            weights[k] = (short)SilkFixedPoint.MinInt(tmp1 + tmp2, SilkFixedPoint.Int16Max);

            tmp2 = SilkFixedPoint.MaxInt(nlsfQ15[k + 2] - nlsfQ15[k + 1], 1);
            tmp2 = (1 << (15 + NlsfWQ)) / tmp2;
            weights[k + 1] = (short)SilkFixedPoint.MinInt(tmp1 + tmp2, SilkFixedPoint.Int16Max);
        }

        tmp1 = SilkFixedPoint.MaxInt((1 << 15) - nlsfQ15[order - 1], 1);
        tmp1 = (1 << (15 + NlsfWQ)) / tmp1;
        weights[order - 1] = (short)SilkFixedPoint.MinInt(
            tmp1 + tmp2, SilkFixedPoint.Int16Max);
    }

    /// <summary>Computes the coefficients of one (anti-)symmetric polynomial (RFC 6716 §4.2.7.5.6).</summary>
    private static void FindPoly(Span<int> output, ReadOnlySpan<int> cosLsf, int half)
    {
        const int QA = 16;
        output[0] = 1 << QA;
        output[1] = -cosLsf[0];
        for (int k = 1; k < half; k++)
        {
            int ftmp = cosLsf[2 * k];
            output[k + 1] = SilkFixedPoint.Lshift(output[k - 1], 1)
                - (int)SilkFixedPoint.RshiftRound64(SilkFixedPoint.Smull(ftmp, output[k]), QA);
            for (int n = k; n > 1; n--)
            {
                output[n] = unchecked(output[n] + (
                    output[n - 2]
                    - (int)SilkFixedPoint.RshiftRound64(
                        SilkFixedPoint.Smull(ftmp, output[n - 1]), QA)));
            }

            output[1] = unchecked(output[1] - ftmp);
        }
    }

    /// <summary>Bandwidth-expands a Q17/Q1 AR filter (RFC 6716 §4.2.7.5.7, <c>silk_bwexpander_32</c>).</summary>
    private static void Bwexpander32(Span<int> ar, int order, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < order - 1; i++)
        {
            ar[i] = SilkFixedPoint.Smulww(chirpQ16, ar[i]);
            chirpQ16 = unchecked(chirpQ16
                + SilkFixedPoint.RshiftRound(SilkFixedPoint.Mul(chirpQ16, chirpMinusOneQ16), 16));
        }

        ar[order - 1] = SilkFixedPoint.Smulww(chirpQ16, ar[order - 1]);
    }

    /// <summary>
    /// Computes the inverse prediction gain of Q12 LPC coefficients and tests stability
    /// (RFC 6716 §4.2.7.5.8, with the RFC 8251 §6 64-bit range check).
    /// </summary>
    private static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order)
    {
        const int QA = 24;
        Span<int> aTmp = stackalloc int[2 * MaxOrder];
        int anew = (order & 1) * MaxOrder;
        int dcResponse = 0;
        for (int k = 0; k < order; k++)
        {
            dcResponse += aQ12[k];
            aTmp[anew + k] = SilkFixedPoint.Lshift(aQ12[k], QA - 12);
        }

        if (dcResponse >= 4096)
        {
            return 0;
        }

        return LpcInversePredGainQa(aTmp, order);
    }

    /// <summary>The Levinson recursion of <c>LPC_inverse_pred_gain_QA</c> (RFC 6716 §4.2.7.5.8).</summary>
    private static int LpcInversePredGainQa(Span<int> aQa, int order)
    {
        const int QA = 24;
        int anew = (order & 1) * MaxOrder;
        int inverseGainQ30 = 1 << 30;

        for (int k = order - 1; k > 0; k--)
        {
            if (aQa[anew + k] > ALimitQ24 || aQa[anew + k] < -ALimitQ24)
            {
                return 0;
            }

            int rcQ31 = -SilkFixedPoint.Lshift(aQa[anew + k], 31 - QA);
            int rcMult1Q30 = unchecked(
                (1 << 30) - SilkFixedPoint.Smmul(rcQ31, rcQ31));
            int mult2Q = 32 - SilkFixedPoint.Clz32(SilkFixedPoint.Abs(rcMult1Q30));
            int rcMult2 = SilkFixedPoint.Inverse32VarQ(rcMult1Q30, mult2Q + 30);

            inverseGainQ30 = SilkFixedPoint.Lshift(
                SilkFixedPoint.Smmul(inverseGainQ30, rcMult1Q30), 2);

            int aold = anew;
            anew = (k & 1) * MaxOrder;
            for (int n = 0; n < k; n++)
            {
                int tmpQa = SilkFixedPoint.SubSat32(
                    aQa[aold + n],
                    (int)SilkFixedPoint.RshiftRound64(
                        SilkFixedPoint.Smull(aQa[aold + k - n - 1], rcQ31), 31));
                long tmp64 = SilkFixedPoint.RshiftRound64(
                    SilkFixedPoint.Smull(tmpQa, rcMult2), mult2Q);
                if (tmp64 > SilkFixedPoint.Int32Max || tmp64 < SilkFixedPoint.Int32Min)
                {
                    return 0;
                }

                aQa[anew + n] = (int)tmp64;
            }
        }

        if (aQa[anew] > ALimitQ24 || aQa[anew] < -ALimitQ24)
        {
            return 0;
        }

        int rc0Q31 = -SilkFixedPoint.Lshift(aQa[anew], 31 - QA);
        int rcMult1Q30Last = unchecked(
            (1 << 30) - SilkFixedPoint.Smmul(rc0Q31, rc0Q31));
        return SilkFixedPoint.Lshift(
            SilkFixedPoint.Smmul(inverseGainQ30, rcMult1Q30Last), 2);
    }

    /// <summary>Insertion sort of a 16-bit vector in increasing order (RFC 6716 §4.2.7.5.4 fallback).</summary>
    private static void SortIncreasing(Span<short> values, int length)
    {
        for (int i = 1; i < length; i++)
        {
            short value = values[i];
            int j = i - 1;
            while (j >= 0 && value < values[j])
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = value;
        }
    }
}
