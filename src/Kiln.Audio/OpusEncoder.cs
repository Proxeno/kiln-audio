// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;

namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) encoder for the CELT-only case.</summary>
/// <remarks>
/// <para>
/// This version supports the CELT family: mono or stereo, 8, 12, 16, 24 or 48 kHz input, 2.5, 5,
/// 10 or 20 ms frames, constant or variable bitrate, and the pitch pre-filter. All three
/// <see cref="OpusApplication"/> values currently map onto the same CELT settings. SILK, hybrid,
/// in-band FEC and DTX are not implemented and are rejected with
/// <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// <see cref="Encode(ReadOnlySpan{short}, Span{byte})"/> writes one complete Opus packet: the TOC
/// byte followed by one CELT frame. It allocates nothing after construction.
/// </para>
/// </remarks>
public sealed class OpusEncoder : IDisposable
{
    private readonly CeltEncoder _encoder;
    private readonly OpusFrameDuration _frameDuration;
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

        if (options.FrameDuration is OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60)
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"CELT supports 2.5, 5, 10 and 20 ms frames; got {options.FrameDuration}."));
        }

        if (options.UseInbandFec)
        {
            throw new NotSupportedException("In-band forward error correction (LBRR) is not implemented.");
        }

        if (options.UseDtx)
        {
            throw new NotSupportedException("Discontinuous transmission is not implemented.");
        }

        Options = options;
        _frameDuration = options.FrameDuration;
        _encoder = new CeltEncoder(
            options.FrameDuration,
            options.BitrateBps,
            options.Complexity,
            options.Channels,
            options.SampleRate,
            options.UseVbr,
            options.ConstrainedVbr);
        _conversionBuffer = new short[_encoder.FrameSamples * options.Channels];
    }

    /// <summary>The validated options this encoder was created with.</summary>
    public OpusEncoderOptions Options { get; }

    /// <summary>
    /// Gets the range-coder final value of the most recently encoded frame. For a packet stream,
    /// this is the value a conformant decoder must report after decoding the packet.
    /// </summary>
    public uint FinalRange => _encoder.FinalRange;

    /// <summary>Encodes one frame of interleaved 16-bit PCM into an Opus packet.</summary>
    /// <param name="pcm">
    /// Exactly one frame of interleaved 16-bit PCM at <see cref="OpusEncoderOptions.SampleRate"/>.
    /// </param>
    /// <param name="packet">
    /// Destination for the packet; must hold the TOC byte and the frame. Constant bitrate needs
    /// the configured frame size plus one byte; variable bitrate may use as much of the buffer as
    /// is available (up to 1276 bytes).
    /// </param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="pcm"/> is not exactly one frame, or <paramref name="packet"/> is too small.
    /// </exception>
    public int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int expected = _encoder.FrameSamples * _encoder.Channels;
        if (pcm.Length != expected)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {expected} interleaved samples; got {pcm.Length}."),
                nameof(pcm));
        }

        if (Options.UseVbr)
        {
            if (packet.Length < 3)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The packet buffer must hold at least 3 bytes; got {packet.Length}."),
                    nameof(packet));
            }
        }
        else
        {
            int frameBytes = _encoder.FrameBytes;
            if (packet.Length < frameBytes + 1)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The packet buffer must hold at least {frameBytes + 1} bytes; got {packet.Length}."),
                    nameof(packet));
            }
        }

        packet[0] = BuildToc();
        int written = _encoder.EncodeFrame(pcm, packet.Slice(1));
        return written + 1;
    }

    /// <summary>Encodes one frame of interleaved floating-point PCM in [-1, 1] into an Opus packet.</summary>
    /// <param name="pcm">
    /// Exactly one frame of interleaved normalized samples at <see cref="OpusEncoderOptions.SampleRate"/>.
    /// </param>
    /// <param name="packet">Destination for the packet.</param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        int expected = _encoder.FrameSamples * _encoder.Channels;
        if (pcm.Length != expected)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain exactly {expected} interleaved samples; got {pcm.Length}."),
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
        // CELT-only configurations 16..31 (RFC 6716 §3.1, Table 2): the bandwidth group depends on
        // the API rate and the low two bits select the frame duration. Bit 2 is the stereo flag.
        int config = InputResampler.TocConfigBase(Options.SampleRate) + (int)_frameDuration;
        int toc = config << 3;
        if (Options.Channels == 2)
        {
            toc |= 0x04;
        }

        return (byte)toc;
    }
}