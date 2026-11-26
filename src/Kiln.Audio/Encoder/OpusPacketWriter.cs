// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Encoder;

/// <summary>
/// Writes an Opus packet from a TOC byte and one or more encoded frames (RFC 6716 §3.2).
/// </summary>
/// <remarks>
/// <para>
/// The layout is chosen the way the reference encoder's repacketizer chooses it: one frame is
/// code 0, two frames are code 1 when equal-sized and code 2 otherwise, and three or more frames
/// are code 3 (CBR when every frame is equal-sized, VBR with explicit lengths otherwise).
/// Requested padding forces code 3 because only code 3 can carry it (§3.2.1, §3.3).
/// </para>
/// <para>
/// Nothing is allocated: the caller's packet span is written in place and every scratch value
/// lives on the stack.
/// </para>
/// </remarks>
internal static class OpusPacketWriter
{
    /// <summary>Maximum number of frames in one packet (RFC 6716 §3.2.1).</summary>
    internal const int MaxFrames = OpusPacket.MaxFrames;

    /// <summary>Maximum encoded size of one frame in bytes (RFC 6716 §3.2.1).</summary>
    internal const int MaxFrameBytes = OpusPacket.MaxFrameBytes;

    /// <summary>
    /// Writes one packet and returns its size.
    /// </summary>
    /// <param name="packet">Destination; must hold the complete packet.</param>
    /// <param name="toc">A TOC byte from <see cref="OpusTocBuilder"/>; its low two bits are replaced.</param>
    /// <param name="frameSizes">One length per frame, in packet order.</param>
    /// <param name="frameData">The frames' bytes, concatenated in the same order.</param>
    /// <param name="paddingBytes">
    /// Padding bytes to append after the frames, excluding the padding-length bytes themselves
    /// (RFC 6716 §3.3). Zero means no padding.
    /// </param>
    /// <returns>The number of bytes written to <paramref name="packet"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The arguments describe an unrepresentable packet, or <paramref name="packet"/> is too small.
    /// </exception>
    internal static int Write(
        Span<byte> packet,
        byte toc,
        ReadOnlySpan<int> frameSizes,
        ReadOnlySpan<byte> frameData,
        int paddingBytes = 0)
    {
        if (frameSizes.Length is < 1 or > MaxFrames)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A packet holds 1 to {MaxFrames} frames; got {frameSizes.Length}."),
                nameof(frameSizes));
        }

        if (paddingBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paddingBytes), paddingBytes, "Padding cannot be negative.");
        }

        int dataBytes = 0;
        bool allEqual = true;
        for (int i = 0; i < frameSizes.Length; i++)
        {
            int size = frameSizes[i];
            if (size is < 0 or > MaxFrameBytes)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Frame {i} has {size} bytes; the limit is {MaxFrameBytes}."),
                    nameof(frameSizes));
            }

            dataBytes += size;
            if (i > 0 && size != frameSizes[0])
            {
                allEqual = false;
            }
        }

        if (dataBytes != frameData.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame lengths sum to {dataBytes} but the data holds {frameData.Length} bytes."),
                nameof(frameData));
        }

        int count = frameSizes.Length;
        int code = ChooseCode(count, allEqual, paddingBytes);
        int required = RequiredBytes(frameSizes, dataBytes, paddingBytes);
        if (packet.Length < required)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The packet needs {required} bytes but the destination holds {packet.Length}."),
                nameof(packet));
        }

        bool vbr = code == 3 && !allEqual;
        int position = 0;
        packet[position++] = (byte)((toc & 0xFC) | code);
        if (code == 3)
        {
            int countByte = count;
            if (vbr)
            {
                countByte |= 0x80;
            }

            if (paddingBytes > 0)
            {
                countByte |= 0x40;
            }

            packet[position++] = (byte)countByte;
            if (paddingBytes > 0)
            {
                position += WritePaddingChain(packet[position..], paddingBytes);
            }
        }

        if (code == 2)
        {
            position += WriteFrameLength(packet[position..], frameSizes[0]);
        }
        else if (vbr)
        {
            for (int i = 0; i < count - 1; i++)
            {
                position += WriteFrameLength(packet[position..], frameSizes[i]);
            }
        }

        frameData.CopyTo(packet[position..]);
        position += dataBytes;
        if (paddingBytes > 0)
        {
            packet.Slice(position, paddingBytes).Clear();
            position += paddingBytes;
        }

        return position;
    }

    /// <summary>Returns the packet size a write of these frames will produce.</summary>
    /// <param name="frameSizes">One length per frame, in packet order.</param>
    /// <param name="frameDataBytes">The frames' total bytes.</param>
    /// <param name="paddingBytes">Padding bytes, excluding the padding-length bytes.</param>
    /// <returns>The complete packet size in bytes.</returns>
    internal static int RequiredBytes(ReadOnlySpan<int> frameSizes, int frameDataBytes, int paddingBytes)
    {
        int count = frameSizes.Length;
        bool allEqual = true;
        for (int i = 1; i < count; i++)
        {
            if (frameSizes[i] != frameSizes[0])
            {
                allEqual = false;
                break;
            }
        }

        int code = ChooseCode(count, allEqual, paddingBytes);
        bool vbr = code == 3 && !allEqual;
        int header = 1 + (code == 3 ? 1 : 0);
        if (paddingBytes > 0)
        {
            header += PaddingChainBytes(paddingBytes);
        }

        if (code == 2)
        {
            header += FrameLengthBytes(frameSizes[0]);
        }
        else if (vbr)
        {
            for (int i = 0; i < count - 1; i++)
            {
                header += FrameLengthBytes(frameSizes[i]);
            }
        }

        return header + frameDataBytes + paddingBytes;
    }

    /// <summary>Returns the frame-count code the writer will use.</summary>
    /// <param name="count">The number of frames.</param>
    /// <param name="allEqual">Whether every frame is the same size.</param>
    /// <param name="paddingBytes">Padding bytes, excluding the padding-length bytes.</param>
    /// <returns>The frame-count code, 0 to 3.</returns>
    private static int ChooseCode(int count, bool allEqual, int paddingBytes)
    {
        if (paddingBytes > 0)
        {
            return 3;
        }

        if (count == 1)
        {
            return 0;
        }

        if (count == 2)
        {
            return allEqual ? 1 : 2;
        }

        return 3;
    }

    /// <summary>Returns the number of padding-length bytes needed to announce <paramref name="padding"/>.</summary>
    /// <param name="padding">Padding bytes, excluding the length bytes.</param>
    /// <returns>The number of length bytes, at least one.</returns>
    internal static int PaddingChainBytes(int padding)
    {
        int bytes = 0;
        while (padding >= 255)
        {
            bytes++;
            padding -= 254;
        }

        return bytes + 1;
    }

    /// <summary>Writes the padding-length chain and returns its size in bytes (RFC 6716 §3.3).</summary>
    /// <param name="destination">Destination for the chain.</param>
    /// <param name="padding">Padding bytes announced by the chain, excluding the chain itself.</param>
    /// <returns>The number of bytes written.</returns>
    internal static int WritePaddingChain(Span<byte> destination, int padding)
    {
        int written = 0;
        while (padding >= 255)
        {
            destination[written++] = 255; // a 255 byte continues the chain and announces 254 bytes
            padding -= 254;
        }

        destination[written++] = (byte)padding;
        return written;
    }

    /// <summary>Returns 1 for a one-byte length and 2 for a two-byte length (RFC 6716 §3.2.1).</summary>
    /// <param name="size">The frame size.</param>
    /// <returns>The encoded length's size in bytes.</returns>
    private static int FrameLengthBytes(int size) => size < 252 ? 1 : 2;

    /// <summary>Writes a frame length and returns the bytes written (RFC 6716 §3.2.1).</summary>
    /// <param name="destination">Destination for the length field.</param>
    /// <param name="size">The frame size, at most 1275.</param>
    /// <returns>The number of bytes written.</returns>
    private static int WriteFrameLength(Span<byte> destination, int size)
    {
        if (size < 252)
        {
            destination[0] = (byte)size;
            return 1;
        }

        int first = 252 + (size & 0x3);
        destination[0] = (byte)first;
        destination[1] = (byte)((size - first) >> 2);
        return 2;
    }
}
