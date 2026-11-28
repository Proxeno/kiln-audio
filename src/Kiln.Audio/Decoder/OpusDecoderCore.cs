// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.IO;
using Kiln.Audio.Entropy;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Decoder;

/// <summary>
/// The RFC 6716 packet and frame decoder behind <see cref="OpusDecoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a managed transcription of the reference decoder's <c>opus_decode_native()</c> and
/// <c>opus_decode_frame()</c> (RFC 6716 Appendix A, <c>src/opus_decoder.c</c>) built on the
/// repository's SILK and CELT layers. It performs mode dispatch (SILK-only, hybrid, CELT-only),
/// the hybrid band split (§4.3), transition redundancy and cross-fades (§4.5.1), the reset rules
/// of §4.5.2 with the RFC 8251 §3 stereo-state fix, and the final-range bookkeeping of RFC 8251.
/// </para>
/// <para>
/// Every buffer is allocated in the constructor. After construction, <see cref="DecodePacket"/>
/// and <see cref="DecodeLoss"/> allocate nothing.
/// </para>
/// </remarks>
internal sealed class OpusDecoderCore
{
    private const int MaxFrames = OpusPacket.MaxFrames;
    private const int FramesPerSecond = 400;
    private const int FiftiethOfSecond = 50;
    private const int TwoAndAHalfMilliseconds = 400;

    private readonly int _apiRate;
    private readonly int _channels;
    private readonly int _f2_5;
    private readonly int _f5;
    private readonly int _f20;
    private readonly int _maxFrameSamples;
    private readonly SilkOutput _silk;
    private readonly CeltOutput _celt;
    private readonly float[] _pcmSilk;
    private readonly float[] _pcmTransition;
    private readonly float[] _redundantAudio;

    private OpusMode? _prevMode;
    private OpusMode _mode;
    private OpusBandwidth _bandwidth;
    private OpusFrameDuration _frameDuration;
    private int _frameDurationMs;
    private int _frameSize;
    private int _streamChannels;
    private bool _prevRedundancy;
    private int _lastLm;
    private uint _finalRange;

    /// <summary>Initializes the decoder for one API rate and channel count.</summary>
    /// <param name="apiRate">API sample rate: 8000, 12000, 16000, 24000 or 48000 Hz.</param>
    /// <param name="apiChannels">API channel count: 1 or 2.</param>
    /// <param name="mode">The decoder performance mode.</param>
    internal OpusDecoderCore(int apiRate, int apiChannels, OpusPerformanceMode mode = OpusPerformanceMode.Precise)
    {
        _apiRate = apiRate;
        _channels = apiChannels;
        _f2_5 = apiRate / TwoAndAHalfMilliseconds;
        _f5 = apiRate / 200;
        _f20 = apiRate / FiftiethOfSecond;
        _maxFrameSamples = apiRate * 60 / 1000;
        _frameSize = apiRate / FramesPerSecond;
        _mode = OpusMode.Silk;
        _bandwidth = OpusBandwidth.Narrowband;

        _silk = new SilkOutput(apiRate, apiChannels);
        _celt = new CeltOutput(apiRate, apiChannels, mode);
        _pcmSilk = new float[_maxFrameSamples * apiChannels];
        _pcmTransition = new float[_f5 * apiChannels];
        _redundantAudio = new float[_f5 * apiChannels];
    }

    /// <summary>Gets the final range of the last decoded frame (RFC 8251).</summary>
    internal uint FinalRange => _finalRange;

    /// <summary>
    /// Gets the outcome of the last in-band FEC decode, as exposed by
    /// <see cref="OpusDecoder.LastFecOutcome"/>.
    /// </summary>
    internal OpusFecOutcome LastFecOutcome { get; private set; }

    /// <summary>
    /// Returns the number of samples per channel a valid packet produces at <paramref name="apiRate"/>.
    /// </summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <param name="apiRate">API sample rate in Hz.</param>
    /// <returns>Samples per channel for the whole packet.</returns>
    /// <exception cref="InvalidDataException">The packet violates RFC 6716 §3 framing.</exception>
    internal static int RequiredSamplesPerChannel(ReadOnlySpan<byte> packet, int apiRate)
    {
        int frameCount = OpusPacket.GetFrameCount(packet);
        if (frameCount == 0)
        {
            throw new InvalidDataException("The Opus packet is malformed (RFC 6716 §3.2–§3.4).");
        }

        OpusToc toc = OpusToc.Parse(packet[0]);
        return frameCount * OpusPacket.GetSamplesPerFrame(toc, apiRate);
    }

