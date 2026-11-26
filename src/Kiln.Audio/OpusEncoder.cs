// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Encoder;
using Kiln.Audio.Silk.Encoder;

namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) encoder.</summary>
/// <remarks>
/// <para>
/// Two configurations are implemented. <see cref="OpusApplication.RestrictedLowDelay"/> selects
/// the CELT-only restricted-low-delay encoder (48 kHz, mono, CBR, 2.5, 5, 10, 20, 40 or 60 ms);
/// a 40 or 60 ms frame is encoded as two or three 20 ms frames in one code 1 or code 3 packet.
/// <see cref="OpusApplication.Voip"/> selects the SILK-only encoder (mono, 10, 20, 40 or 60 ms,
/// at <see cref="OpusEncoderOptions.Bandwidth"/> narrowband, mediumband or wideband).
/// </para>
/// <para>
/// Hybrid mode, stereo, in-band FEC, DTX and automatic mode switching are not implemented and are
/// rejected with <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/> writes one complete Opus packet: the TOC
/// byte followed by the frame or frames. It allocates nothing after construction.
/// </para>
/// </remarks>
public sealed class OpusEncoder : IDisposable
{
    private readonly CeltEncoder? _encoder;
    private readonly SilkEncoderCore? _silk;
    private readonly OpusFrameDuration _tocDuration;
    private readonly int _subFrameSamples;
    private readonly int _subFrameCount;
    private readonly int _inputFrameSamples;
    private readonly int _silkFrameSamples;
    private readonly byte _silkToc;
    private readonly short[] _conversionBuffer;
    private readonly byte[] _subFrameData;
    private bool _disposed;

    /// <summary>Creates an encoder after validating and checking <paramref name="options"/>.</summary>
    /// <param name="options">Encoder configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is illegal.</exception>
    /// <exception cref="NotSupportedException">A legal but unsupported option was requested.</exception>
    public OpusEncoder(OpusEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Channels != 1)
        {
            throw new NotSupportedException(
                FormattableString.Invariant($"Only mono input is supported; got {options.Channels} channels."));
        }

        Options = options;

        if (options.Application == OpusApplication.Voip)
        {
            if (options.UseInbandFec)
            {
                throw new NotSupportedException("In-band forward error correction (LBRR) is not implemented.");
            }

            if (options.UseDtx)
            {
                throw new NotSupportedException("Discontinuous transmission is not implemented.");
            }

            if (options.FrameDuration is OpusFrameDuration.Ms2_5 or OpusFrameDuration.Ms5)
            {
                throw new NotSupportedException(
                    FormattableString.Invariant($"SILK supports 10, 20, 40 and 60 ms frames; got {options.FrameDuration}."));
            }

            OpusBandwidth bandwidth = options.Bandwidth ?? OpusBandwidth.Wideband;
            int internalRate = bandwidth switch
            {
                OpusBandwidth.Narrowband => 8000,
                OpusBandwidth.Mediumband => 12000,
                OpusBandwidth.Wideband => 16000,
                _ => throw new NotSupportedException(
                    "SILK supports narrowband, mediumband and wideband; hybrid and super-wideband/fullband are not implemented."),
            };

            int frameDurationMs = options.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                OpusFrameDuration.Ms60 => 60,
                _ => throw new NotSupportedException("Unsupported SILK frame duration."),
            };

