using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// The user's bandwidth cap must reach the hybrid CELT end band: an SWB-capped stream codes no
/// energy above 12 kHz and signals SWB in the TOC; an FB-capped stream codes up to 20 kHz.
/// </summary>
public class HybridBandwidthCapTests
{
    private const int FrameSamples = 960;

    [Fact]
    public void A_super_wideband_cap_bounds_the_celt_end_band()
    {
        // A voiced 300 Hz body keeps the mode policy in SILK/hybrid (a noise-only input is
        // classified as music and selects CELT at 32 kbps), with a 16 kHz probe above the SWB
        // limit to reveal the coded band range.
        short[] pcm = ToneMix(20 * FrameSamples);

        List<byte[]> swbPackets = Encode(pcm, OpusBandwidth.SuperWideband);
        List<byte[]> fbPackets = Encode(pcm, OpusBandwidth.Fullband);

        OpusToc.Parse(swbPackets[5][0]).Mode.Should().Be(OpusMode.Hybrid);
        OpusToc.Parse(swbPackets[5][0]).Bandwidth.Should().Be(OpusBandwidth.SuperWideband);
        OpusToc.Parse(fbPackets[5][0]).Bandwidth.Should().Be(OpusBandwidth.Fullband);

        // A different coded band range must produce different packets.
        swbPackets[5].Should().NotEqual(fbPackets[5],
            "a 12 kHz band limit and a 20 kHz band limit cannot produce the same hybrid packets");

        short[] swbDecoded = Decode(swbPackets);
        short[] fbDecoded = Decode(fbPackets);

        double swbHigh = HighBandEnergy(swbDecoded, 14000);
        double fbHigh = HighBandEnergy(fbDecoded, 14000);
        double swbLow = HighBandEnergy(swbDecoded, 1000);

        fbHigh.Should().BeGreaterThan(
            swbHigh * 8,
            $"the FB stream must carry content above 12 kHz that the SWB stream cannot (SWB {swbHigh:F1}, FB {fbHigh:F1})");
        swbHigh.Should().BeLessThan(
            swbLow * 0.05,
            $"an SWB-capped stream must have almost no energy above 12 kHz (high {swbHigh:F1}, low {swbLow:F1})");
    }

    private static List<byte[]> Encode(short[] pcm, OpusBandwidth bandwidth)
    {
        using var encoder = new OpusEncoder(new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        });

        var packets = new List<byte[]>();
        var packet = new byte[1276];
        for (int offset = 0; offset + FrameSamples <= pcm.Length; offset += FrameSamples)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, FrameSamples), packet);
            packets.Add(packet.AsSpan(0, written).ToArray());
        }

        return packets;
    }

    private static short[] Decode(List<byte[]> packets)
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var all = new List<short>();
        var frame = new short[FrameSamples];
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet, frame).Should().Be(FrameSamples);
            all.AddRange(frame);
        }

        return all.ToArray();
    }

    /// <summary>Goertzel power at a frequency, over samples after the first five frames.</summary>
    private static double HighBandEnergy(short[] pcm, double frequency)
    {
        int start = 5 * FrameSamples;
        int count = pcm.Length - start;
        double w = 2 * Math.PI * frequency / 48000;
        double coefficient = 2 * Math.Cos(w);
        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < count; i++)
        {
            double s0 = pcm[start + i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        double power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return power / count / 32768.0 / 32768.0;
    }

    private static short[] ToneMix(int samples)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)((6000 * Math.Sin(2 * Math.PI * 300 * i / 48000.0))
                + (1500 * Math.Sin(2 * Math.PI * 16000 * i / 48000.0)));
        }

        return pcm;
    }
}