    /// <summary>
    /// Decodes one Opus packet to normalized interleaved PCM at the API rate.
    /// </summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    /// <param name="decodeFec">
    /// Decode this packet's in-band FEC (LBRR) data to recover the frame that preceded it, with
    /// packet loss concealment where an interval carries no LBRR.
    /// </param>
    /// <returns>The number of samples per channel written.</returns>
    /// <exception cref="InvalidDataException">The packet violates RFC 6716 §3 framing.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short.</exception>
    internal int DecodePacket(ReadOnlySpan<byte> packet, Span<float> pcm, bool decodeFec)
    {
        if (packet.IsEmpty)
        {
            return DecodeLoss(pcm);
        }

        Span<Range> frames = stackalloc Range[MaxFrames];
        OpusStatus status = OpusPacket.TryParse(
            packet, frames, out int frameCount, out OpusToc toc, out _);
        if (status == OpusStatus.InvalidPacket)
        {
            throw new InvalidDataException("The Opus packet is malformed (RFC 6716 §3.2–§3.4).");
        }

        // RFC 6716 §4.2.4–§4.2.5: an FEC decode recovers the previous interval only when the
        // packet's first SILK frame carries LBRR data; otherwise it conceals that interval. A
        // packet with several frames reports its first frame, matching OpusPacket.HasLbrr.
        LastFecOutcome = !decodeFec
            ? OpusFecOutcome.NotRequested
            : OpusPacket.HasLbrr(packet)
                ? OpusFecOutcome.Recovered
                : OpusFecOutcome.Concealed;

        _mode = toc.Mode;
        _bandwidth = toc.Bandwidth;
        _frameDuration = toc.FrameDuration;
        _frameDurationMs = toc.Mode == OpusMode.Celt ? 0 : DurationMilliseconds(toc.FrameDuration);
        _frameSize = OpusPacket.GetSamplesPerFrame(toc, _apiRate);
        _streamChannels = toc.IsStereo ? 2 : 1;
        _lastLm = LinearToLog(toc.FrameDuration);

        int required = frameCount * _frameSize;
        if (pcm.Length < required * _channels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {required * _channels} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        int offset = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            int written = DecodeFrame(
                packet[frames[frame]], _frameSize, pcm.Slice(offset, _frameSize * _channels), decodeFec);
            offset += written * _channels;
        }

        return offset / _channels;
    }

    /// <summary>
    /// Produces concealment for one lost frame at the API rate.
    /// </summary>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    /// <returns>The number of samples per channel written.</returns>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short.</exception>
    internal int DecodeLoss(Span<float> pcm)
    {
        LastFecOutcome = OpusFecOutcome.NotRequested;

        int samples = _frameSize;
        if (pcm.Length < samples * _channels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {samples * _channels} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        if (_prevMode is null)
        {
            pcm[..(samples * _channels)].Clear();
        }
        else
        {
            DecodeConcealment(_prevMode.Value, samples, pcm);
        }

        _finalRange = 0;
        return samples;
    }

    /// <summary>Decodes one Opus frame into <paramref name="pcm"/> at the API rate.</summary>
    /// <param name="data">The frame's bytes.</param>
    /// <param name="capacity">Samples per channel available in <paramref name="pcm"/>.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    /// <param name="decodeFec">
    /// Whether to decode the in-band FEC (LBRR) data carried by this frame rather than the frame
    /// itself.
    /// </param>
    /// <returns>The number of samples per channel written.</returns>
    private int DecodeFrame(ReadOnlySpan<byte> data, int capacity, Span<float> pcm, bool decodeFec)
    {
        if (capacity < _f2_5)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The PCM destination must hold at least {_f2_5} samples per channel."),
                nameof(pcm));
        }

