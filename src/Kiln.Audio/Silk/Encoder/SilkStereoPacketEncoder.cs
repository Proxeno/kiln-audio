// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The SILK packet encoder for mono and stereo: header bits, in-band FEC (LBRR), DTX and the
/// interleaved mid/side frame layout (RFC 6716 §5.2; Appendix A, <c>silk/enc_API.c</c> and
/// <c>silk/fixed/encode_frame_FIX.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="SilkEncoderCore"/> runs per channel. This layer owns what is shared or
/// cross-channel: the stereo mixer and its prediction indices, the VAD bits and LBRR flags that
/// open the payload, the LBRR frames carried from the previous packet, the mid/side rate split and
/// the DTX suppression of an all-silent packet.
/// </para>
/// <para>
/// Every buffer is allocated in the constructor; <see cref="Encode"/> makes no allocations.
/// </para>
/// </remarks>
internal sealed class SilkStereoPacketEncoder
{
    private const int MaxPacketBytes = 1276;
    private const int IndependentCoding = 0;
    private const int ConditionalCoding = 2;
    private const int BitReservoirDecayTimeMs = 500;
    private const int MaxFramesPerPacket = 3;

    private readonly SilkEncoderCore _mid;
    private readonly SilkEncoderCore? _side;
    private readonly SilkStereoEncoder? _stereo;
    private readonly SilkDtxEncoder _midDtx = new();
    private readonly SilkDtxEncoder _sideDtx = new();
    private readonly LbrrWriter _writer;
    private readonly byte[] _rangeBuffer = new byte[MaxPacketBytes];
    private readonly short[] _left;
    private readonly short[] _right;
    private readonly short[] _midFrameBuffer;
    private readonly short[] _sideFrameBuffer;
    private readonly byte[] _predictionIndices = new byte[MaxFramesPerPacket * 6];
    private readonly bool[] _midOnlyFlags = new bool[MaxFramesPerPacket];
    private readonly byte[] _previousPredictionIndices = new byte[MaxFramesPerPacket * 6];
    private readonly bool[] _previousMidOnlyFlags = new bool[MaxFramesPerPacket];
    private readonly int[] _midRates = new int[MaxFramesPerPacket];
    private readonly int[] _sideRates = new int[MaxFramesPerPacket];
    private int _midVadMask;
    private int _sideVadMask;

    private readonly int _channels;
    private readonly int _durationMs;
    private readonly int _silkFrames;
    private readonly int _apiRate;
    private readonly int _bitrateBps;
    private readonly bool _useCbr;
    private readonly bool _useLbrr;
    private readonly bool _useDtx;
    private int _bitsExceeded;
    private int _previousSpeechActivityQ8 = 256;

    /// <summary>Creates the packet encoder for one fixed mode.</summary>
    /// <param name="apiRate">The API sample rate in Hz.</param>
    /// <param name="internalRate">The SILK internal rate in Hz (8000, 12000 or 16000).</param>
    /// <param name="durationMs">The Opus frame duration: 10, 20, 40 or 60 ms.</param>
    /// <param name="bitrateBps">The total target bitrate.</param>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    /// <param name="useCbr">Whether the encoder targets a constant packet size.</param>
    /// <param name="channels">The SILK channel count, 1 or 2.</param>
    /// <param name="useInbandFec">Whether to emit LBRR frames.</param>
    /// <param name="packetLossPercent">The expected packet loss, 0 to 100.</param>
    /// <param name="useDtx">Whether to suppress all-silent packets.</param>
    /// <param name="mode">The performance mode.</param>
    internal SilkStereoPacketEncoder(
        int apiRate,
        int internalRate,
        int durationMs,
        int bitrateBps,
        int complexity,
        bool useCbr,
        int channels,
        bool useInbandFec,
        int packetLossPercent,
        bool useDtx,
        OpusPerformanceMode mode = OpusPerformanceMode.Precise)
    {
        _channels = channels;
        _durationMs = durationMs;
        _silkFrames = durationMs == 10 ? 1 : durationMs / 20;
        _apiRate = apiRate;
        _bitrateBps = bitrateBps;
        _useCbr = useCbr;
        _useDtx = useDtx;

        _mid = new SilkEncoderCore(apiRate, internalRate, durationMs, bitrateBps, complexity, useCbr, mode);
        _side = channels == 2
            ? new SilkEncoderCore(apiRate, internalRate, durationMs, bitrateBps, complexity, useCbr)
            : null;
        _stereo = channels == 2 ? new SilkStereoEncoder() : null;
        _writer = new LbrrWriter(_mid, _side);

        int frameSamples = apiRate * durationMs / 1000;
        _left = new short[frameSamples];
        int frameSamplesInternal = internalRate * 20 / 1000;
        _midFrameBuffer = new short[frameSamplesInternal * MaxFramesPerPacket];
        _sideFrameBuffer = new short[frameSamplesInternal * MaxFramesPerPacket];
        _right = channels == 2 ? new short[frameSamples] : [];

        _midDtx.UseDtx = useDtx;
        _sideDtx.UseDtx = useDtx;

        // RFC 6716 Appendix A, silk_setup_LBRR: the threshold uses the per-channel rate.
        int channelRate = channels == 2 ? bitrateBps >> 1 : bitrateBps;
        SilkLbrrEncoder.Setup(
            useInbandFec, packetLossPercent, internalRate / 1000, channelRate,
            out _useLbrr, out int gainIncreases);
        for (int f = 0; f < MaxFramesPerPacket; f++)
        {
            _mid.State.LbrrGainIncreases = gainIncreases;
            if (_side is not null)
            {
                _side.State.LbrrGainIncreases = gainIncreases;
            }
        }

        _mid.State.LbrrEnabled = _useLbrr ? 1 : 0;
        if (_side is not null)
        {
            _side.State.LbrrEnabled = _useLbrr ? 1 : 0;
        }
    }

