// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// Reconstructs PCM from the SILK parameters of one Opus frame at the internal sample rate
/// (RFC 6716 §4.2.7.5.2 through §4.2.9).
/// </summary>
/// <remarks>
/// <para>
/// This is a direct port of the fixed-point reference decoder (<c>silk_decode_parameters</c>,
/// <c>silk_decode_core</c> and <c>silk_stereo_MS_to_LR</c>), so its output is bit-exact with
/// libopus. The RFC 8251 errata are applied: §3 resets the stereo state on a mode switch, §6
/// range-checks the inverse prediction gain, and §7 saturates the LSF fallback addition.
/// </para>
/// <para>
/// The returned PCM is the SILK synthesis output, i.e. the signal libopus feeds to its
/// internal-rate resampler. libopus's copy resampler delays that signal by a fixed number of
/// samples per rate (see <c>docs/architecture.md</c>); the oracle test accounts for it.
/// </para>
/// <para>
/// One instance decodes one stream: it carries the LPC/LTP histories, previous NLSFs and gains,
/// and the stereo mid/side state across frames. A change of internal rate resets it, as RFC 6716
/// §4.5.2 requires for a bandwidth change. A change of channel count at the same rate keeps the
/// mid channel's synthesis state, starts the side channel fresh and clears the stereo mid/side
/// state, matching the reference implementation (Appendix A, <c>silk/dec_API.c</c>).
/// Synthesizing a frame allocates nothing.
/// </para>
/// </remarks>
internal sealed partial class SilkSynthesis
{
    private const int MaxChannels = 2;
    private const int MaxLpcOrder = 16;
    private const int MaxSubframes = 4;
    private const int LtpOrder = 5;
    private const int SubframeMilliseconds = 5;
    private const int LtpMemoryMilliseconds = 20;
    private const int MaxFrameLength = LtpMemoryMilliseconds * 16;
    private const int MaxSubframeLength = SubframeMilliseconds * 16;

    /// <summary>Per-channel reconstruction state.</summary>
    private sealed class ChannelState
    {
        public int PrevGainQ16 = 65536;
        public int[] SLpcQ14Buf = new int[MaxLpcOrder];
        public short[] OutBuf = new short[MaxFrameLength + (2 * MaxSubframeLength)];
        public short[] PrevNlsfQ15 = new short[MaxLpcOrder];
        public bool FirstFrameAfterReset = true;
        public int LagPrev = 100;
        public int LastGainIndex = 10;
        public int PrevSignalType;

        // Per-frame scratch, kept here so a frame allocates nothing.
        public readonly int[] ExcQ14 = new int[MaxFrameLength];
        public readonly short[] SLtp = new short[MaxFrameLength];
        public readonly int[] SLtpQ15 = new int[2 * MaxFrameLength];
        public readonly int[] SLpcQ14 = new int[MaxLpcOrder + MaxSubframeLength];
        public readonly int[] ResQ14 = new int[MaxSubframeLength];
        public readonly short[] PredCoefQ12 = new short[2 * MaxLpcOrder];
        public readonly short[] LtpCoefQ14 = new short[LtpOrder * MaxSubframes];
        public readonly int[] PitchL = new int[MaxSubframes];

        public void Reset()
        {
            PrevGainQ16 = 65536;
            SLpcQ14Buf.AsSpan().Clear();
            OutBuf.AsSpan().Clear();
            PrevNlsfQ15.AsSpan().Clear();
            FirstFrameAfterReset = true;
            LagPrev = 100;
            LastGainIndex = 10;
            PrevSignalType = 0;
        }

        /// <summary>Clears only the prediction memory the reference clears on a mid-only transition.</summary>
        public void ResetPredictionMemory()
        {
            SLpcQ14Buf.AsSpan().Clear();
            OutBuf.AsSpan().Clear();
            FirstFrameAfterReset = true;
            LagPrev = 100;
            LastGainIndex = 10;
            PrevSignalType = 0;
        }
    }

