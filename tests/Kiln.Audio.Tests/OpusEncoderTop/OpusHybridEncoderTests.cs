using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Encoder;
using Kiln.Audio.Silk.Encoder;
using Kiln.Audio.Tests.Encoder;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Hybrid frames: SILK wideband at 16 kHz plus CELT from band 17 in one range coder
/// (RFC 6716 §4.5.2). Every packet must decode with the library decoder and with <c>opus_demo</c>.
/// </summary>
public class OpusHybridEncoderTests
{
    private const int FrameSamples = 960;
    private const int Bitrate = 32000;

    [Fact]
    public void A_hybrid_frame_decodes_and_matches_the_final_range()
    {
        (byte[] packet, uint range) = EncodeHybridPacket(OpusBandwidth.Fullband, redundancy: false, celtToSilk: false, frame: 0);

        OpusToc toc = OpusToc.Parse(packet[0]);
        toc.Mode.Should().Be(OpusMode.Hybrid);
        toc.Bandwidth.Should().Be(OpusBandwidth.Fullband);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        decoder.Decode(packet, pcm).Should().Be(FrameSamples);
        decoder.FinalRange.Should().Be(range);
    }

    [Fact]
    public void A_super_wideband_hybrid_frame_decodes_and_matches_the_final_range()
    {
        (byte[] packet, uint range) = EncodeHybridPacket(OpusBandwidth.SuperWideband, false, false, frame: 0);

        OpusToc.Parse(packet[0]).Bandwidth.Should().Be(OpusBandwidth.SuperWideband);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        decoder.Decode(packet, pcm).Should().Be(FrameSamples);
        decoder.FinalRange.Should().Be(range);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hybrid_redundancy_decodes_and_matches_the_final_range(bool celtToSilk)
    {
        (byte[] packet, uint range) = EncodeHybridPacket(
            OpusBandwidth.Fullband, redundancy: true, celtToSilk, frame: 1);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        decoder.Decode(packet, pcm).Should().Be(FrameSamples);
        decoder.FinalRange.Should().Be(range);
    }

    [Fact]
    public void Opus_demo_decodes_a_hybrid_stream_without_a_range_mismatch()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        var packets = new List<(byte[] Packet, uint Range)>();
        for (int i = 0; i < 8; i++)
        {
            (byte[] packet, uint range) = EncodeHybridPacket(
                OpusBandwidth.Fullband, redundancy: i == 4, celtToSilk: i == 5, frame: i);
            packets.Add((packet, range));
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-hybrid-{Guid.NewGuid():N}.bit");
        try
        {
            EncoderCorpus.WriteBitFile(path, packets);
            (short[] decoded, string diagnostics) = EncoderOracle.Decode(path);
            EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                $"opus_demo reported a range mismatch:\n{diagnostics}");
            decoded.Length.Should().Be(packets.Count * FrameSamples);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static (byte[] Packet, uint Range) EncodeHybridPacket(
        OpusBandwidth bandwidth, bool redundancy, bool celtToSilk, int frame)
    {
        var silk = new SilkEncoderCore(48000, 16000, 20, Bitrate, 5, useCbr: false);
        var celt = new CeltEncoder(OpusFrameDuration.Ms20, Bitrate, 5, channels: 1, 48000, useVbr: false, constrainedVbr: false);
        var redundant = new CeltRedundantEncoder(Bitrate, 5);

        short[] pcm = EncoderTestSignals.Tone((frame + 2) * FrameSamples, 300, 6000);
        var payload = new byte[1274];
        int endBand = bandwidth == OpusBandwidth.Fullband ? 21 : 19;
        int bytes = OpusHybridEncoder.Encode(
            silk, celt, redundant, pcm.AsSpan(frame * FrameSamples, FrameSamples), 48000, FrameSamples,
            Bitrate, payload.Length, endBand, redundancy, celtToSilk, payload, out uint range);

        byte toc = OpusTocBuilder.Build(OpusMode.Hybrid, bandwidth, OpusFrameDuration.Ms20, stereo: false);
        var packet = new byte[bytes + 2];
        int written = OpusPacketWriter.Write(packet, toc, [bytes], payload.AsSpan(0, bytes));
        uint expectedRange = range;
        return (packet.AsSpan(0, written).ToArray(), expectedRange);
    }
}
