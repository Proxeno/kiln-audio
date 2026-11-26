// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The SILK encoder's prediction, noise-shaping and gain analysis stages
/// (RFC 6716 §5.2.3.3 through §5.2.3.7; Appendix A, <c>silk/fixed/*</c>).
/// </summary>
/// <remarks>
/// Every routine is a transcription of the fixed-point reference. Scratch storage is
/// <c>stackalloc</c>, so a frame of analysis allocates nothing. The per-frame state lives in
/// <see cref="SilkEncoderState"/> and is mutated in place.
/// </remarks>
internal static class SilkEncoderAnalysis
{
    private const int Qa = 25;
    private const int LtpCorrelationHeadRoom = 2;
    private const int FindLpcConditionFactorQ32 = 42950; // round(1e-5 * 2^32)

    // ---------------------------------------------------------------------------------------
    // Burg LPC analysis (silk/fixed/burg_modified_FIX.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Computes the LPC coefficients with the modified Burg recursion.</summary>
    internal static void BurgModified(
        out int residualEnergy,
        out int residualEnergyQ,
        Span<int> coefficientsQ16,
        ReadOnlySpan<short> x,
        int minInverseGainQ30,
        int subframeLength,
        int subframeCount,
        int order)
    {
        const int maxFrameSize = 384;
        _ = maxFrameSize;
        const int maxRightShifts = 32 - Qa;
        const int minRightShifts = -16;

        Span<int> firstRow = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> lastRow = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> afQa = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> caf = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];
        Span<int> cab = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];

        SilkEncoderMath.SumSqrShift(out int c0, out int rightShifts, x[..(subframeCount * subframeLength)]);
        if (rightShifts > maxRightShifts)
        {
            c0 <<= rightShifts - maxRightShifts;
            rightShifts = maxRightShifts;
        }
        else
        {
            int leadingZeros = SilkEncoderMath.Clz32(c0) - 1;
            int extraShifts = 2 - leadingZeros;
            if (extraShifts > 0)
            {
                extraShifts = Math.Min(extraShifts, maxRightShifts - rightShifts);
                c0 >>= extraShifts;
            }
            else
            {
                extraShifts = Math.Max(extraShifts, minRightShifts - rightShifts);
                c0 <<= -extraShifts;
            }

            rightShifts += extraShifts;
        }

        cab[0] = caf[0] = unchecked(c0 + SilkFixedPoint.Smmul(FindLpcConditionFactorQ32, c0) + 1);
        firstRow.Clear();

        if (rightShifts > 0)
        {
            for (int s = 0; s < subframeCount; s++)
            {
                int start = s * subframeLength;
                for (int n = 1; n < order + 1; n++)
                {
                    firstRow[n - 1] += (int)(SilkEncoderMath.InnerProd64(
                        x[start..], x[(start + n)..], subframeLength - n) >> rightShifts);
                }
            }
        }
        else
        {
            for (int s = 0; s < subframeCount; s++)
            {
                int start = s * subframeLength;
                for (int n = 1; n < order + 1; n++)
                {
                    firstRow[n - 1] += SilkEncoderMath.InnerProd(
                        x[start..], x[(start + n)..], subframeLength - n) << -rightShifts;
                }
            }
        }

        firstRow.CopyTo(lastRow);
        int inverseGainQ30 = 1 << 30;
        bool reachedMaxGain = false;

        for (int n = 0; n < order; n++)
        {
            if (rightShifts > -2)
            {
                for (int s = 0; s < subframeCount; s++)
                {
                    int start = s * subframeLength;
                    int x1 = -SilkEncoderMath.Shl(x[start + n], 16 - rightShifts);
                    int x2 = -SilkEncoderMath.Shl(x[start + subframeLength - n - 1], 16 - rightShifts);
                    int tmp1 = SilkEncoderMath.Shl(x[start + n], Qa - 16);
                    int tmp2 = SilkEncoderMath.Shl(x[start + subframeLength - n - 1], Qa - 16);
                    for (int k = 0; k < n; k++)
                    {
                        firstRow[k] = SilkFixedPoint.Smlawb(firstRow[k], x1, x[start + n - k - 1]);
                        lastRow[k] = SilkFixedPoint.Smlawb(lastRow[k], x2, x[start + subframeLength - n + k]);
                        int atmpQa = afQa[k];
                        tmp1 = SilkFixedPoint.Smlawb(tmp1, atmpQa, x[start + n - k - 1]);
                        tmp2 = SilkFixedPoint.Smlawb(tmp2, atmpQa, x[start + subframeLength - n + k]);
                    }

                    tmp1 = SilkEncoderMath.Shl(-tmp1, 32 - Qa - rightShifts);
                    tmp2 = SilkEncoderMath.Shl(-tmp2, 32 - Qa - rightShifts);
                    for (int k = 0; k <= n; k++)
                    {
                        caf[k] = SilkFixedPoint.Smlawb(caf[k], tmp1, x[start + n - k]);
                        cab[k] = SilkFixedPoint.Smlawb(cab[k], tmp2, x[start + subframeLength - n + k - 1]);
                    }
                }
            }
            else
            {
                for (int s = 0; s < subframeCount; s++)
                {
                    int start = s * subframeLength;
                    int x1 = -SilkEncoderMath.Shl(x[start + n], -rightShifts);
                    int x2 = -SilkEncoderMath.Shl(x[start + subframeLength - n - 1], -rightShifts);
                    int tmp1 = SilkEncoderMath.Shl(x[start + n], 17);
                    int tmp2 = SilkEncoderMath.Shl(x[start + subframeLength - n - 1], 17);
                    for (int k = 0; k < n; k++)
                    {
                        firstRow[k] = SilkEncoderMath.Mla(firstRow[k], x1, x[start + n - k - 1]);
                        lastRow[k] = SilkEncoderMath.Mla(lastRow[k], x2, x[start + subframeLength - n + k]);
                        int atmp1 = SilkEncoderMath.RshiftRound(afQa[k], Qa - 17);
                        tmp1 = SilkEncoderMath.Mla(tmp1, x[start + n - k - 1], atmp1);
                        tmp2 = SilkEncoderMath.Mla(tmp2, x[start + subframeLength - n + k], atmp1);
                    }

                    tmp1 = -tmp1;
                    tmp2 = -tmp2;
                    for (int k = 0; k <= n; k++)
                    {
                        caf[k] = SilkFixedPoint.Smlaww(caf[k], tmp1, SilkEncoderMath.Shl(x[start + n - k], -rightShifts - 1));
                        cab[k] = SilkFixedPoint.Smlaww(cab[k], tmp2, SilkEncoderMath.Shl(x[start + subframeLength - n + k - 1], -rightShifts - 1));
                    }
                }
            }

            int tmp1b = firstRow[n];
            int tmp2b = lastRow[n];
            int numerator = 0;
            int energy = unchecked(cab[0] + caf[0]);
            for (int k = 0; k < n; k++)
            {
                int atmpQa = afQa[k];
                int leadingZeros = SilkEncoderMath.Clz32(SilkEncoderMath.Abs(atmpQa)) - 1;
                leadingZeros = Math.Min(32 - Qa, leadingZeros);
                int atmp1 = SilkEncoderMath.Shl(atmpQa, leadingZeros);
                tmp1b = SilkEncoderMath.AddLshift(
                    tmp1b, SilkFixedPoint.Smmul(lastRow[n - k - 1], atmp1), 32 - Qa - leadingZeros);
                tmp2b = SilkEncoderMath.AddLshift(
                    tmp2b, SilkFixedPoint.Smmul(firstRow[n - k - 1], atmp1), 32 - Qa - leadingZeros);
                numerator = SilkEncoderMath.AddLshift(
                    numerator, SilkFixedPoint.Smmul(cab[n - k], atmp1), 32 - Qa - leadingZeros);
                energy = SilkEncoderMath.AddLshift(
                    energy, SilkFixedPoint.Smmul(unchecked(cab[k + 1] + caf[k + 1]), atmp1), 32 - Qa - leadingZeros);
            }

            caf[n + 1] = tmp1b;
            cab[n + 1] = tmp2b;
            numerator = unchecked(numerator + tmp2b);
            numerator = SilkEncoderMath.Shl(-numerator, 1);

            int reflectionQ31;
            if (SilkEncoderMath.Abs(numerator) < energy)
            {
                reflectionQ31 = SilkFixedPoint.Div32VarQ(numerator, energy, 31);
            }
            else
            {
                reflectionQ31 = numerator > 0 ? SilkEncoderMath.Int32Max : SilkEncoderMath.Int32Min;
            }

            int tmp = unchecked((1 << 30) - SilkFixedPoint.Smmul(reflectionQ31, reflectionQ31));
            tmp = SilkEncoderMath.Shl(SilkFixedPoint.Smmul(inverseGainQ30, tmp), 2);
            if (tmp <= minInverseGainQ30)
            {
                int tmp2c = (1 << 30) - SilkFixedPoint.Div32VarQ(minInverseGainQ30, inverseGainQ30, 30);
                reflectionQ31 = SilkFixedPoint.SqrtApprox(tmp2c);
                reflectionQ31 = SilkEncoderMath.RshiftRound(unchecked(reflectionQ31 + (tmp2c / reflectionQ31)), 1);
                reflectionQ31 = SilkEncoderMath.Shl(reflectionQ31, 16);
                if (numerator < 0)
                {
                    reflectionQ31 = -reflectionQ31;
                }

                inverseGainQ30 = minInverseGainQ30;
                reachedMaxGain = true;
            }
            else
            {
                inverseGainQ30 = tmp;
            }

            for (int k = 0; k < (n + 1) >> 1; k++)
            {
                int t1 = afQa[k];
                int t2 = afQa[n - k - 1];
                afQa[k] = SilkEncoderMath.AddLshift(t1, SilkFixedPoint.Smmul(t2, reflectionQ31), 1);
                afQa[n - k - 1] = SilkEncoderMath.AddLshift(t2, SilkFixedPoint.Smmul(t1, reflectionQ31), 1);
            }

            afQa[n] = SilkEncoderMath.Sar(reflectionQ31, 31 - Qa);

            if (reachedMaxGain)
            {
                for (int k = n + 1; k < order; k++)
                {
                    afQa[k] = 0;
                }

                break;
            }

            for (int k = 0; k <= n + 1; k++)
            {
                int t1 = caf[k];
                int t2 = cab[n - k + 1];
                caf[k] = SilkEncoderMath.AddLshift(t1, SilkFixedPoint.Smmul(t2, reflectionQ31), 1);
                cab[n - k + 1] = SilkEncoderMath.AddLshift(t2, SilkFixedPoint.Smmul(t1, reflectionQ31), 1);
            }
        }

        if (reachedMaxGain)
        {
            for (int k = 0; k < order; k++)
            {
                coefficientsQ16[k] = -SilkEncoderMath.RshiftRound(afQa[k], Qa - 16);
            }

            if (rightShifts > 0)
            {
                for (int s = 0; s < subframeCount; s++)
                {
                    int start = s * subframeLength;
                    c0 -= (int)(SilkEncoderMath.InnerProd64(x[start..], x[start..], order) >> rightShifts);
                }
            }
            else
            {
                for (int s = 0; s < subframeCount; s++)
                {
                    int start = s * subframeLength;
                    c0 -= SilkEncoderMath.InnerProd(x[start..], x[start..], order) << -rightShifts;
                }
            }

            residualEnergy = SilkEncoderMath.Shl(SilkFixedPoint.Smmul(inverseGainQ30, c0), 2);
            residualEnergyQ = -rightShifts;
        }
        else
        {
            int energy = caf[0];
            int tmp = 1 << 16;
            for (int k = 0; k < order; k++)
            {
                int atmp1 = SilkEncoderMath.RshiftRound(afQa[k], Qa - 16);
                energy = SilkFixedPoint.Smlaww(energy, caf[k + 1], atmp1);
                tmp = SilkFixedPoint.Smlaww(tmp, atmp1, atmp1);
                coefficientsQ16[k] = -atmp1;
            }

            residualEnergy = energy;
            residualEnergyQ = -rightShifts;
        }
    }

    // ---------------------------------------------------------------------------------------
    // LPC analysis and interpolation decision (silk/fixed/find_LPC_FIX.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Finds the frame's NLSFs and chooses the interpolation factor.</summary>
    internal static void FindLpc(
        SilkEncoderState encoder,
        Span<short> nlsfQ15,
        ReadOnlySpan<short> x,
        int minInverseGainQ30)
    {
        int order = encoder.PredictLpcOrder;
        int subframeLength = encoder.SubframeLength + order;
        encoder.Indices.NlsfInterpCoefQ2 = 4;

        Span<int> coefficientsQ16 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> temporaryQ16 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<short> temporaryQ12 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> nlsf0Q15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> lpcResidual = stackalloc short[SilkEncoderMath.MaxLpcOrder + (2 * SilkEncoderMath.MaxSubFrameLength) + SilkEncoderMath.MaxLpcOrder];
        Span<short> windowed = lpcResidual;

        BurgModified(
            out int residualEnergy,
            out int residualEnergyQ,
            coefficientsQ16,
            x,
            minInverseGainQ30,
            subframeLength,
            encoder.SubframeCount,
            order);

        if (encoder.UseInterpolatedNlsfs == 1 && encoder.FirstFrameAfterReset == 0 && encoder.SubframeCount == 4)
        {
            BurgModified(
                out int temporaryEnergy,
                out int temporaryEnergyQ,
                temporaryQ16,
                x[(2 * subframeLength)..],
                minInverseGainQ30,
                subframeLength,
                2,
                order);

            int shift = temporaryEnergyQ - residualEnergyQ;
            if (shift >= 0)
            {
                if (shift < 32)
                {
                    residualEnergy -= temporaryEnergy >> shift;
                }
            }
            else
            {
                residualEnergy = (residualEnergy >> -shift) - temporaryEnergy;
                residualEnergyQ = temporaryEnergyQ;
            }

            SilkNlsfEncoder.A2Nlsf(nlsfQ15, temporaryQ16, order);

            for (int k = 3; k >= 0; k--)
            {
                SilkEncoderMath.Interpolate(nlsf0Q15, encoder.PrevNlsfQ15, nlsfQ15, k, order);
                SilkNlsf.ConvertToLpc(temporaryQ12, nlsf0Q15, order);
                SilkEncoderMath.LpcAnalysisFilter(
                    lpcResidual, x, temporaryQ12, 2 * subframeLength, order);

                SilkEncoderMath.SumSqrShift(
                    out int energy0, out int shift0, lpcResidual.Slice(order, subframeLength - order));
                SilkEncoderMath.SumSqrShift(
                    out int energy1, out int shift1, lpcResidual.Slice(order + subframeLength, subframeLength - order));

                int interpolatedEnergy;
                int interpolatedEnergyQ;
                shift = shift0 - shift1;
                if (shift >= 0)
                {
                    energy1 >>= shift;
                    interpolatedEnergyQ = -shift0;
                }
                else
                {
                    energy0 >>= -shift;
                    interpolatedEnergyQ = -shift1;
                }

                interpolatedEnergy = unchecked(energy0 + energy1);

                shift = interpolatedEnergyQ - residualEnergyQ;
                bool lower;
                if (shift >= 0)
                {
                    lower = (interpolatedEnergy >> shift) < residualEnergy;
                }
                else if (-shift < 32)
                {
                    lower = interpolatedEnergy < (residualEnergy >> -shift);
                }
                else
                {
                    lower = false;
                }

                if (lower)
                {
                    residualEnergy = interpolatedEnergy;
                    residualEnergyQ = interpolatedEnergyQ;
                    encoder.Indices.NlsfInterpCoefQ2 = k;
                }
            }
        }

        if (encoder.Indices.NlsfInterpCoefQ2 == 4)
        {
            SilkNlsfEncoder.A2Nlsf(nlsfQ15, coefficientsQ16, order);
        }

        _ = windowed;
    }

    // ---------------------------------------------------------------------------------------
    // Noise shape analysis (silk/fixed/noise_shape_analysis_FIX.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Computes the noise shaping coefficients and initial gains.</summary>
    internal static void NoiseShapeAnalysis(
        SilkEncoderState encoder,
        SilkEncoderControl control,
        ReadOnlySpan<short> pitchResidual,
        ReadOnlySpan<short> xBuffer,
        int frameOffset)
    {
        int order = encoder.ShapingLpcOrder;
        int subframeLength = encoder.SubframeLength;
        int subframeCount = encoder.SubframeCount;
        SilkShapeState shape = encoder.Shape;

        int snrAdjustQ7 = encoder.SnrDbQ7;
        control.InputQualityQ14 = (encoder.InputQualityBandsQ15[0] + encoder.InputQualityBandsQ15[1]) >> 2;
        control.CodingQualityQ14 = SilkEncoderMath.SigmQ15(
            SilkEncoderMath.RshiftRound(snrAdjustQ7 - (20 * 128), 4)) >> 1;

        if (encoder.UseCbr == 0)
        {
            int bQ8 = 256 - encoder.SpeechActivityQ8;
            bQ8 = SilkFixedPoint.Smulwb(bQ8 << 8, bQ8);
            snrAdjustQ7 = SilkFixedPoint.Smlawb(
                snrAdjustQ7,
                SilkEncoderMath.Smulbb(-256 >> 5, bQ8),
                SilkFixedPoint.Smulwb((1 << 14) + control.InputQualityQ14, control.CodingQualityQ14));
        }

        if (encoder.Indices.SignalType == 2)
        {
            snrAdjustQ7 = SilkFixedPoint.Smlawb(snrAdjustQ7, 512, encoder.LtpCorrQ15);
        }
        else
        {
            snrAdjustQ7 = SilkFixedPoint.Smlawb(
                snrAdjustQ7,
                SilkFixedPoint.Smlawb(3072, -104858, encoder.SnrDbQ7),
                (1 << 14) - control.InputQualityQ14);
        }

        if (encoder.Indices.SignalType == 2)
        {
            encoder.Indices.QuantOffsetType = 0;
            control.SparsenessQ8 = 0;
        }
        else
        {
            int samples = encoder.FsKhz * 2;
            int blocks = (5 * subframeCount) / 2;
            int variationQ7 = 0;
            int previousLogQ7 = 0;
            for (int k = 0; k < blocks; k++)
            {
                SilkEncoderMath.SumSqrShift(
                    out int nrg, out int scale, pitchResidual.Slice(k * samples, samples));
                nrg += samples >> scale;
                int logQ7 = SilkEncoderMath.Lin2Log(nrg);
                if (k > 0)
                {
                    variationQ7 += SilkEncoderMath.Abs(logQ7 - previousLogQ7);
                }

                previousLogQ7 = logQ7;
            }

            control.SparsenessQ8 = SilkEncoderMath.SigmQ15(
                SilkFixedPoint.Smulwb(variationQ7 - (5 * 128), 6554)) >> 7;

            encoder.Indices.QuantOffsetType = control.SparsenessQ8 > 192 ? 0 : 1;

            snrAdjustQ7 = SilkFixedPoint.Smlawb(
                snrAdjustQ7, 65536, control.SparsenessQ8 - 128);
        }

        int strengthQ16 = SilkFixedPoint.Smulwb(control.PredGainQ16, 66);
        int bwExp1Q16 = SilkFixedPoint.Div32VarQ(
            62259, SilkFixedPoint.Smlaww(1 << 16, strengthQ16, strengthQ16), 16);
        int bwExp2Q16 = bwExp1Q16;
        int deltaQ16 = SilkFixedPoint.Smulwb(
            (1 << 16) - SilkEncoderMath.Smulbb(3, control.CodingQualityQ14), 655);
        bwExp1Q16 -= deltaQ16;
        bwExp2Q16 += deltaQ16;
        bwExp1Q16 = SilkEncoderMath.Div32_16(bwExp1Q16 << 14, bwExp2Q16 >> 2);

        int warpingQ16 = encoder.WarpingQ16 > 0
            ? SilkFixedPoint.Smlawb(encoder.WarpingQ16, control.CodingQualityQ14, 2621)
            : 0;

        Span<int> autocorrelation = stackalloc int[SilkEncoderMath.MaxShapeLpcOrder + 1];
        Span<int> reflectionQ16 = stackalloc int[SilkEncoderMath.MaxShapeLpcOrder];
        Span<int> ar1Q24 = stackalloc int[SilkEncoderMath.MaxShapeLpcOrder];
        Span<int> ar2Q24 = stackalloc int[SilkEncoderMath.MaxShapeLpcOrder];
        Span<short> windowed = stackalloc short[240];

        int windowOffset = frameOffset - encoder.LaShape;
        for (int k = 0; k < subframeCount; k++)
        {
            int flatPart = encoder.FsKhz * 3;
            int slopePart = (encoder.ShapeWinLength - flatPart) >> 1;
            ReadOnlySpan<short> source = xBuffer[(windowOffset + (k * subframeLength))..];
            SilkEncoderMath.ApplySineWindow(windowed, source, 1, slopePart);
            source.Slice(slopePart, flatPart).CopyTo(windowed[slopePart..]);
            SilkEncoderMath.ApplySineWindow(windowed[(slopePart + flatPart)..], source[(slopePart + flatPart)..], 2, slopePart);

            int scale;
            if (encoder.WarpingQ16 > 0)
            {
                WarpedAutocorrelation(autocorrelation, out scale, windowed[..encoder.ShapeWinLength], warpingQ16, encoder.ShapeWinLength, order);
            }
            else
            {
                SilkEncoderMath.Autocorr(out scale, autocorrelation, windowed[..encoder.ShapeWinLength], encoder.ShapeWinLength, order + 1);
            }

            autocorrelation[0] = unchecked(autocorrelation[0] + Math.Max(
                SilkFixedPoint.Smulwb(autocorrelation[0] >> 4, 52), 1));

            int nrg = Schur64(reflectionQ16, autocorrelation, order);
            SilkEncoderMath.K2aQ16(ar2Q24, reflectionQ16, order);

            int qnrg = -scale;
            if ((qnrg & 1) != 0)
            {
                qnrg -= 1;
                nrg >>= 1;
            }

            int tmp32 = SilkFixedPoint.SqrtApprox(nrg);
            qnrg >>= 1;
            control.GainsQ16[k] = SilkFixedPoint.LshiftSat32(tmp32, 16 - qnrg);

            if (encoder.WarpingQ16 > 0)
            {
                int gainMultiplierQ16 = WarpedGain(ar2Q24, warpingQ16, order);
                control.GainsQ16[k] = SilkFixedPoint.Smulww(control.GainsQ16[k], gainMultiplierQ16);
                if (control.GainsQ16[k] < 0)
                {
                    control.GainsQ16[k] = SilkEncoderMath.Int32Max;
                }
            }

            SilkEncoderMath.Bwexpander32(ar2Q24, order, bwExp2Q16);
            ar2Q24[..order].CopyTo(ar1Q24);
            SilkEncoderMath.Bwexpander32(ar1Q24, order, bwExp1Q16);

            int preNrgQ30 = LpcInversePredGainQ24(ar2Q24, order);
            nrg = LpcInversePredGainQ24(ar1Q24, order);

            preNrgQ30 = SilkEncoderMath.Shl(SilkFixedPoint.Smulwb(preNrgQ30, 22938), 1);
            control.GainsPreQ14[k] = 4915 + SilkFixedPoint.Div32VarQ(preNrgQ30, nrg, 14);

            LimitWarpedCoefficients(ar2Q24, ar1Q24, warpingQ16, 67092087, order);

            for (int i = 0; i < order; i++)
            {
                control.Ar1Q13[(k * SilkEncoderMath.MaxShapeLpcOrder) + i] =
                    (short)SilkEncoderMath.Sat16(SilkEncoderMath.RshiftRound(ar1Q24[i], 11));
                control.Ar2Q13[(k * SilkEncoderMath.MaxShapeLpcOrder) + i] =
                    (short)SilkEncoderMath.Sat16(SilkEncoderMath.RshiftRound(ar2Q24[i], 11));
            }
        }

        int gainMultiplier = SilkEncoderMath.Log2Lin(
            -SilkFixedPoint.Smlawb(-2048, snrAdjustQ7, 10486));
        int gainAdd = SilkEncoderMath.Log2Lin(
            SilkFixedPoint.Smlawb(2048, 256, 10486));
        for (int k = 0; k < subframeCount; k++)
        {
            control.GainsQ16[k] = SilkFixedPoint.Smulww(control.GainsQ16[k], gainMultiplier);
            control.GainsQ16[k] = (int)Math.Min((long)control.GainsQ16[k] + gainAdd, SilkEncoderMath.Int32Max);
        }

        int tiltGainMultiplier = (1 << 16) + SilkEncoderMath.RshiftRound(
            SilkEncoderMath.Mla(3355443, control.CodingQualityQ14, 410), 10);
        for (int k = 0; k < subframeCount; k++)
        {
            control.GainsPreQ14[k] = SilkFixedPoint.Smulwb(tiltGainMultiplier, control.GainsPreQ14[k]);
        }

        int lfStrengthQ16 = SilkEncoderMath.Mul(
            64,
            SilkFixedPoint.Smlawb(4096, 4096, encoder.InputQualityBandsQ15[0] - 32768));
        lfStrengthQ16 = SilkEncoderMath.Mul(lfStrengthQ16, encoder.SpeechActivityQ8) >> 8;

        int tiltQ16;
        if (encoder.Indices.SignalType == 2)
        {
            int inverseFsQ14 = 3277 / encoder.FsKhz;
            for (int k = 0; k < subframeCount; k++)
            {
                int bQ14 = inverseFsQ14 + (49152 / control.PitchL[k]);
                control.LfShpQ14[k] = SilkEncoderMath.Shl(
                    (1 << 14) - bQ14 - SilkFixedPoint.Smulwb(lfStrengthQ16, bQ14), 16)
                    | (ushort)(bQ14 - (1 << 14));
            }

            tiltQ16 = -16384 - SilkFixedPoint.Smulwb(
                (1 << 16) - 16384,
                SilkFixedPoint.Smulwb(5872026, encoder.SpeechActivityQ8));
        }
        else
        {
            int bQ14 = 21299 / encoder.FsKhz;
            control.LfShpQ14[0] = SilkEncoderMath.Shl(
                (1 << 14) - bQ14 - SilkFixedPoint.Smulwb(lfStrengthQ16, SilkFixedPoint.Smulwb(39322, bQ14)), 16)
                | (ushort)(bQ14 - (1 << 14));
            for (int k = 1; k < subframeCount; k++)
            {
                control.LfShpQ14[k] = control.LfShpQ14[0];
            }

            tiltQ16 = -16384;
        }

        int harmonicBoostQ16 = SilkFixedPoint.Smulwb(
            SilkFixedPoint.Smulwb((1 << 17) - SilkEncoderMath.Shl(control.CodingQualityQ14, 3), encoder.LtpCorrQ15),
            6554);
        harmonicBoostQ16 = SilkFixedPoint.Smlawb(
            harmonicBoostQ16, (1 << 16) - SilkEncoderMath.Shl(control.InputQualityQ14, 2), 6554);

        int harmonicShapeGainQ16;
        if (encoder.Indices.SignalType == 2)
        {
            harmonicShapeGainQ16 = SilkFixedPoint.Smlawb(
                19661,
                (1 << 16) - SilkFixedPoint.Smulwb((1 << 18) - SilkEncoderMath.Shl(control.CodingQualityQ14, 4), control.InputQualityQ14),
                13107);
            harmonicShapeGainQ16 = SilkFixedPoint.Smulwb(
                SilkEncoderMath.Shl(harmonicShapeGainQ16, 1),
                SilkFixedPoint.SqrtApprox(SilkEncoderMath.Shl(encoder.LtpCorrQ15, 15)));
        }
        else
        {
            harmonicShapeGainQ16 = 0;
        }

        for (int k = 0; k < 4; k++)
        {
            shape.HarmBoostSmthQ16 = SilkFixedPoint.Smlawb(
                shape.HarmBoostSmthQ16, harmonicBoostQ16 - shape.HarmBoostSmthQ16, 26214);
            shape.HarmShapeGainSmthQ16 = SilkFixedPoint.Smlawb(
                shape.HarmShapeGainSmthQ16, harmonicShapeGainQ16 - shape.HarmShapeGainSmthQ16, 26214);
            shape.TiltSmthQ16 = SilkFixedPoint.Smlawb(
                shape.TiltSmthQ16, tiltQ16 - shape.TiltSmthQ16, 26214);

            control.HarmBoostQ14[k] = SilkEncoderMath.RshiftRound(shape.HarmBoostSmthQ16, 2);
            control.HarmShapeGainQ14[k] = SilkEncoderMath.RshiftRound(shape.HarmShapeGainSmthQ16, 2);
            control.TiltQ14[k] = SilkEncoderMath.RshiftRound(shape.TiltSmthQ16, 2);
        }
    }

    // ---------------------------------------------------------------------------------------
    // LTP analysis and quantization (silk/fixed/find_LTP_FIX.c, silk/quant_LTP_gains.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Finds and quantizes the LTP coefficients of a voiced frame.</summary>
    internal static void FindLtp(
        SilkEncoderState encoder,
        SilkEncoderControl control,
        ReadOnlySpan<short> residual,
        ReadOnlySpan<int> lags,
        ReadOnlySpan<int> weightsQ15,
        out int ltPredCodGainQ7)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int subframeLength = encoder.SubframeLength;
        int subframeCount = encoder.SubframeCount;
        int memoryOffset = encoder.LtpMemLength;

        Span<int> wltp = stackalloc int[4 * order * order];
        Span<int> correlationMatrix = stackalloc int[order * order];
        Span<int> correlationVector = stackalloc int[order];
        Span<int> coefficientsQ16 = stackalloc int[order];
        Span<int> residualEnergy = stackalloc int[subframeCount];
        Span<int> rrEnergies = stackalloc int[subframeCount];
        Span<int> w = stackalloc int[subframeCount];
        Span<int> sums = stackalloc int[subframeCount];
        Span<int> deltas = stackalloc int[order];
        Span<int> correlationShifts = stackalloc int[subframeCount];
        int maxCorrelationShifts = 0;

        for (int k = 0; k < subframeCount; k++)
        {
            int rIndex = memoryOffset + (k * subframeLength);
            int lagIndex = rIndex - lags[k] - (order / 2);

            SilkEncoderMath.SumSqrShift(out int rr, out int rrShifts, residual.Slice(rIndex, subframeLength));
            int leadingZeros = SilkEncoderMath.Clz32(rr);
            if (leadingZeros < LtpCorrelationHeadRoom)
            {
                rr = SilkEncoderMath.RshiftRound(rr, LtpCorrelationHeadRoom - leadingZeros);
                rrShifts += LtpCorrelationHeadRoom - leadingZeros;
            }

            correlationShifts[k] = rrShifts;
            CorrMatrix(
                residual[lagIndex..], subframeLength, order, LtpCorrelationHeadRoom,
                correlationMatrix, ref correlationShifts[k]);
            CorrVector(
                residual[lagIndex..], residual[rIndex..], subframeLength, order, correlationVector,
                correlationShifts[k]);

            if (correlationShifts[k] > rrShifts)
            {
                rr >>= correlationShifts[k] - rrShifts;
            }

            int regularization = 1;
            regularization = SilkFixedPoint.Smlawb(regularization, rr, 1092);
            regularization = SilkFixedPoint.Smlawb(
                regularization, correlationMatrix[0], 1092);
            regularization = SilkFixedPoint.Smlawb(
                regularization, correlationMatrix[(order * order) - 1], 1092);
            for (int i = 0; i < order; i++)
            {
                correlationMatrix[(i * order) + i] = unchecked(correlationMatrix[(i * order) + i] + regularization);
            }

            rr = unchecked(rr + regularization);
            rrEnergies[k] = rr;
            SolveLdl(correlationMatrix, order, correlationVector, coefficientsQ16);

            for (int i = 0; i < order; i++)
            {
                control.LtpCoefQ14[(k * order) + i] =
                    (short)SilkEncoderMath.Sat16(SilkEncoderMath.RshiftRound(coefficientsQ16[i], 2));
            }

            residualEnergy[k] = ResidualEnergy16Covar(
                control.LtpCoefQ14.AsSpan(k * order, order), correlationMatrix, correlationVector, rr, order, 14);

            int extraShifts = Math.Min(correlationShifts[k], LtpCorrelationHeadRoom);
            int denominator = SilkFixedPoint.LshiftSat32(
                SilkFixedPoint.Smulwb(residualEnergy[k], weightsQ15[k]), 1 + extraShifts)
                + (SilkFixedPoint.Smulwb(subframeLength, 655) >> (correlationShifts[k] - extraShifts));
            denominator = Math.Max(denominator, 1);
            int temp = (weightsQ15[k] << 16) / denominator;
            temp >>= 31 + correlationShifts[k] - extraShifts - 26;

            int wltpMax = 0;
            for (int i = 0; i < order * order; i++)
            {
                wltpMax = Math.Max(correlationMatrix[i], wltpMax);
            }

            int leftShift = SilkEncoderMath.Clz32(wltpMax) - 1 - 3;
            if (26 - 18 + leftShift < 31)
            {
                temp = Math.Min(temp, 1 << (26 - 18 + leftShift));
            }

            for (int i = 0; i < order * order; i++)
            {
                wltp[(k * order * order) + i] = (int)((long)correlationMatrix[i] * temp >> 8);
            }

            w[k] = wltp[(k * order * order) + ((order / 2) * order) + (order / 2)];
        }

        for (int k = 0; k < subframeCount; k++)
        {
            maxCorrelationShifts = Math.Max(correlationShifts[k], maxCorrelationShifts);
        }

        int lpcResidualEnergy = 0;
        int lpcLtpResidualEnergy = 0;
        for (int k = 0; k < subframeCount; k++)
        {
            int scaleShift = 1 + (maxCorrelationShifts - correlationShifts[k]);
            lpcResidualEnergy = unchecked(lpcResidualEnergy
                + ((SilkFixedPoint.Smulwb(rrEnergies[k], weightsQ15[k]) + 1) >> scaleShift));
            lpcLtpResidualEnergy = unchecked(lpcLtpResidualEnergy
                + ((SilkFixedPoint.Smulwb(residualEnergy[k], weightsQ15[k]) + 1) >> scaleShift));
        }

        lpcLtpResidualEnergy = Math.Max(lpcLtpResidualEnergy, 1);
        int divisionQ16 = SilkFixedPoint.Div32VarQ(lpcResidualEnergy, lpcLtpResidualEnergy, 16);
        ltPredCodGainQ7 = SilkEncoderMath.Smulbb(3, SilkEncoderMath.Lin2Log(divisionQ16) - (16 << 7));

        // The smoothing below runs on the unquantized coefficients (find_LTP_FIX.c); the
        // codebook quantization (silk_quant_LTP_gains) follows it.
        for (int k = 0; k < subframeCount; k++)
        {
            sums[k] = 0;
            for (int i = 0; i < order; i++)
            {
                sums[k] += control.LtpCoefQ14[(k * order) + i];
            }
        }

        int maximumAbsSum = 0;
        int maximumWeightBits = 0;
        for (int k = 0; k < subframeCount; k++)
        {
            maximumAbsSum = Math.Max(maximumAbsSum, SilkEncoderMath.Abs(sums[k]));
            maximumWeightBits = Math.Max(
                maximumWeightBits, 32 - SilkEncoderMath.Clz32(w[k]) + correlationShifts[k] - maxCorrelationShifts);
        }

        int extra = maximumWeightBits + 32 - SilkEncoderMath.Clz32(maximumAbsSum) - 14;
        extra -= 32 - 1 - 2 + maxCorrelationShifts;
        extra = Math.Max(extra, 0);

        int maxShiftsWithExtra = maxCorrelationShifts + extra;
        int denominatorSmoothing = (262 >> (maxCorrelationShifts + extra)) + 1;
        int weightedSum = 0;
        for (int k = 0; k < subframeCount; k++)
        {
            int shiftedWeight = w[k] >> (maxShiftsWithExtra - correlationShifts[k]);
            denominatorSmoothing = unchecked(denominatorSmoothing + shiftedWeight);
            weightedSum = unchecked(weightedSum
                + SilkEncoderMath.Shl(SilkFixedPoint.Smulww(shiftedWeight, sums[k]), 2));
        }

        int meanQ12 = SilkFixedPoint.Div32VarQ(weightedSum, denominatorSmoothing, 12);

        for (int k = 0; k < subframeCount; k++)
        {
            int weightQ16 = 2 - correlationShifts[k] > 0
                ? w[k] >> (2 - correlationShifts[k])
                : SilkFixedPoint.LshiftSat32(w[k], correlationShifts[k] - 2);

            int gQ26 = SilkEncoderMath.Mul(
                6710886 / (6710886 / 1024 + weightQ16),
                SilkFixedPoint.LshiftSat32(unchecked(meanQ12 - (sums[k] >> 2)), 4));

            int deltaSum = 0;
            for (int i = 0; i < order; i++)
            {
                deltas[i] = Math.Max((int)control.LtpCoefQ14[(k * order) + i], 1638);
                deltaSum += deltas[i];
            }

            int deltaScale = gQ26 / deltaSum;
            for (int i = 0; i < order; i++)
            {
                int value = control.LtpCoefQ14[(k * order) + i]
                    + SilkFixedPoint.Smulwb(deltaScale << 4, deltas[i]);
                control.LtpCoefQ14[(k * order) + i] = (short)SilkEncoderMath.Limit(value, -16000, 28000);
            }
        }

        QuantizeLtpGains(encoder, control, wltp);
    }

    private static void QuantizeLtpGains(SilkEncoderState encoder, SilkEncoderControl control, ReadOnlySpan<int> weightsQ18)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int subframeCount = encoder.SubframeCount;
        int minimumRateDistortion = SilkEncoderMath.Int32Max;
        Span<int> temporaryIndices = stackalloc int[4];

        for (int codebook = 0; codebook < 3; codebook++)
        {
            int codebookSize = SilkEncoderTables.LtpCodebookSizes[codebook];
            ReadOnlySpan<byte> rates = SilkEncoderTables.LtpGainBitsQ5[codebook];
            ReadOnlySpan<sbyte> codebookVectors = SilkSynthesisTables.LtpCodebookQ7[codebook];
            int rateDistortion = 0;

            for (int k = 0; k < subframeCount; k++)
            {
                int index = VectorQuantizeWeighted(
                    out int subframeCost,
                    control.LtpCoefQ14.AsSpan(k * order, order),
                    weightsQ18.Slice(k * order * order, order * order),
                    codebookVectors,
                    rates,
                    encoder.MuLtpQ9,
                    codebookSize);
                temporaryIndices[k] = index;
                rateDistortion = (int)Math.Min((long)rateDistortion + subframeCost, SilkEncoderMath.Int32Max);
            }

            rateDistortion = Math.Min(SilkEncoderMath.Int32Max - 1, rateDistortion);
            if (rateDistortion < minimumRateDistortion)
            {
                minimumRateDistortion = rateDistortion;
                encoder.Indices.PerIndex = codebook;
                for (int k = 0; k < subframeCount; k++)
                {
                    encoder.Indices.LtpIndex[k] = temporaryIndices[k];
                }
            }

            if (encoder.LtpQuantLowComplexity == 1 && rateDistortion < SilkEncoderTables.LtpGainMiddleAverageRdQ14)
            {
                break;
            }
        }

        ReadOnlySpan<sbyte> selected = SilkSynthesisTables.LtpCodebookQ7[encoder.Indices.PerIndex];
        for (int k = 0; k < subframeCount; k++)
        {
            for (int i = 0; i < order; i++)
            {
                control.LtpCoefQ14[(k * order) + i] =
                    (short)(selected[(encoder.Indices.LtpIndex[k] * order) + i] << 7);
            }
        }
    }

    private static int VectorQuantizeWeighted(
        out int rateDistortion,
        ReadOnlySpan<short> input,
        ReadOnlySpan<int> weights,
        ReadOnlySpan<sbyte> codebook,
        ReadOnlySpan<byte> codeLengths,
        int muQ9,
        int size)
    {
        const int order = SilkEncoderMath.LtpOrder;
        rateDistortion = SilkEncoderMath.Int32Max;
        int best = 0;
        Span<short> difference = stackalloc short[order];

        for (int k = 0; k < size; k++)
        {
            for (int i = 0; i < order; i++)
            {
                difference[i] = (short)(input[i] - (codebook[(k * order) + i] << 7));
            }

            int sum = SilkEncoderMath.Smulbb(muQ9, codeLengths[k]);
            int partial = SilkFixedPoint.Smulwb(weights[1], difference[1]);
            partial = SilkFixedPoint.Smlawb(partial, weights[2], difference[2]);
            partial = SilkFixedPoint.Smlawb(partial, weights[3], difference[3]);
            partial = SilkFixedPoint.Smlawb(partial, weights[4], difference[4]);
            partial = SilkEncoderMath.Shl(partial, 1);
            partial = SilkFixedPoint.Smlawb(partial, weights[0], difference[0]);
            sum = SilkFixedPoint.Smlawb(sum, partial, difference[0]);

            partial = SilkFixedPoint.Smulwb(weights[7], difference[2]);
            partial = SilkFixedPoint.Smlawb(partial, weights[8], difference[3]);
            partial = SilkFixedPoint.Smlawb(partial, weights[9], difference[4]);
            partial = SilkEncoderMath.Shl(partial, 1);
            partial = SilkFixedPoint.Smlawb(partial, weights[6], difference[1]);
            sum = SilkFixedPoint.Smlawb(sum, partial, difference[1]);

            partial = SilkFixedPoint.Smulwb(weights[13], difference[3]);
            partial = SilkFixedPoint.Smlawb(partial, weights[14], difference[4]);
            partial = SilkEncoderMath.Shl(partial, 1);
            partial = SilkFixedPoint.Smlawb(partial, weights[12], difference[2]);
            sum = SilkFixedPoint.Smlawb(sum, partial, difference[2]);

            partial = SilkFixedPoint.Smulwb(weights[19], difference[4]);
            partial = SilkEncoderMath.Shl(partial, 1);
            partial = SilkFixedPoint.Smlawb(partial, weights[18], difference[3]);
            sum = SilkFixedPoint.Smlawb(sum, partial, difference[3]);

            partial = SilkFixedPoint.Smulwb(weights[24], difference[4]);
            sum = SilkFixedPoint.Smlawb(sum, partial, difference[4]);

            if (sum < rateDistortion)
            {
                rateDistortion = sum;
                best = k;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------------------------------
    // Gains (silk/fixed/process_gains_FIX.c and silk/gain_quant.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Shapes, limits and quantizes the subframe gains.</summary>
    internal static void ProcessGains(SilkEncoderState encoder, SilkEncoderControl control, int conditionalCoding)
    {
        int subframeCount = encoder.SubframeCount;

        if (encoder.Indices.SignalType == 2)
        {
            int sQ16 = -SilkEncoderMath.SigmQ15(SilkEncoderMath.RshiftRound(control.LtPredCodGainQ7 - (12 * 128), 4));
            for (int k = 0; k < subframeCount; k++)
            {
                control.GainsQ16[k] = SilkFixedPoint.Smlawb(control.GainsQ16[k], control.GainsQ16[k], sQ16);
            }
        }

        // InvMaxSqrVal = pow(2, 0.33 * (21 - SNR_dB)) / subfr_length; the Q7 constant is
        // round((21 + 16/0.33) * 128) = 8894.
        int invMaxQ16 = SilkEncoderMath.Div32_16(
            SilkEncoderMath.Log2Lin(SilkFixedPoint.Smulwb(8894 - encoder.SnrDbQ7, 21627)),
            encoder.SubframeLength);

        for (int k = 0; k < subframeCount; k++)
        {
            int residualPart = SilkFixedPoint.Smulww(control.ResNrg[k], invMaxQ16);
            if (control.ResNrgQ[k] > 0)
            {
                residualPart = SilkEncoderMath.RshiftRound(residualPart, control.ResNrgQ[k]);
            }
            else if (residualPart >= SilkEncoderMath.Int32Max >> -control.ResNrgQ[k])
            {
                residualPart = SilkEncoderMath.Int32Max;
            }
            else
            {
                residualPart <<= -control.ResNrgQ[k];
            }

            int gain = control.GainsQ16[k];
            int gainSquared = SilkEncoderMath.AddSat32(residualPart, SilkFixedPoint.Smmul(gain, gain));
            if (gainSquared < SilkEncoderMath.Int16Max)
            {
                gainSquared = SilkFixedPoint.Smlaww(residualPart << 16, gain, gain);
                gain = SilkFixedPoint.SqrtApprox(gainSquared);
                gain = Math.Min(gain, SilkEncoderMath.Int32Max >> 8);
                control.GainsQ16[k] = SilkFixedPoint.LshiftSat32(gain, 8);
            }
            else
            {
                gain = SilkFixedPoint.SqrtApprox(gainSquared);
                gain = Math.Min(gain, SilkEncoderMath.Int32Max >> 16);
                control.GainsQ16[k] = SilkFixedPoint.LshiftSat32(gain, 16);
            }
        }

        control.GainsUnqQ16.AsSpan(0, subframeCount).Clear();
        for (int k = 0; k < subframeCount; k++)
        {
            control.GainsUnqQ16[k] = control.GainsQ16[k];
        }

        control.LastGainIndexPrev = encoder.Shape.LastGainIndex;
        QuantizeGains(
            encoder.Indices.GainsIndices, control.GainsQ16, ref encoder.Shape.LastGainIndex,
            conditionalCoding, subframeCount);

        if (encoder.Indices.SignalType == 2)
        {
            encoder.Indices.QuantOffsetType =
                control.LtPredCodGainQ7 + (encoder.InputTiltQ15 >> 8) > 128 ? 0 : 1;
        }

        int quantOffsetQ10 = SilkSynthesisTables.QuantizationOffsetQ10[encoder.Indices.SignalType >> 1][encoder.Indices.QuantOffsetType];
        control.LambdaQ10 = 1229
            + SilkEncoderMath.Smulbb(-50, 1) // LAMBDA_DELAYED_DECISIONS, one state
            + SilkFixedPoint.Smulwb(-52428, encoder.SpeechActivityQ8)
            + SilkFixedPoint.Smulwb(-409, control.InputQualityQ14)
            + SilkFixedPoint.Smulwb(-818, control.CodingQualityQ14)
            + SilkFixedPoint.Smulwb(52429, quantOffsetQ10);
    }

    /// <summary>Quantizes the gains with hysteresis (Appendix A, <c>silk_gains_quant</c>).</summary>
    internal static void QuantizeGains(
        Span<int> indices, Span<int> gainsQ16, ref int previousIndex, int conditional, int subframeCount)
    {
        for (int k = 0; k < subframeCount; k++)
        {
            int index = SilkFixedPoint.Smulwb(
                SilkEncoderTables.GainScaleQ16,
                SilkEncoderMath.Lin2Log(gainsQ16[k]) - SilkEncoderTables.GainOffsetQ7);

            if (index < previousIndex)
            {
                index++;
            }

            index = SilkEncoderMath.Limit(index, 0, 63);

            if (k == 0 && conditional == 0)
            {
                index = SilkEncoderMath.Limit(index, previousIndex + SilkEncoderTables.MinDeltaGainQuant, 63);
                previousIndex = index;
            }
            else
            {
                index -= previousIndex;
                int doubleStepThreshold = (2 * SilkEncoderTables.MaxDeltaGainQuant)
                    - SilkEncoderMath.GainLevels + previousIndex;
                if (index > doubleStepThreshold)
                {
                    index = doubleStepThreshold + ((index - doubleStepThreshold + 1) >> 1);
                }

                index = SilkEncoderMath.Limit(index, SilkEncoderTables.MinDeltaGainQuant, SilkEncoderTables.MaxDeltaGainQuant);
                if (index > doubleStepThreshold)
                {
                    previousIndex += (index << 1) - doubleStepThreshold;
                }
                else
                {
                    previousIndex += index;
                }

                index -= SilkEncoderTables.MinDeltaGainQuant;
            }

            indices[k] = index;
            gainsQ16[k] = SilkEncoderMath.Log2Lin(
                Math.Min(SilkFixedPoint.Smulwb(SilkEncoderTables.GainInvScaleQ16, previousIndex) + SilkEncoderTables.GainOffsetQ7, 3967));
        }
    }

    /// <summary>Computes the unique identifier of a gain index vector.</summary>
    internal static int GainsId(ReadOnlySpan<int> indices, int subframeCount)
    {
        int id = 0;
        for (int k = 0; k < subframeCount; k++)
        {
            id = SilkEncoderMath.AddLshift(indices[k], id, 8);
        }

        return id;
    }

    // ---------------------------------------------------------------------------------------
    // LTP scale and analysis filter
    // ---------------------------------------------------------------------------------------

    /// <summary>Selects the LTP scaling index (Appendix A, <c>silk_LTP_scale_ctrl_FIX</c>).</summary>
    internal static void LtpScaleCtrl(SilkEncoderState encoder, SilkEncoderControl control, int conditionalCoding)
    {
        if (conditionalCoding == 0)
        {
            int roundLoss = encoder.FramesPerPacket;
            encoder.Indices.LtpScaleIndex = SilkEncoderMath.Limit(
                SilkFixedPoint.Smulwb(
                    SilkEncoderMath.Smulbb(roundLoss, control.LtPredCodGainQ7), 51),
                0, 2);
        }
        else
        {
            encoder.Indices.LtpScaleIndex = 0;
        }

        control.LtpScaleQ14 = SilkTables.LtpScalingQ14[encoder.Indices.LtpScaleIndex];
    }

    /// <summary>Computes the LTP residual (Appendix A, <c>silk_LTP_analysis_filter_FIX</c>).</summary>
    internal static void LtpAnalysisFilter(
        Span<short> output,
        ReadOnlySpan<short> xBuffer,
        int bufferOffset,
        ReadOnlySpan<short> ltpCoefficientsQ14,
        ReadOnlySpan<int> pitchLags,
        ReadOnlySpan<int> inverseGainsQ16,
        int subframeLength,
        int subframeCount,
        int preLength)
    {
        const int order = SilkEncoderMath.LtpOrder;
        int outputOffset = 0;
        for (int k = 0; k < subframeCount; k++)
        {
            int xOffset = bufferOffset + (k * subframeLength);
            int lagOffset = xOffset - pitchLags[k];
            for (int i = 0; i < subframeLength + preLength; i++)
            {
                int prediction = SilkEncoderMath.Smulbb(
                    xBuffer[lagOffset + i + (order / 2)], ltpCoefficientsQ14[(k * order) + 0]);
                for (int j = 1; j < order; j++)
                {
                    prediction = unchecked(prediction + (
                        xBuffer[lagOffset + i + (order / 2) - j] * ltpCoefficientsQ14[(k * order) + j]));
                }

                prediction = SilkEncoderMath.RshiftRound(prediction, 14);
                int residual = SilkEncoderMath.Sat16(xBuffer[xOffset + i] - prediction);
                output[outputOffset + i] = (short)SilkFixedPoint.Smulwb(inverseGainsQ16[k], residual);
            }

            outputOffset += subframeLength + preLength;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Prediction coefficient pipeline (silk/fixed/find_pred_coefs_FIX.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs the LPC/LTP analysis, NLSF quantization and residual energy measurement.</summary>
    internal static void FindPredictionCoefficients(
        SilkEncoderState encoder,
        SilkEncoderControl control,
        ReadOnlySpan<short> residual,
        ReadOnlySpan<short> xBuffer,
        int frameOffset,
        int conditionalCoding)
    {
        int subframeCount = encoder.SubframeCount;
        int subframeLength = encoder.SubframeLength;
        int order = encoder.PredictLpcOrder;

        Span<int> inverseGainsQ16 = stackalloc int[subframeCount];
        Span<int> localGains = stackalloc int[subframeCount];
        Span<int> weightsQ15 = stackalloc int[subframeCount];
        Span<short> nlsfQ15 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        Span<short> lpcInPre = stackalloc short[(4 * SilkEncoderMath.MaxLpcOrder) + SilkEncoderMath.MaxFrameLength];

        int minimumGainQ16 = SilkEncoderMath.Int32Max >> 6;
        for (int i = 0; i < subframeCount; i++)
        {
            minimumGainQ16 = Math.Min(minimumGainQ16, control.GainsQ16[i]);
        }

        for (int i = 0; i < subframeCount; i++)
        {
            inverseGainsQ16[i] = SilkFixedPoint.Div32VarQ(minimumGainQ16, control.GainsQ16[i], 14);
            inverseGainsQ16[i] = Math.Max(inverseGainsQ16[i], 363);
            int squared = SilkFixedPoint.Smulwb(inverseGainsQ16[i], inverseGainsQ16[i]);
            weightsQ15[i] = squared >> 1;
            localGains[i] = (1 << 16) / inverseGainsQ16[i];
        }

        if (encoder.Indices.SignalType == 2)
        {
            FindLtp(encoder, control, residual, control.PitchL, weightsQ15, out int ltPredCodGainQ7);
            control.LtPredCodGainQ7 = ltPredCodGainQ7;
            LtpScaleCtrl(encoder, control, conditionalCoding);

            LtpAnalysisFilter(
                lpcInPre,
                xBuffer,
                frameOffset - order,
                control.LtpCoefQ14,
                control.PitchL,
                inverseGainsQ16,
                subframeLength,
                subframeCount,
                order);
        }
        else
        {
            int outputOffset = 0;
            int inputOffset = frameOffset - order;
            for (int i = 0; i < subframeCount; i++)
            {
                for (int j = 0; j < subframeLength + order; j++)
                {
                    lpcInPre[outputOffset + j] =
                        (short)SilkFixedPoint.Smulwb(inverseGainsQ16[i], xBuffer[inputOffset + j]);
                }

                outputOffset += subframeLength + order;
                inputOffset += subframeLength;
            }

            control.LtpCoefQ14.AsSpan().Clear();
            control.LtPredCodGainQ7 = 0;
        }

        int minInverseGainQ30;
        if (encoder.FirstFrameAfterReset != 0)
        {
            minInverseGainQ30 = 10737418;
        }
        else
        {
            minInverseGainQ30 = SilkEncoderMath.Log2Lin(
                SilkFixedPoint.Smlawb(16 << 7, control.LtPredCodGainQ7, 21845));
            minInverseGainQ30 = SilkFixedPoint.Div32VarQ(
                minInverseGainQ30,
                SilkFixedPoint.Smulww(
                    10000,
                    SilkFixedPoint.Smlawb(65536, 196608, control.CodingQualityQ14)),
                14);
        }

        FindLpc(encoder, nlsfQ15, lpcInPre, minInverseGainQ30);
        SilkNlsfEncoder.ProcessNlsfs(
            encoder, control.PredCoefQ12[0], control.PredCoefQ12[1], nlsfQ15);

        ResidualEnergy(
            control.ResNrg, control.ResNrgQ, lpcInPre, control.PredCoefQ12, localGains,
            subframeLength, subframeCount, order);

        nlsfQ15.CopyTo(encoder.PrevNlsfQ15);
    }

    /// <summary>Computes the residual energy per subframe (Appendix A, <c>silk_residual_energy_FIX</c>).</summary>
    internal static void ResidualEnergy(
        Span<int> energies,
        Span<int> energyQ,
        ReadOnlySpan<short> x,
        short[][] coefficientsQ12,
        ReadOnlySpan<int> gains,
        int subframeLength,
        int subframeCount,
        int order)
    {
        int offset = order + subframeLength;
        Span<short> lpcResidual = stackalloc short[(SilkEncoderMath.MaxFrameLength + (4 * SilkEncoderMath.MaxLpcOrder)) / 2 + 64];
        int xOffset = 0;
        for (int i = 0; i < subframeCount >> 1; i++)
        {
            SilkEncoderMath.LpcAnalysisFilter(
                lpcResidual, x[xOffset..], coefficientsQ12[i], (4 >> 1) * offset, order);

            int residualOffset = order;
            for (int j = 0; j < 2; j++)
            {
                SilkEncoderMath.SumSqrShift(
                    out int energy, out int shift, lpcResidual.Slice(residualOffset, subframeLength));
                energies[(i * 2) + j] = energy;
                energyQ[(i * 2) + j] = -shift;
                residualOffset += offset;
            }

            xOffset += 2 * offset;
        }

        for (int i = 0; i < subframeCount; i++)
        {
            int leadingZeros1 = SilkEncoderMath.Clz32(energies[i]) - 1;
            int leadingZeros2 = SilkEncoderMath.Clz32(gains[i]) - 1;
            int tmp = SilkEncoderMath.Shl(gains[i], leadingZeros2);
            tmp = SilkFixedPoint.Smmul(tmp, tmp);
            energies[i] = SilkFixedPoint.Smmul(tmp, SilkEncoderMath.Shl(energies[i], leadingZeros1));
            energyQ[i] += leadingZeros1 + (2 * leadingZeros2) - 64;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Prefilter (silk/fixed/prefilter_FIX.c)
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs the noise-shaping prefilter to produce the quantizer input.</summary>
    internal static void Prefilter(
        SilkEncoderState encoder,
        SilkEncoderControl control,
        Span<int> xwQ3,
        ReadOnlySpan<short> xBuffer,
        int frameOffset)
    {
        SilkPrefilterState state = encoder.Prefilt;
        int subframeLength = encoder.SubframeLength;
        int subframeCount = encoder.SubframeCount;
        int order = encoder.ShapingLpcOrder;
        int lag = state.LagPrev;

        Span<int> filteredQ12 = stackalloc int[SilkEncoderMath.MaxSubFrameLength];
        Span<int> shortTermResidualQ2 = stackalloc int[SilkEncoderMath.MaxSubFrameLength + SilkEncoderMath.MaxLpcOrder];
        Span<short> bQ10 = stackalloc short[2];

        for (int k = 0; k < subframeCount; k++)
        {
            if (encoder.Indices.SignalType == 2)
            {
                lag = control.PitchL[k];
            }

            int harmShapeGainQ12 = SilkFixedPoint.Smulwb(
                control.HarmShapeGainQ14[k], 16384 - control.HarmBoostQ14[k]);
            int harmShapeFirPackedQ12 = harmShapeGainQ12 >> 2;
            harmShapeFirPackedQ12 |= (harmShapeGainQ12 >> 1) << 16;
            int tiltQ14 = control.TiltQ14[k];
            int lfShapeQ14 = control.LfShpQ14[k];

            WarpedLpcAnalysisFilter(
                state.SArShp, shortTermResidualQ2, control.Ar1Q13.AsSpan(k * SilkEncoderMath.MaxShapeLpcOrder, order),
                xBuffer[(frameOffset + (k * subframeLength))..], (short)encoder.WarpingQ16, subframeLength, order);

            bQ10[0] = (short)SilkEncoderMath.RshiftRound(control.GainsPreQ14[k], 4);
            int tmp = SilkEncoderMath.Smlabb(3355443, control.HarmBoostQ14[k], harmShapeGainQ12);
            tmp = SilkEncoderMath.Smlabb(tmp, control.CodingQualityQ14, 410);
            tmp = SilkFixedPoint.Smulwb(tmp, -control.GainsPreQ14[k]);
            tmp = SilkEncoderMath.RshiftRound(tmp, 14);
            bQ10[1] = (short)SilkEncoderMath.Sat16(tmp);

            filteredQ12[0] = SilkEncoderMath.Smlabb(
                SilkEncoderMath.Smulbb(shortTermResidualQ2[0], bQ10[0]), state.SHarmHpQ2, bQ10[1]);
            for (int j = 1; j < subframeLength; j++)
            {
                filteredQ12[j] = SilkEncoderMath.Smlabb(
                    SilkEncoderMath.Smulbb(shortTermResidualQ2[j], bQ10[0]), shortTermResidualQ2[j - 1], bQ10[1]);
            }

            state.SHarmHpQ2 = shortTermResidualQ2[subframeLength - 1];

            // Each subframe writes its own slice of the quantizer input (silk_prefilter_FIX
            // advances pxw_Q3 by subfr_length per subframe).
            PrefilterInner(
                state, filteredQ12, xwQ3[(k * subframeLength)..],
                harmShapeFirPackedQ12, tiltQ14, lfShapeQ14, lag, subframeLength);
        }

        state.LagPrev = control.PitchL[subframeCount - 1];
    }

    private static void PrefilterInner(
        SilkPrefilterState state,
        ReadOnlySpan<int> shortTermResidualQ12,
        Span<int> xwQ3,
        int harmShapeFirPackedQ12,
        int tiltQ14,
        int lfShapeQ14,
        int lag,
        int length)
    {
        int bufferIndex = state.SLtpShpBufIdx;
        int lfArShpQ12 = state.SLfArShpQ12;
        int lfMaShpQ12 = state.SLfMaShpQ12;

        for (int i = 0; i < length; i++)
        {
            int ltpQ12;
            if (lag > 0)
            {
                int index = lag + bufferIndex;
                ltpQ12 = SilkEncoderMath.Smulbb(
                    state.SLtpShp[(index - (SilkEncoderMath.HarmShapeFirTaps / 2) - 1) & SilkEncoderMath.LtbMask], harmShapeFirPackedQ12);
                ltpQ12 = SilkEncoderMath.Smlabt(
                    ltpQ12, state.SLtpShp[(index - (SilkEncoderMath.HarmShapeFirTaps / 2)) & SilkEncoderMath.LtbMask], harmShapeFirPackedQ12);
                ltpQ12 = SilkEncoderMath.Smlabb(
                    ltpQ12, state.SLtpShp[(index - (SilkEncoderMath.HarmShapeFirTaps / 2) + 1) & SilkEncoderMath.LtbMask], harmShapeFirPackedQ12);
            }
            else
            {
                ltpQ12 = 0;
            }

            int tiltQ10 = SilkFixedPoint.Smulwb(lfArShpQ12, tiltQ14);
            int lfQ10 = SilkFixedPoint.Smlawb(
                SilkEncoderMath.Smulwt(lfArShpQ12, lfShapeQ14), lfMaShpQ12, lfShapeQ14);

            lfArShpQ12 = unchecked(shortTermResidualQ12[i] - (tiltQ10 << 2));
            lfMaShpQ12 = unchecked(lfArShpQ12 - (lfQ10 << 2));

            bufferIndex = (bufferIndex - 1) & SilkEncoderMath.LtbMask;
            state.SLtpShp[bufferIndex] = (short)SilkEncoderMath.Sat16(SilkEncoderMath.RshiftRound(lfMaShpQ12, 12));

            xwQ3[i] = SilkEncoderMath.RshiftRound(unchecked(lfMaShpQ12 - ltpQ12), 9);
        }

        state.SLfArShpQ12 = lfArShpQ12;
        state.SLfMaShpQ12 = lfMaShpQ12;
        state.SLtpShpBufIdx = bufferIndex;
    }

    /// <summary>The warped LPC analysis filter (Appendix A, <c>silk_warped_LPC_analysis_filter_FIX</c>).</summary>
    internal static void WarpedLpcAnalysisFilter(
        Span<int> state,
        Span<int> residualQ2,
        ReadOnlySpan<short> coefficientsQ13,
        ReadOnlySpan<short> input,
        short warpingQ16,
        int length,
        int order)
    {
        for (int n = 0; n < length; n++)
        {
            int tmp2 = SilkFixedPoint.Smlawb(state[0], state[1], warpingQ16);
            state[0] = input[n] << 14;
            int tmp1 = SilkFixedPoint.Smlawb(state[1], state[2] - tmp2, warpingQ16);
            state[1] = tmp2;
            int accumulatorQ11 = order >> 1;
            accumulatorQ11 = SilkFixedPoint.Smlawb(accumulatorQ11, tmp2, coefficientsQ13[0]);
            for (int i = 2; i < order; i += 2)
            {
                tmp2 = SilkFixedPoint.Smlawb(state[i], state[i + 1] - tmp1, warpingQ16);
                state[i] = tmp1;
                accumulatorQ11 = SilkFixedPoint.Smlawb(accumulatorQ11, tmp1, coefficientsQ13[i - 1]);
                tmp1 = SilkFixedPoint.Smlawb(state[i + 1], state[i + 2] - tmp2, warpingQ16);
                state[i + 1] = tmp2;
                accumulatorQ11 = SilkFixedPoint.Smlawb(accumulatorQ11, tmp2, coefficientsQ13[i]);
            }

            state[order] = tmp1;
            accumulatorQ11 = SilkFixedPoint.Smlawb(accumulatorQ11, tmp1, coefficientsQ13[order - 1]);
            residualQ2[n] = unchecked((input[n] << 2) - SilkEncoderMath.RshiftRound(accumulatorQ11, 9));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Correlation matrix helpers and LDL solve (silk/fixed/corrMatrix_FIX.c, solve_LS_FIX.c)
    // ---------------------------------------------------------------------------------------

    private static void CorrVector(
        ReadOnlySpan<short> x, ReadOnlySpan<short> target, int length, int order, Span<int> output, int rightShifts)
    {
        int pointer = order - 1;
        for (int lag = 0; lag < order; lag++)
        {
            if (rightShifts > 0)
            {
                int inner = 0;
                for (int i = 0; i < length; i++)
                {
                    inner += (x[pointer + i] * target[i]) >> rightShifts;
                }

                output[lag] = inner;
            }
            else
            {
                output[lag] = SilkEncoderMath.InnerProd(x[pointer..], target, length);
            }

            pointer--;
        }
    }

    private static void CorrMatrix(
        ReadOnlySpan<short> x, int length, int order, int headRoom, Span<int> matrix, ref int rightShifts)
    {
        SilkEncoderMath.SumSqrShift(out int energy, out int localShifts, x[..(length + order - 1)]);
        int headRoomShifts = Math.Max(headRoom - SilkEncoderMath.Clz32(energy), 0);
        energy >>= headRoomShifts;
        localShifts += headRoomShifts;

        for (int i = 0; i < order - 1; i++)
        {
            energy -= (x[i] * x[i]) >> localShifts;
        }

        if (localShifts < rightShifts)
        {
            energy >>= rightShifts - localShifts;
            localShifts = rightShifts;
        }

        matrix[0] = energy;
        int pointer1 = order - 1;
        for (int j = 1; j < order; j++)
        {
            energy -= (x[pointer1 + length - j] * x[pointer1 + length - j]) >> localShifts;
            energy += (x[pointer1 - j] * x[pointer1 - j]) >> localShifts;
            matrix[(j * order) + j] = energy;
        }

        int pointer2 = order - 2;
        if (localShifts > 0)
        {
            for (int lag = 1; lag < order; lag++)
            {
                energy = 0;
                for (int i = 0; i < length; i++)
                {
                    energy += (x[pointer1 + i] * x[pointer2 + i]) >> localShifts;
                }

                matrix[(lag * order) + 0] = energy;
                matrix[lag] = energy;
                for (int j = 1; j < order - lag; j++)
                {
                    energy -= (x[pointer1 + length - j] * x[pointer2 + length - j]) >> localShifts;
                    energy += (x[pointer1 - j] * x[pointer2 - j]) >> localShifts;
                    matrix[((lag + j) * order) + j] = energy;
                    matrix[(j * order) + lag + j] = energy;
                }

                pointer2--;
            }
        }
        else
        {
            for (int lag = 1; lag < order; lag++)
            {
                energy = SilkEncoderMath.InnerProd(x[pointer1..], x[pointer2..], length);
                matrix[(lag * order) + 0] = energy;
                matrix[lag] = energy;
                for (int j = 1; j < order - lag; j++)
                {
                    energy -= x[pointer1 + length - j] * x[pointer2 + length - j];
                    energy = SilkEncoderMath.Smlabb(energy, x[pointer1 - j], x[pointer2 - j]);
                    matrix[((lag + j) * order) + j] = energy;
                    matrix[(j * order) + lag + j] = energy;
                }

                pointer2--;
            }
        }

        rightShifts = localShifts;
    }

    private static void SolveLdl(Span<int> a, int size, ReadOnlySpan<int> b, Span<int> xQ16)
    {
        Span<int> lQ16 = stackalloc int[SilkEncoderMath.MaxLpcOrder * SilkEncoderMath.MaxLpcOrder];
        Span<int> y = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> inverseQ36 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> inverseQ48 = stackalloc int[SilkEncoderMath.MaxLpcOrder];

        LdlFactorize(a, size, lQ16, inverseQ36, inverseQ48);

        for (int i = 0; i < size; i++)
        {
            int inner = 0;
            for (int j = 0; j < i; j++)
            {
                inner = SilkFixedPoint.Smlaww(inner, lQ16[(i * size) + j], y[j]);
            }

            y[i] = unchecked(b[i] - inner);
        }

        for (int i = 0; i < size; i++)
        {
            int value = y[i];
            y[i] = unchecked(SilkFixedPoint.Smmul(value, inverseQ48[i])
                + ((SilkFixedPoint.Smulww(value, inverseQ36[i])) >> 4));
        }

        for (int i = size - 1; i >= 0; i--)
        {
            int inner = 0;
            for (int j = size - 1; j > i; j--)
            {
                inner = SilkFixedPoint.Smlaww(inner, lQ16[(j * size) + i], xQ16[j]);
            }

            xQ16[i] = unchecked(y[i] - inner);
        }
    }

    private static void LdlFactorize(
        Span<int> a, int size, Span<int> lQ16, Span<int> inverseQ36, Span<int> inverseQ48)
    {
        Span<int> vQ0 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        Span<int> dQ0 = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        int status = 1;
        int diagonalMinimum = Math.Max(
            SilkFixedPoint.Smmul(
                SilkEncoderMath.AddSat32(a[0], a[(size * size) - 1]), 21475),
            1 << 9);

        for (int loop = 0; loop < size && status == 1; loop++)
        {
            status = 0;
            for (int j = 0; j < size; j++)
            {
                int row = j * size;
                int temporary = 0;
                for (int i = 0; i < j; i++)
                {
                    vQ0[i] = SilkFixedPoint.Smulww(dQ0[i], lQ16[row + i]);
                    temporary = SilkFixedPoint.Smlaww(temporary, vQ0[i], lQ16[row + i]);
                }

                temporary = unchecked(a[(j * size) + j] - temporary);
                if (temporary < diagonalMinimum)
                {
                    temporary = unchecked(((loop + 1) * diagonalMinimum) - temporary);
                    for (int i = 0; i < size; i++)
                    {
                        a[(i * size) + i] = unchecked(a[(i * size) + i] + temporary);
                    }

                    status = 1;
                    break;
                }

                dQ0[j] = temporary;
                int inverse36 = SilkFixedPoint.Inverse32VarQ(temporary, 36);
                int inverse40 = inverse36 << 4;
                int error = unchecked((1 << 24) - SilkFixedPoint.Smulww(temporary, inverse40));
                int inverse48 = SilkFixedPoint.Smulww(error, inverse40);
                inverseQ36[j] = inverse36;
                inverseQ48[j] = inverse48;
                lQ16[(j * size) + j] = 65536;

                int column = (j + 1) * size;
                for (int i = j + 1; i < size; i++)
                {
                    temporary = 0;
                    for (int k = 0; k < j; k++)
                    {
                        temporary = SilkFixedPoint.Smlaww(temporary, vQ0[k], lQ16[column + k]);
                    }

                    temporary = unchecked(a[(j * size) + i] - temporary);
                    lQ16[(i * size) + j] = unchecked(
                        SilkFixedPoint.Smmul(temporary, inverse48)
                        + ((SilkFixedPoint.Smulww(temporary, inverse36)) >> 4));
                    column += size;
                }
            }
        }
    }

    private static int ResidualEnergy16Covar(
        ReadOnlySpan<short> c, ReadOnlySpan<int> wxx, ReadOnlySpan<int> wxX, int wxxValue, int dimension, int cQ)
    {
        int leftShifts = 16 - cQ;
        int extra = leftShifts;
        int maximum = 0;
        for (int i = 0; i < dimension; i++)
        {
            maximum = Math.Max(maximum, SilkEncoderMath.Abs(c[i]));
        }

        extra = Math.Min(extra, SilkEncoderMath.Clz32(maximum) - 17);
        int wMax = Math.Max(wxx[0], wxx[(dimension * dimension) - 1]);
        extra = Math.Min(extra, SilkEncoderMath.Clz32(dimension * (SilkFixedPoint.Smulwb(wMax, maximum) >> 4)) - 5);
        extra = Math.Max(extra, 0);

        Span<int> cn = stackalloc int[SilkEncoderMath.MaxLpcOrder];
        for (int i = 0; i < dimension; i++)
        {
            cn[i] = c[i] << extra;
        }

        leftShifts -= extra;
        int temporary = 0;
        for (int i = 0; i < dimension; i++)
        {
            temporary = SilkFixedPoint.Smlawb(temporary, wxX[i], cn[i]);
        }

        int nrg = (wxxValue >> (1 + leftShifts)) - temporary;
        int temporary2 = 0;
        for (int i = 0; i < dimension; i++)
        {
            temporary = 0;
            int row = i * dimension;
            for (int j = i + 1; j < dimension; j++)
            {
                temporary = SilkFixedPoint.Smlawb(temporary, wxx[row + j], cn[j]);
            }

            temporary = SilkFixedPoint.Smlawb(temporary, wxx[row + i] >> 1, cn[i]);
            temporary2 = SilkFixedPoint.Smlawb(temporary2, temporary, cn[i]);
        }

        nrg = SilkEncoderMath.AddLshift(nrg, temporary2, leftShifts);
        if (nrg < 1)
        {
            return 1;
        }

        if (nrg > (SilkEncoderMath.Int32Max >> (leftShifts + 2)))
        {
            return SilkEncoderMath.Int32Max >> 1;
        }

        return nrg << (leftShifts + 1);
    }

    // ---------------------------------------------------------------------------------------
    // Warped autocorrelation and warped coefficient limits
    // ---------------------------------------------------------------------------------------

    /// <summary>Computes warped autocorrelations (Appendix A, <c>silk_warped_autocorrelation_FIX</c>).</summary>
    internal static void WarpedAutocorrelation(
        Span<int> output, out int scale, ReadOnlySpan<short> input, int warpingQ16, int length, int order)
    {
        const int qc = 10;
        const int qs = 14;
        Span<int> state = stackalloc int[SilkEncoderMath.MaxShapeLpcOrder + 1];
        Span<long> correlation = stackalloc long[SilkEncoderMath.MaxShapeLpcOrder + 1];
        state.Clear();
        correlation.Clear();

        for (int n = 0; n < length; n++)
        {
            int tmp1 = input[n] << qs;
            for (int i = 0; i < order; i += 2)
            {
                int tmp2 = SilkFixedPoint.Smlawb(state[i], state[i + 1] - tmp1, warpingQ16);
                state[i] = tmp1;
                correlation[i] += ((long)tmp1 * state[0]) >> (2 * qs - qc);
                tmp1 = SilkFixedPoint.Smlawb(state[i + 1], state[i + 2] - tmp2, warpingQ16);
                state[i + 1] = tmp2;
                correlation[i + 1] += ((long)tmp2 * state[0]) >> (2 * qs - qc);
            }

            state[order] = tmp1;
            correlation[order] += ((long)tmp1 * state[0]) >> (2 * qs - qc);
        }

        int leftShift = SilkEncoderMath.Clz64(correlation[0]) - 35;
        leftShift = SilkEncoderMath.Limit(leftShift, -12 - qc, 30 - qc);
        scale = -(qc + leftShift);
        for (int i = 0; i < order + 1; i++)
        {
            output[i] = leftShift >= 0
                ? (int)(correlation[i] << leftShift)
                : (int)(correlation[i] >> -leftShift);
        }
    }

    private static int Schur64(Span<int> reflectionQ16, ReadOnlySpan<int> correlations, int order)
    {
        if (correlations[0] <= 0)
        {
            reflectionQ16[..order].Clear();
            return 0;
        }

        Span<int> c0 = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];
        Span<int> c1 = stackalloc int[SilkEncoderMath.MaxLpcOrder + 1];
        for (int k = 0; k <= order; k++)
        {
            c0[k] = correlations[k];
            c1[k] = correlations[k];
        }

        for (int k = 0; k < order; k++)
        {
            int reflectionQ31 = SilkFixedPoint.Div32VarQ(-c0[k + 1], c1[0], 31);
            reflectionQ16[k] = SilkEncoderMath.RshiftRound(reflectionQ31, 15);
            for (int n = 0; n < order - k; n++)
            {
                int tmp1 = c0[n + k + 1];
                int tmp2 = c1[n];
                c0[n + k + 1] = unchecked(tmp1 + SilkFixedPoint.Smmul(tmp2 << 1, reflectionQ31));
                c1[n] = unchecked(tmp2 + SilkFixedPoint.Smmul(tmp1 << 1, reflectionQ31));
            }
        }

        return c1[0];
    }

    private static int WarpedGain(ReadOnlySpan<int> coefficientsQ24, int lambdaQ16, int order)
    {
        lambdaQ16 = -lambdaQ16;
        int gainQ24 = coefficientsQ24[order - 1];
        for (int i = order - 2; i >= 0; i--)
        {
            gainQ24 = SilkFixedPoint.Smlawb(coefficientsQ24[i], gainQ24, lambdaQ16);
        }

        gainQ24 = SilkFixedPoint.Smlawb(1 << 24, gainQ24, -lambdaQ16);
        return SilkFixedPoint.Inverse32VarQ(gainQ24, 40);
    }

    private static void LimitWarpedCoefficients(
        Span<int> synthesisQ24, Span<int> analysisQ24, int lambdaQ16, int limitQ24, int order)
    {
        lambdaQ16 = -lambdaQ16;
        for (int i = order - 1; i > 0; i--)
        {
            synthesisQ24[i - 1] = SilkFixedPoint.Smlawb(synthesisQ24[i - 1], synthesisQ24[i], lambdaQ16);
            analysisQ24[i - 1] = SilkFixedPoint.Smlawb(analysisQ24[i - 1], analysisQ24[i], lambdaQ16);
        }

        lambdaQ16 = -lambdaQ16;
        int numeratorQ16 = SilkFixedPoint.Smlawb(1 << 16, -lambdaQ16, lambdaQ16);
        int denominatorQ24 = SilkFixedPoint.Smlawb(1 << 24, synthesisQ24[0], lambdaQ16);
        int synthesisGainQ16 = SilkFixedPoint.Div32VarQ(numeratorQ16, denominatorQ24, 24);
        denominatorQ24 = SilkFixedPoint.Smlawb(1 << 24, analysisQ24[0], lambdaQ16);
        int analysisGainQ16 = SilkFixedPoint.Div32VarQ(numeratorQ16, denominatorQ24, 24);
        for (int i = 0; i < order; i++)
        {
            synthesisQ24[i] = SilkFixedPoint.Smulww(synthesisGainQ16, synthesisQ24[i]);
            analysisQ24[i] = SilkFixedPoint.Smulww(analysisGainQ16, analysisQ24[i]);
        }

        for (int iteration = 0; iteration < 10; iteration++)
        {
            int maximum = -1;
            int index = 0;
            for (int i = 0; i < order; i++)
            {
                int tmp = Math.Max(SilkEncoderMath.Abs(synthesisQ24[i]), SilkEncoderMath.Abs(analysisQ24[i]));
                if (tmp > maximum)
                {
                    maximum = tmp;
                    index = i;
                }
            }

            if (maximum <= limitQ24)
            {
                return;
            }

            for (int i = 1; i < order; i++)
            {
                synthesisQ24[i - 1] = SilkFixedPoint.Smlawb(synthesisQ24[i - 1], synthesisQ24[i], lambdaQ16);
                analysisQ24[i - 1] = SilkFixedPoint.Smlawb(analysisQ24[i - 1], analysisQ24[i], lambdaQ16);
            }

            synthesisGainQ16 = SilkFixedPoint.Inverse32VarQ(synthesisGainQ16, 32);
            analysisGainQ16 = SilkFixedPoint.Inverse32VarQ(analysisGainQ16, 32);
            for (int i = 0; i < order; i++)
            {
                synthesisQ24[i] = SilkFixedPoint.Smulww(synthesisGainQ16, synthesisQ24[i]);
                analysisQ24[i] = SilkFixedPoint.Smulww(analysisGainQ16, analysisQ24[i]);
            }

            int chirpQ16 = 64881 - SilkFixedPoint.Div32VarQ(
                SilkFixedPoint.Smulwb(maximum - limitQ24, SilkEncoderMath.Smlabb(819, 102, iteration)),
                maximum * (index + 1), 22);
            SilkEncoderMath.Bwexpander32(synthesisQ24, order, chirpQ16);
            SilkEncoderMath.Bwexpander32(analysisQ24, order, chirpQ16);

            lambdaQ16 = -lambdaQ16;
            for (int i = order - 1; i > 0; i--)
            {
                synthesisQ24[i - 1] = SilkFixedPoint.Smlawb(synthesisQ24[i - 1], synthesisQ24[i], lambdaQ16);
                analysisQ24[i - 1] = SilkFixedPoint.Smlawb(analysisQ24[i - 1], analysisQ24[i], lambdaQ16);
            }

            lambdaQ16 = -lambdaQ16;
            numeratorQ16 = SilkFixedPoint.Smlawb(1 << 16, -lambdaQ16, lambdaQ16);
            denominatorQ24 = SilkFixedPoint.Smlawb(1 << 24, synthesisQ24[0], lambdaQ16);
            synthesisGainQ16 = SilkFixedPoint.Div32VarQ(numeratorQ16, denominatorQ24, 24);
            denominatorQ24 = SilkFixedPoint.Smlawb(1 << 24, analysisQ24[0], lambdaQ16);
            analysisGainQ16 = SilkFixedPoint.Div32VarQ(numeratorQ16, denominatorQ24, 24);
            for (int i = 0; i < order; i++)
            {
                synthesisQ24[i] = SilkFixedPoint.Smulww(synthesisGainQ16, synthesisQ24[i]);
                analysisQ24[i] = SilkFixedPoint.Smulww(analysisGainQ16, analysisQ24[i]);
            }
        }
    }

    private static int LpcInversePredGainQ24(ReadOnlySpan<int> coefficientsQ24, int order)
    {
        Span<short> q12 = stackalloc short[SilkEncoderMath.MaxLpcOrder];
        for (int i = 0; i < order; i++)
        {
            q12[i] = (short)(coefficientsQ24[i] >> 12);
        }

        return SilkEncoderMath.LpcInversePredGain(q12, order);
    }
}
