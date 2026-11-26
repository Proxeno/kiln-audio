// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Pitch analysis: the whitening LPC, the three-stage lag search and the LTP correlation
/// (RFC 6716 §5.2.3.2; Appendix A, <c>silk/fixed/find_pitch_lags_FIX.c</c> and
/// <c>silk/fixed/pitch_analysis_core_FIX.c</c>).
/// </summary>
/// <remarks>
/// The analysis runs on the LPC residual. It decimates to 8 kHz and 4 kHz with the same
/// linear-phase polyphase resampler as the input stage (the reference's own resampler is
/// non-normative), runs a coarse 4 kHz lag search, refines it at 8 kHz, and searches the
/// five-tap contour codebooks at the internal rate. The pitch lags it produces are physical
/// lags, so they are valid for the LTP stage at the internal rate.
/// </remarks>
internal sealed class SilkPitchAnalysis
{
    private const int DSearchLength = 24;
    private const int Stage3Lags = 5;
    private const int Stage2CodebookExt = 11;
    private const int Stage3CodebookMax = 34;
    private const int Stage3Codebook10Ms = 12;
    private const int Stage2Codebook10Ms = 3;

    private const int ShortLagBiasQ15 = 6554;   // round(0.2 * 2^15)
    private const int PrevLagBiasQ15 = 6554;    // round(0.2 * 2^15)
    private const int FlatContourBiasQ20 = 52429; // round(0.05 * 2^20)

    private readonly int _fsKhz;
    private readonly int _frameLength;
    private readonly int _frameLength8Khz;
    private readonly int _frameLength4Khz;
    private readonly int _subframeLength;
    private readonly int _subframeLength8Khz;
    private readonly int _subframeLength4Khz;
    private readonly int _minLag;
    private readonly int _minLag8Khz;
    private readonly int _minLag4Khz;
    private readonly int _maxLag;
    private readonly int _maxLag8Khz;
    private readonly int _maxLag4Khz;

    private readonly SilkInputResampler _to8Khz;
    private readonly SilkInputResampler _to4Khz;

    /// <summary>Creates the analysis for a fixed internal rate and frame size.</summary>
    internal SilkPitchAnalysis(int fsKhz, int subframeCount)
    {
        _fsKhz = fsKhz;
        _frameLength = (20 + (subframeCount * 5)) * fsKhz;
        _frameLength8Khz = (20 + (subframeCount * 5)) * 8;
        _frameLength4Khz = (20 + (subframeCount * 5)) * 4;
        _subframeLength = 5 * fsKhz;
        _subframeLength8Khz = 40;
        _subframeLength4Khz = 20;
        _minLag = 2 * fsKhz;
        _minLag8Khz = 16;
        _minLag4Khz = 8;
        _maxLag = (18 * fsKhz) - 1;
        _maxLag8Khz = 143;
        _maxLag4Khz = 72;

        _to8Khz = fsKhz == 8 ? null! : new SilkInputResampler(fsKhz * 1000, 8000);
        _to4Khz = new SilkInputResampler(8000, 4000);
    }

    /// <summary>
    /// Finds the pitch lags and sets the frame's signal type (Appendix A, <c>silk_find_pitch_lags_FIX</c>).
    /// </summary>
    /// <param name="encoder">Encoder state.</param>
    /// <param name="control">Receives the per-subframe pitch lags and the pitch prediction gain.</param>
    /// <param name="residual">Receives the LPC residual; must hold <c>la_pitch + frame_length + ltp_mem_length</c>.</param>
    /// <param name="xBuffer">The encoder's x buffer; the frame starts at <paramref name="frameOffset"/>.</param>
    /// <param name="frameOffset">Offset of the frame in <paramref name="xBuffer"/> (the LTP memory length).</param>
    internal void FindPitchLags(
        SilkEncoderState encoder,
        SilkEncoderControl control,
        Span<short> residual,
        ReadOnlySpan<short> xBuffer,
        int frameOffset)
    {
        int bufferLength = encoder.LaPitch + encoder.FrameLength + encoder.LtpMemLength;
        int windowLength = encoder.PitchLpcWinLength;
        int windowStart = bufferLength - windowLength;
        ReadOnlySpan<short> xBufferView = xBuffer[..bufferLength];

        Span<short> windowed = stackalloc short[SilkEncoderMath.LaShapeMax > 0 ? 352 : 352];
        SilkEncoderMath.ApplySineWindow(windowed, xBufferView[windowStart..], 1, encoder.LaPitch);
        xBufferView.Slice(windowStart + encoder.LaPitch, windowLength - (2 * encoder.LaPitch))
            .CopyTo(windowed[encoder.LaPitch..]);
        SilkEncoderMath.ApplySineWindow(
            windowed[(windowLength - encoder.LaPitch)..],
            xBufferView[(windowStart + windowLength - encoder.LaPitch)..],
            2,
            encoder.LaPitch);

        Span<int> autocorrelation = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];
        Span<short> reflectionQ15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<int> coefficientsQ24 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<short> coefficientsQ12 = stackalloc short[SilkEncoderMath.MaxLpcOrder];

