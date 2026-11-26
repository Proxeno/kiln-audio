// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The SILK noise shaping quantizer (RFC 6716 §5.2.3.8; Appendix A, <c>silk/NSQ.c</c>).
/// </summary>
/// <remarks>
/// This is the single-state quantizer. The delayed-decision variant is a quality refinement that
/// belongs to a later lane; the bitstream it produces is the same format either way.
/// </remarks>
internal static class SilkNsq
{
    private const int LpcBufferLength = 32;
    private const int QuantizationLevelAdjustQ10 = 80;

    /// <summary>Runs the noise shaping quantizer over one frame and produces the excitation pulses.</summary>
    internal static void Process(
        SilkEncoderState encoder,
        SilkNsqState nsq,
        SilkSideInfo indices,
        ReadOnlySpan<int> inputQ3,
        Span<short> pulses,
        ReadOnlySpan<short> prediction0Q12,
        ReadOnlySpan<short> prediction1Q12,
        ReadOnlySpan<short> ltpCoefficientsQ14,
        ReadOnlySpan<short> noiseShapeQ13,
        ReadOnlySpan<int> harmonicShapeGainQ14,
        ReadOnlySpan<int> tiltQ14,
        ReadOnlySpan<int> lfShapeQ14,
        ReadOnlySpan<int> gainsQ16,
        ReadOnlySpan<int> pitchLags,
        int lambdaQ10,
        int ltpScaleQ14)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int subframeLength = encoder.SubframeLength;
        int subframeCount = encoder.SubframeCount;
        int lpcOrder = encoder.PredictLpcOrder;

        Span<int> ltpQ15 = stackalloc int[2 * SilkEncoderMath.MaxFrameLength];
        Span<short> ltp = stackalloc short[2 * SilkEncoderMath.MaxFrameLength];
        Span<int> scaledInputQ10 = stackalloc int[SilkEncoderMath.MaxSubFrameLength];

        nsq.RandSeed = indices.Seed;
        int lag = nsq.LagPrev;
        int offsetQ10 = SilkSynthesisTables.QuantizationOffsetQ10[indices.SignalType >> 1][indices.QuantOffsetType];
        int interpolationFlag = indices.NlsfInterpCoefQ2 == 4 ? 0 : 1;

        nsq.SLtpShpBufIdx = encoder.LtpMemLength;
        nsq.SLtpBufIdx = encoder.LtpMemLength;
        int xqOffset = encoder.LtpMemLength;

        for (int k = 0; k < subframeCount; k++)
        {
            int predictionRow = (k >> 1) | (1 - interpolationFlag);
            ReadOnlySpan<short> predictionQ12 = predictionRow == 0 ? prediction0Q12 : prediction1Q12;
            int ltpOffset = k * order;
            int shapeOffset = k * SilkEncoderMath.MaxShapeLpcOrder;

            int harmonicShapeGain = harmonicShapeGainQ14[k];
            int harmonicShapeFirPackedQ14 = harmonicShapeGain >> 2;
            harmonicShapeFirPackedQ14 |= (harmonicShapeGain >> 1) << 16;

            nsq.RewhiteFlag = 0;
            if (indices.SignalType == 2)
            {
                lag = pitchLags[k];
                if ((k & (3 - (interpolationFlag << 1))) == 0)
                {
                    int startIndex = encoder.LtpMemLength - lag - lpcOrder - (order / 2);
                    SilkEncoderMath.LpcAnalysisFilter(
                        ltp[startIndex..],
                        nsq.Xq.AsSpan(startIndex + (k * subframeLength)),
                        predictionQ12[..lpcOrder],
                        encoder.LtpMemLength - startIndex,
                        lpcOrder);
                    nsq.RewhiteFlag = 1;
                    nsq.SLtpBufIdx = encoder.LtpMemLength;
                }
            }

            ScaleStates(
                encoder, nsq, inputQ3[(k * subframeLength)..], scaledInputQ10, ltp, ltpQ15,
                k, ltpScaleQ14, gainsQ16, pitchLags, indices.SignalType);

            NoiseShapeQuantizer(
                nsq, indices.SignalType, scaledInputQ10, pulses[(k * subframeLength)..],
                nsq.Xq.AsSpan(xqOffset + (k * subframeLength)), ltpQ15,
                predictionQ12[..lpcOrder],
                ltpCoefficientsQ14.Slice(ltpOffset, order),
                noiseShapeQ13.Slice(shapeOffset, encoder.ShapingLpcOrder),
                lag, harmonicShapeFirPackedQ14, tiltQ14[k], lfShapeQ14[k], gainsQ16[k],
                lambdaQ10, offsetQ10, subframeLength, encoder.ShapingLpcOrder, lpcOrder);
        }

