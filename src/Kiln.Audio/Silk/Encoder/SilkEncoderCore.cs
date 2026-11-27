// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;
using Kiln.Audio.Encoder;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The mono SILK encoder: configuration, the per-frame pipeline and the packet rate-control loop
/// (RFC 6716 §5.2.3; Appendix A, <c>silk/enc_API.c</c>, <c>silk/control_codec.c</c>,
/// <c>silk/control_SNR.c</c> and <c>silk/fixed/encode_frame_FIX.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// An Opus frame of 10, 20, 40 or 60 ms is resampled to the internal rate and split into one,
/// one, two or three 20 ms SILK frames (10 ms packets use a single 10 ms frame with two
/// subframes). The VAD is run for every SILK frame first, because the header VAD bits are the
/// first symbols of the payload; the analysis and quantization then run frame by frame.
/// </para>
/// <para>
/// Everything is allocated in the constructor. <see cref="Encode"/> only mutates existing
/// storage, so it allocates nothing after construction.
/// </para>
/// </remarks>
internal sealed class SilkEncoderCore : ISilkLayerEncoder
{
    private const int MaxPacketBytes = 1276;
    private const int IndependentCoding = 0;
    private const int ConditionalCoding = 2;
    private const int MaximumIterations = 6;

    private readonly SilkEncoderState _state = new();
    private readonly SilkEncoderControl _control = new();
    private readonly SilkPitchAnalysis _pitchAnalysis;
    private readonly SilkInputResampler _resampler;
    private readonly short[] _resampled;
    private readonly byte[] _rangeBuffer = new byte[MaxPacketBytes];
    private readonly byte[] _rangeLowerBytes = new byte[MaxPacketBytes];
    private int _rangeLowerCount;
    private readonly int[] _xfwQ3 = new int[SilkEncoderMath.MaxFrameLength];
    private readonly int[] _xfwLbrrQ3 = new int[SilkEncoderMath.MaxFrameLength];
    private readonly short[] _residual = new short[(2 * SilkEncoderMath.MaxFrameLength) + SilkEncoderMath.LaPitchMax];

    private readonly int[] _speechActivity = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _frameWeight = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _frameTargetRate = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _inputTilt = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _vadSignalType = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[][] _bandQuality =
    [
        new int[SilkEncoderMath.VadBandCount],
        new int[SilkEncoderMath.VadBandCount],
        new int[SilkEncoderMath.VadBandCount],
    ];

    private readonly SilkNsqDelayedDecision _delayedDecision = new();
    private readonly SilkNsqDelayedDecision _delayedDecisionStart = new();
    private readonly SilkNsqDelayedDecision _delayedDecisionLower = new();
    private readonly SilkNsqDelayedDecision _delayedDecisionLbrr = new();
    private readonly sbyte[] _delayedDecisionPulses = new sbyte[SilkEncoderMath.MaxFrameLength];
    private readonly short[] _delayedDecisionOutput = new short[SilkEncoderMath.MaxFrameLength];
    private readonly short[] _delayedDecisionPredictionQ12 = new short[2 * SilkEncoderMath.MaxLpcOrder];
    private readonly int _delayedDecisionStates;
    private readonly SilkSideInfo _lastEncodedIndices = new();
    private readonly SilkSideInfo[] _encodedPerFrame =
    [
        new SilkSideInfo(),
        new SilkSideInfo(),
        new SilkSideInfo(),
    ];

    private readonly int _apiRate;
    private readonly int _internalRate;
    private readonly int _durationMs;
    private readonly int _silkFrames;
    private readonly int _bitrateBps;
    private readonly int _complexity;
    private readonly bool _useCbr;
    private const int BitReservoirDecayTimeMs = 500;
    private const int LbrrHighRateLambdaBonusQ10 = 150; // FEC-QUAL-1, measured on the Enc2 FEC stream
    private const int LbrrSpeechActivityThresholdQ8 = 77; // SILK_FIX_CONST(0.3, 8)
    private int _bitsExceeded;

    // Variable high-pass filter (opus_encode_native hp_cutoff and silk_HP_variable_cutoff).
    private const int HpMinCutoffHz = 60;
    private const int HpMaxCutoffHz = 100;
    private int _hpSmth1Q15 = ((SilkEncoderMath.Lin2Log(HpMinCutoffHz << 16) - (16 << 7)) << 8);
    private int _hpSmth2Q15 = SilkEncoderMath.Lin2Log(HpMinCutoffHz) << 8;
    private int _hpMem0;
    private int _hpMem1;
    private int _hpPrevActivityQ8;
    private int _hpPrevBand0Q15;
    private short[] _hpBuffer = [];
    private readonly AudioKernelTier _tier = AudioKernels.Select();

    /// <summary>Creates and configures the encoder for one fixed mode.</summary>
    internal SilkEncoderCore(
        int apiRate,
        int bandwidthHz,
        int durationMs,
        int bitrateBps,
        int complexity,
        bool useCbr)
    {
        _apiRate = apiRate;
        _internalRate = bandwidthHz;
        _durationMs = durationMs;
        _bitrateBps = bitrateBps;
        _complexity = Math.Clamp(complexity, 0, 10);
        _useCbr = useCbr;
        _silkFrames = durationMs == 10 ? 1 : durationMs / 20;
        // Above 1.2 bits per internal-rate sample the quantization is fine enough that the
        // delayed-decision trellis stops paying: a single greedy state is cheaper and slightly
        // better. Below it the trellis and warped shaping are what let the encoder beat libopus.
        _state.HighRate = _bitrateBps * 10 >= _internalRate * 12 ? 1 : 0;
        // Complexity 0 and 1 use a four-state trellis (the reference uses one) so the delayed
        // decision and warped shaping also pay off at the fastest setting.
        _delayedDecisionStates = _state.HighRate != 0
            ? 1
            : (_complexity < 2 ? 4 : SilkNsqDelayedDecision.StatesForComplexity(_complexity));

        _resampler = new SilkInputResampler(apiRate, bandwidthHz);
        _resampled = new short[bandwidthHz * durationMs / 1000];
        _pitchAnalysis = new SilkPitchAnalysis(bandwidthHz / 1000, durationMs == 10 ? 2 : 4);

        Configure();
    }

