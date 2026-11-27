namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Test-only independent band-limited resampler used to build the G2 reference signal. It shares
/// no code with <c>SilkInputResampler</c>, so the encoder under test cannot bias its own reference.
/// </summary>
/// <remarks>
/// A long Kaiser-windowed sinc (symmetric, odd length, centred on an input sample), beta 9 for more
/// than 80 dB of stopband attenuation. With an integer ratio the filter is centred exactly on every
/// decimated input sample, so the output has exactly zero delay: output sample k is the band-limited
/// signal at input time ratio * k. The input is treated as zero outside its extent.
/// </remarks>
internal static class IdealResampler
{
    private const double Beta = 9.0;
    private const int HalfLength = 1500;

    /// <summary>Resamples <paramref name="input"/> from <paramref name="inputRate"/> to <paramref name="outputRate"/> (integer ratio).</summary>
    internal static short[] Resample(short[] input, int inputRate, int outputRate)
    {
        if (inputRate % outputRate != 0)
        {
            throw new ArgumentException("The ratio must be an integer.", nameof(outputRate));
        }

        int ratio = inputRate / outputRate;

        // Cutoff just below the output Nyquist; the transition (about 100 Hz at this length and
        // beta) therefore ends at the Nyquist, so nothing aliases into the passband.
        double cutoff = ((outputRate / 2.0) - 60.0) / inputRate;
        var taps = new double[(2 * HalfLength) + 1];
        double i0Beta = Bessel0(Beta);
        for (int n = -HalfLength; n <= HalfLength; n++)
        {
            double x = (double)n / HalfLength;
            double window = Bessel0(Beta * Math.Sqrt(Math.Max(0, 1 - (x * x)))) / i0Beta;
            double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            taps[n + HalfLength] = sinc * window;
        }

        int outputLength = input.Length / ratio;
        var output = new short[outputLength];
        for (int k = 0; k < outputLength; k++)
        {
            int center = k * ratio;
            int from = Math.Max(0, center - HalfLength);
            int to = Math.Min(input.Length - 1, center + HalfLength);
            double acc = 0;
            for (int i = from; i <= to; i++)
            {
                acc += input[i] * taps[i - center + HalfLength];
            }

            output[k] = (short)Math.Clamp(Math.Round(acc), short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static double Bessel0(double x)
    {
        double sum = 1;
        double term = 1;
        double q = x * x / 4;
        for (int k = 1; k < 60; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < 1e-17 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
