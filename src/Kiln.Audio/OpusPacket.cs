using System.IO;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio;

/// <summary>Result of <see cref="OpusPacket.TryParse"/>.</summary>
internal enum OpusStatus
{
    /// <summary>The packet is valid; every frame range was written to the caller's span.</summary>
    Ok = 0,

    /// <summary>
    /// The packet violates a packet-framing rule and must be discarded or concealed. The decoder
    /// must not interpret any of its bytes (RFC 6716 §3.4).
    /// </summary>
    InvalidPacket,

    /// <summary>
    /// The packet is valid, but the caller's span is too short to receive every frame range.
    /// Nothing was written.
    /// </summary>
    BufferTooSmall,
}

/// <summary>
/// Splits an Opus packet into its frames and validates it (RFC 6716 §3.2).
/// </summary>
/// <remarks>
/// <para>
/// A packet is a one-byte TOC (<see cref="OpusToc"/>, §3.1) followed by packed frames. The
/// frame-count code in the TOC selects the layout (RFC 6716 §3.2.1):
/// </para>
/// <list type="bullet">
/// <item><description>Code 0: one frame occupying the rest of the packet.</description></item>
/// <item><description>Code 1: two frames of equal size.</description></item>
/// <item><description>Code 2: two frames, the first length encoded in one or two bytes.</description></item>
/// <item><description>Code 3: a frame-count byte (VBR flag, padding flag, M), an optional
/// padding-length chain, then M equal (CBR) or explicitly sized (VBR) frames.</description></item>
/// </list>
/// <para>
/// The parser enforces the seven validity rules of RFC 6716 §3.2–§3.4, numbered R1–R7 here:
/// </para>
/// <list type="number">
/// <item><description>R1: the packet contains at least the TOC byte (§3.2).</description></item>
/// <item><description>R2: a code 1 payload splits into two equal frames, so it has even
/// length (§3.2.1).</description></item>
/// <item><description>R3: a code 2 first-frame length is present and no larger than the
/// remaining payload (§3.2.1).</description></item>
/// <item><description>R4: a code 3 frame-count byte is present and M is 1 to 48
/// (§3.2.1).</description></item>
/// <item><description>R5: no packet lasts more than 120 ms (§3.4).</description></item>
/// <item><description>R6: the padding-length chain and the padding it announces fit inside
/// the packet (§3.3).</description></item>
/// <item><description>R7: every frame is at most 1275 bytes and the frame bytes are accounted
/// for exactly; a zero-length frame is legal and means DTX or a lost frame (§3.2.1,
/// §3.4).</description></item>
/// </list>
/// <para>
/// Parsing is allocation-free. On <see cref="OpusStatus.BufferTooSmall"/> the packet is valid
/// and <c>frameCount</c> still receives the number of frames it contains.
/// </para>
/// </remarks>
public static class OpusPacket
{
    /// <summary>Maximum number of frames in one packet (RFC 6716 §3.2.1).</summary>
    internal const int MaxFrames = 48;

    /// <summary>Maximum encoded size of one frame in bytes (RFC 6716 §3.2.1).</summary>
    internal const int MaxFrameBytes = 1275;

    /// <summary>Maximum duration of one packet in milliseconds (RFC 6716 §3.4).</summary>
    internal const int MaxPacketMilliseconds = 120;

    /// <summary>Tenths of a millisecond in one millisecond, for the duration check.</summary>
    private const int TenthsPerMillisecond = 10;

