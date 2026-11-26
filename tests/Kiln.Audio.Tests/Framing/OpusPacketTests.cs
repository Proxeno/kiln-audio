using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.Framing;

/// <summary>
/// Hand-built packets covering every frame-count code, the length encodings, padding, and the
/// seven validity rules of RFC 6716 §3.2–§3.4, plus a seeded fuzz test.
/// </summary>
public class OpusPacketTests
{
    [Fact]
    public void Code0_single_frame_covers_payload()
    {
        byte[] packet = [0x00, 0xAA, 0xBB];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out OpusToc toc, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(1);
        frames[0].Should().Be(new Range(1, 3));
        padding.Should().Be(0);
        toc.FrameCountCode.Should().Be(0);
        toc.Config.Should().Be(0);
    }

    [Fact]
    public void Code0_empty_frame_is_legal()
    {
        byte[] packet = [0x00];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(1);
        frames[0].Should().Be(new Range(1, 1));
        padding.Should().Be(0);
    }

    [Fact]
    public void Code0_frame_of_1275_bytes_is_legal()
    {
        byte[] packet = new byte[1 + OpusPacket.MaxFrameBytes];
        packet[0] = 0x00;
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(1);
        frames[0].Should().Be(new Range(1, 1 + OpusPacket.MaxFrameBytes));
    }

    [Fact]
    public void Code0_frame_of_1276_bytes_is_invalid() // R7
    {
        byte[] packet = new byte[1 + OpusPacket.MaxFrameBytes + 1];
        packet[0] = 0x00;
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code1_two_equal_frames()
    {
        byte[] packet = [0x01, 0x11, 0x22, 0x33, 0x44];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(1, 3));
        frames[1].Should().Be(new Range(3, 5));
        padding.Should().Be(0);
    }

    [Fact]
    public void Code1_odd_payload_is_invalid() // R2
    {
        byte[] packet = [0x01, 0x11, 0x22, 0x33];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code1_two_empty_frames_are_legal()
    {
        byte[] packet = [0x01];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(1, 1));
        frames[1].Should().Be(new Range(1, 1));
    }

