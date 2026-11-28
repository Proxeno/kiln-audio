using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The per-subframe inputs of the lane-parallel delayed-decision quantizer.</summary>
internal readonly ref struct SilkNsqSubframeArgs
{
    /// <summary>The live trellis states, 1 to 4 (the other lanes shadow lane 0 and never influence a decision).</summary>
    public required int StateCount { get; init; }

    /// <summary>The subframe index within the frame.</summary>
    public required int Subframe { get; init; }

    /// <summary>The number of subframes quantized so far in the current interpolation half (reference <c>subfr</c>).</summary>
    public required int SubframeArgument { get; init; }

    /// <summary>The pitch lag in samples (0 when unvoiced and no previous lag).</summary>
    public required int Lag { get; init; }

    /// <summary>The decision delay in samples.</summary>
    public required int DecisionDelay { get; init; }

    /// <summary>The quantization offset, Q10.</summary>
    public required int OffsetQ10 { get; init; }

    /// <summary>The rate/distortion trade-off, Q10.</summary>
    public required int LambdaQ10 { get; init; }

    /// <summary>The warping coefficient, Q16.</summary>
    public required int WarpingQ16 { get; init; }

    /// <summary>The harmonic shaping FIR, packed (low and high 16 bits).</summary>
    public required int HarmonicFirPacked { get; init; }

    /// <summary>The spectral tilt, Q14.</summary>
    public required int TiltQ14 { get; init; }

    /// <summary>The low-frequency shaping, packed.</summary>
    public required int LowFrequencyShapingQ14 { get; init; }

    /// <summary>The subframe gain, Q10.</summary>
    public required int GainQ10 { get; init; }

    /// <summary>Whether the frame is voiced.</summary>
    public required bool Voiced { get; init; }

    /// <summary>The offset of the output samples in the quantized-signal history.</summary>
    public required int LtpMemoryLength { get; init; }

    /// <summary>The short-term prediction coefficients, Q12 (length is the prediction order).</summary>
    public required ReadOnlySpan<short> Prediction { get; init; }

    /// <summary>The long-term prediction taps, Q14 (five).</summary>
    public required ReadOnlySpan<short> Ltp { get; init; }

    /// <summary>The noise shaping coefficients, Q13 (length is the shaping order).</summary>
    public required ReadOnlySpan<short> Shaping { get; init; }
}