    /// <summary>
    /// Determines whether the packet's first SILK frame carries in-band FEC (LBRR) data.
    /// </summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <returns>
    /// True when the first SILK frame of a SILK-only or hybrid packet signals LBRR data; false
    /// for a CELT-only packet, and for a SILK frame whose LBRR flags are clear.
    /// </returns>
    /// <remarks>
    /// <para>
    /// LBRR data describes the audio immediately before the packet (RFC 6716 §4.2.4–§4.2.5), so
    /// this is the data an FEC decode of the packet would use. It range-decodes only the VAD and
    /// LBRR flags of the first frame (§4.2.3–§4.2.4), keeps no decoder state between calls and
    /// allocates nothing. For a stereo packet either channel's flag counts.
    /// </para>
    /// <para>
    /// For 40 ms and 60 ms frames, where each channel's LBRR flags are a per-frame mask, only the
    /// first SILK frame's bit is considered. This matches
    /// <see cref="OpusDecoder.LastFecOutcome"/> after a successful FEC decode of the same packet.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The packet violates RFC 6716 §3 framing.</exception>
    public static bool HasLbrr(ReadOnlySpan<byte> packet)
    {
        Span<Range> frames = stackalloc Range[MaxFrames];
        if (TryParse(packet, frames, out _, out OpusToc toc, out _) != OpusStatus.Ok)
        {
            throw new InvalidDataException("The Opus packet is malformed (RFC 6716 §3.2–§3.4).");
        }

        // CELT-only packets have no SILK layer and therefore no LBRR (RFC 6716 §4.2).
        if (toc.Mode == OpusMode.Celt)
        {
            return false;
        }

        ReadOnlySpan<byte> data = packet[frames[0]];

        // A payload of 0 or 1 byte is DTX or a lost frame; there is no SILK header to read.
        if (data.Length <= 1)
        {
            return false;
        }

        // A 10 or 20 ms Opus frame holds one SILK frame; longer frames hold two or three.
        int silkFrames = toc.FrameDuration switch
        {
            OpusFrameDuration.Ms40 => 2,
            OpusFrameDuration.Ms60 => 3,
            _ => 1,
        };

        var dec = new RangeDecoder(data);
        return (DecodeFirstFrameLbrrFlags(ref dec, silkFrames, toc.IsStereo) & 1) != 0;
    }

    /// <summary>
    /// Range-decodes the VAD and LBRR flags of one frame's SILK header (RFC 6716 §4.2.3–§4.2.4),
    /// leaving the decoder after the flags.
    /// </summary>
    /// <param name="dec">Range decoder over the frame's bytes.</param>
    /// <param name="silkFrames">Number of SILK frames in the Opus frame: 1, 2 or 3.</param>
    /// <param name="stereo">Whether the frame carries a side channel.</param>
    /// <returns>
    /// The union of both channels' LBRR flags, with bit <c>f</c> set when SILK frame <c>f</c>
    /// carries LBRR data.
    /// </returns>
    private static int DecodeFirstFrameLbrrFlags(ref RangeDecoder dec, int silkFrames, bool stereo)
    {
        // The flags precede every state-dependent SILK symbol, so a fresh range decoder reads them
        // exactly (RFC 6716 §4.2.3: VAD flags first, then the channel's LBRR flag).
        for (int frame = 0; frame < silkFrames; frame++)
        {
            dec.DecodeBitLogp(1);
        }

        bool midLbrr = dec.DecodeBitLogp(1);
        bool sideLbrr = false;
        if (stereo)
        {
            for (int frame = 0; frame < silkFrames; frame++)
            {
                dec.DecodeBitLogp(1);
            }

            sideLbrr = dec.DecodeBitLogp(1);
        }

        // RFC 6716 §4.2.4: 40 ms and 60 ms frames carry a per-frame LBRR mask after the flags.
        int flags = midLbrr ? SilkDecoder.DecodeLbrrMask(ref dec, silkFrames) : 0;
        if (sideLbrr)
        {
            flags |= SilkDecoder.DecodeLbrrMask(ref dec, silkFrames);
        }

        return flags;
    }

