namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) decoder. Skeleton: the codec is not implemented yet.</summary>
public sealed class OpusDecoder : IDisposable
{
    private bool _disposed;

    /// <summary>Creates a decoder after validating <paramref name="options"/>.</summary>
    /// <param name="options">Decoder configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is illegal.</exception>
    public OpusDecoder(OpusDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    /// <summary>The validated options this decoder was created with.</summary>
    public OpusDecoderOptions Options { get; }

    /// <summary>Decodes one Opus packet to interleaved 16-bit PCM.</summary>
    /// <param name="packet">The Opus packet.</param>
    /// <param name="pcm">Destination for interleaved PCM.</param>
    /// <param name="decodeFec">Decode the in-band FEC data of this packet to recover the previous (lost) frame.</param>
    /// <returns>Number of samples per channel written to <paramref name="pcm"/>.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm, bool decodeFec = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("Decoder not implemented yet — see EWI-2742");
    }

    /// <summary>Synthesises concealment audio for a lost packet (packet loss concealment).</summary>
    /// <param name="pcm">Destination for interleaved PCM.</param>
    /// <returns>Number of samples per channel written to <paramref name="pcm"/>.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public int DecodeLoss(Span<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("Packet loss concealment not implemented yet — see EWI-2742");
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