        SilkEncoderMath.Autocorr(out _, autocorrelation, windowed[..windowLength], windowLength, encoder.PitchEstimationLpcOrder + 1);

        autocorrelation[0] = SilkFixedPoint.Smlawb(autocorrelation[0], autocorrelation[0], 66) + 1;

        int residualEnergy = SilkEncoderMath.Schur(
            reflectionQ15, autocorrelation[..(encoder.PitchEstimationLpcOrder + 1)], encoder.PitchEstimationLpcOrder);

        control.PredGainQ16 = SilkFixedPoint.Div32VarQ(
            autocorrelation[0], Math.Max(residualEnergy, 1), 16);

        SilkEncoderMath.K2a(coefficientsQ24, reflectionQ15, encoder.PitchEstimationLpcOrder);
        for (int i = 0; i < encoder.PitchEstimationLpcOrder; i++)
        {
            coefficientsQ12[i] = (short)SilkEncoderMath.Sat16(coefficientsQ24[i] >> 12);
        }

        SilkEncoderMath.Bwexpander(coefficientsQ12, encoder.PitchEstimationLpcOrder, 64881);
        SilkEncoderMath.LpcAnalysisFilter(
            residual, xBufferView, coefficientsQ12, bufferLength, encoder.PitchEstimationLpcOrder);

