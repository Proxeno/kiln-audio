// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Encoder;

/// <summary>
/// Writes the main SILK layer of a SILK-only Opus frame into a shared range encoder.
/// </summary>
/// <remarks>
/// This is the seam the SILK-ENC lanes fill. The transition encoder does not depend on the
/// SILK implementation, so tests can substitute a stub that emits a decodable frame.
/// </remarks>
internal interface ISilkLayerEncoder
{
    /// <summary>Encodes one SILK-only frame's symbols.</summary>
    /// <param name="encoder">The frame's range encoder, positioned after no symbols.</param>
    /// <param name="pcm">Exactly <paramref name="frameSamples"/> samples at the encoder's API rate.</param>
    /// <param name="frameSamples">Samples per channel in the frame.</param>
    /// <param name="maxFrameBytes">The frame's byte capacity, excluding the TOC byte.</param>
    void Encode(ref RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes);
}

/// <summary>
/// Encodes a 5 ms redundant CELT frame for a mode transition (RFC 6716 §4.5.1.4).
/// </summary>
internal interface IRedundantCeltEncoder
{
    /// <summary>Encodes one 5 ms CELT frame into its own range coder.</summary>
    /// <param name="pcm">Exactly 240 samples at 48 kHz.</param>
    /// <param name="endBand">One past the last coded band, from the packet's bandwidth.</param>
    /// <param name="destination">
    /// The redundant frame's bytes. The implementation fills all of it, zero-padding any tail
    /// beyond the entropy coder's output.
    /// </param>
    void Encode(ReadOnlySpan<short> pcm, int endBand, Span<byte> destination);

    /// <summary>Gets the redundant frame's final range.</summary>
    uint FinalRange { get; }

    /// <summary>Gets the number of bytes one redundant frame occupies.</summary>
    int FrameBytes { get; }
}

/// <summary>
/// The real 5 ms redundant CELT encoder, backed by the CELT encoder of this library.
/// </summary>
/// <remarks>
/// The redundant frame starts at band 0 and stops at the packet's end band, so it is band-limited
/// the same way the main CELT layer is (RFC 6716 §4.5.1.4). The encoder's state is shared with the
/// main layer: the reference resets CELT around a transition, so the caller supplies the instance
/// whose state matches what the decoder will do.
/// </remarks>
internal sealed class CeltRedundantEncoder : IRedundantCeltEncoder
{
    private readonly CeltEncoder _encoder;
    private readonly bool _resetEachFrame;

    /// <summary>Creates an encoder for the given bitrate and complexity.</summary>
    /// <param name="bitrateBps">The frame's CBR target bitrate; the 5 ms frame size is bitrate/1600 bytes.</param>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    /// <param name="mode">The performance mode.</param>
    internal CeltRedundantEncoder(int bitrateBps, int complexity, OpusPerformanceMode mode = OpusPerformanceMode.Precise)
    {
        _encoder = new CeltEncoder(
            OpusFrameDuration.Ms5,
            bitrateBps,
            complexity,
            channels: 1,
            InputResampler.InternalSampleRate,
            useVbr: false,
            constrainedVbr: true,
            mode);
        _resetEachFrame = true;
    }

    /// <summary>Wraps an existing CELT encoder so the redundant frame shares the main layer's state.</summary>
    /// <param name="encoder">A 48 kHz, mono CELT encoder.</param>
    internal CeltRedundantEncoder(CeltEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _encoder = encoder;
        _resetEachFrame = false;
    }

    /// <summary>Gets the number of bytes the encoder emits for one 5 ms frame.</summary>
    public int FrameBytes => _encoder.FrameBytes;

    /// <inheritdoc />
    public uint FinalRange => _encoder.FinalRange;

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<short> pcm, int endBand, Span<byte> destination)
    {
        if (pcm.Length != _encoder.FrameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The redundant frame must hold {_encoder.FrameSamples} samples."),
                nameof(pcm));
        }

        int frameBytes = _encoder.FrameBytes;
        if (destination.Length < frameBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The destination must hold {frameBytes} bytes but holds {destination.Length}."),
                nameof(destination));
        }

        if (_resetEachFrame)
        {
            // A standalone redundant encoder keeps no history: every redundant frame starts from
            // the reset state, which is what the decoder uses before a SILK-to-CELT transition.
            _encoder.Reset();
        }

        int written = _encoder.EncodeFrame(pcm, destination, 0, endBand);
        destination[written..].Clear();
    }
}

