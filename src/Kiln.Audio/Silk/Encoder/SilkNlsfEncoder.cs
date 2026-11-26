// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Conversion between prediction coefficients and normalized LSFs, and the multi-stage NLSF
/// quantizer (RFC 6716 §5.2.3.4 and §5.2.3.5; Appendix A, <c>silk/A2NLSF.c</c>,
/// <c>silk/NLSF_encode.c</c>, <c>silk/NLSF_del_dec_quant.c</c> and friends).
/// </summary>
/// <remarks>
/// The decoder-side reconstruction (<see cref="SilkNlsf"/>) is reused unchanged: the encoder
/// quantizes, then immediately reconstructs with the decoder's exact routine so the local LPC
/// coefficients match what a decoder will produce.
/// </remarks>
internal static class SilkNlsfEncoder
{
    private const int BinDivSteps = 3;
    private const int MaxIterations = 30;
    private const int MaxA2NlsfOrder = 16;
    private const int DelayedDecisionStatesLog2 = 2;
    private const int DelayedDecisionStates = 1 << DelayedDecisionStatesLog2;

    /// <summary>Computes NLSFs from Q24 monic prediction coefficients (Appendix A, <c>silk_A2NLSF</c>).</summary>
    internal static void A2Nlsf(Span<short> nlsfQ15, Span<int> coefficientsQ16, int order)
    {
        Span<int> p = stackalloc int[(MaxA2NlsfOrder / 2) + 1];
        Span<int> q = stackalloc int[(MaxA2NlsfOrder / 2) + 1];
        int half = order >> 1;

        A2NlsfInit(coefficientsQ16, p, q, half);
        Span<int> polynomial = p;
        int low = SilkSynthesisTables.LsfCosQ12[0];
        int lowValue = EvalPoly(polynomial, low, half);
        int rootIndex;
        if (lowValue < 0)
        {
            nlsfQ15[0] = 0;
            polynomial = q;
            lowValue = EvalPoly(polynomial, low, half);
            rootIndex = 1;
        }
        else
        {
            rootIndex = 0;
        }

        int k = 1;
        int expansions = 0;
        int threshold = 0;
        while (true)
        {
            int high = SilkSynthesisTables.LsfCosQ12[k];
            int highValue = EvalPoly(polynomial, high, half);

            if ((lowValue <= 0 && highValue >= threshold) || (lowValue >= 0 && highValue <= -threshold))
            {
                if (highValue == 0)
                {
                    threshold = 1;
                }
                else
                {
                    threshold = 0;
                }

                int fraction = -256;
                for (int m = 0; m < BinDivSteps; m++)
                {
                    int mid = SilkFixedPoint.RshiftRound(low + high, 1);
                    int midValue = EvalPoly(polynomial, mid, half);
                    if ((lowValue <= 0 && midValue >= 0) || (lowValue >= 0 && midValue <= 0))
                    {
                        high = mid;
                        highValue = midValue;
                    }
                    else
                    {
                        low = mid;
                        lowValue = midValue;
                        fraction = SilkEncoderMath.AddRshift(fraction, 128, m);
                    }
                }

                if (SilkEncoderMath.Abs(lowValue) < 65536)
                {
                    int denominator = lowValue - highValue;
                    int numerator = (lowValue << (8 - BinDivSteps)) + (denominator >> 1);
                    if (denominator != 0)
                    {
                        fraction += numerator / denominator;
                    }
                }
                else
                {
                    fraction += lowValue / ((lowValue - highValue) >> (8 - BinDivSteps));
                }

                nlsfQ15[rootIndex] = (short)Math.Min((k << 8) + fraction, SilkEncoderMath.Int16Max);
                rootIndex++;
                if (rootIndex >= order)
                {
                    return;
                }

                polynomial = (rootIndex & 1) == 0 ? p : q;
                low = SilkSynthesisTables.LsfCosQ12[k - 1];
                lowValue = (1 - (rootIndex & 2)) << 12;
            }
            else
            {
                k++;
                low = high;
                lowValue = highValue;
                threshold = 0;

                if (k > SilkNlsf.CosTableSize)
                {
                    expansions++;
                    if (expansions > MaxIterations)
                    {
                        nlsfQ15[0] = (short)((1 << 15) / (order + 1));
                        for (int i = 1; i < order; i++)
                        {
                            nlsfQ15[i] = (short)SilkEncoderMath.Smulbb(i + 1, nlsfQ15[0]);
                        }

                        return;
                    }

                    SilkEncoderMath.Bwexpander32(
                        coefficientsQ16, order, 65536 - SilkEncoderMath.Smulbb(10 + expansions, expansions));
                    A2NlsfInit(coefficientsQ16, p, q, half);
                    polynomial = p;
                    low = SilkSynthesisTables.LsfCosQ12[0];
                    lowValue = EvalPoly(polynomial, low, half);
                    if (lowValue < 0)
                    {
                        nlsfQ15[0] = 0;
                        polynomial = q;
                        lowValue = EvalPoly(polynomial, low, half);
                        rootIndex = 1;
                    }
                    else
                    {
                        rootIndex = 0;
                    }

                    k = 1;
                }
            }
        }
    }