        if (encoder.Indices.SignalType != 0 && encoder.FirstFrameAfterReset == 0)
        {
            int thresholdQ15 = 19661;
            thresholdQ15 = SilkEncoderMath.Smlabb(thresholdQ15, -130, encoder.PitchEstimationLpcOrder);
            thresholdQ15 = SilkEncoderMath.Smlabb(thresholdQ15, -12, encoder.SpeechActivityQ8);
            thresholdQ15 = SilkEncoderMath.Smlabb(thresholdQ15, -4914, encoder.PrevSignalType >> 1);
            thresholdQ15 = SilkFixedPoint.Smlawb(thresholdQ15, -6553, encoder.InputTiltQ15);
            thresholdQ15 = SilkEncoderMath.Sat16(thresholdQ15);

            int voiced = PitchAnalysisCore(
                residual[.._frameLength],
                control.PitchL,
                out int lagIndex,
                out int contourIndex,
                ref encoder.LtpCorrQ15,
                encoder.PrevLag,
                encoder.PitchEstimationThresholdQ16,
                thresholdQ15,
                encoder.PitchEstimationComplexity,
                encoder.SubframeCount);

            encoder.Indices.SignalType = voiced == 0 ? 2 : 1;
            if (voiced == 0)
            {
                encoder.Indices.LagIndex = lagIndex;
                encoder.Indices.ContourIndex = contourIndex;
            }
            else
            {
                encoder.Indices.LagIndex = 0;
                encoder.Indices.ContourIndex = 0;
            }
        }
        else
        {
            control.PitchL.AsSpan().Clear();
            encoder.Indices.LagIndex = 0;
            encoder.Indices.ContourIndex = 0;
            encoder.LtpCorrQ15 = 0;
        }
    }

    private int PitchAnalysisCore(
        ReadOnlySpan<short> frame,
        Span<int> pitchOut,
        out int lagIndex,
        out int contourIndex,
        ref int ltpCorrQ15,
        int prevLag,
        int searchThreshold1Q16,
        int searchThreshold2Q15,
        int complexity,
        int subframeCount)
    {
        int frameLength = _frameLength;
        int frameLength8Khz = _frameLength8Khz;
        int frameLength4Khz = _frameLength4Khz;
        int subframeLength = _subframeLength;
        int subframeLength8Khz = _subframeLength8Khz;
        int subframeLength4Khz = _subframeLength4Khz;
        int minLag = _minLag;
        int minLag8Khz = _minLag8Khz;
        int minLag4Khz = _minLag4Khz;
        int maxLag = _maxLag;
        int maxLag8Khz = _maxLag8Khz;
        int maxLag4Khz = _maxLag4Khz;

        Span<short> frame8Khz = stackalloc short[320];
        Span<short> frame4Khz = stackalloc short[160];
        Span<short> c = stackalloc short[4 * 160];
        Span<short> compressed = stackalloc short[160];
        Span<int> searchLags = stackalloc int[DSearchLength];
        Span<int> cc = stackalloc int[Stage2CodebookExt];
        Span<int> stage3Energy = stackalloc int[4 * Stage3CodebookMax * Stage3Lags];
        Span<int> stage3Correlation = stackalloc int[4 * Stage3CodebookMax * Stage3Lags];
        Span<short> scaledFrame = stackalloc short[2 * SilkEncoderMath.MaxFrameLength];
        Span<int> scratch = stackalloc int[22];

        // Resample the residual to 8 kHz and then to 4 kHz.
        if (_fsKhz == 8)
        {
            frame[..frameLength8Khz].CopyTo(frame8Khz);
        }
        else
        {
            _to8Khz.Reset();
            _to8Khz.Process(frame[..frameLength], frame8Khz);
        }

        _to4Khz.Reset();
        _to4Khz.Process(frame8Khz[..frameLength8Khz], frame4Khz);

        for (int i = frameLength4Khz - 1; i > 0; i--)
        {
            frame4Khz[i] = (short)SilkEncoderMath.AddSat16(frame4Khz[i], frame4Khz[i - 1]);
        }

        int maxSumSquaredLength = Math.Max(subframeLength8Khz, subframeLength4Khz << 2);
        int shift = FindScaling(frame4Khz[..frameLength4Khz], maxSumSquaredLength);
        if (shift > 0)
        {
            for (int i = 0; i < frameLength4Khz; i++)
            {
                frame4Khz[i] >>= shift;
            }
        }

        // First stage: 4 kHz lag search.
        int targetIndex = subframeLength4Khz << 2;
        int halfSubframes = subframeCount >> 1;
        for (int k = 0; k < halfSubframes; k++)
        {
            int basisIndex = targetIndex - minLag4Khz;
            int crossCorrelation = SilkEncoderMath.InnerProd(
                frame4Khz[targetIndex..], frame4Khz[basisIndex..], subframeLength8Khz);
            int normalizer = SilkEncoderMath.AddSat32(
                SilkEncoderMath.InnerProd(frame4Khz[basisIndex..], frame4Khz[basisIndex..], subframeLength8Khz),
                subframeLength8Khz * 4000);

            int value = crossCorrelation / (SilkFixedPoint.SqrtApprox(normalizer) + 1);
            c[(k * 160) + minLag4Khz] = (short)SilkEncoderMath.Sat16(value);

            for (int d = minLag4Khz + 1; d <= maxLag4Khz; d++)
            {
                basisIndex--;
                crossCorrelation = SilkEncoderMath.InnerProd(
                    frame4Khz[targetIndex..], frame4Khz[basisIndex..], subframeLength8Khz);
                normalizer += (frame4Khz[basisIndex] * frame4Khz[basisIndex])
                    - (frame4Khz[basisIndex + subframeLength8Khz] * frame4Khz[basisIndex + subframeLength8Khz]);
                value = crossCorrelation / (SilkFixedPoint.SqrtApprox(normalizer) + 1);
                c[(k * 160) + d] = (short)SilkEncoderMath.Sat16(value);
            }

            targetIndex += subframeLength8Khz;
        }

        if (subframeCount == 4)
        {
            for (int i = maxLag4Khz; i >= minLag4Khz; i--)
            {
                int sum = (c[i] + c[160 + i]) >> 1;
                sum = SilkFixedPoint.Smlawb(sum, sum, -i << 4);
                c[i] = (short)sum;
            }
        }
        else
        {
            for (int i = maxLag4Khz; i >= minLag4Khz; i--)
            {
                int sum = c[i];
                sum = SilkFixedPoint.Smlawb(sum, sum, -i << 4);
                c[i] = (short)sum;
            }
        }

        int lengthDSearch = 4 + (complexity << 1);
        InsertionSortDecreasing(c[(0 * 160)..], minLag4Khz, maxLag4Khz - minLag4Khz + 1, lengthDSearch, searchLags);

        int escapeTarget = subframeLength4Khz * subframeCount;
        int energy = SilkEncoderMath.InnerProd(
            frame4Khz[escapeTarget..], frame4Khz[escapeTarget..], subframeLength4Khz << 2);
        energy = SilkEncoderMath.AddSat32(energy, 1000);
        int cmax = c[minLag4Khz];
        int threshold = cmax * cmax;
        if ((energy >> 6) > threshold)
        {
            pitchOut[..subframeCount].Clear();
            ltpCorrQ15 = 0;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        threshold = SilkFixedPoint.Smulwb(searchThreshold1Q16, cmax);
        for (int i = 0; i < lengthDSearch; i++)
        {
            if (c[minLag4Khz + i] > threshold)
            {
                searchLags[i] = (searchLags[i] + minLag4Khz) << 1;
            }
            else
            {
                lengthDSearch = i;
                break;
            }
        }

        for (int i = minLag8Khz - 5; i < maxLag8Khz + 5; i++)
        {
            compressed[i] = 0;
        }

        for (int i = 0; i < lengthDSearch; i++)
        {
            compressed[searchLags[i]] = 1;
        }

        for (int i = maxLag8Khz + 3; i >= minLag8Khz; i--)
        {
            compressed[i] = (short)(compressed[i] + compressed[i - 1] + compressed[i - 2]);
        }

        lengthDSearch = 0;
        for (int i = minLag8Khz; i < maxLag8Khz + 1; i++)
        {
            if (compressed[i + 1] > 0)
            {
                searchLags[lengthDSearch++] = i;
            }
        }

        for (int i = maxLag8Khz + 3; i >= minLag8Khz; i--)
        {
            compressed[i] = (short)(compressed[i] + compressed[i - 1] + compressed[i - 2] + compressed[i - 3]);
        }

        int lengthDComp = 0;
        for (int i = minLag8Khz; i < maxLag8Khz + 4; i++)
        {
            if (compressed[i] > 0)
            {
                compressed[lengthDComp++] = (short)(i - 2);
            }
        }

        shift = FindScaling(frame8Khz[..frameLength8Khz], subframeLength8Khz);
        if (shift > 0)
        {
            for (int i = 0; i < frameLength8Khz; i++)
            {
                frame8Khz[i] >>= shift;
            }
        }

        c.Clear();

        int target8 = 160;
        for (int k = 0; k < subframeCount; k++)
        {
            int energyTarget = SilkEncoderMath.InnerProd(
                frame8Khz[target8..], frame8Khz[target8..], subframeLength8Khz);
            for (int j = 0; j < lengthDComp; j++)
            {
                int d = compressed[j];
                int basis8 = target8 - d;
                int crossCorrelation = SilkEncoderMath.InnerProd(
                    frame8Khz[target8..], frame8Khz[basis8..], subframeLength8Khz);
                int energyBasis = SilkEncoderMath.InnerProd(
                    frame8Khz[basis8..], frame8Khz[basis8..], subframeLength8Khz);
                if (crossCorrelation > 0)
                {
                    int maximumEnergy = Math.Max(energyTarget, energyBasis);
                    int leadingZeros = SilkEncoderMath.Clz32(crossCorrelation);
                    int leftShift = SilkEncoderMath.Limit(leadingZeros - 1, 0, 15);
                    int value = (crossCorrelation << leftShift) / ((maximumEnergy >> (15 - leftShift)) + 1);
                    value = SilkFixedPoint.Smulwb(crossCorrelation, value);
                    value = SilkEncoderMath.AddSat32(value, value);
                    leadingZeros = SilkEncoderMath.Clz32(value);
                    leftShift = SilkEncoderMath.Limit(leadingZeros - 1, 0, 15);
                    int minimumEnergy = Math.Min(energyTarget, energyBasis);
                    c[(k * 160) + d] = (short)((value << leftShift) / ((minimumEnergy >> (15 - leftShift)) + 1));
                }
                else
                {
                    c[(k * 160) + d] = 0;
                }
            }

            target8 += subframeLength8Khz;
        }

        int ccMax = SilkEncoderMath.Int32Min;
        int ccMaxBiased = SilkEncoderMath.Int32Min;
        int codebookIndex = 0;
        int lag = -1;
        int prevLagLog2Q7 = 0;
        int previousLag = prevLag;
        if (previousLag > 0)
        {
            if (_fsKhz == 12)
            {
                previousLag = (previousLag << 1) / 3;
            }
            else if (_fsKhz == 16)
            {
                previousLag >>= 1;
            }

            prevLagLog2Q7 = SilkEncoderMath.Lin2Log(previousLag);
        }

        int codebookSearch;
        int correlationThresholdQ15;
        sbyte[][] lagCodebook;
        if (subframeCount == 4)
        {
            lagCodebook = SilkEncoderTables.PitchLagStage2;
            codebookSearch = _fsKhz == 8 && complexity > 0 ? Stage2CodebookExt : 3;
            correlationThresholdQ15 = (searchThreshold2Q15 * searchThreshold2Q15) >> 13;
        }
        else
        {
            lagCodebook = SilkEncoderTables.PitchLagStage2_10Ms;
            codebookSearch = Stage2Codebook10Ms;
            correlationThresholdQ15 = (searchThreshold2Q15 * searchThreshold2Q15) >> 14;
        }

        for (int k = 0; k < lengthDSearch; k++)
        {
            int d = searchLags[k];
            for (int j = 0; j < codebookSearch; j++)
            {
                cc[j] = 0;
                for (int i = 0; i < subframeCount; i++)
                {
                    cc[j] += c[(i * 160) + d + lagCodebook[i][j]];
                }
            }

            int ccMaxNew = SilkEncoderMath.Int32Min;
            int codebookIndexNew = 0;
            for (int i = 0; i < codebookSearch; i++)
            {
                if (cc[i] > ccMaxNew)
                {
                    ccMaxNew = cc[i];
                    codebookIndexNew = i;
                }
            }

            int lagLog2Q7 = SilkEncoderMath.Lin2Log(d);
            int ccMaxNewBiased = ccMaxNew - (((subframeCount * ShortLagBiasQ15) * lagLog2Q7) >> 7);

            if (previousLag > 0)
            {
                int deltaLagLog2 = lagLog2Q7 - prevLagLog2Q7;
                int deltaLagLog2SquaredQ7 = (deltaLagLog2 * deltaLagLog2) >> 7;
                int prevLagBiasQ15 = ((subframeCount * PrevLagBiasQ15) * ltpCorrQ15) >> 15;
                prevLagBiasQ15 = (prevLagBiasQ15 * deltaLagLog2SquaredQ7) / (deltaLagLog2SquaredQ7 + 64);
                ccMaxNewBiased -= prevLagBiasQ15;
            }

            if (ccMaxNewBiased > ccMaxBiased
                && ccMaxNew > correlationThresholdQ15
                && lagCodebook[0][codebookIndexNew] <= minLag8Khz)
            {
                ccMaxBiased = ccMaxNewBiased;
                ccMax = ccMaxNew;
                lag = d;
                codebookIndex = codebookIndexNew;
            }
        }

        if (lag == -1)
        {
            pitchOut[..subframeCount].Clear();
            ltpCorrQ15 = 0;
            lagIndex = 0;
            contourIndex = 0;
            return 1;
        }

        if (_fsKhz > 8)
        {
            shift = FindScaling(frame[..frameLength], subframeLength);
            Span<short> inputFrame = scaledFrame[..frameLength];
            if (shift > 0)
            {
                for (int i = 0; i < frameLength; i++)
                {
                    inputFrame[i] = (short)(frame[i] >> shift);
                }
            }
            else
            {
                frame[..frameLength].CopyTo(inputFrame);
            }

            int codebookIndexOld = codebookIndex;
            lag = _fsKhz == 12 ? (lag * 3) >> 1 : lag << 1;
            lag = SilkEncoderMath.Limit(lag, minLag, maxLag);
            int startLag = Math.Max(lag - 2, minLag);
            int endLag = Math.Min(lag + 2, maxLag);
            int lagNew = lag;
            codebookIndex = 0;
            ltpCorrQ15 = SilkFixedPoint.SqrtApprox(ccMax << 13);
            ccMax = SilkEncoderMath.Int32Min;

            for (int k = 0; k < subframeCount; k++)
            {
                pitchOut[k] = lag + (2 * SilkEncoderTables.PitchLagStage2[k][codebookIndexOld]);
            }

            CalcCorrelationStage3(stage3Correlation, inputFrame, startLag, subframeLength, subframeCount, complexity, scratch);
            CalcEnergyStage3(stage3Energy, inputFrame, startLag, subframeLength, subframeCount, complexity, scratch);

            int lagCounter = 0;
            int contourBiasQ20 = FlatContourBiasQ20 / lag;
            int nbCodebookSearch;
            sbyte[][] stage3Codebook;
            if (subframeCount == 4)
            {
                nbCodebookSearch = SilkEncoderTables.PitchStage3Candidates[complexity];
                stage3Codebook = SilkEncoderTables.PitchLagStage3;
            }
            else
            {
                nbCodebookSearch = Stage3Codebook10Ms;
                stage3Codebook = SilkEncoderTables.PitchLagStage3_10Ms;
            }

            for (int d = startLag; d <= endLag; d++)
            {
                for (int j = 0; j < nbCodebookSearch; j++)
                {
                    int crossCorrelation = 0;
                    int stage3EnergySum = 0;
                    for (int k = 0; k < subframeCount; k++)
                    {
                        stage3EnergySum += stage3Energy[(k * Stage3CodebookMax * Stage3Lags) + (j * Stage3Lags) + lagCounter] >> 2;
                        crossCorrelation += stage3Correlation[(k * Stage3CodebookMax * Stage3Lags) + (j * Stage3Lags) + lagCounter] >> 2;
                    }

                    int ccMaxNew;
                    if (crossCorrelation > 0)
                    {
                        int leadingZeros = SilkEncoderMath.Clz32(crossCorrelation);
                        int leftShift = SilkEncoderMath.Limit(leadingZeros - 1, 0, 13);
                        ccMaxNew = (crossCorrelation << leftShift) / ((stage3EnergySum >> (13 - leftShift)) + 1);
                        ccMaxNew = SilkEncoderMath.Sat16(ccMaxNew);
                        ccMaxNew = SilkFixedPoint.Smulwb(crossCorrelation, ccMaxNew);
                        ccMaxNew = ccMaxNew > SilkEncoderMath.Int32Max >> 3
                            ? SilkEncoderMath.Int32Max
                            : ccMaxNew << 3;
                        int diff = SilkEncoderMath.Int16Max - ((contourBiasQ20 * j) >> 5);
                        ccMaxNew = SilkFixedPoint.Smulwb(ccMaxNew, diff) << 1;
                    }
                    else
                    {
                        ccMaxNew = 0;
                    }

                    if (ccMaxNew > ccMax && (d + stage3Codebook[0][j]) <= maxLag)
                    {
                        ccMax = ccMaxNew;
                        lagNew = d;
                        codebookIndex = j;
                    }
                }

                lagCounter++;
            }

            for (int k = 0; k < subframeCount; k++)
            {
                pitchOut[k] = lagNew + stage3Codebook[k][codebookIndex];
                pitchOut[k] = SilkEncoderMath.Limit(pitchOut[k], minLag, 18 * _fsKhz);
            }

            lagIndex = lagNew - minLag;
            contourIndex = codebookIndex;
        }
        else
        {
            ccMax = Math.Max(ccMax, 0);
            ltpCorrQ15 = SilkFixedPoint.SqrtApprox(ccMax << 13);
            for (int k = 0; k < subframeCount; k++)
            {
                pitchOut[k] = lag + lagCodebook[k][codebookIndex];
                pitchOut[k] = SilkEncoderMath.Limit(pitchOut[k], minLag8Khz, 18 * _fsKhz);
            }

            lagIndex = lag - minLag8Khz;
            contourIndex = codebookIndex;
        }

        return 0;

        static void CalcCorrelationStage3(
            Span<int> output,
            ReadOnlySpan<short> frameIn,
            int startLag,
            int sfLength,
            int nbSubframes,
            int complexityLevel,
            Span<int> scratch)
        {
            sbyte[][] lagRange = nbSubframes == 4
                ? SilkEncoderTables.PitchLagRangeStage3[complexityLevel]
                : SilkEncoderTables.PitchLagRangeStage3_10Ms;
            sbyte[][] lagCodebookIn = nbSubframes == 4
                ? SilkEncoderTables.PitchLagStage3
                : SilkEncoderTables.PitchLagStage3_10Ms;
            int nbSearch = nbSubframes == 4
                ? SilkEncoderTables.PitchStage3Candidates[complexityLevel]
                : Stage3Codebook10Ms;

            int target = sfLength << 2;
            for (int k = 0; k < nbSubframes; k++)
            {
                int lagCounter = 0;
                int lagLow = lagRange[k][0];
                int lagHigh = lagRange[k][1];
                for (int j = lagLow; j <= lagHigh; j++)
                {
                    int basis = target - (startLag + j);
                    scratch[lagCounter++] = SilkEncoderMath.InnerProd(
                        frameIn[target..], frameIn[basis..], sfLength);
                }

                int delta = lagRange[k][0];
                for (int i = 0; i < nbSearch; i++)
                {
                    int index = lagCodebookIn[k][i] - delta;
                    for (int j = 0; j < Stage3Lags; j++)
                    {
                        output[(k * Stage3CodebookMax * Stage3Lags) + (i * Stage3Lags) + j] = scratch[index + j];
                    }
                }

                target += sfLength;
            }
        }

        static void CalcEnergyStage3(
            Span<int> output,
            ReadOnlySpan<short> frameIn,
            int startLag,
            int sfLength,
            int nbSubframes,
            int complexityLevel,
            Span<int> scratch)
        {
            sbyte[][] lagRange = nbSubframes == 4
                ? SilkEncoderTables.PitchLagRangeStage3[complexityLevel]
                : SilkEncoderTables.PitchLagRangeStage3_10Ms;
            sbyte[][] lagCodebookIn = nbSubframes == 4
                ? SilkEncoderTables.PitchLagStage3
                : SilkEncoderTables.PitchLagStage3_10Ms;
            int nbSearch = nbSubframes == 4
                ? SilkEncoderTables.PitchStage3Candidates[complexityLevel]
                : Stage3Codebook10Ms;

            int target = sfLength << 2;
            for (int k = 0; k < nbSubframes; k++)
            {
                int lagCounter = 0;
                int basis = target - (startLag + lagRange[k][0]);
                int energy = SilkEncoderMath.InnerProd(frameIn[basis..], frameIn[basis..], sfLength);
                scratch[lagCounter++] = energy;

                int lagDifference = lagRange[k][1] - lagRange[k][0] + 1;
                for (int i = 1; i < lagDifference; i++)
                {
                    energy -= frameIn[basis + sfLength - i] * frameIn[basis + sfLength - i];
                    energy = SilkEncoderMath.AddSat32(energy, frameIn[basis - i] * frameIn[basis - i]);
                    scratch[lagCounter++] = energy;
                }

                int delta = lagRange[k][0];
                for (int i = 0; i < nbSearch; i++)
                {
                    int index = lagCodebookIn[k][i] - delta;
                    for (int j = 0; j < Stage3Lags; j++)
                    {
                        output[(k * Stage3CodebookMax * Stage3Lags) + (i * Stage3Lags) + j] = scratch[index + j];
                    }
                }

                target += sfLength;
            }
        }
    }

    private static void InsertionSortDecreasing(Span<short> values, int offset, int length, int count, Span<int> indices)
    {
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        for (int i = 1; i < count; i++)
        {
            short value = values[offset + i];
            int j;
            for (j = i - 1; j >= 0 && value > values[offset + j]; j--)
            {
                values[offset + j + 1] = values[offset + j];
                indices[j + 1] = indices[j];
            }

            values[offset + j + 1] = value;
            indices[j + 1] = i;
        }

        for (int i = count; i < length; i++)
        {
            short value = values[offset + i];
            if (value > values[offset + count - 1])
            {
                int j;
                for (j = count - 2; j >= 0 && value > values[offset + j]; j--)
                {
                    values[offset + j + 1] = values[offset + j];
                    indices[j + 1] = indices[j];
                }

                values[offset + j + 1] = value;
                indices[j + 1] = i;
            }
        }
    }

    private static int FindScaling(ReadOnlySpan<short> frame, int sumSquaredLength)
    {
        int maximum = SilkEncoderMath.Int16ArrayMaxAbs(frame);
        int bits;
        if (maximum < SilkEncoderMath.Int16Max)
        {
            bits = 32 - SilkEncoderMath.Clz32(maximum * maximum);
        }
        else
        {
            bits = 30;
        }

        bits += 17 - SilkEncoderMath.Clz16(sumSquaredLength);
        return bits < 31 ? 0 : bits - 30;
    }
}
