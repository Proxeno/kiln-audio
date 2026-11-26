using System;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// A slow, offline band-limited resampler used only to build test references. It evaluates an
/// ideal windowed-sinc interpolation at the exact rational output instants, with the filter
/// symmetric about the evaluation point, so it introduces no delay. It allocates freely.
/// </summary>
internal static class IdealResampler
{
    private const int DefaultHalfWidth = 64;

    /// <summary>Resamples interleaved or mono samples at integer rates with a zero-delay sinc.</summary>
    /// <param name="input">Input samples.</param>
    /// <param name="inRate">Input rate in hertz.</param>
    /// <param name="outRate">Output rate in hertz.</param>
    /// <param name="halfWidth">Half-width of the window in input samples.</param>
    /// <returns>Output samples; edge samples whose support leaves the input are zero.</returns>
    internal static double[] Resample(
        ReadOnlySpan<short> input, int inRate, int outRate, int halfWidth = DefaultHalfWidth)
    {
        int divisor = Gcd(inRate, outRate);
        int up = outRate / divisor;
        int down = inRate / divisor;
        int outputLength = (int)((long)input.Length * up / down);
        var output = new double[outputLength];
        double scale = Math.Min(1.0, (double)outRate / inRate);
        for (int n = 0; n < outputLength; n++)
        {
            long numerator = (long)n * down;
            int whole = (int)(numerator / up);
            double fraction = (numerator % up) / (double)up;
            double sum = 0;
            double weight = 0;
            for (int i = whole - halfWidth; i <= whole + halfWidth; i++)
            {
                if (i < 0 || i >= input.Length)
                {
                    continue;
                }

                double u = (whole - i) + fraction;
                double w = Kernel(u, scale, halfWidth);
                sum += input[i] * w;
                weight += w;
            }

            if (weight > 1e-9 || weight < -1e-9)
            {
                output[n] = sum / weight;
            }
        }

        return output;
    }

    /// <summary>Converts doubles to 16-bit PCM with rounding and saturation.</summary>
    /// <param name="samples">Samples to convert.</param>
    internal static short[] ToPcm(ReadOnlySpan<double> samples)
    {
        var output = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double rounded = Math.Round(samples[i]);
            output[i] = rounded switch
            {
                > short.MaxValue => short.MaxValue,
                < short.MinValue => short.MinValue,
                _ => (short)rounded,
            };
        }

        return output;
    }

    /// <summary>Computes the normalized cross-correlation lag of <paramref name="test"/> against <paramref name="reference"/>.</summary>
    /// <param name="reference">Reference signal.</param>
    /// <param name="test">Test signal; the result is the lag <c>d</c> maximizing test[n] ~ reference[n - d].</param>
    /// <param name="maxLag">Largest lag searched, in samples; the search is symmetric.</param>
    /// <param name="skip">Samples skipped at both ends.</param>
    /// <param name="stride">Correlation stride; larger values trade precision for speed.</param>
    internal static int FindLag(
        short[] reference, ReadOnlySpan<short> test, int maxLag, int skip, int stride = 1)
    {
        ArgumentNullException.ThrowIfNull(reference);
        int length = Math.Min(reference.Length, test.Length);
        int start = Math.Min(skip, length);
        int end = length - start;
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double numerator = 0;
            double referencePower = 0;
            double testPower = 0;
            for (int n = start; n < end; n += stride)
            {
                int j = n - lag;
                if (j < 0 || j >= reference.Length)
                {
                    continue;
                }

                double r = reference[j];
                double t = test[n];
                numerator += r * t;
                referencePower += r * r;
                testPower += t * t;
            }

            if (referencePower <= 0 || testPower <= 0)
            {
                continue;
            }

            double correlation = numerator / Math.Sqrt(referencePower * testPower);
            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        return bestLag;
    }

    /// <summary>Computes sample-wise SNR of <paramref name="test"/> against <paramref name="reference"/>.</summary>
    /// <param name="reference">Reference signal.</param>
    /// <param name="test">Test signal, aligned with the reference.</param>
    /// <param name="skip">Samples skipped at both ends.</param>
    internal static double Snr(short[] reference, ReadOnlySpan<short> test, int skip)
    {
        ArgumentNullException.ThrowIfNull(reference);
        int length = Math.Min(reference.Length, test.Length);
        int start = Math.Min(skip, length);
        double signal = 0;
        double error = 0;
        for (int n = start; n < length; n++)
        {
            double difference = reference[n] - test[n];
            signal += reference[n] * (double)reference[n];
            error += difference * difference;
        }

        return error <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
    }

    /// <summary>Shifts a signal by whole samples, padding the vacated side with zeros.</summary>
    /// <param name="samples">Input samples.</param>
    /// <param name="shift">Positive delays, negative advances.</param>
    internal static short[] Shift(short[] samples, int shift)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (shift == 0)
        {
            return samples;
        }

        var output = new short[samples.Length];
        if (shift > 0)
        {
            Array.Copy(samples, 0, output, shift, samples.Length - shift);
        }
        else
        {
            Array.Copy(samples, -shift, output, 0, samples.Length + shift);
        }

        return output;
    }

    private static double Kernel(double u, double scale, int halfWidth)
    {
        if (Math.Abs(u) > halfWidth)
        {
            return 0;
        }

        double arg = Math.PI * scale * u;
        double sinc = arg == 0 ? 1 : Math.Sin(arg) / arg;
        double r = u / halfWidth;
        double window = 0.42 + (0.5 * Math.Cos(Math.PI * r)) + (0.08 * Math.Cos(2 * Math.PI * r));
        return scale * sinc * window;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
