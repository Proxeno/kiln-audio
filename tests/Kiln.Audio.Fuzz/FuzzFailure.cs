using System.Security.Cryptography;

namespace Kiln.Audio.Fuzz;

/// <summary>One decoder operation in a replayable sequence.</summary>
/// <param name="Packet">The packet bytes, or an empty array for a lost packet.</param>
/// <param name="SampleRate">API sample rate.</param>
/// <param name="Channels">API channel count.</param>
/// <param name="DecodeFec">Whether in-band FEC was requested.</param>
/// <param name="UseFloat">Whether the float overload was used.</param>
/// <param name="Loss">True for a <c>DecodeLoss</c> call, false for a <c>Decode</c> call.</param>
internal sealed record FuzzStep(
    byte[] Packet,
    int SampleRate,
    int Channels,
    bool DecodeFec,
    bool UseFloat,
    bool Loss);

/// <summary>Identifies which entry point a finding came from.</summary>
internal enum FuzzTargetKind
{
    /// <summary><c>OpusPacket.TryParse</c> and its framing rules.</summary>
    Parser,

    /// <summary><c>OpusDecoder.Decode</c> and <c>OpusDecoder.DecodeLoss</c>.</summary>
    Decoder,

    /// <summary><c>OpusEncoder</c> construction and <c>Encode</c>.</summary>
    Encoder,
}

/// <summary>
/// A violation of the resilience contract: an undocumented exception, a value out of its
/// documented range, non-finite PCM, or a per-packet time over the bound.
/// </summary>
internal sealed class FuzzFailureException : Exception
{
    /// <summary>Creates a failure for <paramref name="target"/>.</summary>
    /// <param name="target">The entry point that failed.</param>
    /// <param name="packet">The input that failed; may be empty.</param>
    /// <param name="message">What the contract check observed.</param>
    /// <param name="inner">The unexpected exception, when one was caught.</param>
    internal FuzzFailureException(FuzzTargetKind target, byte[] packet, string message, Exception? inner = null)
        : base(message, inner)
    {
        Target = target;
        Packet = packet;
    }

    /// <summary>Gets the entry point that failed.</summary>
    internal FuzzTargetKind Target { get; }

    /// <summary>Gets the input that failed.</summary>
    internal byte[] Packet { get; }

    /// <summary>Gets or sets the decoder sample rate, when the target is the decoder.</summary>
    internal int SampleRate { get; init; }

    /// <summary>Gets or sets the decoder channel count, when the target is the decoder.</summary>
    internal int Channels { get; init; }

    /// <summary>Gets or sets whether in-band FEC decoding was requested.</summary>
    internal bool DecodeFec { get; init; }

    /// <summary>Gets or sets whether the float PCM overload was used.</summary>
    internal bool UseFloat { get; init; }

    /// <summary>
    /// Gets or sets the decoder operations that preceded the failure on the same decoder, oldest
    /// first, ending with the failing operation. Null when the failure reproduces from a fresh
    /// decoder and needs no history.
    /// </summary>
    internal IReadOnlyList<FuzzStep>? Sequence { get; init; }
}

/// <summary>One saved finding, with enough context to replay it.</summary>
/// <param name="Target">The failing entry point.</param>
/// <param name="Packet">The failing input, minimized when minimization succeeded.</param>
/// <param name="Message">The contract violation.</param>
/// <param name="Execution">The execution index within the session.</param>
/// <param name="SampleRate">Decoder sample rate, or 0.</param>
/// <param name="Channels">Decoder channel count, or 0.</param>
/// <param name="DecodeFec">Whether FEC decoding was requested.</param>
/// <param name="UseFloat">Whether the float PCM overload was used.</param>
/// <param name="Sequence">The minimized decoder sequence, or null for a single input.</param>
internal sealed record FuzzFinding(
    FuzzTargetKind Target,
    byte[] Packet,
    string Message,
    long Execution,
    int SampleRate,
    int Channels,
    bool DecodeFec,
    bool UseFloat,
    IReadOnlyList<FuzzStep>? Sequence = null)
{
    /// <summary>Gets a short content hash used in the saved file name.</summary>
    internal string Hash => Convert.ToHexString(SHA256.HashData(Packet))[..12].ToLowerInvariant();

    /// <summary>Gets the input as lowercase hex, for test messages.</summary>
    internal string Hex => Convert.ToHexString(Packet).ToLowerInvariant();
}