    /// <summary>Gets the range-coder final value of the most recently encoded payload.</summary>
    internal uint FinalRange { get; private set; }

    /// <summary>Gets the number of SILK frames in one packet.</summary>
    internal int FrameCount => _silkFrames;

    /// <summary>Gets whether the last packet was suppressed by DTX.</summary>
    internal bool LastPacketSuppressed { get; private set; }

    /// <summary>Encodes one Opus frame's SILK payload (without the TOC byte).</summary>
    /// <param name="pcm">Interleaved input at the API rate for exactly one Opus frame.</param>
    /// <param name="packet">Destination for the payload.</param>
    /// <returns>The number of payload bytes, or zero when DTX suppresses the packet.</returns>
    internal int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        var rangeEncoder = new RangeEncoder(_rangeBuffer);
        int payloadBits = EncodeInto(ref rangeEncoder, pcm, Math.Min(packet.Length + 1, 1276));
        if (LastPacketSuppressed)
        {
            // A DTX-suppressed packet carries no range-coded payload, so the decoder reports a
            // final range of zero; do not leave the previous frame's value in place.
            FinalRange = 0;
            return 0;
        }

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

    /// <summary>Encodes one Opus frame's SILK symbols into a caller-owned, unfinalized coder.</summary>
    /// <param name="rangeEncoder">The shared range encoder (SILK-only or hybrid frame).</param>
    /// <param name="pcm">Interleaved input at the API rate for exactly one Opus frame.</param>
    /// <param name="maxDataBytes">The frame's byte capacity, including the TOC byte.</param>
    /// <returns>The number of payload bits written; the final range is not updated.</returns>
    internal int EncodeInto(ref RangeEncoder rangeEncoder, ReadOnlySpan<short> pcm, int maxDataBytes)
    {
        LastPacketSuppressed = false;
        if (_channels == 2)
        {
            Deinterleave(pcm);
        }
        else
        {
            pcm.CopyTo(_left);
        }

        _mid.LoadPacket(_left);
        _side?.LoadPacket(_right);

        // The LBRR frames written in this packet describe the previous packet's intervals, so
        // they must use that packet's stereo prediction indices and mid-only flags.
        _predictionIndices.AsSpan().CopyTo(_previousPredictionIndices);
        _midOnlyFlags.AsSpan().CopyTo(_previousMidOnlyFlags);

        _midDtx.BeginPacket();
        _sideDtx.BeginPacket();
        if (_channels == 2)
        {
            PrepareStereoFrames();
        }
        else
        {
            PrepareMonoFrames();
        }

        SilkLbrrEncoder.EncodeHeader(
            ref rangeEncoder,
            _channels,
            _silkFrames,
            _midVadMask,
            _channels == 2 ? _sideVadMask : 0,
            LbrrMask(_mid),
            _channels == 2 ? LbrrMask(_side!) : 0);

        // The header's LBRR flags describe the previous packet's frames; write those payloads now.
        WritePendingLbrr(ref rangeEncoder);
        _mid.ClearLbrrFlags();
        _side?.ClearLbrrFlags();

        int channelRate = _channels == 2 ? _bitrateBps >> 1 : _bitrateBps;
        int bitrate = _channels == 2 ? channelRate << 1 : channelRate;
        int frameRate = 48000 / (48 * _durationMs);
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
        // The mono path is the one SILK-QUAL-3 measured: at high rate and 60 ms the target rate is
        // distributed across the frame by voice activity, exactly as the single-stream
        // EncodeInto path does. Stereo keeps the packet-level mid/side allocation.
        bool weightedAllocation = _channels == 1 && _mid.UsesWeightedFrameAllocation;
        if (weightedAllocation)
        {
            _mid.PrepareWeightedFrameAllocation(silkBitrate);
        }

        for (int f = 0; f < _silkFrames; f++)
        {
            int targetRate;
            if (weightedAllocation)
            {
                targetRate = _mid.WeightedFrameTargetRate(f) - (_bitsExceeded * 1000 / BitReservoirDecayTimeMs);
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
            // RFC 6716 §4.2.7.2: an active side channel codes the side frame and omits the
            // mid-only flag; otherwise the signalled VAD bit opens the mid-only flag.
            bool sideVad = ((_sideVadMask >> f) & 1) != 0;
            bool sideCoded = _channels == 2 && (sideVad || !_midOnlyFlags[f]);
            int midRate = _channels == 2 ? _midRates[f] : targetRate;
            int sideRate = _channels == 2 ? _sideRates[f] : 0;

            if (_channels == 2)
            {
                SilkStereoEncoder.EncodePrediction(ref rangeEncoder, _predictionIndices.AsSpan(f * 6, 6));
                if (!sideVad)
                {
                    SilkStereoEncoder.EncodeMidOnly(ref rangeEncoder, _midOnlyFlags[f]);
                }
            }

            if (midRate <= 0)
            {
                midRate = targetRate;
            }

            if (_channels == 2 && sideRate > 0)
            {
                maxBits -= packetMaxBits / (_silkFrames * 2);
            }

            if (_channels == 2)
            {
                _mid.SetInputFrame(_midFrameBuffer.AsSpan(f * _mid.FrameLength, _mid.FrameLength));
            }

            _mid.PrepareFrame(f, refreshInput: _channels == 1);
            _mid.SetTargetRate(Math.Max(midRate, 5000));
            _mid.EncodeFrameInto(ref rangeEncoder, f, f == 0 ? IndependentCoding : ConditionalCoding, maxBits, useCbr);

            if (sideCoded && _side is not null)
            {
                _side.SetInputFrame(_sideFrameBuffer.AsSpan(f * _side.FrameLength, _side.FrameLength));
                _side.PrepareFrame(f, refreshInput: false);
                _side.SetTargetRate(Math.Max(sideRate, 5000));
                _side.EncodeFrameInto(ref rangeEncoder, f, f == 0 ? IndependentCoding : ConditionalCoding, maxBits, useCbr);
            }

            _mid.FinishFrame(f);
            _side?.FinishFrame(f);
        }

        _previousSpeechActivityQ8 = _mid.State.SpeechActivityQ8;

        int payloadBits = rangeEncoder.Tell();
        _bitsExceeded = Math.Clamp(_bitsExceeded + (((payloadBits + 7) >> 3) * 8) - packetBits, 0, 10000);

        // RFC 6716 Appendix A, silk_Encode: when every channel is in DTX the payload is dropped
        // and the packet is the TOC byte alone.
        if (_useDtx && SilkDtxEncoder.SuppressPacket(_midDtx, _channels == 2 ? _sideDtx : null))
        {
            LastPacketSuppressed = true;
            return 0;
        }

        return payloadBits;
    }

    private void PrepareMonoFrames()
    {
        _midVadMask = 0;
        _sideVadMask = 0;
        for (int f = 0; f < _silkFrames; f++)
        {
            _mid.SetInputFromResampled(f);
            _mid.AnalyzeInput(f);
            SilkDtxDecision decision = _midDtx.ProcessFrame(_mid.SpeechActivityQ8(f));
            _mid.State.Indices.SignalType = (int)decision.SignalType;
            _mid.State.InDtx = decision.InDtx ? 1 : 0;
            if (decision.VadFlag)
            {
                _midVadMask |= 1 << f;
            }
        }
    }

    private void PrepareStereoFrames()
    {
        int fsKHz = _mid.State.FsKhz;
        int frameLength = _mid.FrameLength;
        _midVadMask = 0;
        _sideVadMask = 0;
        for (int f = 0; f < _silkFrames; f++)
        {
            ReadOnlySpan<short> left = _mid.ResampledFrame(f);
            ReadOnlySpan<short> right = _side!.ResampledFrame(f);
            Span<short> midOut = _midFrameBuffer.AsSpan(f * frameLength, frameLength);
            Span<short> sideOut = _sideFrameBuffer.AsSpan(f * frameLength, frameLength);
            _stereo!.Analyze(
                left,
                right,
                fsKHz,
                _bitrateBps,
                _previousSpeechActivityQ8,
                toMono: false,
                midOut,
                sideOut,
                _predictionIndices.AsSpan(f * 6, 6),
                out bool midOnly,
                out int midRate,
                out int sideRate,
                frameLength);

            _midOnlyFlags[f] = midOnly;
            _midRates[f] = Math.Max(midRate, 1);
            _sideRates[f] = Math.Max(sideRate, 0);

            _mid.SetInputFrame(midOut);
            _mid.AnalyzeInput(f);
            SilkDtxDecision midDecision = _midDtx.ProcessFrame(_mid.State.SpeechActivityQ8);
            _mid.State.InDtx = midDecision.InDtx ? 1 : 0;
            if (midDecision.VadFlag)
            {
                _midVadMask |= 1 << f;
            }

            if (midOnly)
            {
                // RFC 6716 Appendix A: a mid-only interval codes no side frame, and its side VAD
                // flag (which opens the mid-only flag in the stream) must be zero.
                _side.State.VadFlags[f] = 0;
            }
            else
            {
                _side.SetInputFrame(sideOut);
                _side.AnalyzeInput(f);
                SilkDtxDecision sideDecision = _sideDtx.ProcessFrame(_side.State.SpeechActivityQ8);
                _side.State.InDtx = sideDecision.InDtx ? 1 : 0;
                if (sideDecision.VadFlag)
                {
                    _sideVadMask |= 1 << f;
                }
            }
        }
    }

    private void WritePendingLbrr(ref RangeEncoder rangeEncoder)
    {
        int midMask = LbrrMask(_mid);
        int sideMask = _side is null ? 0 : LbrrMask(_side);
        if (midMask == 0 && sideMask == 0)
        {
            return;
        }

        SilkLbrrEncoder.EncodeFrames(
            ref rangeEncoder,
            _channels,
            _silkFrames,
            midMask,
            sideMask,
            _previousPredictionIndices,
            _previousMidOnlyFlags,
            _writer);
    }

    private int LbrrMask(SilkEncoderCore core)
    {
        int mask = 0;
        for (int f = 0; f < _silkFrames; f++)
        {
            mask |= core.State.LbrrFlags[f] << f;
        }

        return mask;
    }

    private void Deinterleave(ReadOnlySpan<short> pcm)
    {
        for (int i = 0; i < _left.Length; i++)
        {
            _left[i] = pcm[2 * i];
            _right[i] = pcm[(2 * i) + 1];
        }
    }

    /// <summary>Writes stored LBRR frames from either channel (RFC 6716 §4.2.5).</summary>
    private sealed class LbrrWriter : ISilkLbrrFrameWriter
    {
        private readonly SilkEncoderCore _mid;
        private readonly SilkEncoderCore? _side;

        internal LbrrWriter(SilkEncoderCore mid, SilkEncoderCore? side)
        {
            _mid = mid;
            _side = side;
        }

        /// <inheritdoc />
        public void WriteLbrrFrame(ref RangeEncoder enc, int frameIndex, int channel, bool conditionalCoding)
        {
            SilkEncoderCore core = channel == 0 ? _mid : _side!;
            core.WriteLbrrFrame(
                ref enc, frameIndex, conditionalCoding ? ConditionalCoding : IndependentCoding);
        }
    }
}
