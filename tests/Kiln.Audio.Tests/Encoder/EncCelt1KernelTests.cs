using FluentAssertions;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>ENC-CELT-1: every analysis kernel of every tier must match its scalar reference exactly.</summary>
public class EncCelt1KernelTests
{
    private static float[] Random(int n, int seed, float scale = 1f)
    {
        var r = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = (float)((r.NextDouble() * 2 - 1) * scale);
        }

        return a;
    }


    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(31)]
    [InlineData(992)]
    public void Autocorr5_matches_scalar_on_every_tier(int n)
    {
        float[] x = Random(n, n);
        var expected = new float[5];
        CeltEncScalar.Autocorr5(x, expected);
        Check<Vector128Kernels>(x, expected);
        Check<AdvSimdArm64Kernels>(x, expected);
        if (System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            Check<Avx2FmaKernels>(x, expected);
        }

        static void Check<TK>(float[] input, float[] reference)
            where TK : struct, IEncoderKernels
        {
            if (typeof(TK) == typeof(AdvSimdArm64Kernels) && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                return;
            }

            var actual = new float[5];
            TK.Autocorr5(input, actual);
            actual.Should().Equal(reference);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(992)]
    public void Fir_and_decimation_match_scalar_on_every_tier(int n)
    {
        float[] x = Random((2 * n) + 16, n + 1);
        var firExpected = Random(n, n + 2);
        float[] fir4 = (float[])firExpected.Clone();
        float[] fir1 = (float[])firExpected.Clone();
        CeltEncScalar.Fir4(fir4, -0.31f, 0.27f, 0.11f, -0.05f);
        CeltEncScalar.Fir1(fir1, 0.8f);
        var decExpected = new float[n];
        var decAccExpected = Random(n, 9);
        float[] accStart = (float[])decAccExpected.Clone();
        CeltEncScalar.DecimateLowpass(x, decExpected, n, false);
        CeltEncScalar.DecimateLowpass(x, decAccExpected, n, true);

        Run<Vector128Kernels>();
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Run<AdvSimdArm64Kernels>();
        }

        if (System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            Run<Avx2FmaKernels>();
        }

        void Run<TK>()
            where TK : struct, IEncoderKernels
        {
            float[] a = (float[])firExpected.Clone();
            TK.Fir4(a, -0.31f, 0.27f, 0.11f, -0.05f);
            a.Should().Equal(fir4);
            float[] b = (float[])firExpected.Clone();
            TK.Fir1(b, 0.8f);
            b.Should().Equal(fir1);
            var d = new float[n];
            TK.DecimateLowpass(x, d, n, false);
            d.Should().Equal(decExpected);
            float[] e = (float[])accStart.Clone();
            TK.DecimateLowpass(x, e, n, true);
            e.Should().Equal(decAccExpected);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(176)]
    public void SpreadCounts_and_BlockMaxAbs_match_scalar(int n)
    {
        float[] x = Random(n, n + 3, 0.2f);
        CeltEncScalar.SpreadCounts(x, n, out int e0, out int e1, out int e2);
        CeltEncVec<PortableOps>.SpreadCounts(x, n, out int a0, out int a1, out int a2);
        (a0, a1, a2).Should().Be((e0, e1, e2));

        float[] big = Random(60 * 18, 77, 4f);
        var expected = new float[18];
        var actual = new float[18];
        CeltEncScalar.BlockMaxAbs(big, 60, expected, 18);
        CeltEncVec<PortableOps>.BlockMaxAbs(big, 60, actual, 18);
        actual.Should().Equal(expected);
    }

    [Theory]
    [InlineData(4, 0.0f)]
    [InlineData(4, 0.5f)]
    [InlineData(40, 0.5f)]
    [InlineData(960, 0.625f)]
    [InlineData(965, 0.625f)]
    public void CombFilter_matches_the_reference_exactly(int n, float gain1)
    {
        const int maxPeriod = 1024;
        float[] x = Random(maxPeriod + n + 8, n, 100f);
        float[] window = Random(120, 5, 0.5f);
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = Math.Abs(window[i]) + 0.1f;
        }

        ReadOnlySpan<float> table = Kiln.Audio.Celt.CeltTables.PostFilterGains;
        var expected = new float[n];
        CeltPrefilter.CombFilterReference(expected, x, maxPeriod, 100, 237, n, -0.3f, -gain1, table.Slice(0, 3), table.Slice(3, 3), window, 120);
        var actual = new float[n];
        CeltEncVec<PortableOps>.CombFilter(actual, x, maxPeriod, 100, 237, n, -0.3f, -gain1, table.Slice(0, 3), table.Slice(3, 3), window, 120);
        actual.Should().Equal(expected);
    }

    [Theory]
    [InlineData(960)]
    [InlineData(480)]
    [InlineData(240)]
    public void Mdct_fold_and_post_match_scalar(int count)
    {
        int half = count / 2;
        float[] input = Random(count + 120, count);
        var er = new float[half];
        var ei = new float[half];
        var ar = new float[half];
        var ai = new float[half];
        int s1 = 60 + 2 * 30;
        int s2 = count - 1 + 60 - 2 * 30;
        CeltEncScalar.MdctFoldMiddle(input, s1, s2, er, ei, 30, half - 30);
        CeltEncVec<PortableOps>.MdctFoldMiddle(input, s1, s2, ar, ai, 30, half - 30);
        ar.Should().Equal(er);
        ai.Should().Equal(ei);

        float[] re = Random(half, 1);
        float[] im = Random(half, 2);
        float[] cos = Random(half, 3);
        float[] sin = Random(half, 4);
        var expected = new float[count];
        var actual = new float[count];
        CeltEncScalar.MdctPostContiguous(expected, re, im, cos, sin, count);
        CeltEncVec<PortableOps>.MdctPostContiguous(actual, re, im, cos, sin, count);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Dot4_matches_the_single_dot_product()
    {
        float[] a = Random(500, 1);
        float[] b = Random(500, 2);
        float[] c = Random(500, 3);
        CeltEncVec<PortableOps>.Dot4(480, a, b, a, c, b, b, c, c, out float s0, out float s1, out float s2, out float s3);
        s0.Should().Be(Vector128Kernels.Dot(a.AsSpan(0, 480), b.AsSpan(0, 480)));
        s1.Should().Be(Vector128Kernels.Dot(a.AsSpan(0, 480), c.AsSpan(0, 480)));
        s2.Should().Be(Vector128Kernels.Dot(b.AsSpan(0, 480), b.AsSpan(0, 480)));
        s3.Should().Be(Vector128Kernels.Dot(c.AsSpan(0, 480), c.AsSpan(0, 480)));
    }

    [Fact]
    public void PulseIndex_inverts_the_decoder_walk()
    {
        var random = new System.Random(11);
        for (int trial = 0; trial < 3000; trial++)
        {
            int n = 2 + random.Next(0, 40);
            int k = 1 + random.Next(0, 24);
            var y = new int[n];
            for (int p = 0; p < k; p++)
            {
                int pos = random.Next(0, n);
                int sign = y[pos] != 0 ? Math.Sign(y[pos]) : (random.Next(0, 2) == 0 ? 1 : -1);
                y[pos] += sign;
            }

            uint index = CeltEncoderVq.PulseIndex(y, n, k, out uint nc);
            index.Should().BeLessThan(nc);
            var decoded = new int[n];
            DecodeWalk(n, k, index, decoded);
            decoded.Should().Equal(y, $"n={n} k={k}");
        }
    }

    // The decoder's table-driven walk (RFC 6716 cwrsi), written against the same cells.
    private static void DecodeWalk(int n, int k, uint index, int[] y)
    {
        var table = Kiln.Audio.Celt.CeltDecoder.PvqCwrs;
        const int maxK = Kiln.Audio.Celt.CeltDecoder.PvqMaxK;
        uint i = index;
        for (int j = 0; j < n && k > 0; j++)
        {
            int remaining = n - j;
            if (remaining == 1)
            {
                y[j] = i == 0 ? k : -k;
                return;
            }

            var e = table[(remaining * maxK) + k];
            uint d = i - e.Zero;
            if (d < e.Below)
            {
                i = d;
                continue;
            }

            bool negative = i >= e.Zero + e.Below;
            if (negative)
            {
                i -= e.Zero + e.Below;
            }

            int kk = k - 1;
            while (table[(remaining * maxK) + kk].Zero > i)
            {
                kk--;
            }

            i -= table[(remaining * maxK) + kk].Zero;
            y[j] = negative ? -(k - kk) : k - kk;
            k = kk;
        }
    }
}