    /// <summary>
    /// Quantizes an NLSF vector and reconstructs the quantized values
    /// (Appendix A, <c>silk_NLSF_encode</c>).
    /// </summary>
    /// <param name="codebook">The NLSF codebook for the bandwidth.</param>
    /// <param name="nlsfQ15">On input the target NLSFs; on output the reconstructed quantized NLSFs.</param>
    /// <param name="weightsQw">The Laroia weights for the target vector.</param>
    /// <param name="muQ20">Rate weight for the rate-distortion decision.</param>
    /// <param name="survivors">Maximum survivors after stage one.</param>
    /// <param name="signalType">Signal type: 0 inactive, 1 unvoiced, 2 voiced.</param>
    /// <param name="indices">Receives <c>order + 1</c> indices: stage one, then the stage-two residuals.</param>
    /// <returns>The rate-distortion cost, Q25.</returns>
    internal static int Encode(
        SilkNlsfCodebook codebook,
        Span<short> nlsfQ15,
        ReadOnlySpan<short> weightsQw,
        int muQ20,
        int survivors,
        int signalType,
        Span<int> indices)
    {
        int order = codebook.Order;
        SilkNlsf.Stabilize(
            nlsfQ15,
            codebook.Order == 16 ? SilkSynthesisTables.NlsfDeltaMinWbQ15 : SilkSynthesisTables.NlsfDeltaMinNbMbQ15,
            order);

        Span<int> errors = stackalloc int[SilkEncoderMath.NlsfVectors];
        Span<int> sortedIndices = stackalloc int[SilkEncoderMath.NlsfMaxSurvivors];
        Span<int> costs = stackalloc int[SilkEncoderMath.NlsfMaxSurvivors];
        Span<int> survivorStages = stackalloc int[SilkEncoderMath.NlsfMaxSurvivors * SilkEncoderMath.MaxLpcOrder];
        Span<short> residualQ15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> residualQ10 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> codebookNlsfQ15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> tempWeights = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> adjustedWeightsQ5 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<int> predictionQ8 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> entropyIndex = stackalloc int[SilkEncoderMath.MaxLpcOrder];

        ComputeVectorQuantization(errors, nlsfQ15, codebook.Cb1, SilkEncoderMath.NlsfVectors, order);
        SilkEncoderMath.InsertionSortIncreasing(errors, sortedIndices, SilkEncoderMath.NlsfVectors, survivors);

        for (int s = 0; s < survivors; s++)
        {
            int stage1Index = sortedIndices[s];
            int codebookOffset = stage1Index * order;
            for (int i = 0; i < order; i++)
            {
                codebookNlsfQ15[i] = (short)(codebook.Cb1[codebookOffset + i] << 7);
                residualQ15[i] = (short)(nlsfQ15[i] - codebookNlsfQ15[i]);
            }

            ComputeWeights(tempWeights, codebookNlsfQ15, order);

            for (int i = 0; i < order; i++)
            {
                int weightQ9 = SilkFixedPoint.SqrtApprox(tempWeights[i] << (18 - SilkEncoderMath.NlsfWeightQ));
                residualQ10[i] = (short)(SilkEncoderMath.Smulbb(residualQ15[i], weightQ9) >> 14);
            }

            for (int i = 0; i < order; i++)
            {
                adjustedWeightsQ5[i] = (short)((weightsQw[i] << 5) / tempWeights[i]);
            }

            Unpack(predictionQ8, entropyIndex, codebook, stage1Index);

            costs[s] = DelayedDecisionQuantize(
                survivorStages.Slice(s * SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder),
                residualQ10,
                adjustedWeightsQ5,
                predictionQ8,
                entropyIndex,
                codebook.Stage2RatesQ5,
                codebook.QuantStepSizeQ16,
                codebook.InvQuantStepSizeQ6,
                muQ20,
                order);

            int probabilityQ8 = codebook.Stage1Pdf(signalType)[stage1Index];
            int bitsQ7 = (8 << 7) - SilkEncoderMath.Lin2Log(probabilityQ8);
            costs[s] = SilkEncoderMath.Smlabb(costs[s], bitsQ7, muQ20 >> 2);
        }

        Span<int> bestIndex = stackalloc int[1];
        SilkEncoderMath.InsertionSortIncreasing(costs, bestIndex, survivors, 1);
        int best = bestIndex[0];

        indices[0] = sortedIndices[best];
        survivorStages.Slice(best * SilkEncoderMath.MaxLpcOrder, order).CopyTo(indices[1..]);

        int bandwidthIndex = order == 16 ? 2 : 0;
        SilkNlsf.Decode(bandwidthIndex, indices[0], indices[1..(order + 1)], nlsfQ15);

        return costs[0];
    }

