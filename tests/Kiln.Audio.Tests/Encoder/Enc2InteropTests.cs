using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>
/// G1 for the extended encoder: mono and stereo, every API rate, CBR and VBR, 20 ms and 10 ms
/// frames. The non-oracle part checks that our decoder consumes every packet and reproduces the
/// encoder's final range; the oracle part asks <c>opus_demo</c> to decode the same packets.
/// </summary>
public class Enc2InteropTests
{
    public static TheoryData<int, int, OpusFrameDuration, bool> Combinations()
    {
        var data = new TheoryData<int, int, OpusFrameDuration, bool>();
        foreach (int channels in new[] { 1, 2 })
        {
            foreach (int rate in new[] { 8000, 12000, 16000, 24000, 48000 })
            {
                foreach (OpusFrameDuration duration in new[] { OpusFrameDuration.Ms10, OpusFrameDuration.Ms20 })
                {
                    foreach (bool vbr in new[] { false, true })
                    {
                        data.Add(channels, rate, duration, vbr);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Our_decoder_agrees_with_the_encoder(
        int channels, int rate, OpusFrameDuration duration, bool vbr)
    {
        int frameSamples = FrameSamples(rate, duration);
        short[] pcm = BuildSignal(channels, rate, duration, frames: 25);
        var packets = Encode(pcm, channels, rate, duration, vbr);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = rate, Channels = channels });
        var decoded = new short[frameSamples * channels];
        foreach ((byte[] packet, uint expectedRange) in packets)
        {
            int count = decoder.Decode(packet, decoded);
            count.Should().Be(frameSamples);
            decoder.FinalRange.Should().Be(expectedRange);
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void OpusDemo_decodes_every_packet(
        int channels, int rate, OpusFrameDuration duration, bool vbr)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        short[] pcm = BuildSignal(channels, rate, duration, frames: 25);
        var packets = Encode(pcm, channels, rate, duration, vbr);

        byte[] bit = EncoderCorpus.WriteBitFileToBytes(packets);
        (short[] decoded, string diagnostics) = EncoderOracle.Decode(bit, channels);
        EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
            $"{channels}ch, {rate} Hz, {duration}, vbr={vbr}: {diagnostics}");
        // opus_demo decodes at 48 kHz, so every frame is 480 (10 ms) or 960 (20 ms) samples.
        int decodedFrameSamples = duration == OpusFrameDuration.Ms10 ? 480 : 960;
        decoded.Length.Should().Be(packets.Count * decodedFrameSamples * channels);
    }

    private static int FrameSamples(int rate, OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms10 => rate / 100,
        OpusFrameDuration.Ms20 => rate / 50,
        _ => throw new ArgumentOutOfRangeException(nameof(duration)),
    };

    private static List<(byte[] Packet, uint Range)> Encode(
        short[] pcm, int channels, int rate, OpusFrameDuration duration, bool vbr)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            SampleRate = rate,
            Channels = channels,
            BitrateBps = channels == 2 ? 64000 : 32000,
            FrameDuration = duration,
            Complexity = 5,
            UseVbr = vbr,
        };

        int frameSamples = FrameSamples(rate, duration) * channels;
        using var encoder = new OpusEncoder(options);
        var packets = new List<(byte[], uint)>();
        var packet = new byte[1276];
        for (int offset = 0; offset + frameSamples <= pcm.Length; offset += frameSamples)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, frameSamples), packet);
            packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        packets.Should().NotBeEmpty();
        return packets;
    }

    private static short[] BuildSignal(int channels, int rate, OpusFrameDuration duration, int frames)
    {
        int perChannel = FrameSamples(rate, duration) * frames;
        var pcm = new short[perChannel * channels];
        for (int i = 0; i < perChannel; i++)
        {
            short left = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            if (channels == 1)
            {
                pcm[i] = left;
            }
            else
            {
                pcm[2 * i] = left;
                pcm[(2 * i) + 1] = (short)(7000 * Math.Sin(2 * Math.PI * 660 * i / rate));
            }
        }

        return pcm;
    }
}