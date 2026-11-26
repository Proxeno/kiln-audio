using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Encoder;
using Kiln.Audio.Tests.Encoder;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Packets written by <see cref="OpusPacketWriter"/> parse with the library's parser, decode with
/// the library's decoder, and are accepted by <c>opus_demo -d</c> without a range mismatch.
/// </summary>
public class OpusPacketWriterTests
{
    private static readonly byte Toc =
        OpusTocBuilder.Build(OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms20, stereo: false);

    public static TheoryData<string, int, int, bool> Shapes()
    {
        // name, frame count, padding bytes, whether the last frame is made unequal (VBR)
        return new TheoryData<string, int, int, bool>
        {
            { "code 0", 1, 0, false },
            { "code 1", 2, 0, false },
            { "code 3 CBR", 3, 0, false },
            { "code 3 CBR + padding", 3, 7, false },
            { "code 2", 2, 0, true },
            { "code 3 VBR", 3, 0, true },
            { "code 3 VBR + padding", 3, 5, true },
        };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_round_trips_through_the_parser_and_the_decoder(
        string name, int count, int padding, bool unequal)
    {
        _ = name;
        List<byte[]> frames = BuildFrames(count, unequal);
        var packet = new byte[4096];
        int written = Write(packet, frames, padding);

        OpusStatus status = OpusPacket.TryParse(
            packet.AsSpan(0, written), new Range[OpusPacket.MaxFrames], out int frameCount, out OpusToc toc, out int parsedPadding);

        status.Should().Be(OpusStatus.Ok);
        frameCount.Should().Be(count);
        parsedPadding.Should().Be(padding);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        toc.FrameCountCode.Should().Be(packet[0] & 0x03);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[count * EncoderTestSignals.FrameSamples];
        decoder.Decode(packet.AsSpan(0, written), pcm).Should().Be(count * EncoderTestSignals.FrameSamples);
    }

    [Fact]
    public void Code0_is_one_toc_byte_and_the_frame()
    {
        List<byte[]> frames = BuildFrames(1, unequal: false);

        var packet = new byte[4096];
        int written = Write(packet, frames);

        (packet[0] & 0x03).Should().Be(0);
        packet[1].Should().Be(frames[0][0]);
        written.Should().Be(frames[0].Length + 1);
    }

    [Fact]
    public void Two_equal_frames_use_code1()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(2);
        var frames = new List<byte[]> { encoded[0], encoded[1] };

        var packet = new byte[4096];
        Write(packet, frames);

        (packet[0] & 0x03).Should().Be(1);
    }