    /// <summary>Computes the Laroia weights (Appendix A, <c>silk_NLSF_VQ_weights_laroia</c>).</summary>
    internal static void ComputeWeights(Span<short> weights, ReadOnlySpan<short> nlsfQ15, int order)
    {
        int tmp1 = (1 << (15 + SilkEncoderMath.NlsfWeightQ)) / Math.Max((int)nlsfQ15[0], 1);
        int tmp2 = (1 << (15 + SilkEncoderMath.NlsfWeightQ)) / Math.Max(nlsfQ15[1] - nlsfQ15[0], 1);
        weights[0] = (short)Math.Min(tmp1 + tmp2, SilkEncoderMath.Int16Max);

        for (int k = 1; k < order - 1; k += 2)
        {
            tmp1 = (1 << (15 + SilkEncoderMath.NlsfWeightQ)) / Math.Max(nlsfQ15[k + 1] - nlsfQ15[k], 1);
            weights[k] = (short)Math.Min(tmp1 + tmp2, SilkEncoderMath.Int16Max);
            tmp2 = (1 << (15 + SilkEncoderMath.NlsfWeightQ)) / Math.Max(nlsfQ15[k + 2] - nlsfQ15[k + 1], 1);
            weights[k + 1] = (short)Math.Min(tmp1 + tmp2, SilkEncoderMath.Int16Max);
        }

        tmp1 = (1 << (15 + SilkEncoderMath.NlsfWeightQ)) / Math.Max((1 << 15) - (int)nlsfQ15[order - 1], 1);
        weights[order - 1] = (short)Math.Min(tmp1 + tmp2, SilkEncoderMath.Int16Max);
    }

