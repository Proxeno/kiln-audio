using FluentAssertions;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// End-to-end in-band FEC: a Voip mono 16 kHz stream with <c>UseInbandFec</c> and 10% loss must
/// carry LBRR on its SILK packets, and decoding the packet after a drop with <c>decodeFec</c> must
/// recover the lost interval better than concealment.
/// </summary>
public class Enc2FecEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public Enc2FecEndToEndTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// FEC-QUAL-1 ratchet: a 120-packet stream with 14 drops (every 8th frame), each recovered
    /// from the next packet's LBRR copy. The floors are the values before SILK-QUAL-3 retuned the
    /// high-rate quantizer (mean 3.93 dB, aggregate 5.54 dB) less 0.05 dB of slack; the coverage
    /// floor is the LBRR packet count there. Frames whose successor carries no LBRR are
    /// unrecoverable and concealed, as in a real stream.
    /// </summary>
    [Fact]
    public void Multi_drop_stream_fec_snr_and_lbrr_coverage_hold_the_floor()
    {
        const int FrameSamples = 320;
        const int Frames = 120;
        short[] source = CreateSpeech(FrameSamples * Frames);
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

        int lbrrPackets = packets.Count(p => OpusPacket.HasLbrr(p));
        var dropped = new HashSet<int>();
        for (int d = 8; d <= 112; d += 8)
        {
            dropped.Add(d);
        }

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 16000, Channels = 1 });
        var output = new short[FrameSamples * Frames];
        int recovered = 0;
        for (int i = 0; i < Frames; i++)
        {
            if (dropped.Contains(i))
            {
                // The lost packet never reaches the decoder; the next packet's LBRR recovers it.
                continue;
            }

            var frame = new short[FrameSamples];
            if (dropped.Contains(i - 1))
            {
                var fec = new short[FrameSamples];
                decoder.Decode(packets[i], fec, decodeFec: true);
                if (decoder.LastFecOutcome == OpusFecOutcome.Recovered)
                {
                    recovered++;
                }

                Array.Copy(fec, 0, output, (i - 1) * FrameSamples, FrameSamples);
            }

            decoder.Decode(packets[i], frame);
            Array.Copy(frame, 0, output, i * FrameSamples, FrameSamples);
        }

        double meanSnr = 0;
        double signal = 0;
        double noise = 0;
        foreach (int d in dropped)
        {
            ReadOnlySpan<short> reference = source.AsSpan(d * FrameSamples, FrameSamples);
            meanSnr += Snr(reference, output.AsSpan(d * FrameSamples, FrameSamples).ToArray());
            for (int i = 0; i < FrameSamples; i++)
            {
                double x = reference[i];
                double e = x - output[(d * FrameSamples) + i];
                signal += x * x;
                noise += e * e;
            }
        }

        meanSnr /= dropped.Count;
        double aggregateSnr = 10 * Math.Log10((signal + 1) / (noise + 1));
        _output.WriteLine(
            $"FEC ratchet: lbrrPackets={lbrrPackets}/{packets.Count} recovered={recovered}/{dropped.Count} meanSnr={meanSnr:F2} dB aggregateSnr={aggregateSnr:F2} dB");

        lbrrPackets.Should().BeGreaterThanOrEqualTo(84, "LBRR coverage must not shrink");
        recovered.Should().BeGreaterThanOrEqualTo(9);
        meanSnr.Should().BeGreaterThanOrEqualTo(3.93 - 0.05);
        aggregateSnr.Should().BeGreaterThanOrEqualTo(5.54 - 0.05);
    }

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
