// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// The delayed-decision noise shaping quantizer of SILK (RFC 6716 §5.2.3.8,
/// Appendix A <c>silk/NSQ_del_dec.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// A Viterbi search over <c>N</c> quantizer states (1, 2, 3 or 4, chosen from the encoder
/// complexity) with a 32-sample decision delay. Each state keeps its own short-term prediction,
/// noise shaping and LTP history; at every sample the two quantizer levels that straddle the
/// residual are scored with the reference's rate/distortion cost and the best is kept. The
/// surviving path is the one with the lowest accumulated cost, so the resulting weighted error
/// is never worse than the greedy quantizer's.
/// </para>
/// <para>
/// All arithmetic, including wrap-around and 16-bit operand truncation, follows the reference
/// source. Buffers are allocated once in the constructor and reused, so <see cref="Run"/> makes
/// no allocations.
/// </para>
/// </remarks>
internal sealed class SilkNsqDelayedDecision
{
    /// <summary>The decision delay in samples (Appendix A <c>DECISION_DELAY</c>).</summary>
    internal const int DecisionDelay = 32;

    /// <summary>The largest number of trellis states (Appendix A <c>MAX_DEL_DEC_STATES</c>).</summary>
    internal const int MaxStates = 4;

    /// <summary>The long-term prediction order (Appendix A <c>LTP_ORDER</c>).</summary>
    internal const int LtpOrder = 5;

    /// <summary>The longest subframe, 5 ms at 16 kHz (Appendix A <c>MAX_SUB_FRAME_LENGTH</c>).</summary>
    internal const int MaxSubframeLength = 80;

    /// <summary>The longest SILK frame, 20 ms at 16 kHz (Appendix A <c>MAX_FRAME_LENGTH</c>).</summary>
    internal const int MaxFrameLength = 320;

    /// <summary>The short-term prediction history kept for each state (Appendix A <c>NSQ_LPC_BUF_LENGTH</c>).</summary>
    internal const int LpcBufferLength = 32;

    /// <summary>The longest noise shaping filter order (Appendix A <c>MAX_SHAPE_LPC_ORDER</c>).</summary>
    internal const int MaxShapingOrder = 16;

    /// <summary>The longest short-term prediction order (Appendix A <c>MAX_LPC_ORDER</c>).</summary>
    internal const int MaxPredictionOrder = 16;

    /// <summary>The number of harmonic shaping FIR taps (Appendix A <c>HARM_SHAPE_FIR_TAPS</c>).</summary>
    private const int HarmonicShapeTaps = 3;

    // Quantizer state shared across frames (Appendix A silk_nsq_state).
    private readonly short[] _xq = new short[2 * MaxFrameLength];
    private readonly int[] _sLtpShpQ14 = new int[2 * MaxFrameLength];
    private readonly int[] _sLpcQ14 = new int[LpcBufferLength];
    private readonly int[] _sAr2Q14 = new int[MaxShapingOrder];

    // Per-subframe scratch.
    private readonly int[] _sLtpQ15 = new int[2 * MaxFrameLength];
    private readonly short[] _sLtp = new short[2 * MaxFrameLength];
    private readonly int[] _xScQ10 = new int[MaxSubframeLength];
    private readonly int[] _delayedGainQ10 = new int[DecisionDelay];
    private readonly sbyte[] _pulses = new sbyte[MaxFrameLength];

    // Per-state buffers (Appendix A NSQ_del_dec_struct).
    private readonly int[][] _stateLpcQ14 = new int[MaxStates][];
    private readonly int[][] _stateRandState = new int[MaxStates][];
    private readonly int[][] _stateQQ10 = new int[MaxStates][];
    private readonly int[][] _stateXqQ14 = new int[MaxStates][];
    private readonly int[][] _statePredQ15 = new int[MaxStates][];
    private readonly int[][] _stateShapeQ14 = new int[MaxStates][];
    private readonly int[][] _stateAr2Q14 = new int[MaxStates][];
    private readonly int[] _stateLfArQ14 = new int[MaxStates];
    private readonly int[] _stateSeed = new int[MaxStates];
    private readonly int[] _stateSeedInit = new int[MaxStates];
    private readonly int[] _stateRdQ10 = new int[MaxStates];

