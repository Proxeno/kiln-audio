using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Encoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>Joining and splitting packets with <see cref="OpusRepacketizer"/>.</summary>
public class OpusRepacketizerTests
{
    private static readonly byte Toc =
        OpusTocBuilder.Build(OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms20, stereo: false);

    [Fact]
    public void Joining_two_packets_preserves_the_decoded_stream()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(3);
        byte[] first = MakePacket(encoded[0], encoded[1]);
        byte[] second = MakePacket(encoded[2]);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(first).Should().Be(OpusStatus.Ok);
        repacketizer.TryCat(second).Should().Be(OpusStatus.Ok);
        repacketizer.FrameCount.Should().Be(3);

        var joined = new byte[4096];
        repacketizer.TryOut(joined, out int written).Should().Be(OpusStatus.Ok);
        written.Should().BeGreaterThan(0);
        (joined[0] & 0x03).Should().Be(3);

        var separate = new short[3 * EncoderTestSignals.FrameSamples];
        using (var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 }))
        {
            int a = decoder.Decode(first, separate);
            int b = decoder.Decode(second, separate.AsSpan(a));
            (a + b).Should().Be(separate.Length);
        }

        var combined = new short[3 * EncoderTestSignals.FrameSamples];
        using (var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 }))
        {
            decoder.Decode(joined.AsSpan(0, written), combined).Should().Be(combined.Length);
        }

        combined.Should().Equal(separate);
    }

    [Fact]
    public void Out_range_splits_a_packet_into_a_contiguous_subset()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(3);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(MakePacket(encoded[0], encoded[1], encoded[2])).Should().Be(OpusStatus.Ok);

        var split = new byte[4096];
        repacketizer.TryOutRange(1, 3, split, out int written).Should().Be(OpusStatus.Ok);
        OpusPacket.TryParse(
            split.AsSpan(0, written), new Range[OpusPacket.MaxFrames], out int count, out _, out _).Should().Be(OpusStatus.Ok);
        count.Should().Be(2);

        Span<Range> ranges = stackalloc Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(split.AsSpan(0, written), ranges, out _, out _, out _);
        split[ranges[0]].ToArray().Should().Equal(encoded[1]);
        split[ranges[1]].ToArray().Should().Equal(encoded[2]);

        repacketizer.TryOutRange(2, 3, split, out written).Should().Be(OpusStatus.Ok);
        (split[0] & 0x03).Should().Be(0);
    }

    [Fact]
    public void Cat_rejects_a_different_configuration()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(MakePacket(encoded[0])).Should().Be(OpusStatus.Ok);

        byte otherToc = OpusTocBuilder.Build(
            OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms10, stereo: false);
        var other = new byte[8];
        int written = OpusPacketWriter.Write(other, otherToc, [0], []);

        repacketizer.TryCat(other.AsSpan(0, written)).Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Cat_enforces_the_120_ms_limit()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(6);
        byte[] twelveFrames = MakePacket(encoded[0], encoded[1]);
        // Six 20 ms frames are exactly 120 ms.
        byte[] six = MakePacket(encoded[0], encoded[1], encoded[2], encoded[3], encoded[4], encoded[5]);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(six).Should().Be(OpusStatus.Ok);
        repacketizer.FrameCount.Should().Be(6);
        repacketizer.TryCat(twelveFrames).Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Cat_rejects_a_malformed_packet()
    {
        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();

        repacketizer.TryCat([]).Should().Be(OpusStatus.InvalidPacket);
        repacketizer.TryCat([0x03]).Should().Be(OpusStatus.InvalidPacket); // code 3 without a count byte
    }

    [Fact]
    public void Out_reports_an_invalid_range_or_a_small_buffer()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(MakePacket(encoded[0])).Should().Be(OpusStatus.Ok);

        repacketizer.TryOutRange(-1, 1, new byte[64], out _).Should().Be(OpusStatus.InvalidPacket);
        repacketizer.TryOutRange(0, 0, new byte[64], out _).Should().Be(OpusStatus.InvalidPacket);
        repacketizer.TryOutRange(0, 2, new byte[64], out _).Should().Be(OpusStatus.InvalidPacket);
        repacketizer.TryOut(new byte[1], out _).Should().Be(OpusStatus.BufferTooSmall);
    }

    [Fact]
    public void One_frame_from_two_unequal_packets_uses_code2()
    {
        (byte[][] tone, _) = EncoderTestSignals.EncodeCeltFrames(1);
        byte[] silence = EncoderTestSignals.EncodeSilenceCeltFrame();

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(MakePacket(tone[0])).Should().Be(OpusStatus.Ok);
        repacketizer.TryCat(MakePacket(silence)).Should().Be(OpusStatus.Ok);

        var output = new byte[4096];
        repacketizer.TryOut(output, out int written).Should().Be(OpusStatus.Ok);

        (output[0] & 0x03).Should().Be(2);
        Span<Range> ranges = stackalloc Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(output.AsSpan(0, written), ranges, out int count, out _, out _).Should().Be(OpusStatus.Ok);
        count.Should().Be(2);
        (ranges[0].End.Value - ranges[0].Start.Value).Should().Be(tone[0].Length);
        (ranges[1].End.Value - ranges[1].Start.Value).Should().Be(silence.Length);
    }

    [Fact]
    public void Init_discards_collected_frames()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);

        var repacketizer = new OpusRepacketizer();
        repacketizer.Init();
        repacketizer.TryCat(MakePacket(encoded[0]));
        repacketizer.Init();

        repacketizer.FrameCount.Should().Be(0);
        repacketizer.TryOut(new byte[64], out _).Should().Be(OpusStatus.InvalidPacket);
    }

    [Fact]
    public void Cat_and_out_allocate_nothing()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(3);
        byte[] packet = MakePacket(encoded[0], encoded[1], encoded[2]);
        var output = new byte[4096];
        var repacketizer = new OpusRepacketizer();

        repacketizer.Init();
        repacketizer.TryCat(packet);
        repacketizer.TryOut(output, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        repacketizer.Init();
        repacketizer.TryCat(packet);
        repacketizer.TryOut(output, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    private static byte[] MakePacket(params byte[][] frames)
    {
        int[] sizes = frames.Select(frame => frame.Length).ToArray();
        byte[] data = frames.SelectMany(frame => frame).ToArray();
        var packet = new byte[data.Length + 128];
        int written = OpusPacketWriter.Write(packet, Toc, sizes, data);
        return packet.AsSpan(0, written).ToArray();
    }
}