        // RFC 6716 Appendix A: a payload of 0 or 1 byte is DTX or packet loss and is concealed
        // rather than decoded (opus_decode_frame() takes the data == NULL path).
        bool packetLoss = data.Length <= 1;
        int audioSize;
        OpusMode mode;
        RangeDecoder dec = default;
        if (packetLoss)
        {
            audioSize = Math.Min(capacity, _frameSize);
            if (_prevMode is null)
            {
                pcm[..(audioSize * _channels)].Clear();
                _finalRange = 0;
                return audioSize;
            }

            mode = _prevMode.Value;
        }
        else
        {
            audioSize = _frameSize;
            mode = _mode;
            dec = new RangeDecoder(data);
        }

        if (audioSize > capacity || pcm.Length < audioSize * _channels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {audioSize * _channels} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        if (packetLoss)
        {
            DecodeConcealment(mode, audioSize, pcm);
            _finalRange = 0;
            return audioSize;
        }

        if (decodeFec)
        {
            return DecodeFecFrame(data, audioSize, pcm);
        }

        // Only a SILK-only frame can reach the merge below with an unwritten buffer. Its one
        // accumulating case is the optional 2.5 ms CELT flush (which covers just the start of the
        // frame); without it the SILK output is copied straight in, with no clear and no add.
        // CELT-only and hybrid frames overwrite every sample with the CELT layer before SILK is
        // added.
        bool silkAccumulates = false;

        bool transition = _prevMode is not null
            && ((mode == OpusMode.Celt && _prevMode != OpusMode.Celt && !_prevRedundancy)
                || (mode != OpusMode.Celt && _prevMode == OpusMode.Celt));

        if (transition && mode == OpusMode.Celt)
        {
            DecodeConcealment(_prevMode!.Value, Math.Min(_f5, audioSize), _pcmTransition);
        }

        // SILK layer.
        if (mode != OpusMode.Celt)
        {
            if (_prevMode == OpusMode.Celt)
            {
                // RFC 6716 §4.5.2: reset SILK before every SILK/hybrid frame that follows CELT.
                _silk.Reset();
            }

            _silk.Decode(ref dec, InternalRate(mode), _streamChannels, _frameDurationMs, _pcmSilk);
        }

        // Transition redundancy (§4.5.1).
        int len = data.Length;
        bool redundancy = false;
        bool celtToSilk = false;
        int redundancyBytes = 0;
        uint redundantRange = 0;
        if (mode != OpusMode.Celt)
        {
            int thresholdBits = 17 + (mode == OpusMode.Hybrid ? 20 : 0);
            if (dec.Tell() + thresholdBits <= 8 * len)
            {
                bool present = mode == OpusMode.Hybrid ? dec.DecodeBitLogp(12) : true;
                if (present)
                {
                    celtToSilk = dec.DecodeBitLogp(1);
                    redundancyBytes = mode == OpusMode.Hybrid
                        ? (int)dec.DecodeUInt(256) + 2
                        : len - ((dec.Tell() + 7) >> 3);
                    len -= redundancyBytes;
                    if (len * 8 < dec.Tell())
                    {
                        // A desynchronized size is not normative: discard the redundancy.
                        len = 0;
                        redundancyBytes = 0;
                        redundancy = false;
                    }
                    else
                    {
                        // RFC 6716 §4.5.1.3: hide the trailing redundant frame from the main
                        // coder's forward and raw-bit reads.
                        dec.TrimStorage(redundancyBytes);
                        redundancy = true;
                    }
                }
            }
        }

        int startBand = mode != OpusMode.Celt ? 17 : 0;
        int endBand = EndBand(_bandwidth);

        if (redundancy)
        {
            transition = false;
        }

        if (transition && mode != OpusMode.Celt)
        {
            DecodeConcealment(_prevMode!.Value, Math.Min(_f5, audioSize), _pcmTransition);
        }

        // A redundant CELT frame at the beginning is decoded before the main layer, without a
        // CELT reset (RFC 6716 §4.5.2: CELT-only to SILK/hybrid with redundancy).
        if (redundancy && celtToSilk)
        {
            redundantRange = DecodeRedundant(
                data.Slice(len, redundancyBytes), endBand);
        }

        // Main CELT layer (hybrid or CELT-only).
        if (mode != OpusMode.Silk)
        {
            if (mode != _prevMode && _prevMode is not null && !_prevRedundancy)
            {
                _celt.Reset();
            }

            _celt.Decode(
                ref dec, len, LinearToLog(_frameDuration), startBand, endBand, _streamChannels, pcm);
        }
        else if (_prevMode == OpusMode.Hybrid
            && !(redundancy && celtToSilk && _prevRedundancy))
        {
            // RFC 6716 §4.5: a hybrid-to-SILK transition flushes the CELT overlap by decoding a
            // 2.5 ms silence frame at CELT start band 0.
            pcm[..(audioSize * _channels)].Clear();
            silkAccumulates = true;
            _celt.DecodeSilenceFlush(endBand, _streamChannels, pcm);
        }

        if (mode != OpusMode.Celt)
        {
            int count = audioSize * _channels;
            if (mode == OpusMode.Silk && !silkAccumulates)
            {
                _pcmSilk.AsSpan(0, count).CopyTo(pcm);
            }
            else
            {
                CeltVec<PortableOps>.AddInPlace(pcm[..count], _pcmSilk.AsSpan(0, count));
            }
        }

        // A redundant CELT frame at the end cross-laps with the end of this frame
        // (RFC 6716 §4.5.1.4). CELT is reset before decoding it.
        if (redundancy && !celtToSilk)
        {
            _celt.Reset();
            redundantRange = DecodeRedundant(data.Slice(len, redundancyBytes), endBand);
            SmoothFade(
                pcm.Slice((audioSize - _f2_5) * _channels),
                _redundantAudio.AsSpan(_f2_5 * _channels),
                pcm.Slice((audioSize - _f2_5) * _channels),
                _f2_5);
        }

        if (redundancy && celtToSilk)
        {
            _redundantAudio.AsSpan(0, _f2_5 * _channels)
                .CopyTo(pcm[..(_f2_5 * _channels)]);
            SmoothFade(
                _redundantAudio.AsSpan(_f2_5 * _channels),
                pcm.Slice(_f2_5 * _channels),
                pcm.Slice(_f2_5 * _channels),
                _f2_5);
        }

        if (transition)
        {
            if (audioSize >= _f5)
            {
                _pcmTransition.AsSpan(0, _f2_5 * _channels).CopyTo(pcm);
                SmoothFade(
                    _pcmTransition.AsSpan(_f2_5 * _channels),
                    pcm.Slice(_f2_5 * _channels),
                    pcm.Slice(_f2_5 * _channels),
                    _f2_5);
            }
            else
            {
                SmoothFade(
                    _pcmTransition.AsSpan(0, _f2_5 * _channels),
                    pcm,
                    pcm,
                    _f2_5);
            }
        }

        _finalRange = len <= 1 ? 0 : dec.Rng ^ redundantRange;
        _prevMode = mode;
        _prevRedundancy = redundancy && !celtToSilk;
        return audioSize;
    }

