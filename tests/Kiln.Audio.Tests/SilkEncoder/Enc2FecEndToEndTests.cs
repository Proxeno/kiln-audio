using FluentAssertions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// End-to-end in-band FEC: a Voip mono 16 kHz stream with <c>UseInbandFec</c> and 10% loss must
/// carry LBRR on its SILK packets, and decoding the packet after a drop with <c>decodeFec</c> must
/// recover the lost interval better than concealment.
/// </summary>
public class Enc2FecEndToEndTests
{
    [Fact]
    public void Fec_recovers_a_dropped_frame_better_than_plc()
    {
        const int FrameSamples = 320; // 20 ms at 16 kHz, mono
        short[] source = CreateSpeech(FrameSamples * 120);

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 16000,
            Channels = 1,
            Bandwidth = OpusBandwidth.Wideband,
            BitrateBps = 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
            UseInbandFec = true,
            PacketLossPercent = 10,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        var packets = new List<byte[]>();
        for (int offset = 0; offset + FrameSamples <= source.Length; offset += FrameSamples)
        {
            int written = encoder.Encode(source.AsSpan(offset, FrameSamples), packet);
            packets.Add(packet.AsSpan(0, written).ToArray());
        }

        packets.Should().NotBeEmpty();
        int withLbrr = 0;
        foreach (byte[] encoded in packets)
        {
            OpusToc toc = OpusToc.Parse(encoded[0]);
            if (toc.Mode == OpusMode.Silk && OpusPacket.HasLbrr(encoded))
            {
                withLbrr++;
            }
        }

        withLbrr.Should().BeGreaterThan(0, "a FEC stream above the LBRR rate threshold must carry LBRR");

        // Drop a frame whose successor actually carries its LBRR copy, and recover it from that
        // packet.
        int recoverPacket = -1;
        for (int i = 1; i < packets.Count; i++)
        {
            if (OpusPacket.HasLbrr(packets[i]))
            {
                recoverPacket = i;
                break;
            }
        }

        recoverPacket.Should().BeGreaterThan(0, "the stream must carry at least one LBRR frame");
        int Dropped = recoverPacket - 1;

        using var fecDecoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 16000, Channels = 1 });
        var fecOut = new short[FrameSamples];
        for (int i = 0; i < Dropped; i++)
        {
            fecDecoder.Decode(packets[i], new short[FrameSamples]);
        }

        fecDecoder.Decode(packets[recoverPacket], fecOut, decodeFec: true);
        fecDecoder.LastFecOutcome.Should().Be(OpusFecOutcome.Recovered);

        using var plcDecoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 16000, Channels = 1 });
        for (int i = 0; i <= Dropped; i++)
        {
            plcDecoder.Decode(packets[i], new short[FrameSamples]);
        }

        var plcOut = new short[FrameSamples];
        plcDecoder.DecodeLoss(plcOut);

        double fecSnr = Snr(source.AsSpan(Dropped * FrameSamples, FrameSamples), fecOut);
        double plcSnr = Snr(source.AsSpan(Dropped * FrameSamples, FrameSamples), plcOut);
        fecSnr.Should().BeGreaterThan(plcSnr, $"FEC ({fecSnr:F2} dB) must beat PLC ({plcSnr:F2} dB)");
    }

    private static short[] CreateSpeech(int samples)
    {
        var rng = new Random(20261127);
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / 16000.0;
            double envelope = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 2.3 * t));
            double voiced = 9000 * envelope * Math.Sin(2 * Math.PI * 170 * t);
            pcm[i] = (short)Math.Clamp(voiced + ((rng.NextDouble() - 0.5) * 2500), short.MinValue, short.MaxValue);
        }

        return pcm;
    }

    private static double Snr(ReadOnlySpan<short> reference, short[] decoded)
    {
        double signal = 0;
        double noise = 0;
        for (int i = 0; i < reference.Length && i < decoded.Length; i++)
        {
            double sample = reference[i];
            double error = sample - decoded[i];
            signal += sample * sample;
            noise += error * error;
        }

        return 10 * Math.Log10((signal + 1) / (noise + 1));
    }
}
