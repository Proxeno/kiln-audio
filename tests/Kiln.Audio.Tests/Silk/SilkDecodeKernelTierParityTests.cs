using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;
using Kiln.Audio.Simd.Kernels;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// SIMD-2: every <see cref="ISilkKernels"/> tier against <see cref="ScalarKernels"/>, calling the
/// tiers directly (no environment variable). The integer kernels must be bit-exact; the float
/// kernels agree exactly where conversion is exact and to one 16-bit step in the resampler.
/// </summary>
public class SilkDecodeKernelTierParityTests
{
    /// <summary>The tiers under test (Scalar is the reference).</summary>
    public static TheoryData<int> Tiers => new()
    {
        (int)AudioKernelTier.Vector128,
        (int)AudioKernelTier.AdvSimdArm64,
        (int)AudioKernelTier.Avx2Fma,
    };

    private interface ITierBody
    {
        void Run<TK>()
            where TK : struct, ISilkKernels;
    }

    private static void With(AudioKernelTier tier, ITierBody body)
    {
        if (!AudioKernels.IsSupported(tier))
        {
            return;
        }

        switch (tier)
        {
            case AudioKernelTier.Vector128:
                body.Run<Vector128Kernels>();
                break;
            case AudioKernelTier.AdvSimdArm64:
                body.Run<AdvSimdArm64Kernels>();
                break;
            case AudioKernelTier.Avx2Fma:
                body.Run<Avx2FmaKernels>();
                break;
            default:
                body.Run<ScalarKernels>();
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Lpc_synthesis_is_bit_exact(int tier) => With((AudioKernelTier)tier, new LpcSynthesisBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Ltp_synthesis_is_bit_exact(int tier) => With((AudioKernelTier)tier, new LtpSynthesisBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Lpc_analysis_is_bit_exact(int tier) => With((AudioKernelTier)tier, new LpcAnalysisBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Scaling_kernels_are_bit_exact(int tier) => With((AudioKernelTier)tier, new ScalingBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Stereo_unmix_is_bit_exact(int tier) => With((AudioKernelTier)tier, new StereoBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Output_plumbing_is_exact(int tier) => With((AudioKernelTier)tier, new PlumbingBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Resampler_kernel_matches_scalar_within_one_step(int tier) => With((AudioKernelTier)tier, new ResampleBody());

    [Fact]
    public void Smulww_and_smulwb_primitives_match_the_scalar_definitions()
    {
        var rng = new Random(77);
        int[] edges = [int.MinValue, int.MaxValue, 0, 1, -1, 65535, -65536, 32767, -32768, 1 << 30];
        for (int round = 0; round < 2000; round++)
        {
            int a = round < edges.Length * edges.Length ? edges[round / edges.Length] : rng.Next(int.MinValue, int.MaxValue);
            int b = round < edges.Length * edges.Length ? edges[round % edges.Length] : rng.Next(int.MinValue, int.MaxValue);
            AssertPrimitives<PortableSilkOps>(a, b);
            if (AudioKernels.IsSupported(AudioKernelTier.AdvSimdArm64))
            {
                AssertPrimitives<ArmSilkOps>(a, b);
            }

            if (AudioKernels.IsSupported(AudioKernelTier.Avx2Fma))
            {
                AssertPrimitives<X86SilkOps>(a, b);
            }
        }
    }

    [Fact]
    public void Whole_decoder_tiers_agree_on_the_silk_vectors()
    {
        if (!TestVectors.Available)
        {
            return;
        }

        foreach (int vector in new[] { 2, 3, 4, 12 })
        {
            var packets = OpusBitFile.Read(TestVectors.BitPath(vector)).Take(120).ToList();
            foreach (int channels in new[] { 2, 1 })
            {
                short[]? reference = null;
                foreach (AudioKernelTier tier in new[] { AudioKernelTier.Scalar, AudioKernelTier.Vector128, AudioKernels.Best })
                {
                    if (!AudioKernels.IsSupported(tier))
                    {
                        continue;
                    }

                    short[] output = Decode(packets, channels, tier);
                    if (reference is null)
                    {
                        reference = output;
                        continue;
                    }

                    Assert.Equal(reference.Length, output.Length);
                    int maxDiff = 0;
                    for (int i = 0; i < output.Length; i++)
                    {
                        maxDiff = Math.Max(maxDiff, Math.Abs(reference[i] - output[i]));
                    }

                    Assert.True(maxDiff <= 1, $"vector {vector} channels {channels} tier {tier}: max difference {maxDiff}");
                }
            }
        }
    }

    private static short[] Decode(List<(byte[] Packet, uint FinalRange)> packets, int channels, AudioKernelTier tier)
    {
        using IDisposable scope = AudioKernels.Scope(tier);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = channels });
        var all = new List<short>();
        var pcm = new short[48000 * 120 / 1000 * channels];
        foreach ((byte[] packet, _) in packets)
        {
            int n = decoder.Decode(packet, pcm);
            all.AddRange(pcm.AsSpan(0, n * channels).ToArray());
        }

        return all.ToArray();
    }

    private static void AssertPrimitives<TO>(int a, int b)
        where TO : struct, ISilkOps
    {
        int expectedWb = SilkFixedPoint.Smulwb(a, b);
        var va = Vector128.Create(a);
        var vb = Vector128.Create(b);
        var prepared = TO.FromRaw(vb);
        Assert.Equal(expectedWb, TO.Smulwb(va, prepared).GetElement(2));

        // The sign-extended form must agree for a 16-bit coefficient.
        int sext = (short)b;
        Assert.Equal(expectedWb, TO.Smulwb(va, TO.FromSext(Vector128.Create(sext))).GetElement(0));
        Assert.Equal(SilkFixedPoint.Smulww(a, b), TO.Smulww(va, vb).GetElement(3));
    }

    private static short[] RandomShorts(Random rng, int n, int range)
    {
        var v = new short[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = (short)rng.Next(-range, range + 1);
        }

        return v;
    }

    private static int[] RandomInts(Random rng, int n, int range)
    {
        var v = new int[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = rng.Next(-range, range);
        }

        return v;
    }

    private sealed class LpcSynthesisBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(1);
            foreach (int order in new[] { 10, 16 })
            {
                foreach (int length in new[] { 40, 60, 80, 5, 1 })
                {
                    for (int round = 0; round < 20; round++)
                    {
                        short[] coefficients = RandomShorts(rng, 16, round < 10 ? 4096 : 32767);
                        int[] residual = RandomInts(rng, 80, 1 << (round < 10 ? 18 : 28));
                        int[] seedState = RandomInts(rng, 96, 1 << (round < 10 ? 22 : 30));
                        int gain = rng.Next(1 << 10, 1 << 24);
                        int[] expectedState = (int[])seedState.Clone();
                        int[] actualState = (int[])seedState.Clone();
                        var expected = new short[80];
                        var actual = new short[80];
                        ScalarKernels.LpcSynthesize(expectedState, coefficients, residual, gain, order, length, expected);
                        TK.LpcSynthesize(actualState, coefficients, residual, gain, order, length, actual);
                        Assert.Equal(expected, actual);
                        Assert.Equal(expectedState, actualState);
                    }
                }
            }
        }
    }

    private sealed class LtpSynthesisBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(2);
            foreach (int lag in new[] { 2, 5, 9, 10, 11, 17, 40, 100, 288 })
            {
                foreach (int count in new[] { 40, 80, 7 })
                {
                    short[] coefficients = RandomShorts(rng, 5, 16000);
                    int[] excitation = RandomInts(rng, 80, 1 << 22);
                    int[] seed = RandomInts(rng, 640, 1 << 24);
                    int writeIndex = 320 + 40;
                    int predictionLag = writeIndex - lag + 2;
                    int[] expectedBuffer = (int[])seed.Clone();
                    int[] actualBuffer = (int[])seed.Clone();
                    var expectedResidual = new int[80];
                    var actualResidual = new int[80];
                    ScalarKernels.LtpSynthesize(expectedBuffer, predictionLag, writeIndex, coefficients, excitation, expectedResidual, count);
                    TK.LtpSynthesize(actualBuffer, predictionLag, writeIndex, coefficients, excitation, actualResidual, count);
                    Assert.Equal(expectedBuffer, actualBuffer);
                    Assert.Equal(expectedResidual, actualResidual);
                }
            }
        }
    }

    private sealed class LpcAnalysisBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(3);
            foreach (int order in new[] { 10, 16 })
            {
                foreach (int length in new[] { 0, 9, 10, 11, 17, 18, 26, 27, 33, 100, 129, 300, 321 })
                {
                    for (int round = 0; round < 6; round++)
                    {
                        short[] coefficients = RandomShorts(rng, 16, round < 3 ? 6000 : 32767);
                        short[] input = RandomShorts(rng, 480, round < 3 ? 12000 : 32767);
                        var expected = new short[480];
                        var actual = new short[480];
                        Array.Fill(expected, (short)123);
                        Array.Fill(actual, (short)123);
                        ScalarKernels.LpcAnalysis(expected, input, coefficients, length, order);
                        TK.LpcAnalysis(actual, input, coefficients, length, order);
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
    }

    private sealed class ScalingBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(4);
            int[] gains = [0, 1, -1, int.MaxValue, int.MinValue, 65536, -65536, 1 << 30, 12345678, -87654321];
            foreach (int gain in gains.Concat(Enumerable.Range(0, 20).Select(_ => rng.Next(int.MinValue, int.MaxValue))))
            {
                foreach (int n in new[] { 0, 1, 7, 8, 9, 16, 33, 120, 301 })
                {
                    short[] source = RandomShorts(rng, n, 32767);
                    if (n > 2)
                    {
                        source[0] = short.MinValue;
                        source[1] = short.MaxValue;
                    }

                    var expected = new int[n];
                    var actual = new int[n];
                    ScalarKernels.ScaleToQ15(expected, source, gain);
                    TK.ScaleToQ15(actual, source, gain);
                    Assert.Equal(expected, actual);

                    int[] values = RandomInts(rng, n, int.MaxValue);
                    if (n > 2)
                    {
                        values[0] = int.MinValue;
                        values[1] = int.MaxValue;
                    }

                    int[] expectedWide = (int[])values.Clone();
                    int[] actualWide = (int[])values.Clone();
                    ScalarKernels.ScaleWideInPlace(expectedWide, gain);
                    TK.ScaleWideInPlace(actualWide, gain);
                    Assert.Equal(expectedWide, actualWide);
                }
            }
        }
    }

    private sealed class StereoBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(5);
            (int Frame, int Interp)[] shapes = [(80, 64), (120, 96), (160, 128), (320, 128), (83, 13), (8, 64), (100, 100), (17, 0)];
            foreach ((int frame, int interp) in shapes)
            {
                for (int round = 0; round < 12; round++)
                {
                    int range = round < 6 ? 9000 : 32767;
                    short[] mid = RandomShorts(rng, frame + 2, range);
                    short[] side = RandomShorts(rng, frame + 2, range);
                    int p0 = rng.Next(-9000, 9000);
                    int p1 = rng.Next(-9000, 9000);
                    int q0 = rng.Next(-9000, 9000);
                    int q1 = rng.Next(-9000, 9000);
                    int d0 = interp == 0 ? 0 : ((q0 - p0) * (65536 / Math.Max(1, interp)) + 32768) >> 16;
                    int d1 = interp == 0 ? 0 : ((q1 - p1) * (65536 / Math.Max(1, interp)) + 32768) >> 16;
                    if (round == 11)
                    {
                        p0 = 40000;
                        d0 = -7000;
                        q1 = -50000;
                    }

                    var expected = new short[2 * frame];
                    var actual = new short[2 * frame];
                    ScalarKernels.StereoUnmix(mid, side, frame, interp, p0, p1, d0, d1, q0, q1, expected);
                    TK.StereoUnmix(mid, side, frame, interp, p0, p1, d0, d1, q0, q1, actual);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    private sealed class PlumbingBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(6);
            foreach (int n in new[] { 0, 1, 7, 8, 9, 31, 160, 321 })
            {
                short[] interleaved = RandomShorts(rng, 2 * n, 32767);
                var e0 = new short[n];
                var e1 = new short[n];
                var a0 = new short[n];
                var a1 = new short[n];
                ScalarKernels.Deinterleave(interleaved, e0, e1, n);
                TK.Deinterleave(interleaved, a0, a1, n);
                Assert.Equal(e0, a0);
                Assert.Equal(e1, a1);

                short[] x = RandomShorts(rng, n, 32767);
                short[] y = RandomShorts(rng, n, 32767);
                var expectedAverage = new short[n];
                var actualAverage = new short[n];
                ScalarKernels.AverageShorts(x, y, expectedAverage, n);
                TK.AverageShorts(x, y, actualAverage, n);
                Assert.Equal(expectedAverage, actualAverage);

                const float scale = 1f / 32768f;
                var expectedFloat = new float[n];
                var actualFloat = new float[n];
                ScalarKernels.ShortsToFloat(x, expectedFloat, n, scale);
                TK.ShortsToFloat(x, actualFloat, n, scale);
                Assert.Equal(expectedFloat, actualFloat);

                var expectedPair = new float[2 * n];
                var actualPair = new float[2 * n];
                ScalarKernels.InterleaveToFloat(x, y, expectedPair, n, scale);
                TK.InterleaveToFloat(x, y, actualPair, n, scale);
                Assert.Equal(expectedPair, actualPair);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Upsampler_kernel_matches_scalar_within_one_step(int tier) => With((AudioKernelTier)tier, new ResampleUpBody());

    private sealed class ResampleUpBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(8);
            foreach (int up in new[] { 3, 4, 6 })
            {
                int pad = up == 6 ? 8 : 4;
                foreach (int length in new[] { 8, 24, 88 })
                {
                    var bankT = new float[length * pad];
                    for (int i = 0; i < bankT.Length; i++)
                    {
                        bankT[i] = (float)((rng.NextDouble() - 0.4) * 0.2);
                    }

                    var history = new float[length + 400];
                    for (int i = 0; i < history.Length; i++)
                    {
                        history[i] = rng.Next(-32768, 32768);
                    }

                    for (int i = 60; i < 90; i++)
                    {
                        history[i] = i % 2 == 0 ? 32767 : -32768;
                    }

                    foreach (int inputs in new[] { 0, 3, 4, 8, 12, 20, 37, 160 })
                    {
                        int newest = length + 5;
                        var expected = new short[inputs * up];
                        var actual = new short[inputs * up];
                        int expectedDone = ScalarKernels.ResampleUp(history, bankT, up, length, newest, inputs, expected);
                        int actualDone = TK.ResampleUp(history, bankT, up, length, newest, inputs, actual);
                        Assert.Equal(expectedDone, actualDone);
                        for (int i = 0; i < expectedDone * up; i++)
                        {
                            Assert.True(
                                Math.Abs(expected[i] - actual[i]) <= 1,
                                $"up {up} length {length} inputs {inputs} sample {i}: {expected[i]} vs {actual[i]}");
                        }
                    }
                }
            }
        }
    }

    private sealed class ResampleBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ISilkKernels
        {
            var rng = new Random(7);
            (int Up, int Down, int Length, bool Uneven)[] shapes =
                [(6, 1, 16, false), (3, 1, 32, false), (4, 1, 24, false), (3, 2, 16, false), (6, 1, 16, true), (3, 1, 24, true)];
            foreach ((int up, int down, int length, bool uneven) in shapes)
            {
                var bankOffset = new int[up];
                var bankLength = new int[up];
                int total = 0;
                for (int p = 0; p < up; p++)
                {
                    bankOffset[p] = total;
                    bankLength[p] = uneven && (p % 2 == 1) ? length - 8 : length;
                    total += bankLength[p];
                }

                var bank = new float[total];
                for (int i = 0; i < total; i++)
                {
                    bank[i] = (float)((rng.NextDouble() - 0.4) * 0.2);
                }

                var history = new float[length + 1200];
                for (int i = 0; i < history.Length; i++)
                {
                    history[i] = rng.Next(-32768, 32768);
                }

                // A loud block so that saturation is exercised too.
                for (int i = 100; i < 140; i++)
                {
                    history[i] = i % 2 == 0 ? 32767 : -32768;
                }

                foreach (int count in new[] { 0, 1, 3, 4, 5, 97, 480 })
                {
                    int stepSamples = down / up;
                    int stepPhase = down % up;
                    int newest = length + 3;
                    var expected = new short[count];
                    var actual = new short[count];
                    ScalarKernels.Resample(history, bank, bankOffset, bankLength, newest, 1 % up, stepSamples, stepPhase, up, count, expected);
                    TK.Resample(history, bank, bankOffset, bankLength, newest, 1 % up, stepSamples, stepPhase, up, count, actual);
                    for (int i = 0; i < count; i++)
                    {
                        Assert.True(Math.Abs(expected[i] - actual[i]) <= 1, $"up {up} down {down} sample {i}: {expected[i]} vs {actual[i]}");
                    }
                }
            }
        }
    }
}