    /// <summary>
    /// Decodes the in-band FEC (LBRR) frames of this packet to recover the previous frame
    /// (RFC 6716 §4.2.4–§4.2.5, Appendix A <c>opus_decode_frame()</c> with <c>decode_fec</c>).
    /// </summary>
    /// <param name="data">The frame's bytes, from the packet that follows the lost one.</param>
    /// <param name="audioSize">Samples per channel the frame produces.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    /// <returns>The number of samples per channel written.</returns>
    /// <remarks>
    /// SILK and hybrid frames decode their LBRR data; an interval without LBRR falls back to
    /// packet loss concealment, as the reference does. The CELT layer has no FEC, so for CELT-only
    /// and hybrid frames it contributes concealment (<c>celt_decode_with_ec(NULL)</c>). Output has
    /// the duration of the frame in <paramref name="data"/>, which is the frame size the caller
    /// sees, not the size of the lost frame.
    /// </remarks>
    private int DecodeFecFrame(ReadOnlySpan<byte> data, int audioSize, Span<float> pcm)
    {
        OpusMode mode = _mode;
        var dec = new RangeDecoder(data);
        pcm[..(audioSize * _channels)].Clear();

        if (mode != OpusMode.Celt)
        {
            if (_prevMode == OpusMode.Celt)
            {
                _silk.Reset();
            }

            _silk.DecodeFec(
                ref dec, InternalRate(mode), _streamChannels, _frameDurationMs, _pcmSilk);
        }

        if (mode != OpusMode.Silk)
        {
            if (mode != _prevMode && _prevMode is not null && !_prevRedundancy)
            {
                _celt.Reset();
            }

            _celt.DecodeLost(
                LogForSamples(audioSize),
                mode == OpusMode.Hybrid ? 17 : 0,
                pcm[..(audioSize * _channels)]);
        }

        if (mode != OpusMode.Celt)
        {
            int count = audioSize * _channels;
            CeltVec<PortableOps>.AddInPlace(pcm[..count], _pcmSilk.AsSpan(0, count));
        }

        _finalRange = dec.Rng;
        _prevMode = mode;
        _prevRedundancy = false;
        return audioSize;
    }

