// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Encoder;

/// <summary>
/// Joins and splits Opus packets at the frame level, like the reference repacketizer
/// (RFC 6716 Appendix A, <c>src/repacketizer.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryCat"/> appends every frame of a packet; <see cref="TryOut"/> emits one packet
/// containing all of them and <see cref="TryOutRange"/> emits a contiguous subset, which is the
/// split operation. All packets must share the same configuration and channel count, and the
/// combined duration may not exceed 120 ms (§3.4).
/// </para>
/// <para>
/// The reference stores pointers into the caller's buffers. This port instead copies each frame
/// into a fixed buffer allocated at construction, so the class owns its input's lifetime and
/// every operation after construction is allocation-free. The buffer is sized for the worst
/// legal packet: 48 frames of 1275 bytes.
/// </para>
/// </remarks>
internal sealed class OpusRepacketizer
{
    private readonly byte[] _storage = new byte[OpusPacket.MaxFrames * OpusPacket.MaxFrameBytes];
    private readonly int[] _offsets = new int[OpusPacket.MaxFrames];
    private readonly int[] _lengths = new int[OpusPacket.MaxFrames];

    private byte _toc;
    private int _samplesPerFrame;
    private int _used;

    /// <summary>Gets the number of frames currently collected.</summary>
    internal int FrameCount { get; private set; }

    /// <summary>Discards every collected frame.</summary>
    internal void Init()
    {
        FrameCount = 0;
        _used = 0;
        _toc = 0;
        _samplesPerFrame = 0;
    }

    /// <summary>Appends every frame of <paramref name="packet"/>.</summary>
    /// <param name="packet">A complete Opus packet.</param>
    /// <returns>
    /// <see cref="OpusStatus.Ok"/>; <see cref="OpusStatus.InvalidPacket"/> when the packet is
    /// malformed, has a different configuration, or would exceed 120 ms in total.
    /// </returns>
    internal OpusStatus TryCat(ReadOnlySpan<byte> packet)
    {
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        OpusStatus status = OpusPacket.TryParse(packet, frames, out int count, out OpusToc toc, out _);
        if (status != OpusStatus.Ok)
        {
            return status;
        }

        if (FrameCount == 0)
        {
            _toc = packet[0];
            _samplesPerFrame = OpusPacket.GetSamplesPerFrame(toc, 8000);
        }
        else if ((_toc & 0xFC) != (packet[0] & 0xFC))
        {
            // The configuration and channel flag must match; the frame-count code may differ.
            return OpusStatus.InvalidPacket;
        }

        if (FrameCount + count > OpusPacket.MaxFrames)
        {
            return OpusStatus.InvalidPacket;
        }

        // RFC 6716 §3.4: no packet may last more than 120 ms at 8 kHz (960 samples).
        if ((FrameCount + count) * _samplesPerFrame > OpusPacket.MaxPacketMilliseconds * 8)
        {
            return OpusStatus.InvalidPacket;
        }

        int dataLength = 0;
        for (int i = 0; i < count; i++)
        {
            dataLength += frames[i].End.Value - frames[i].Start.Value;
        }

        if (_used + dataLength > _storage.Length)
        {
            return OpusStatus.InvalidPacket;
        }

        for (int i = 0; i < count; i++)
        {
            Range range = frames[i];
            int length = range.End.Value - range.Start.Value;
            int slot = FrameCount + i;
            packet[range].CopyTo(_storage.AsSpan(_used));
            _offsets[slot] = _used;
            _lengths[slot] = length;
            _used += length;
        }

        FrameCount += count;
        return OpusStatus.Ok;
    }

    /// <summary>Writes one packet containing every collected frame.</summary>
    /// <param name="packet">Destination for the packet.</param>
    /// <param name="written">Receives the packet size in bytes.</param>
    /// <returns><see cref="OpusStatus.Ok"/>, or <see cref="OpusStatus.BufferTooSmall"/>.</returns>
    internal OpusStatus TryOut(Span<byte> packet, out int written) =>
        TryOutRange(0, FrameCount, packet, out written);

    /// <summary>Writes one packet containing frames <paramref name="begin"/> to <paramref name="end"/> - 1.</summary>
    /// <param name="begin">First frame, inclusive.</param>
    /// <param name="end">Last frame, exclusive.</param>
    /// <param name="packet">Destination for the packet.</param>
    /// <param name="written">Receives the packet size in bytes.</param>
    /// <returns>
    /// <see cref="OpusStatus.Ok"/>; <see cref="OpusStatus.InvalidPacket"/> for an empty or
    /// out-of-range selection; or <see cref="OpusStatus.BufferTooSmall"/>.
    /// </returns>
    internal OpusStatus TryOutRange(int begin, int end, Span<byte> packet, out int written)
    {
        written = 0;
        if (begin < 0 || begin >= end || end > FrameCount)
        {
            return OpusStatus.InvalidPacket;
        }

        int start = _offsets[begin];
        int last = end - 1;
        int length = _offsets[last] + _lengths[last] - start;
        ReadOnlySpan<int> sizes = _lengths.AsSpan(begin, end - begin);
        ReadOnlySpan<byte> data = _storage.AsSpan(start, length);

        int required = OpusPacketWriter.RequiredBytes(sizes, length, 0);
        if (packet.Length < required)
        {
            return OpusStatus.BufferTooSmall;
        }

        written = OpusPacketWriter.Write(packet, _toc, sizes, data);
        return OpusStatus.Ok;
    }
}
