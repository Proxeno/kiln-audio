// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Silk;

/// <summary>
/// The SILK stereo mixer and stereo-prediction quantizer (RFC 6716 §5.2.2, encoder side of
/// §4.2.7.1 and §4.2.7.2).
/// </summary>
/// <remarks>
/// <para>
/// Transcribed from the Appendix A sources <c>silk/stereo_LR_to_MS.c</c>,
/// <c>silk/stereo_find_predictor.c</c>, <c>silk/stereo_quant_pred.c</c> and
/// <c>silk/stereo_encode_pred.c</c>. The mid/side conversion, the smoothed prediction weights,
/// the adaptive stereo width and the mid-only decision all keep the reference's exact fixed-point
/// arithmetic, so the quantized indices decode through the existing decoder
/// (<see cref="SilkDecoder"/>) to the same weights this module reports.
/// </para>
/// <para>
/// Scratch buffers are allocated once in the constructor; <see cref="Analyze"/> and the encoder
/// helpers do not allocate. All loop-shaped work is written over spans so the same code serves
/// every frame length.
/// </para>
/// <para>
/// The reference keeps a two-sample mid and side history in front of the frame and writes the
/// predicted side signal one sample late (its <c>x2[n-1]</c>). <see cref="Analyze"/> reproduces
/// that layout exactly: <c>mid[i]</c> is the reference's <c>x1[i]</c> and <c>side[i]</c> is its
/// <c>x2[i]</c>, including the final slot that the reference leaves untouched.
/// </para>
/// </remarks>
internal sealed class SilkStereoEncoder
{
    /// <summary>Number of interpolation samples at the start of a frame (8 ms, RFC 6716 §5.2.2).</summary>
    internal const int InterpolationLengthMs = 8;

    /// <summary>Number of samples of shape look-ahead (5 ms, Appendix A <c>LA_SHAPE_MS</c>).</summary>
    private const int ShapeLookaheadMs = 5;

    /// <summary>Longest internal frame, 20 ms at 16 kHz (Appendix A <c>MAX_FRAME_LENGTH</c>).</summary>
    private const int MaxFrameLength = 320;

    /// <summary>Number of quantization substeps between two prediction table entries.</summary>
    private const int QuantSubSteps = 5;

    private const int SmoothCoefQ16 = 655;      // SILK_FIX_CONST(0.01, 16)
    private const int SmoothCoef10MsQ16 = 328;  // SILK_FIX_CONST(0.005, 16)
    private const int WidthFullQ14 = 16384;     // SILK_FIX_CONST(1, 14)
    private const int WidthFullThresholdQ14 = 15565; // SILK_FIX_CONST(0.95, 14)
    private const int WidthNearZeroQ14 = 819;   // SILK_FIX_CONST(0.05, 14)
    private const int WidthZeroQ14 = 328;       // SILK_FIX_CONST(0.02, 14)

    // Two samples of history in front of the current frame, exactly as the reference indexes
    // mid[-2..frame_length-1] through its input buffer.
    private readonly short[] _mid = new short[MaxFrameLength + 2];
    private readonly short[] _side = new short[MaxFrameLength + 2];
    private readonly short[] _lpMid = new short[MaxFrameLength];
    private readonly short[] _hpMid = new short[MaxFrameLength];
    private readonly short[] _lpSide = new short[MaxFrameLength];
    private readonly short[] _hpSide = new short[MaxFrameLength];

    // Persistent state (Appendix A stereo_enc_state).
    private readonly short[] _stateMid = new short[2];
    private readonly short[] _stateSide = new short[2];
    private readonly int[] _midSideAmpQ0 = [0, 1, 0, 1];
    private readonly short[] _predPrevQ13 = new short[2];
    private short _smthWidthQ14 = WidthFullQ14;
    private short _widthPrevQ14;
    private short _silentSideLen;

    /// <summary>Returns the encoder to its initial state (Appendix A <c>silk_init_encoder</c>).</summary>
    public void Reset()
    {
        _stateMid.AsSpan().Clear();
        _stateSide.AsSpan().Clear();
        _midSideAmpQ0[0] = 0;
        _midSideAmpQ0[1] = 1;
        _midSideAmpQ0[2] = 0;
        _midSideAmpQ0[3] = 1;
        _predPrevQ13[0] = 0;
        _predPrevQ13[1] = 0;
        _smthWidthQ14 = WidthFullQ14;
        _widthPrevQ14 = 0;
        _silentSideLen = 0;
    }

