// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Encoder;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk.Encoder;

namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) encoder.</summary>
/// <remarks>
/// <para>
/// The options select one of three encoder paths at construction:
/// </para>
/// <list type="bullet">
/// <item><description><b>Automatic per-frame SILK, hybrid or CELT selection:</b>
/// <see cref="OpusEncoderOptions.Application"/> is <see cref="OpusApplication.Voip"/> or
/// <see cref="OpusApplication.Audio"/>, mono, 48 kHz, 10 or 20 ms frames, and
/// <see cref="OpusEncoderOptions.UseDtx"/> is false. Each frame is coded as SILK-only, hybrid
/// (SILK wideband plus CELT from band 17) or CELT-only from the bitrate, a per-frame voice/music
/// estimate, the application and the reference's decision thresholds.
/// <see cref="OpusEncoderOptions.Bandwidth"/>, when set, caps the audio bandwidth. In-band FEC is
/// supported, so SILK and hybrid frames can carry LBRR.</description></item>
/// <item><description><b>SILK-only:</b> <see cref="OpusApplication.Voip"/> with a bandwidth that is
/// null, narrowband, mediumband or wideband, 10, 20, 40 or 60 ms frames, any sample rate, mono or
/// stereo, when the automatic case does not apply. In-band FEC and DTX are supported.</description></item>
/// <item><description><b>CELT-only:</b> everything else. That is
/// <see cref="OpusApplication.RestrictedLowDelay"/> always; <see cref="OpusApplication.Audio"/> in
/// stereo, at a rate other than 48 kHz or at a frame size other than 10 or 20 ms; and
/// <see cref="OpusApplication.Voip"/> with 2.5 or 5 ms frames or a super-wideband or fullband
/// bandwidth outside the automatic case. Frames of 40 and 60 ms are packed as two or three 20 ms
/// frames in one packet. Requesting <see cref="OpusEncoderOptions.UseInbandFec"/> or
/// <see cref="OpusEncoderOptions.UseDtx"/> on this path makes the constructor throw
/// <see cref="NotSupportedException"/>.</description></item>
/// </list>
/// <para>
/// <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/> writes one complete Opus packet: the TOC
/// byte followed by the frame data for the selected path. It allocates nothing after construction.
/// </para>
/// <para>
/// An encoder instance is not thread-safe: it carries the codec state of one stream, so calls on
/// one instance must not overlap. Separate instances are independent and can be used concurrently
/// on different threads.
/// </para>
/// </remarks>
public sealed class OpusEncoder : IDisposable
{
    private readonly CeltEncoder? _encoder;
    private readonly SilkEncoderCore? _silk;
    private readonly SilkStereoPacketEncoder? _silkPacket;
    private readonly int _silkFrameSamples;
    private readonly byte _silkToc;
    private readonly OpusFrameDuration _tocDuration;
    private readonly int _subFrameSamples;
    private readonly int _subFrameCount;
    private readonly int _inputFrameSamples;
    private readonly short[] _conversionBuffer;
    private readonly byte[] _subFrameData;

    // Automatic mode selection (Voip/Audio, mono, 48 kHz, 10 or 20 ms).
    private readonly OpusModeSelector? _selector;
    private readonly CeltEncoder? _autoCelt;
    private readonly CeltRedundantEncoder? _redundantCelt;
    private readonly ISilkLayerEncoder? _silkLayer;
    private readonly OpusBandwidth _autoSilkBandwidth;
    private readonly OpusBandwidth _autoMaxBandwidth;
    private readonly int _autoFrameSamples;
    private int _voiceEstimate;
    private uint _autoRange;
    private bool _disposed;

    /// <summary>The length of a DTX packet: the TOC byte and an empty payload (RFC 6716 §3.2.1).</summary>
    private const int DtxPacketLength = 1;