    private readonly ChannelState[] _channelState = [new ChannelState(), new ChannelState()];
    private readonly short[] _mid = new short[MaxFrameLength];
    private readonly short[] _side = new short[MaxFrameLength];
    private readonly short[] _tmpMid = new short[MaxFrameLength + 2];
    private readonly short[] _tmpSide = new short[MaxFrameLength + 2];
    private readonly short[] _nlsfQ15 = new short[MaxLpcOrder];
    private readonly short[] _nlsf0Q15 = new short[MaxLpcOrder];

    private readonly short[] _stereoMid = new short[2];
    private readonly short[] _stereoSide = new short[2];
    private readonly int[] _stereoPredPrevQ13 = new int[2];
    private bool _previousDecodeOnlyMiddle;

    private int _rateHz;
    private int _channels;
    private int _bandwidthIndex;
    private int _lpcOrder;
    private int _subframeCount;
    private int _subframeLength;
    private int _frameLength;
    private int _ltpMemoryLength;

    /// <summary>Creates a synthesizer in its reset state.</summary>
    public SilkSynthesis() => Reset();

    /// <summary>
    /// Gets the last mid sample of the most recently synthesized frame.
    /// </summary>
    /// <remarks>
    /// This is the second element of the reference decoder's <c>sStereo.sMid</c> history. The
    /// top-level decoder uses it to carry the mid history across a mono/stereo channel change,
    /// which is not a mode change and therefore does not reset the decoder (RFC 6716 §4.5.2).
    /// </remarks>
    internal short LastMidSample => _stereoMid[1];

    /// <summary>
    /// Clears all cross-frame state, including the stereo mid/side memory RFC 8251 §3 requires to
    /// be reinitialized on a mode switch.
    /// </summary>
    public void Reset()
    {
        _rateHz = 0;
        _channels = 0;
        _bandwidthIndex = 0;
        _lpcOrder = 0;
        _subframeCount = 0;
        _subframeLength = 0;
        _frameLength = 0;
        _ltpMemoryLength = 0;
        foreach (ChannelState state in _channelState)
        {
            state.Reset();
        }

        _stereoMid.AsSpan().Clear();
        _stereoSide.AsSpan().Clear();
        _stereoPredPrevQ13.AsSpan().Clear();
        _previousDecodeOnlyMiddle = false;
        ResetPlc();
    }

    /// <summary>
    /// Synthesizes every SILK frame of one Opus frame into interleaved PCM at the internal rate.
    /// </summary>
    /// <param name="frame">The decoded SILK parameters of the Opus frame.</param>
    /// <param name="pcmOut">
    /// Receives the samples, per channel and interleaved when stereo. It must hold
    /// <c>SilkFrameCount * frameLength * Channels</c> entries.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcmOut"/> is too short.</exception>
    public void Synthesize(SilkFrameResult frame, Span<short> pcmOut)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ConfigureForFrame(frame);

