using Kiln.Audio.Celt;
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Tests;

/// <summary>
/// Parity of every non-scalar kernel tier against <see cref="ScalarKernels"/>, calling the tiers
/// directly (no switch, no environment variable). Float kernels agree to a fraction of the signal
/// RMS: fused multiply-add and reassociated sums are the only sources of difference.
/// </summary>
public class KernelTierParityTests
{
    private const double Tolerance = 2e-6;

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
            where TK : struct, ICeltKernels;
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

    [Fact]
    public void Selection_honours_the_disable_switch_and_reports_a_supported_tier()
    {
        Assert.True(AudioKernels.IsSupported(AudioKernels.Best));
        Assert.True(AudioKernels.IsSupported(AudioKernelTier.Scalar));
        if (SimdConfig.Disabled)
        {
            Assert.Equal(AudioKernelTier.Scalar, AudioKernels.Best);
        }

        using (AudioKernels.Scope(AudioKernelTier.Scalar))
        {
            Assert.Equal(AudioKernelTier.Scalar, AudioKernels.Select());
            Assert.Equal(AudioKernelTier.Scalar, new CeltSynthesizer(2).Tier);
        }
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Elementwise_kernels_match_scalar(int tierValue) => With((AudioKernelTier)tierValue, new Elementwise());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Rotate_and_haar_match_scalar(int tierValue) => With((AudioKernelTier)tierValue, new RotateHaar());

    [Theory]
    [InlineData((int)AudioKernelTier.Vector128, 60)]
    [InlineData((int)AudioKernelTier.Vector128, 120)]
    [InlineData((int)AudioKernelTier.Vector128, 240)]
    [InlineData((int)AudioKernelTier.Vector128, 480)]
    [InlineData((int)AudioKernelTier.Vector128, 30)]
    [InlineData((int)AudioKernelTier.Vector128, 90)]
    [InlineData((int)AudioKernelTier.Vector128, 20)]
    [InlineData((int)AudioKernelTier.Vector128, 6)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 60)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 120)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 240)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 480)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 30)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 90)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 20)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64, 6)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 60)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 120)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 240)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 480)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 30)]
    [InlineData((int)AudioKernelTier.Avx2Fma, 90)]
    public void Fft_matches_scalar(int tierValue, int size)
    {
        With((AudioKernelTier)tierValue, new FftBody(size));
    }

    [Theory]
    [InlineData((int)AudioKernelTier.Vector128)]
    [InlineData((int)AudioKernelTier.AdvSimdArm64)]
    [InlineData((int)AudioKernelTier.Avx2Fma)]
    public void Inverse_mdct_matches_scalar(int tierValue) => With((AudioKernelTier)tierValue, new ImdctBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Comb_filter_and_overlap_add_match_scalar(int tierValue) => With((AudioKernelTier)tierValue, new CombBody());

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Deemphasis_matches_scalar(int tierValue) => With((AudioKernelTier)tierValue, new DeemphasisBody());

    [Fact]
    public void Mono_stream_on_stereo_output_matches_two_channel_synthesis()
    {
        // A mono stream decoded to stereo runs one synthesis and duplicates: every sample of the
        // two output channels must be identical, on every tier.
        foreach (AudioKernelTier tier in new[] { AudioKernelTier.Scalar, AudioKernels.Best })
        {
            var synth = new CeltSynthesizer(2, 1, tier);
            var frame = new CeltFrameResult();
            var rng = new Random(5);
            var pcm = new float[1920];
            for (int f = 0; f < 4; f++)
            {
                PrepareFrame(frame, rng, streamChannels: 1);
                synth.Synthesize(frame, pcm);
                for (int i = 0; i < 960; i++)
                {
                    Assert.Equal(pcm[2 * i], pcm[(2 * i) + 1]);
                }
            }
        }
    }

    [Fact]
    public void Synthesis_tiers_agree_on_a_stereo_stream()
    {
        var rng = new Random(11);
        var reference = new CeltSynthesizer(2, 1, AudioKernelTier.Scalar);
        var best = new CeltSynthesizer(2, 1, AudioKernels.Best);
        var frame = new CeltFrameResult();
        var a = new float[1920];
        var b = new float[1920];
        for (int f = 0; f < 6; f++)
        {
            PrepareFrame(frame, rng, streamChannels: 2);
            reference.Synthesize(frame, a);
            best.Synthesize(frame, b);
            AssertClose(a, b, $"frame {f}");
        }
    }

    private static void PrepareFrame(CeltFrameResult frame, Random rng, int streamChannels)
    {
        frame.Lm = 3;
        frame.SampleCount = 960;
        frame.StartBand = 0;
        frame.EndBand = 21;
        frame.StreamChannels = streamChannels;
        frame.ShortBlocks = 0;
        for (int i = 0; i < frame.Spectrum.Length; i++)
        {
            frame.Spectrum[i] = (float)((rng.NextDouble() * 2) - 1);
        }

        for (int i = 0; i < frame.BandEnergies.Length; i++)
        {
            frame.BandEnergies[i] = (float)((rng.NextDouble() * 6) - 3);
        }
    }

    private static float[] Random32(Random rng, int n, double scale = 1.0)
    {
        var values = new float[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = (float)(((rng.NextDouble() * 2) - 1) * scale);
        }

        return values;
    }

    private static void AssertClose(float[] expected, float[] actual, string context, int count = -1)
    {
        count = count < 0 ? expected.Length : count;
        double sum = 0;
        double max = 0;
        for (int i = 0; i < count; i++)
        {
            sum += (double)expected[i] * expected[i];
            max = Math.Max(max, Math.Abs((double)expected[i] - actual[i]));
        }

        double rms = Math.Max(Math.Sqrt(sum / Math.Max(count, 1)), 1e-30);
        Assert.True(max <= Tolerance * rms, FormattableString.Invariant($"{context}: max error {max:E3} vs rms {rms:E3}"));
    }

    private readonly struct Elementwise : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(1);
            foreach (int n in new[] { 1, 3, 4, 7, 8, 13, 64, 176 })
            {
                float[] x = Random32(rng, n);
                float[] y = Random32(rng, n);
                float[] expected = new float[n];
                float[] actual = new float[n];
                ScalarKernels.ScaleCopy(expected, x, 0.37f);
                TK.ScaleCopy(actual, x, 0.37f);
                AssertClose(expected, actual, $"ScaleCopy {n}");

                int[] pulses = new int[n];
                for (int i = 0; i < n; i++)
                {
                    pulses[i] = rng.Next(-5, 6);
                }

                ScalarKernels.ScaleIntToFloat(expected, pulses, 0.21f);
                TK.ScaleIntToFloat(actual, pulses, 0.21f);
                AssertClose(expected, actual, $"ScaleIntToFloat {n}");

                float[] e1 = (float[])x.Clone();
                float[] a1 = (float[])x.Clone();
                ScalarKernels.ScaleInPlace(e1, 1.7f);
                TK.ScaleInPlace(a1, 1.7f);
                AssertClose(e1, a1, $"ScaleInPlace {n}");

                float sumE = ScalarKernels.SumSquares(x);
                float sumA = TK.SumSquares(x);
                Assert.True(Math.Abs(sumE - sumA) <= 1e-5 * Math.Max(1f, sumE), $"SumSquares {n}: {sumE} vs {sumA}");

                ScalarKernels.StereoDots(x, y, out float xyE, out float yyE);
                TK.StereoDots(x, y, out float xyA, out float yyA);
                Assert.True(Math.Abs(xyE - xyA) <= 1e-5 * Math.Max(1f, Math.Abs(xyE)) && Math.Abs(yyE - yyA) <= 1e-5 * Math.Max(1f, yyE), $"StereoDots {n}");

                float[] xe = (float[])x.Clone();
                float[] ye = (float[])y.Clone();
                float[] xa = (float[])x.Clone();
                float[] ya = (float[])y.Clone();
                ScalarKernels.StereoMerge(xe, ye, 0.7f, 0.4f, 0.9f, n);
                TK.StereoMerge(xa, ya, 0.7f, 0.4f, 0.9f, n);
                AssertClose(xe, xa, $"StereoMerge x {n}");
                AssertClose(ye, ya, $"StereoMerge y {n}");

                float[] de = (float[])x.Clone();
                float[] da = (float[])x.Clone();
                ScalarKernels.AddInPlace(de, y);
                TK.AddInPlace(da, y);
                AssertClose(de, da, $"AddInPlace {n}");

                de = (float[])x.Clone();
                da = (float[])x.Clone();
                ScalarKernels.Average(de, y);
                TK.Average(da, y);
                AssertClose(de, da, $"Average {n}");
            }
        }
    }

    private readonly struct RotateHaar : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(2);
            foreach (int stride in new[] { 1, 2, 3, 4, 8 })
            {
                foreach (int length in new[] { 4, 9, 16, 33, 64, 176 })
                {
                    float[] x = Random32(rng, length);
                    float[] expected = (float[])x.Clone();
                    float[] actual = (float[])x.Clone();
                    ScalarKernels.Rotate(expected, length, stride, 0.8f, 0.6f);
                    TK.Rotate(actual, length, stride, 0.8f, 0.6f);
                    AssertClose(expected, actual, $"Rotate stride {stride} length {length}");
                }
            }

            foreach (int stride in new[] { 1, 2, 4, 8 })
            {
                foreach (int n0 in new[] { 2, 4, 8, 16, 18, 32, 64 })
                {
                    float[] x = Random32(rng, n0 * stride);
                    float[] expected = (float[])x.Clone();
                    float[] actual = (float[])x.Clone();
                    ScalarKernels.Haar(expected, n0, stride);
                    TK.Haar(actual, n0, stride);
                    AssertClose(expected, actual, $"Haar stride {stride} n0 {n0}");
                }
            }
        }
    }

    private readonly struct FftBody(int size) : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(size);
            var fft = Fft.Get(size);
            foreach (bool inverse in new[] { false, true })
            {
                for (int seed = 0; seed < 3; seed++)
                {
                    float[] re = Random32(rng, size);
                    float[] im = Random32(rng, size);
                    var er = new float[size];
                    var ei = new float[size];
                    var ar = new float[size];
                    var ai = new float[size];
                    fft.Transform<ScalarKernels>(re, im, er, ei, inverse);
                    fft.Transform<TK>(re, im, ar, ai, inverse);
                    AssertClose(er, ar, $"fft {size} inverse={inverse} real");
                    AssertClose(ei, ai, $"fft {size} inverse={inverse} imag");
                }
            }
        }
    }

    private readonly struct ImdctBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(3);
            foreach (int count in new[] { 120, 240, 480, 960 })
            {
                var mdct = new Mdct(count);
                foreach (int stride in count == 120 ? new[] { 1, 8 } : new[] { 1 })
                {
                    float[] coefficients = Random32(rng, count * stride);
                    var expected = new float[count + 120];
                    var actual = new float[count + 120];
                    mdct.InverseUnscaled<ScalarKernels>(coefficients, expected, stride);
                    mdct.InverseUnscaled<TK>(coefficients, actual, stride);
                    AssertClose(expected, actual, $"imdct {count} stride {stride}");
                }
            }
        }
    }

    private readonly struct CombBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(4);
            float[] window = new float[120];
            for (int i = 0; i < window.Length; i++)
            {
                window[i] = (float)Math.Pow(Math.Sin(Math.PI * (i + 0.5) / 240), 4);
            }

            (int p0, int p1, float g0, float g1)[] cases =
            [
                (40, 57, 0.5f, 0.28f),
                (15, 15, 0.3f, 0.3f),
                (20, 33, 0f, 0.4f),
                (20, 33, 0.4f, 0f),
                (20, 33, 0f, 0f),
                (7, 9, 0.2f, 0.25f),
            ];
            foreach (var (p0, p1, g0, g1) in cases)
            {
                foreach (int count in new[] { 120, 840, 100 })
                {
                    float[] history = Random32(rng, 2048 + 120);
                    float[] e = (float[])history.Clone();
                    float[] a = (float[])history.Clone();
                    ScalarKernels.CombFilter(e, 1000, count, p0, p1, g0, g1, 0, 1, window, 120);
                    TK.CombFilter(a, 1000, count, p0, p1, g0, g1, 0, 1, window, 120);
                    AssertClose(e, a, $"comb {p0}/{p1} {g0}/{g1} count {count}");
                }
            }

            foreach (int count in new[] { 960, 480, 120 })
            {
                float[] raw = Random32(rng, count + 120);
                float[] state = Random32(rng, 120);
                float[] re = (float[])raw.Clone();
                float[] ae = (float[])raw.Clone();
                float[] se = (float[])state.Clone();
                float[] sa = (float[])state.Clone();
                ScalarKernels.OverlapAddSave(re, se, count, 120);
                TK.OverlapAddSave(ae, sa, count, 120);
                AssertClose(re, ae, "overlap raw");
                AssertClose(se, sa, "overlap state");
            }
        }
    }

    private readonly struct DeemphasisBody : ITierBody
    {
        public void Run<TK>()
            where TK : struct, ICeltKernels
        {
            var rng = new Random(6);
            foreach (int samples in new[] { 120, 960, 7 })
            {
                foreach (int channels in new[] { 1, 2 })
                {
                    foreach (bool duplicate in channels == 2 ? new[] { false, true } : new[] { false })
                    {
                        float[] s0 = Random32(rng, samples, 3000);
                        float[] s1 = Random32(rng, samples, 3000);
                        var e = new float[samples * channels];
                        var a = new float[samples * channels];
                        float em0 = 12f;
                        float em1 = -7f;
                        float am0 = 12f;
                        float am1 = -7f;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            ScalarKernels.Deemphasis(s0, s1, e, samples, channels, duplicate, ref em0, ref em1, 1f / 32768f);
                            TK.Deemphasis(s0, s1, a, samples, channels, duplicate, ref am0, ref am1, 1f / 32768f);
                            AssertClose(e, a, $"deemphasis {samples}x{channels} dup={duplicate}");
                            Assert.True(Math.Abs(em0 - am0) <= 1e-3f * Math.Max(1f, Math.Abs(em0)), "memory0");
                            Assert.True(Math.Abs(em1 - am1) <= 1e-3f * Math.Max(1f, Math.Abs(em1)), "memory1");
                        }
                    }
                }
            }
        }
    }
}