    /// <summary>Creates an encoder after validating and checking <paramref name="options"/>.</summary>
    /// <param name="options">Encoder configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is illegal.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="OpusEncoderOptions.UseInbandFec"/> or <see cref="OpusEncoderOptions.UseDtx"/> was
    /// requested with options that select the CELT-only path, which supports neither.
    /// </exception>
    public OpusEncoder(OpusEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Options = options;

        // Automatic per-frame mode selection uses the SILK and CELT encoders side by side. The
        // SILK layer is mono and 48 kHz only, and hybrid frames are 10 or 20 ms (§4.5.2). DTX is
        // handled by the fixed SILK path, where it can suppress whole packets.
        bool autoMode = options.Application != OpusApplication.RestrictedLowDelay
            && options.Channels == 1
            && options.SampleRate == 48000
            && options.FrameDuration is OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20
            && !options.UseDtx;
        if (autoMode)
        {
            _autoMaxBandwidth = options.Bandwidth ?? OpusBandwidth.Fullband;
            _autoSilkBandwidth = options.Bandwidth switch
            {
                OpusBandwidth.Narrowband => OpusBandwidth.Narrowband,
                OpusBandwidth.Mediumband => OpusBandwidth.Mediumband,
                _ => OpusBandwidth.Wideband,
            };

            int frameDurationMs = options.FrameDuration == OpusFrameDuration.Ms10 ? 10 : 20;
            int silkRate = _autoSilkBandwidth switch
            {
                OpusBandwidth.Narrowband => 8000,
                OpusBandwidth.Mediumband => 12000,
                _ => 16000,
            };

            _selector = new OpusModeSelector();
            if (options.UseInbandFec)
            {
                // The multi-channel packet encoder writes the VAD bits, LBRR flags, redundant
                // frames and the SILK frame into the packet's coder, which is what the hybrid and
                // SILK-only writers need for in-band FEC.
                _silkPacket = new SilkStereoPacketEncoder(
                    48000,
                    silkRate,
                    frameDurationMs,
                    options.BitrateBps,
                    options.Complexity,
                    options.UseCbr,
                    channels: 1,
                    useInbandFec: true,
                    options.PacketLossPercent,
                    useDtx: false,
                    options.PerformanceMode);
                _silkLayer = new SilkFecLayerAdapter(_silkPacket);
            }
            else
            {
                _silk = new SilkEncoderCore(
                    48000, silkRate, frameDurationMs, options.BitrateBps, options.Complexity, options.UseCbr,
                    options.PerformanceMode);
                _silkLayer = new SilkLayerAdapter(_silk);
            }

            _autoCelt = new CeltEncoder(
                options.FrameDuration,
                options.BitrateBps,
                options.Complexity,
                channels: 1,
                48000,
                options.UseVbr,
                options.ConstrainedVbr,
                options.PerformanceMode);
            _redundantCelt = new CeltRedundantEncoder(
                Math.Clamp(options.BitrateBps, 3200, 257 * 1600), options.Complexity, options.PerformanceMode);
            _autoFrameSamples = frameDurationMs * 48;

            // The reference seeds its voice estimate from the application and refines it with a
            // running speech/music classification (opus_encoder.c, voice_ratio).
            _voiceEstimate = options.Application == OpusApplication.Voip ? 115 : 48;
            _conversionBuffer = new short[_autoFrameSamples];
            _subFrameData = [];
            _tocDuration = options.FrameDuration;
            return;
        }

        // SILK owns Voip at SILK frame durations and a SILK bandwidth, mono or stereo. Anything
        // else that asks for Voip keeps the pre-SILK behaviour and encodes with CELT, so existing
        // callers (2.5/5 ms, super-wideband hints) are not broken.
        OpusBandwidth? requestedBandwidth = options.Bandwidth;
        bool silkBandwidth = requestedBandwidth is null
            or OpusBandwidth.Narrowband or OpusBandwidth.Mediumband or OpusBandwidth.Wideband;
        bool useSilk = options.Application == OpusApplication.Voip
            && silkBandwidth
            && options.FrameDuration is OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20
                or OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60;

        if (useSilk)
        {
            OpusBandwidth bandwidth = requestedBandwidth ?? OpusBandwidth.Wideband;
            int internalRate = bandwidth switch
            {
                OpusBandwidth.Narrowband => 8000,
                OpusBandwidth.Mediumband => 12000,
                OpusBandwidth.Wideband => 16000,
                _ => 16000,
            };

            int frameDurationMs = options.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                OpusFrameDuration.Ms60 => 60,
                _ => throw new NotSupportedException("Unsupported SILK frame duration."),
            };

            _silkPacket = new SilkStereoPacketEncoder(
                options.SampleRate,
                internalRate,
                frameDurationMs,
                options.BitrateBps,
                options.Complexity,
                options.UseCbr,
                options.Channels,
                options.UseInbandFec,
                options.PacketLossPercent,
                options.UseDtx,
                options.PerformanceMode);
            _silkFrameSamples = options.SampleRate * frameDurationMs / 1000 * options.Channels;
            _conversionBuffer = new short[_silkFrameSamples];
            _subFrameData = [];

            // RFC 6716 §3.1, Table 2: SILK-only configs 0..11, in (bandwidth, duration) order.
            int bandwidthIndex = bandwidth switch
            {
                OpusBandwidth.Narrowband => 0,
                OpusBandwidth.Mediumband => 1,
                _ => 2,
            };
            int durationIndex = options.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 0,
                OpusFrameDuration.Ms20 => 1,
                OpusFrameDuration.Ms40 => 2,
                _ => 3,
            };
            _silkToc = (byte)(((bandwidthIndex * 4) + durationIndex) << 3);
            if (options.Channels == 2)
            {
                // RFC 6716 §3.1: bit 2 of the TOC is the SILK/hybrid stereo flag.
                _silkToc |= 0x04;
            }