        int silkFrames = frame.SilkFrameCount;
        int required = silkFrames * _frameLength * _channels;
        if (pcmOut.Length < required)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The output needs {required} samples for {silkFrames} SILK frame(s) but holds {pcmOut.Length}."),
                nameof(pcmOut));
        }

        for (int f = 0; f < silkFrames; f++)
        {
            SilkChannelFrame mid = frame.Regular(f, 0);
            bool sideCoded = _channels == 2 && frame.IsRegularCoded(f, 1);

            // RFC 6716 §4.2.7.2 and §4.2.8, dec_API.c: a side frame that was mid-only leaves the
            // side prediction memory stale, so clear it before the first frame that codes it.
            if (_channels == 2 && sideCoded && _previousDecodeOnlyMiddle)
            {
                _channelState[1].ResetPredictionMemory();
            }

            SynthesizeChannel(_channelState[0], mid, _mid.AsSpan(0, _frameLength));
            PlcUpdate(0, mid);
            PlcGlue(0, _mid.AsSpan(0, _frameLength));
            Cng(0, _mid.AsSpan(0, _frameLength), mid);

            if (sideCoded)
            {
                SilkChannelFrame side = frame.Regular(f, 1);
                SynthesizeChannel(_channelState[1], side, _side.AsSpan(0, _frameLength));
                PlcUpdate(1, side);
                PlcGlue(1, _side.AsSpan(0, _frameLength));
                Cng(1, _side.AsSpan(0, _frameLength), side);
            }
            else if (_channels == 2)
            {
                _side.AsSpan(0, _frameLength).Clear();
            }

            int frameOffset = f * _frameLength * _channels;
            if (_channels == 2)
            {
                StereoUnmix(mid, frameOffset, pcmOut);
            }
            else
            {
                _mid.AsSpan(0, _frameLength).CopyTo(pcmOut[frameOffset..]);

                // RFC 6716 Appendix A, dec_API.c: the mono/API-mono buffering branch keeps the
                // last two mid samples in sStereo.sMid. Keeping them current is what makes the
                // first stereo frame after mono start from the correct history (the side history
                // is deliberately left untouched, as the reference does).
                _stereoMid[0] = _mid[_frameLength - 2];
                _stereoMid[1] = _mid[_frameLength - 1];
            }

            _previousDecodeOnlyMiddle = _channels == 2 && mid.IsMidOnly;
        }
    }

    /// <summary>Reconstructs one channel's SILK frame (RFC 6716 §4.2.7.9, <c>silk_decode_core</c>).</summary>
    private void SynthesizeChannel(ChannelState state, SilkChannelFrame frame, Span<short> output)
    {
        int order = _lpcOrder;
        int subframeLength = _subframeLength;
        int subframes = _subframeCount;
        int frameLength = _frameLength;
        int ltpMemoryLength = _ltpMemoryLength;
        int bandwidthIndex = _bandwidthIndex;
        bool voiced = frame.IsVoiced;
        int quantizationOffsetQ10 =
            SilkSynthesisTables.QuantizationOffsetQ10[(int)frame.SignalType >> 1][
                frame.QuantizationOffsetType];
        int interpolationFactor = state.FirstFrameAfterReset ? 4 : frame.NlsfInterpolationFactor;

        // RFC 6716 §4.2.7.5: reconstruct the NLSFs and both LPC coefficient sets. PredCoef[1] is
        // the current frame; PredCoef[0] is the interpolated first half (decode_parameters.c).
        SilkNlsf.Decode(bandwidthIndex, frame.NlsfStage1Index, frame.NlsfStage2Index, _nlsfQ15);
        Span<short> predCoef = state.PredCoefQ12;
        Span<short> currentCoef = predCoef[MaxLpcOrder..(MaxLpcOrder + order)];
        Span<short> interpolatedCoef = predCoef[..order];
        SilkNlsf.ConvertToLpc(currentCoef, _nlsfQ15, order);

        if (interpolationFactor < 4)
        {
            for (int i = 0; i < order; i++)
            {
                _nlsf0Q15[i] = (short)(state.PrevNlsfQ15[i]
                    + SilkFixedPoint.Rshift(
                        SilkFixedPoint.Mul(
                            interpolationFactor, _nlsfQ15[i] - state.PrevNlsfQ15[i]), 2));
            }

            SilkNlsf.ConvertToLpc(interpolatedCoef, _nlsf0Q15, order);
        }
        else
        {
            currentCoef.CopyTo(interpolatedCoef);
        }

        _nlsfQ15.AsSpan(0, order).CopyTo(state.PrevNlsfQ15);

        // RFC 6716 §4.2.7.6: LTP parameters, zero for unvoiced frames.
        if (voiced)
        {
            ReadOnlySpan<sbyte> codebook = SilkSynthesisTables.LtpCodebookQ7[frame.LtpPeriodicityIndex];
            for (int k = 0; k < subframes; k++)
            {
                state.PitchL[k] = frame.PitchLag[k];
                int tap = frame.LtpFilterIndex[k] * LtpOrder;
                for (int i = 0; i < LtpOrder; i++)
                {
                    state.LtpCoefQ14[(k * LtpOrder) + i] =
                        (short)SilkFixedPoint.Lshift(codebook[tap + i], 7);
                }
            }
        }
        else
        {
            state.PitchL.AsSpan(0, subframes).Clear();
            state.LtpCoefQ14.AsSpan(0, subframes * LtpOrder).Clear();
        }

        int ltpScaleQ14 = voiced ? frame.LtpScalingQ14 : 0;

        // RFC 6716 §4.2.7.8.6: reconstruct the excitation, Q14.
        int randSeed = frame.Seed;
        for (int i = 0; i < frameLength; i++)
        {
            randSeed = SilkFixedPoint.Rand(randSeed);
            int excitation = SilkFixedPoint.Lshift(frame.Pulses[i], 14);
            if (excitation > 0)
            {
                excitation -= 80 << 4;
            }
            else if (excitation < 0)
            {
                excitation += 80 << 4;
            }

            excitation = SilkFixedPoint.Add(excitation, SilkFixedPoint.Lshift(quantizationOffsetQ10, 4));
            if (randSeed < 0)
            {
                excitation = -excitation;
            }

            state.ExcQ14[i] = excitation;
            randSeed = SilkFixedPoint.Add(randSeed, frame.Pulses[i]);
        }

        // RFC 6716 §4.2.7.9: LTP and LPC synthesis.
        Span<int> sLpcQ14 = state.SLpcQ14;
        state.SLpcQ14Buf.AsSpan().CopyTo(sLpcQ14);
        int ltpBufferIndex = ltpMemoryLength;
        int excitationOffset = 0;

        for (int k = 0; k < subframes; k++)
        {
            int gainQ16 = frame.GainQ16[k];
            int gainQ10 = SilkFixedPoint.Rshift(gainQ16, 6);
            int inverseGainQ31 = SilkFixedPoint.Inverse32VarQ(gainQ16, 47);
            int gainAdjustmentQ16;
            if (gainQ16 != state.PrevGainQ16)
            {
                gainAdjustmentQ16 = SilkFixedPoint.Div32VarQ(state.PrevGainQ16, gainQ16, 16);
                for (int i = 0; i < MaxLpcOrder; i++)
                {
                    sLpcQ14[i] = SilkFixedPoint.Smulww(gainAdjustmentQ16, sLpcQ14[i]);
                }
            }
            else
            {
                gainAdjustmentQ16 = 1 << 16;
            }

            state.PrevGainQ16 = gainQ16;

            Span<short> aQ12 = predCoef.Slice((k >> 1) * MaxLpcOrder, order);
            Span<int> residualQ14 = state.ResQ14;

            if (voiced)
            {
                int lag = state.PitchL[k];

                // Rewhitening at the start of the frame, and again at the third subframe when the
                // LPC coefficients change (RFC 6716 §4.2.7.9.1).
                if (k == 0 || (k == 2 && interpolationFactor < 4))
                {
                    int startIndex = ltpMemoryLength - lag - order - (LtpOrder / 2);
                    if (k == 2)
                    {
                        output[..(2 * subframeLength)].CopyTo(state.OutBuf.AsSpan(ltpMemoryLength));
                    }

                    LpcAnalysisFilter(
                        state.SLtp,
                        startIndex,
                        state.OutBuf,
                        startIndex + (k * subframeLength),
                        aQ12,
                        ltpMemoryLength - startIndex,
                        order);

                    if (k == 0)
                    {
                        inverseGainQ31 = SilkFixedPoint.Lshift(
                            SilkFixedPoint.Smulwb(inverseGainQ31, ltpScaleQ14), 2);
                    }

                    for (int i = 0; i < lag + (LtpOrder / 2); i++)
                    {
                        state.SLtpQ15[ltpBufferIndex - i - 1] = SilkFixedPoint.Smulwb(
                            inverseGainQ31, state.SLtp[ltpMemoryLength - i - 1]);
                    }
                }
                else if (gainAdjustmentQ16 != 1 << 16)
                {
                    for (int i = 0; i < lag + (LtpOrder / 2); i++)
                    {
                        state.SLtpQ15[ltpBufferIndex - i - 1] = SilkFixedPoint.Smulww(
                            gainAdjustmentQ16, state.SLtpQ15[ltpBufferIndex - i - 1]);
                    }
                }

                int predictionLag = ltpBufferIndex - lag + (LtpOrder / 2);
                int ltpCoef = k * LtpOrder;
                for (int i = 0; i < subframeLength; i++)
                {
                    int ltpPredictionQ13 = 2;
                    ltpPredictionQ13 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ13, state.SLtpQ15[predictionLag], state.LtpCoefQ14[ltpCoef]);
                    ltpPredictionQ13 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ13, state.SLtpQ15[predictionLag - 1], state.LtpCoefQ14[ltpCoef + 1]);
                    ltpPredictionQ13 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ13, state.SLtpQ15[predictionLag - 2], state.LtpCoefQ14[ltpCoef + 2]);
                    ltpPredictionQ13 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ13, state.SLtpQ15[predictionLag - 3], state.LtpCoefQ14[ltpCoef + 3]);
                    ltpPredictionQ13 = SilkFixedPoint.Smlawb(
                        ltpPredictionQ13, state.SLtpQ15[predictionLag - 4], state.LtpCoefQ14[ltpCoef + 4]);
                    predictionLag++;

                    residualQ14[i] = SilkFixedPoint.Add(
                        state.ExcQ14[excitationOffset + i],
                        SilkFixedPoint.Lshift(ltpPredictionQ13, 1));
                    state.SLtpQ15[ltpBufferIndex] = SilkFixedPoint.Lshift(residualQ14[i], 1);
                    ltpBufferIndex++;
                }
            }
            else
            {
                state.ExcQ14.AsSpan(excitationOffset, subframeLength).CopyTo(residualQ14);
            }

            // Short-term LPC synthesis (RFC 6716 §4.2.7.9.2).
            for (int i = 0; i < subframeLength; i++)
            {
                int lpcPredictionQ10 = SilkFixedPoint.Rshift(order, 1);
                for (int j = 0; j < order; j++)
                {
                    lpcPredictionQ10 = SilkFixedPoint.Smlawb(
                        lpcPredictionQ10, sLpcQ14[MaxLpcOrder + i - 1 - j], aQ12[j]);
                }

                sLpcQ14[MaxLpcOrder + i] = SilkFixedPoint.Add(
                    residualQ14[i], SilkFixedPoint.Lshift(lpcPredictionQ10, 4));
                output[(k * subframeLength) + i] = (short)SilkFixedPoint.Sat16(
                    SilkFixedPoint.RshiftRound(
                        SilkFixedPoint.Smulww(sLpcQ14[MaxLpcOrder + i], gainQ10), 8));
            }

            sLpcQ14[subframeLength..(subframeLength + MaxLpcOrder)].CopyTo(sLpcQ14);
            excitationOffset += subframeLength;
        }

        sLpcQ14[..MaxLpcOrder].CopyTo(state.SLpcQ14Buf);

        // Shift the LTP output history and append this frame (decode_frame.c).
        int moveLength = ltpMemoryLength - frameLength;
        state.OutBuf.AsSpan(frameLength, moveLength).CopyTo(state.OutBuf);
        output.CopyTo(state.OutBuf.AsSpan(moveLength));

        state.FirstFrameAfterReset = false;
    }

    /// <summary>Converts mid/side to left/right (RFC 6716 §4.2.8, <c>silk_stereo_MS_to_LR</c>).</summary>
    private void StereoUnmix(SilkChannelFrame mid, int frameOffset, Span<short> pcmOut)
    {
        int frameLength = _frameLength;
        int interpLength = 8 * (_rateHz / 1000);

        _tmpMid[0] = _stereoMid[0];
        _tmpMid[1] = _stereoMid[1];
        _tmpSide[0] = _stereoSide[0];
        _tmpSide[1] = _stereoSide[1];
        _mid.AsSpan(0, frameLength).CopyTo(_tmpMid.AsSpan(2));
        _side.AsSpan(0, frameLength).CopyTo(_tmpSide.AsSpan(2));

        _stereoMid[0] = _tmpMid[frameLength];
        _stereoMid[1] = _tmpMid[frameLength + 1];
        _stereoSide[0] = _tmpSide[frameLength];
        _stereoSide[1] = _tmpSide[frameLength + 1];

        int pred0Q13 = _stereoPredPrevQ13[0];
        int pred1Q13 = _stereoPredPrevQ13[1];
        int denomQ16 = (1 << 16) / interpLength;
        int delta0Q13 = SilkFixedPoint.RshiftRound(
            SilkFixedPoint.Smulbb(mid.StereoWeight0Q13 - _stereoPredPrevQ13[0], denomQ16), 16);
        int delta1Q13 = SilkFixedPoint.RshiftRound(
            SilkFixedPoint.Smulbb(mid.StereoWeight1Q13 - _stereoPredPrevQ13[1], denomQ16), 16);

        for (int n = 0; n < interpLength; n++)
        {
            pred0Q13 += delta0Q13;
            pred1Q13 += delta1Q13;
            PredictSide(n, pred0Q13, pred1Q13);
        }

        pred0Q13 = mid.StereoWeight0Q13;
        pred1Q13 = mid.StereoWeight1Q13;
        for (int n = interpLength; n < frameLength; n++)
        {
            PredictSide(n, pred0Q13, pred1Q13);
        }

        _stereoPredPrevQ13[0] = mid.StereoWeight0Q13;
        _stereoPredPrevQ13[1] = mid.StereoWeight1Q13;

        for (int n = 0; n < frameLength; n++)
        {
            int left = _tmpMid[n + 1] + _tmpSide[n + 1];
            int right = _tmpMid[n + 1] - _tmpSide[n + 1];
            pcmOut[frameOffset + (2 * n)] = (short)SilkFixedPoint.Sat16(left);
            pcmOut[frameOffset + (2 * n) + 1] = (short)SilkFixedPoint.Sat16(right);
        }
    }

    /// <summary>Predicts one side sample during stereo unmixing (RFC 6716 §4.2.8).</summary>
    private void PredictSide(int n, int pred0Q13, int pred1Q13)
    {
        int sum = SilkFixedPoint.Lshift(
            SilkFixedPoint.Add(_tmpMid[n] + _tmpMid[n + 2], SilkFixedPoint.Lshift(_tmpMid[n + 1], 1)),
            9);
        sum = SilkFixedPoint.Smlawb(SilkFixedPoint.Lshift(_tmpSide[n + 1], 8), sum, pred0Q13);
        sum = SilkFixedPoint.Smlawb(sum, SilkFixedPoint.Lshift(_tmpMid[n + 1], 11), pred1Q13);
        _tmpSide[n + 1] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(sum, 8));
    }

    /// <summary>Runs the LPC analysis filter used for LTP rewhitening (RFC 6716 §4.2.7.9.1).</summary>
    private static void LpcAnalysisFilter(
        short[] output,
        int outputOffset,
        short[] input,
        int inputOffset,
        ReadOnlySpan<short> coefficients,
        int length,
        int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int inputIndex = inputOffset + ix - 1;
            int predictionQ12 = SilkFixedPoint.Smulbb(input[inputIndex], coefficients[0]);
            for (int j = 1; j < order; j++)
            {
                predictionQ12 = unchecked(predictionQ12
                    + (SilkFixedPoint.Smulbb(input[inputIndex - j], coefficients[j])));
            }

            int residualQ12 = unchecked(
                SilkFixedPoint.Lshift(input[inputIndex + 1], 12) - predictionQ12);
            output[outputOffset + ix] = (short)SilkFixedPoint.Sat16(
                SilkFixedPoint.RshiftRound(residualQ12, 12));
        }

        output.AsSpan(outputOffset, order).Clear();
    }
}