    /// <summary>Limits, stabilizes and quantizes an NLSF vector (Appendix A, <c>silk_process_NLSFs</c>).</summary>
    /// <param name="encoder">Encoder state.</param>
    /// <param name="prediction0Q12">Receives the LPC coefficients of the first half-frame.</param>
    /// <param name="prediction1Q12">Receives the LPC coefficients of the second half-frame.</param>
    /// <param name="nlsfQ15">On input the target NLSFs, on output the quantized NLSFs.</param>
    internal static void ProcessNlsfs(
        SilkEncoderState encoder,
        Span<short> prediction0Q12,
        Span<short> prediction1Q12,
        Span<short> nlsfQ15)
    {
        SilkNlsfCodebook codebook = encoder.PredictLpcOrder == 16 ? SilkEncoderTables.NlsfWb : SilkEncoderTables.NlsfNbMb;
        int order = codebook.Order;

        int muQ20 = SilkFixedPoint.Smlawb(3146, -268434, encoder.SpeechActivityQ8); // 0.003 Q20, -0.001 Q28
        if (encoder.SubframeCount == 2)
        {
            muQ20 = SilkEncoderMath.AddRshift(muQ20, muQ20, 1);
        }

        Span<short> weightsQw = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        ComputeWeights(weightsQw, nlsfQ15, order);

        bool interpolate = encoder.UseInterpolatedNlsfs == 1 && encoder.Indices.NlsfInterpCoefQ2 < 4;
        Span<short> interpolatedQ15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> interpolatedWeights = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        if (interpolate)
        {
            SilkEncoderMath.Interpolate(
                interpolatedQ15, encoder.PrevNlsfQ15, nlsfQ15, encoder.Indices.NlsfInterpCoefQ2, order);
            ComputeWeights(interpolatedWeights, interpolatedQ15, order);
            int factorSquaredQ15 = SilkEncoderMath.Smulbb(
                encoder.Indices.NlsfInterpCoefQ2, encoder.Indices.NlsfInterpCoefQ2) << 11;
            for (int i = 0; i < order; i++)
            {
                weightsQw[i] = (short)SilkFixedPoint.Smlawb(weightsQw[i] >> 1, interpolatedWeights[i], factorSquaredQ15);
            }
        }

        Span<int> indices = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];
        indices.Clear();
        Encode(codebook, nlsfQ15, weightsQw, muQ20, encoder.NlsfMsvqSurvivors, encoder.Indices.SignalType, indices);
        for (int i = 0; i < order + 1; i++)
        {
            encoder.Indices.NlsfIndices[i] = indices[i];
        }

        SilkNlsf.ConvertToLpc(prediction1Q12, nlsfQ15, order);

