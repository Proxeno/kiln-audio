namespace Kiln.Audio.Silk;

/// <summary>
/// The transition redundancy that follows the SILK layer of a SILK-only Opus frame
/// (RFC 6716 §4.5.1).
/// </summary>
/// <remarks>
/// The redundant frame is a 5 ms CELT frame in its own range coder at the end of the Opus
/// frame. This type carries only what is needed to locate it; decoding it belongs to the
/// CELT layer. The top-level decoder checks a final range as the main coder's range XOR the
/// redundant coder's range, so a frame with redundancy cannot be checked until that layer
/// exists.
/// </remarks>
internal readonly struct OpusRedundancyInfo
{
    /// <summary>
    /// Minimum number of bits that must remain after the SILK layer for a SILK-only frame to
    /// carry redundancy; the check itself consumes none of them (RFC 6716 §4.5.1.1).
    /// </summary>
    internal const int MinimumBits = 17;

    private OpusRedundancyInfo(bool present, bool celtToSilk, int bytes)
    {
        Present = present;
        CeltToSilk = celtToSilk;
        Bytes = bytes;
    }

    /// <summary>Gets a value indicating whether the frame carries a redundant CELT frame.</summary>
    internal bool Present { get; }

    /// <summary>
    /// Gets the redundancy position flag: true when the redundant frame belongs at the
    /// beginning (a CELT-to-SILK transition), false when it belongs at the end
    /// (RFC 6716 §4.5.1.2).
    /// </summary>
    internal bool CeltToSilk { get; }

    /// <summary>
    /// Gets the number of whole bytes in the redundant CELT frame, at least 2
    /// (RFC 6716 §4.5.1.3).
    /// </summary>
    internal int Bytes { get; }

    /// <summary>Gets the value for a frame with no redundancy.</summary>
    internal static OpusRedundancyInfo None => default;

    internal static OpusRedundancyInfo Create(bool celtToSilk, int bytes) => new(true, celtToSilk, bytes);
}