        nsq.LagPrev = pitchLags[subframeCount - 1];

        // Retain the LTP memory and the noise shaping history for the next frame.
        int ltpMemory = encoder.LtpMemLength;
        int frameLength = encoder.FrameLength;
        nsq.Xq.AsSpan(frameLength, ltpMemory).CopyTo(nsq.Xq);
        nsq.SLtpShpQ14.AsSpan(frameLength, ltpMemory).CopyTo(nsq.SLtpShpQ14);
    }

    private static void ScaleStates(
        SilkEncoderState encoder,
        SilkNsqState nsq,
        ReadOnlySpan<int> inputQ3,
        Span<int> scaledInputQ10,
        ReadOnlySpan<short> ltp,
        Span<int> ltpQ15,
        int subframe,
        int ltpScaleQ14,
        ReadOnlySpan<int> gainsQ16,
        ReadOnlySpan<int> pitchLags,
        int signalType)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int lag = pitchLags[subframe];
        int inverseGainQ31 = SilkFixedPoint.Inverse32VarQ(Math.Max(gainsQ16[subframe], 1), 47);

        int gainAdjustmentQ16 = gainsQ16[subframe] != nsq.PrevGainQ16
            ? SilkFixedPoint.Div32VarQ(nsq.PrevGainQ16, gainsQ16[subframe], 16)
            : 1 << 16;

        int inverseGainQ23 = SilkEncoderMath.RshiftRound(inverseGainQ31, 8);
        for (int i = 0; i < encoder.SubframeLength; i++)
        {
            scaledInputQ10[i] = SilkFixedPoint.Smulww(inputQ3[i], inverseGainQ23);
        }

        nsq.PrevGainQ16 = gainsQ16[subframe];

        if (nsq.RewhiteFlag != 0)
        {
            if (subframe == 0)
            {
                inverseGainQ31 = SilkEncoderMath.Shl(
                    SilkFixedPoint.Smulwb(inverseGainQ31, ltpScaleQ14), 2);
            }

            for (int i = nsq.SLtpBufIdx - lag - (order / 2); i < nsq.SLtpBufIdx; i++)
            {
                ltpQ15[i] = SilkFixedPoint.Smulwb(inverseGainQ31, ltp[i]);
            }
        }

        if (gainAdjustmentQ16 != 1 << 16)
        {
            for (int i = nsq.SLtpShpBufIdx - encoder.LtpMemLength; i < nsq.SLtpShpBufIdx; i++)
            {
                nsq.SLtpShpQ14[i] = SilkFixedPoint.Smulww(gainAdjustmentQ16, nsq.SLtpShpQ14[i]);
            }

            if (signalType == 2 && nsq.RewhiteFlag == 0)
            {
                for (int i = nsq.SLtpBufIdx - lag - (order / 2); i < nsq.SLtpBufIdx; i++)
                {
                    ltpQ15[i] = SilkFixedPoint.Smulww(gainAdjustmentQ16, ltpQ15[i]);
                }
            }

            nsq.SLfArShpQ14 = SilkFixedPoint.Smulww(gainAdjustmentQ16, nsq.SLfArShpQ14);

            for (int i = 0; i < LpcBufferLength; i++)
            {
                nsq.SLpcQ14[i] = SilkFixedPoint.Smulww(gainAdjustmentQ16, nsq.SLpcQ14[i]);
            }

            for (int i = 0; i < SilkEncoderMath.MaxShapeLpcOrder; i++)
            {
                nsq.SAr2Q14[i] = SilkFixedPoint.Smulww(gainAdjustmentQ16, nsq.SAr2Q14[i]);
            }
        }
    }

    private static void NoiseShapeQuantizer(
        SilkNsqState nsq,
        int signalType,
        ReadOnlySpan<int> scaledInputQ10,
        Span<short> pulses,
        Span<short> quantizedOutput,
        Span<int> ltpQ15,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<short> ltpCoefficientsQ14,
        ReadOnlySpan<short> noiseShapeQ13,
        int lag,
        int harmonicShapeFirPackedQ14,
        int tiltQ14,
        int lfShapeQ14,
        int gainQ16,
        int lambdaQ10,
        int offsetQ10,
        int length,
        int shapingLpcOrder,
        int predictionLpcOrder)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int gainQ10 = gainQ16 >> 6;
        int lpcIndex = LpcBufferLength - 1;
        int shapeLagIndex = nsq.SLtpShpBufIdx - lag + (SilkEncoderMath.HarmShapeFirTaps / 2);
        int predictionLagIndex = nsq.SLtpBufIdx - lag + (order / 2);

        for (int i = 0; i < length; i++)
        {
            nsq.RandSeed = SilkFixedPoint.Rand(nsq.RandSeed);
            int lpcPredictionQ10 = predictionLpcOrder >> 1;
            for (int j = 0; j < predictionLpcOrder; j++)
            {
                lpcPredictionQ10 = SilkFixedPoint.Smlawb(
                    lpcPredictionQ10, nsq.SLpcQ14[lpcIndex - j], coefficientsQ12[j]);
            }

            int ltpPredictionQ13;
            if (signalType == 2)
            {
                ltpPredictionQ13 = 2;
                ltpPredictionQ13 = SilkFixedPoint.Smlawb(ltpPredictionQ13, ltpQ15[predictionLagIndex], ltpCoefficientsQ14[0]);
                ltpPredictionQ13 = SilkFixedPoint.Smlawb(ltpPredictionQ13, ltpQ15[predictionLagIndex - 1], ltpCoefficientsQ14[1]);
                ltpPredictionQ13 = SilkFixedPoint.Smlawb(ltpPredictionQ13, ltpQ15[predictionLagIndex - 2], ltpCoefficientsQ14[2]);
                ltpPredictionQ13 = SilkFixedPoint.Smlawb(ltpPredictionQ13, ltpQ15[predictionLagIndex - 3], ltpCoefficientsQ14[3]);
                ltpPredictionQ13 = SilkFixedPoint.Smlawb(ltpPredictionQ13, ltpQ15[predictionLagIndex - 4], ltpCoefficientsQ14[4]);
                predictionLagIndex++;
            }
            else
            {
                ltpPredictionQ13 = 0;
            }

            int tmp2 = nsq.SLpcQ14[lpcIndex];
            int tmp1 = nsq.SAr2Q14[0];
            nsq.SAr2Q14[0] = tmp2;
            int noiseArQ12 = shapingLpcOrder >> 1;
            noiseArQ12 = SilkFixedPoint.Smlawb(noiseArQ12, tmp2, noiseShapeQ13[0]);
            for (int j = 2; j < shapingLpcOrder; j += 2)
            {
                tmp2 = nsq.SAr2Q14[j - 1];
                nsq.SAr2Q14[j - 1] = tmp1;
                noiseArQ12 = SilkFixedPoint.Smlawb(noiseArQ12, tmp1, noiseShapeQ13[j - 1]);
                tmp1 = nsq.SAr2Q14[j];
                nsq.SAr2Q14[j] = tmp2;
                noiseArQ12 = SilkFixedPoint.Smlawb(noiseArQ12, tmp2, noiseShapeQ13[j]);
            }

            nsq.SAr2Q14[shapingLpcOrder - 1] = tmp1;
            noiseArQ12 = SilkFixedPoint.Smlawb(noiseArQ12, tmp1, noiseShapeQ13[shapingLpcOrder - 1]);
            noiseArQ12 = SilkEncoderMath.Shl(noiseArQ12, 1);
            noiseArQ12 = SilkFixedPoint.Smlawb(noiseArQ12, nsq.SLfArShpQ14, tiltQ14);

            int noiseLfQ12 = SilkFixedPoint.Smulwb(nsq.SLtpShpQ14[nsq.SLtpShpBufIdx - 1], lfShapeQ14);
            noiseLfQ12 = SilkEncoderMath.Smlawt(noiseLfQ12, nsq.SLfArShpQ14, lfShapeQ14);

            tmp1 = SilkEncoderMath.Sub(SilkEncoderMath.Shl(lpcPredictionQ10, 2), noiseArQ12);
            tmp1 = SilkEncoderMath.Sub(tmp1, noiseLfQ12);
            if (lag > 0)
            {
                int noiseLtpQ13 = SilkFixedPoint.Smulwb(
                    unchecked(nsq.SLtpShpQ14[shapeLagIndex] + nsq.SLtpShpQ14[shapeLagIndex - 2]),
                    harmonicShapeFirPackedQ14);
                noiseLtpQ13 = SilkEncoderMath.Smlawt(noiseLtpQ13, nsq.SLtpShpQ14[shapeLagIndex - 1], harmonicShapeFirPackedQ14);
                noiseLtpQ13 = SilkEncoderMath.Shl(noiseLtpQ13, 1);
                shapeLagIndex++;
                tmp2 = SilkEncoderMath.Sub(ltpPredictionQ13, noiseLtpQ13);
                tmp1 = SilkEncoderMath.AddLshift(tmp2, tmp1, 1);
                tmp1 = SilkEncoderMath.RshiftRound(tmp1, 3);
            }
            else
            {
                tmp1 = SilkEncoderMath.RshiftRound(tmp1, 2);
            }

            int residualQ10 = SilkEncoderMath.Sub(scaledInputQ10[i], tmp1);
            if (nsq.RandSeed < 0)
            {
                residualQ10 = -residualQ10;
            }

            residualQ10 = SilkEncoderMath.Limit(residualQ10, -(31 << 10), 30 << 10);

            int quantized1Q10 = SilkEncoderMath.Sub(residualQ10, offsetQ10);
            int quantizedIndex = quantized1Q10 >> 10;
            int quantized2Q10;
            int rd1Q20;
            int rd2Q20;
            if (quantizedIndex > 0)
            {
                quantized1Q10 = SilkEncoderMath.Sub(SilkEncoderMath.Shl(quantizedIndex, 10), QuantizationLevelAdjustQ10);
                quantized1Q10 = SilkEncoderMath.Add(quantized1Q10, offsetQ10);
                quantized2Q10 = SilkEncoderMath.Add(quantized1Q10, 1024);
                rd1Q20 = SilkEncoderMath.Smulbb(quantized1Q10, lambdaQ10);
                rd2Q20 = SilkEncoderMath.Smulbb(quantized2Q10, lambdaQ10);
            }
            else if (quantizedIndex == 0)
            {
                quantized1Q10 = offsetQ10;
                quantized2Q10 = SilkEncoderMath.Add(quantized1Q10, 1024 - QuantizationLevelAdjustQ10);
                rd1Q20 = SilkEncoderMath.Smulbb(quantized1Q10, lambdaQ10);
                rd2Q20 = SilkEncoderMath.Smulbb(quantized2Q10, lambdaQ10);
            }
            else if (quantizedIndex == -1)
            {
                quantized2Q10 = offsetQ10;
                quantized1Q10 = SilkEncoderMath.Sub(quantized2Q10, 1024 - QuantizationLevelAdjustQ10);
                rd1Q20 = SilkEncoderMath.Smulbb(-quantized1Q10, lambdaQ10);
                rd2Q20 = SilkEncoderMath.Smulbb(quantized2Q10, lambdaQ10);
            }
            else
            {
                quantized1Q10 = SilkEncoderMath.Add(SilkEncoderMath.Shl(quantizedIndex, 10), QuantizationLevelAdjustQ10);
                quantized1Q10 = SilkEncoderMath.Add(quantized1Q10, offsetQ10);
                quantized2Q10 = SilkEncoderMath.Add(quantized1Q10, 1024);
                rd1Q20 = SilkEncoderMath.Smulbb(-quantized1Q10, lambdaQ10);
                rd2Q20 = SilkEncoderMath.Smulbb(-quantized2Q10, lambdaQ10);
            }

            int errorQ10 = SilkEncoderMath.Sub(residualQ10, quantized1Q10);
            rd1Q20 = SilkEncoderMath.Smlabb(rd1Q20, errorQ10, errorQ10);
            errorQ10 = SilkEncoderMath.Sub(residualQ10, quantized2Q10);
            rd2Q20 = SilkEncoderMath.Smlabb(rd2Q20, errorQ10, errorQ10);

            if (rd2Q20 < rd1Q20)
            {
                quantized1Q10 = quantized2Q10;
            }

            pulses[i] = (short)SilkEncoderMath.RshiftRound(quantized1Q10, 10);

            int excitationQ14 = SilkEncoderMath.Shl(quantized1Q10, 4);
            if (nsq.RandSeed < 0)
            {
                excitationQ14 = -excitationQ14;
            }

            int lpcExcitationQ14 = SilkEncoderMath.AddLshift(excitationQ14, ltpPredictionQ13, 1);
            int outputQ14 = SilkEncoderMath.AddLshift(lpcExcitationQ14, lpcPredictionQ10, 4);

            quantizedOutput[i] = (short)SilkEncoderMath.Sat16(
                SilkEncoderMath.RshiftRound(SilkFixedPoint.Smulww(outputQ14, gainQ10), 8));

            lpcIndex++;
            nsq.SLpcQ14[lpcIndex] = outputQ14;
            int lfArShapeQ14 = SilkEncoderMath.SubLshift(outputQ14, noiseArQ12, 2);
            nsq.SLfArShpQ14 = lfArShapeQ14;
            nsq.SLtpShpQ14[nsq.SLtpShpBufIdx] = SilkEncoderMath.SubLshift(lfArShapeQ14, noiseLfQ12, 2);
            ltpQ15[nsq.SLtpBufIdx] = SilkEncoderMath.Shl(lpcExcitationQ14, 1);
            nsq.SLtpShpBufIdx++;
            nsq.SLtpBufIdx++;

            nsq.RandSeed = unchecked(nsq.RandSeed + pulses[i]);
        }

        // Retain the LPC synthesis history for the next subframe.
        nsq.SLpcQ14.AsSpan(length, LpcBufferLength).CopyTo(nsq.SLpcQ14);
    }
}