    /// <summary>Decodes a redundant 5 ms CELT frame from its own range-coded bytes.</summary>
    /// <param name="data">The redundant frame's bytes.</param>
    /// <param name="endBand">One past the last coded band, from the current packet's bandwidth.</param>
    /// <returns>The redundant frame's final range.</returns>
    private uint DecodeRedundant(ReadOnlySpan<byte> data, int endBand) =>
        _celt.DecodeRedundant(data, endBand, _streamChannels, _redundantAudio);

    /// <summary>
    /// Writes concealment for a lost frame in the mode of the last good frame (RFC 6716 §4.4).
    /// </summary>
    /// <param name="mode">Coding mode of the frame being concealed.</param>
    /// <param name="samples">Samples per channel to write.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    private void DecodeConcealment(OpusMode mode, int samples, Span<float> pcm)
    {
        int durationMs = samples * 1000 / _apiRate;
        switch (mode)
        {
            case OpusMode.Celt:
                ConcealCelt(samples, startBand: 0, pcm);
                break;
            case OpusMode.Silk:
                ConcealSilk(Math.Max(durationMs, 10), samples, pcm[..(samples * _channels)]);
                break;
            default:
                ConcealHybrid(samples, pcm);
                break;
        }
    }

    /// <summary>Conceals a CELT-only frame, splitting at 20 ms as the reference does.</summary>
    /// <param name="samples">Samples per channel to conceal.</param>
    /// <param name="startBand">First band: 0 for CELT-only, 17 for the CELT half of a hybrid frame.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    private void ConcealCelt(int samples, int startBand, Span<float> pcm)
    {
        int offset = 0;
        while (offset < samples)
        {
            int chunk = Math.Min(_f20, samples - offset);
            _celt.DecodeLost(
                LogForSamples(chunk),
                startBand,
                pcm.Slice(offset * _channels, chunk * _channels));
            offset += chunk;
        }
    }

    /// <summary>Conceals a hybrid frame: SILK for the low band, CELT for the high band.</summary>
    /// <param name="samples">Samples per channel to conceal.</param>
    /// <param name="pcm">Destination for interleaved samples in [-1, 1].</param>
    private void ConcealHybrid(int samples, Span<float> pcm)
    {
        int offset = 0;
        while (offset < samples)
        {
            int chunk = Math.Min(_f20, samples - offset);
            int chunkMs = chunk * 1000 / _apiRate;
            ConcealSilk(Math.Max(chunkMs, 10), chunk, _pcmSilk);
            _celt.DecodeLost(
                LogForSamples(chunk),
                startBand: 17,
                pcm.Slice(offset * _channels, chunk * _channels));
            int baseOffset = offset * _channels;
            int count = chunk * _channels;
            for (int i = 0; i < count; i++)
            {
                pcm[baseOffset + i] += _pcmSilk[i];
            }

            offset += chunk;
        }
    }

