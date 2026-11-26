// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>The per-frame result of the DTX state machine.</summary>
/// <param name="SignalType">The signal type the frame is coded as.</param>
/// <param name="VadFlag">The frame's VAD bit (RFC 6716 §4.2.3).</param>
/// <param name="InDtx">Whether the frame's payload is suppressed.</param>
internal readonly record struct SilkDtxDecision(SilkSignalType SignalType, bool VadFlag, bool InDtx);

/// <summary>
/// Voice-activity-driven discontinuous transmission for one SILK channel
/// (RFC 6716 §2.1.9; Appendix A <c>silk_encode_do_VAD_FIX</c> and <c>silk_Encode</c>).
/// </summary>
/// <remarks>
/// <para>
/// The encoder never stops running the SILK analysis during silence: it keeps refreshing the
/// decoder's comfort-noise model by sending an inactive frame, then suppresses the payload for
/// up to 400 ms. The first 200 ms of silence are coded normally so the decoder's concealment and
/// comfort-noise state settle, and a refresh frame is sent before the suppression can run longer
/// than 400 ms. The state machine below is the reference's, with the speech activity supplied by
/// the VAD.
/// </para>
/// <para>
/// When every channel suppresses its payload the Opus packet carries no payload at all, which is
/// the DTX packet: a single TOC byte (RFC 6716 §3.2.1). <see cref="WriteDtxPacket"/> emits it.
/// </para>
/// </remarks>
internal sealed class SilkDtxEncoder
{
    /// <summary>The number of silent frames coded before DTX starts (200 ms).</summary>
    internal const int SpeechFramesBeforeDtx = 10;

    /// <summary>The longest DTX run, after which a refresh frame is sent (400 ms).</summary>
    internal const int MaxConsecutiveDtx = 20;

    /// <summary>The speech activity below which a frame is inactive, Q8 (0.05).</summary>
    internal const int ActivityThresholdQ8 = 13;

    /// <summary>The length of a DTX packet: the TOC byte and an empty payload (RFC 6716 §3.2.1).</summary>
    internal const int DtxPacketLength = 1;

    /// <summary>Gets or sets whether DTX is requested for the current packet.</summary>
    public bool UseDtx { get; set; }

    /// <summary>Gets the number of consecutive silent frames seen so far.</summary>
    public int NoSpeechCounter { get; private set; }

    /// <summary>Gets whether the current frame's payload is suppressed.</summary>
    public bool InDtx { get; private set; }

    /// <summary>Returns the channel to its initial state.</summary>
    public void Reset()
    {
        NoSpeechCounter = 0;
        InDtx = false;
    }

    /// <summary>
    /// Starts a packet, loading the current DTX request into the running state
    /// (Appendix A <c>silk_Encode</c>: <c>inDTX = useDTX</c>).
    /// </summary>
    public void BeginPacket() => InDtx = UseDtx;

    /// <summary>
    /// Classifies one frame from its speech activity and advances the DTX counters
    /// (Appendix A <c>silk_encode_do_VAD_FIX</c>).
    /// </summary>
    /// <param name="speechActivityQ8">The VAD's speech activity, Q8 (0 to 256).</param>
    /// <returns>The frame's signal type, VAD bit and suppression state.</returns>
    public SilkDtxDecision ProcessFrame(int speechActivityQ8)
    {
        if (speechActivityQ8 < ActivityThresholdQ8)
        {
            NoSpeechCounter++;
            if (NoSpeechCounter < SpeechFramesBeforeDtx)
            {
                InDtx = false;
            }
            else if (NoSpeechCounter > MaxConsecutiveDtx + SpeechFramesBeforeDtx)
            {
                // Send a refresh frame and restart the DTX run.
                NoSpeechCounter = SpeechFramesBeforeDtx;
                InDtx = false;
            }

            return new SilkDtxDecision(SilkSignalType.Inactive, VadFlag: false, InDtx);
        }

        NoSpeechCounter = 0;
        InDtx = false;
        return new SilkDtxDecision(SilkSignalType.Unvoiced, VadFlag: true, InDtx: false);
    }

    /// <summary>
    /// Returns whether a packet's payload is dropped because every channel is in DTX
    /// (Appendix A <c>silk_Encode</c>).
    /// </summary>
    /// <param name="mid">The mid (or mono) channel's state machine.</param>
    /// <param name="side">The side channel's state machine, or <see langword="null"/> for mono.</param>
    public static bool SuppressPacket(SilkDtxEncoder mid, SilkDtxEncoder? side)
    {
        ArgumentNullException.ThrowIfNull(mid);
        return mid.InDtx && (side is null || side.InDtx);
    }

    /// <summary>Writes the 1-byte DTX packet: the TOC byte followed by an empty payload.</summary>
    /// <param name="destination">The packet buffer, at least one byte.</param>
    /// <param name="toc">The Opus TOC byte for the frame.</param>
    /// <returns>The number of bytes written, always <see cref="DtxPacketLength"/>.</returns>
    public static int WriteDtxPacket(Span<byte> destination, byte toc)
    {
        if (destination.Length < DtxPacketLength)
        {
            throw new ArgumentException("The packet buffer needs at least one byte.", nameof(destination));
        }

        destination[0] = toc;
        return DtxPacketLength;
    }
}
