// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Silk.Encoder;

namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) encoder.</summary>
/// <remarks>
/// <para>
/// Two configurations are implemented. <see cref="OpusApplication.RestrictedLowDelay"/> selects
/// the CELT-only restricted-low-delay encoder (48 kHz, mono, CBR, 2.5/5/10/20 ms).
/// <see cref="OpusApplication.Voip"/> selects the SILK-only encoder (mono, 10/20/40/60 ms,
/// narrowband, mediumband or wideband). Hybrid mode, stereo, in-band FEC, DTX and automatic mode
/// switching are not implemented and are rejected with <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/> writes one complete Opus packet: the TOC
/// byte followed by one frame. It allocates nothing after construction.
/// </para>
/// </remarks>
public sealed class OpusEncoder : IDisposable
{
    private readonly CeltEncoder? _celt;
    private readonly SilkEncoderCore? _silk;
    private readonly OpusFrameDuration _frameDuration;
    private readonly int _frameSamples;
    private readonly byte _silkToc;
    private readonly short[] _conversionBuffer;
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
        _frameDuration = options.FrameDuration;

        if (options.Application == OpusApplication.RestrictedLowDelay)
        {
            if (options.SampleRate != 48000)
            {
                throw new NotSupportedException(
                    FormattableString.Invariant(
                        $"Only 48 kHz input is supported by this CELT encoder; got {options.SampleRate} Hz."));
            }

            if (options.FrameDuration is OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60)
            {
                throw new NotSupportedException(
                    FormattableString.Invariant(
                        $"CELT supports 2.5, 5, 10 and 20 ms frames; got {options.FrameDuration}."));
            }

            RejectFecAndDtx(options);
            _celt = new CeltEncoder(options.FrameDuration, options.BitrateBps, options.Complexity);
            _frameSamples = _celt.FrameSamples;
            _conversionBuffer = new short[_frameSamples];
            return;
        }

        if (options.Application != OpusApplication.Voip)
        {
            throw new NotSupportedException(
                "Only OpusApplication.Voip (SILK) and OpusApplication.RestrictedLowDelay (CELT) are supported.");
        }

        RejectFecAndDtx(options);
        if (options.FrameDuration is OpusFrameDuration.Ms2_5 or OpusFrameDuration.Ms5)
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"SILK supports 10, 20, 40 and 60 ms frames; got {options.FrameDuration}."));
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
        _frameSamples = options.SampleRate * frameDurationMs / 1000;
        _conversionBuffer = new short[_frameSamples];

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
        _silkToc = (byte)((bandwidthIndex * 4 + durationIndex) << 3);
    }

    /// <summary>The validated options this encoder was created with.</summary>
    public OpusEncoderOptions Options { get; }

    /// <summary>
    /// Gets the range-coder final value of the most recently encoded frame. For a packet stream,
    /// this is the value a conformant decoder must report after decoding the packet.
    /// </summary>
    public uint FinalRange => _celt?.FinalRange ?? _silk?.FinalRange ?? 0;

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
        if (pcm.Length != _frameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_frameSamples} samples; got {pcm.Length}."),
                nameof(pcm));
        }

        if (_celt is not null)
        {
            int frameBytes = _celt.FrameBytes;
            if (packet.Length < frameBytes + 1)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The packet buffer must hold at least {frameBytes + 1} bytes; got {packet.Length}."),
                    nameof(packet));
            }

            packet[0] = BuildCeltToc();
            int written = _celt.EncodeFrame(pcm, packet[1..]);
            return written + 1;
        }

        if (packet.Length < 2)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The packet buffer must hold at least 2 bytes; got {packet.Length}."),
                nameof(packet));
        }

        packet[0] = _silkToc;
        int payload = _silk!.Encode(pcm, packet[1..]);
        return payload + 1;
    }

    /// <summary>Encodes one frame of mono floating-point PCM in [-1, 1] into an Opus packet.</summary>
    /// <param name="pcm">Exactly one frame of normalized samples at the configured sample rate.</param>
    /// <param name="packet">Destination for the packet.</param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        if (pcm.Length != _frameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {_frameSamples} samples; got {pcm.Length}."),
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

    private static void RejectFecAndDtx(OpusEncoderOptions options)
    {
        if (options.UseInbandFec)
        {
            throw new NotSupportedException("In-band forward error correction (LBRR) is not implemented.");
        }

        if (options.UseDtx)
        {
            throw new NotSupportedException("Discontinuous transmission is not implemented.");
        }
    }

    private byte BuildCeltToc()
    {
        // CELT-only fullband configs are 28..31 (RFC 6716 §3.1, Table 2), one per frame duration.
        int config = 28 + (int)_frameDuration;
        return (byte)(config << 3);
    }
}