    /// <summary>
    /// Converts one frame of left/right stereo into the adaptive mid/side representation and
    /// quantizes the stereo prediction weights (RFC 6716 §5.2.2).
    /// </summary>
    /// <param name="left">The left input frame.</param>
    /// <param name="right">The right input frame, the same length as <paramref name="left"/>.</param>
    /// <param name="fsKHz">The internal SILK sample rate in kHz (8, 12 or 16).</param>
    /// <param name="totalRateBps">The total bitrate budget in bits per second.</param>
    /// <param name="previousSpeechActivityQ8">The previous frame's speech activity, Q8.</param>
    /// <param name="toMono">True for the last frame before a stereo to mono transition.</param>
    /// <param name="mid">Receives the mid signal; the SILK core encodes it as channel 0.</param>
    /// <param name="side">Receives the residual side signal; channel 1.</param>
    /// <param name="predictionIndices">Receives the six quantization indices, in <c>[channel][0..2]</c> order.</param>
    /// <param name="midOnly">Receives the mid-only flag (RFC 6716 §4.2.7.2).</param>
    /// <param name="midRateBps">Receives the mid channel bitrate.</param>
    /// <param name="sideRateBps">Receives the side channel bitrate; zero when coding mid only.</param>
    /// <exception cref="ArgumentException">A span is shorter than the frame or the rate is out of range.</exception>
    public void Analyze(
        ReadOnlySpan<short> left,
        ReadOnlySpan<short> right,
        int fsKHz,
        int totalRateBps,
        int previousSpeechActivityQ8,
        bool toMono,
        Span<short> mid,
        Span<short> side,
        Span<byte> predictionIndices,
        out bool midOnly,
        out int midRateBps,
        out int sideRateBps)
    {
        if (fsKHz is not (8 or 12 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(fsKHz), fsKHz, "The internal rate must be 8, 12 or 16 kHz.");
        }

        int frameLength = fsKHz * 20;
        if (left.Length < frameLength || right.Length < frameLength)
        {
            throw new ArgumentException("The input frames are shorter than 20 ms.", nameof(left));
        }

        if (mid.Length < frameLength || side.Length < frameLength)
        {
            throw new ArgumentException("The output frames are shorter than 20 ms.", nameof(mid));
        }

        if (predictionIndices.Length < 6)
        {
            throw new ArgumentException("The prediction index buffer needs six entries.", nameof(predictionIndices));
        }

        // M[n] and S[n] mirror the reference's mid[n] and side[n]. Entries 0 and 1 are the
        // two-sample history; the current frame lands at 2..frameLength+1.
        Span<short> midSig = _mid.AsSpan(0, frameLength + 2);
        Span<short> sideSig = _side.AsSpan(0, frameLength + 2);

        for (int i = 0; i < frameLength; i++)
        {
            midSig[i + 2] = (short)SilkFixedPoint.RshiftRound(left[i] + right[i], 1);
            sideSig[i + 2] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(left[i] - right[i], 1));
        }

        _stateMid.AsSpan().CopyTo(midSig);
        _stateSide.AsSpan().CopyTo(sideSig);
        midSig.Slice(frameLength, 2).CopyTo(_stateMid);
        sideSig.Slice(frameLength, 2).CopyTo(_stateSide);

        // LP and HP filter of the mid and side signals.
        Span<short> lpMid = _lpMid.AsSpan(0, frameLength);
        Span<short> hpMid = _hpMid.AsSpan(0, frameLength);
        Span<short> lpSide = _lpSide.AsSpan(0, frameLength);
        Span<short> hpSide = _hpSide.AsSpan(0, frameLength);
        for (int n = 0; n < frameLength; n++)
        {
            int sum = SilkFixedPoint.RshiftRound(
                midSig[n] + midSig[n + 2] + (midSig[n + 1] << 1), 2);
            lpMid[n] = (short)sum;
            hpMid[n] = (short)(midSig[n + 1] - sum);

            sum = SilkFixedPoint.RshiftRound(
                sideSig[n] + sideSig[n + 2] + (sideSig[n + 1] << 1), 2);
            lpSide[n] = (short)sum;
            hpSide[n] = (short)(sideSig[n + 1] - sum);
        }

