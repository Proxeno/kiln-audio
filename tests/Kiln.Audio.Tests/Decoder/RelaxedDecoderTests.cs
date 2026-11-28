using FluentAssertions;
using Kiln.Audio.Celt;
using Kiln.Audio.Simd.Kernels;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Decoder;

/// <summary>
/// The <see cref="OpusPerformanceMode.Relaxed"/> decoder: RFC 8251 conformance on all twelve vectors
/// (both reference sets, managed and native <c>opus_compare</c>), agreement with the Precise
/// decoder, loss concealment and in-band FEC, zero allocations, and the accuracy of the
/// approximate kernels on every tier.
/// </summary>
public class RelaxedDecoderTests
{
    private const int Rate = 48000;

    private readonly ITestOutputHelper _output;

    public RelaxedDecoderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int> Vectors()
    {
        var data = new TheoryData<int>();
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            data.Add(vector);
        }

        return data;
    }

    [Fact]
    public void Default_mode_is_precise_and_undefined_modes_are_rejected()
    {
        new OpusDecoderOptions().PerformanceMode.Should().Be(OpusPerformanceMode.Precise);
        new OpusDecoderOptions { PerformanceMode = OpusPerformanceMode.Relaxed }.Validate();

        var bad = new OpusDecoderOptions { PerformanceMode = (OpusPerformanceMode)2 };
        Action validate = bad.Validate;
        validate.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(OpusDecoderOptions.PerformanceMode));
        Action create = () => _ = new OpusDecoder(bad);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Relaxed_decoder_is_rfc_8251_conformant_at_48_khz_stereo(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        (short[] pcm, uint[] ranges) = Decode(vector, Rate, 2, OpusPerformanceMode.Relaxed);
        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        short[] modified = Pcm16.Read(TestVectors.ModifiedDecPath(vector));
        pcm.Length.Should().Be(reference.Length);

        uint[] expected = OpusBitFile.Read(TestVectors.BitPath(vector)).Select(p => p.FinalRange).ToArray();
        ranges.Should().Equal(expected, "vector {0:D2}: the final range is bitstream state and must match exactly", vector);

        OpusCompare.Result managedRef = OpusCompare.Compare(reference, pcm, Rate, stereo: true);
        OpusCompare.Result managedMod = OpusCompare.Compare(modified, pcm, Rate, stereo: true);
        LibOpusOracle.Comparison nativeRef = LibOpusOracle.Compare(reference, pcm, Rate, stereo: true);
        LibOpusOracle.Comparison nativeMod = LibOpusOracle.Compare(modified, pcm, Rate, stereo: true);
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2} Relaxed: vs .dec {managedRef.Quality:F2} % (native {nativeRef.Quality:F2} %), vs m.dec {managedMod.Quality:F2} % (native {nativeMod.Quality:F2} %)"));

        (managedRef.Passed || managedMod.Passed).Should().BeTrue("vector {0:D2}: managed RFC 8251 metric", vector);
        (nativeRef.Passed || nativeMod.Passed).Should().BeTrue("vector {0:D2}: native opus_compare", vector);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Relaxed_output_stays_close_to_precise_at_48_khz_stereo_and_mono(int vector)
    {
        foreach (int channels in (int[])[2, 1])
        {
            (short[] precise, uint[] preciseRanges) = Decode(vector, Rate, channels, OpusPerformanceMode.Precise);
            (short[] relaxed, uint[] relaxedRanges) = Decode(vector, Rate, channels, OpusPerformanceMode.Relaxed);
            relaxed.Length.Should().Be(precise.Length);
            relaxedRanges.Should().Equal(preciseRanges);

            double snr = Snr(precise, relaxed);
            int maxDifference = 0;
            for (int i = 0; i < precise.Length; i++)
            {
                maxDifference = Math.Max(maxDifference, Math.Abs(precise[i] - relaxed[i]));
            }

            _output.WriteLine(FormattableString.Invariant(
                $"vector {vector:D2} {channels} ch: Relaxed vs Precise SNR {snr:F1} dB, max difference {maxDifference} LSB"));
            snr.Should().BeGreaterThanOrEqualTo(90.0, "vector {0:D2} {1} ch", vector, channels);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Silk_only_vectors_are_identical_in_both_modes_and_hybrid_stays_close(int vector)
    {
        // Vector 02 and 04 are SILK-only: integer arithmetic, so Relaxed must be identical.
        if (vector is 2 or 4)
        {
            Decode(vector, Rate, 1, OpusPerformanceMode.Relaxed).Pcm
                .Should().Equal(Decode(vector, Rate, 1, OpusPerformanceMode.Precise).Pcm);
        }
        else
        {
            double snr = Snr(
                Decode(vector, Rate, 1, OpusPerformanceMode.Precise).Pcm,
                Decode(vector, Rate, 1, OpusPerformanceMode.Relaxed).Pcm);
            snr.Should().BeGreaterThanOrEqualTo(90.0);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(11)]
    public void Loss_concealment_with_relaxed_stays_close_to_precise(int vector)
    {
        byte[][] packets = OpusBitFile.Read(TestVectors.BitPath(vector)).Select(p => p.Packet).ToArray();
        short[] precise = DecodeWithLosses(packets, OpusPerformanceMode.Precise);
        short[] relaxed = DecodeWithLosses(packets, OpusPerformanceMode.Relaxed);
        relaxed.Length.Should().Be(precise.Length);
        relaxed.Should().OnlyContain(s => s >= short.MinValue && s <= short.MaxValue);

        double snr = Snr(precise, relaxed);
        _output.WriteLine(FormattableString.Invariant($"vector {vector:D2}: concealed stream, Relaxed vs Precise SNR {snr:F1} dB"));
        snr.Should().BeGreaterThanOrEqualTo(45.0);
    }

    [Fact]
    public void Fec_decode_with_relaxed_recovers_the_same_frames_as_precise()
    {
        int checkedPackets = 0;
        foreach (int vector in (int[])[2, 4, 6, 12])
        {
            using var precise = new OpusDecoder(new OpusDecoderOptions { SampleRate = Rate, Channels = 1 });
            using var relaxed = new OpusDecoder(
                new OpusDecoderOptions { SampleRate = Rate, Channels = 1, PerformanceMode = OpusPerformanceMode.Relaxed });
            var a = new short[Rate * 120 / 1000];
            var b = new short[Rate * 120 / 1000];
            foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
            {
                bool lbrr = OpusPacket.HasLbrr(packet);
                OpusMode mode = OpusToc.Parse(packet[0]).Mode;
                // decodeFec on a packet without LBRR conceals the lost interval instead, so both the
                // recovered and the concealed outcome are covered.
                int n1 = precise.Decode(packet, a, decodeFec: true);
                int n2 = relaxed.Decode(packet, b, decodeFec: true);
                relaxed.LastFecOutcome.Should().Be(precise.LastFecOutcome);
                n2.Should().Be(n1);
                if (mode == OpusMode.Silk)
                {
                    b.AsSpan(0, n2).ToArray().Should().Equal(a.AsSpan(0, n1).ToArray());
                }
                else
                {
                    Snr(a.AsSpan(0, n1).ToArray(), b.AsSpan(0, n2).ToArray()).Should().BeGreaterThan(40.0);
                }

                if (lbrr)
                {
                    checkedPackets++;
                }

                // Decode again normally so both decoders stay in step with the stream.
                precise.Decode(packet, a);
                relaxed.Decode(packet, b);
            }
        }

        _output.WriteLine($"{checkedPackets} packets with LBRR data decoded with FEC in both modes");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(11)]
    public void Relaxed_decode_allocates_nothing_after_warm_up(int vector)
    {
        byte[][] packets = OpusBitFile.Read(TestVectors.BitPath(vector)).Select(p => p.Packet).ToArray();
        using var decoder = new OpusDecoder(
            new OpusDecoderOptions { SampleRate = Rate, Channels = 2, PerformanceMode = OpusPerformanceMode.Relaxed });
        var pcm = new short[Rate * 120 / 1000 * 2];
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet, pcm);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet, pcm);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(11)]
    public void Relaxed_loss_concealment_and_fec_allocate_nothing(int vector)
    {
        byte[] packet = OpusBitFile.Read(TestVectors.BitPath(vector)).First().Packet;
        using var decoder = new OpusDecoder(
            new OpusDecoderOptions { SampleRate = Rate, Channels = 1, PerformanceMode = OpusPerformanceMode.Relaxed });
        var pcm = new short[Rate * 120 / 1000];
        decoder.Decode(packet, pcm);
        for (int i = 0; i < 8; i++)
        {
            decoder.DecodeLoss(pcm);
            decoder.Decode(packet, pcm, decodeFec: true);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++)
        {
            decoder.DecodeLoss(pcm);
            decoder.Decode(packet, pcm, decodeFec: true);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    // ---- kernel accuracy and tier parity ----

    [Fact]
    public void Exp2_approximation_is_accurate_across_the_decoder_range()
    {
        double worst = 0;
        for (double x = -60; x <= 32; x += 0.00731)
        {
            float input = (float)x;
            double expected = Math.Pow(2, input);
            double actual = RelaxedMath.Exp2(input);
            worst = Math.Max(worst, Math.Abs((actual / expected) - 1));
        }

        _output.WriteLine($"Exp2 worst relative error {worst:E2}");
        worst.Should().BeLessThan(4e-7);
    }

    [Theory]
    [InlineData((int)AudioKernelTier.Scalar)]
    [InlineData((int)AudioKernelTier.Vector128)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64)]
    [InlineData((int)AudioKernelTier.Avx2Fma)]
    public void Fast_reciprocal_square_root_is_accurate_on_every_tier(int tierValue)
    {
        var tier = (AudioKernelTier)tierValue;
        if (!AudioKernels.IsSupported(tier))
        {
            return;
        }

        double worst = tier switch
        {
            AudioKernelTier.Avx2Fma => Rsqrt<Avx2FmaKernels>(),
            AudioKernelTier.AdvSimdArm64 => Rsqrt<AdvSimdArm64Kernels>(),
            AudioKernelTier.Vector128 => Rsqrt<Vector128Kernels>(),
            _ => Rsqrt<ScalarKernels>(),
        };
        _output.WriteLine($"{tier} FastRsqrt worst relative error {worst:E2}");
        worst.Should().BeLessThan(5e-7);
    }

    [Theory]
    [InlineData((int)AudioKernelTier.Vector128)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64)]
    [InlineData((int)AudioKernelTier.Avx2Fma)]
    public void Relaxed_synthesis_on_each_tier_matches_scalar_precise_closely(int tierValue)
    {
        var tier = (AudioKernelTier)tierValue;
        if (!AudioKernels.IsSupported(tier))
        {
            return;
        }

        foreach (int vector in (int[])[1, 7, 11])
        {
            float[] reference = CeltPass(vector, AudioKernelTier.Scalar, OpusPerformanceMode.Precise);
            float[] relaxed = CeltPass(vector, tier, OpusPerformanceMode.Relaxed);
            relaxed.Length.Should().Be(reference.Length);
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                signal += (double)reference[i] * reference[i];
                noise += Math.Pow(reference[i] - relaxed[i], 2);
            }

            double snr = 10 * Math.Log10(signal / Math.Max(noise, 1e-30));
            _output.WriteLine($"{tier} Relaxed vs Scalar Precise, vector {vector:D2}: {snr:F1} dB");
            snr.Should().BeGreaterThan(120.0);
        }
    }

    // ---- helpers ----

    private static double Rsqrt<TK>()
        where TK : struct, ICeltKernels
    {
        double worst = 0;
        for (double x = 1e-6; x < 1e7; x *= 1.003)
        {
            double expected = 1 / Math.Sqrt((float)x);
            worst = Math.Max(worst, Math.Abs((TK.FastRsqrt((float)x) / expected) - 1));
        }

        return worst;
    }

    private static float[] CeltPass(int vector, AudioKernelTier tier, OpusPerformanceMode mode)
    {
        var decoder = new CeltDecoder(2, tier, mode);
        var synthesizer = new CeltSynthesizer(2, 1, tier, mode);
        var frame = new CeltFrameResult();
        var output = new List<float>();
        var scratch = new float[2 * CeltTables.MaxFrameSamples];
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            OpusToc toc = OpusToc.Parse(packet[0]);
            if (toc.Mode != OpusMode.Celt || OpusPacket.GetFrameCount(packet) != 1 || packet.Length < 2)
            {
                continue;
            }

            int lm = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms2_5 => 0,
                OpusFrameDuration.Ms5 => 1,
                OpusFrameDuration.Ms10 => 2,
                _ => 3,
            };
            int endBand = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband => 17,
                OpusBandwidth.Wideband => 17,
                OpusBandwidth.SuperWideband => 19,
                _ => 21,
            };
            var range = new Kiln.Audio.Entropy.RangeDecoder(packet.AsSpan(1));
            decoder.DecodeFrame(ref range, packet.Length - 1, lm, 0, endBand, toc.IsStereo ? 2 : 1, frame);
            synthesizer.Synthesize(frame, scratch);
            for (int i = 0; i < frame.SampleCount * 2; i++)
            {
                output.Add(scratch[i]);
            }
        }

        return [.. output];
    }

    private static (short[] Pcm, uint[] Ranges) Decode(int vector, int rate, int channels, OpusPerformanceMode mode)
    {
        using var decoder = new OpusDecoder(
            new OpusDecoderOptions { SampleRate = rate, Channels = channels, PerformanceMode = mode });
        var pcm = new List<short>();
        var ranges = new List<uint>();
        var buffer = new short[rate * 120 / 1000 * channels];
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            int samples = decoder.Decode(packet, buffer);
            pcm.AddRange(buffer.AsSpan(0, samples * channels).ToArray());
            ranges.Add(decoder.FinalRange);
        }

        return ([.. pcm], [.. ranges]);
    }

    private static short[] DecodeWithLosses(byte[][] packets, OpusPerformanceMode mode)
    {
        using var decoder = new OpusDecoder(
            new OpusDecoderOptions { SampleRate = Rate, Channels = 1, PerformanceMode = mode });
        var pcm = new List<short>();
        var buffer = new short[Rate * 120 / 1000];
        for (int i = 0; i < packets.Length; i++)
        {
            int samples = i % 10 == 9 && OpusPacket.GetFrameCount(packets[i]) == 1
                ? decoder.DecodeLoss(buffer)
                : decoder.Decode(packets[i], buffer);
            pcm.AddRange(buffer.AsSpan(0, samples).ToArray());
        }

        return [.. pcm];
    }

    private static double Snr(short[] reference, short[] test)
    {
        double signal = 0;
        double noise = 0;
        int count = Math.Min(reference.Length, test.Length);
        for (int i = 0; i < count; i++)
        {
            double r = reference[i];
            signal += r * r;
            noise += Math.Pow(r - test[i], 2);
        }

        return noise <= 0 ? 200 : 10 * Math.Log10(Math.Max(signal, 1e-9) / noise);
    }
}