    [Fact]
    public void Two_unequal_frames_use_code2_with_the_first_length_explicit()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);
        var frames = new List<byte[]> { encoded[0], EncoderTestSignals.EncodeSilenceCeltFrame() };

        var packet = new byte[4096];
        int written = Write(packet, frames);

        (packet[0] & 0x03).Should().Be(2);
        packet[1].Should().Be((byte)frames[0].Length);

        Span<Range> ranges = stackalloc Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(packet.AsSpan(0, written), ranges, out int count, out _, out _).Should().Be(OpusStatus.Ok);
        count.Should().Be(2);
        (ranges[0].End.Value - ranges[0].Start.Value).Should().Be(frames[0].Length);
        (ranges[1].End.Value - ranges[1].Start.Value).Should().Be(frames[1].Length);
    }

    [Fact]
    public void Three_equal_frames_use_code3_cbr()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(3);
        var frames = new List<byte[]> { encoded[0], encoded[1], encoded[2] };

        var packet = new byte[4096];
        Write(packet, frames);

        (packet[0] & 0x03).Should().Be(3);
        packet[1].Should().Be(3); // M = 3, CBR, no padding
    }

    [Fact]
    public void Unequal_frames_use_code3_vbr_and_write_every_but_the_last_length()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(2);
        var frames = new List<byte[]> { encoded[0], encoded[1], EncoderTestSignals.EncodeSilenceCeltFrame() };

        var packet = new byte[4096];
        Write(packet, frames);

        (packet[0] & 0x03).Should().Be(3);
        (packet[1] & 0x80).Should().Be(0x80);
        packet[2].Should().Be((byte)frames[0].Length);
        packet[3].Should().Be((byte)frames[1].Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(509)]
    [InlineData(1000)]
    public void Padding_chain_round_trips(int padding)
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);
        var frames = new List<byte[]> { encoded[0] };

        var packet = new byte[4096 + padding];
        int written = Write(packet, frames, padding);

        OpusStatus status = OpusPacket.TryParse(
            packet.AsSpan(0, written), new Range[OpusPacket.MaxFrames], out int count, out _, out int parsedPadding);

        status.Should().Be(OpusStatus.Ok);
        count.Should().Be(1);
        parsedPadding.Should().Be(padding);
        written.Should().Be(1 + 1 + OpusPacketWriter.PaddingChainBytes(padding) + frames[0].Length + padding);
        packet.AsSpan(written - padding, padding).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Padding_chain_bytes_match_their_announced_total()
    {
        for (int padding = 1; padding <= 1200; padding++)
        {
            int bytes = OpusPacketWriter.PaddingChainBytes(padding);
            var chain = new byte[bytes];
            bytes.Should().Be(OpusPacketWriter.WritePaddingChain(chain, padding));

            int announced = 0;
            for (int i = 0; i < bytes; i++)
            {
                announced += chain[i] == 255 ? 254 : chain[i];
            }

            announced.Should().Be(padding);
        }
    }

    [Fact]
    public void Writing_allocates_nothing()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(3);
        var frames = new List<byte[]> { encoded[0], encoded[1], encoded[2] };
        int[] sizes = [encoded[0].Length, encoded[1].Length, encoded[2].Length];
        byte[] data = frames.SelectMany(frame => frame).ToArray();
        var packet = new byte[4096];

        OpusPacketWriter.Write(packet, Toc, sizes, data, 9); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        OpusPacketWriter.Write(packet, Toc, sizes, data, 9);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(1);
        int[] sizes = [encoded[0].Length];
        var packet = new byte[4096];

        ((Action)(() => OpusPacketWriter.Write(packet, Toc, [], []))).Should()
            .Throw<ArgumentException>();
        ((Action)(() => OpusPacketWriter.Write(packet, Toc, sizes, []))).Should()
            .Throw<ArgumentException>();
        ((Action)(() => OpusPacketWriter.Write(new byte[1], Toc, sizes, encoded[0]))).Should()
            .Throw<ArgumentException>();
        ((Action)(() => OpusPacketWriter.Write(packet, Toc, [1276], new byte[1276]))).Should()
            .Throw<ArgumentException>();
        ((Action)(() => OpusPacketWriter.Write(packet, Toc, sizes, encoded[0], -1))).Should()
            .Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Opus_demo_decodes_every_shape_without_a_range_mismatch(
        string name, int count, int padding, bool unequal)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        List<byte[]> frames = BuildFrames(count, unequal);
        var packet = new byte[4096];
        int written = Write(packet, frames, padding);
        byte[] bytes = packet.AsSpan(0, written).ToArray();

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[count * EncoderTestSignals.FrameSamples];
        int samples = decoder.Decode(bytes, pcm);
        samples.Should().Be(count * EncoderTestSignals.FrameSamples);

        string path = Path.Combine(Path.GetTempPath(), $"kiln-writer-{Guid.NewGuid():N}.bit");
        try
        {
            EncoderCorpus.WriteBitFile(path, [(bytes, decoder.FinalRange)]);
            (short[] decoded, string diagnostics) = EncoderOracle.Decode(path);
            EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                $"{name}: opus_demo reported a range mismatch:\n{diagnostics}");
            decoded.Length.Should().Be(samples, $"{name}: opus_demo decoded an unexpected sample count");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static List<byte[]> BuildFrames(int count, bool unequal)
    {
        (byte[][] encoded, _) = EncoderTestSignals.EncodeCeltFrames(count);
        if (unequal)
        {
            // A shorter silence frame in the last slot makes the packet VBR; two frames then use
            // code 2 and three or more use VBR code 3.
            encoded[^1] = EncoderTestSignals.EncodeSilenceCeltFrame();
        }

        return encoded.ToList();
    }

    private static int Write(Span<byte> packet, List<byte[]> frames, int padding = 0)
    {
        int[] sizes = frames.Select(frame => frame.Length).ToArray();
        byte[] data = frames.SelectMany(frame => frame).ToArray();
        return OpusPacketWriter.Write(packet, Toc, sizes, data, padding);
    }
}