/// <summary>
/// Encoder-side transition redundancy for SILK/CELT switches (RFC 6716 §4.5.1).
/// </summary>
/// <remarks>
/// <para>
/// The reference encodes the redundancy signalling in the same range coder as the SILK layer and
/// appends a complete 5 ms CELT frame, in its own range coder, at the end of the Opus frame. The
/// packet's final range is the main coder's range XOR the redundant coder's range
/// (<c>rangeFinal = enc.rng ^ redundant_rng</c>).
/// </para>
/// <para>
/// The redundant CELT encoder's cross-frame state belongs to the caller. The reference resets
/// CELT before a SILK-to-CELT redundant frame and resets it again after a CELT-to-SILK one; the
/// current CELT encoder has no reset API, so the Phase B wiring must supply a fresh encoder in
/// those cases.
/// </para>
/// </remarks>
internal static class OpusTransitionEncoder
{
    /// <summary>Samples in one 5 ms redundant frame at 48 kHz.</summary>
    internal const int RedundantFrameSamples = 240;

    /// <summary>Upper bound on a redundant frame's bytes (RFC 6716 §4.5.1.3).</summary>
    internal const int MaxRedundancyBytes = 257;

    /// <summary>Maps a packet bandwidth to the CELT end band (RFC 6716 §4.5.2).</summary>
    /// <param name="bandwidth">The packet's audio bandwidth.</param>
    /// <returns>One past the last coded band.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bandwidth"/> is unknown.</exception>
    internal static int EndBandOf(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.Narrowband => 13,
        OpusBandwidth.Mediumband or OpusBandwidth.Wideband => 17,
        OpusBandwidth.SuperWideband => 19,
        OpusBandwidth.Fullband => 21,
        _ => throw new ArgumentOutOfRangeException(nameof(bandwidth), bandwidth, "Unknown bandwidth."),
    };

    /// <summary>
    /// Writes the redundancy flag, position and byte count into the main range coder.
    /// </summary>
    /// <param name="encoder">The main range coder, positioned after the SILK layer.</param>
    /// <param name="mode">The frame's mode; CELT-only frames never carry redundancy.</param>
    /// <param name="redundancy">Whether the transition wants a redundant frame.</param>
    /// <param name="celtToSilk">Whether the transition is CELT to SILK.</param>
    /// <param name="nbComprBytes">The CELT layer's byte budget; used only in hybrid mode.</param>
    /// <param name="maxFrameBytes">The frame's capacity in bytes, excluding the TOC byte.</param>
    /// <param name="bitrateBps">The target bitrate.</param>
    /// <param name="redundancyBytes">Receives the number of redundant bytes.</param>
    /// <param name="desiredBytes">
    /// The redundant frame's fixed size, or 0 to derive it from <paramref name="bitrateBps"/>.
    /// </param>
    /// <returns>True when redundancy is signalled and <paramref name="redundancyBytes"/> is valid.</returns>
    internal static bool TryEncodeSignalling(
        ref RangeEncoder encoder,
        OpusMode mode,
        bool redundancy,
        bool celtToSilk,
        int nbComprBytes,
        int maxFrameBytes,
        int bitrateBps,
        out int redundancyBytes,
        int desiredBytes = 0)
    {
        redundancyBytes = 0;
        if (mode == OpusMode.Celt)
        {
            return false;
        }

        // Target the same rate for the redundancy as for the rest of the frame.
        int bytes = desiredBytes > 0
            ? desiredBytes
            : Math.Min(MaxRedundancyBytes, Math.Max(2, bitrateBps / 1600));
        bytes = Math.Min(MaxRedundancyBytes, Math.Max(2, bytes));

        int tell = encoder.Tell();
        int hybridBits = mode == OpusMode.Hybrid ? 20 : 0;
        if (tell + OpusRedundancyInfo.MinimumBits + hybridBits > 8 * maxFrameBytes)
        {
            return false;
        }

        if (mode == OpusMode.Hybrid)
        {
            // The presence flag is explicit, so availability must be decided before writing it. A requested
            // redundant frame that has no room is dropped, but the flag still has to say so whenever the decoder
            // will read it (it does when 37 bits fit in the frame).
            bool send = redundancy && maxFrameBytes - nbComprBytes - 1 >= bytes;
            if (send || tell + 37 <= 8 * nbComprBytes)
            {
                encoder.EncodeBitLogp(send ? 1 : 0, 12);
            }

            if (!send)
            {
                return false;
            }

            encoder.EncodeBitLogp(celtToSilk ? 1 : 0, 1);
            encoder.EncodeUInt((uint)(bytes - 2), 256);
            redundancyBytes = bytes;
            return true;
        }

        // SILK-only: presence is implicit, so check that the frame can hold the position bit and
        // the whole redundant frame before writing anything.
        if (!redundancy || maxFrameBytes - ((tell + 9 + 7) >> 3) < bytes)
        {
            return false;
        }

        encoder.EncodeBitLogp(celtToSilk ? 1 : 0, 1);
        redundancyBytes = bytes;
        return true;
    }

