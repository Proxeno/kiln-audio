// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using System.Runtime.Intrinsics;
using Kiln.Audio.Simd.Kernels;

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

    // Per-state histories (Appendix A NSQ_del_dec_struct), one SIMD lane per trellis state: position-major,
    // array[position * Lanes + state], so one position of all states is one vector.
    private const int Lanes = 4;
    private readonly int[] _lpcSoa = new int[(MaxSubframeLength + LpcBufferLength) * Lanes];
    private readonly int[] _ar2Soa = new int[MaxShapingOrder * Lanes];
    private readonly int[] _randSoa = new int[DecisionDelay * Lanes];
    private readonly int[] _qSoa = new int[DecisionDelay * Lanes];
    private readonly int[] _xqSoa = new int[DecisionDelay * Lanes];
    private readonly int[] _predSoa = new int[DecisionDelay * Lanes];
    private readonly int[] _shapeSoa = new int[DecisionDelay * Lanes];
    private readonly int[] _laneLfAr = new int[Lanes];
    private readonly int[] _laneSeed = new int[Lanes];
    private readonly int[] _laneSeedInit = new int[Lanes];
    private readonly int[] _laneRd = new int[Lanes];

    // Per-sample candidate scores (first and second candidate of every state) and prepared filter taps.
    private readonly int[] _candidates = new int[2 * Lanes];
    private readonly Vector128<int>[] _predictionPrepared = new Vector128<int>[MaxPredictionOrder];
    private readonly Vector128<int>[] _shapingPrepared = new Vector128<int>[MaxShapingOrder];
    private readonly AudioKernelTier _tier = AudioKernels.Select();

    // Accessors for the lane-parallel kernel (SilkNsqVec).
    internal int[] LpcSoa => _lpcSoa;

    internal int[] Ar2Soa => _ar2Soa;

    internal int[] RandSoa => _randSoa;

    internal int[] QSoa => _qSoa;

    internal int[] XqSoa => _xqSoa;

    internal int[] PredSoa => _predSoa;

    internal int[] ShapeSoa => _shapeSoa;

    internal int[] LaneLfAr => _laneLfAr;

    internal int[] LaneSeed => _laneSeed;

    internal int[] LaneSeedInit => _laneSeedInit;

    internal int[] LaneRd => _laneRd;

    internal int[] Candidates => _candidates;

    internal Vector128<int>[] PredictionPrepared => _predictionPrepared;

    internal Vector128<int>[] ShapingPrepared => _shapingPrepared;

    internal int[] SLtpQ15 => _sLtpQ15;

    internal int[] SLtpShpQ14 => _sLtpShpQ14;

    internal int[] XScQ10 => _xScQ10;

    internal int[] DelayedGainQ10 => _delayedGainQ10;

    internal sbyte[] Pulses => _pulses;

    internal short[] Xq => _xq;

    internal int SampleBufferIdx { get; set; }

    internal int SLtpBufIdx { get => _sLtpBufIdx; set => _sLtpBufIdx = value; }

    internal int SLtpShpBufIdx { get => _sLtpShpBufIdx; set => _sLtpShpBufIdx = value; }

    internal int CurrentSubframeLength { get; private set; }

    private int _sLfArShpQ14;
    private int _lagPrev;
    private int _sLtpBufIdx;
    private int _sLtpShpBufIdx;
    private int _prevGainQ16;
    private int _rewhiteFlag;

    /// <summary>Creates a quantizer in its initial state.</summary>
    public SilkNsqDelayedDecision()
    {
        Reset();
    }

    /// <summary>
    /// Returns the number of trellis states for an encoder complexity
    /// (Appendix A <c>silk_setup_complexity</c>).
    /// </summary>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    /// <returns>The delayed-decision state count, 1 to 4.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="complexity"/> is outside 0 to 10.</exception>
    /// <remarks>
    /// Complexity 0 and 1 use the greedy quantizer, 2 and 3 still use one state, and the
    /// trellis widens from complexity 4 on. The 2-4 state widths are what make the
    /// rate/distortion improvement over greedy coding measurable at complexity 8 and above.
    /// </remarks>
    internal static int StatesForComplexity(int complexity) => complexity switch
    {
        < 0 or > 10 => throw new ArgumentOutOfRangeException(
            nameof(complexity), complexity, "The encoder complexity must be 0 to 10."),
        < 4 => 1,
        < 6 => 2,
        < 8 => 3,
        _ => MaxStates,
    };

    /// <summary>
    /// Copies the persistent quantizer state from <paramref name="other"/>. The rate-control loop
    /// uses this to rewind to the start of a frame before trying a new gain.
    /// </summary>
    /// <param name="other">The quantizer to snapshot from or restore.</param>
    internal void CopyFrom(SilkNsqDelayedDecision other)
    {
        ArgumentNullException.ThrowIfNull(other);
        other._xq.AsSpan().CopyTo(_xq);
        other._sLtpShpQ14.AsSpan().CopyTo(_sLtpShpQ14);
        other._sLpcQ14.AsSpan().CopyTo(_sLpcQ14);
        other._sAr2Q14.AsSpan().CopyTo(_sAr2Q14);
        _sLfArShpQ14 = other._sLfArShpQ14;
        _lagPrev = other._lagPrev;
        _sLtpBufIdx = other._sLtpBufIdx;
        _sLtpShpBufIdx = other._sLtpShpBufIdx;
        _prevGainQ16 = other._prevGainQ16;
        _rewhiteFlag = other._rewhiteFlag;
        LastCostQ10 = other.LastCostQ10;
    }

    /// <summary>
    /// Copies the complete quantizer state, including every trellis state's history, so a second
    /// quantizer (the LBRR path) starts exactly where this one stands.
    /// </summary>
    /// <param name="other">The state to copy from; it must have the same geometry.</param>
    internal void CopyFullFrom(SilkNsqDelayedDecision other)
    {
        other._xq.AsSpan().CopyTo(_xq);
        other._sLtpShpQ14.AsSpan().CopyTo(_sLtpShpQ14);
        other._sLpcQ14.AsSpan().CopyTo(_sLpcQ14);
        other._sAr2Q14.AsSpan().CopyTo(_sAr2Q14);
        other._lpcSoa.AsSpan().CopyTo(_lpcSoa);
        other._ar2Soa.AsSpan().CopyTo(_ar2Soa);
        other._randSoa.AsSpan().CopyTo(_randSoa);
        other._qSoa.AsSpan().CopyTo(_qSoa);
        other._xqSoa.AsSpan().CopyTo(_xqSoa);
        other._predSoa.AsSpan().CopyTo(_predSoa);
        other._shapeSoa.AsSpan().CopyTo(_shapeSoa);
        other._laneLfAr.AsSpan().CopyTo(_laneLfAr);
        other._laneSeed.AsSpan().CopyTo(_laneSeed);
        other._laneSeedInit.AsSpan().CopyTo(_laneSeedInit);
        other._laneRd.AsSpan().CopyTo(_laneRd);
        SampleBufferIdx = other.SampleBufferIdx;
        _sLfArShpQ14 = other._sLfArShpQ14;
        _lagPrev = other._lagPrev;
        _sLtpBufIdx = other._sLtpBufIdx;
        _sLtpShpBufIdx = other._sLtpShpBufIdx;
        _prevGainQ16 = other._prevGainQ16;
        _rewhiteFlag = other._rewhiteFlag;
        LastCostQ10 = other.LastCostQ10;
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

        if (predictOrder is < 1 or > MaxPredictionOrder)
        {
            throw new ArgumentException("The prediction order must be 1 to 16.", nameof(parameters));
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

        // One switch per frame; everything below it is a direct call specialised to the tier.
        switch (_tier)
        {
            case AudioKernelTier.Avx2Fma:
                seed = RunCore<Avx2FmaKernels>(parameters, stateCount, pulses, xq);
                break;
            case AudioKernelTier.AdvSimdArm64:
                seed = RunCore<AdvSimdArm64Kernels>(parameters, stateCount, pulses, xq);
                break;
            case AudioKernelTier.Vector128:
                seed = RunCore<Vector128Kernels>(parameters, stateCount, pulses, xq);
                break;
            default:
                seed = RunCore<ScalarKernels>(parameters, stateCount, pulses, xq);
                break;
        }
    }

    private int RunCore<TK>(
        in SilkNsqParameters parameters,
        int stateCount,
        Span<sbyte> pulses,
        Span<short> xq)
        where TK : struct, ISilkEncoderKernels
    {
        int subframeLength = parameters.SubframeLength;
        int subframeCount = parameters.SubframeCount;
        int frameLength = subframeLength * subframeCount;
        int ltpMemLength = parameters.LtpMemoryLength;
        int predictOrder = parameters.PredictLpcOrder;
        int shapingOrder = parameters.ShapingLpcOrder;
        ReadOnlySpan<int> pitchLags = parameters.PitchLags;
        ReadOnlySpan<int> gainsQ16 = parameters.GainsQ16;
        bool voiced = parameters.SignalType == 2;
        int lag = _lagPrev;
        CurrentSubframeLength = subframeLength;

        // Initialize the delayed-decision states. The reference zeroes the whole state array
        // first, so the sample buffers start at zero in every frame. Lanes past the live states
        // shadow state 0 (they are computed but never read by a decision).
        _randSoa.AsSpan().Clear();
        _qSoa.AsSpan().Clear();
        _xqSoa.AsSpan().Clear();
        _predSoa.AsSpan().Clear();
        _shapeSoa.AsSpan().Clear();
        for (int k = 0; k < Lanes; k++)
        {
            int s = k < stateCount ? k : 0;
            _laneSeed[k] = (s + parameters.Seed) & 3;
            _laneSeedInit[k] = _laneSeed[k];
            _laneRd[k] = 0;
            _laneLfAr[k] = _sLfArShpQ14;
            _shapeSoa[k] = _sLtpShpQ14[ltpMemLength - 1];
            for (int p = 0; p < LpcBufferLength; p++)
            {
                _lpcSoa[(p * Lanes) + k] = _sLpcQ14[p];
            }

            for (int j = 0; j < MaxShapingOrder; j++)
            {
                _ar2Soa[(j * Lanes) + k] = _sAr2Q14[j];
            }
        }

        int offsetQ10 = SilkSynthesisTables.QuantizationOffsetQ10[parameters.SignalType >> 1][parameters.QuantizationOffsetType];
        SampleBufferIdx = 0;
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
                        int winner = SelectWinner(stateCount, _laneRd);
                        for (int i = 0; i < stateCount; i++)
                        {
                            if (i != winner)
                            {
                                _laneRd[i] = SilkFixedPoint.Add(_laneRd[i], int.MaxValue >> 4);
                            }
                        }

                        int lastIndex = SampleBufferIdx + decisionDelay;
                        for (int i = 0; i < decisionDelay; i++)
                        {
                            lastIndex = (lastIndex - 1) & (DecisionDelay - 1);
                            int slot = (lastIndex * Lanes) + winner;
                            int pulseIndex = (2 * subframeLength) + i - decisionDelay;
                            _pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(_qSoa[slot], 10);
                            _xq[ltpMemLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                                SilkFixedPoint.Smulww(_xqSoa[slot], gainsQ16[1]), 14));
                            _sLtpShpQ14[_sLtpShpBufIdx - decisionDelay + i] = _shapeSoa[slot];
                        }

                        subframeArgument = 0;
                    }

                    // Re-whiten the LTP state with the new coefficients.
                    int startIndex = ltpMemLength - lag - predictOrder - (LtpOrder / 2);
                    int filterLength = ltpMemLength - startIndex;
                    ReadOnlySpan<short> filterInput = _xq.AsSpan(startIndex + (k * subframeLength), filterLength);
                    TK.LpcAnalysisFilter(
                        _sLtp.AsSpan(startIndex, filterLength), filterInput, prediction, filterLength, predictOrder);
                    _sLtpBufIdx = ltpMemLength;
                    _rewhiteFlag = 1;
                }
            }

            ScaleStates<TK>(parameters, k, lag, decisionDelay);
            TK.NsqSubframe(this, new SilkNsqSubframeArgs
            {
                StateCount = stateCount,
                Subframe = k,
                SubframeArgument = subframeArgument,
                Lag = lag,
                DecisionDelay = decisionDelay,
                OffsetQ10 = offsetQ10,
                LambdaQ10 = parameters.LambdaQ10,
                WarpingQ16 = parameters.WarpingQ16,
                HarmonicFirPacked = harmonicFirPacked,
                TiltQ14 = parameters.TiltQ14[k],
                LowFrequencyShapingQ14 = parameters.LowFrequencyShapingQ14[k],
                GainQ10 = gainsQ16[k] >> 6,
                Voiced = voiced,
                LtpMemoryLength = ltpMemLength,
                Prediction = prediction,
                Ltp = ltp,
                Shaping = shaping,
            });
            subframeArgument++;

            if (k != subframeCount - 1)
            {
                // End of subframe: shift the short-term prediction history. The last subframe
                // leaves its array in place; the winner copy below skips the shifted part.
                _lpcSoa.AsSpan(subframeLength * Lanes, LpcBufferLength * Lanes).CopyTo(_lpcSoa);
            }
        }

        // Pick the winner and flush its last decisionDelay samples.
        int finalWinner = SelectWinner(stateCount, _laneRd);
        int result = _laneSeedInit[finalWinner];
        LastCostQ10 = _laneRd[finalWinner];
        int finalIndex = SampleBufferIdx + decisionDelay;
        int gainQ10 = gainsQ16[subframeCount - 1] >> 6;
        for (int i = 0; i < decisionDelay; i++)
        {
            finalIndex = (finalIndex - 1) & (DecisionDelay - 1);
            int slot = (finalIndex * Lanes) + finalWinner;
            int pulseIndex = frameLength + i - decisionDelay;
            _pulses[pulseIndex] = (sbyte)SilkFixedPoint.RshiftRound(_qSoa[slot], 10);
            _xq[ltpMemLength + pulseIndex] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(
                SilkFixedPoint.Smulww(_xqSoa[slot], gainQ10), 8));
            _sLtpShpQ14[_sLtpShpBufIdx - decisionDelay + i] = _shapeSoa[slot];
        }

        for (int p = 0; p < LpcBufferLength; p++)
        {
            _sLpcQ14[p] = _lpcSoa[((subframeLength + p) * Lanes) + finalWinner];
        }

        for (int j = 0; j < MaxShapingOrder; j++)
        {
            _sAr2Q14[j] = _ar2Soa[(j * Lanes) + finalWinner];
        }

        _sLfArShpQ14 = _laneLfAr[finalWinner];
        _lagPrev = pitchLags[subframeCount - 1];

        for (int i = 0; i < frameLength; i++)
        {
            xq[i] = _xq[ltpMemLength + i];
        }

        // Save the quantized speech and shaping history for the next frame's long-term filters.
        _xq.AsSpan(frameLength, ltpMemLength).CopyTo(_xq);
        _sLtpShpQ14.AsSpan(frameLength, ltpMemLength).CopyTo(_sLtpShpQ14);

        _pulses.AsSpan(0, frameLength).CopyTo(pulses);
        return result;
    }

    private void ScaleStates<TK>(in SilkNsqParameters parameters, int subframe, int lag, int decisionDelay)
        where TK : struct, ISilkEncoderKernels
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
            int shapeStart = _sLtpShpBufIdx - parameters.LtpMemoryLength;
            TK.ScaleQ16(_sLtpShpQ14.AsSpan(shapeStart, parameters.LtpMemoryLength), gainAdjustQ16);

            if (parameters.SignalType == 2 && _rewhiteFlag == 0)
            {
                int start = _sLtpBufIdx - lag - (LtpOrder / 2);
                TK.ScaleQ16(_sLtpQ15.AsSpan(start, _sLtpBufIdx - decisionDelay - start), gainAdjustQ16);
            }

            TK.ScaleQ16(_laneLfAr, gainAdjustQ16);
            TK.ScaleQ16(_lpcSoa.AsSpan(0, LpcBufferLength * Lanes), gainAdjustQ16);
            TK.ScaleQ16(_ar2Soa, gainAdjustQ16);
            TK.ScaleQ16(_predSoa, gainAdjustQ16);
            TK.ScaleQ16(_shapeSoa, gainAdjustQ16);
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
}