    /// <summary>
    /// Conceals SILK audio, producing whole 10 ms or 20 ms frames even when fewer samples are
    /// requested, because the SILK PLC cannot make shorter frames (Appendix A, dec_API.c).
    /// </summary>
    /// <param name="concealDurationMs">Duration the SILK PLC runs for: at least 10 ms.</param>
    /// <param name="requestedSamples">Samples per channel the caller wants.</param>
    /// <param name="destination">Destination for the requested samples.</param>
    /// <returns>The number of samples per channel written.</returns>
    private int ConcealSilk(int concealDurationMs, int requestedSamples, Span<float> destination)
    {
        if (concealDurationMs == requestedSamples * 1000 / _apiRate)
        {
            return _silk.DecodeLost(concealDurationMs, destination);
        }

        _silk.DecodeLost(concealDurationMs, _pcmSilk);
        int count = requestedSamples * _channels;
        _pcmSilk.AsSpan(0, count).CopyTo(destination);
        return requestedSamples;
    }

    /// <summary>Applies the CELT power-complementary cross-fade used for transitions.</summary>
    /// <param name="first">Signal faded out; at least <paramref name="overlap"/> frames.</param>
    /// <param name="second">Signal faded in; at least <paramref name="overlap"/> frames.</param>
    /// <param name="output">Destination; may alias <paramref name="first"/> or <paramref name="second"/>.</param>
    /// <param name="overlap">Number of interleaved frames to cross-fade.</param>
    private void SmoothFade(ReadOnlySpan<float> first, ReadOnlySpan<float> second, Span<float> output, int overlap)
    {
        ReadOnlySpan<float> window = _celt.WindowSquared;
        int step = 48000 / _apiRate;
        for (int i = 0; i < overlap; i++)
        {
            float weight = window[i * step];
            for (int channel = 0; channel < _channels; channel++)
            {
                int index = (i * _channels) + channel;
                output[index] = (weight * second[index]) + ((1f - weight) * first[index]);
            }
        }
    }

    /// <summary>Maps the TOC bandwidth to the exclusive CELT end band of RFC 6716 Table 2.</summary>
    /// <param name="bandwidth">The packet bandwidth.</param>
    /// <returns>One past the last coded CELT band.</returns>
    private static int EndBand(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.Narrowband => 13,
        OpusBandwidth.Mediumband => 17,
        OpusBandwidth.Wideband => 17,
        OpusBandwidth.SuperWideband => 19,
        _ => 21,
    };

    /// <summary>Returns the SILK internal rate for a mode and bandwidth.</summary>
    /// <param name="mode">Coding mode; must not be <see cref="OpusMode.Celt"/>.</param>
    /// <returns>The internal rate in Hz.</returns>
    private int InternalRate(OpusMode mode) => mode == OpusMode.Hybrid
        ? 16000
        : _bandwidth switch
        {
            OpusBandwidth.Narrowband => 8000,
            OpusBandwidth.Mediumband => 12000,
            _ => 16000,
        };

    /// <summary>Returns the Opus frame duration in milliseconds.</summary>
    /// <param name="duration">The TOC frame duration.</param>
    /// <returns>The duration in milliseconds.</returns>
    private static int DurationMilliseconds(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms10 => 10,
        OpusFrameDuration.Ms20 => 20,
        OpusFrameDuration.Ms40 => 40,
        OpusFrameDuration.Ms60 => 60,
        _ => throw new InvalidDataException("A SILK-only or hybrid frame must be 10, 20, 40 or 60 ms."),
    };

    /// <summary>Maps an Opus frame duration to the CELT <c>LM</c> parameter.</summary>
    /// <param name="duration">The TOC frame duration.</param>
    /// <returns>LOG2 of the number of short blocks, 0 to 3.</returns>
    private static int LinearToLog(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms2_5 => 0,
        OpusFrameDuration.Ms5 => 1,
        OpusFrameDuration.Ms10 => 2,
        OpusFrameDuration.Ms20 => 3,
        OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60 => 3,
        _ => 3,
    };

    /// <summary>Chooses the CELT <c>LM</c> that covers a concealment length in API samples.</summary>
    /// <param name="samples">Samples per channel to conceal.</param>
    /// <returns>LOG2 of the number of 48 kHz short blocks.</returns>
    private int LogForSamples(int samples)
    {
        int samples48 = samples * (48000 / _apiRate);
        if (samples48 <= 120)
        {
            return 0;
        }

        return samples48 <= 240 ? 1 : samples48 <= 480 ? 2 : 3;
    }
}
