namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) encoder. Skeleton: the codec is not implemented yet.</summary>
public sealed class OpusEncoder : IDisposable
{
    private bool _disposed;

    /// <summary>Creates an encoder after validating <paramref name="options"/>.</summary>
    /// <param name="options">Encoder configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is illegal.</exception>
    public OpusEncoder(OpusEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    /// <summary>The validated options this encoder was created with.</summary>
    public OpusEncoderOptions Options { get; }

    /// <summary>Encodes one frame of interleaved 16-bit PCM into an Opus packet.</summary>
    /// <param name="pcm">Interleaved PCM for exactly one frame.</param>
    /// <param name="packet">Destination for the packet.</param>
    /// <returns>Number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotImplementedException("Encoder not implemented yet — see EWI-2743");
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
