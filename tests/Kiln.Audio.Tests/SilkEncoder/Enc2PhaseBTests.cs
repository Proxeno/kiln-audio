using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Phase B gates: stereo/FEC/DTX interop (G1), FEC recovery beating PLC, the delayed-decision
/// quality gain, and zero allocations (G3).
/// </summary>
public class Enc2PhaseBTests
{
    private static readonly Lazy<short[]> StereoSpeech = new(() => Pcm16.Read(TestVectors.DecPath(2)));

    /// <summary>A deterministic, amplitude-panned stereo signal at a level where SNR is meaningful.</summary>
    private static readonly Lazy<short[]> SyntheticStereo = new(() => CreateSyntheticStereo(150));

    private static short[] CreateSyntheticStereo(int frames)
    {
        const int FrameSamples = 1920;
        var rng = new Random(20261126);
        var data = new short[frames * FrameSamples];
        for (int i = 0; i < data.Length / 2; i++)
        {
            double t = i / 48000.0;
            double envelope = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 2.3 * t));
            double voiced = 9000 * envelope * Math.Sin(2 * Math.PI * 170 * t);
            double left = voiced + ((rng.NextDouble() - 0.5) * 2500);
            double right = (0.55 * voiced) + ((rng.NextDouble() - 0.5) * 2500);
            data[2 * i] = (short)Math.Clamp(left, short.MinValue, short.MaxValue);
            data[(2 * i) + 1] = (short)Math.Clamp(right, short.MinValue, short.MaxValue);
        }

        return data;
    }

    public static TheoryData<OpusBandwidth, OpusFrameDuration, int> StereoCases()
    {
        var data = new TheoryData<OpusBandwidth, OpusFrameDuration, int>();
        foreach (OpusBandwidth bandwidth in new[] { OpusBandwidth.Narrowband, OpusBandwidth.Wideband })
        {
            foreach (OpusFrameDuration duration in new[]
                     {
                         OpusFrameDuration.Ms10, OpusFrameDuration.Ms20, OpusFrameDuration.Ms60,
                     })
            {
                data.Add(bandwidth, duration, bandwidth == OpusBandwidth.Narrowband ? 12000 : 24000);
            }
        }

        return data;
    }

    /// <summary>G1: stereo SILK packets decode in libopus and our decoder matches the final range.</summary>
    [Theory]
    [MemberData(nameof(StereoCases))]
    public void Stereo_packets_interop(OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        List<(byte[] Packet, uint Range)> packets = EncodeStereo(
            StereoSpeech.Value, bandwidth, duration, bitrate,
            useInbandFec: false, packetLossPercent: 0, useDtx: false, complexity: 5);

        packets.Should().NotBeEmpty();
        foreach ((byte[] packet, uint range) in packets)
        {
            (packet[0] >> 3).Should().BeLessThan(12, "the TOC must be SILK-only");
            (packet[0] & 0x04).Should().NotBe(0, "the stereo flag must be set");
        }

        int ms = duration switch
        {
            OpusFrameDuration.Ms10 => 10,
            OpusFrameDuration.Ms20 => 20,
            _ => 60,
        };
        int frameSamples = 48000 * ms / 1000;
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new short[frameSamples * 2];
        foreach ((byte[] packet, uint expectedRange) in packets)
        {
            decoder.Decode(packet, pcm).Should().Be(frameSamples);
            decoder.FinalRange.Should().Be(expectedRange);
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-silk-enc2-{Guid.NewGuid():N}.bit");
        try
        {
            WriteBitFile(path, packets);
            short[] decoded = LibOpusOracle.Decode(path, 48000, 2);
            decoded.Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>G1: FEC on with 10% loss still interops.</summary>
    [Fact]
    public void Fec_packets_interop()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        // 64 kbps stereo gives each SILK channel 32 kbps, above the 18.4 kbps LBRR threshold.
        List<(byte[] Packet, uint Range)> packets = EncodeStereo(
            SyntheticStereo.Value, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, 64000,
            useInbandFec: true, packetLossPercent: 10, useDtx: false, complexity: 5);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new short[960 * 2];
        foreach ((byte[] packet, uint expectedRange) in packets)
        {
            decoder.Decode(packet, pcm).Should().Be(960);
            if (packet.Length > 1)
            {
                decoder.FinalRange.Should().Be(expectedRange);
            }
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-silk-enc2-fec-{Guid.NewGuid():N}.bit");
        try
        {
            WriteBitFile(path, packets);
            LibOpusOracle.Decode(path, 48000, 2).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>DTX packets interop: a 1-byte packet decodes as concealment in libopus.</summary>
    [Fact]
    public void Dtx_packets_interop()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        List<(byte[] Packet, uint Range)> packets = EncodeStereo(
            new short[StereoSpeech.Value.Length], OpusBandwidth.Wideband, OpusFrameDuration.Ms20, 24000,
            useInbandFec: false, packetLossPercent: 0, useDtx: true, complexity: 5);

        packets.Should().Contain(p => p.Packet.Length == DtxPacketLength, "silence must produce DTX packets");

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new short[1920];
        foreach ((byte[] packet, _) in packets)
        {
            decoder.Decode(packet, pcm).Should().Be(960);
        }

        var bitPackets = new List<(byte[] Packet, uint Range)>(packets.Count);
        foreach ((byte[] packet, uint range) in packets)
        {
            // A payload-less frame has no range-coded data, so its decoder range is zero.
            bitPackets.Add((packet, packet.Length == DtxPacketLength ? 0u : range));
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-silk-enc2-dtx-{Guid.NewGuid():N}.bit");
        try
        {
            WriteBitFile(path, bitPackets);
            LibOpusOracle.Decode(path, 48000, 2).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>FEC recovery beats packet loss concealment when packets are dropped.</summary>
    [Fact]
    public void Fec_recovery_beats_plc()
    {
        short[] source = SyntheticStereo.Value;
        List<(byte[] Packet, uint Range)> packets = EncodeStereo(
            source, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, 64000,
            useInbandFec: true, packetLossPercent: 10, useDtx: false, complexity: 5);

        const int Channels = 2;
        const int FrameSamples = 960 * Channels;
        int frameCount = Math.Min(packets.Count, 200);
        source.Length.Should().BeGreaterThanOrEqualTo(frameCount * FrameSamples);

        double fecSnr = DropAndRecover(packets, source, frameCount, FrameSamples, useFec: true);
        double plcSnr = DropAndRecover(packets, source, frameCount, FrameSamples, useFec: false);

        fecSnr.Should().BeGreaterThan(plcSnr, "in-band FEC must recover dropped frames better than PLC");
    }

    /// <summary>Long silence collapses into 1-byte DTX packets after the lead-in.</summary>
    [Fact]
    public void Silence_produces_one_byte_dtx_packets()
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = OpusBandwidth.Wideband,
            BitrateBps = 24000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
            UseDtx = true,
        };

        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var packet = new byte[1276];
        var pcm = new short[960];
        int dtxPackets = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            int written = encoder.Encode(new short[960], packet);
            written.Should().BePositive();
            if (written == 1)
            {
                dtxPackets++;
            }

            decoder.Decode(packet.AsSpan(0, written), pcm).Should().BeGreaterThan(0);
        }

        dtxPackets.Should().BeGreaterThan(30, "most of a 1.2 s silence should be DTX");
    }

    /// <summary>
    /// At complexity 8 the delayed-decision quantizer beats the greedy one; the same complexity
    /// keeps every other encoder choice fixed, so only the quantizer differs.
    /// </summary>
    [Fact]
    public void Delayed_decision_improves_on_greedy()
    {
        short[] mono = Pcm16.Read(TestVectors.DecPath(2));
        double greedy = MeasureCoreSnr(mono, complexity: 8, forceGreedy: true);
        double delayed = MeasureCoreSnr(mono, complexity: 8, forceGreedy: false);

        delayed.Should().BeGreaterThan(greedy, "the trellis must not lose to the greedy quantizer");
    }

    /// <summary>
    /// Re-encodes a mono signal through the SILK core and reports the delay-aligned segmental SNR
    /// at the internal rate, the same metric the SILK quality gate uses.
    /// </summary>
    private static double MeasureCoreSnr(short[] pcm, int complexity, bool forceGreedy)
    {
        const int FrameSamples = 960;
        const int InternalRate = 16000;
        var core = new Kiln.Audio.Silk.Encoder.SilkEncoderCore(48000, InternalRate, 20, 24000, complexity, false);
        if (forceGreedy)
        {
            core.CurrentState.NStatesDelayedDecision = 1;
            core.CurrentState.WarpingQ16 = 0;
        }

        // Use a level where the metric is meaningful rather than the corpus's near-silence.
        var amplified = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            amplified[i] = (short)Math.Clamp(pcm[i] * 400, short.MinValue, short.MaxValue);
        }

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var packet = new byte[1276];
        var decoded = new short[FrameSamples];
        int frames = Math.Min(150, amplified.Length / FrameSamples);
        var output = new List<short>(frames * FrameSamples);
        for (int frame = 0; frame < frames; frame++)
        {
            // RFC 6716 §3.1, Table 2: config 9 is SILK-only wideband 20 ms.
            packet[0] = 9 << 3;
            int payload = core.Encode(amplified.AsSpan(frame * FrameSamples, FrameSamples), packet.AsSpan(1));
            decoder.Decode(packet.AsSpan(0, payload + 1), decoded);
            foreach (short sample in decoded)
            {
                output.Add(sample);
            }
        }

        short[] reference = DownsampleToInternal(amplified, InternalRate, frames);
        short[] decodedRate = DownsampleToInternal(output.ToArray(), InternalRate, frames);
        return SegmentalSnr(reference, decodedRate, InternalRate / 50);
    }

    private static short[] DownsampleToInternal(short[] mono, int internalRate, int frames)
    {
        var resampler = new Kiln.Audio.Silk.Encoder.SilkInputResampler(48000, internalRate);
        var output = new short[internalRate / 50];
        var result = new short[frames * output.Length];
        for (int frame = 0; frame < frames; frame++)
        {
            resampler.Process(mono.AsSpan(frame * 960, 960), output);
            output.CopyTo(result.AsSpan(frame * output.Length));
        }

        return result;
    }

    private static double SegmentalSnr(short[] reference, short[] decoded, int segment)
    {
        int delay = FindDelay(reference, decoded);
        double total = 0;
        long count = 0;
        int first = Math.Max(0, -delay);
        int limit = Math.Min(reference.Length, decoded.Length - delay);
        for (int start = first; start + segment <= limit; start += segment)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < segment; i++)
            {
                double sample = reference[start + i];
                double error = decoded[start + i + delay] - sample;
                signal += sample * sample;
                noise += error * error;
            }

            double snr = signal <= 0
                ? (noise <= 0 ? 60 : -10)
                : (noise <= 0 ? 60 : 10 * Math.Log10(signal / noise));
            total += Math.Clamp(snr, -10, 60);
            count++;
        }

        return count == 0 ? -10 : total / count;
    }

    private static int FindDelay(short[] reference, short[] decoded)
    {
        const int Maximum = 400;
        int best = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int delay = -Maximum; delay <= Maximum; delay += 4)
        {
            double correlation = 0;
            int limit = Math.Min(reference.Length, decoded.Length);
            for (int i = Maximum; i + Maximum < limit; i += 4)
            {
                int j = i + delay;
                if (j < 0 || j >= decoded.Length)
                {
                    continue;
                }

                correlation += (double)reference[i] * decoded[j];
            }

            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                best = delay;
            }
        }

        return best;
    }

    /// <summary>G3: encoding a frame allocates nothing.</summary>
    [Fact]
    public void Encoding_allocates_nothing()
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 2,
            Bandwidth = OpusBandwidth.Wideband,
            BitrateBps = 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 8,
            UseInbandFec = true,
            PacketLossPercent = 10,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        short[] source = StereoSpeech.Value;
        var frame = new short[960 * 2];
        source.AsSpan(0, frame.Length).CopyTo(frame);
        // Warm up past tiered JIT, whose compilation allocates on this thread.
        for (int i = 0; i < 30; i++)
        {
            encoder.Encode(frame, packet);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            encoder.Encode(frame, packet);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    private static List<(byte[] Packet, uint Range)> EncodeStereo(
        short[] stereo, OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate,
        bool useInbandFec, int packetLossPercent, bool useDtx, int complexity)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 2,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = duration,
            Complexity = complexity,
            UseInbandFec = useInbandFec,
            PacketLossPercent = packetLossPercent,
            UseDtx = useDtx,
        };

        int ms = duration switch
        {
            OpusFrameDuration.Ms10 => 10,
            OpusFrameDuration.Ms20 => 20,
            _ => 60,
        };
        int frameSamples = 48000 * ms / 1000 * 2;
        using var encoder = new OpusEncoder(options);
        var packets = new List<(byte[], uint)>();
        var packet = new byte[1276];
        for (int offset = 0; offset + frameSamples <= stereo.Length; offset += frameSamples)
        {
            int written = encoder.Encode(stereo.AsSpan(offset, frameSamples), packet);
            packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        return packets;
    }

    private static double DropAndRecover(
        List<(byte[] Packet, uint Range)> packets, short[] source, int frameCount, int frameSamples, bool useFec)
    {
        const int DropEvery = 5;
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new short[frameSamples];
        var reference = new short[frameCount * frameSamples];
        var output = new short[frameCount * frameSamples];
        double signal = 0;
        double noise = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            source.AsSpan(frame * frameSamples, frameSamples)
                .CopyTo(reference.AsSpan(frame * frameSamples));
            bool lost = frame % DropEvery == DropEvery - 1;
            if (lost)
            {
                // The next packet carries this frame's LBRR copy; decode it as FEC.
                if (useFec && frame + 1 < frameCount)
                {
                    decoder.Decode(packets[frame + 1].Packet, pcm, decodeFec: true);
                }
                else
                {
                    decoder.DecodeLoss(pcm);
                }
            }
            else
            {
                decoder.Decode(packets[frame].Packet, pcm);
            }

            pcm.AsSpan().CopyTo(output.AsSpan(frame * frameSamples));
        }

        for (int i = 0; i < reference.Length; i++)
        {
            double sample = reference[i];
            signal += sample * sample;
            double error = sample - output[i];
            noise += error * error;
        }

        return 10 * Math.Log10((signal + 1) / (noise + 1));
    }

    private const int DtxPacketLength = 1;

    private static void WriteBitFile(string path, IReadOnlyList<(byte[] Packet, uint Range)> packets)
    {
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[8];
        foreach ((byte[] packet, uint range) in packets)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], packet.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[4..], range);
            stream.Write(header);
            stream.Write(packet);
        }
    }
}
