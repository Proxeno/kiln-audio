// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk.Encoder;

namespace Kiln.Audio.Encoder;

/// <summary>
/// Encodes a hybrid frame: a SILK wideband layer at 16 kHz followed by the CELT layer from band
/// 17 in the same range coder (RFC 6716 §4.5.2; Appendix A <c>opus_encoder.c</c>, the
/// <c>MODE_HYBRID</c> branch).
/// </summary>
/// <remarks>
/// <para>
/// The bit budget follows the reference: SILK may use at most 90% of the frame's capacity, the
/// CELT layer gets <c>len + bytes_target - silk_bytes</c> in variable-bitrate mode and
/// <c>max(len, bytes_target)</c> in constant-bitrate mode, where <c>len</c> is the SILK layer's
/// whole bytes (plus three when a redundant frame is signalled).
/// </para>
/// <para>
/// The CELT layer is always constant bitrate here, as the reference forces for hybrid frames.
/// Nothing is allocated per frame.
/// </para>
/// </remarks>
internal static class OpusHybridEncoder
{
    /// <summary>First coded CELT band of a hybrid frame (RFC 6716 §4.5.2).</summary>
    internal const int StartBand = 17;

    /// <summary>
    /// Encodes one hybrid Opus frame payload (without the TOC byte).
    /// </summary>
    /// <param name="silk">The SILK encoder; must be configured for 16 kHz wideband.</param>
    /// <param name="celt">The CELT encoder, configured for the frame duration, mono, 48 kHz, CBR.</param>
    /// <param name="redundantCelt">Encodes the optional 5 ms redundant CELT frame.</param>
    /// <param name="pcm">Exactly <paramref name="frameSamples"/> mono samples at <paramref name="apiRate"/>.</param>
    /// <param name="apiRate">The input sample rate in Hz.</param>
    /// <param name="frameSamples">Samples per channel in one Opus frame at <paramref name="apiRate"/>.</param>
    /// <param name="bitrateBps">The target bitrate.</param>
    /// <param name="maxFrameBytes">The frame capacity in bytes, excluding the TOC byte.</param>
    /// <param name="endBand">One past the last coded band: 19 for super-wideband, 21 for fullband.</param>
    /// <param name="redundancy">Whether the transition wants a redundant frame.</param>
    /// <param name="celtToSilk">Whether the transition is CELT to SILK.</param>
    /// <param name="constantBitrate">
    /// Whether the packet must not outgrow the nominal size: the frame is then bounded by its CBR size, so a
    /// redundant frame that would have to be appended past it is not sent.
    /// </param>
    /// <param name="frameData">Destination for the payload.</param>
    /// <param name="finalRange">Receives the decoder's expected final range.</param>
    /// <returns>The number of payload bytes written.</returns>
    internal static int Encode(
        ISilkLayerEncoder silk,
        CeltEncoder celt,
        IRedundantCeltEncoder redundantCelt,
        ReadOnlySpan<short> pcm,
        int apiRate,
        int frameSamples,
        int bitrateBps,
        int maxFrameBytes,
        int endBand,
        bool redundancy,
        bool celtToSilk,
        bool constantBitrate,
        Span<byte> frameData,
        out uint finalRange)
    {
        ArgumentNullException.ThrowIfNull(silk);
        ArgumentNullException.ThrowIfNull(celt);
        ArgumentNullException.ThrowIfNull(redundantCelt);
        if (pcm.Length != frameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The frame must hold {frameSamples} samples; got {pcm.Length}."),
                nameof(pcm));
        }

