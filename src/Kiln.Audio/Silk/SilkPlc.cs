// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// Packet-loss concealment, comfort noise and re-entry gluing for the SILK layer
/// (RFC 6716 §4.4; Appendix A, <c>silk/PLC.c</c> and <c>silk/CNG.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the second half of <see cref="SilkSynthesis"/>. It is a direct transcription of the
/// fixed-point reference PLC: the previous frame's LPC and LTP state is re-whitened, the LTP
/// history is extrapolated with a decaying pitch gain and an increasing pitch lag, a random
/// excitation taken from the quietest of the last two subframes is mixed in, and the result is
/// filtered through the broadened previous LPC filter. Consecutive losses attenuate the pitch
/// and noise gains, so the output fades rather than ringing. <see cref="PlcGlue"/> fades a good
/// frame back in after a run of losses, and <see cref="Cng"/> adds comfort noise during loss,
/// matching the reference.
/// </para>
/// <para>
/// The concealed frame is pushed through the same <c>outBuf</c> and LPC history the normal
/// synthesis uses, so the first good frame after a loss resumes from the concealment rather
/// than from stale state. Every scratch buffer is allocated once, so concealment allocates
/// nothing.
/// </para>
/// </remarks>
internal sealed partial class SilkSynthesis
{
    private const int PlcLtpOrder = 5;
    private const int RandBufferSize = 128;

    private const int BweCoefficientQ16 = 64881; // 0.99 in Q16
    private const int VoicedPitchGainStartMinQ14 = 11469; // 0.7 in Q14
    private const int VoicedPitchGainStartMaxQ14 = 15565; // 0.95 in Q14
    private const int MaxPitchLagMilliseconds = 18;
    private const int Log2InverseLpcGainHighThreshold = 3;
    private const int Log2InverseLpcGainLowThreshold = 8;
    private const int PitchDriftFactorQ16 = 655; // 0.01 in Q16
    private const int HarmonicAttenuationLateQ15 = 31130; // 0.95 in Q15
    private const int HarmonicAttenuationEarlyQ15 = 32440; // 0.99 in Q15
    private const int RandomAttenuationVoicedLateQ15 = 26214; // 0.8 in Q15
    private const int RandomAttenuationVoicedEarlyQ15 = 31130; // 0.95 in Q15
    private const int RandomAttenuationUnvoicedLateQ15 = 29491; // 0.9 in Q15
    private const int RandomAttenuationUnvoicedEarlyQ15 = 32440; // 0.99 in Q15

    private const int CngGainSmoothingQ16 = 4634; // 0.25^(1/4)
    private const int CngNlsfSmoothingQ16 = 16348; // 0.25
    private const int CngBufferMaskMax = 255;

    /// <summary>Per-channel PLC state carried between frames (Appendix A, <c>silk_PLC_struct</c>).</summary>
    private sealed class PlcChannelState
    {
        /// <summary>Pitch lag in Q8, slowly drifting upwards while concealing.</summary>
        public int PitchLagQ8;

        /// <summary>The two most recent subframe gains, Q16.</summary>
        public readonly int[] PreviousGainQ16 = new int[2];

        /// <summary>LTP filter tapped for the concealed excitation, Q14.</summary>
        public readonly short[] LtpCoefficientQ14 = new short[PlcLtpOrder];

        /// <summary>Previous frame's LPC coefficients, Q12.</summary>
        public readonly short[] PreviousLpcQ12 = new short[MaxLpcOrder];

        /// <summary>Previous frame's LTP scaling factor, Q14.</summary>
        public int PreviousLtpScaleQ14;

        /// <summary>Random excitation gain, Q14.</summary>
        public int RandomScaleQ14;

        /// <summary>Random number generator state for the excitation.</summary>
        public int RandomSeed;

        /// <summary>Subframe length of the frame the state was captured from.</summary>
        public int SubframeLength;

        /// <summary>Number of subframes of the frame the state was captured from.</summary>
        public int SubframeCount;

        /// <summary>Number of consecutive lost frames.</summary>
        public int LossCount;

        /// <summary>Signal type of the last decoded frame (RFC 6716 §4.2.7.3).</summary>
        public int PreviousSignalType;

        /// <summary>Energy of the last concealed frame, for the glue fade.</summary>
        public int ConcealedEnergy;

        /// <summary>Shift applied to <see cref="ConcealedEnergy"/>.</summary>
        public int ConcealedEnergyShift;

        /// <summary>True when the previous frame was concealed.</summary>
        public bool LastFrameLost;

        /// <summary>Comfort noise: smoothed NLSFs, Q15.</summary>
        public readonly short[] CngNlsfQ15 = new short[MaxLpcOrder];

        /// <summary>Comfort noise: smoothed gain, Q16.</summary>
        public int CngGainQ16;