    /// <summary>
    /// Splits <paramref name="packet"/> into frames and validates it.
    /// </summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <param name="frames">
    /// Destination for one range per frame, indexing <paramref name="packet"/>. It must hold at
    /// least <see cref="MaxFrames"/> entries to accept every valid packet.
    /// </param>
    /// <param name="frameCount">Receives the number of frames in the packet.</param>
    /// <param name="toc">Receives the parsed TOC byte.</param>
    /// <param name="paddingBytes">
    /// Receives the number of padding bytes at the end of the packet, excluding the
    /// padding-length bytes themselves (RFC 6716 §3.3).
    /// </param>
    /// <returns>
    /// <see cref="OpusStatus.Ok"/> when the packet is valid and every frame range was written;
    /// <see cref="OpusStatus.InvalidPacket"/> when it violates a framing rule; or
    /// <see cref="OpusStatus.BufferTooSmall"/> when <paramref name="frames"/> cannot hold every
    /// frame.
    /// </returns>
    internal static OpusStatus TryParse(
        ReadOnlySpan<byte> packet,
        Span<Range> frames,
        out int frameCount,
        out OpusToc toc,
        out int paddingBytes)
    {
        // A valid packet has at most MaxFrames frames, so this buffer always suffices.
        Span<int> sizes = stackalloc int[MaxFrames];
        OpusStatus status = Parse(packet, sizes, out frameCount, out toc, out paddingBytes, out int frameDataOffset);
        if (status != OpusStatus.Ok)
        {
            return status;
        }

        if (frames.Length < frameCount)
        {
            return OpusStatus.BufferTooSmall;
        }

        int position = frameDataOffset;
        for (int i = 0; i < frameCount; i++)
        {
            frames[i] = new Range(position, position + sizes[i]);
            position += sizes[i];
        }

        return OpusStatus.Ok;
    }

    /// <summary>Returns the number of frames in <paramref name="packet"/>, or 0 when it is invalid.</summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <returns>The frame count (1 to 48), or 0 for an invalid packet.</returns>
    internal static int GetFrameCount(ReadOnlySpan<byte> packet)
    {
        Span<int> sizes = stackalloc int[MaxFrames];
        return Parse(packet, sizes, out int frameCount, out _, out _, out _) == OpusStatus.Ok
            ? frameCount
            : 0;
    }

    /// <summary>Returns the number of samples per channel in one frame at <paramref name="fs"/> Hz.</summary>
    /// <param name="toc">The parsed TOC byte, which carries the frame duration (RFC 6716 §2, Table 2).</param>
    /// <param name="fs">Sample rate in Hz: 8000, 12000, 16000, 24000 or 48000.</param>
    /// <returns>Samples per channel in one frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fs"/> is not an Opus sample rate.</exception>
    internal static int GetSamplesPerFrame(OpusToc toc, int fs)
    {
        OpusValidation.ValidateSampleRate(fs, nameof(fs));
        return toc.FrameDuration switch
        {
            OpusFrameDuration.Ms2_5 => fs / 400,
            OpusFrameDuration.Ms5 => fs / 200,
            OpusFrameDuration.Ms10 => fs / 100,
            OpusFrameDuration.Ms20 => fs / 50,
            OpusFrameDuration.Ms40 => fs * 40 / 1000,
            OpusFrameDuration.Ms60 => fs * 60 / 1000,
            _ => throw new ArgumentOutOfRangeException(nameof(toc), toc.FrameDuration, "Unknown frame duration."),
        };
    }

