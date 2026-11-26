using FluentAssertions;
using Kiln.Audio.Celt.Encoder;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>Kernels added by ENC-2: rate mapping, the decimated-correlation dot product and the comb filter.</summary>
public class Enc2KernelTests
{
    [Theory]
    [InlineData(48000, 1, 21, 28)]
    [InlineData(24000, 2, 19, 24)]
    [InlineData(16000, 3, 17, 20)]
    [InlineData(12000, 4, 17, 20)]
    [InlineData(8000, 6, 13, 16)]
    public void The_rate_mapping_matches_the_reference(int rate, int upsample, int endBand, int configBase)
    {
        InputResampler.UpsampleFactor(rate).Should().Be(upsample);
        InputResampler.EndBand(rate).Should().Be(endBand);
        InputResampler.TocConfigBase(rate).Should().Be(configBase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(240)]
    [InlineData(480)]
    [InlineData(961)]
    public void The_pitch_dot_product_agrees_with_its_scalar_reference(int length)
    {
        var random = new Random(length);
        var a = new float[length];
        var b = new float[length];
        for (int i = 0; i < length; i++)
        {
            a[i] = (float)(random.NextDouble() * 2 - 1);
            b[i] = (float)(random.NextDouble() * 2 - 1);
        }

        float scalar = CeltPrefilter.DotScalar(a, b);
        float vector = CeltPrefilter.DotProduct(a, b, useVector: true);
        vector.Should().BeApproximately(scalar, 1e-3f * MathF.Max(1f, MathF.Abs(scalar)));
        CeltPrefilter.DotProduct(a, b, useVector: false).Should().Be(scalar);
    }

    [Fact]
    public void A_zero_gain_comb_filter_copies_the_input()
    {
        const int xStart = 32;
        const int length = 64;
        var x = new float[xStart + length];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)Math.Sin(i * 0.1);
        }

        var y = new float[length];
        var window = new float[16];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = (float)Math.Sin(Math.PI * (i + 0.5) / (2 * window.Length));
        }

        CeltPrefilter.CombFilter(y, x, xStart, 16, 16, length, 0f, 0f, 0, 0, window, window.Length);
        y.Should().Equal(x.AsSpan(xStart, length).ToArray());
    }

    [Fact]
    public void A_single_tap_comb_filter_subtracts_the_delayed_signal()
    {
        const int xStart = 32;
        const int length = 32;
        const int period = 16;
        var x = new float[xStart + length];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (i % 7) - 3f;
        }

        var y = new float[length];
        var window = new float[16];
        // Tap set 0, gain 1: the first tap is 0.3066406250 and the filter cross-fades over the
        // window; with the window unused past the overlap the full gain applies.
        CeltPrefilter.CombFilter(y, x, xStart, period, period, length, 1f, 1f, 0, 0, window, 0);
        for (int i = 0; i < length; i++)
        {
            float expected = x[xStart + i]
                + (0.3066406250f * x[xStart + i - period])
                + (0.2170410156f * (x[xStart + i - period - 1] + x[xStart + i - period + 1]))
                + (0.1296386719f * (x[xStart + i - period - 2] + x[xStart + i - period + 2]));
            y[i].Should().BeApproximately(expected, 1e-6f);
        }
    }
}