        if (maxFrameBytes < 4 || frameData.Length < maxFrameBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The destination must hold {maxFrameBytes} bytes."),
                nameof(frameData));
        }

        if (endBand is not (19 or 21))
        {
            throw new ArgumentOutOfRangeException(
                nameof(endBand), endBand, "A hybrid frame codes super-wideband (19) or fullband (21).");
        }

        int frameRate = apiRate / frameSamples;
        var enc = new RangeEncoder(frameData[..maxFrameBytes]);

        // SILK: at most 90% of the frame, less when a redundant frame must share the budget.
        int silkMaxDataBytes = 1 + (maxFrameBytes * 9 / 10);
        if (redundancy)
        {
            int framesPerFiveMilliseconds = frameSamples / (apiRate / 200);
            silkMaxDataBytes -= silkMaxDataBytes / (1 + Math.Max(1, framesPerFiveMilliseconds));
        }

        // bytes_target is the equivalent CBR size minus the TOC byte (reference line 817).
        int bytesTarget = Math.Min(maxFrameBytes + 1, (int)((long)bitrateBps * frameSamples / (apiRate * 8))) - 1;
        if (bytesTarget < 0)
        {
            bytesTarget = 0;
        }

        // In CBR the SILK layer sizes its rate loop to the capacity it is handed and writes its flag bits on top,
        // so it is held one byte under the CBR payload to keep the frame (and the packet) at the nominal size.
        int silkCapacity = constantBitrate && bytesTarget > 1
            ? Math.Min(silkMaxDataBytes - 1, bytesTarget - 1)
            : silkMaxDataBytes - 1;
        silk.Encode(ref enc, pcm, frameSamples, silkCapacity);
        int silkBits = enc.Tell();
        int silkBytes = (silkBits + 7) >> 3;

        // The reference reserves 3 bytes for the redundancy signalling; in CBR the frame is capped at its nominal
        // size and a redundant frame that would not fit is dropped, so nothing is reserved for it there.
        bool cbrBounded = constantBitrate && bytesTarget > 0;
        int len = silkBytes + (redundancy && !cbrBounded ? 3 : 0);

        int nbComprBytes;
        if (bytesTarget == 0)
        {
            nbComprBytes = len;
        }
        else
        {
            // Constant bitrate, as the reference forces for hybrid frames.
            nbComprBytes = Math.Max(len, bytesTarget);
        }

        nbComprBytes = Math.Max(2, Math.Min(maxFrameBytes, nbComprBytes));

        // In CBR the packet, redundant frame included, must not outgrow the nominal size, so the frame is
        // bounded by its CBR size here and a redundant frame that would not fit inside it is not sent.
        int budget = cbrBounded ? Math.Min(maxFrameBytes, nbComprBytes) : maxFrameBytes;

        // In CBR the main payload normally fills the whole nominal budget, which leaves the signalling
        // helper no room for a redundant frame. Offer it the smaller main payload that would make room
        // when at least the 37 bits the hybrid flag needs still fit after SILK.
        int signallingBytes = nbComprBytes;
        if (cbrBounded && redundancy)
        {
            int prospectiveBytes = budget - redundantCelt.FrameBytes - 1;
            if (enc.Tell() + 37 <= 8 * prospectiveBytes)
            {
                signallingBytes = prospectiveBytes;
            }
        }

        bool hasRedundancy = OpusTransitionEncoder.TryEncodeSignalling(
            ref enc, OpusMode.Hybrid, redundancy, celtToSilk, signallingBytes, budget, bitrateBps,
            out int redundancyBytes, redundantCelt.FrameBytes);
        if (hasRedundancy)
        {
            // Keep the reduced main payload that made room for the redundant frame.
            nbComprBytes = Math.Min(budget - redundancyBytes, signallingBytes);
        }

        nbComprBytes = Math.Max(2, Math.Min(maxFrameBytes, nbComprBytes));
        if (enc.Error || enc.Tell() > 8 * nbComprBytes)
        {
            throw new InvalidOperationException("The SILK layer overflowed the hybrid frame budget.");
        }

        // RFC 6716 Appendix A (opus_encoder.c): the coder is shrunk to the frame size before the
        // CELT layer runs, so finalization cannot spill past the announced length.
        enc.Shrink(nbComprBytes);

        int celtBytes = celt.EncodeFrameInto(ref enc, pcm, StartBand, endBand, nbComprBytes, frameData[..maxFrameBytes]);
        if (celtBytes != nbComprBytes)
        {
            throw new InvalidOperationException("The CELT layer did not fill its hybrid budget.");
        }

        int mainBytes = nbComprBytes;
        finalRange = enc.Rng;
        enc.Done();
        if (enc.Error || enc.RangeBytes > mainBytes)
        {
            throw new InvalidOperationException("The hybrid frame overflowed its budget.");
        }

        if (!hasRedundancy)
        {
            return mainBytes;
        }

        finalRange ^= EncodeRedundant(
            redundantCelt, pcm, apiRate, endBand, celtToSilk, frameData.Slice(mainBytes, redundancyBytes));
        return mainBytes + redundancyBytes;
    }

    /// <summary>Encodes the 5 ms redundant CELT frame and returns its final range.</summary>
    private static uint EncodeRedundant(
        IRedundantCeltEncoder redundantCelt,
        ReadOnlySpan<short> pcm,
        int apiRate,
        int endBand,
        bool celtToSilk,
        Span<byte> destination)
    {
        int fiveMilliseconds = apiRate / 200;
        ReadOnlySpan<short> window = celtToSilk
            ? pcm[..fiveMilliseconds]
            : pcm[(pcm.Length - fiveMilliseconds)..];
        redundantCelt.Encode(window, endBand, destination);
        return redundantCelt.FinalRange;
    }
}