    /// <summary>Validates the packet and writes each frame size into <paramref name="sizes"/>.</summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <param name="sizes">Destination for the frame sizes; at least <see cref="MaxFrames"/> entries.</param>
    /// <param name="frameCount">Receives the number of frames.</param>
    /// <param name="toc">Receives the parsed TOC byte.</param>
    /// <param name="paddingBytes">Receives the number of padding bytes.</param>
    /// <param name="frameDataOffset">Receives the offset of the first frame's data in <paramref name="packet"/>.</param>
    /// <returns>The parse status.</returns>
    private static OpusStatus Parse(
        ReadOnlySpan<byte> packet,
        Span<int> sizes,
        out int frameCount,
        out OpusToc toc,
        out int paddingBytes,
        out int frameDataOffset)
    {
        frameCount = 0;
        toc = default;
        paddingBytes = 0;
        frameDataOffset = 0;

        // R1 (RFC 6716 §3.2): every packet starts with a TOC byte.
        if (packet.IsEmpty)
        {
            return OpusStatus.InvalidPacket;
        }

        toc = OpusToc.Parse(packet[0]);
        const int frameDataStart = 1;

        switch (toc.FrameCountCode)
        {
            case 0:
            {
                // Code 0 (§3.2.1): exactly one frame occupies the rest of the packet.
                int size = packet.Length - frameDataStart;
                if (size > MaxFrameBytes)
                {
                    return OpusStatus.InvalidPacket; // R7
                }

                sizes[0] = size;
                frameCount = 1;
                frameDataOffset = frameDataStart;
                return OpusStatus.Ok;
            }

            case 1:
            {
                // Code 1 (§3.2.1): two frames of equal size.
                int payload = packet.Length - frameDataStart;
                if ((payload & 1) != 0)
                {
                    return OpusStatus.InvalidPacket; // R2
                }

                int size = payload / 2;
                if (size > MaxFrameBytes)
                {
                    return OpusStatus.InvalidPacket; // R7
                }

                sizes[0] = size;
                sizes[1] = size;
                frameCount = 2;
                frameDataOffset = frameDataStart;
                return OpusStatus.Ok;
            }

            case 2:
            {
                // Code 2 (§3.2.1): two frames; the first length is one or two bytes.
                int offset = frameDataStart;
                if (!TryReadFrameLength(packet, packet.Length, ref offset, out int firstSize))
                {
                    return OpusStatus.InvalidPacket; // R3
                }

                int remaining = packet.Length - offset;
                if (firstSize > remaining)
                {
                    return OpusStatus.InvalidPacket; // R3
                }

                // firstSize is at most 1275 by construction of the length encoding; the second
                // frame is whatever is left, which must also respect the frame size limit.
                int secondSize = remaining - firstSize;
                if (secondSize > MaxFrameBytes)
                {
                    return OpusStatus.InvalidPacket; // R7
                }

                sizes[0] = firstSize;
                sizes[1] = secondSize;
                frameCount = 2;
                frameDataOffset = offset;
                return OpusStatus.Ok;
            }

            default:
                return ParseCode3(packet, sizes, toc, out frameCount, out paddingBytes, out frameDataOffset);
        }
    }