        // Find energies and predictors.
        bool is10MsFrame = frameLength == 10 * fsKHz;
        int smoothCoefQ16 = is10MsFrame ? SmoothCoef10MsQ16 : SmoothCoefQ16;
        smoothCoefQ16 = SilkFixedPoint.Smulwb(
            SilkFixedPoint.Smulbb(previousSpeechActivityQ8, previousSpeechActivityQ8), smoothCoefQ16);

        int pred0Q13 = FindPredictor(
            lpMid, lpSide, _midSideAmpQ0.AsSpan(0, 2), frameLength, smoothCoefQ16, out int lpRatioQ14);
        int pred1Q13 = FindPredictor(
            hpMid, hpSide, _midSideAmpQ0.AsSpan(2, 2), frameLength, smoothCoefQ16, out int hpRatioQ14);

        // Ratio of the norms of the residual and mid signals.
        int fracQ16 = SilkFixedPoint.MinInt(
            SilkFixedPoint.Smlawb(hpRatioQ14, lpRatioQ14, 3), 1 << 16);

        // Distribute the bitrate between mid and side, reducing stereo width if the mid rate is low.
        totalRateBps -= is10MsFrame ? 1200 : 600;
        if (totalRateBps < 1)
        {
            totalRateBps = 1;
        }

        int minMidRateBps = 2000 + (fsKHz * 900);
        int frac3Q16 = 3 * fracQ16;
        int widthQ14;
        midRateBps = SilkFixedPoint.Div32VarQ(totalRateBps, ((8 + 5) * 65536) + frac3Q16, 19);
        if (midRateBps < minMidRateBps)
        {
            midRateBps = minMidRateBps;
            sideRateBps = totalRateBps - midRateBps;
            widthQ14 = SilkFixedPoint.Div32VarQ(
                (sideRateBps << 1) - minMidRateBps,
                SilkFixedPoint.Smulwb(65536 + frac3Q16, minMidRateBps), 16);
            widthQ14 = SilkFixedPoint.Limit(widthQ14, 0, WidthFullQ14);
        }
        else
        {
            sideRateBps = totalRateBps - midRateBps;
            widthQ14 = WidthFullQ14;
        }

        _smthWidthQ14 = (short)SilkFixedPoint.Smlawb(
            _smthWidthQ14, widthQ14 - _smthWidthQ14, smoothCoefQ16);