            return;
        }

        // Audio and RestrictedLowDelay both select the CELT path, as before the SILK encoder.
        if (options.UseInbandFec)
        {
            throw new NotSupportedException("In-band forward error correction (LBRR) is a SILK feature and is not available on the CELT-only path selected by these options.");
        }

        if (options.UseDtx)
        {
            throw new NotSupportedException("Discontinuous transmission is only available on the SILK-only path (Voip, narrowband to wideband, 10 to 60 ms frames).");
        }

        Options = options;

        // CELT packets are at most 20 ms; 40 and 60 ms are split into 20 ms frames (RFC 6716
        // §3.2.5, Appendix A opus_encoder.c). The packet TOC therefore advertises 20 ms.
        _tocDuration = options.FrameDuration is OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60
            ? OpusFrameDuration.Ms20
            : options.FrameDuration;
        _subFrameCount = options.FrameDuration switch
        {
            OpusFrameDuration.Ms40 => 2,
            OpusFrameDuration.Ms60 => 3,
            _ => 1,
        };

        _encoder = new CeltEncoder(
            _tocDuration,
            options.BitrateBps,
            options.Complexity,
            options.Channels,
            options.SampleRate,
            options.UseVbr,
            options.ConstrainedVbr,
            options.PerformanceMode);
        _subFrameSamples = _encoder.FrameSamples;
        _inputFrameSamples = _subFrameSamples * _subFrameCount * options.Channels;
        _silkFrameSamples = _inputFrameSamples;
        _conversionBuffer = new short[_inputFrameSamples];

        // VBR may use the whole maximum frame; CBR uses only the configured size.
        _subFrameData = new byte[_subFrameCount * _encoder.MaxFrameBytes];
    }

    /// <summary>The validated options this encoder was created with.</summary>
    public OpusEncoderOptions Options { get; }

    /// <summary>
    /// Gets the range-coder final value of the most recently encoded frame. For a packet stream,
    /// this is the value a conformant decoder must report after decoding the packet.
    /// </summary>
    public uint FinalRange => _selector is not null
        ? _autoRange
        : _silkPacket?.FinalRange ?? (_encoder?.FinalRange ?? 0);

    /// <summary>Encodes one frame of interleaved 16-bit PCM into an Opus packet.</summary>
    /// <param name="pcm">
    /// Exactly one frame of interleaved 16-bit PCM at <see cref="OpusEncoderOptions.SampleRate"/>.
    /// </param>
    /// <param name="packet">
    /// Destination for the packet; must hold the TOC byte and the frame. Constant bitrate needs
    /// the configured frame size plus the framing bytes; variable bitrate may use as much of the
    /// buffer as is available (up to 1275 bytes per 20 ms frame). A buffer of 1276 bytes (the
    /// 1275-byte maximum frame plus the TOC byte) always suffices for frames up to 20 ms.
    /// </param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not exactly one frame, or <paramref name="packet"/> is too small.
    /// </exception>
    public int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_selector is not null)
        {
            return EncodeAuto(pcm, packet);
        }

        if (_silkPacket is not null)
        {
            if (pcm.Length != _silkFrameSamples)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The frame must contain exactly {_silkFrameSamples} samples; got {pcm.Length}."),
                    nameof(pcm));
            }

            if (packet.Length < DtxPacketLength)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The packet buffer must hold at least 1 byte; got {packet.Length}."),
                    nameof(packet));
            }

            packet[0] = _silkToc;
            int silkPayload = _silkPacket.Encode(pcm, packet[1..]);
            if (silkPayload == 0)
            {
                // RFC 6716 Appendix A: an all-DTX packet carries the TOC byte and no payload.
                return DtxPacketLength;
            }

            return silkPayload + 1;
        }

        if (pcm.Length != _inputFrameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_inputFrameSamples} interleaved samples; got {pcm.Length}."),
                nameof(pcm));
        }

        // The constructor creates exactly one of the two back ends.
        CeltEncoder encoder = _encoder ?? throw new InvalidOperationException("No encoder back end.");
        int frameBytes = encoder.FrameBytes;
        int minimumPacket = Options.UseVbr
            ? 2 + (_subFrameCount * 2)
            : (_subFrameCount == 1 ? frameBytes + 1 : (frameBytes * _subFrameCount) + 1 + _subFrameCount);
        if (packet.Length < minimumPacket)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The packet buffer must hold at least {minimumPacket} bytes; got {packet.Length}."),
                nameof(packet));
        }

        if (_subFrameCount == 1)
        {
            packet[0] = BuildToc();
            return encoder.EncodeFrame(pcm, packet[1..]) + 1;
        }

        // 40 and 60 ms CELT are code 1/code 3 packets of 20 ms frames (RFC 6716 Appendix A,
        // opus_encoder.c: the "can't support higher than wideband for > 20 ms frames" block).
        int subFrameInterleaved = _subFrameSamples * Options.Channels;
        Span<int> sizes = stackalloc int[_subFrameCount];
        int dataBytes = 0;
        for (int i = 0; i < _subFrameCount; i++)
        {
            int size = encoder.EncodeFrame(
                pcm.Slice(i * subFrameInterleaved, subFrameInterleaved), _subFrameData.AsSpan(dataBytes));
            sizes[i] = size;
            dataBytes += size;
        }

        return OpusPacketWriter.Write(packet, BuildToc(), sizes, _subFrameData.AsSpan(0, dataBytes));
    }

    /// <summary>Encodes one frame of interleaved floating-point PCM in [-1, 1] into an Opus packet.</summary>
    /// <param name="pcm">
    /// Exactly one frame of interleaved normalized samples at <see cref="OpusEncoderOptions.SampleRate"/>.
    /// </param>
    /// <param name="packet">
    /// Destination for the packet. See <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/> for
    /// how to size it; a 1276-byte buffer always suffices for frames up to 20 ms.
    /// </param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not exactly one frame, or <paramref name="packet"/> is too small.
    /// </exception>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pcm.Length != _conversionBuffer.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_conversionBuffer.Length} interleaved samples; got {pcm.Length}."),
                nameof(pcm));
        }

        for (int i = 0; i < pcm.Length; i++)
        {
            _conversionBuffer[i] = CeltPcm.ToInt16(pcm[i]);
        }

        return Encode(_conversionBuffer.AsSpan(), packet);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Encodes one frame with automatic SILK/hybrid/CELT selection (RFC 6716 Appendix A,
    /// <c>opus_encoder.c</c>). The SILK internal bandwidth is fixed at construction, so the mode
    /// and CELT bandwidth follow the selector while a SILK-only frame keeps the configured
    /// bandwidth.
    /// </summary>
    private int EncodeAuto(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        if (pcm.Length != _autoFrameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_autoFrameSamples} samples; got {pcm.Length}."),
                nameof(pcm));
        }

        if (packet.Length < 4)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The packet buffer must hold at least 4 bytes; got {packet.Length}."),
                nameof(packet));
        }

        var request = new OpusModeRequest(Options.BitrateBps, Options.Application, channels: 1, Options.FrameDuration)
        {
            Complexity = Options.Complexity,
            UseVbr = Options.UseVbr,
            MaxBandwidth = _autoMaxBandwidth,
            MaxDataBytes = packet.Length,
            SilkAllowsBandwidthSwitch = true,

            // The SILK core is constructed wideband, so it is already in wideband mode and the
            // reference's "hold at wideband until SILK settles" guard never needs to bind.
            SilkInWidebandWithoutVariableLp = _autoSilkBandwidth == OpusBandwidth.Wideband,
            VoiceEstimate = UpdateVoiceEstimate(pcm),
        };
        OpusModeDecision decision = _selector!.Decide(request);

        bool hybrid = decision.Mode == OpusMode.Hybrid && _autoSilkBandwidth == OpusBandwidth.Wideband;
        if (hybrid)
        {
            OpusBandwidth bandwidth = (int)decision.Bandwidth > (int)OpusBandwidth.Wideband
                ? decision.Bandwidth
                : OpusBandwidth.SuperWideband;
            int endBand = OpusTransitionEncoder.EndBandOf(bandwidth);
            int bytes = OpusHybridEncoder.Encode(
                _silkLayer!,
                _autoCelt!,
                _redundantCelt!,
                pcm,
                48000,
                _autoFrameSamples,
                Options.BitrateBps,
                packet.Length - 1,
                endBand,
                decision.Redundancy,
                decision.CeltToSilk,
                Options.UseCbr,
                packet[1..],
                out _autoRange);
            packet[0] = OpusTocBuilder.Build(OpusMode.Hybrid, bandwidth, Options.FrameDuration, stereo: false);
            return bytes + 1;
        }

        if (decision.Mode == OpusMode.Celt)
        {
            int endBand = OpusTransitionEncoder.EndBandOf(decision.Bandwidth);

            // The automatic CELT fallback must fit the nominal CBR packet, so its payload is the
            // nominal size less the TOC byte. Standalone CELT keeps its own one-byte-larger
            // convention (see CeltEncoder.EncodeFrame); VBR is not bounded by the nominal size.
            int cbrPayload = Options.UseVbr ? -1 : Math.Max(1, NominalCbrPacketBytes() - 1);
            int minimum = Options.UseVbr ? 2 : Math.Min(_autoCelt!.FrameBytes, cbrPayload) + 1;
            if (packet.Length < minimum)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The packet buffer must hold at least {minimum} bytes; got {packet.Length}."),
                    nameof(packet));
            }

            packet[0] = OpusTocBuilder.Build(OpusMode.Celt, decision.Bandwidth, Options.FrameDuration, stereo: false);
            int celtBytes = _autoCelt!.EncodeFrame(pcm, packet[1..], 0, endBand, cbrPayload);
            _autoRange = _autoCelt.FinalRange;
            return celtBytes + 1;
        }

        // SILK-only, or a hybrid request that the configured SILK bandwidth cannot satisfy.
        int frameBytes = OpusTransitionEncoder.EncodeSilkOnlyFrame(
            _silkLayer!,
            _redundantCelt!,
            pcm,
            _autoFrameSamples,
            Options.BitrateBps,
            packet.Length - 1,
            _autoSilkBandwidth,
            decision.Redundancy,
            decision.CeltToSilk,
            SilkOnlyFrameBudget(packet.Length - 1),
            packet[1..],
            out _autoRange);
        packet[0] = OpusTocBuilder.Build(OpusMode.Silk, _autoSilkBandwidth, Options.FrameDuration, stereo: false);
        return frameBytes + 1;
    }

    /// <summary>
    /// The payload budget of a SILK-only frame: in CBR the nominal packet size (less the TOC byte), so a
    /// redundant frame cannot push the packet past it.
    /// </summary>
    private int SilkOnlyFrameBudget(int capacity)
    {
        if (!Options.UseCbr)
        {
            return capacity;
        }

        int frameRate = 48000 / _autoFrameSamples;
        int cbrBytes = (Options.BitrateBps + (4 * frameRate)) / (8 * frameRate);
        return Math.Max(1, Math.Min(capacity, cbrBytes - 1));
    }

    /// <summary>
    /// The nominal CBR packet size in bytes, TOC included: the frame duration's bitrate budget. The
    /// automatic CELT fallback sizes its payload one byte under this so the packet fits.
    /// </summary>
    private int NominalCbrPacketBytes()
    {
        // bitrate (bits/s) * frame duration (s) / 8 (bits/byte), floored like the nominal size gate.
        return Math.Max(1, (int)((long)Options.BitrateBps * _autoFrameSamples / (48000 * 8)));
    }

    /// <summary>
    /// Updates the running speech/music estimate from cheap time-domain features: short-term
    /// energy gates silence, and the zero-crossing rate separates low-frequency voiced speech
    /// from noisier music. The estimate is smoothed so mode changes follow the signal with
    /// hysteresis rather than flipping every frame.
    /// </summary>
    /// <param name="pcm">One frame of mono input.</param>
    /// <returns>The smoothed estimate, 0 (music) to 127 (voice).</returns>
    private int UpdateVoiceEstimate(ReadOnlySpan<short> pcm)
    {
        long energy = 0;
        int crossings = 0;
        short previous = pcm.Length > 0 ? pcm[0] : (short)0;
        for (int i = 0; i < pcm.Length; i++)
        {
            int sample = pcm[i];
            energy += (long)sample * sample;
            if (i > 0 && (sample >= 0) != (previous >= 0))
            {
                crossings++;
            }

            previous = (short)sample;
        }

        double meanSquare = pcm.Length == 0 ? 0 : energy / (double)pcm.Length;
        if (meanSquare < 256)
        {
            // Silence carries no classification evidence.
            return _voiceEstimate;
        }

        bool voiceLike = crossings < pcm.Length / 8;
        int instant = voiceLike ? 115 : 48;
        _voiceEstimate = ((_voiceEstimate * 7) + instant + 4) / 8;
        return _voiceEstimate;
    }

    private byte BuildToc()
    {
        // CELT-only configurations 16..31 (RFC 6716 §3.1, Table 2): the bandwidth group depends on
        // the API rate and the low two bits select the frame duration. Bit 2 is the stereo flag.
        // A 40 or 60 ms input frame is signalled as 20 ms and split into several frames.
        int config = InputResampler.TocConfigBase(Options.SampleRate) + (int)_tocDuration;
        int toc = config << 3;
        if (Options.Channels == 2)
        {
            toc |= 0x04;
        }

        return (byte)toc;
    }

    /// <summary>
    /// The SILK layer backed by the multi-channel packet encoder, so SILK and hybrid frames can
    /// carry in-band FEC (LBRR). It writes the VAD bits, the LBRR flags, the redundant frames and
    /// the SILK frame into the caller's range coder, exactly as the decoder reads them.
    /// </summary>
    private sealed class SilkFecLayerAdapter : ISilkLayerEncoder
    {
        private readonly SilkStereoPacketEncoder _packet;

        internal SilkFecLayerAdapter(SilkStereoPacketEncoder packet) => _packet = packet;

        /// <inheritdoc />
        public void Encode(ref RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes) =>
            _packet.EncodeInto(ref encoder, pcm[..frameSamples], maxFrameBytes + 1);
    }

    /// <summary>Adapts the SILK core to the transition encoder's layer interface.</summary>
    private sealed class SilkLayerAdapter : ISilkLayerEncoder
    {
        private readonly SilkEncoderCore _core;

        internal SilkLayerAdapter(SilkEncoderCore core) => _core = core;

        /// <inheritdoc />
        public void Encode(ref RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes)
        {
            _core.EncodeInto(ref encoder, pcm, maxFrameBytes + 1);
        }
    }
}