    /// <summary>Parses a code 3 packet: frame-count byte, padding chain, then CBR or VBR frames.</summary>
    /// <param name="packet">The packet, starting at its TOC byte.</param>
    /// <param name="sizes">Destination for the frame sizes.</param>
    /// <param name="toc">The parsed TOC byte.</param>
    /// <param name="frameCount">Receives the number of frames.</param>
    /// <param name="paddingBytes">Receives the number of padding bytes.</param>
    /// <param name="frameDataOffset">Receives the offset of the first frame's data.</param>
    /// <returns>The parse status.</returns>
    private static OpusStatus ParseCode3(
        ReadOnlySpan<byte> packet,
        Span<int> sizes,
        OpusToc toc,
        out int frameCount,
        out int paddingBytes,
        out int frameDataOffset)
    {
        frameCount = 0;
        paddingBytes = 0;
        frameDataOffset = 0;

        int offset = 1;

        // R4 (§3.2.1): the frame-count byte is mandatory.
        if (offset >= packet.Length)
        {
            return OpusStatus.InvalidPacket;
        }

        byte countByte = packet[offset++];
        int count = countByte & 0x3F;              // M: bits 0..5
        bool vbr = (countByte & 0x80) != 0;        // bit 7: 0 = CBR, 1 = VBR
        bool hasPadding = (countByte & 0x40) != 0; // bit 6: padding present

        // R4/R5 (§3.2.1, §3.4): 1 to 48 frames and at most 120 ms of audio.
        if (count is < 1 or > MaxFrames
            || count * DurationTenthsOfMilliseconds(toc.FrameDuration) > MaxPacketMilliseconds * TenthsPerMillisecond)
        {
            return OpusStatus.InvalidPacket;
        }

        // R6 (§3.3): the padding length is a chain of bytes. A value of 255 continues the
        // chain and contributes 254 padding bytes; any other value ends it.
        if (hasPadding)
        {
            int padding = 0;
            byte paddingLength;
            do
            {
                if (offset >= packet.Length)
                {
                    return OpusStatus.InvalidPacket; // R6
                }

                paddingLength = packet[offset++];
                padding += paddingLength == 255 ? 254 : paddingLength;

                // Bound the accumulation before it can overflow; a packet cannot hold more
                // padding than its own length.
                if (padding > packet.Length)
                {
                    return OpusStatus.InvalidPacket; // R6
                }
            }
            while (paddingLength == 255);

            paddingBytes = padding;
        }

        // All frame data precedes the padding (§3.3).
        int dataEnd = packet.Length - paddingBytes;
        if (dataEnd < offset)
        {
            return OpusStatus.InvalidPacket; // R6
        }

        if (vbr)
        {
            // VBR (§3.2.1): the first count-1 lengths are explicit; the last frame takes the rest.
            int explicitBytes = 0;
            for (int i = 0; i < count - 1; i++)
            {
                if (!TryReadFrameLength(packet, dataEnd, ref offset, out int size))
                {
                    return OpusStatus.InvalidPacket; // R7
                }

                sizes[i] = size;
                explicitBytes += size;
                if (explicitBytes > dataEnd - offset)
                {
                    return OpusStatus.InvalidPacket; // R7
                }
            }

            int lastSize = dataEnd - offset - explicitBytes;
            if (lastSize > MaxFrameBytes)
            {
                return OpusStatus.InvalidPacket; // R7
            }

            sizes[count - 1] = lastSize;
        }
        else
        {
            // CBR (§3.2.1): the remaining frame bytes split evenly into count frames.
            int frameBytes = dataEnd - offset;
            if (frameBytes % count != 0)
            {
                return OpusStatus.InvalidPacket; // R7
            }

            int size = frameBytes / count;
            if (size > MaxFrameBytes)
            {
                return OpusStatus.InvalidPacket; // R7
            }

            for (int i = 0; i < count; i++)
            {
                sizes[i] = size;
            }
        }

        frameCount = count;
        frameDataOffset = offset;
        return OpusStatus.Ok;
    }

    /// <summary>
    /// Reads one frame length (RFC 6716 §3.2.1): a value below 252 is one byte; otherwise the
    /// value is <c>4 * secondByte + firstByte</c> in two bytes, giving 252 to 1275.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="limit">Exclusive end of the bytes the length field may occupy.</param>
    /// <param name="offset">On entry, the length field's offset; on success, the offset after it.</param>
    /// <param name="size">Receives the frame length.</param>
    /// <returns>True when a complete length field was read; false when it is truncated.</returns>
    private static bool TryReadFrameLength(ReadOnlySpan<byte> packet, int limit, ref int offset, out int size)
    {
        size = 0;
        if (offset >= limit)
        {
            return false;
        }

        byte first = packet[offset];
        if (first < 252)
        {
            size = first;
            offset++;
            return true;
        }

        if (offset + 1 >= limit)
        {
            return false;
        }

        size = (4 * packet[offset + 1]) + first;
        offset += 2;
        return true;
    }

    /// <summary>Returns a frame duration in tenths of a millisecond (RFC 6716 §2, Table 2).</summary>
    /// <param name="duration">The frame duration.</param>
    /// <returns>The duration in tenths of a millisecond.</returns>
    private static int DurationTenthsOfMilliseconds(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms2_5 => 25,
        OpusFrameDuration.Ms5 => 50,
        OpusFrameDuration.Ms10 => 100,
        OpusFrameDuration.Ms20 => 200,
        OpusFrameDuration.Ms40 => 400,
        OpusFrameDuration.Ms60 => 600,
        _ => throw new ArgumentOutOfRangeException(nameof(duration), duration, "Unknown frame duration."),
    };
}