/// <summary>
/// The delayed-decision quantizer's per-sample loop with one SIMD lane per trellis state (RFC 6716 Appendix A
/// <c>silk_noise_shape_quantizer_del_dec</c>): the short-term prediction, the warped noise-shaping chain, the candidate
/// levels and the rate/distortion scores of all four states run together. The states' histories are stored
/// position-major (<c>array[position * 4 + state]</c>) so a position is one vector. Only the winner search and the
/// state replacement stay scalar, because they are over four numbers. Every operation is exact integer arithmetic with
/// the reference's wrap-around and 16-bit operand truncation, so the result is bit-identical to the scalar reference
/// on every tier.
/// </summary>
internal static class SilkNsqVec<TOps>
    where TOps : struct, IIntOps
{
    private const int Lanes = 4;
    private const int Delay = SilkNsqDelayedDecision.DecisionDelay;
    private const int LpcLength = SilkNsqDelayedDecision.LpcBufferLength;
    private const int PenaltyQ10 = int.MaxValue >> 4;

    /// <summary><c>data[i] = Smulww(gain, data[i])</c> over the whole span (the reference's <c>silk_SMULWW</c>).</summary>
    internal static void ScaleQ16(Span<int> data, int gainQ16)
    {
        int i = 0;
        if (data.Length >= Lanes)
        {
            Vector128<int> gh = Vector128.Create(gainQ16 >> 16);
            Vector128<int> gl = Vector128.Create(gainQ16 & 0xFFFF);
            Vector128<int> g = Vector128.Create(gainQ16);
            ref int start = ref MemoryMarshal.GetReference(data);
            for (; i <= data.Length - Lanes; i += Lanes)
            {
                Vector128<int> a = Vector128.LoadUnsafe(ref start, (nuint)i);
                Vector128<int> ah = Vector128.ShiftRightArithmetic(a, 16);
                Vector128<int> al = a & Vector128.Create(0xFFFF);
                // (a * g) >> 16 modulo 2^32, from 32-bit partial products: a*gh + ah*gl + ((al*gl) >>> 16).
                Vector128<int> r = (a * gh) + (ah * gl) + Vector128.ShiftRightLogical(al * gl, 16);
                r.StoreUnsafe(ref start, (nuint)i);
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] = SilkFixedPoint.Smulww(gainQ16, data[i]);
        }
    }

    /// <summary>Quantizes one subframe over all states.</summary>
    internal static void Subframe(SilkNsqDelayedDecision q, in SilkNsqSubframeArgs a)
    {
        int count = a.StateCount;
        int length = q.CurrentSubframeLength;
        int predOrder = a.Prediction.Length;
        int shapeOrder = a.Shaping.Length;
        int decisionDelay = a.DecisionDelay;
        int lag = a.Lag;
        bool voiced = a.Voiced;
        int[] lpc = q.LpcSoa;
        int[] ar2 = q.Ar2Soa;
        int[] rnd = q.RandSoa;
        int[] qq = q.QSoa;
        int[] xq14 = q.XqSoa;
        int[] pred15 = q.PredSoa;
        int[] shp = q.ShapeSoa;
        int[] sLtpQ15 = q.SLtpQ15;
        int[] sLtpShp = q.SLtpShpQ14;
        int[] xSc = q.XScQ10;
        int[] cand = q.Candidates;
        Vector128<int>[] predP = q.PredictionPrepared;
        Vector128<int>[] shapeP = q.ShapingPrepared;

        for (int j = 0; j < predOrder; j++)
        {
            predP[j] = TOps.PrepCoef(a.Prediction[j]);
        }

        for (int j = 0; j < shapeOrder; j++)
        {
            shapeP[j] = TOps.PrepCoef(a.Shaping[j]);
        }

        Vector128<int> warpP = TOps.PrepCoef((short)a.WarpingQ16);
        Vector128<int> tiltP = TOps.PrepCoef((short)a.TiltQ14);
        Vector128<int> lfLowP = TOps.PrepCoef((short)a.LowFrequencyShapingQ14);
        Vector128<int> lfHighP = TOps.PrepCoef(a.LowFrequencyShapingQ14 >> 16);
        Vector128<int> predInit = Vector128.Create(predOrder >> 1);
        Vector128<int> shapeInit = Vector128.Create(shapeOrder >> 1);

        Vector128<int> offset = Vector128.Create(a.OffsetQ10);
        Vector128<int> lambda = Vector128.Create((int)(short)a.LambdaQ10);
        Vector128<int> offsetStep = offset - Vector128.Create(1024 - SilkNsqDelayedDecisionKernel.QuantLevelAdjustQ10);
        Vector128<int> offsetUp = offset + Vector128.Create(1024 - SilkNsqDelayedDecisionKernel.QuantLevelAdjustQ10);
        Vector128<int> adjust = Vector128.Create(SilkNsqDelayedDecisionKernel.QuantLevelAdjustQ10);
        Vector128<int> resMin = Vector128.Create(SilkNsqDelayedDecisionKernel.ResidualMinQ10);
        Vector128<int> resMax = Vector128.Create(SilkNsqDelayedDecisionKernel.ResidualMaxQ10);
        Vector128<int> k1024 = Vector128.Create(1024);
        Vector128<int> minusOne = Vector128.Create(-1);
        Vector128<int> laneIndex = Vector128.Create(0, 1, 2, 3);

        Vector128<int> seed = Ld(q.LaneSeed, 0);
        Vector128<int> stateRd = Ld(q.LaneRd, 0);
        Vector128<int> lfAr = Ld(q.LaneLfAr, 0);

        int sampleBufferIdx = q.SampleBufferIdx;
        int sLtpBufIdx = q.SLtpBufIdx;
        int sLtpShpBufIdx = q.SLtpShpBufIdx;
        int shapingLagIndex = sLtpShpBufIdx - lag + 1;
        int predictionLagIndex = sLtpBufIdx - lag + 2;
        int harmonic = a.HarmonicFirPacked;
        int ltp0 = a.Ltp[0];
        int ltp1 = a.Ltp[1];
        int ltp2 = a.Ltp[2];
        int ltp3 = a.Ltp[3];
        int ltp4 = a.Ltp[4];
        int subframeBase = a.Subframe * length;

        for (int i = 0; i < length; i++)
        {
            // Long-term prediction and shaping: shared by every state.
            int ltpPredictionQ14 = 0;
            if (voiced)
            {
                int acc = 2;
                acc = SilkFixedPoint.Smlawb(acc, sLtpQ15[predictionLagIndex], ltp0);
                acc = SilkFixedPoint.Smlawb(acc, sLtpQ15[predictionLagIndex - 1], ltp1);
                acc = SilkFixedPoint.Smlawb(acc, sLtpQ15[predictionLagIndex - 2], ltp2);
                acc = SilkFixedPoint.Smlawb(acc, sLtpQ15[predictionLagIndex - 3], ltp3);
                acc = SilkFixedPoint.Smlawb(acc, sLtpQ15[predictionLagIndex - 4], ltp4);
                ltpPredictionQ14 = SilkFixedPoint.Lshift(acc, 1);
                predictionLagIndex++;
            }

            int longTermShapingQ14 = 0;
            if (lag > 0)
            {
                longTermShapingQ14 = SilkFixedPoint.Smulwb(
                    sLtpShp[shapingLagIndex] + sLtpShp[shapingLagIndex - 2], harmonic);
                longTermShapingQ14 = Smlawt(longTermShapingQ14, sLtpShp[shapingLagIndex - 1], harmonic);
                longTermShapingQ14 = SilkFixedPoint.Sub(ltpPredictionQ14, longTermShapingQ14 << 2);
                shapingLagIndex++;
            }

            seed = (seed * Vector128.Create(196314165)) + Vector128.Create(907633515);

            // Short-term prediction, one multiply-accumulate per tap for all states.
            int lpcBase = LpcLength - 1 + i;
            // Four independent accumulators break the serial dependency; modular addition is associative, so the sum is exact.
            Vector128<int> lpcAcc = predInit;
            Vector128<int> lpcAcc1 = Vector128<int>.Zero;
            Vector128<int> lpcAcc2 = Vector128<int>.Zero;
            Vector128<int> lpcAcc3 = Vector128<int>.Zero;
            int tap = 0;
            for (; tap <= predOrder - 4; tap += 4)
            {
                lpcAcc = TOps.Smlawb(lpcAcc, Ld(lpc, lpcBase - tap), predP[tap]);
                lpcAcc1 = TOps.Smlawb(lpcAcc1, Ld(lpc, lpcBase - tap - 1), predP[tap + 1]);
                lpcAcc2 = TOps.Smlawb(lpcAcc2, Ld(lpc, lpcBase - tap - 2), predP[tap + 2]);
                lpcAcc3 = TOps.Smlawb(lpcAcc3, Ld(lpc, lpcBase - tap - 3), predP[tap + 3]);
            }

            for (; tap < predOrder; tap++)
            {
                lpcAcc = TOps.Smlawb(lpcAcc, Ld(lpc, lpcBase - tap), predP[tap]);
            }

            lpcAcc = (lpcAcc + lpcAcc1) + (lpcAcc2 + lpcAcc3);

            Vector128<int> lpcPred = Vector128.ShiftLeft(lpcAcc, 4);

            // Warped noise shaping filter, the serial chain run on all states at once.
            Vector128<int> noiseAr;
            if ((short)a.WarpingQ16 == 0)
            {
                // Without warping every Smlawb of the chain multiplies by zero, so the all-pass cascade degenerates
                // to a one-position shift register and the taps become independent multiply-accumulates.
                Vector128<int> x0 = Ld(lpc, lpcBase);
                Vector128<int> shift0 = Ld(ar2, 0);
                noiseAr = TOps.Smlawb(shapeInit, x0, shapeP[0]);
                Vector128<int> acc1 = Vector128<int>.Zero;
                Vector128<int> carry = x0;
                int j = 1;
                for (; j + 1 < shapeOrder; j += 2)
                {
                    Vector128<int> v0 = Ld(ar2, j - 1);
                    Vector128<int> v1 = Ld(ar2, j);
                    noiseAr = TOps.Smlawb(noiseAr, v0, shapeP[j]);
                    acc1 = TOps.Smlawb(acc1, v1, shapeP[j + 1]);
                    St(ar2, j - 1, carry);
                    St(ar2, j, v0);
                    carry = v1;
                }

                for (; j < shapeOrder; j++)
                {
                    Vector128<int> v0 = Ld(ar2, j - 1);
                    noiseAr = TOps.Smlawb(noiseAr, v0, shapeP[j]);
                    St(ar2, j - 1, carry);
                    carry = v0;
                }

                St(ar2, shapeOrder - 1, carry);
                noiseAr += acc1;
            }
            else
            {
                Vector128<int> a0 = Ld(ar2, 0);
                Vector128<int> t2 = TOps.Smlawb(Ld(lpc, lpcBase), a0, warpP);
                Vector128<int> t1 = TOps.Smlawb(a0, Ld(ar2, 1) - t2, warpP);
                St(ar2, 0, t2);
                noiseAr = TOps.Smlawb(shapeInit, t2, shapeP[0]);
                for (int j = 2; j < shapeOrder; j += 2)
                {
                    t2 = TOps.Smlawb(Ld(ar2, j - 1), Ld(ar2, j) - t1, warpP);
                    St(ar2, j - 1, t1);
                    noiseAr = TOps.Smlawb(noiseAr, t1, shapeP[j - 1]);
                    t1 = TOps.Smlawb(Ld(ar2, j), Ld(ar2, j + 1) - t2, warpP);
                    St(ar2, j, t2);
                    noiseAr = TOps.Smlawb(noiseAr, t2, shapeP[j]);
                }

                St(ar2, shapeOrder - 1, t1);
                noiseAr = TOps.Smlawb(noiseAr, t1, shapeP[shapeOrder - 1]);
            }

            noiseAr = Vector128.ShiftLeft(noiseAr, 1);
            noiseAr = TOps.Smlawb(noiseAr, lfAr, tiltP);
            noiseAr = Vector128.ShiftLeft(noiseAr, 2);

            Vector128<int> noiseLf = TOps.Smlawb(Vector128<int>.Zero, Ld(shp, sampleBufferIdx), lfLowP);
            noiseLf = TOps.Smlawb(noiseLf, lfAr, lfHighP);
            noiseLf = Vector128.ShiftLeft(noiseLf, 2);

            Vector128<int> combined = Vector128.ShiftRightArithmetic(
                Vector128.ShiftRightArithmetic(
                    (Vector128.Create(longTermShapingQ14) + lpcPred) - (noiseAr + noiseLf), 3) + Vector128.Create(1),
                1);
            Vector128<int> residual = Vector128.Create(xSc[i]) - combined;

            // Candidate levels and rate/distortion scores (the same arithmetic as SilkNsqDelayedDecisionKernel.EvaluateSimd).
            Vector128<int> seedNeg = Vector128.LessThan(seed, Vector128<int>.Zero);
            Vector128<int> rv = Vector128.ConditionalSelect(seedNeg, -residual, residual);
            rv = Vector128.Min(Vector128.Max(rv, resMin), resMax);
            Vector128<int> q1Q0 = Vector128.ShiftRightArithmetic(rv - offset, 10);
            Vector128<int> gt0 = Vector128.GreaterThan(q1Q0, Vector128<int>.Zero);
            Vector128<int> eq0 = Vector128.Equals(q1Q0, Vector128<int>.Zero);
            Vector128<int> eqm1 = Vector128.Equals(q1Q0, minusOne);
            Vector128<int> lt = Vector128.LessThan(q1Q0, minusOne);
            Vector128<int> shifted = Vector128.ShiftLeft(q1Q0, 10);
            Vector128<int> q1a = shifted - adjust + offset;
            Vector128<int> q1d = shifted + adjust + offset;
            Vector128<int> q1 = (gt0 & q1a) | (eq0 & offset) | (eqm1 & offsetStep) | (lt & q1d);
            Vector128<int> q2 = (gt0 & (q1a + k1024)) | (eq0 & offsetUp) | (eqm1 & offset) | (lt & (q1d + k1024));

            Vector128<int> n1 = Narrow16(q1);
            n1 = Vector128.ConditionalSelect(Vector128.LessThan(n1, Vector128<int>.Zero), -n1, n1);
            Vector128<int> n2 = Narrow16(q2);
            n2 = Vector128.ConditionalSelect(Vector128.LessThan(n2, Vector128<int>.Zero), -n2, n2);
            Vector128<int> rr1 = Narrow16(rv - q1);
            Vector128<int> rr2 = Narrow16(rv - q2);
            Vector128<int> rd1 = Vector128.ShiftRightArithmetic((n1 * lambda) + (rr1 * rr1), 10);
            Vector128<int> rd2 = Vector128.ShiftRightArithmetic((n2 * lambda) + (rr2 * rr2), 10);

            // First candidate is the cheaper level (a tie goes to the second level, as in the reference).
            Vector128<int> firstIsOne = Vector128.LessThan(rd1, rd2);
            Vector128<int> firstRd = stateRd + Vector128.ConditionalSelect(firstIsOne, rd1, rd2);
            Vector128<int> secondRd = stateRd + Vector128.ConditionalSelect(firstIsOne, rd2, rd1);
            Vector128<int> firstQ = Vector128.ConditionalSelect(firstIsOne, q1, q2);
            Vector128<int> secondQ = Vector128.ConditionalSelect(firstIsOne, q2, q1);

            Vector128<int> ltpP = Vector128.Create(ltpPredictionQ14);
            Vector128<int> exc1 = Vector128.ShiftLeft(firstQ, 4);
            Vector128<int> lpcExc1 = Vector128.ConditionalSelect(seedNeg, -exc1, exc1) + ltpP;
            Vector128<int> xq1 = lpcExc1 + lpcPred;
            Vector128<int> lf1 = xq1 - noiseAr;
            Vector128<int> sh1 = lf1 - noiseLf;
            Vector128<int> exc2 = Vector128.ShiftLeft(secondQ, 4);
            Vector128<int> lpcExc2 = Vector128.ConditionalSelect(seedNeg, -exc2, exc2) + ltpP;
            Vector128<int> xq2 = lpcExc2 + lpcPred;
            Vector128<int> lf2 = xq2 - noiseAr;
            Vector128<int> sh2 = lf2 - noiseLf;

            sampleBufferIdx = (sampleBufferIdx - 1) & (Delay - 1);
            int lastSampleIndex = (sampleBufferIdx + decisionDelay) & (Delay - 1);

            St(cand, 0, firstRd);
            St(cand, 1, secondRd);

            // Winner, divergence penalty and the replacement of the worst first-set state: four numbers, scalar.
            int winner = 0;
            int minimum = cand[0];
            for (int s = 1; s < count; s++)
            {
                if (cand[s] < minimum)
                {
                    minimum = cand[s];
                    winner = s;
                }
            }

            int winnerRand = rnd[(lastSampleIndex * Lanes) + winner];
            for (int s = 0; s < count; s++)
            {
                if (rnd[(lastSampleIndex * Lanes) + s] != winnerRand)
                {
                    cand[s] += PenaltyQ10;
                    cand[Lanes + s] += PenaltyQ10;
                }
            }

            int worstIndex = 0;
            int worst = cand[0];
            int bestIndex = 0;
            int best = cand[Lanes];
            for (int s = 1; s < count; s++)
            {
                if (cand[s] > worst)
                {
                    worst = cand[s];
                    worstIndex = s;
                }

                if (cand[Lanes + s] < best)
                {
                    best = cand[Lanes + s];
                    bestIndex = s;
                }
            }

            if (best < worst)
            {
                CopyLane(q, bestIndex, worstIndex, i, sampleBufferIdx, decisionDelay);
                cand[worstIndex] = cand[Lanes + bestIndex];
                Vector128<int> pick = Vector128.Equals(laneIndex, Vector128.Create(worstIndex));
                seed = Vector128.ConditionalSelect(pick, Vector128.Create(seed.GetElement(bestIndex)), seed);
                q.LaneSeedInit[worstIndex] = q.LaneSeedInit[bestIndex];
                firstQ = Vector128.ConditionalSelect(pick, Vector128.Create(secondQ.GetElement(bestIndex)), firstQ);
                xq1 = Vector128.ConditionalSelect(pick, Vector128.Create(xq2.GetElement(bestIndex)), xq1);
                lf1 = Vector128.ConditionalSelect(pick, Vector128.Create(lf2.GetElement(bestIndex)), lf1);
                sh1 = Vector128.ConditionalSelect(pick, Vector128.Create(sh2.GetElement(bestIndex)), sh1);
                lpcExc1 = Vector128.ConditionalSelect(pick, Vector128.Create(lpcExc2.GetElement(bestIndex)), lpcExc1);
            }

            // Write the delayed sample from the winner.
            if (a.SubframeArgument > 0 || i >= decisionDelay)
            {
                int pulseIndex = subframeBase + i - decisionDelay;
                int slot = (lastSampleIndex * Lanes) + winner;
                q.Pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(qq[slot], 10);
                q.Xq[a.LtpMemoryLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                    SilkFixedPoint.Smulww(xq14[slot], q.DelayedGainQ10[lastSampleIndex]), 8));
                sLtpShp[sLtpShpBufIdx - decisionDelay] = shp[slot];
                sLtpQ15[sLtpBufIdx - decisionDelay] = pred15[slot];
            }

            sLtpShpBufIdx++;
            sLtpBufIdx++;

            // Advance every state.
            lfAr = lf1;
            St(lpc, LpcLength + i, xq1);
            St(xq14, sampleBufferIdx, xq1);
            St(qq, sampleBufferIdx, firstQ);
            St(pred15, sampleBufferIdx, Vector128.ShiftLeft(lpcExc1, 1));
            St(shp, sampleBufferIdx, sh1);
            seed += Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(firstQ, 9) + Vector128.Create(1), 1);
            St(rnd, sampleBufferIdx, seed);
            stateRd = Ld(cand, 0);
            q.DelayedGainQ10[sampleBufferIdx] = a.GainQ10;
        }

        St(q.LaneSeed, 0, seed);
        St(q.LaneRd, 0, stateRd);
        St(q.LaneLfAr, 0, lfAr);
        q.SampleBufferIdx = sampleBufferIdx;
        q.SLtpBufIdx = sLtpBufIdx;
        q.SLtpShpBufIdx = sLtpShpBufIdx;
    }

    /// <summary>
    /// Copies state <paramref name="source"/>'s live history into state <paramref name="destination"/> (the reference's
    /// <c>silk_copy_del_dec_state</c> restricted to the entries that are read again).
    /// </summary>
    private static void CopyLane(SilkNsqDelayedDecision q, int source, int destination, int sample, int sampleBufferIdx, int decisionDelay)
    {
        int[] lpc = q.LpcSoa;
        for (int p = sample + 16; p < sample + LpcLength; p++)
        {
            lpc[(p * Lanes) + destination] = lpc[(p * Lanes) + source];
        }

        int[] ar2 = q.Ar2Soa;
        for (int j = 0; j < SilkNsqDelayedDecision.MaxShapingOrder; j++)
        {
            ar2[(j * Lanes) + destination] = ar2[(j * Lanes) + source];
        }

        int[] rnd = q.RandSoa;
        int[] qq = q.QSoa;
        int[] xq = q.XqSoa;
        int[] pred = q.PredSoa;
        int[] shape = q.ShapeSoa;
        for (int t = 1; t <= decisionDelay; t++)
        {
            int slot = (((sampleBufferIdx + t) & (Delay - 1)) * Lanes);
            rnd[slot + destination] = rnd[slot + source];
            qq[slot + destination] = qq[slot + source];
            xq[slot + destination] = xq[slot + source];
            pred[slot + destination] = pred[slot + source];
            shape[slot + destination] = shape[slot + source];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Ld(int[] array, int position)
    {
        Debug.Assert((uint)((position * Lanes) + Lanes) <= (uint)array.Length, "vector load out of range");
        return Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(array), (nuint)(position * Lanes));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St(int[] array, int position, Vector128<int> value)
    {
        Debug.Assert((uint)((position * Lanes) + Lanes) <= (uint)array.Length, "vector store out of range");
        value.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(array), (nuint)(position * Lanes));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Narrow16(Vector128<int> value) =>
        Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(value, 16), 16);

    /// <summary>The reference's <c>silk_SMLAWT</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Smlawt(int a, int b, int c) => unchecked(a + (int)(((long)b * (c >> 16)) >> 16));
}