    // Per-sample candidate scratch (SoA over the states for the vector kernel).
    private readonly int[] _residualQ10 = new int[MaxStates];
    private readonly int[] _seeds = new int[MaxStates];
    private readonly int[] _q1Q10 = new int[MaxStates];
    private readonly int[] _q2Q10 = new int[MaxStates];
    private readonly int[] _rd1Q10 = new int[MaxStates];
    private readonly int[] _rd2Q10 = new int[MaxStates];
    private readonly int[] _stateLpcPredQ14 = new int[MaxStates];
    private readonly int[] _stateNoiseArQ14 = new int[MaxStates];
    private readonly int[] _stateNoiseLfQ14 = new int[MaxStates];
    private readonly int[] _candRdQ10 = new int[MaxStates * 2];
    private readonly int[] _candQQ10 = new int[MaxStates * 2];
    private readonly int[] _candXqQ14 = new int[MaxStates * 2];
    private readonly int[] _candLfArQ14 = new int[MaxStates * 2];
    private readonly int[] _candShapeQ14 = new int[MaxStates * 2];
    private readonly int[] _candLpcExcQ14 = new int[MaxStates * 2];

    private int _sLfArShpQ14;
    private int _lagPrev;
    private int _sLtpBufIdx;
    private int _sLtpShpBufIdx;
    private int _prevGainQ16;
    private int _rewhiteFlag;

    /// <summary>Creates a quantizer in its initial state.</summary>
    public SilkNsqDelayedDecision()
    {
        for (int k = 0; k < MaxStates; k++)
        {
            _stateLpcQ14[k] = new int[MaxSubframeLength + LpcBufferLength];
            _stateRandState[k] = new int[DecisionDelay];
            _stateQQ10[k] = new int[DecisionDelay];
            _stateXqQ14[k] = new int[DecisionDelay];
            _statePredQ15[k] = new int[DecisionDelay];
            _stateShapeQ14[k] = new int[DecisionDelay];
            _stateAr2Q14[k] = new int[MaxShapingOrder];
        }

        Reset();
    }

    /// <summary>Gets the previous frame's pitch lag (Appendix A <c>NSQ-&gt;lagPrev</c>).</summary>
    internal int LagPrev => _lagPrev;

    /// <summary>Gets the surviving path's accumulated rate/distortion cost from the last <see cref="Run"/>.</summary>
    internal int LastCostQ10 { get; private set; }

    /// <summary>Returns the quantizer to its initial state (Appendix A <c>silk_init_encoder</c>).</summary>
    public void Reset()
    {
        _xq.AsSpan().Clear();
        _sLtpShpQ14.AsSpan().Clear();
        _sLpcQ14.AsSpan().Clear();
        _sAr2Q14.AsSpan().Clear();
        _sLfArShpQ14 = 0;
        _lagPrev = 0;
        _sLtpBufIdx = 0;
        _sLtpShpBufIdx = 0;
        _prevGainQ16 = 65536;
        _rewhiteFlag = 0;
    }

