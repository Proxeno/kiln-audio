using System;
using FluentAssertions;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests;

/// <summary>Unit tests for the SILK output resampler's filter, streaming behavior and latency.</summary>
public class SilkResamplerTests
{
    private static readonly int[] InputRates = [8000, 12000, 16000];
    private static readonly int[] OutputRates = [8000, 12000, 16000, 24000, 48000];

    public static TheoryData<int, int> RatePairs()
    {
        var data = new TheoryData<int, int>();
        foreach (int input in InputRates)
        {
            foreach (int output in OutputRates)
            {
                data.Add(input, output);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void Passband_is_flat_and_stopband_is_deep(int inRate, int outRate)
    {
        var resampler = new SilkResampler(inRate, outRate);
        if (inRate == outRate)
        {
            resampler.GainAt(0).Should().Be(1.0);
            return;
        }

        int divisor = Gcd(inRate, outRate);
        int upsampledRate = inRate * (outRate / divisor);
        int lowerNyquist = Math.Min(inRate, outRate) / 2;

        double[] fractions = [0.1, 0.3, 0.5, 0.7, 0.85, 0.9];
        foreach (double fraction in fractions)
        {
            double gain = ToDecibels(resampler.GainAt(fraction * lowerNyquist));
            gain.Should().BeInRange(
                -0.5,
                0.5,
                FormattableString.Invariant(
                    $"{inRate}->{outRate}: passband at {fraction:F2} of Nyquist"));
        }

        for (double hz = lowerNyquist * 1.05; hz < upsampledRate / 2.0; hz *= 1.2)
        {
            double gain = ToDecibels(resampler.GainAt(hz));
            gain.Should().BeLessThanOrEqualTo(
                -60.0,
                FormattableString.Invariant($"{inRate}->{outRate}: stopband at {hz:F0} Hz is {gain:F1} dB"));
        }
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void Chunked_calls_match_one_shot(int inRate, int outRate)
    {
        const int total = 40000;
        var random = new Random(17 + inRate + (outRate * 31));
        var input = new short[total];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        var oneShot = new SilkResampler(inRate, outRate);
        int totalOutput = oneShot.OutputCount(total);
        var expected = new short[totalOutput];
        oneShot.Process(input, expected);

        var chunked = new SilkResampler(inRate, outRate);
        var actual = new short[totalOutput];
        int consumed = 0;
        int produced = 0;
        int chunkSize = 1;
        while (consumed < total)
        {
            int take = Math.Min(chunkSize, total - consumed);
            ReadOnlySpan<short> chunk = input.AsSpan(consumed, take);
            int count = chunked.OutputCount(take);
            var buffer = new short[count];
            chunked.Process(chunk, buffer);
            buffer.CopyTo(actual, produced);
            produced += count;
            consumed += take;
            chunkSize++;
            if (chunkSize > 997)
            {
                chunkSize = 1;
            }
        }

        produced.Should().Be(totalOutput);
        actual.Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void Reset_restores_the_initial_state(int inRate, int outRate)
    {
        var random = new Random(23 + inRate + (outRate * 37));
        var first = new short[777];
        var second = new short[913];
        for (int i = 0; i < first.Length; i++)
        {
            first[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        for (int i = 0; i < second.Length; i++)
        {
            second[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        var reused = new SilkResampler(inRate, outRate);
        Run(reused, first);
        reused.Reset();
        short[] afterReset = Run(reused, second);

        short[] fresh = Run(new SilkResampler(inRate, outRate), second);
        afterReset.Should().Equal(fresh);
    }

    [Theory]
    [InlineData(8000, 12000)]
    [InlineData(12000, 16000)]
    [InlineData(16000, 48000)]
    public void Process_does_not_allocate(int inRate, int outRate)
    {
        var resampler = new SilkResampler(inRate, outRate);
        var input = new short[1000];
        var output = new short[resampler.OutputCount(input.Length) + 8];
        for (int i = 0; i < 10; i++)
        {
            resampler.Process(input, output);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            resampler.Process(input, output);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).Should().Be(0);
    }

    [Fact]
    public void Equal_rates_copy_exactly()
    {
        var random = new Random(99);
        var input = new short[5000];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        foreach (int rate in InputRates)
        {
            var resampler = new SilkResampler(rate, rate);
            resampler.DelaySamplesOut.Should().Be(0);
            var output = new short[input.Length];
            resampler.Process(input, output);
            output.Should().Equal(input);
        }
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void Latency_matches_the_documented_value(int inRate, int outRate)
    {
        if (inRate == outRate)
        {
            return;
        }

        const int total = 60000;
        var random = new Random(101 + inRate + (outRate * 41));
        var input = new short[total];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (short)(random.Next(-12000, 12001));
        }

        var resampler = new SilkResampler(inRate, outRate);
        var output = new short[resampler.OutputCount(total)];
        resampler.Process(input, output);

        double[] ideal = IdealResampler.Resample(input, inRate, outRate);
        short[] idealPcm = IdealResampler.ToPcm(ideal);
        int lag = IdealResampler.FindLag(idealPcm, output, maxLag: 200, skip: 4000);

        lag.Should().BeInRange(
            resampler.DelaySamplesOut - 2,
            resampler.DelaySamplesOut + 2,
            FormattableString.Invariant(
                $"{inRate}->{outRate}: measured {lag}, documented {resampler.DelaySamplesOut}"));
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(8000, 44100)]
    [InlineData(24000, 48000)]
    [InlineData(0, 48000)]
    public void Unsupported_rates_are_rejected(int inRate, int outRate)
    {
        Action create = () => _ = new SilkResampler(inRate, outRate);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static short[] Run(SilkResampler resampler, short[] input)
    {
        var output = new short[resampler.OutputCount(input.Length)];
        resampler.Process(input, output);
        return output;
    }

    private static double ToDecibels(double gain) => 20 * Math.Log10(gain);

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