        /// <summary>Comfort noise: random number generator state.</summary>
        public int CngRandomSeed;

        /// <summary>Comfort noise: excitation history, Q14 (one frame plus the previous subframes).</summary>
        public readonly int[] CngExcitationQ14 = new int[MaxFrameLength];

        /// <summary>Comfort noise: LPC synthesis memory, Q10.</summary>
        public readonly int[] CngSynthesisStateQ10 = new int[MaxLpcOrder];

        /// <summary>Sample rate the state was captured at, in kHz.</summary>
        public int RateKhz;

        public void Clear()
        {
            PitchLagQ8 = 0;
            PreviousGainQ16[0] = 1 << 16;
            PreviousGainQ16[1] = 1 << 16;
            LtpCoefficientQ14.AsSpan().Clear();
            PreviousLpcQ12.AsSpan().Clear();
            PreviousLtpScaleQ14 = 0;
            RandomScaleQ14 = 0;
            RandomSeed = 0;
            SubframeLength = 20;
            SubframeCount = 2;
            LossCount = 0;
            PreviousSignalType = 0;
            ConcealedEnergy = 0;
            ConcealedEnergyShift = 0;
            LastFrameLost = false;
            CngNlsfQ15.AsSpan().Clear();
            CngGainQ16 = 0;
            CngRandomSeed = 3176576;
            CngExcitationQ14.AsSpan().Clear();
            CngSynthesisStateQ10.AsSpan().Clear();
            RateKhz = 0;
        }
    }

    private readonly PlcChannelState[] _plc = [new PlcChannelState(), new PlcChannelState()];
    private readonly SilkChannelFrame _plcStereoFrame = new();

    private readonly int[] _plcSlpcQ14 = new int[2 * MaxFrameLength];
    private readonly short[] _plcSltp = new short[MaxFrameLength];
    private readonly short[] _plcAQ12 = new short[MaxLpcOrder];
    private readonly short[] _plcExcBuf = new short[2 * MaxSubframeLength];
    private readonly int[] _plcCngSignalQ10 = new int[MaxFrameLength + MaxLpcOrder];
    private readonly int[] _plcInverseGainWork = new int[2 * MaxLpcOrder];

    /// <summary>Clears every PLC and comfort-noise state (called by <see cref="Reset"/>).</summary>
    private void ResetPlc()
    {
        foreach (PlcChannelState plc in _plc)
        {
            plc.Clear();
        }
    }