    /// <summary>
    /// Quantizes one SILK frame, producing the pulse indices and the quantized output
    /// (Appendix A <c>silk_NSQ_del_dec</c>).
    /// </summary>
    /// <param name="parameters">The frame's prediction, shaping and gain parameters.</param>
    /// <param name="stateCount">The number of trellis states, 1 to 4, from the encoder complexity.</param>
    /// <param name="pulses">Receives the quantized pulse indices, one per frame sample.</param>
    /// <param name="xq">Receives the quantized output signal, one per frame sample.</param>
    /// <param name="seed">Receives the LCG seed the surviving path ends on.</param>
    /// <exception cref="ArgumentException">A buffer is too short or a parameter is out of range.</exception>
    public void Run(
        in SilkNsqParameters parameters,
        int stateCount,
        Span<sbyte> pulses,
        Span<short> xq,
        out int seed)
    {
        int subframeLength = parameters.SubframeLength;
        int subframeCount = parameters.SubframeCount;
        int frameLength = subframeLength * subframeCount;
        int ltpMemLength = parameters.LtpMemoryLength;
        int predictOrder = parameters.PredictLpcOrder;
        int shapingOrder = parameters.ShapingLpcOrder;
        if (stateCount is < 1 or > MaxStates)
        {
            throw new ArgumentOutOfRangeException(nameof(stateCount), stateCount, "One to four states are supported.");
        }

        if (subframeCount is < 1 or > 4 || subframeLength is < 1 or > MaxSubframeLength)
        {
            throw new ArgumentException("The subframe geometry is out of range.", nameof(parameters));
        }

        if (shapingOrder is < 2 or > MaxShapingOrder || (shapingOrder & 1) != 0)
        {
            throw new ArgumentException("The shaping order must be even, 2 to 16.", nameof(parameters));
        }

        if (frameLength > MaxFrameLength || ltpMemLength < 1 || ltpMemLength + frameLength > 2 * MaxFrameLength)
        {
            throw new ArgumentException("The frame does not fit the quantizer buffers.", nameof(parameters));
        }

        if (pulses.Length < frameLength || xq.Length < frameLength)
        {
            throw new ArgumentException("The output buffers are shorter than the frame.", nameof(pulses));
        }

        if (parameters.XQ3.Length < frameLength
            || parameters.PredictionCoefficientsQ12.Length < 2 * MaxPredictionOrder
            || parameters.LtpCoefficientsQ14.Length < LtpOrder * subframeCount
            || parameters.ShapingCoefficientsQ13.Length < MaxShapingOrder * subframeCount
            || parameters.HarmonicShapeGainQ14.Length < subframeCount
            || parameters.TiltQ14.Length < subframeCount
            || parameters.LowFrequencyShapingQ14.Length < subframeCount
            || parameters.GainsQ16.Length < subframeCount
            || parameters.PitchLags.Length < subframeCount)
        {
            throw new ArgumentException("An input buffer is shorter than the frame needs.", nameof(parameters));
        }

        ReadOnlySpan<int> pitchLags = parameters.PitchLags;
        ReadOnlySpan<int> gainsQ16 = parameters.GainsQ16;
        bool voiced = parameters.SignalType == 2;
        int lag = _lagPrev;

        // Initialize the delayed-decision states. The reference zeroes the whole state array
        // first, so the sample buffers start at zero in every frame.
        for (int k = 0; k < stateCount; k++)
        {
            _stateSeed[k] = (k + parameters.Seed) & 3;
            _stateSeedInit[k] = _stateSeed[k];
            _stateRdQ10[k] = 0;
            _stateLfArQ14[k] = _sLfArShpQ14;
            _stateRandState[k].AsSpan().Clear();
            _stateQQ10[k].AsSpan().Clear();
            _stateXqQ14[k].AsSpan().Clear();
            _statePredQ15[k].AsSpan().Clear();
            _stateShapeQ14[k].AsSpan().Clear();
            _stateShapeQ14[k][0] = _sLtpShpQ14[ltpMemLength - 1];
            _sLpcQ14.AsSpan().CopyTo(_stateLpcQ14[k]);
            _sAr2Q14.AsSpan().CopyTo(_stateAr2Q14[k]);
        }

        int offsetQ10 = SilkSynthesisTables.QuantizationOffsetQ10[parameters.SignalType >> 1][parameters.QuantizationOffsetType];
        int sampleBufferIdx = 0;
        int decisionDelay = Math.Min(DecisionDelay, subframeLength);
        if (voiced)
        {
            for (int k = 0; k < subframeCount; k++)
            {
                decisionDelay = Math.Min(decisionDelay, pitchLags[k] - (LtpOrder / 2) - 1);
            }
        }
        else if (lag > 0)
        {
            decisionDelay = Math.Min(decisionDelay, lag - (LtpOrder / 2) - 1);
        }

        int lsfInterpolationFlag = parameters.NlsfInterpolationFactor == 4 ? 0 : 1;
        _sLtpShpBufIdx = ltpMemLength;
        _sLtpBufIdx = ltpMemLength;
        int subframeArgument = 0;
        int lpcStride = MaxPredictionOrder;

        for (int k = 0; k < subframeCount; k++)
        {
            ReadOnlySpan<short> prediction = parameters.PredictionCoefficientsQ12.Slice(
                ((k >> 1) | (1 - lsfInterpolationFlag)) * lpcStride, predictOrder);
            ReadOnlySpan<short> ltp = parameters.LtpCoefficientsQ14.Slice(k * LtpOrder, LtpOrder);
            ReadOnlySpan<short> shaping = parameters.ShapingCoefficientsQ13.Slice(k * MaxShapingOrder, shapingOrder);

            int harmonicGain = parameters.HarmonicShapeGainQ14[k];
            int harmonicFirPacked = (harmonicGain >> 2) | ((harmonicGain >> 1) << 16);

            _rewhiteFlag = 0;
            if (voiced)
            {
                lag = pitchLags[k];
                if ((k & (3 - (lsfInterpolationFlag << 1))) == 0)
                {
                    if (k == 2)
                    {
                        // Reset the delayed decisions at the interpolation boundary.
                        int winner = SelectWinner(stateCount, _stateRdQ10);
                        for (int i = 0; i < stateCount; i++)
                        {
                            if (i != winner)
                            {
                                _stateRdQ10[i] = SilkFixedPoint.Add(_stateRdQ10[i], int.MaxValue >> 4);
                            }
                        }

                        int lastIndex = sampleBufferIdx + decisionDelay;
                        for (int i = 0; i < decisionDelay; i++)
                        {
                            lastIndex = (lastIndex - 1) & (DecisionDelay - 1);
                            int pulseIndex = (2 * subframeLength) + i - decisionDelay;
                            _pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(_stateQQ10[winner][lastIndex], 10);
                            _xq[ltpMemLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                                SilkFixedPoint.Smulww(_stateXqQ14[winner][lastIndex], gainsQ16[1]), 14));
                            _sLtpShpQ14[_sLtpShpBufIdx - decisionDelay + i] = _stateShapeQ14[winner][lastIndex];
                        }

                        subframeArgument = 0;
                    }

                    // Re-whiten the LTP state with the new coefficients.
                    int startIndex = ltpMemLength - lag - predictOrder - (LtpOrder / 2);
                    int filterLength = ltpMemLength - startIndex;
                    ReadOnlySpan<short> filterInput = _xq.AsSpan(startIndex + (k * subframeLength), filterLength);
                    LpcAnalysisFilter(
                        _sLtp.AsSpan(startIndex, filterLength), filterInput, prediction, filterLength, predictOrder);
                    _sLtpBufIdx = ltpMemLength;
                    _rewhiteFlag = 1;
                }
            }

            ScaleStates(parameters, stateCount, k, lag, decisionDelay);
            QuantizeSubframe(
                parameters, stateCount, k, subframeArgument, lag, ltpMemLength, prediction, ltp, shaping,
                harmonicFirPacked, offsetQ10, decisionDelay, ref sampleBufferIdx);
            subframeArgument++;

            if (k != subframeCount - 1)
            {
                // End of subframe: shift the short-term prediction history. The last subframe
                // leaves its array in place; the winner copy below skips the shifted part.
                for (int state = 0; state < stateCount; state++)
                {
                    _stateLpcQ14[state].AsSpan(subframeLength, LpcBufferLength)
                        .CopyTo(_stateLpcQ14[state]);
                }
            }
        }