    /// <summary>Runs the SILK encoder for one Opus frame and writes the payload.</summary>
    /// <param name="pcm">Mono input at the API rate for exactly one Opus frame.</param>
    /// <param name="packet">Destination for the payload (no TOC byte).</param>
    /// <returns>The number of payload bytes written; the final range is in <see cref="FinalRange"/>.</returns>
    internal int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        var rangeEncoder = new RangeEncoder(_rangeBuffer);
        int payloadBits = EncodeInto(ref rangeEncoder, pcm, Math.Min(packet.Length + 1, 1276));
        int payloadBytes = (payloadBits + 7) >> 3;
        if (payloadBytes > packet.Length || payloadBytes > _rangeBuffer.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The packet buffer must hold at least {payloadBytes + 1} bytes; got {packet.Length + 1}."),
                nameof(packet));
        }

        // SILK writes no raw bits, so the range-coded bytes already occupy the front of the
        // buffer; the frame is simply the first `written` bytes after finalization.
        FinalRange = rangeEncoder.Rng;
        rangeEncoder.Done();
        int written = rangeEncoder.RangeBytes;
        if (written > packet.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The packet buffer must hold at least {written + 1} bytes; got {packet.Length + 1}."),
                nameof(packet));
        }

        _rangeBuffer.AsSpan(0, written).CopyTo(packet);
        return written;
    }

    /// <summary>
    /// Runs the SILK encoder for one Opus frame into a caller-owned range encoder, without
    /// finalizing it. This is the hook hybrid frames use: the SILK symbols come first and the
    /// CELT layer continues in the same coder (RFC 6716 §4.5.2).
    /// </summary>
    /// <param name="rangeEncoder">
    /// The frame's range encoder. It must be fresh (no symbols written) or contain only symbols
    /// as the decoder will see them before this frame.
    /// </param>
    /// <param name="pcm">Mono input at the API rate for exactly one Opus frame.</param>
    /// <param name="maxDataBytes">The frame's byte capacity, including the TOC byte.</param>
    /// <returns>The number of payload bits written; the final range is not updated.</returns>
    internal int EncodeInto(ref RangeEncoder rangeEncoder, ReadOnlySpan<short> pcm, int maxDataBytes)
    {
        ApplyInputHighPass(pcm);
        _resampler.Process(_hpBuffer.AsSpan(0, pcm.Length), _resampled);

        int frameLength = _state.FrameLength;
        for (int f = 0; f < _silkFrames; f++)
        {
            _resampled.AsSpan(f * frameLength, frameLength).CopyTo(_state.InputBuf.AsSpan(1, frameLength));
            SilkVad.GetSpeechActivity(_state, _state.InputBuf.AsSpan(1, frameLength));
            _speechActivity[f] = _state.SpeechActivityQ8;
            _inputTilt[f] = _state.InputTiltQ15;
            _state.InputQualityBandsQ15.AsSpan().CopyTo(_bandQuality[f]);

            // RFC 6716 Appendix A, silk_encode_do_VAD_FIX: inactivity is signal type 0, activity
            // starts as unvoiced and the pitch search may upgrade it to voiced.
            if (_state.SpeechActivityQ8 < 13)
            {
                _vadSignalType[f] = 0;
                _state.NoSpeechCounter++;
            }
            else
            {
                _state.NoSpeechCounter = 0;
                _vadSignalType[f] = 1;
            }
        }

        for (int f = 0; f < _silkFrames; f++)
        {
            rangeEncoder.EncodeBitLogp(_vadSignalType[f] != 0 ? 1 : 0, 1);
        }

        rangeEncoder.EncodeBitLogp(0, 1); // LBRR flag; in-band FEC is a later lane.

        // Rate control as in opus_encode_native and silk_Encode: the SILK rate is derived from the
        // byte budget (one byte is reserved for the TOC), the per-frame target rate drives the SNR
        // through the bit reservoir, and maxBits is only the packet capacity. Using the target
        // budget as maxBits would force the gain loop to inflate every gain.
        int frameRate = 48000 / (48 * _durationMs);
        int bitrate = _bitrateBps;
        maxDataBytes = Math.Min(maxDataBytes, 1276);
        if (_useCbr)
        {
            int cbrBytes = Math.Min((bitrate + (4 * frameRate)) / (8 * frameRate), maxDataBytes);
            bitrate = cbrBytes * 8 * frameRate;
            maxDataBytes = cbrBytes;
        }

        int bytesTarget = Math.Min(maxDataBytes, (int)((long)bitrate * _durationMs / 8000)) - 1;
        int silkBitrate = 8 * bytesTarget * frameRate;
        int packetMaxBits = Math.Min(1275, maxDataBytes - 1) * 8;
        if (_useCbr)
        {
            packetMaxBits = (silkBitrate * _durationMs / 8000) * 8;
            silkBitrate = Math.Max(1, silkBitrate - 2000);
        }

        int packetBits = silkBitrate * _durationMs / 1000;
        bool weightedAllocation = _state.HighRate != 0 && _silkFrames > 1;
        if (weightedAllocation)
        {
            ComputeFrameTargetRates(silkBitrate);
        }

        for (int f = 0; f < _silkFrames; f++)
        {
            HpVariableCutoff();
            _resampled.AsSpan(f * frameLength, frameLength).CopyTo(_state.InputBuf.AsSpan(1, frameLength));
            _state.SpeechActivityQ8 = _speechActivity[f];
            _state.InputTiltQ15 = _inputTilt[f];
            _bandQuality[f].AsSpan().CopyTo(_state.InputQualityBandsQ15);
            _state.Indices.SignalType = _vadSignalType[f];
            _state.VadFlags[f] = _vadSignalType[f];

            int targetRate;
            if (weightedAllocation)
            {
                targetRate = _frameTargetRate[f] - (_bitsExceeded * 1000 / BitReservoirDecayTimeMs);
                targetRate = Math.Clamp(targetRate, 5000, 2 * silkBitrate);
            }
            else
            {
                int framesLeft = _silkFrames - f;
                int frameBits = (packetBits - (rangeEncoder.Tell() >> 1)) / framesLeft;
                targetRate = frameBits * (_durationMs == 10 ? 100 : 50);
                targetRate -= _bitsExceeded * 1000 / BitReservoirDecayTimeMs;
                targetRate = Math.Max(Math.Min(targetRate, silkBitrate), 5000);
            }

            ControlSnr(targetRate);

            int maxBits = packetMaxBits;
            if (_silkFrames == 2 && f == 0)
            {
                maxBits = maxBits * 3 / 5;
            }
            else if (_silkFrames == 3)
            {
                if (f == 0)
                {
                    maxBits = maxBits * 2 / 5;
                }
                else if (f == 1)
                {
                    maxBits = maxBits * 3 / 4;
                }
            }

            int useCbr = _useCbr && f == _silkFrames - 1 ? 1 : 0;
            EncodeFrame(ref rangeEncoder, f, f == 0 ? IndependentCoding : ConditionalCoding, maxBits, useCbr);
            _hpPrevActivityQ8 = _speechActivity[f];
            _hpPrevBand0Q15 = _bandQuality[f][0];
        }

        int payloadBits = rangeEncoder.Tell();
        _bitsExceeded = Math.Clamp(_bitsExceeded + (((payloadBits + 7) >> 3) * 8) - packetBits, 0, 10000);
        return payloadBits;
    }

    /// <summary>
    /// Whether the voice-activity-weighted per-frame allocation applies. The packet layer drives
    /// the frames directly and must use this to reproduce the same rate control as
    /// <see cref="EncodeInto"/>; the high-rate regime and multi-frame packets are its only scope.
    /// </summary>
    internal bool UsesWeightedFrameAllocation => _state.HighRate != 0 && _silkFrames > 1;

    /// <summary>Prepares the weighted per-frame target rates for one packet.</summary>
    /// <param name="silkBitrate">The packet's SILK rate, from the byte budget.</param>
    internal void PrepareWeightedFrameAllocation(int silkBitrate) => ComputeFrameTargetRates(silkBitrate);

    /// <summary>Gets the weighted target rate for one frame, before the bit-reservoir damping.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal int WeightedFrameTargetRate(int frameIndex) => _frameTargetRate[frameIndex];

    /// <summary>
    /// Distributes the packet bit budget across the SILK frames of a packet by voice activity, so
    /// the target SNR spends more bits on active frames and fewer on inactive ones while the mean
    /// stays at the configured rate. The per-frame totals are normalized, so the packet budget is
    /// preserved over the block; the bit reservoir damps the actual excursions.
    /// </summary>
    private void ComputeFrameTargetRates(int silkBitrate)
    {
        int totalWeight = 0;
        for (int f = 0; f < _silkFrames; f++)
        {
            _frameWeight[f] = 128 + Math.Min(_speechActivity[f], 255);
            totalWeight += _frameWeight[f];
        }

        for (int f = 0; f < _silkFrames; f++)
        {
            _frameTargetRate[f] = silkBitrate * _frameWeight[f] * _silkFrames / totalWeight;
        }
    }

    /// <summary>
    /// Applies the Opus-level variable high-pass to the input frame (<c>hp_cutoff</c> in
    /// <c>opus_encoder.c</c>, used for the VoIP application).
    /// </summary>
    private void ApplyInputHighPass(ReadOnlySpan<short> pcm)
    {
        if (_hpBuffer.Length < pcm.Length)
        {
            _hpBuffer = new short[pcm.Length];
        }

        _hpSmth2Q15 = SilkFixedPoint.Smlawb(_hpSmth2Q15, _hpSmth1Q15 - _hpSmth2Q15, 983);
        int cutoffHz = SilkEncoderMath.Log2Lin(_hpSmth2Q15 >> 8);

        int fcQ19 = SilkEncoderMath.Div32_16(SilkEncoderMath.Smulbb(2471, cutoffHz), _apiRate / 1000);
        int rQ28 = (1 << 28) - SilkEncoderMath.Mul(471, fcQ19);
        int b0 = rQ28;
        int b1 = -rQ28 << 1;
        int b2 = rQ28;
        int rQ22 = rQ28 >> 6;
        int a0 = SilkFixedPoint.Smulww(rQ22, SilkFixedPoint.Smulww(fcQ19, fcQ19) - (2 << 22));
        int a1 = SilkFixedPoint.Smulww(rQ22, rQ22);

        int a0L = -a0 & 0x3FFF;
        int a0U = -a0 >> 14;
        int a1L = -a1 & 0x3FFF;
        int a1U = -a1 >> 14;
        for (int k = 0; k < pcm.Length; k++)
        {
            int input = pcm[k];
            int out32Q14 = SilkFixedPoint.Smlawb(_hpMem0, b0, input) << 2;
            _hpMem0 = _hpMem1 + SilkEncoderMath.RshiftRound(SilkFixedPoint.Smulwb(out32Q14, a0L), 14);
            _hpMem0 = SilkFixedPoint.Smlawb(_hpMem0, out32Q14, a0U);
            _hpMem0 = SilkFixedPoint.Smlawb(_hpMem0, b1, input);
            _hpMem1 = SilkEncoderMath.RshiftRound(SilkFixedPoint.Smulwb(out32Q14, a1L), 14);
            _hpMem1 = SilkFixedPoint.Smlawb(_hpMem1, out32Q14, a1U);
            _hpMem1 = SilkFixedPoint.Smlawb(_hpMem1, b2, input);
            _hpBuffer[k] = (short)SilkEncoderMath.Sat16((out32Q14 + (1 << 14) - 1) >> 14);
        }
    }

    /// <summary>Adapts the high-pass cutoff to the pitch statistics (<c>silk_HP_variable_cutoff</c>).</summary>
    private void HpVariableCutoff()
    {
        if (_state.PrevSignalType != 2)
        {
            return;
        }

        int pitchFreqHzQ16 = SilkEncoderMath.Div32_16((_state.FsKhz * 1000) << 16, _state.PrevLag);
        int pitchFreqLogQ7 = SilkEncoderMath.Lin2Log(pitchFreqHzQ16) - (16 << 7);
        int qualityQ15 = _hpPrevBand0Q15;
        pitchFreqLogQ7 = SilkFixedPoint.Smlawb(
            pitchFreqLogQ7,
            SilkFixedPoint.Smulwb((-qualityQ15) << 2, qualityQ15),
            pitchFreqLogQ7 - (SilkEncoderMath.Lin2Log(HpMinCutoffHz << 16) - (16 << 7)));

        int deltaFreqQ7 = pitchFreqLogQ7 - (_hpSmth1Q15 >> 8);
        if (deltaFreqQ7 < 0)
        {
            deltaFreqQ7 *= 3;
        }

        deltaFreqQ7 = Math.Clamp(deltaFreqQ7, -51, 51);
        _hpSmth1Q15 = SilkFixedPoint.Smlawb(
            _hpSmth1Q15, SilkEncoderMath.Smulbb(_hpPrevActivityQ8, deltaFreqQ7), 6554);
        _hpSmth1Q15 = Math.Clamp(
            _hpSmth1Q15,
            SilkEncoderMath.Lin2Log(HpMinCutoffHz) << 8,
            SilkEncoderMath.Lin2Log(HpMaxCutoffHz) << 8);
    }

    // ---------------------------------------------------------------------------------------
    // Frame-level hooks used by the multi-channel packet encoder. They expose the same steps
    // EncodeInto runs, so one packet can interleave mid and side frames and the packet layer can
    // own the header, LBRR and DTX decisions.
    // ---------------------------------------------------------------------------------------

    /// <summary>Number of SILK frames in one packet.</summary>
    internal int FrameCount => _silkFrames;

    /// <summary>Samples in one SILK frame at the internal rate.</summary>
    internal int FrameLength => _state.FrameLength;

    /// <summary>The SILK encoder state (analysis output and quantizer state).</summary>
    internal SilkEncoderState State => _state;

    /// <summary>The per-frame analysis output.</summary>
    internal SilkEncoderControl Control => _control;

    /// <summary>High-passes, resamples and stores the internal-rate frame.</summary>
    /// <param name="pcm">One channel's input at the API rate for exactly one Opus frame.</param>
    internal void LoadPacket(ReadOnlySpan<short> pcm)
    {
        ApplyInputHighPass(pcm);
        _resampler.Process(_hpBuffer.AsSpan(0, pcm.Length), _resampled);
    }

    /// <summary>Copies one resampled frame into the frame input buffer.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal void SetInputFromResampled(int frameIndex)
    {
        int frameLength = _state.FrameLength;
        _resampled.AsSpan(frameIndex * frameLength, frameLength).CopyTo(_state.InputBuf.AsSpan(1, frameLength));
    }

    /// <summary>Replaces the frame input buffer, for the stereo mixer's mid or side signal.</summary>
    /// <param name="frame">One internal-rate frame.</param>
    internal void SetInputFrame(ReadOnlySpan<short> frame) =>
        frame.CopyTo(_state.InputBuf.AsSpan(1, frame.Length));

    /// <summary>Returns the resampled input of one frame.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal ReadOnlySpan<short> ResampledFrame(int frameIndex) =>
        _resampled.AsSpan(frameIndex * _state.FrameLength, _state.FrameLength);

    /// <summary>Runs the VAD on the current frame input and records the packet header's activity.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal void AnalyzeInput(int frameIndex)
    {
        int frameLength = _state.FrameLength;
        SilkVad.GetSpeechActivity(_state, _state.InputBuf.AsSpan(1, frameLength));
        _speechActivity[frameIndex] = _state.SpeechActivityQ8;
        _inputTilt[frameIndex] = _state.InputTiltQ15;
        _state.InputQualityBandsQ15.AsSpan().CopyTo(_bandQuality[frameIndex]);

        if (_state.SpeechActivityQ8 < 13)
        {
            _vadSignalType[frameIndex] = 0;
            _state.NoSpeechCounter++;
        }
        else
        {
            _vadSignalType[frameIndex] = 1;
            _state.NoSpeechCounter = 0;
            _state.InDtx = 0;
        }
    }

    /// <summary>Gets one frame's speech activity, Q8.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal int SpeechActivityQ8(int frameIndex) => _speechActivity[frameIndex];

    /// <summary>Gets one frame's VAD signal type: 0 inactive, 1 active.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal int VadSignalType(int frameIndex) => _vadSignalType[frameIndex];

    /// <summary>Resets the side channel's encoder memory after a mid-only interval.</summary>
    internal void ResetAfterMidOnly()
    {
        _state.Shape.Reset();
        _state.Prefilt.Reset();
        _state.PrefiltLbrr.Reset();
        _state.Nsq.Reset();
        _delayedDecision.Reset();
        _state.PrevNlsfQ15.AsSpan().Clear();
        _state.Lp.InLpState.AsSpan().Clear();
        _state.PrevLag = 100;
        _state.Nsq.LagPrev = 100;
        _state.Shape.LastGainIndex = 10;
        _state.PrevSignalType = 0;
        _state.Nsq.PrevGainQ16 = 65536;
        _state.FirstFrameAfterReset = 1;
    }

    /// <summary>Prepares one frame's state before quantization.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    /// <param name="refreshInput">Whether to copy this frame's resampled signal into the input buffer.</param>
    internal void PrepareFrame(int frameIndex, bool refreshInput = true)
    {
        HpVariableCutoff();
        if (refreshInput)
        {
            SetInputFromResampled(frameIndex);
        }

        _state.SpeechActivityQ8 = _speechActivity[frameIndex];
        _state.InputTiltQ15 = _inputTilt[frameIndex];
        _bandQuality[frameIndex].AsSpan().CopyTo(_state.InputQualityBandsQ15);
        _state.Indices.SignalType = _vadSignalType[frameIndex];
        _state.VadFlags[frameIndex] = _vadSignalType[frameIndex];
    }

    /// <summary>Updates the high-pass adaptation memory after a frame.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal void FinishFrame(int frameIndex)
    {
        _hpPrevActivityQ8 = _speechActivity[frameIndex];
        _hpPrevBand0Q15 = _bandQuality[frameIndex][0];
    }

    /// <summary>Encodes one channel's frame into an existing range coder.</summary>
    /// <param name="rangeEncoder">The packet's range encoder.</param>
    /// <param name="frameIndex">The SILK frame index.</param>
    /// <param name="conditionalCoding">0 independent, 2 conditional.</param>
    /// <param name="maxBits">The frame's bit budget.</param>
    /// <param name="useCbr">Whether the frame must hit the budget exactly.</param>
    internal void EncodeFrameInto(
        ref RangeEncoder rangeEncoder, int frameIndex, int conditionalCoding, int maxBits, int useCbr) =>
        EncodeFrame(ref rangeEncoder, frameIndex, conditionalCoding, maxBits, useCbr);

    /// <summary>Runs the SNR control for a channel target rate (Appendix A, <c>silk_control_SNR</c>).</summary>
    /// <param name="targetRateBps">The channel's target bitrate.</param>
    internal void SetTargetRate(int targetRateBps) => ControlSnr(targetRateBps);

    /// <summary>Writes a stored LBRR frame's side information and excitation.</summary>
    /// <param name="rangeEncoder">The packet's range encoder.</param>
    /// <param name="frameIndex">The SILK frame index the redundancy describes.</param>
    /// <param name="conditionalCoding">0 independent, 2 conditional.</param>
    internal void WriteLbrrFrame(ref RangeEncoder rangeEncoder, int frameIndex, int conditionalCoding)
    {
        SilkSideInfo saved = _state.Indices;
        int savedVad = _state.VadFlags[frameIndex];
        try
        {
            _state.Indices = _state.LbrrIndices[frameIndex];
            _state.VadFlags[frameIndex] = 1;
            SilkEntropyEncoder.EncodeIndices(ref rangeEncoder, _state, frameIndex, conditionalCoding);
            SilkEntropyEncoder.EncodePulses(
                ref rangeEncoder, _state.Indices.SignalType, _state.Indices.QuantOffsetType,
                _state.LbrrPulses[frameIndex], _state.FrameLength);
        }
        finally
        {
            _state.Indices = saved;
            _state.VadFlags[frameIndex] = savedVad;
        }
    }

    /// <summary>Clears the LBRR flags after their frames have been written.</summary>
    internal void ClearLbrrFlags() => _state.LbrrFlags.AsSpan(0, _silkFrames).Clear();

    /// <summary>Runs the delayed-decision quantizer over one frame into <paramref name="pulses"/>.</summary>
    private void Quantize(
        SilkEncoderState state,
        SilkEncoderControl control,
        SilkSideInfo indices,
        ReadOnlySpan<int> inputQ3,
        Span<short> pulses,
        SilkNsqDelayedDecision delayed,
        int[] tiltQ14)
    {
        control.PredCoefQ12[0].CopyTo(_delayedDecisionPredictionQ12.AsSpan(0, SilkEncoderMath.MaxLpcOrder));
        control.PredCoefQ12[1].CopyTo(
            _delayedDecisionPredictionQ12.AsSpan(SilkEncoderMath.MaxLpcOrder, SilkEncoderMath.MaxLpcOrder));
        var nsqParameters = new SilkNsqParameters(
            inputQ3[..state.FrameLength],
            _delayedDecisionPredictionQ12,
            control.LtpCoefQ14,
            control.Ar2Q13,
            control.HarmShapeGainQ14,
            tiltQ14,
            control.LfShpQ14,
            control.GainsQ16,
            control.PitchL,
            indices.SignalType,
            indices.QuantOffsetType,
            control.LambdaQ10,
            control.LtpScaleQ14,
            indices.Seed,
            indices.NlsfInterpCoefQ2,
            state.SubframeCount,
            state.SubframeLength,
            state.LtpMemLength,
            state.PredictLpcOrder,
            state.ShapingLpcOrder,
            state.WarpingQ16);

        int states = DelayedDecisionStatesOverride > 0 ? DelayedDecisionStatesOverride : _delayedDecisionStates;
        delayed.Run(nsqParameters, states, _delayedDecisionPulses, _delayedDecisionOutput, out int seed);
        // The surviving trellis state may have started from a different seed than the frame's.
        indices.Seed = seed;
        for (int i = 0; i < state.FrameLength; i++)
        {
            pulses[i] = _delayedDecisionPulses[i];
        }
    }

    /// <summary>Encodes this frame's low-bitrate redundancy at a raised gain.</summary>
    private void EncodeLbrrFrame(int frameIndex, int conditionalCoding)
    {
        SilkEncoderState state = _state;
        SilkEncoderControl control = _control;
        state.LbrrFlags[frameIndex] = 1;

        SilkSideInfo lbrr = state.LbrrIndices[frameIndex];
        lbrr.CopyFrom(state.Indices);

        Span<int> savedGains = stackalloc int[state.SubframeCount];
        control.GainsQ16.AsSpan(0, state.SubframeCount).CopyTo(savedGains);

        if (frameIndex == 0 || state.LbrrFlags[frameIndex - 1] == 0)
        {
            state.LbrrPrevLastGainIndex = state.Shape.LastGainIndex;
            lbrr.GainsIndices[0] = Math.Min(lbrr.GainsIndices[0] + state.LbrrGainIncreases, 63);
        }

        SilkEncoderAnalysis.DequantizeGains(
            control.GainsQ16, lbrr.GainsIndices, ref state.LbrrPrevLastGainIndex,
            conditionalCoding == ConditionalCoding ? 1 : 0, state.SubframeCount);

        _delayedDecisionLbrr.CopyFullFrom(_delayedDecision);
        // FEC-QUAL-1: the LBRR copy is requantized at a gain several steps coarser than the main
        // frame, so it wants a stronger rate penalty than the high-rate main path's lambda.
        int regularLambdaQ10 = control.LambdaQ10;
        if (state.HighRate != 0)
        {
            control.LambdaQ10 += LbrrHighRateLambdaBonusQ10;
        }
        // FEC-QUAL-1: the high-rate tilt boost is a main-path tuning; the coarse LBRR copy is
        // quantized from the reference-tilt prefilter output instead (identical when not high rate).
        bool separateLbrrFilter = state.HighRate != 0;
        Quantize(
            state, control, lbrr, separateLbrrFilter ? _xfwLbrrQ3 : _xfwQ3, state.LbrrPulses[frameIndex],
            _delayedDecisionLbrr, separateLbrrFilter ? control.TiltLbrrQ14 : control.TiltQ14);

        control.LambdaQ10 = regularLambdaQ10;
        savedGains.CopyTo(control.GainsQ16);
    }

    /// <summary>Encodes the SILK layer into a shared range coder (hybrid and SILK-only frames).</summary>
    void ISilkLayerEncoder.Encode(
        ref RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes) =>
        EncodeInto(ref encoder, pcm[..frameSamples], maxFrameBytes + 1);

    /// <summary>Gets the range-coder final value for the most recently encoded packet.</summary>
    internal uint FinalRange { get; private set; }

    /// <summary>Exposes the encoder state for internal diagnostics and tests.</summary>
    internal SilkEncoderState CurrentState => _state;

    /// <summary>
    /// Test hook: forces the trellis width. Zero keeps the width the bitrate and complexity choose.
    /// </summary>
    internal int DelayedDecisionStatesOverride { get; set; }

    /// <summary>Exposes the side information of the last actual encoding for diagnostics.</summary>
    internal SilkSideInfo LastEncodedIndices => _lastEncodedIndices;

    /// <summary>Exposes the side information of each SILK frame for diagnostics.</summary>
    internal SilkSideInfo EncodedIndicesForFrame(int frameIndex) => _encodedPerFrame[frameIndex];

    /// <summary>Exposes the per-frame analysis output for diagnostics.</summary>
    internal SilkEncoderControl CurrentControl => _control;

    /// <summary>Exposes the most recent prefiltered input for diagnostics.</summary>
    internal int[] PrefilteredInput => _xfwQ3;

    /// <summary>Exposes the resampled API-rate input for diagnostics.</summary>
    internal short[] ResampledInput => _resampled;

    private void Configure()
    {
        int fsKhz = _internalRate / 1000;
        int subframeCount = _durationMs == 10 ? 2 : 4;

        _state.ResetForBandwidthChange();
        _state.ApiFsHz = _apiRate;
        _state.MaxInternalFsHz = _internalRate;
        _state.MinInternalFsHz = _internalRate;
        _state.DesiredInternalFsHz = _internalRate;
        _state.FsKhz = fsKhz;
        _state.SubframeCount = subframeCount;
        _state.SubframeLength = SilkEncoderMath.SubFrameLengthMs * fsKhz;
        _state.FrameLength = _state.SubframeLength * subframeCount;
        _state.LtpMemLength = 20 * fsKhz;
        _state.LaPitch = 2 * fsKhz;
        _state.PacketSizeMs = _durationMs;
        _state.FramesPerPacket = _silkFrames;
        _state.FramesEncoded = 0;
        _state.FrameCounter = 0;
        _state.PrevLag = 100;
        _state.PrevSignalType = 0;
        _state.FirstFrameAfterReset = 1;
        _state.MaxPitchLag = 18 * fsKhz;
        _state.PredictLpcOrder = fsKhz == 16 ? 16 : 10;
        _state.PitchLpcWinLength = (subframeCount == 4 ? 22 : 14) * fsKhz;
        _state.UseCbr = _useCbr ? 1 : 0;
        _state.EcPrevSignalType = 0;
        _state.EcPrevLagIndex = 0;
        _state.Shape.LastGainIndex = 10;
        _state.Nsq.LagPrev = 100;
        _state.Nsq.PrevGainQ16 = 65536;
        _state.Prefilt.LagPrev = 100;
        _state.PrefiltLbrr.LagPrev = 100;
        _state.TargetRateBps = 0;
        _state.MuLtpQ9 = fsKhz switch
        {
            16 => 10,
            12 => 13,
            _ => 15,
        };

        // The warped noise-shaping filter is only valid in the delayed-decision quantizer
        // (silk_NSQ_del_dec), which this encoder now always uses. The reference only enables
        // warped shaping from complexity 4; because delayed decision runs at every complexity
        // here, warping is enabled everywhere, scaled by the internal rate.
        int complexity = _complexity;
        int warpingQ16 = fsKhz * 983; // WARPING_MULTIPLIER = 0.015 in Q16
        if (complexity < 2)
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMinComplexity;
            _state.PitchEstimationThresholdQ16 = 52429;
            _state.PitchEstimationLpcOrder = 6;
            _state.ShapingLpcOrder = 8;
            _state.LaShape = 3 * fsKhz;
            _state.UseInterpolatedNlsfs = 0;
            _state.LtpQuantLowComplexity = 0;
            _state.NlsfMsvqSurvivors = 8;
            _state.WarpingQ16 = warpingQ16;
        }
        else if (complexity < 4)
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMidComplexity;
            _state.PitchEstimationThresholdQ16 = 49807;
            _state.PitchEstimationLpcOrder = 8;
            _state.ShapingLpcOrder = 10;
            _state.LaShape = 5 * fsKhz;
            _state.UseInterpolatedNlsfs = 0;
            _state.LtpQuantLowComplexity = 0;
            _state.NlsfMsvqSurvivors = 4;
            _state.WarpingQ16 = warpingQ16;
        }
        else if (complexity < 6)
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMidComplexity;
            _state.PitchEstimationThresholdQ16 = 48497;
            _state.PitchEstimationLpcOrder = 10;
            _state.ShapingLpcOrder = 12;
            _state.LaShape = 5 * fsKhz;
            _state.UseInterpolatedNlsfs = 1;
            _state.LtpQuantLowComplexity = 0;
            _state.NlsfMsvqSurvivors = 8;
            _state.WarpingQ16 = warpingQ16;
        }
        else if (complexity < 8)
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMidComplexity;
            _state.PitchEstimationThresholdQ16 = 47186;
            _state.PitchEstimationLpcOrder = 12;
            _state.ShapingLpcOrder = 14;
            _state.LaShape = 5 * fsKhz;
            _state.UseInterpolatedNlsfs = 1;
            _state.LtpQuantLowComplexity = 0;
            _state.NlsfMsvqSurvivors = 16;
            _state.WarpingQ16 = warpingQ16;
        }
        else
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMaxComplexity;
            _state.PitchEstimationThresholdQ16 = 45875;
            _state.PitchEstimationLpcOrder = 16;
            _state.ShapingLpcOrder = 16;
            _state.LaShape = 5 * fsKhz;
            _state.UseInterpolatedNlsfs = 1;
            _state.LtpQuantLowComplexity = 0;
            _state.NlsfMsvqSurvivors = 32;
            _state.WarpingQ16 = warpingQ16;
        }

        _state.PitchEstimationLpcOrder = Math.Min(_state.PitchEstimationLpcOrder, _state.PredictLpcOrder);
        _state.ShapeWinLength = (5 * fsKhz) + (2 * _state.LaShape);

        // Warped shaping only helps when the quantization is coarse. At high bits per sample it
        // moves noise without lowering it, so it is disabled there (see the constructor).
        if (_state.HighRate != 0)
        {
            _state.WarpingQ16 = 0;
        }

        SilkVad.Initialize(_state.Vad);
    }

    private void ControlSnr(int targetRateBps)
    {
        int target = Math.Clamp(targetRateBps, 5000, 80000);
        if (target == _state.TargetRateBps)
        {
            return;
        }

        _state.TargetRateBps = target;
        int[] rateTable = _state.FsKhz switch
        {
            8 => SilkEncoderTables.TargetRateTableNb,
            12 => SilkEncoderTables.TargetRateTableMb,
            _ => SilkEncoderTables.TargetRateTableWb,
        };

        if (_state.SubframeCount == 2)
        {
            target -= 2200;
        }

        for (int k = 1; k < rateTable.Length; k++)
        {
            if (target <= rateTable[k])
            {
                int fractionQ6 = (target - rateTable[k - 1]) * 64 / (rateTable[k] - rateTable[k - 1]);
                _state.SnrDbQ7 = (SilkEncoderTables.SnrTableQ1[k - 1] << 6)
                    + (fractionQ6 * (SilkEncoderTables.SnrTableQ1[k] - SilkEncoderTables.SnrTableQ1[k - 1]));
                break;
            }
        }

        // The RFC 1.0 SNR table leaves about 2 kbps of the budget unused at 20 kbps. Spend it:
        // above the threshold the target SNR rises by up to 3 dB, which tracks the observed bit
        // usage back to the configured rate. At the high-rate side the table already spends the
        // budget, so the boost starts later there.
        int thresholdBps = _state.HighRate != 0 ? 14000 : 11000;
        const int boostCeilingQ7 = 3 * 128;
        if (target > thresholdBps)
        {
            int boostQ7 = Math.Min((target - thresholdBps) * boostCeilingQ7 / 6000, boostCeilingQ7);
            _state.SnrDbQ7 += boostQ7;
        }

        // SILK-QUAL-3: at narrowband 12 kbps the high-rate single-state quantizer is bit-starved
        // relative to libopus; one extra dB of target SNR flips both frame durations above it.
        // It is applied only in the high-rate regime, leaving the low-rate wins untouched.
        if (_state.HighRate != 0 && _state.FsKhz == 8)
        {
            _state.SnrDbQ7 += 128;
        }
    }

    private void EncodeFrame(ref RangeEncoder rangeEncoder, int frameIndex, int conditionalCoding, int maxBits, int useCbr)
    {
        // One switch per frame; everything below it is a direct call specialised to the tier.
        switch (_tier)
        {
            case AudioKernelTier.Avx2Fma:
                EncodeFrameCore<Avx2FmaKernels>(ref rangeEncoder, frameIndex, conditionalCoding, maxBits, useCbr);
                break;
            case AudioKernelTier.AdvSimdArm64:
                EncodeFrameCore<AdvSimdArm64Kernels>(ref rangeEncoder, frameIndex, conditionalCoding, maxBits, useCbr);
                break;
            case AudioKernelTier.Vector128:
                EncodeFrameCore<Vector128Kernels>(ref rangeEncoder, frameIndex, conditionalCoding, maxBits, useCbr);
                break;
            default:
                EncodeFrameCore<ScalarKernels>(ref rangeEncoder, frameIndex, conditionalCoding, maxBits, useCbr);
                break;
        }
    }

    private void EncodeFrameCore<TK>(ref RangeEncoder rangeEncoder, int frameIndex, int conditionalCoding, int maxBits, int useCbr)
        where TK : struct, ISilkEncoderKernels
    {
        SilkEncoderState state = _state;
        SilkEncoderControl control = _control;
        int frameLength = state.FrameLength;
        int subframeLength = state.SubframeLength;
        int subframeCount = state.SubframeCount;

        EncProf.Mark(0, "silk other (resample/vad/wrap)");
        state.Indices.Seed = state.FrameCounter++ & 3;

        // Copy the new frame next to the shape look-ahead, then run the analysis. The frame input
        // buffer holds this frame's signal; the stereo mixer may have replaced it with the mid or
        // side signal.
        int frameOffset = state.LtpMemLength;
        state.InputBuf.AsSpan(1, frameLength)
            .CopyTo(state.XBuf.AsSpan(frameOffset + (5 * state.FsKhz)));

        _pitchAnalysis.FindPitchLags<TK>(state, control, _residual, state.XBuf, frameOffset);
        EncProf.Mark(1, "silk pitch analysis");
        SilkEncoderAnalysis.NoiseShapeAnalysis<TK>(
            state, control, _residual.AsSpan(state.LtpMemLength), state.XBuf, frameOffset);
        EncProf.Mark(2, "silk noise shape analysis");
        SilkEncoderAnalysis.FindPredictionCoefficients<TK>(
            state, control, _residual, state.XBuf, frameOffset, conditionalCoding);
        EncProf.Mark(3, "silk find pred coefs (burg/ltp/nlsf)");
        SilkEncoderAnalysis.ProcessGains(state, control, conditionalCoding);
        EncProf.Mark(4, "silk process gains");
        SilkEncoderAnalysis.Prefilter(state, control, _xfwQ3, state.XBuf, frameOffset, state.Prefilt, control.TiltQ14);
        if (state.LbrrEnabled != 0 && state.HighRate != 0)
        {
            // The reference-tilt prefilter runs every frame so its filter memory tracks the signal.
            SilkEncoderAnalysis.Prefilter(
                state, control, _xfwLbrrQ3, state.XBuf, frameOffset, state.PrefiltLbrr, control.TiltLbrrQ14);
        }

        EncProf.Mark(5, "silk noise-shape prefilter");

        // Low-bitrate redundancy reuses this frame's parameters at a higher gain
        // (RFC 6716 Appendix A, silk_LBRR_encode_FIX).
        if (state.LbrrEnabled != 0 && state.SpeechActivityQ8 > LbrrSpeechActivityThresholdQ8)
        {
            EncodeLbrrFrame(frameIndex, conditionalCoding);
        }

        EncProf.Mark(6, "silk lbrr");
        int gainMultiplierQ8 = 256;
        bool foundLower = false;
        bool foundUpper = false;
        int gainsId = SilkEncoderAnalysis.GainsId(state.Indices.GainsIndices, subframeCount);
        int gainsIdLower = -1;
        int gainsIdUpper = -1;
        int nBits;
        int nBitsLower = 0;
        int nBitsUpper = 0;
        int gainMultiplierLower = 0;
        int gainMultiplierUpper = 0;
        int lastGainIndexLower = 0;

        RangeEncoder savedStart = rangeEncoder;
        RangeEncoder savedLower = default;
        _delayedDecisionStart.CopyFrom(_delayedDecision);
        int seedStart = state.Indices.Seed;
        int ecPrevLagStart = state.EcPrevLagIndex;
        int ecPrevSignalStart = state.EcPrevSignalType;

        for (int iter = 0; ; iter++)
        {
            if (gainsId == gainsIdLower)
            {
                nBits = nBitsLower;
            }
            else if (gainsId == gainsIdUpper)
            {
                nBits = nBitsUpper;
            }
            else
            {
                if (iter > 0)
                {
                    rangeEncoder = savedStart;
                    _delayedDecision.CopyFrom(_delayedDecisionStart);
                    state.Indices.Seed = seedStart;
                    state.EcPrevLagIndex = ecPrevLagStart;
                    state.EcPrevSignalType = ecPrevSignalStart;
                }

                Quantize(state, control, state.Indices, _xfwQ3, state.Pulses, _delayedDecision, control.TiltQ14);
                EncProf.Mark(7, "silk NSQ");

                SilkEntropyEncoder.EncodeIndices(ref rangeEncoder, state, frameIndex, conditionalCoding);
                _lastEncodedIndices.CopyFrom(state.Indices);
                _encodedPerFrame[frameIndex].CopyFrom(state.Indices);
                SilkEntropyEncoder.EncodePulses(
                    ref rangeEncoder, state.Indices.SignalType, state.Indices.QuantOffsetType,
                    state.Pulses, frameLength);
                EncProf.Mark(8, "silk entropy");

                nBits = rangeEncoder.Tell();
                if (useCbr == 0 && iter == 0 && nBits <= maxBits)
                {
                    break;
                }
            }

            if (iter == MaximumIterations)
            {
                if (foundLower && (gainsId == gainsIdLower || nBits > maxBits))
                {
                    rangeEncoder = savedLower;
                    _rangeLowerBytes.AsSpan(0, _rangeLowerCount).CopyTo(_rangeBuffer);
                    _delayedDecision.CopyFrom(_delayedDecisionLower);
                    state.Shape.LastGainIndex = lastGainIndexLower;
                }

                break;
            }

            if (nBits > maxBits)
            {
                if (!foundLower && iter >= 2)
                {
                    control.LambdaQ10 = SilkEncoderMath.AddRshift(control.LambdaQ10, control.LambdaQ10, 1);
                    foundUpper = false;
                    gainsIdUpper = -1;
                }
                else
                {
                    foundUpper = true;
                    nBitsUpper = nBits;
                    gainMultiplierUpper = gainMultiplierQ8;
                    gainsIdUpper = gainsId;
                }
            }
            else if (nBits < maxBits - 5)
            {
                foundLower = true;
                nBitsLower = nBits;
                gainMultiplierLower = gainMultiplierQ8;
                if (gainsId != gainsIdLower)
                {
                    gainsIdLower = gainsId;
                    savedLower = rangeEncoder;
                    _rangeLowerCount = rangeEncoder.RangeBytes;
                    _rangeBuffer.AsSpan(0, _rangeLowerCount).CopyTo(_rangeLowerBytes);
                    _delayedDecisionLower.CopyFrom(_delayedDecision);
                    lastGainIndexLower = state.Shape.LastGainIndex;
                }
            }
            else
            {
                break;
            }

            if (!foundLower || !foundUpper)
            {
                int gainFactorQ16 = SilkEncoderMath.Log2Lin(
                    ((nBits - maxBits) << 7) / frameLength + (16 * 128));
                gainFactorQ16 = Math.Min(gainFactorQ16, 2 << 16);
                if (nBits > maxBits)
                {
                    gainFactorQ16 = Math.Max(gainFactorQ16, 85197);
                }

                gainMultiplierQ8 = SilkFixedPoint.Smulwb(gainFactorQ16, gainMultiplierQ8);
            }
            else
            {
                gainMultiplierQ8 = gainMultiplierLower
                    + (((gainMultiplierUpper - gainMultiplierLower) * (maxBits - nBitsLower))
                        / (nBitsUpper - nBitsLower));
                int lowerBound = SilkEncoderMath.AddRshift(
                    gainMultiplierLower, gainMultiplierUpper - gainMultiplierLower, 2);
                int upperBound = SilkEncoderMath.SubRshift(
                    gainMultiplierUpper, gainMultiplierUpper - gainMultiplierLower, 2);
                if (gainMultiplierQ8 > lowerBound)
                {
                    gainMultiplierQ8 = lowerBound;
                }
                else if (gainMultiplierQ8 < upperBound)
                {
                    gainMultiplierQ8 = upperBound;
                }
            }

            for (int i = 0; i < subframeCount; i++)
            {
                control.GainsQ16[i] = SilkFixedPoint.LshiftSat32(
                    SilkFixedPoint.Smulwb(control.GainsUnqQ16[i], gainMultiplierQ8), 8);
            }

            state.Shape.LastGainIndex = control.LastGainIndexPrev;
            SilkEncoderAnalysis.QuantizeGains(
                state.Indices.GainsIndices, control.GainsQ16, ref state.Shape.LastGainIndex,
                conditionalCoding == ConditionalCoding ? 1 : 0, subframeCount);
            gainsId = SilkEncoderAnalysis.GainsId(state.Indices.GainsIndices, subframeCount);
        }

        state.PrevLag = control.PitchL[subframeCount - 1];
        state.PrevSignalType = state.Indices.SignalType;
        state.FirstFrameAfterReset = 0;

        int ltpMemory = state.LtpMemLength + (5 * state.FsKhz);
        state.XBuf.AsSpan(frameLength, ltpMemory).CopyTo(state.XBuf);
    }
}