    /// <summary>
    /// Encodes one SILK-only Opus frame, optionally with a trailing redundant CELT frame.
    /// </summary>
    /// <param name="silkEncoder">Writes the SILK layer's symbols.</param>
    /// <param name="redundantCelt">Encodes the 5 ms redundant CELT frame.</param>
    /// <param name="pcm">Exactly <paramref name="frameSamples"/> samples at 48 kHz.</param>
    /// <param name="frameSamples">Samples per channel: 480, 960, 1920 or 2880.</param>
    /// <param name="bitrateBps">The target bitrate.</param>
    /// <param name="maxFrameBytes">The frame's capacity in bytes, excluding the TOC byte.</param>
    /// <param name="bandwidth">The packet bandwidth; must be SILK-only valid (at most wideband).</param>
    /// <param name="redundancy">Whether the transition wants a redundant frame.</param>
    /// <param name="celtToSilk">Whether the transition is CELT to SILK.</param>
    /// <param name="frameData">Destination for the frame payload (without the TOC byte).</param>
    /// <param name="finalRange">Receives the decoder's expected final range.</param>
    /// <returns>The number of bytes written to <paramref name="frameData"/>.</returns>
    /// <exception cref="ArgumentException">A span has the wrong length or is too small.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A scalar argument is out of range.</exception>
    /// <exception cref="InvalidOperationException">The SILK layer overflowed the frame budget.</exception>
    internal static int EncodeSilkOnlyFrame(
        ISilkLayerEncoder silkEncoder,
        IRedundantCeltEncoder redundantCelt,
        ReadOnlySpan<short> pcm,
        int frameSamples,
        int bitrateBps,
        int maxFrameBytes,
        OpusBandwidth bandwidth,
        bool redundancy,
        bool celtToSilk,
        Span<byte> frameData,
        out uint finalRange) =>
        EncodeSilkOnlyFrame(
            silkEncoder, redundantCelt, pcm, frameSamples, bitrateBps, maxFrameBytes, bandwidth, redundancy,
            celtToSilk, maxFrameBytes, frameData, out finalRange);

