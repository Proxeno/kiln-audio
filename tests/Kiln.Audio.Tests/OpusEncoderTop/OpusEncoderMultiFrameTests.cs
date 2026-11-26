using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Encoder;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// 40 and 60 ms CELT frames are code 1/code 3 packets of 20 ms frames (RFC 6716 Appendix A,
/// <c>opus_encoder.c</c>).
/// </summary>
public class OpusEncoderMultiFrameTests
{
    [Fact]
    public void A_40_ms_frame_is_a_code1_packet_of_two_20_ms_frames()
    {
        using var encoder = new OpusEncoder(Options(OpusFrameDuration.Ms40));
        var packet = new byte[1276];

        int written = encoder.Encode(EncoderTestSignals.Tone(1920), packet);

        OpusStatus status = OpusPacket.TryParse(
            packet.AsSpan(0, written), new Range[OpusPacket.MaxFrames], out int count, out OpusToc toc, out _);
        status.Should().Be(OpusStatus.Ok);
        count.Should().Be(2);
        toc.FrameCountCode.Should().Be(1);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        toc.Config.Should().Be(31);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[1920];
        decoder.Decode(packet.AsSpan(0, written), pcm).Should().Be(1920);
        decoder.FinalRange.Should().Be(encoder.FinalRange);
    }

    [Fact]
    public void A_60_ms_frame_is_a_code3_packet_of_three_20_ms_frames()
    {
        using var encoder = new OpusEncoder(Options(OpusFrameDuration.Ms60));
        var packet = new byte[1276];

        int written = encoder.Encode(EncoderTestSignals.Tone(2880), packet);

        OpusStatus status = OpusPacket.TryParse(
            packet.AsSpan(0, written), new Range[OpusPacket.MaxFrames], out int count, out OpusToc toc, out _);
        status.Should().Be(OpusStatus.Ok);
        count.Should().Be(3);
        toc.FrameCountCode.Should().Be(3);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        packet[1].Should().Be(3); // CBR, no padding

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[2880];
        decoder.Decode(packet.AsSpan(0, written), pcm).Should().Be(2880);
        decoder.FinalRange.Should().Be(encoder.FinalRange);
    }

    [Theory]
    [InlineData(OpusFrameDuration.Ms40, 2)]
    [InlineData(OpusFrameDuration.Ms60, 3)]
    public void Long_frames_allocate_nothing(OpusFrameDuration duration, int frameCount)
    {
        if (!TestVectors.Available)
        {
            return;
        }

        short[] stereo = Pcm16.Read(TestVectors.DecPath(1));
        var mono = new short[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
        }

        int samples = frameCount * EncoderTestSignals.FrameSamples;
        using var encoder = new OpusEncoder(Options(duration));
        var packet = new byte[1276];

        Run(encoder, mono, packet, samples);
        Run(encoder, mono, packet, samples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(encoder, mono, packet, samples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    [Theory]
    [InlineData(OpusFrameDuration.Ms40, 1920)]
    [InlineData(OpusFrameDuration.Ms60, 2880)]
    public void Opus_demo_decodes_long_frames_without_a_range_mismatch(OpusFrameDuration duration, int samples)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        const int packets = 4;
        using var encoder = new OpusEncoder(Options(duration));
        var packet = new byte[1276];
        var stream = new List<(byte[] Packet, uint Range)>();
        short[] pcm = EncoderTestSignals.Tone(packets * samples);
        for (int i = 0; i < packets; i++)
        {
            int written = encoder.Encode(pcm.AsSpan(i * samples, samples), packet);
            stream.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-long-{Guid.NewGuid():N}.bit");
        try
        {
            EncoderCorpus.WriteBitFile(path, stream);
            (short[] decoded, string diagnostics) = EncoderOracle.Decode(path);
            EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                $"opus_demo reported a range mismatch:\n{diagnostics}");
            decoded.Length.Should().Be(packets * samples);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void The_float_overload_matches_for_long_frames()
    {
        short[] shortPcm = EncoderTestSignals.Tone(1920);
        var floatPcm = new float[shortPcm.Length];
        for (int i = 0; i < shortPcm.Length; i++)
        {
            floatPcm[i] = shortPcm[i] / 32768f;
        }

        var shortPacket = new byte[1276];
        var floatPacket = new byte[1276];
        int shortCount;
        int floatCount;
        using (var encoder = new OpusEncoder(Options(OpusFrameDuration.Ms40)))
        {
            shortCount = encoder.Encode(shortPcm, shortPacket);
        }

        using (var encoder = new OpusEncoder(Options(OpusFrameDuration.Ms40)))
        {
            floatCount = encoder.Encode(floatPcm, floatPacket);
        }

        floatCount.Should().Be(shortCount);
        floatPacket.AsSpan(0, floatCount).SequenceEqual(shortPacket.AsSpan(0, shortCount)).Should().BeTrue();
    }

    private static OpusEncoderOptions Options(OpusFrameDuration duration) => new()
    {
        Application = OpusApplication.RestrictedLowDelay,
        BitrateBps = 32000,
        FrameDuration = duration,
        Complexity = 5,
    };

    private static void Run(OpusEncoder encoder, short[] mono, byte[] packet, int samples)
    {
        for (int offset = 0; offset + samples <= mono.Length; offset += samples)
        {
            encoder.Encode(mono.AsSpan(offset, samples), packet);
        }
    }
}