        // At very low bitrates or for near amplitude-panned inputs, switch to panned-mono coding.
        midOnly = false;
        if (toMono)
        {
            widthQ14 = 0;
            pred0Q13 = 0;
            pred1Q13 = 0;
            QuantizePredictors(ref pred0Q13, ref pred1Q13, predictionIndices);
        }
        else if (_widthPrevQ14 == 0
            && (8 * totalRateBps < 13 * minMidRateBps
                || SilkFixedPoint.Smulwb(fracQ16, _smthWidthQ14) < WidthNearZeroQ14))
        {
            // Code as panned-mono; the previous frame already had zero width.
            pred0Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred0Q13), 14);
            pred1Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred1Q13), 14);
            QuantizePredictors(ref pred0Q13, ref pred1Q13, predictionIndices);
            widthQ14 = 0;
            midOnly = true;
        }
        else if (_widthPrevQ14 != 0
            && (8 * totalRateBps < 11 * minMidRateBps
                || SilkFixedPoint.Smulwb(fracQ16, _smthWidthQ14) < WidthZeroQ14))
        {
            // Transition to zero-width stereo.
            pred0Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred0Q13), 14);
            pred1Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred1Q13), 14);
            QuantizePredictors(ref pred0Q13, ref pred1Q13, predictionIndices);
            widthQ14 = 0;
        }
        else if (_smthWidthQ14 > WidthFullThresholdQ14)
        {
            // Full-width stereo coding.
            QuantizePredictors(ref pred0Q13, ref pred1Q13, predictionIndices);
            widthQ14 = WidthFullQ14;
        }
        else
        {
            // Reduced-width stereo coding.
            pred0Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred0Q13), 14);
            pred1Q13 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulbb(_smthWidthQ14, pred1Q13), 14);
            QuantizePredictors(ref pred0Q13, ref pred1Q13, predictionIndices);
            widthQ14 = _smthWidthQ14;
        }

        // Keep encoding the tapered output until it has been transmitted.
        if (midOnly)
        {
            _silentSideLen += (short)(frameLength - (InterpolationLengthMs * fsKHz));
            if (_silentSideLen < ShapeLookaheadMs * fsKHz)
            {
                midOnly = false;
            }
            else
            {
                _silentSideLen = 10000;
            }
        }
        else
        {
            _silentSideLen = 0;
        }

        if (midOnly)
        {
            midRateBps = totalRateBps;
            sideRateBps = 0;
        }
        else if (sideRateBps < 1)
        {
            sideRateBps = 1;
            midRateBps = SilkFixedPoint.MaxInt(1, totalRateBps - sideRateBps);
        }

        // Interpolate the predictors over 8 ms and subtract the prediction from the side signal.
        int pred0Interp = -_predPrevQ13[0];
        int pred1Interp = -_predPrevQ13[1];
        int wQ24 = _widthPrevQ14 << 10;
        int denomQ16 = 65536 / (InterpolationLengthMs * fsKHz);
        int delta0Q13 = -SilkFixedPoint.RshiftRound(
            SilkFixedPoint.Smulbb(pred0Q13 - _predPrevQ13[0], denomQ16), 16);
        int delta1Q13 = -SilkFixedPoint.RshiftRound(
            SilkFixedPoint.Smulbb(pred1Q13 - _predPrevQ13[1], denomQ16), 16);
        int deltaWQ24 = SilkFixedPoint.Lshift(
            SilkFixedPoint.Smulwb(widthQ14 - _widthPrevQ14, denomQ16), 10);
        int interpLength = InterpolationLengthMs * fsKHz;

        // The reference writes x2[n-1], so the first prediction (n = 0) is dropped.
        side[frameLength - 1] = right[frameLength - 1];
        for (int n = 0; n < frameLength; n++)
        {
            if (n < interpLength)
            {
                pred0Interp += delta0Q13;
                pred1Interp += delta1Q13;
                wQ24 += deltaWQ24;
            }
            else if (n == interpLength)
            {
                pred0Interp = -pred0Q13;
                pred1Interp = -pred1Q13;
                wQ24 = widthQ14 << 10;
            }

            int predicted = PredictSide(midSig, sideSig, n, wQ24, pred0Interp, pred1Interp);
            if (n > 0)
            {
                side[n - 1] = (short)predicted;
            }
        }

        _predPrevQ13[0] = (short)pred0Q13;
        _predPrevQ13[1] = (short)pred1Q13;
        _widthPrevQ14 = (short)widthQ14;

        midSig.Slice(2, frameLength).CopyTo(mid);
    }

    /// <summary>
    /// Encodes the stereo prediction indices (Appendix A <c>silk_stereo_encode_pred</c>,
    /// RFC 6716 §4.2.7.1).
    /// </summary>
    /// <param name="enc">The range encoder for this Opus frame.</param>
    /// <param name="predictionIndices">The six indices from <see cref="Analyze"/>.</param>
    public static void EncodePrediction(ref RangeEncoder enc, ReadOnlySpan<byte> predictionIndices)
    {
        int n = (5 * predictionIndices[2]) + predictionIndices[5];
        EncodePdf(ref enc, SilkTables.StereoPredJointPdf, n);
        EncodePdf(ref enc, SilkTables.StereoPredStage2Pdf, predictionIndices[0]);
        EncodePdf(ref enc, SilkTables.StereoPredStage3Pdf, predictionIndices[1]);
        EncodePdf(ref enc, SilkTables.StereoPredStage2Pdf, predictionIndices[3]);
        EncodePdf(ref enc, SilkTables.StereoPredStage3Pdf, predictionIndices[4]);
    }

    /// <summary>Encodes the mid-only flag (Appendix A <c>silk_stereo_encode_mid_only</c>).</summary>
    /// <param name="enc">The range encoder for this Opus frame.</param>
    /// <param name="midOnly">The flag from <see cref="Analyze"/>.</param>
    public static void EncodeMidOnly(ref RangeEncoder enc, bool midOnly) =>
        EncodePdf(ref enc, SilkTables.MidOnlyPdf, midOnly ? 1 : 0);

    /// <summary>
    /// Quantizes the two prediction weights (Appendix A <c>silk_stereo_quant_pred</c>).
    /// </summary>
    /// <param name="pred0Q13">The low-pass predictor, Q13; returns the quantized value minus the second.</param>
    /// <param name="pred1Q13">The high-pass predictor, Q13; returns the quantized value.</param>
    /// <param name="indices">Receives the six indices used by <see cref="EncodePrediction"/>.</param>
    /// <remarks>
    /// The returned weights are exactly what the decoder reconstructs from the same indices
    /// (RFC 6716 §4.2.7.1), including the first weight being stored relative to the second.
    /// </remarks>
    internal static void QuantizePredictors(ref int pred0Q13, ref int pred1Q13, Span<byte> indices)
    {
        Span<int> pred = [pred0Q13, pred1Q13];
        for (int channel = 0; channel < 2; channel++)
        {
            int errorMinQ13 = SilkFixedPoint.Int32Max;
            int quantizedQ13 = 0;
            bool done = false;
            for (int i = 0; i < SilkTables.StereoWeightQ13.Length - 1 && !done; i++)
            {
                int lowQ13 = SilkTables.StereoWeightQ13[i];
                int stepQ13 = SilkFixedPoint.Smulwb(
                    SilkTables.StereoWeightQ13[i + 1] - lowQ13, 6554); // SILK_FIX_CONST(0.1, 16)
                for (int j = 0; j < QuantSubSteps; j++)
                {
                    int levelQ13 = SilkFixedPoint.Add(lowQ13, SilkFixedPoint.Smulbb(stepQ13, (2 * j) + 1));
                    int errorQ13 = SilkFixedPoint.Abs(pred[channel] - levelQ13);
                    if (errorQ13 < errorMinQ13)
                    {
                        errorMinQ13 = errorQ13;
                        quantizedQ13 = levelQ13;
                        indices[channel * 3] = (byte)i;
                        indices[(channel * 3) + 1] = (byte)j;
                    }
                    else
                    {
                        // The error is increasing, so the search has passed the optimum.
                        done = true;
                        break;
                    }
                }
            }

            indices[(channel * 3) + 2] = (byte)(indices[channel * 3] / 3);
            indices[channel * 3] = (byte)(indices[channel * 3] - (3 * indices[(channel * 3) + 2]));
            pred[channel] = quantizedQ13;
        }

        // The first weight is stored relative to the second, which helps when applying them.
        pred0Q13 = pred[0] - pred[1];
        pred1Q13 = pred[1];
    }

    private static int PredictSide(
        ReadOnlySpan<short> midSig, ReadOnlySpan<short> sideSig, int n, int wQ24, int pred0Q13, int pred1Q13)
    {
        int sum = SilkFixedPoint.Lshift(midSig[n] + midSig[n + 2] + (midSig[n + 1] << 1), 9); // Q11
        sum = SilkFixedPoint.Smlawb(SilkFixedPoint.Smulwb(wQ24, sideSig[n + 1]), sum, pred0Q13); // Q8
        sum = SilkFixedPoint.Smlawb(sum, midSig[n + 1] << 11, pred1Q13); // Q8
        return SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(sum, 8));
    }

    /// <summary>Finds the least-squares prediction gain and updates the smoothed norms.</summary>
    private static int FindPredictor(
        ReadOnlySpan<short> x,
        ReadOnlySpan<short> y,
        Span<int> midResAmpQ0,
        int length,
        int smoothCoefQ16,
        out int ratioQ14)
    {
        SumSqrShift(x, out int nrgx, out int scale1);
        SumSqrShift(y, out int nrgy, out int scale2);
        int scale = SilkFixedPoint.MaxInt(scale1, scale2);
        scale += scale & 1;
        nrgy = SilkFixedPoint.Rshift(nrgy, scale - scale2);
        nrgx = SilkFixedPoint.Rshift(nrgx, scale - scale1);
        nrgx = SilkFixedPoint.MaxInt(nrgx, 1);
        int corr = InnerProductAlignedScale(x, y, scale, length);
        int predQ13 = SilkFixedPoint.Div32VarQ(corr, nrgx, 13);
        predQ13 = SilkFixedPoint.Limit(predQ13, -(1 << 14), 1 << 14);
        int pred2Q10 = SilkFixedPoint.Smulwb(predQ13, predQ13);

        // Faster update for signals with large prediction parameters.
        smoothCoefQ16 = SilkFixedPoint.MaxInt(smoothCoefQ16, SilkFixedPoint.Abs(pred2Q10));

        scale >>= 1;
        midResAmpQ0[0] = SilkFixedPoint.Smlawb(
            midResAmpQ0[0],
            SilkFixedPoint.Lshift(SilkFixedPoint.SqrtApprox(nrgx), scale) - midResAmpQ0[0],
            smoothCoefQ16);

        // Residual energy = nrgy - 2 * pred * corr + pred^2 * nrgx.
        nrgy = SilkFixedPoint.Sub(nrgy, SilkFixedPoint.Smulwb(corr, predQ13) << 4);
        nrgy = SilkFixedPoint.Add(nrgy, SilkFixedPoint.Smulwb(nrgx, pred2Q10) << 6);
        midResAmpQ0[1] = SilkFixedPoint.Smlawb(
            midResAmpQ0[1],
            SilkFixedPoint.Lshift(SilkFixedPoint.SqrtApprox(nrgy), scale) - midResAmpQ0[1],
            smoothCoefQ16);

        ratioQ14 = SilkFixedPoint.Div32VarQ(
            midResAmpQ0[1], SilkFixedPoint.MaxInt(midResAmpQ0[0], 1), 14);
        ratioQ14 = SilkFixedPoint.Limit(ratioQ14, 0, 32767);
        return predQ13;
    }

    /// <summary>The sum of squares of a 16-bit vector, shifted to fit in 32 bits.</summary>
    private static void SumSqrShift(ReadOnlySpan<short> x, out int energy, out int shift)
    {
        int nrg = 0;
        int shft = 0;
        int len = x.Length - 1;
        int i = 0;
        for (; i < len; i += 2)
        {
            nrg = SilkFixedPoint.Add(nrg, SilkFixedPoint.Smulbb(x[i], x[i]));
            nrg = SilkFixedPoint.Add(nrg, SilkFixedPoint.Smulbb(x[i + 1], x[i + 1]));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft = 2;
                break;
            }
        }

        for (; i < len; i += 2)
        {
            int nrgTmp = SilkFixedPoint.Add(
                SilkFixedPoint.Smulbb(x[i], x[i]), SilkFixedPoint.Smulbb(x[i + 1], x[i + 1]));
            nrg = SilkFixedPoint.Add(nrg, (int)((uint)nrgTmp >> shft));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft += 2;
            }
        }

        if (i == len)
        {
            nrg = SilkFixedPoint.Add(nrg, SilkFixedPoint.Smulbb(x[i], x[i]) >> shft);
        }

        if ((nrg & unchecked((int)0xC0000000)) != 0)
        {
            nrg = (int)((uint)nrg >> 2);
            shft += 2;
        }

        shift = shft;
        energy = nrg;
    }

    /// <summary>The inner product with each product shifted right first (Appendix A).</summary>
    private static int InnerProductAlignedScale(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int scale, int length)
    {
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum = SilkFixedPoint.Add(sum, SilkFixedPoint.Smulbb(a[i], b[i]) >> scale);
        }

        return sum;
    }

    /// <summary>Encodes one symbol whose probability mass is given as a forward PDF out of 256.</summary>
    private static void EncodePdf(ref RangeEncoder enc, ReadOnlySpan<byte> pdf, int symbol)
    {
        uint fl = 0;
        for (int k = 0; k < symbol; k++)
        {
            fl += pdf[k];
        }

        enc.Encode(fl, fl + pdf[symbol], 256);
    }
}