        // Pick the winner and flush its last decisionDelay samples.
        int finalWinner = SelectWinner(stateCount, _stateRdQ10);
        seed = _stateSeedInit[finalWinner];
        LastCostQ10 = _stateRdQ10[finalWinner];
        int finalIndex = sampleBufferIdx + decisionDelay;
        int gainQ10 = gainsQ16[subframeCount - 1] >> 6;
        for (int i = 0; i < decisionDelay; i++)
        {
            finalIndex = (finalIndex - 1) & (DecisionDelay - 1);
            int pulseIndex = frameLength + i - decisionDelay;
            _pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(_stateQQ10[finalWinner][finalIndex], 10);
            _xq[ltpMemLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                SilkFixedPoint.Smulww(_stateXqQ14[finalWinner][finalIndex], gainQ10), 8));
            _sLtpShpQ14[_sLtpShpBufIdx - decisionDelay + i] = _stateShapeQ14[finalWinner][finalIndex];
        }

        _stateLpcQ14[finalWinner].AsSpan(subframeLength, LpcBufferLength).CopyTo(_sLpcQ14);
        _stateAr2Q14[finalWinner].AsSpan().CopyTo(_sAr2Q14);
        _sLfArShpQ14 = _stateLfArQ14[finalWinner];
        _lagPrev = pitchLags[subframeCount - 1];

        for (int i = 0; i < frameLength; i++)
        {
            xq[i] = _xq[ltpMemLength + i];
        }

        // Save the quantized speech and shaping history for the next frame's long-term filters.
        _xq.AsSpan(frameLength, ltpMemLength).CopyTo(_xq);
        _sLtpShpQ14.AsSpan(frameLength, ltpMemLength).CopyTo(_sLtpShpQ14);

        _pulses.AsSpan(0, frameLength).CopyTo(pulses);
    }

    private void ScaleStates(in SilkNsqParameters parameters, int stateCount, int subframe, int lag, int decisionDelay)
    {
        int subframeLength = parameters.SubframeLength;
        int gainQ16 = parameters.GainsQ16[subframe];
        int inverseGainQ31 = SilkFixedPoint.Inverse32VarQ(Math.Max(gainQ16, 1), 47);
        int gainAdjustQ16 = gainQ16 != _prevGainQ16
            ? SilkFixedPoint.Div32VarQ(_prevGainQ16, gainQ16, 16)
            : 1 << 16;

        int inverseGainQ23 = SilkFixedPoint.RshiftRound(inverseGainQ31, 8);
        for (int i = 0; i < subframeLength; i++)
        {
            _xScQ10[i] = SilkFixedPoint.Smulww(parameters.XQ3[(subframe * subframeLength) + i], inverseGainQ23);
        }

        _prevGainQ16 = gainQ16;

        if (_rewhiteFlag != 0)
        {
            if (subframe == 0)
            {
                inverseGainQ31 = SilkFixedPoint.Lshift(
                    SilkFixedPoint.Smulwb(inverseGainQ31, parameters.LtpScaleQ14), 2);
            }

            for (int i = _sLtpBufIdx - lag - (LtpOrder / 2); i < _sLtpBufIdx; i++)
            {
                _sLtpQ15[i] = SilkFixedPoint.Smulwb(inverseGainQ31, _sLtp[i]);
            }
        }

        if (gainAdjustQ16 != 1 << 16)
        {
            for (int i = _sLtpShpBufIdx - parameters.LtpMemoryLength; i < _sLtpShpBufIdx; i++)
            {
                _sLtpShpQ14[i] = SilkFixedPoint.Smulww(gainAdjustQ16, _sLtpShpQ14[i]);
            }

            if (parameters.SignalType == 2 && _rewhiteFlag == 0)
            {
                for (int i = _sLtpBufIdx - lag - (LtpOrder / 2); i < _sLtpBufIdx - decisionDelay; i++)
                {
                    _sLtpQ15[i] = SilkFixedPoint.Smulww(gainAdjustQ16, _sLtpQ15[i]);
                }
            }

            for (int state = 0; state < stateCount; state++)
            {
                _stateLfArQ14[state] = SilkFixedPoint.Smulww(gainAdjustQ16, _stateLfArQ14[state]);
                int[] lpc = _stateLpcQ14[state];
                for (int i = 0; i < LpcBufferLength; i++)
                {
                    lpc[i] = SilkFixedPoint.Smulww(gainAdjustQ16, lpc[i]);
                }

                int[] ar = _stateAr2Q14[state];
                for (int i = 0; i < MaxShapingOrder; i++)
                {
                    ar[i] = SilkFixedPoint.Smulww(gainAdjustQ16, ar[i]);
                }

                int[] pred = _statePredQ15[state];
                int[] shape = _stateShapeQ14[state];
                for (int i = 0; i < DecisionDelay; i++)
                {
                    pred[i] = SilkFixedPoint.Smulww(gainAdjustQ16, pred[i]);
                    shape[i] = SilkFixedPoint.Smulww(gainAdjustQ16, shape[i]);
                }
            }
        }
    }

    private void QuantizeSubframe(
        in SilkNsqParameters parameters,
        int stateCount,
        int subframe,
        int subframeArgument,
        int lag,
        int ltpMemLength,
        ReadOnlySpan<short> prediction,
        ReadOnlySpan<short> ltp,
        ReadOnlySpan<short> shaping,
        int harmonicFirPacked,
        int offsetQ10,
        int decisionDelay,
        ref int sampleBufferIdx)
    {
        int subframeLength = parameters.SubframeLength;
        bool voiced = parameters.SignalType == 2;
        int gainQ10 = parameters.GainsQ16[subframe] >> 6;
        int tiltQ14 = parameters.TiltQ14[subframe];
        int lowFrequencyShapingQ14 = parameters.LowFrequencyShapingQ14[subframe];
        int shapingLagIndex = _sLtpShpBufIdx - lag + (HarmonicShapeTaps / 2);
        int predictionLagIndex = _sLtpBufIdx - lag + (LtpOrder / 2);

        for (int i = 0; i < subframeLength; i++)
        {
            // Long-term prediction.
            int ltpPredictionQ14 = 0;
            if (voiced)
            {
                ltpPredictionQ14 = 2;
                for (int j = 0; j < LtpOrder; j++)
                {
                    ltpPredictionQ14 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ14, _sLtpQ15[predictionLagIndex - j], ltp[j]);
                }

                ltpPredictionQ14 = SilkFixedPoint.Lshift(ltpPredictionQ14, 1);
                predictionLagIndex++;
            }

            // Long-term shaping.
            int longTermShapingQ14 = 0;
            if (lag > 0)
            {
                longTermShapingQ14 = SilkFixedPoint.Smulwb(
                    _sLtpShpQ14[shapingLagIndex] + _sLtpShpQ14[shapingLagIndex - 2], harmonicFirPacked);
                longTermShapingQ14 = Smlawt(longTermShapingQ14, _sLtpShpQ14[shapingLagIndex - 1], harmonicFirPacked);
                longTermShapingQ14 = SilkFixedPoint.Sub(ltpPredictionQ14, longTermShapingQ14 << 2);
                shapingLagIndex++;
            }

            for (int state = 0; state < stateCount; state++)
            {
                _stateSeed[state] = SilkFixedPoint.Rand(_stateSeed[state]);
                int lpcBase = LpcBufferLength - 1 + i;
                int lpcPrediction = LpcPrediction(_stateLpcQ14[state], lpcBase, prediction);
                int noiseAr = NoiseShaping(
                    _stateAr2Q14[state], _stateLpcQ14[state][lpcBase], _stateLfArQ14[state],
                    shaping, parameters.WarpingQ16, tiltQ14);
                int noiseLf = SilkFixedPoint.Smulwb(_stateShapeQ14[state][sampleBufferIdx], lowFrequencyShapingQ14);
                noiseLf = Smlawt(noiseLf, _stateLfArQ14[state], lowFrequencyShapingQ14);
                noiseLf = SilkFixedPoint.Lshift(noiseLf, 2);

                int shapingSum = SilkFixedPoint.Add(noiseAr, noiseLf);
                int predictionSum = SilkFixedPoint.Add(longTermShapingQ14, lpcPrediction);
                int combined = SilkFixedPoint.RshiftRound(SilkFixedPoint.Sub(predictionSum, shapingSum), 4);
                _residualQ10[state] = SilkFixedPoint.Sub(_xScQ10[i], combined);
                _seeds[state] = _stateSeed[state];
                _stateLpcPredQ14[state] = lpcPrediction;
                _stateNoiseArQ14[state] = noiseAr;
                _stateNoiseLfQ14[state] = noiseLf;
            }

            SilkNsqDelayedDecisionKernel.Evaluate(
                _residualQ10, _seeds, stateCount, offsetQ10, parameters.LambdaQ10,
                _q1Q10, _q2Q10, _rd1Q10, _rd2Q10);

            for (int state = 0; state < stateCount; state++)
            {
                int firstQ;
                int secondQ;
                int firstRd;
                int secondRd;
                if (_rd1Q10[state] < _rd2Q10[state])
                {
                    firstRd = SilkFixedPoint.Add(_stateRdQ10[state], _rd1Q10[state]);
                    secondRd = SilkFixedPoint.Add(_stateRdQ10[state], _rd2Q10[state]);
                    firstQ = _q1Q10[state];
                    secondQ = _q2Q10[state];
                }
                else
                {
                    firstRd = SilkFixedPoint.Add(_stateRdQ10[state], _rd2Q10[state]);
                    secondRd = SilkFixedPoint.Add(_stateRdQ10[state], _rd1Q10[state]);
                    firstQ = _q2Q10[state];
                    secondQ = _q1Q10[state];
                }

                _candRdQ10[state] = firstRd;
                _candQQ10[state] = firstQ;
                _candRdQ10[MaxStates + state] = secondRd;
                _candQQ10[MaxStates + state] = secondQ;
                CompleteCandidate(state, candidate: 0, firstQ, ltpPredictionQ14);
                CompleteCandidate(state, candidate: 1, secondQ, ltpPredictionQ14);
            }

            sampleBufferIdx = (sampleBufferIdx - 1) & (DecisionDelay - 1);
            int lastSampleIndex = (sampleBufferIdx + decisionDelay) & (DecisionDelay - 1);

            // Find the winner and penalize states that have diverged from its history.
            int winner = 0;
            int minimum = _candRdQ10[0];
            for (int state = 1; state < stateCount; state++)
            {
                if (_candRdQ10[state] < minimum)
                {
                    minimum = _candRdQ10[state];
                    winner = state;
                }
            }

            int winnerRandState = _stateRandState[winner][lastSampleIndex];
            for (int state = 0; state < stateCount; state++)
            {
                if (_stateRandState[state][lastSampleIndex] != winnerRandState)
                {
                    _candRdQ10[state] = SilkFixedPoint.Add(_candRdQ10[state], int.MaxValue >> 4);
                    _candRdQ10[MaxStates + state] = SilkFixedPoint.Add(_candRdQ10[MaxStates + state], int.MaxValue >> 4);
                }
            }

            // Replace the worst first-set state when the best second-set state beats it.
            int worstIndex = 0;
            int worst = _candRdQ10[0];
            int bestIndex = 0;
            int best = _candRdQ10[MaxStates];
            for (int state = 1; state < stateCount; state++)
            {
                if (_candRdQ10[state] > worst)
                {
                    worst = _candRdQ10[state];
                    worstIndex = state;
                }

                if (_candRdQ10[MaxStates + state] < best)
                {
                    best = _candRdQ10[MaxStates + state];
                    bestIndex = state;
                }
            }

            if (best < worst)
            {
                CopyState(bestIndex, worstIndex, i);
                _candRdQ10[worstIndex] = _candRdQ10[MaxStates + bestIndex];
                _candQQ10[worstIndex] = _candQQ10[MaxStates + bestIndex];
                _candXqQ14[worstIndex] = _candXqQ14[MaxStates + bestIndex];
                _candLfArQ14[worstIndex] = _candLfArQ14[MaxStates + bestIndex];
                _candShapeQ14[worstIndex] = _candShapeQ14[MaxStates + bestIndex];
                _candLpcExcQ14[worstIndex] = _candLpcExcQ14[MaxStates + bestIndex];
            }

            // Write the delayed sample from the winner.
            if (subframeArgument > 0 || i >= decisionDelay)
            {
                int pulseIndex = (subframe * subframeLength) + i - decisionDelay;
                _pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(_stateQQ10[winner][lastSampleIndex], 10);
                _xq[ltpMemLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                    SilkFixedPoint.Smulww(_stateXqQ14[winner][lastSampleIndex], _delayedGainQ10[lastSampleIndex]), 8));
                _sLtpShpQ14[_sLtpShpBufIdx - decisionDelay] = _stateShapeQ14[winner][lastSampleIndex];
                _sLtpQ15[_sLtpBufIdx - decisionDelay] = _statePredQ15[winner][lastSampleIndex];
            }

            _sLtpShpBufIdx++;
            _sLtpBufIdx++;

            for (int state = 0; state < stateCount; state++)
            {
                _stateLfArQ14[state] = _candLfArQ14[state];
                _stateLpcQ14[state][LpcBufferLength + i] = _candXqQ14[state];
                _stateXqQ14[state][sampleBufferIdx] = _candXqQ14[state];
                _stateQQ10[state][sampleBufferIdx] = _candQQ10[state];
                _statePredQ15[state][sampleBufferIdx] = SilkFixedPoint.Lshift(_candLpcExcQ14[state], 1);
                _stateShapeQ14[state][sampleBufferIdx] = _candShapeQ14[state];
                _stateSeed[state] = SilkFixedPoint.Add(
                    _stateSeed[state], SilkFixedPoint.RshiftRound(_candQQ10[state], 10));
                _stateRandState[state][sampleBufferIdx] = _stateSeed[state];
                _stateRdQ10[state] = _candRdQ10[state];
            }

            _delayedGainQ10[sampleBufferIdx] = gainQ10;
        }
    }

    private static int SelectWinner(int stateCount, int[] cost)
    {
        int winner = 0;
        int minimum = cost[0];
        for (int i = 1; i < stateCount; i++)
        {
            if (cost[i] < minimum)
            {
                minimum = cost[i];
                winner = i;
            }
        }

        return winner;
    }

    private static int LpcPrediction(int[] lpc, int lpcBase, ReadOnlySpan<short> coefficients)
    {
        int prediction = coefficients.Length >> 1;
        for (int j = 0; j < coefficients.Length; j++)
        {
            prediction = SilkFixedPoint.Smlawb(prediction, lpc[lpcBase - j], coefficients[j]);
        }

        return SilkFixedPoint.Lshift(prediction, 4);
    }

    private static int NoiseShaping(
        int[] ar2, int lpcCurrent, int lfAr, ReadOnlySpan<short> coefficients, int warpingQ16, int tiltQ14)
    {
        int order = coefficients.Length;
        int tmp2 = SilkFixedPoint.Smlawb(lpcCurrent, ar2[0], warpingQ16);
        int tmp1 = SilkFixedPoint.Smlawb(ar2[0], ar2[1] - tmp2, warpingQ16);
        ar2[0] = tmp2;
        int noiseAr = order >> 1;
        noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, coefficients[0]);
        for (int j = 2; j < order; j += 2)
        {
            tmp2 = SilkFixedPoint.Smlawb(ar2[j - 1], ar2[j] - tmp1, warpingQ16);
            ar2[j - 1] = tmp1;
            noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, coefficients[j - 1]);
            tmp1 = SilkFixedPoint.Smlawb(ar2[j], ar2[j + 1] - tmp2, warpingQ16);
            ar2[j] = tmp2;
            noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, coefficients[j]);
        }

        ar2[order - 1] = tmp1;
        noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, coefficients[order - 1]);
        noiseAr = SilkFixedPoint.Lshift(noiseAr, 1);
        noiseAr = SilkFixedPoint.Smlawb(noiseAr, lfAr, tiltQ14);
        return SilkFixedPoint.Lshift(noiseAr, 2);
    }

    /// <summary>Completes one candidate's quantized-signal state (Appendix A, best/second best update).</summary>
    private void CompleteCandidate(int state, int candidate, int qQ10, int ltpPredictionQ14)
    {
        int index = (candidate * MaxStates) + state;
        int excitationQ14 = SilkFixedPoint.Lshift(qQ10, 4);
        if (_stateSeed[state] < 0)
        {
            excitationQ14 = -excitationQ14;
        }

        int lpcExcitationQ14 = SilkFixedPoint.Add(excitationQ14, ltpPredictionQ14);
        int xqQ14 = SilkFixedPoint.Add(lpcExcitationQ14, _stateLpcPredQ14[state]);
        int lfAr = SilkFixedPoint.Sub(xqQ14, _stateNoiseArQ14[state]);
        _candLpcExcQ14[index] = lpcExcitationQ14;
        _candXqQ14[index] = xqQ14;
        _candLfArQ14[index] = lfAr;
        _candShapeQ14[index] = SilkFixedPoint.Sub(lfAr, _stateNoiseLfQ14[state]);
    }

    private void CopyState(int source, int destination, int sample)
    {
        int[] sourceLpc = _stateLpcQ14[source];
        int[] destinationLpc = _stateLpcQ14[destination];
        Array.Copy(sourceLpc, sample, destinationLpc, sample, sourceLpc.Length - sample);
        Array.Copy(_stateRandState[source], _stateRandState[destination], DecisionDelay);
        Array.Copy(_stateQQ10[source], _stateQQ10[destination], DecisionDelay);
        Array.Copy(_stateXqQ14[source], _stateXqQ14[destination], DecisionDelay);
        Array.Copy(_statePredQ15[source], _statePredQ15[destination], DecisionDelay);
        Array.Copy(_stateShapeQ14[source], _stateShapeQ14[destination], DecisionDelay);
        Array.Copy(_stateAr2Q14[source], _stateAr2Q14[destination], MaxShapingOrder);
        _stateLfArQ14[destination] = _stateLfArQ14[source];
        _stateSeed[destination] = _stateSeed[source];
        _stateSeedInit[destination] = _stateSeedInit[source];
        _stateRdQ10[destination] = _stateRdQ10[source];
    }

    /// <summary>Multiplies by the high half of a packed coefficient (Appendix A <c>silk_SMLAWT</c>).</summary>
    private static int Smlawt(int a, int b, int c) =>
        unchecked(a + (int)(((long)b * (c >> 16)) >> 16));

    /// <summary>The inverse LPC filter used to re-whiten the LTP state (Appendix A <c>silk_LPC_analysis_filter</c>).</summary>
    private static void LpcAnalysisFilter(
        Span<short> output,
        ReadOnlySpan<short> input,
        ReadOnlySpan<short> coefficients,
        int length,
        int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int out32Q12 = 0;
            for (int j = 0; j < order; j++)
            {
                out32Q12 = SilkFixedPoint.Add(
                    out32Q12, SilkFixedPoint.Smulbb(input[ix - 1 - j], coefficients[j]));
            }

            out32Q12 = SilkFixedPoint.Sub(SilkFixedPoint.Lshift(input[ix], 12), out32Q12);
            output[ix] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(out32Q12, 12));
        }

        output[..order].Clear();
    }
}
