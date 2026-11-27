using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Tests;

/// <summary>
/// SIMD-3: every SILK encoder kernel tier is bit-identical to the scalar tier (exact integer
/// arithmetic), including the wrap-around and the 16-bit extremes, and a whole SILK encode is
/// byte-identical across tiers.
/// </summary>
public class SilkKernelTierParityTests
{
    public static TheoryData<int> Tiers => new()
    {
        (int)AudioKernelTier.Vector128,
        (int)AudioKernelTier.AdvSimdArm64,
        (int)AudioKernelTier.Avx2Fma,
    };

    private interface IBody
    {
        void Run<TK>()
            where TK : struct, ISilkEncoderKernels;
    }

    private static void With(AudioKernelTier tier, IBody body)
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
            default:
                body.Run<Avx2FmaKernels>();
                break;
        }
    }

    private static short[] Signal(int seed, int count, int mode)
    {
        var random = new Random(seed);
        var data = new short[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = mode switch
            {
                0 => (short)random.Next(-32768, 32768),
                1 => short.MinValue,
                2 => (short)random.Next(-300, 300),
                _ => (short)(random.Next(2) == 0 ? short.MaxValue : short.MinValue),
            };
        }

        return data;
    }

    private sealed class KernelBody : IBody
    {
        public void Run<TK>()
            where TK : struct, ISilkEncoderKernels
        {
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                foreach (int length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 40, 80, 81, 160, 321 })
                {
                    short[] a = Signal(1 + length, length + 8, mode);
                    short[] b = Signal(2 + length, length + 8, mode == 1 ? 1 : 0);
                    Assert.Equal(ScalarKernels.InnerProd(a, b, length), TK.InnerProd(a, b, length));
                    Assert.Equal(ScalarKernels.InnerProd64(a, b, length), TK.InnerProd64(a, b, length));
                }

                foreach (int lags in new[] { 1, 3, 4, 5, 9, 22, 65 })
                {
                    foreach (int length in new[] { 5, 8, 20, 40, 80 })
                    {
                        short[] frame = Signal(lags + length, 400, mode);
                        int start = 300;
                        short[] target = Signal(9 + length, length + 4, mode);
                        var expected = new int[lags];
                        var actual = new int[lags];
                        ScalarKernels.CorrLags(target, frame, start, lags, length, expected);
                        TK.CorrLags(target, frame, start, lags, length, actual);
                        Assert.Equal(expected, actual);
                    }
                }

                foreach (int order in new[] { 6, 8, 10, 12, 16 })
                {
                    foreach (int length in new[] { order + 1, order + 7, order + 8, 100, 321, 640 })
                    {
                        short[] input = Signal(order * length, length, mode);
                        var coefficients = new short[order];
                        var random = new Random(order + length);
                        for (int i = 0; i < order; i++)
                        {
                            coefficients[i] = (short)random.Next(-8192, 8192);
                        }

                        var expected = new short[length];
                        var actual = new short[length];
                        ScalarKernels.LpcAnalysisFilter(expected, input, coefficients, length, order);
                        TK.LpcAnalysisFilter(actual, input, coefficients, length, order);
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Kernels_are_bit_identical_to_the_scalar_tier(int tier) => With((AudioKernelTier)tier, new KernelBody());

    [Theory]
    [InlineData(OpusBandwidthCase.Wideband, 0)]
    [InlineData(OpusBandwidthCase.Wideband, 5)]
    [InlineData(OpusBandwidthCase.Mediumband, 3)]
    public void A_whole_silk_encode_is_byte_identical_across_tiers(OpusBandwidthCase bandwidth, int complexity)
    {
        const int Rate = 48000;
        int frame = Rate / 50;
        var pcm = new short[frame * 40];
        var random = new Random(7);
        double phase = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            double f = 140 + (60 * Math.Sin(i / 9000.0));
            phase += 2 * Math.PI * f / Rate;
            double voiced = 6000 * (Math.Sin(phase) + (0.5 * Math.Sin(2 * phase)) + (0.3 * Math.Sin(3 * phase)));
            pcm[i] = (short)Math.Clamp(voiced + random.Next(-400, 400), -32768, 32767);
        }

        byte[][] reference = Encode(AudioKernelTier.Scalar, bandwidth, complexity, pcm, frame);
        foreach (AudioKernelTier tier in new[] { AudioKernelTier.Vector128, AudioKernelTier.AdvSimdArm64, AudioKernelTier.Avx2Fma })
        {
            if (!AudioKernels.IsSupported(tier))
            {
                continue;
            }

            byte[][] actual = Encode(tier, bandwidth, complexity, pcm, frame);
            Assert.Equal(reference.Length, actual.Length);
            for (int i = 0; i < reference.Length; i++)
            {
                Assert.True(reference[i].AsSpan().SequenceEqual(actual[i]), $"{tier}: packet {i} differs");
            }
        }
    }

    public enum OpusBandwidthCase
    {
        Mediumband,
        Wideband,
    }

    private static byte[][] Encode(AudioKernelTier tier, OpusBandwidthCase bandwidth, int complexity, short[] pcm, int frame)
    {
        using (AudioKernels.Scope(tier))
        {
            using var encoder = new OpusEncoder(new OpusEncoderOptions
            {
                Application = OpusApplication.Voip,
                SampleRate = 48000,
                Channels = 1,
                BitrateBps = 20000,
                FrameDuration = OpusFrameDuration.Ms20,
                Complexity = complexity,
                Bandwidth = bandwidth == OpusBandwidthCase.Wideband ? OpusBandwidth.Wideband : OpusBandwidth.Mediumband,
            });
            var packets = new List<byte[]>();
            var buffer = new byte[1276];
            for (int offset = 0; offset + frame <= pcm.Length; offset += frame)
            {
                int length = encoder.Encode(pcm.AsSpan(offset, frame), buffer);
                packets.Add(buffer.AsSpan(0, length).ToArray());
            }

            return [.. packets];
        }
    }
}