            _silk = new SilkEncoderCore(
                options.SampleRate,
                internalRate,
                frameDurationMs,
                options.BitrateBps,
                options.Complexity,
                options.UseCbr);
            _silkFrameSamples = options.SampleRate * frameDurationMs / 1000;
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
            return;
        }

        if (options.Application != OpusApplication.RestrictedLowDelay)
        {
            throw new NotSupportedException(
                "Only OpusApplication.Voip (SILK) and OpusApplication.RestrictedLowDelay (CELT) are supported.");
        }

        if (options.SampleRate != 48000)
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"Only 48 kHz input is supported by this CELT encoder; got {options.SampleRate} Hz."));
        }

        if (options.UseInbandFec)
        {
            throw new NotSupportedException("In-band forward error correction (LBRR) is not implemented.");
        }

        if (options.UseDtx)
        {
            throw new NotSupportedException("Discontinuous transmission is not implemented.");
        }

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

        _encoder = new CeltEncoder(_tocDuration, options.BitrateBps, options.Complexity);
        _subFrameSamples = _encoder.FrameSamples;
        _inputFrameSamples = _subFrameSamples * _subFrameCount;
        _silkFrameSamples = _inputFrameSamples;
        _conversionBuffer = new short[_inputFrameSamples];
        _subFrameData = new byte[_subFrameCount * _encoder.FrameBytes];
    }

    /// <summary>The validated options this encoder was created with.</summary>
    public OpusEncoderOptions Options { get; }

    /// <summary>
    /// Gets the range-coder final value of the most recently encoded frame. For a packet stream,
    /// this is the value a conformant decoder must report after decoding the packet.
    /// </summary>
    public uint FinalRange => _silk?.FinalRange ?? (_encoder?.FinalRange ?? 0);

    /// <summary>Encodes one frame of interleaved 16-bit PCM into an Opus packet.</summary>
    /// <param name="pcm">Exactly one frame of mono 16-bit PCM at the configured sample rate.</param>
    /// <param name="packet">Destination for the packet; must hold the TOC byte and the frame.</param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not exactly one frame, or <paramref name="packet"/> is too small.
    /// </exception>
    public int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_silk is not null)
        {
            if (pcm.Length != _silkFrameSamples)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The frame must contain exactly {_silkFrameSamples} samples; got {pcm.Length}."),
                    nameof(pcm));
            }

            if (packet.Length < 2)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The packet buffer must hold at least 2 bytes; got {packet.Length}."),
                    nameof(packet));
            }

            packet[0] = _silkToc;
            return _silk.Encode(pcm, packet[1..]) + 1;
        }

        if (pcm.Length != _inputFrameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_inputFrameSamples} samples; got {pcm.Length}."),
                nameof(pcm));
        }

        int frameBytes = _encoder!.FrameBytes;
        int minimumPacket = (frameBytes * _subFrameCount) + (_subFrameCount == 1 ? 1 : 2);
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
            return _encoder.EncodeFrame(pcm, packet[1..]) + 1;
        }

        // 40 and 60 ms CELT are code 1/code 3 packets of 20 ms frames (RFC 6716 Appendix A,
        // opus_encoder.c: the "can't support higher than wideband for > 20 ms frames" block).
        Span<int> sizes = stackalloc int[_subFrameCount];
        int dataBytes = 0;
        for (int i = 0; i < _subFrameCount; i++)
        {
            int size = _encoder.EncodeFrame(
                pcm.Slice(i * _subFrameSamples, _subFrameSamples), _subFrameData.AsSpan(dataBytes));
            sizes[i] = size;
            dataBytes += size;
        }

        return OpusPacketWriter.Write(packet, BuildToc(), sizes, _subFrameData.AsSpan(0, dataBytes));
    }

    /// <summary>Encodes one frame of mono floating-point PCM in [-1, 1] into an Opus packet.</summary>
    /// <param name="pcm">Exactly one frame of normalized samples at the configured sample rate.</param>
    /// <param name="packet">Destination for the packet.</param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        if (pcm.Length != _conversionBuffer.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_conversionBuffer.Length} samples; got {pcm.Length}."),
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

    private byte BuildToc()
    {
        // CELT-only fullband configs are 28..31 (RFC 6716 §3.1, Table 2), one per frame duration.
        // A 40 or 60 ms input frame is signalled as 20 ms and split into several frames.
        int config = 28 + (int)_tocDuration;
        return (byte)(config << 3);
    }
}
