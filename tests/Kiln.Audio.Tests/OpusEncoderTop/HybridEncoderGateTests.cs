using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Encoder;
using Kiln.Audio.Silk.Encoder;
using Kiln.Audio.Tests.Encoder;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Phase B gates: automatic mode selection across bitrates and applications, clean decoding with
/// matching final ranges, and zero allocations.
/// </summary>
public class HybridEncoderGateTests
{
    private const int FrameSamples = 960;

    public static TheoryData<int, OpusApplication> Rates()
    {
        var data = new TheoryData<int, OpusApplication>();
        foreach (int bitrate in new[] { 12000, 16000, 24000, 32000, 64000 })
        {
            data.Add(bitrate, OpusApplication.Voip);
            data.Add(bitrate, OpusApplication.Audio);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void G1_streams_decode_and_match_the_final_range(int bitrate, OpusApplication application)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        foreach ((int vector, short[] mono) in CorpusFrames())
        {
            var options = new OpusEncoderOptions
            {
                Application = application,
                BitrateBps = bitrate,
                FrameDuration = OpusFrameDuration.Ms20,
                Complexity = 5,
            };

            var packets = new List<(byte[] Packet, uint Range)>();
            using (var encoder = new OpusEncoder(options))
            {
                var packet = new byte[1276];
                for (int offset = 0; offset + FrameSamples <= mono.Length; offset += FrameSamples)
                {
                    int written = encoder.Encode(mono.AsSpan(offset, FrameSamples), packet);
                    packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
                }
            }

            packets.Should().NotBeEmpty();

            using (var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 }))
            {
                var pcm = new short[FrameSamples];
                for (int i = 0; i < packets.Count; i++)
                {
                    (byte[] packet, uint range) = packets[i];
                    OpusMode mode = OpusToc.Parse(packet[0]).Mode;
                    decoder.Decode(packet, pcm).Should().Be(FrameSamples, $"vector {vector}");
                    decoder.FinalRange.Should().Be(range,
                        $"vector {vector}, {bitrate} bps, {application}, frame {i}, {mode}: the decoder range must match the encoder's");
                }
            }

            string path = Path.Combine(Path.GetTempPath(), $"kiln-g1b-{Guid.NewGuid():N}.bit");
            try
            {
                EncoderCorpus.WriteBitFile(path, packets);
                (short[] decoded, string diagnostics) = EncoderOracle.Decode(path);
                EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                    $"vector {vector}, {bitrate} bps, {application}: opus_demo reported a range mismatch:\n{diagnostics}");
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
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void G1_a_real_silk_transition_frame_decodes_with_a_matching_range(bool celtToSilk)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        var silk = new SilkEncoderCore(48000, 16000, 20, 24000, 5, useCbr: false);
        var redundant = new CeltRedundantEncoder(24000, 5);
        short[] pcm = EncoderTestSignals.Tone(FrameSamples, 300, 6000);
        var payload = new byte[1275];
        int bytes = OpusTransitionEncoder.EncodeSilkOnlyFrame(
            new OpusEncoderSilkAdapter(silk), redundant, pcm, FrameSamples, 24000, payload.Length,
            OpusBandwidth.Wideband, redundancy: true, celtToSilk, payload, out uint range);

        byte toc = OpusTocBuilder.Build(OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false);
        var packet = new byte[bytes + 2];
        int written = OpusPacketWriter.Write(packet, toc, [bytes], payload.AsSpan(0, bytes));

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var decoded = new short[FrameSamples];
        decoder.Decode(packet.AsSpan(0, written), decoded).Should().Be(FrameSamples);
        decoder.FinalRange.Should().Be(range, $"celtToSilk={celtToSilk}: the redundant frame's XOR must match");

        string path = Path.Combine(Path.GetTempPath(), $"kiln-trans-{Guid.NewGuid():N}.bit");
        try
        {
            EncoderCorpus.WriteBitFile(path, [(packet.AsSpan(0, written).ToArray(), range)]);
            (_, string diagnostics) = EncoderOracle.Decode(path);
            EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(diagnostics);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class OpusEncoderSilkAdapter(SilkEncoderCore core) : ISilkLayerEncoder
    {
        public void Encode(ref Kiln.Audio.Entropy.RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes) =>
            core.EncodeInto(ref encoder, pcm, maxFrameBytes + 1);
    }

    [Theory]
    [InlineData(12000, OpusApplication.Voip, (int)OpusMode.Silk)]
    [InlineData(24000, OpusApplication.Voip, (int)OpusMode.Hybrid)]
    [InlineData(64000, OpusApplication.Audio, (int)OpusMode.Celt)]
    public void G3_automatic_selection_allocates_nothing(int bitrate, OpusApplication application, int expectedMode)
    {
        OpusMode expected = (OpusMode)expectedMode;
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

        var options = new OpusEncoderOptions
        {
            Application = application,
            BitrateBps = bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        OpusToc.Parse(EncodeOne(encoder, mono, packet)).Mode.Should().Be(expected);

        Run(encoder, mono, packet);
        Run(encoder, mono, packet);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(encoder, mono, packet);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "automatic mode selection must not allocate per frame");

        static byte EncodeOne(OpusEncoder encoder, short[] mono, byte[] packet)
        {
            encoder.Encode(mono.AsSpan(0, FrameSamples), packet);
            return packet[0];
        }

        static void Run(OpusEncoder encoder, short[] mono, byte[] packet)
        {
            for (int offset = 0; offset + FrameSamples <= mono.Length; offset += FrameSamples)
            {
                encoder.Encode(mono.AsSpan(offset, FrameSamples), packet);
            }
        }
    }

    private static IEnumerable<(int Vector, short[] Mono)> CorpusFrames()
    {
        for (int vector = 1; vector <= 3; vector++)
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(vector));
            int frames = Math.Min(stereo.Length / 2, 50 * FrameSamples);
            var mono = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
            }

            yield return (vector, mono);
        }
    }
}