    /// <summary>
    /// Conceals a whole lost Opus frame (one or more SILK frames) at the internal sample rate.
    /// </summary>
    /// <param name="frameDurationMs">Duration of the lost Opus frame: 10, 20, 40 or 60 ms.</param>
    /// <param name="pcmOut">
    /// Receives <c>frameDurationMs * Rate / 1000</c> interleaved samples per channel; it must
    /// hold that many samples for every coded channel.
    /// </param>
    internal void Conceal(int frameDurationMs, Span<short> pcmOut)
    {
        if (_rateHz == 0)
        {
            pcmOut.Clear();
            return;
        }

        int silkFrames = frameDurationMs <= 10 ? 1 : frameDurationMs / 20;
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
            ConcealChannel(0, _mid.AsSpan(0, _frameLength));
            PlcGlue(0, _mid.AsSpan(0, _frameLength));
            Cng(0, _mid.AsSpan(0, _frameLength), null);

            if (_channels == 2)
            {
                // A mid-only run leaves the side channel silent; otherwise it is concealed too.
                if (_previousDecodeOnlyMiddle)
                {
                    _side.AsSpan(0, _frameLength).Clear();
                }
                else
                {
                    ConcealChannel(1, _side.AsSpan(0, _frameLength));
                    PlcGlue(1, _side.AsSpan(0, _frameLength));
                    Cng(1, _side.AsSpan(0, _frameLength), null);
                }
            }

            int frameOffset = f * _frameLength * _channels;
            WriteMixedFrame(frameOffset, pcmOut);
        }
    }

    /// <summary>
    /// Synthesises the in-band FEC (LBRR) frames of this packet, concealing any interval whose
    /// LBRR frame is absent.
    /// </summary>
    /// <param name="frame">The decoded packet's SILK parameters, including the LBRR frames.</param>
    /// <param name="pcmOut">Receives the interleaved internal-rate samples.</param>
    internal void SynthesizeFec(SilkFrameResult frame, Span<short> pcmOut)
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
            SilkChannelFrame mid = frame.Lbrr(f, 0);
            bool midCoded = frame.IsLbrrCoded(f, 0);
            bool sideCoded = _channels == 2 && frame.IsLbrrCoded(f, 1);
            bool sidePresent = sideCoded || !_previousDecodeOnlyMiddle;

            if (_channels == 2 && sideCoded && _previousDecodeOnlyMiddle)
            {
                _channelState[1].ResetPredictionMemory();
            }

            if (midCoded)
            {
                SynthesizeChannel(_channelState[0], mid, _mid.AsSpan(0, _frameLength));
                PlcUpdate(0, mid);
            }
            else
            {
                ConcealChannel(0, _mid.AsSpan(0, _frameLength));
            }

            PlcGlue(0, _mid.AsSpan(0, _frameLength));
            Cng(0, _mid.AsSpan(0, _frameLength), midCoded ? mid : null);

            if (_channels == 2)
            {
                if (sideCoded)
                {
                    SilkChannelFrame side = frame.Lbrr(f, 1);
                    SynthesizeChannel(_channelState[1], side, _side.AsSpan(0, _frameLength));
                    PlcUpdate(1, side);
                    PlcGlue(1, _side.AsSpan(0, _frameLength));
                    Cng(1, _side.AsSpan(0, _frameLength), side);
                }
                else if (sidePresent)
                {
                    ConcealChannel(1, _side.AsSpan(0, _frameLength));
                    PlcGlue(1, _side.AsSpan(0, _frameLength));
                    Cng(1, _side.AsSpan(0, _frameLength), null);
                }
                else
                {
                    _side.AsSpan(0, _frameLength).Clear();
                }
            }

            int frameOffset = f * _frameLength * _channels;
            WriteMixedFrame(frameOffset, pcmOut);
            _previousDecodeOnlyMiddle = _channels == 2 && mid.IsMidOnly;
        }
    }

    /// <summary>Applies the shared rate/channel setup used by both normal and FEC synthesis.</summary>
    private void ConfigureForFrame(SilkFrameResult frame)
    {
        if (_rateHz != frame.InternalRateHz)
        {
            Reset();
            _rateHz = frame.InternalRateHz;
            _channels = frame.Channels;
            _bandwidthIndex = _rateHz switch
            {
                8000 => 0,
                12000 => 1,
                _ => 2,
            };
            _lpcOrder = _bandwidthIndex == 2 ? MaxLpcOrder : 10;
        }
        else if (_channels != frame.Channels)
        {
            _channelState[1].Reset();
            if (frame.Channels == 2)
            {
                _stereoSide.AsSpan().Clear();
                _stereoPredPrevQ13.AsSpan().Clear();
            }

            _previousDecodeOnlyMiddle = false;
            _channels = frame.Channels;
        }

        _subframeCount = frame.SubframeCount;
        _subframeLength = SubframeMilliseconds * (_rateHz / 1000);
        _frameLength = _subframeCount * _subframeLength;
        _ltpMemoryLength = LtpMemoryMilliseconds * (_rateHz / 1000);
    }

    /// <summary>Mixes the concealed mid/side frame to the output channels.</summary>
    private void WriteMixedFrame(int frameOffset, Span<short> pcmOut)
    {
        if (_channels == 2)
        {
            _plcStereoFrame.StereoWeight0Q13 = _stereoPredPrevQ13[0];
            _plcStereoFrame.StereoWeight1Q13 = _stereoPredPrevQ13[1];
            StereoUnmix(_plcStereoFrame, frameOffset, pcmOut);
        }
        else
        {
            _mid.AsSpan(0, _frameLength).CopyTo(pcmOut[frameOffset..]);
            _stereoMid[0] = _mid[_frameLength - 2];
            _stereoMid[1] = _mid[_frameLength - 1];
        }
    }

    /// <summary>Updates the PLC state from a good frame (Appendix A, <c>silk_PLC_update</c>).</summary>
    private void PlcUpdate(int channel, SilkChannelFrame frame)
    {
        PlcChannelState plc = _plc[channel];
        EnsurePlcRate(plc);

        plc.LossCount = 0;
        plc.PreviousSignalType = (int)frame.SignalType;
        int ltpGainQ14 = 0;
        if (frame.IsVoiced)
        {
            int subframes = _subframeCount;
            for (int j = 0; j * _subframeLength < frame.PitchLag[subframes - 1]; j++)
            {
                if (j == subframes)
                {
                    break;
                }

                int subframe = subframes - 1 - j;
                int candidate = 0;
                for (int i = 0; i < PlcLtpOrder; i++)
                {
                    candidate += _channelState[channel].LtpCoefQ14[(subframe * PlcLtpOrder) + i];
                }

                if (candidate > ltpGainQ14)
                {
                    ltpGainQ14 = candidate;
                    _channelState[channel].LtpCoefQ14
                        .AsSpan(subframe * PlcLtpOrder, PlcLtpOrder)
                        .CopyTo(plc.LtpCoefficientQ14);
                    plc.PitchLagQ8 = frame.PitchLag[subframe] << 8;
                }
            }

            Array.Clear(plc.LtpCoefficientQ14);
            plc.LtpCoefficientQ14[PlcLtpOrder / 2] = (short)ltpGainQ14;

            if (ltpGainQ14 < VoicedPitchGainStartMinQ14)
            {
                int scaleQ10 = (int)(((long)VoicedPitchGainStartMinQ14 << 10) / Math.Max(ltpGainQ14, 1));
                for (int i = 0; i < PlcLtpOrder; i++)
                {
                    plc.LtpCoefficientQ14[i] =
                        (short)SilkFixedPoint.Rshift(
                            SilkFixedPoint.Smulbb(plc.LtpCoefficientQ14[i], scaleQ10), 10);
                }
            }
            else if (ltpGainQ14 > VoicedPitchGainStartMaxQ14)
            {
                int scaleQ14 = (int)(((long)VoicedPitchGainStartMaxQ14 << 14) / Math.Max(ltpGainQ14, 1));
                for (int i = 0; i < PlcLtpOrder; i++)
                {
                    plc.LtpCoefficientQ14[i] =
                        (short)SilkFixedPoint.Rshift(
                            SilkFixedPoint.Smulbb(plc.LtpCoefficientQ14[i], scaleQ14), 14);
                }
            }
        }
        else
        {
            plc.PitchLagQ8 = ((_rateHz / 1000) * 18) << 8;
            Array.Clear(plc.LtpCoefficientQ14);
        }

        Span<short> currentCoef = _channelState[channel].PredCoefQ12.AsSpan(MaxLpcOrder, _lpcOrder);
        currentCoef.CopyTo(plc.PreviousLpcQ12);
        plc.PreviousLtpScaleQ14 = frame.IsVoiced ? frame.LtpScalingQ14 : 0;

        int subframeCount = _subframeCount;
        plc.PreviousGainQ16[0] = frame.GainQ16[subframeCount - 2];
        plc.PreviousGainQ16[1] = frame.GainQ16[subframeCount - 1];
        plc.SubframeLength = _subframeLength;
        plc.SubframeCount = subframeCount;
    }

    /// <summary>Generates one concealed channel frame (Appendix A, <c>silk_PLC_conceal</c>).</summary>
    private void ConcealChannel(int channel, Span<short> output)
    {
        ChannelState state = _channelState[channel];
        PlcChannelState plc = _plc[channel];
        EnsurePlcRate(plc);

        int order = _lpcOrder;
        int subframeLength = plc.SubframeLength;
        int subframeCount = _subframeCount;
        int ltpMemoryLength = _ltpMemoryLength;
        int frameLength = _frameLength;
        int rateKhz = _rateHz / 1000;

        if (state.FirstFrameAfterReset)
        {
            plc.PreviousLpcQ12.AsSpan().Clear();
        }

        int previousGainQ10First = SilkFixedPoint.Rshift(plc.PreviousGainQ16[0], 6);
        int previousGainQ10Last = SilkFixedPoint.Rshift(plc.PreviousGainQ16[1], 6);

        // The random excitation comes from the quieter of the last two subframes.
        Span<short> excBuf = _plcExcBuf.AsSpan(0, 2 * subframeLength);
        for (int k = 0; k < 2; k++)
        {
            int gainQ10 = k == 0 ? previousGainQ10First : previousGainQ10Last;
            int source = (k + plc.SubframeCount - 2) * subframeLength;
            for (int i = 0; i < subframeLength; i++)
            {
                excBuf[(k * subframeLength) + i] = (short)SilkFixedPoint.Sat16(
                    SilkFixedPoint.Rshift(
                        SilkFixedPoint.Smulww(state.ExcQ14[source + i], gainQ10), 8));
            }
        }

        SumSqrShift(excBuf, subframeLength, out int energy1, out int shift1);
        SumSqrShift(excBuf[subframeLength..], subframeLength, out int energy2, out int shift2);

        int randomOffset = SilkFixedPoint.Rshift(energy1, shift2) < SilkFixedPoint.Rshift(energy2, shift1)
            ? Math.Max(0, ((plc.SubframeCount - 1) * subframeLength) - RandBufferSize)
            : Math.Max(0, (plc.SubframeCount * subframeLength) - RandBufferSize);

        int randomScaleQ14 = plc.RandomScaleQ14;
        int harmonicGainQ15 = plc.LossCount > 0 ? HarmonicAttenuationLateQ15 : HarmonicAttenuationEarlyQ15;
        int randomGainQ15 = plc.PreviousSignalType == (int)SilkSignalType.Voiced
            ? (plc.LossCount > 0 ? RandomAttenuationVoicedLateQ15 : RandomAttenuationVoicedEarlyQ15)
            : (plc.LossCount > 0 ? RandomAttenuationUnvoicedLateQ15 : RandomAttenuationUnvoicedEarlyQ15);

        Bwexpander(plc.PreviousLpcQ12, order, BweCoefficientQ16);
        Span<short> aQ12 = _plcAQ12.AsSpan(0, order);
        plc.PreviousLpcQ12.AsSpan(0, order).CopyTo(aQ12);

        if (plc.LossCount == 0)
        {
            randomScaleQ14 = 1 << 14;
            if (plc.PreviousSignalType == (int)SilkSignalType.Voiced)
            {
                for (int i = 0; i < PlcLtpOrder; i++)
                {
                    randomScaleQ14 -= plc.LtpCoefficientQ14[i];
                }

                randomScaleQ14 = Math.Max(3277, randomScaleQ14);
                randomScaleQ14 = SilkFixedPoint.Rshift(
                    SilkFixedPoint.Smulbb(randomScaleQ14, plc.PreviousLtpScaleQ14), 14);
            }
            else
            {
                int inverseGainQ30 = LpcInversePredictionGain(plc.PreviousLpcQ12, order);
                int downScaleQ30 = Math.Min(1 << 30 >> Log2InverseLpcGainHighThreshold, inverseGainQ30);
                downScaleQ30 = Math.Max(1 << 30 >> Log2InverseLpcGainLowThreshold, downScaleQ30);
                downScaleQ30 = SilkFixedPoint.Lshift(downScaleQ30, Log2InverseLpcGainHighThreshold);
                randomGainQ15 = SilkFixedPoint.Rshift(SilkFixedPoint.Smulwb(downScaleQ30, randomGainQ15), 14);
            }
        }

        int randomSeed = plc.RandomSeed;
        int lag = SilkFixedPoint.RshiftRound(plc.PitchLagQ8, 8);
        int sltpBufferIndex = ltpMemoryLength;

        // Re-whiten the LTP history with the broadened LPC coefficients.
        int index = ltpMemoryLength - lag - order - (PlcLtpOrder / 2);
        LpcAnalysisFilter(_plcSltp, index, state.OutBuf, index, aQ12, ltpMemoryLength - index, order);

        int inverseGainQ30Scaled = SilkFixedPoint.Inverse32VarQ(plc.PreviousGainQ16[1], 46);
        inverseGainQ30Scaled = Math.Min(inverseGainQ30Scaled, SilkFixedPoint.Int32Max >> 1);
        for (int i = index + order; i < ltpMemoryLength; i++)
        {
            _plcSlpcQ14[i] = SilkFixedPoint.Smulwb(inverseGainQ30Scaled, _plcSltp[i]);
        }

        // LTP synthesis, attenuating the gains and drifting the lag up every subframe.
        for (int k = 0; k < subframeCount; k++)
        {
            int predictedLag = sltpBufferIndex - lag + (PlcLtpOrder / 2);
            for (int i = 0; i < subframeLength; i++)
            {
                int predictionQ12 = 2;
                predictionQ12 = SilkFixedPoint.Smlawb(
                    predictionQ12, _plcSlpcQ14[predictedLag], plc.LtpCoefficientQ14[0]);
                predictionQ12 = SilkFixedPoint.Smlawb(
                    predictionQ12, _plcSlpcQ14[predictedLag - 1], plc.LtpCoefficientQ14[1]);
                predictionQ12 = SilkFixedPoint.Smlawb(
                    predictionQ12, _plcSlpcQ14[predictedLag - 2], plc.LtpCoefficientQ14[2]);
                predictionQ12 = SilkFixedPoint.Smlawb(
                    predictionQ12, _plcSlpcQ14[predictedLag - 3], plc.LtpCoefficientQ14[3]);
                predictionQ12 = SilkFixedPoint.Smlawb(
                    predictionQ12, _plcSlpcQ14[predictedLag - 4], plc.LtpCoefficientQ14[4]);
                predictedLag++;

                randomSeed = SilkFixedPoint.Rand(randomSeed);
                index = SilkFixedPoint.Rshift(randomSeed, 25) & (RandBufferSize - 1);
                _plcSlpcQ14[sltpBufferIndex] = SilkFixedPoint.Lshift(
                    SilkFixedPoint.Smlawb(
                        predictionQ12, state.ExcQ14[randomOffset + index], randomScaleQ14),
                    2);
                sltpBufferIndex++;
            }

            for (int j = 0; j < PlcLtpOrder; j++)
            {
                plc.LtpCoefficientQ14[j] = (short)SilkFixedPoint.Rshift(
                    SilkFixedPoint.Smulbb(harmonicGainQ15, plc.LtpCoefficientQ14[j]), 15);
            }

            randomScaleQ14 = SilkFixedPoint.Rshift(
                SilkFixedPoint.Smulbb(randomScaleQ14, randomGainQ15), 15);

            plc.PitchLagQ8 = SilkFixedPoint.Smlawb(plc.PitchLagQ8, plc.PitchLagQ8, PitchDriftFactorQ16);
            plc.PitchLagQ8 = Math.Min(
                plc.PitchLagQ8, SilkFixedPoint.Lshift(MaxPitchLagMilliseconds * rateKhz, 8));
            lag = SilkFixedPoint.RshiftRound(plc.PitchLagQ8, 8);
        }

        // LPC synthesis from the concealed excitation.
        int lpcState = ltpMemoryLength - MaxLpcOrder;
        state.SLpcQ14Buf.AsSpan(0, MaxLpcOrder).CopyTo(_plcSlpcQ14.AsSpan(lpcState, MaxLpcOrder));
        for (int i = 0; i < frameLength; i++)
        {
            int predictionQ10 = SilkFixedPoint.Rshift(order, 1);
            for (int j = 0; j < order; j++)
            {
                predictionQ10 = SilkFixedPoint.Smlawb(
                    predictionQ10, _plcSlpcQ14[lpcState + MaxLpcOrder + i - 1 - j], aQ12[j]);
            }

            _plcSlpcQ14[lpcState + MaxLpcOrder + i] = SilkFixedPoint.Add(
                _plcSlpcQ14[lpcState + MaxLpcOrder + i],
                SilkFixedPoint.Lshift(predictionQ10, 4));

            output[i] = (short)SilkFixedPoint.Sat16(
                SilkFixedPoint.Sat16(
                    SilkFixedPoint.RshiftRound(
                        SilkFixedPoint.Smulww(
                            _plcSlpcQ14[lpcState + MaxLpcOrder + i], previousGainQ10Last),
                        8)));
        }

        _plcSlpcQ14.AsSpan(lpcState + frameLength, MaxLpcOrder).CopyTo(state.SLpcQ14Buf);

        // Push the concealed frame into the LTP history, as decode_frame.c does before gluing.
        int moveLength = ltpMemoryLength - frameLength;
        state.OutBuf.AsSpan(frameLength, moveLength).CopyTo(state.OutBuf);
        output.CopyTo(state.OutBuf.AsSpan(moveLength));

        plc.RandomSeed = randomSeed;
        plc.RandomScaleQ14 = randomScaleQ14;
        plc.LossCount++;
    }

    /// <summary>Fades a good frame back in after concealment (Appendix A, <c>silk_PLC_glue_frames</c>).</summary>
    private void PlcGlue(int channel, Span<short> frame)
    {
        PlcChannelState plc = _plc[channel];
        if (plc.LossCount != 0)
        {
            SumSqrShift(frame, frame.Length, out plc.ConcealedEnergy, out plc.ConcealedEnergyShift);
            plc.LastFrameLost = true;
            return;
        }

        if (!plc.LastFrameLost)
        {
            return;
        }

        SumSqrShift(frame, frame.Length, out int energy, out int energyShift);
        if (energyShift > plc.ConcealedEnergyShift)
        {
            plc.ConcealedEnergy = SilkFixedPoint.Rshift(plc.ConcealedEnergy, energyShift - plc.ConcealedEnergyShift);
        }
        else if (energyShift < plc.ConcealedEnergyShift)
        {
            energy = SilkFixedPoint.Rshift(energy, plc.ConcealedEnergyShift - energyShift);
        }

        if (energy > plc.ConcealedEnergy)
        {
            int lz = SilkFixedPoint.Clz32(plc.ConcealedEnergy) - 1;
            plc.ConcealedEnergy = SilkFixedPoint.Lshift(plc.ConcealedEnergy, lz);
            energy = SilkFixedPoint.Rshift(energy, Math.Max(24 - lz, 0));
            int fractionQ24 = plc.ConcealedEnergy / Math.Max(energy, 1);
            int gainQ16 = SilkFixedPoint.Lshift(SilkFixedPoint.SqrtApprox(fractionQ24), 4);
            int slopeQ16 = (unchecked((1 << 16) - gainQ16) / frame.Length) << 2;
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (short)SilkFixedPoint.Smulwb(gainQ16, frame[i]);
                gainQ16 += slopeQ16;
                if (gainQ16 > 1 << 16)
                {
                    break;
                }
            }
        }

        plc.LastFrameLost = false;
    }

    /// <summary>Comfort noise generation (Appendix A, <c>silk_CNG</c>).</summary>
    /// <param name="channel">Channel index.</param>
    /// <param name="frame">The frame in place; receives the added comfort noise.</param>
    /// <param name="decoded">The decoded parameters, or null for a lost frame.</param>
    private void Cng(int channel, Span<short> frame, SilkChannelFrame? decoded)
    {
        PlcChannelState plc = _plc[channel];
        EnsurePlcRate(plc);
        int order = _lpcOrder;

        if (plc.LossCount == 0 && plc.PreviousSignalType == (int)SilkSignalType.Inactive && decoded is not null)
        {
            for (int i = 0; i < order; i++)
            {
                plc.CngNlsfQ15[i] = (short)SilkFixedPoint.Add(
                    plc.CngNlsfQ15[i],
                    SilkFixedPoint.Smulwb(
                        _channelState[channel].PrevNlsfQ15[i] - plc.CngNlsfQ15[i], CngNlsfSmoothingQ16));
            }

            int maxGainQ16 = 0;
            int bestSubframe = 0;
            for (int i = 0; i < _subframeCount; i++)
            {
                if (decoded.GainQ16[i] > maxGainQ16)
                {
                    maxGainQ16 = decoded.GainQ16[i];
                    bestSubframe = i;
                }
            }

            int subframeLength = _subframeLength;
            Array.Copy(
                plc.CngExcitationQ14, 0, plc.CngExcitationQ14, subframeLength,
                (_subframeCount - 1) * subframeLength);
            Array.Copy(
                _channelState[channel].ExcQ14, bestSubframe * subframeLength,
                plc.CngExcitationQ14, 0, subframeLength);

            for (int i = 0; i < _subframeCount; i++)
            {
                plc.CngGainQ16 = SilkFixedPoint.Smlawb(
                    plc.CngGainQ16, decoded.GainQ16[i] - plc.CngGainQ16, CngGainSmoothingQ16);
            }
        }

        if (plc.LossCount == 0)
        {
            plc.CngSynthesisStateQ10.AsSpan(0, order).Clear();
            return;
        }

        int length = frame.Length;
        Span<int> signalQ10 = _plcCngSignalQ10.AsSpan(0, MaxLpcOrder + length);
        int excitationMask = CngBufferMaskMax;
        while (excitationMask > length)
        {
            excitationMask >>= 1;
        }

        int seed = plc.CngRandomSeed;
        for (int i = 0; i < length; i++)
        {
            seed = SilkFixedPoint.Rand(seed);
            int excitationIndex = SilkFixedPoint.Rshift(seed, 24) & excitationMask;
            signalQ10[MaxLpcOrder + i] = SilkFixedPoint.Sat16(
                SilkFixedPoint.Smulww(plc.CngExcitationQ14[excitationIndex], plc.CngGainQ16 >> 4));
        }

        plc.CngRandomSeed = seed;

        Span<short> aQ12 = _plcAQ12.AsSpan(0, order);
        SilkNlsf.ConvertToLpc(aQ12, plc.CngNlsfQ15, order);
        plc.CngSynthesisStateQ10.AsSpan(0, order).CopyTo(signalQ10);

        for (int i = 0; i < length; i++)
        {
            int sumQ6 = SilkFixedPoint.Rshift(order, 1);
            for (int j = 0; j < order; j++)
            {
                sumQ6 = SilkFixedPoint.Smlawb(
                    sumQ6, signalQ10[MaxLpcOrder + i - 1 - j], aQ12[j]);
            }

            signalQ10[MaxLpcOrder + i] = SilkFixedPoint.Add(
                signalQ10[MaxLpcOrder + i], SilkFixedPoint.Lshift(sumQ6, 4));
            frame[i] = (short)SilkFixedPoint.AddSat16(
                frame[i], SilkFixedPoint.RshiftRound(sumQ6, 6));
        }

        signalQ10.Slice(length, MaxLpcOrder).CopyTo(plc.CngSynthesisStateQ10);
    }

    /// <summary>Resets the PLC and CNG state when the internal rate changes.</summary>
    private void EnsurePlcRate(PlcChannelState plc)
    {
        int rateKhz = _rateHz / 1000;
        if (plc.RateKhz == rateKhz)
        {
            return;
        }

        plc.Clear();
        plc.RateKhz = rateKhz;
        plc.PitchLagQ8 = _frameLength << 7;
        plc.SubframeLength = 20;
        plc.SubframeCount = 2;

        int order = _bandwidthIndex == 2 ? MaxLpcOrder : 10;
        int step = SilkFixedPoint.Int16Max / (order + 1);
        int accumulator = 0;
        for (int i = 0; i < order; i++)
        {
            accumulator += step;
            plc.CngNlsfQ15[i] = (short)accumulator;
        }
    }

    /// <summary>Broadens an LP filter by a chirp factor (Appendix A, <c>silk_bwexpander</c>).</summary>
    private static void Bwexpander(Span<short> ar, int order, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < order - 1; i++)
        {
            ar[i] = (short)SilkFixedPoint.RshiftRound(SilkFixedPoint.Mul(chirpQ16, ar[i]), 16);
            chirpQ16 += SilkFixedPoint.RshiftRound(SilkFixedPoint.Mul(chirpQ16, chirpMinusOneQ16), 16);
        }

        ar[order - 1] = (short)SilkFixedPoint.RshiftRound(SilkFixedPoint.Mul(chirpQ16, ar[order - 1]), 16);
    }

    /// <summary>Computes the sum of squares and a fit-it-in-32-bits shift (Appendix A, <c>silk_sum_sqr_shift</c>).</summary>
    private static void SumSqrShift(ReadOnlySpan<short> x, int length, out int energy, out int shift)
    {
        int nrg = 0;
        int shft = 0;
        int i;
        int len = length - 1;
        for (i = 0; i < len; i += 2)
        {
            nrg = unchecked(nrg + (x[i] * x[i]));
            nrg = unchecked(nrg + (x[i + 1] * x[i + 1]));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft = 2;
                break;
            }
        }

        for (; i < len; i += 2)
        {
            int temp = unchecked(x[i] * x[i]);
            temp = unchecked(temp + (x[i + 1] * x[i + 1]));
            nrg = unchecked(nrg + (int)((uint)temp >> shft));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft += 2;
            }
        }

        if (i == len && len >= 0)
        {
            int temp = unchecked(x[i] * x[i]);
            nrg = unchecked(nrg + (int)((uint)temp >> shft));
        }

        if ((nrg & unchecked((int)0xC0000000)) != 0)
        {
            nrg = (int)((uint)nrg >> 2);
            shft += 2;
        }

        energy = nrg;
        shift = shft;
    }

    /// <summary>Inverse LPC prediction gain in Q30 (Appendix A, <c>silk_LPC_inverse_pred_gain</c>).</summary>
    private int LpcInversePredictionGain(ReadOnlySpan<short> aQ12, int order)
    {
        const int qa = 24;
        const int aLimit = 16773022; // SILK_FIX_CONST(0.99975, 24)

        int newOffset = (order & 1) * MaxLpcOrder;
        int oldOffset = ((order & 1) ^ 1) * MaxLpcOrder;
        int[] work = _plcInverseGainWork;

        int dcResponse = 0;
        for (int k = 0; k < order; k++)
        {
            dcResponse += aQ12[k];
            work[newOffset + k] = SilkFixedPoint.Lshift(aQ12[k], qa - 12);
        }

        if (dcResponse >= 4096)
        {
            return 0;
        }

        int inverseGainQ30 = 1 << 30;
        for (int k = order - 1; k > 0; k--)
        {
            if (work[newOffset + k] > aLimit || work[newOffset + k] < -aLimit)
            {
                return 0;
            }

            int reflectionQ31 = -SilkFixedPoint.Lshift(work[newOffset + k], 31 - qa);
            int reflectionMult1Q30 = (1 << 30) - SilkFixedPoint.Smmul(reflectionQ31, reflectionQ31);
            int mult2Q = 32 - SilkFixedPoint.Clz32(SilkFixedPoint.Abs(reflectionMult1Q30));
            int reflectionMult2 = SilkFixedPoint.Inverse32VarQ(reflectionMult1Q30, mult2Q + 30);

            inverseGainQ30 = SilkFixedPoint.Lshift(
                SilkFixedPoint.Smmul(inverseGainQ30, reflectionMult1Q30), 2);

            (oldOffset, newOffset) = (newOffset, oldOffset);
            for (int n = 0; n < k; n++)
            {
                int tempQa = unchecked(work[oldOffset + n]
                    - Mul32FracQ(work[oldOffset + k - n - 1], reflectionQ31, 31));
                work[newOffset + n] = Mul32FracQ(tempQa, reflectionMult2, mult2Q);
            }
        }

        if (work[newOffset] > aLimit || work[newOffset] < -aLimit)
        {
            return 0;
        }

        int finalReflectionQ31 = -SilkFixedPoint.Lshift(work[newOffset], 31 - qa);
        int finalMult1Q30 = (1 << 30) - SilkFixedPoint.Smmul(finalReflectionQ31, finalReflectionQ31);
        inverseGainQ30 = SilkFixedPoint.Lshift(SilkFixedPoint.Smmul(inverseGainQ30, finalMult1Q30), 2);
        return inverseGainQ30;
    }

    /// <summary>Rounded 64-bit fractional product (Appendix A, <c>MUL32_FRAC_Q</c>).</summary>
    private static int Mul32FracQ(int a, int b, int q) =>
        (int)SilkFixedPoint.RshiftRound64(SilkFixedPoint.Smull(a, b), q);
}
