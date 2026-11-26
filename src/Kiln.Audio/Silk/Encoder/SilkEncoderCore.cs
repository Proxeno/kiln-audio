// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
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
internal sealed class SilkEncoderCore
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
    private readonly short[] _residual = new short[(2 * SilkEncoderMath.MaxFrameLength) + SilkEncoderMath.LaPitchMax];

    private readonly int[] _speechActivity = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _inputTilt = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[] _vadSignalType = new int[SilkEncoderState.MaxFramesPerPacket];
    private readonly int[][] _bandQuality =
    [
        new int[SilkEncoderMath.VadBandCount],
        new int[SilkEncoderMath.VadBandCount],
        new int[SilkEncoderMath.VadBandCount],
    ];

    private readonly SilkNsqState _nsqStart = new();
    private readonly SilkNsqState _nsqLower = new();
    private readonly SilkNsqState _nsqLbrr = new();
    private readonly SilkNsqDelayedDecision _nsqDelayed = new();
    private readonly SilkNsqDelayedDecision _nsqDelayedStart = new();
    private readonly SilkNsqDelayedDecision _nsqDelayedLower = new();
    private readonly SilkNsqDelayedDecision _nsqDelayedLbrr = new();
    private readonly sbyte[] _delayedPulses = new sbyte[SilkEncoderMath.MaxFrameLength];
    private readonly short[] _delayedOutput = new short[SilkEncoderMath.MaxFrameLength];
    private readonly int[] _quantizerGains = new int[4];
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
    private const int WarpingMultiplierQ16 = 983; // SILK_FIX_CONST(0.015, 16)
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
        LoadPacket(pcm);

        for (int f = 0; f < _silkFrames; f++)
        {
            SetInputFromResampled(f);
            AnalyzeInput(f);
        }

        var rangeEncoder = new RangeEncoder(_rangeBuffer);
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
        int maxDataBytes = Math.Min(packet.Length + 1, 1276);
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
        for (int f = 0; f < _silkFrames; f++)
        {
            PrepareFrame(f);
            int framesLeft = _silkFrames - f;
            int frameBits = (packetBits - (rangeEncoder.Tell() >> 1)) / framesLeft;
            int targetRate = frameBits * (_durationMs == 10 ? 100 : 50);
            targetRate -= _bitsExceeded * 1000 / BitReservoirDecayTimeMs;
            targetRate = Math.Max(Math.Min(targetRate, silkBitrate), 5000);
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
        _bitsExceeded = Math.Clamp(_bitsExceeded + (written * 8) - packetBits, 0, 10000);
        return written;
    }

    // ---------------------------------------------------------------------------------------
    // Frame-level hooks used by the multi-channel packet encoder. They expose the same steps
    // <see cref="Encode"/> runs, so one packet can interleave mid and side frames.
    // ---------------------------------------------------------------------------------------

    /// <summary>Number of SILK frames in one packet.</summary>
    internal int FrameCount => _silkFrames;

    /// <summary>Samples in one SILK frame at the internal rate.</summary>
    internal int FrameLength => _state.FrameLength;

    /// <summary>The SILK encoder state (analysis output and quantizer state).</summary>
    internal SilkEncoderState State => _state;

    /// <summary>The per-frame analysis output.</summary>
    internal SilkEncoderControl Control => _control;

    /// <summary>Prefilters, resamples the high-passed input and stores the internal-rate frame.</summary>
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

    /// <summary>
    /// Runs the VAD on the current frame input and records the activity the packet header needs
    /// (RFC 6716 Appendix A, <c>silk_VAD_GetSA_Q8</c> and <c>silk_encode_do_VAD_FIX</c>).
    /// </summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal void AnalyzeInput(int frameIndex)
    {
        int frameLength = _state.FrameLength;
        SilkVad.GetSpeechActivity(_state, _state.InputBuf.AsSpan(1, frameLength));
        _speechActivity[frameIndex] = _state.SpeechActivityQ8;
        _inputTilt[frameIndex] = _state.InputTiltQ15;
        _state.InputQualityBandsQ15.AsSpan().CopyTo(_bandQuality[frameIndex]);

        // RFC 6716 Appendix A, silk_encode_do_VAD_FIX: inactivity is signal type 0, activity
        // starts as unvoiced and the pitch search may upgrade it to voiced.
        if (_state.SpeechActivityQ8 < 13)
        {
            _vadSignalType[frameIndex] = 0;
            _state.NoSpeechCounter++;
        }
        else
        {
            _state.NoSpeechCounter = 0;
            _vadSignalType[frameIndex] = 1;
            _state.InDtx = 0;
        }
    }

    /// <summary>Gets one frame's speech activity, Q8.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal int SpeechActivityQ8(int frameIndex) => _speechActivity[frameIndex];

    /// <summary>Gets one frame's VAD signal type: 0 inactive, 1 active.</summary>
    /// <param name="frameIndex">The SILK frame index.</param>
    internal int VadSignalType(int frameIndex) => _vadSignalType[frameIndex];

    /// <summary>Resets the side channel's encoder memory after a mid-only interval
    /// (RFC 6716 Appendix A, <c>silk_Encode</c>).</summary>
    internal void ResetAfterMidOnly()
    {
        _state.Shape.Reset();
        _state.Prefilt.Reset();
        _state.Nsq.Reset();
        _nsqDelayed.Reset();
        _state.PrevNlsfQ15.AsSpan().Clear();
        _state.Lp.InLpState.AsSpan().Clear();
        _state.PrevLag = 100;
        _state.Nsq.LagPrev = 100;
        _state.Shape.LastGainIndex = 10;
        _state.PrevSignalType = 0;
        _state.Nsq.PrevGainQ16 = 65536;
        _state.FirstFrameAfterReset = 1;
    }

    /// <summary>Prepares one frame's state before quantization (high-pass adaptation and VAD fields).</summary>
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

    /// <summary>Gets the range-coder final value for the most recently encoded packet.</summary>
    internal uint FinalRange { get; private set; }

    /// <summary>Exposes the encoder state for internal diagnostics and tests.</summary>
    internal SilkEncoderState CurrentState => _state;

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
        _state.TargetRateBps = 0;
        _state.MuLtpQ9 = fsKhz switch
        {
            16 => 10,
            12 => 13,
            _ => 15,
        };

        // The warped noise-shaping filter is only valid in the delayed-decision quantizer
        // (silk_NSQ_del_dec); the single-state quantizer used here assumes unwarped shaping
        // coefficients, so warping stays off until the delayed-decision quantizer is wired in.
        int complexity = _complexity;
        if (complexity < 2)
        {
            _state.PitchEstimationComplexity = SilkEncoderTables.PitchEstimationMinComplexity;
            _state.PitchEstimationThresholdQ16 = 52429;
            _state.PitchEstimationLpcOrder = 6;
            _state.ShapingLpcOrder = 8;
            _state.LaShape = 3 * fsKhz;
            _state.UseInterpolatedNlsfs = 0;
            _state.LtpQuantLowComplexity = 1;
            _state.NlsfMsvqSurvivors = 2;
            _state.WarpingQ16 = 0;
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
            _state.WarpingQ16 = 0;
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
            _state.WarpingQ16 = 0;
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
            _state.WarpingQ16 = 0;
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
            _state.WarpingQ16 = 0;
        }

        // The delayed-decision trellis widens with complexity, and its warped shaping filters
        // become valid from two states up (RFC 6716 Appendix A, silk_setup_complexity). One state
        // keeps the unwarped greedy quantizer, which is what it assumes.
        _state.NStatesDelayedDecision = SilkNsqDelayedDecision.StatesForComplexity(complexity);
        _state.WarpingQ16 = _state.NStatesDelayedDecision > 1 ? fsKhz * WarpingMultiplierQ16 : 0;

        _state.PitchEstimationLpcOrder = Math.Min(_state.PitchEstimationLpcOrder, _state.PredictLpcOrder);
        _state.ShapeWinLength = (5 * fsKhz) + (2 * _state.LaShape);

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
    }

    /// <summary>
    /// Runs the frame quantizer, choosing the delayed-decision trellis when the complexity asks
    /// for more than one state or the shaping filters are warped (RFC 6716 Appendix A,
    /// <c>silk_encode_frame_FIX</c>).
    /// </summary>
    private void Quantize(
        SilkEncoderState state,
        SilkEncoderControl control,
        SilkSideInfo indices,
        ReadOnlySpan<int> inputQ3,
        Span<short> pulses,
        SilkNsqState nsq,
        SilkNsqDelayedDecision delayed)
    {
        if (state.NStatesDelayedDecision <= 1 && state.WarpingQ16 == 0)
        {
            SilkNsq.Process(
                state, nsq, indices, inputQ3, pulses,
                control.PredCoefQ12[0], control.PredCoefQ12[1], control.LtpCoefQ14, control.Ar2Q13,
                control.HarmShapeGainQ14, control.TiltQ14, control.LfShpQ14,
                control.GainsQ16, control.PitchL, control.LambdaQ10, control.LtpScaleQ14);
            return;
        }

        Span<int> gains = _quantizerGains;
        control.GainsQ16.AsSpan(0, state.SubframeCount).CopyTo(gains);
        Span<short> prediction = stackalloc short[2 * SilkEncoderMath.MaxLpcOrder];
        control.PredCoefQ12[0].AsSpan().CopyTo(prediction);
        control.PredCoefQ12[1].AsSpan().CopyTo(prediction[SilkEncoderMath.MaxLpcOrder..]);

        var parameters = new SilkNsqParameters(
            inputQ3[..state.FrameLength],
            prediction[..(2 * SilkEncoderMath.MaxLpcOrder)],
            control.LtpCoefQ14,
            control.Ar2Q13,
            control.HarmShapeGainQ14,
            control.TiltQ14,
            control.LfShpQ14,
            gains,
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

        delayed.Run(parameters, state.NStatesDelayedDecision, _delayedPulses, _delayedOutput, out int seed);
        indices.Seed = seed;
        for (int i = 0; i < state.FrameLength; i++)
        {
            pulses[i] = _delayedPulses[i];
        }
    }

    /// <summary>
    /// Encodes this frame's low-bitrate redundancy at a raised gain
    /// (RFC 6716 Appendix A, <c>silk_LBRR_encode_FIX</c>).
    /// </summary>
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
            lbrr.GainsIndices[0] = Math.Min(
                lbrr.GainsIndices[0] + state.LbrrGainIncreases, 63);
        }

        SilkEncoderAnalysis.DequantizeGains(
            control.GainsQ16, lbrr.GainsIndices, ref state.LbrrPrevLastGainIndex,
            conditionalCoding == ConditionalCoding ? 1 : 0, state.SubframeCount);

        _nsqLbrr.CopyFrom(state.Nsq);
        _nsqDelayedLbrr.CopyFrom(_nsqDelayed);
        Quantize(state, control, lbrr, _xfwQ3, state.LbrrPulses[frameIndex], _nsqLbrr, _nsqDelayedLbrr);

        savedGains.CopyTo(control.GainsQ16);
    }

    private void EncodeFrame(ref RangeEncoder rangeEncoder, int frameIndex, int conditionalCoding, int maxBits, int useCbr)
    {
        SilkEncoderState state = _state;
        SilkEncoderControl control = _control;
        int frameLength = state.FrameLength;
        int subframeLength = state.SubframeLength;
        int subframeCount = state.SubframeCount;

        state.Indices.Seed = state.FrameCounter++ & 3;

        // Copy the new frame next to the shape look-ahead, then run the analysis.
        int frameOffset = state.LtpMemLength;
        state.InputBuf.AsSpan(1, frameLength)
            .CopyTo(state.XBuf.AsSpan(frameOffset + (5 * state.FsKhz)));

        _pitchAnalysis.FindPitchLags(state, control, _residual, state.XBuf, frameOffset);
        SilkEncoderAnalysis.NoiseShapeAnalysis(
            state, control, _residual.AsSpan(state.LtpMemLength), state.XBuf, frameOffset);
        SilkEncoderAnalysis.FindPredictionCoefficients(
            state, control, _residual, state.XBuf, frameOffset, conditionalCoding);
        SilkEncoderAnalysis.ProcessGains(state, control, conditionalCoding);
        SilkEncoderAnalysis.Prefilter(state, control, _xfwQ3, state.XBuf, frameOffset);

        // Low-bitrate redundancy reuses this frame's parameters at a higher gain
        // (RFC 6716 Appendix A, silk_LBRR_encode_FIX).
        if (state.LbrrEnabled != 0 && state.SpeechActivityQ8 > LbrrSpeechActivityThresholdQ8)
        {
            EncodeLbrrFrame(frameIndex, conditionalCoding);
        }

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
        _nsqStart.CopyFrom(state.Nsq);
        _nsqDelayedStart.CopyFrom(_nsqDelayed);
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
                    state.Nsq.CopyFrom(_nsqStart);
                    _nsqDelayed.CopyFrom(_nsqDelayedStart);
                    state.Indices.Seed = seedStart;
                    state.EcPrevLagIndex = ecPrevLagStart;
                    state.EcPrevSignalType = ecPrevSignalStart;
                }

                Quantize(state, control, state.Indices, _xfwQ3, state.Pulses, state.Nsq, _nsqDelayed);

                SilkEntropyEncoder.EncodeIndices(ref rangeEncoder, state, frameIndex, conditionalCoding);
                _lastEncodedIndices.CopyFrom(state.Indices);
                _encodedPerFrame[frameIndex].CopyFrom(state.Indices);
                SilkEntropyEncoder.EncodePulses(
                    ref rangeEncoder, state.Indices.SignalType, state.Indices.QuantOffsetType,
                    state.Pulses, frameLength);

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
                    state.Nsq.CopyFrom(_nsqLower);
                    _nsqDelayed.CopyFrom(_nsqDelayedLower);
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
                    _nsqLower.CopyFrom(state.Nsq);
                    _nsqDelayedLower.CopyFrom(_nsqDelayed);
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