    /// <summary>
    /// <see cref="EncodeSilkOnlyFrame(ISilkLayerEncoder, IRedundantCeltEncoder, ReadOnlySpan{short}, int, int, int, OpusBandwidth, bool, bool, Span{byte}, out uint)"/>
    /// with a separate limit for what the redundant frame may be appended to: in CBR the packet, redundant
    /// frame included, must stay within the nominal size even though the SILK layer's own capacity is larger.
    /// </summary>
    /// <param name="silkEncoder">Writes the SILK layer's symbols.</param>
    /// <param name="redundantCelt">Encodes the redundant CELT frame.</param>
    /// <param name="pcm">One frame of mono input.</param>
    /// <param name="frameSamples">The frame length in samples.</param>
    /// <param name="bitrateBps">The target bitrate.</param>
    /// <param name="maxFrameBytes">The frame capacity in bytes, excluding the TOC byte.</param>
    /// <param name="bandwidth">The SILK bandwidth.</param>
    /// <param name="redundancy">Whether the transition wants a redundant frame.</param>
    /// <param name="celtToSilk">Whether the transition is CELT to SILK.</param>
    /// <param name="redundancyLimitBytes">The size the main frame plus the redundant frame must not exceed.</param>
    /// <param name="frameData">Destination for the payload.</param>
    /// <param name="finalRange">Receives the decoder's expected final range.</param>
    /// <returns>The number of payload bytes written.</returns>
    internal static int EncodeSilkOnlyFrame(
        ISilkLayerEncoder silkEncoder,
        IRedundantCeltEncoder redundantCelt,
        ReadOnlySpan<short> pcm,
        int frameSamples,
        int bitrateBps,
        int maxFrameBytes,
        OpusBandwidth bandwidth,
        bool redundancy,
        bool celtToSilk,
        int redundancyLimitBytes,
        Span<byte> frameData,
        out uint finalRange)
    {
        ArgumentNullException.ThrowIfNull(silkEncoder);
        ArgumentNullException.ThrowIfNull(redundantCelt);
        if (frameSamples is not (480 or 960 or 1920 or 2880))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameSamples), frameSamples, "SILK frames are 10, 20, 40 or 60 ms at 48 kHz.");
        }

        if (pcm.Length != frameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The frame must hold {frameSamples} samples; got {pcm.Length}."),
                nameof(pcm));
        }

        if (bitrateBps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateBps), bitrateBps, "Bitrate must be positive.");
        }

        if (maxFrameBytes <= 0 || frameData.Length < maxFrameBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The destination must hold at least {maxFrameBytes} bytes but holds {frameData.Length}."),
                nameof(frameData));
        }

        if ((int)bandwidth > (int)OpusBandwidth.Wideband)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bandwidth), bandwidth, "A SILK-only frame cannot be wider than wideband.");
        }

        var encoder = new RangeEncoder(frameData[..maxFrameBytes]);
        silkEncoder.Encode(ref encoder, pcm, frameSamples, maxFrameBytes);
        if (encoder.Error)
        {
            throw new InvalidOperationException("The SILK layer overflowed the frame budget.");
        }

        bool hasRedundancy = TryEncodeSignalling(
            ref encoder, OpusMode.Silk, redundancy, celtToSilk, 0, Math.Min(maxFrameBytes, redundancyLimitBytes), bitrateBps,
            out int redundancyBytes, redundantCelt.FrameBytes);
        if (encoder.Error)
        {
            throw new InvalidOperationException("The redundancy signalling overflowed the frame budget.");
        }

        // The decoder locates the redundant frame as everything past ceil(tell/8) (RFC 6716 §4.5.1.3).
        int mainBytes = (encoder.Tell() + 7) >> 3;
        finalRange = encoder.Rng;
        encoder.Done();
        if (encoder.Error)
        {
            throw new InvalidOperationException("The SILK layer did not finalize.");
        }

        if (!hasRedundancy)
        {
            // A SILK-only frame may drop trailing zero bytes (RFC 6716 Appendix A, opus_encoder.c),
            // which the reference's range decoder refills.
            return encoder.RangeBytes;
        }

        int written = encoder.RangeBytes;
        if (written > mainBytes || mainBytes + redundancyBytes > Math.Min(maxFrameBytes, redundancyLimitBytes))
        {
            throw new InvalidOperationException("The redundant frame does not fit in the frame budget.");
        }

        if (mainBytes > written)
        {
            // The main part is the announced ceil(tell/8) bytes; keep the range decoder's padding
            // zeros deterministic before the redundant frame starts.
            frameData.Slice(written, mainBytes - written).Clear();
        }

        ReadOnlySpan<short> fiveMilliseconds = celtToSilk
            ? pcm[..RedundantFrameSamples]
            : pcm[(frameSamples - RedundantFrameSamples)..];
        redundantCelt.Encode(fiveMilliseconds, EndBandOf(bandwidth), frameData.Slice(mainBytes, redundancyBytes));
        finalRange ^= redundantCelt.FinalRange;
        return mainBytes + redundancyBytes;
    }
}