    [Fact]
    public void Code1_frame_of_1275_bytes_is_legal()
    {
        byte[] packet = new byte[1 + (2 * OpusPacket.MaxFrameBytes)];
        packet[0] = 0x01;
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(1, 1 + OpusPacket.MaxFrameBytes));
        frames[1].Should().Be(new Range(1 + OpusPacket.MaxFrameBytes, packet.Length));
    }

    [Fact]
    public void Code1_frame_of_1276_bytes_is_invalid() // R7
    {
        byte[] packet = new byte[1 + (2 * (OpusPacket.MaxFrameBytes + 1))];
        packet[0] = 0x01;
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code2_one_byte_length()
    {
        byte[] packet = [0x02, 0x03, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(2, 5));
        frames[1].Should().Be(new Range(5, 7));
        padding.Should().Be(0);
    }

    [Fact]
    public void Code2_length_251_uses_one_byte()
    {
        byte[] packet = new byte[2 + 251 + 1];
        packet[0] = 0x02;
        packet[1] = 0xFB; // 251
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(2, 2 + 251));
        frames[1].Should().Be(new Range(2 + 251, packet.Length));
    }

    [Fact]
    public void Code2_length_252_uses_two_bytes()
    {
        byte[] packet = new byte[3 + 252 + 1];
        packet[0] = 0x02;
        packet[1] = 0xFC; // 252 or more: two-byte length follows
        packet[2] = 0x00; // 4 * 0 + 252 = 252
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(3, 3 + 252));
        frames[1].Should().Be(new Range(3 + 252, packet.Length));
    }

    [Fact]
    public void Code2_length_1275_is_the_maximum()
    {
        byte[] packet = new byte[3 + OpusPacket.MaxFrameBytes];
        packet[0] = 0x02;
        packet[1] = 0xFF;
        packet[2] = 0xFF; // 4 * 255 + 255 = 1275
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(3, 3 + OpusPacket.MaxFrameBytes));
        frames[1].Should().Be(new Range(packet.Length, packet.Length));
    }

    [Fact]
    public void Code2_missing_length_byte_is_invalid() // R3
    {
        byte[] packet = [0x02];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code2_truncated_two_byte_length_is_invalid() // R3
    {
        byte[] packet = [0x02, 0xFC];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code2_first_length_beyond_payload_is_invalid() // R3
    {
        byte[] packet = [0x02, 0x05, 0xAA, 0xBB];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code2_second_frame_of_1276_bytes_is_invalid() // R7
    {
        byte[] packet = new byte[2 + (OpusPacket.MaxFrameBytes + 1)];
        packet[0] = 0x02;
        packet[1] = 0x00; // first frame empty; second gets 1276 bytes
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_cbr_two_frames()
    {
        byte[] packet = [0x03, 0x02, 0x01, 0x02, 0x03, 0x04];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(2, 4));
        frames[1].Should().Be(new Range(4, 6));
        padding.Should().Be(0);
    }

    [Fact]
    public void Code3_cbr_three_frames()
    {
        byte[] packet = [0x03, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(3);
        frames[0].Should().Be(new Range(2, 4));
        frames[1].Should().Be(new Range(4, 6));
        frames[2].Should().Be(new Range(6, 8));
    }

    [Fact]
    public void Code3_cbr_uneven_split_is_invalid() // R7
    {
        byte[] packet = [0x03, 0x02, 0x01, 0x02, 0x03];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_cbr_empty_frames_are_legal()
    {
        byte[] packet = [0x03, 0x02];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(2, 2));
        frames[1].Should().Be(new Range(2, 2));
    }

    [Fact]
    public void Code3_cbr_frame_of_1276_bytes_is_invalid() // R7
    {
        byte[] packet = new byte[2 + (OpusPacket.MaxFrameBytes + 1)];
        packet[0] = 0x03;
        packet[1] = 0x01; // one CBR frame
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_two_frames()
    {
        byte[] packet = [0x03, 0x82, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(3, 6)); // 3 bytes
        frames[1].Should().Be(new Range(6, 8)); // 2 bytes
        padding.Should().Be(0);
    }

    [Fact]
    public void Code3_vbr_three_frames()
    {
        byte[] packet = [0x03, 0x83, 0x01, 0x02, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(3);
        frames[0].Should().Be(new Range(4, 5));   // 1 byte
        frames[1].Should().Be(new Range(5, 7));   // 2 bytes
        frames[2].Should().Be(new Range(7, 10));  // last 3 bytes
    }

    [Fact]
    public void Code3_vbr_two_byte_length()
    {
        byte[] packet = new byte[4 + 252 + 1];
        packet[0] = 0x03;
        packet[1] = 0x82;
        packet[2] = 0xFC;
        packet[3] = 0x00; // 252
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(4, 4 + 252));
        frames[1].Should().Be(new Range(4 + 252, packet.Length));
    }

    [Fact]
    public void Code3_vbr_missing_length_is_invalid() // R7
    {
        byte[] packet = [0x03, 0x82];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_truncated_two_byte_length_is_invalid() // R7
    {
        byte[] packet = [0x03, 0x82, 0xFC];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_length_beyond_payload_is_invalid() // R7
    {
        byte[] packet = [0x03, 0x82, 0x05, 0x01, 0x02];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_last_frame_of_1276_bytes_is_invalid() // R7
    {
        byte[] packet = new byte[3 + (OpusPacket.MaxFrameBytes + 1)];
        packet[0] = 0x03;
        packet[1] = 0x82;
        packet[2] = 0x00; // first frame empty; last gets 1276 bytes
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_empty_last_frame_is_legal()
    {
        byte[] packet = [0x03, 0x82, 0x02, 0xAA, 0xBB];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(3, 5));
        frames[1].Should().Be(new Range(5, 5));
    }

    [Fact]
    public void Code3_padding_is_excluded_from_frames()
    {
        byte[] packet = [0x03, 0x42, 0x02, 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(3, 5));
        frames[1].Should().Be(new Range(5, 7));
        padding.Should().Be(2);
        frames[frameCount - 1].End.Value.Should().Be(packet.Length - padding);
    }

    [Fact]
    public void Code3_padding_255_chain()
    {
        // The chain 0xFF 0x01 announces 254 + 1 = 255 padding bytes.
        byte[] packet = new byte[4 + 4 + 255];
        packet[0] = 0x03;
        packet[1] = 0x41; // padding flag, M = 1
        packet[2] = 0xFF;
        packet[3] = 0x01;
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(1);
        frames[0].Should().Be(new Range(4, 8));
        padding.Should().Be(255);
        frames[0].End.Value.Should().Be(packet.Length - padding);
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0x41 })] // no padding-length byte
    [InlineData(new byte[] { 0x03, 0x41, 0x05 })] // announces more padding than remains
    [InlineData(new byte[] { 0x03, 0x41, 0xFF })] // 255 requires another length byte
    public void Code3_padding_chain_must_fit(byte[] packet) // R6
    {
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_vbr_with_padding()
    {
        byte[] packet = [0x03, 0xC2, 0x01, 0x01, 0xAA, 0xBB];
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out _, out int padding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(2);
        frames[0].Should().Be(new Range(4, 5)); // 1 byte
        frames[1].Should().Be(new Range(5, 5)); // empty; 0xBB is padding
        padding.Should().Be(1);
    }

    [Fact]
    public void Code3_zero_frames_is_invalid() // R4
    {
        byte[] packet = [0x03, 0x00];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_missing_count_byte_is_invalid() // R4
    {
        byte[] packet = [0x03];
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Code3_48_frames_at_2_5_ms_is_120_ms_and_legal() // R5
    {
        // TOC 0x83 = config 16 (CELT narrowband, 2.5 ms), code 3, mono.
        byte[] packet = [0x83, 0x30]; // M = 48, CBR, no padding, no frame data
        OpusStatus status = Parse(packet, out Range[] frames, out int frameCount, out OpusToc toc, out _);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(OpusPacket.MaxFrames);
        OpusPacket.GetSamplesPerFrame(toc, 48000).Should().Be(120);
        (frameCount * OpusPacket.GetSamplesPerFrame(toc, 48000)).Should().Be(5760); // 120 ms
        frames[0].Should().Be(new Range(2, 2));
        frames[OpusPacket.MaxFrames - 1].Should().Be(new Range(2, 2));
    }

    [Fact]
    public void Code3_49_frames_at_2_5_ms_exceeds_120_ms() // R5
    {
        byte[] packet = [0x83, 0x31]; // M = 49
        OpusStatus status = Parse(packet, out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Empty_packet_is_invalid() // R1
    {
        OpusStatus status = Parse([], out _, out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Buffer_too_small_is_reported_after_validation()
    {
        byte[] packet = [0x03, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        var frames = new Range[2];
        OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.BufferTooSmall);
        frameCount.Should().Be(3);
    }

    [Fact]
    public void Buffer_too_small_for_one_frame()
    {
        byte[] packet = [0x00, 0xAA];
        OpusStatus status = OpusPacket.TryParse(packet, [], out int frameCount, out _, out _);

        status.Should().Be(OpusStatus.BufferTooSmall);
        frameCount.Should().Be(1);
    }

    [Fact]
    public void Invalid_packet_wins_over_buffer_too_small()
    {
        byte[] packet = [0x01, 0x11, 0x22, 0x33];
        OpusStatus status = OpusPacket.TryParse(packet, [], out _, out _, out _);

        status.Should().Be(OpusStatus.InvalidPacket);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xAA }, 1)]
    [InlineData(new byte[] { 0x01, 0x11, 0x22, 0x33, 0x44 }, 2)]
    [InlineData(new byte[] { 0x02, 0x01, 0xAA }, 2)]
    [InlineData(new byte[] { 0x03, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, 3)]
    public void GetFrameCount_matches_TryParse(byte[] packet, int expected)
    {
        OpusPacket.GetFrameCount(packet).Should().Be(expected);
    }

    [Theory]
    [InlineData(new byte[] { }, 0)] // empty packet
    [InlineData(new byte[] { 0x03, 0x00 }, 0)] // M = 0
    [InlineData(new byte[] { 0x01, 0x11, 0x22, 0x33 }, 0)] // code 1, odd payload
    public void GetFrameCount_returns_zero_for_invalid_packets(byte[] packet, int expected)
    {
        OpusPacket.GetFrameCount(packet).Should().Be(expected);
    }

    public static TheoryData<OpusFrameDuration, int, int> FrameSampleCounts()
    {
        var data = new TheoryData<OpusFrameDuration, int, int>();
        (OpusFrameDuration Duration, int[] Counts)[] table =
        [
            (OpusFrameDuration.Ms2_5, [20, 30, 40, 60, 120]),
            (OpusFrameDuration.Ms5, [40, 60, 80, 120, 240]),
            (OpusFrameDuration.Ms10, [80, 120, 160, 240, 480]),
            (OpusFrameDuration.Ms20, [160, 240, 320, 480, 960]),
            (OpusFrameDuration.Ms40, [320, 480, 640, 960, 1920]),
            (OpusFrameDuration.Ms60, [480, 720, 960, 1440, 2880]),
        ];
        int[] rates = [8000, 12000, 16000, 24000, 48000];
        foreach ((OpusFrameDuration duration, int[] counts) in table)
        {
            for (int i = 0; i < rates.Length; i++)
            {
                data.Add(duration, rates[i], counts[i]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FrameSampleCounts))]
    public void GetSamplesPerFrame_uses_the_duration_table(OpusFrameDuration duration, int fs, int expected)
    {
        var toc = new OpusToc(0, OpusMode.Silk, OpusBandwidth.Narrowband, duration, false, 0);

        OpusPacket.GetSamplesPerFrame(toc, fs).Should().Be(expected);
    }

    [Fact]
    public void GetSamplesPerFrame_rejects_an_invalid_rate()
    {
        var toc = new OpusToc(0, OpusMode.Silk, OpusBandwidth.Narrowband, OpusFrameDuration.Ms20, false, 0);

        Action act = () => OpusPacket.GetSamplesPerFrame(toc, 44100);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryParse_is_allocation_free()
    {
        byte[] packet = [0x03, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        var frames = new Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(packet, frames, out _, out _, out _); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        OpusPacket.TryParse(packet, frames, out _, out _, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0);
    }

    /// <summary>
    /// 10,000 seeded random packets: the parser never throws, and every valid packet yields
    /// contiguous frame ranges inside the packet that end where the padding begins.
    /// </summary>
    [Fact]
    public void Random_packets_never_throw_and_ok_ranges_tile_the_payload()
    {
        const int Iterations = 10_000;
        var random = new Random(0x0F1A37);
        var frames = new Range[OpusPacket.MaxFrames];
        Span<byte> buffer = stackalloc byte[1500];

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            int length = random.Next(0, buffer.Length + 1);
            random.NextBytes(buffer[..length]);
            ReadOnlySpan<byte> packet = buffer[..length];

            OpusStatus status = OpusPacket.TryParse(packet, frames, out int count, out OpusToc toc, out int padding);
            if (status != OpusStatus.Ok)
            {
                status.Should().Be(OpusStatus.InvalidPacket);
                OpusPacket.GetFrameCount(packet).Should().Be(0);
                continue;
            }

            count.Should().BeInRange(1, OpusPacket.MaxFrames);
            OpusPacket.GetFrameCount(packet).Should().Be(count);

            int position = frames[0].Start.Value;
            position.Should().BeGreaterThanOrEqualTo(1);
            for (int i = 0; i < count; i++)
            {
                frames[i].Start.Value.Should().Be(position);
                frames[i].End.Value.Should().BeGreaterThanOrEqualTo(frames[i].Start.Value);
                (frames[i].End.Value - frames[i].Start.Value).Should().BeLessThanOrEqualTo(OpusPacket.MaxFrameBytes);
                position = frames[i].End.Value;
            }

            position.Should().Be(packet.Length - padding);
            ((long)count * OpusPacket.GetSamplesPerFrame(toc, 48000))
                .Should().BeLessThanOrEqualTo(OpusPacket.MaxPacketMilliseconds * 48);
        }
    }

    private static OpusStatus Parse(
        byte[] packet,
        out Range[] frames,
        out int frameCount,
        out OpusToc toc,
        out int paddingBytes)
    {
        frames = new Range[OpusPacket.MaxFrames];
        return OpusPacket.TryParse(packet, frames, out frameCount, out toc, out paddingBytes);
    }
}