        if (interpolate)
        {
            SilkEncoderMath.Interpolate(
                interpolatedQ15, encoder.PrevNlsfQ15, nlsfQ15, encoder.Indices.NlsfInterpCoefQ2, order);
            SilkNlsf.ConvertToLpc(prediction0Q12, interpolatedQ15, order);
        }
        else
        {
            prediction1Q12[..order].CopyTo(prediction0Q12);
        }
    }

    private static void ComputeVectorQuantization(
        Span<int> errors, ReadOnlySpan<short> inputQ15, ReadOnlySpan<byte> codebook, int count, int order)
    {
        for (int i = 0; i < count; i++)
        {
            int sum = 0;
            for (int m = 0; m < order; m += 2)
            {
                int diff = inputQ15[m] - (codebook[(i * order) + m] << 7);
                sum = unchecked(sum + ((diff * diff) >> 4));
                diff = inputQ15[m + 1] - (codebook[(i * order) + m + 1] << 7);
                sum = unchecked(sum + ((diff * diff) >> 4));
            }

            errors[i] = sum;
        }
    }

    private static void Unpack(
        Span<int> predictionQ8, Span<int> entropyIndex, SilkNlsfCodebook codebook, int stage1Index)
    {
        int order = codebook.Order;
        ReadOnlySpan<byte> prediction = codebook.PredQ8.Span;
        string predSelect = codebook.PredSelect[stage1Index];
        string stage2Select = codebook.Stage2Select[stage1Index];

        for (int i = 0; i < order; i++)
        {
            if (i < order - 1)
            {
                predictionQ8[i] = prediction[i + (predSelect[i] == 'B' || predSelect[i] == 'D' ? order - 1 : 0)];
            }
            else
            {
                predictionQ8[i] = 0;
            }

            if (i < order)
            {
                char letter = stage2Select[i];
                int row = codebook.Order == 16 ? letter - 'i' : letter - 'a';
                entropyIndex[i] = row * ((2 * SilkEncoderMath.NlsfQuantMaxAmplitude) + 1);
            }
        }
    }

    private static int DelayedDecisionQuantize(
        Span<int> indices,
        ReadOnlySpan<short> inputQ10,
        ReadOnlySpan<short> weightsQ5,
        ReadOnlySpan<int> predictionQ8,
        ReadOnlySpan<int> entropyIndex,
        ReadOnlySpan<byte> ratesQ5Table,
        int quantStepSizeQ16,
        int invQuantStepSizeQ6,
        int muQ20,
        int order)
    {
        Span<int> stateIndices = stackalloc int[DelayedDecisionStates * SilkEncoderMath.MaxLpcOrder];
        Span<short> previousOutputQ10 = stackalloc short[2 * DelayedDecisionStates];
        Span<int> rateDistortionQ25 = stackalloc int[2 * DelayedDecisionStates];
        Span<int> minimumQ25 = stackalloc int[DelayedDecisionStates];
        Span<int> maximumQ25 = stackalloc int[DelayedDecisionStates];
        Span<int> sortIndex = stackalloc int[DelayedDecisionStates];
        int states = 1;
        rateDistortionQ25[0] = 0;
        previousOutputQ10[0] = 0;

        for (int i = order - 1; ; i--)
        {
            ReadOnlySpan<byte> ratesQ5 = ratesQ5Table[entropyIndex[i]..];
            int predictionCoefficientQ16 = predictionQ8[i] << 8;
            int input10 = inputQ10[i];

            for (int j = 0; j < states; j++)
            {
                int predictionQ10 = SilkFixedPoint.Smulwb(predictionCoefficientQ16, previousOutputQ10[j]);
                int residualQ10 = unchecked((short)(input10 - predictionQ10));
                int residualIndex = SilkFixedPoint.Smulwb(invQuantStepSizeQ6, residualQ10);
                residualIndex = SilkEncoderMath.Limit(
                    residualIndex, -SilkEncoderMath.NlsfQuantMaxAmplitudeExt, SilkEncoderMath.NlsfQuantMaxAmplitudeExt - 1);
                stateIndices[(j * SilkEncoderMath.MaxLpcOrder) + i] = residualIndex;

                int output0Q10 = residualIndex << 10;
                int output1Q10 = unchecked((short)(output0Q10 + 1024));
                if (residualIndex > 0)
                {
                    output0Q10 = unchecked((short)(output0Q10 - SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                    output1Q10 = unchecked((short)(output1Q10 - SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                }
                else if (residualIndex == 0)
                {
                    output1Q10 = unchecked((short)(output1Q10 - SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                }
                else if (residualIndex == -1)
                {
                    output0Q10 = unchecked((short)(output0Q10 + SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                }
                else
                {
                    output0Q10 = unchecked((short)(output0Q10 + SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                    output1Q10 = unchecked((short)(output1Q10 + SilkEncoderMath.NlsfQuantLevelAdjustQ10));
                }

                output0Q10 = SilkFixedPoint.Smulwb(output0Q10, quantStepSizeQ16);
                output1Q10 = SilkFixedPoint.Smulwb(output1Q10, quantStepSizeQ16);
                output0Q10 = unchecked((short)(output0Q10 + predictionQ10));
                output1Q10 = unchecked((short)(output1Q10 + predictionQ10));
                previousOutputQ10[j] = (short)output0Q10;
                previousOutputQ10[j + states] = (short)output1Q10;

                int rate0Q5;
                int rate1Q5;
                if (residualIndex + 1 >= SilkEncoderMath.NlsfQuantMaxAmplitude)
                {
                    if (residualIndex + 1 == SilkEncoderMath.NlsfQuantMaxAmplitude)
                    {
                        rate0Q5 = ratesQ5[residualIndex + SilkEncoderMath.NlsfQuantMaxAmplitude];
                        rate1Q5 = 280;
                    }
                    else
                    {
                        rate0Q5 = SilkEncoderMath.Smlabb(
                            280 - (43 * SilkEncoderMath.NlsfQuantMaxAmplitude), 43, residualIndex);
                        rate1Q5 = unchecked((short)(rate0Q5 + 43));
                    }
                }
                else if (residualIndex <= -SilkEncoderMath.NlsfQuantMaxAmplitude)
                {
                    if (residualIndex == -SilkEncoderMath.NlsfQuantMaxAmplitude)
                    {
                        rate0Q5 = 280;
                        rate1Q5 = ratesQ5[residualIndex + 1 + SilkEncoderMath.NlsfQuantMaxAmplitude];
                    }
                    else
                    {
                        rate0Q5 = SilkEncoderMath.Smlabb(
                            280 - (43 * SilkEncoderMath.NlsfQuantMaxAmplitude), -43, residualIndex);
                        rate1Q5 = unchecked((short)(rate0Q5 - 43));
                    }
                }
                else
                {
                    rate0Q5 = ratesQ5[residualIndex + SilkEncoderMath.NlsfQuantMaxAmplitude];
                    rate1Q5 = ratesQ5[residualIndex + 1 + SilkEncoderMath.NlsfQuantMaxAmplitude];
                }

                int rateDistortion = rateDistortionQ25[j];
                int difference = unchecked((short)(input10 - output0Q10));
                rateDistortionQ25[j] = SilkEncoderMath.Smlabb(
                    SilkEncoderMath.Mla(rateDistortion, SilkEncoderMath.Smulbb(difference, difference), weightsQ5[i]),
                    muQ20, rate0Q5);
                difference = unchecked((short)(input10 - output1Q10));
                rateDistortionQ25[j + states] = SilkEncoderMath.Smlabb(
                    SilkEncoderMath.Mla(rateDistortion, SilkEncoderMath.Smulbb(difference, difference), weightsQ5[i]),
                    muQ20, rate1Q5);
            }

            if (states < DelayedDecisionStates)
            {
                for (int j = 0; j < states; j++)
                {
                    stateIndices[((j + states) * SilkEncoderMath.MaxLpcOrder) + i] =
                        stateIndices[(j * SilkEncoderMath.MaxLpcOrder) + i] + 1;
                }

                states <<= 1;
                for (int j = states; j < DelayedDecisionStates; j++)
                {
                    stateIndices.Slice((j - states) * SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder)
                        .CopyTo(stateIndices.Slice(j * SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder));
                }
            }
            else if (i > 0)
            {
                for (int j = 0; j < DelayedDecisionStates; j++)
                {
                    if (rateDistortionQ25[j] > rateDistortionQ25[j + DelayedDecisionStates])
                    {
                        maximumQ25[j] = rateDistortionQ25[j];
                        minimumQ25[j] = rateDistortionQ25[j + DelayedDecisionStates];
                        rateDistortionQ25[j] = minimumQ25[j];
                        rateDistortionQ25[j + DelayedDecisionStates] = maximumQ25[j];
                        (previousOutputQ10[j], previousOutputQ10[j + DelayedDecisionStates]) =
                            (previousOutputQ10[j + DelayedDecisionStates], previousOutputQ10[j]);
                        sortIndex[j] = j + DelayedDecisionStates;
                    }
                    else
                    {
                        minimumQ25[j] = rateDistortionQ25[j];
                        maximumQ25[j] = rateDistortionQ25[j + DelayedDecisionStates];
                        sortIndex[j] = j;
                    }
                }

                while (true)
                {
                    int minimumMaximum = SilkEncoderMath.Int32Max;
                    int maximumMinimum = 0;
                    int indexMinimumMaximum = 0;
                    int indexMaximumMinimum = 0;
                    for (int j = 0; j < DelayedDecisionStates; j++)
                    {
                        if (minimumMaximum > maximumQ25[j])
                        {
                            minimumMaximum = maximumQ25[j];
                            indexMinimumMaximum = j;
                        }

                        if (maximumMinimum < minimumQ25[j])
                        {
                            maximumMinimum = minimumQ25[j];
                            indexMaximumMinimum = j;
                        }
                    }

                    if (minimumMaximum >= maximumMinimum)
                    {
                        break;
                    }

                    sortIndex[indexMaximumMinimum] = sortIndex[indexMinimumMaximum] ^ DelayedDecisionStates;
                    rateDistortionQ25[indexMaximumMinimum] =
                        rateDistortionQ25[indexMinimumMaximum + DelayedDecisionStates];
                    previousOutputQ10[indexMaximumMinimum] =
                        previousOutputQ10[indexMinimumMaximum + DelayedDecisionStates];
                    minimumQ25[indexMaximumMinimum] = 0;
                    maximumQ25[indexMinimumMaximum] = SilkEncoderMath.Int32Max;
                    stateIndices.Slice(indexMinimumMaximum * SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder)
                        .CopyTo(stateIndices.Slice(indexMaximumMinimum * SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder));
                }

                for (int j = 0; j < DelayedDecisionStates; j++)
                {
                    stateIndices[(j * SilkEncoderMath.MaxLpcOrder) + i] += sortIndex[j] >> DelayedDecisionStatesLog2;
                }
            }
            else
            {
                break;
            }
        }

        int bestState = 0;
        int bestCost = SilkEncoderMath.Int32Max;
        for (int j = 0; j < 2 * DelayedDecisionStates; j++)
        {
            if (bestCost > rateDistortionQ25[j])
            {
                bestCost = rateDistortionQ25[j];
                bestState = j;
            }
        }

        int winner = bestState & (DelayedDecisionStates - 1);
        for (int j = 0; j < order; j++)
        {
            indices[j] = stateIndices[(winner * SilkEncoderMath.MaxLpcOrder) + j];
        }

        indices[0] += bestState >> DelayedDecisionStatesLog2;
        return bestCost;
    }

    private static void A2NlsfInit(Span<int> coefficientsQ16, Span<int> p, Span<int> q, int half)
    {
        p[half] = 1 << 16;
        q[half] = 1 << 16;
        for (int k = 0; k < half; k++)
        {
            p[k] = -coefficientsQ16[half - k - 1] - coefficientsQ16[half + k];
            q[k] = -coefficientsQ16[half - k - 1] + coefficientsQ16[half + k];
        }

        for (int k = half; k > 0; k--)
        {
            p[k - 1] -= p[k];
            q[k - 1] += q[k];
        }

        A2NlsfTransform(p, half);
        A2NlsfTransform(q, half);
    }

    private static void A2NlsfTransform(Span<int> p, int half)
    {
        for (int k = 2; k <= half; k++)
        {
            for (int n = half; n > k; n--)
            {
                p[n - 2] -= p[n];
            }

            p[k - 2] -= p[k] << 1;
        }
    }

    private static int EvalPoly(ReadOnlySpan<int> p, int x, int half)
    {
        int result = p[half];
        int xQ16 = x << 4;
        for (int n = half - 1; n >= 0; n--)
        {
            result = SilkFixedPoint.Smlaww(p[n], result, xQ16);
        }

        return result;
    }
}
