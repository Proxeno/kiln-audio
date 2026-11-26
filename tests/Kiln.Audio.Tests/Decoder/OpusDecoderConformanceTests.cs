using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Decoder;

/// <summary>
/// RFC 8251 conformance of the public <see cref="OpusDecoder"/> API: every test vector at
/// 48 kHz stereo through <see cref="OpusDecoder.Decode(ReadOnlySpan{byte}, Span{short}, bool)"/>,
/// with a 100 % final-range match, plus the API rates a voice client uses.
/// </summary>
/// <remarks>
/// <para>
/// The quality metric is the RFC 6716 §6 / RFC 8251 comparison implemented by
/// <see cref="OpusCompare"/> and by libopus <c>opus_compare</c>; each vector must pass against
/// <c>.dec</c> or the second conformant decode <c>m.dec</c>. The sample-wise SNR against
/// <c>opus_demo</c> is a stricter diagnostic, reported rather than used as the quality gate.
/// </para>
/// <para>
/// All decoding here goes through the frozen public surface; no internal decoder type is used.
/// </para>
/// </remarks>
public class OpusDecoderConformanceTests
{
    private const int ReferenceRate = 48000;

    private readonly ITestOutputHelper _output;

    public OpusDecoderConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The twelve RFC 8251 test vectors.</summary>
    public static TheoryData<int> Vectors()
    {
        var data = new TheoryData<int>();
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            data.Add(vector);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Public_decoder_is_conformant_at_48_khz_stereo(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        DecodedStream decoded = DecodeFile(vector, ReferenceRate, channels: 2);
        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        short[] modified = Pcm16.Read(TestVectors.ModifiedDecPath(vector));

        decoded.Pcm.Length.Should().Be(
            reference.Length,
            "vector {0:D2}: the decoder must emit one sample per reference sample",
            vector);

        uint[] expectedRanges = ReadExpectedRanges(vector);
        int firstBadPacket = FirstRangeMismatch(decoded.Ranges, expectedRanges);
        firstBadPacket.Should().Be(
            -1,
            "vector {0:D2}: final range must match for every packet; first mismatch: {1}",
            vector,
            firstBadPacket < 0 ? "" : DescribePacket(vector, firstBadPacket, decoded.Ranges[firstBadPacket], expectedRanges[firstBadPacket]));

        OpusCompare.Result managedReference = OpusCompare.Compare(reference, decoded.Pcm, ReferenceRate, stereo: true);
        OpusCompare.Result managedModified = OpusCompare.Compare(modified, decoded.Pcm, ReferenceRate, stereo: true);
        LibOpusOracle.Comparison nativeReference = LibOpusOracle.Compare(reference, decoded.Pcm, ReferenceRate, stereo: true);
        LibOpusOracle.Comparison nativeModified = LibOpusOracle.Compare(modified, decoded.Pcm, ReferenceRate, stereo: true);

        _output.WriteLine(FormattableString.Invariant($"vector {vector:D2}: quality vs .dec {managedReference.Quality:F2} % (native {nativeReference.Quality:F2} %), vs m.dec {managedModified.Quality:F2} % (native {nativeModified.Quality:F2} %); final range {expectedRanges.Length - CountMismatches(decoded.Ranges, expectedRanges)}/{expectedRanges.Length}"));

        (managedReference.Passed || managedModified.Passed).Should().BeTrue(
            "vector {0:D2}: the RFC 8251 metric must pass against .dec or m.dec (quality {1:F2} % / {2:F2} %)\n{3}",
            vector,
            managedReference.Quality,
            managedModified.Quality,
            DescribeWeakPackets(vector, decoded));

        (nativeReference.Passed || nativeModified.Passed).Should().BeTrue(
            "vector {0:D2}: opus_compare must pass against .dec or m.dec (quality {1:F2} % / {2:F2} %)",
            vector,
            nativeReference.Quality,
            nativeModified.Quality);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Public_decoder_matches_opus_demo_at_48_khz_mono_and_16_khz_mono(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        string bitPath = TestVectors.BitPath(vector);
        foreach (int rate in (int[])[48000, 16000])
        {
            DecodedStream decoded = DecodeFile(vector, rate, channels: 1);
            short[] demo = LibOpusOracle.Decode(bitPath, rate, 1);
            demo.Length.Should().Be(
                decoded.Pcm.Length,
                "vector {0:D2} at {1} Hz mono: the decoder must emit one sample per opus_demo sample",
                vector,
                rate);

            int skip = rate / 100;
            double snr = IdealResampler.Snr(demo, decoded.Pcm, skip);
            int lag = IdealResampler.FindLag(demo, decoded.Pcm, maxLag: 32, skip, stride: 1);
            _output.WriteLine(FormattableString.Invariant(
                $"vector {vector:D2} at {rate} Hz mono: SNR {snr:F2} dB, lag {lag}"));

            snr.Should().BeGreaterThan(
                10,
                "vector {0:D2} at {1} Hz mono must track opus_demo (SNR {2:F2} dB, lag {3})",
                vector,
                rate,
                snr,
                lag);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(12)]
    public void Decode_allocates_nothing_after_warm_up(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        byte[][] packets = ReadPackets(vector);
        var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ReferenceRate, Channels = 2 });
        var pcm = new short[ReferenceRate * 120 / 1000 * 2];

        // Warm up the JIT, the static tables and every code path with one full pass.
        for (int i = 0; i < packets.Length; i++)
        {
            decoder.Decode(packets[i], pcm);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < packets.Length; i++)
        {
            decoder.Decode(packets[i], pcm);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2}: {allocated} bytes allocated over {packets.Length} Decode calls"));
        allocated.Should().Be(0, "Decode must be allocation-free after construction");
    }

    /// <summary>Decodes a whole vector through the public API.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    /// <param name="rate">API sample rate.</param>
    /// <param name="channels">API channel count.</param>
    /// <returns>The decoded PCM and the final range after each packet.</returns>
    private static DecodedStream DecodeFile(int vector, int rate, int channels)
    {
        var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = rate, Channels = channels });
        var pcm = new List<short>();
        var ranges = new List<uint>();
        var buffer = new short[rate * 120 / 1000 * channels];
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            int samples = decoder.Decode(packet, buffer);
            pcm.AddRange(buffer.AsSpan(0, samples * channels));
            ranges.Add(decoder.FinalRange);
        }

        return new DecodedStream([.. pcm], [.. ranges]);
    }

    /// <summary>Reads the encoder's final range for every packet of a vector.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    /// <returns>The final ranges in file order.</returns>
    private static uint[] ReadExpectedRanges(int vector)
    {
        var ranges = new List<uint>();
        foreach ((_, uint finalRange) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            ranges.Add(finalRange);
        }

        return [.. ranges];
    }

    /// <summary>Reads only the packet bytes of a vector.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    /// <returns>The packets in file order.</returns>
    private static byte[][] ReadPackets(int vector)
    {
        var packets = new List<byte[]>();
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            packets.Add(packet);
        }

        return [.. packets];
    }

    /// <summary>Returns the index of the first final-range mismatch, or -1.</summary>
    /// <param name="actual">Decoded final ranges.</param>
    /// <param name="expected">Encoder final ranges.</param>
    /// <returns>The first mismatching packet index, or -1 when all match.</returns>
    private static int FirstRangeMismatch(uint[] actual, uint[] expected)
    {
        int count = Math.Min(actual.Length, expected.Length);
        for (int i = 0; i < count; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }

        return actual.Length == expected.Length ? -1 : count;
    }

    /// <summary>Counts the final-range mismatches between two sequences.</summary>
    /// <param name="actual">Decoded final ranges.</param>
    /// <param name="expected">Encoder final ranges.</param>
    /// <returns>The number of mismatches, including any length difference.</returns>
    private static int CountMismatches(uint[] actual, uint[] expected)
    {
        int mismatches = Math.Abs(actual.Length - expected.Length);
        int count = Math.Min(actual.Length, expected.Length);
        for (int i = 0; i < count; i++)
        {
            if (actual[i] != expected[i])
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    /// <summary>Describes a packet's format for a failure message.</summary>
    /// <param name="vector">Vector number.</param>
    /// <param name="packetIndex">Zero-based packet index.</param>
    /// <param name="actual">Decoded final range.</param>
    /// <param name="expected">Encoder final range.</param>
    /// <returns>A human-readable description.</returns>
    private static string DescribePacket(int vector, int packetIndex, uint actual, uint expected)
    {
        byte[] packet = ReadPackets(vector)[packetIndex];
        OpusToc toc = OpusToc.Parse(packet[0]);
        return FormattableString.Invariant(
            $"packet {packetIndex}, {toc.Mode} {toc.Bandwidth} {toc.FrameDuration} {(toc.IsStereo ? "stereo" : "mono")}, {packet.Length} bytes, decoded 0x{actual:X8}, expected 0x{expected:X8}");
    }

    /// <summary>Finds the first packet whose segment SNR against the reference is poor.</summary>
    /// <param name="vector">Vector number.</param>
    /// <param name="decoded">The decoded stream.</param>
    /// <returns>A diagnostic string.</returns>
    private static string DescribeWeakPackets(int vector, DecodedStream decoded)
    {
        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        int offset = 0;
        int[] frameSamples = PacketSampleCounts(vector, ReferenceRate, decoded.Ranges.Length);
        for (int i = 0; i < frameSamples.Length; i++)
        {
            int count = frameSamples[i] * 2;
            int start = offset + (ReferenceRate / 100 * 2);
            int end = Math.Min(reference.Length, offset + count);
            offset += count;
            if (end <= start)
            {
                continue;
            }

            double signal = 0;
            double error = 0;
            for (int n = start; n < end; n++)
            {
                double difference = reference[n] - decoded.Pcm[n];
                signal += reference[n] * (double)reference[n];
                error += difference * difference;
            }

            double snr = error <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
            if (snr < 20)
            {
                byte[] packet = ReadPackets(vector)[i];
                OpusToc toc = OpusToc.Parse(packet[0]);
                return FormattableString.Invariant(
                    $"first weak packet: {i} ({toc.Mode} {toc.Bandwidth} {toc.FrameDuration}), segment SNR {snr:F2} dB");
            }
        }

        return "no packet segment below 20 dB";
    }

    /// <summary>Returns the number of samples per channel each packet produces.</summary>
    /// <param name="vector">Vector number.</param>
    /// <param name="rate">API sample rate.</param>
    /// <param name="packetCount">Expected number of packets.</param>
    /// <returns>One entry per packet.</returns>
    private static int[] PacketSampleCounts(int vector, int rate, int packetCount)
    {
        var counts = new int[packetCount];
        int index = 0;
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            if (index >= counts.Length)
            {
                break;
            }

            counts[index++] = OpusPacket.GetFrameCount(packet) * OpusPacket.GetSamplesPerFrame(OpusToc.Parse(packet[0]), rate);
        }

        return counts;
    }

    private readonly record struct DecodedStream(short[] Pcm, uint[] Ranges);
}
