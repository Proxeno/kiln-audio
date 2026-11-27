namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// A generated full-band corpus item: 6 s of 48 kHz mono 16-bit PCM with real content from the
/// low end up to 19 kHz. This is original work, generated in code from sinusoids and a seeded
/// xorshift noise source; it contains no third-party audio.
/// </summary>
/// <remarks>
/// The signal is the sum of a voiced speech-like part (a gliding fundamental with harmonics up to
/// 7 kHz under a 250 ms syllable envelope), two amplitude-modulated tones at 13 and 17 kHz, and
/// short high-passed noise bursts above 12.5 kHz. The RFC 8251 vectors are band-limited, so only
/// this item can show whether the 12 to 20 kHz band is coded at all.
/// </remarks>
internal static class FullbandCorpus
{
    /// <summary>Sample rate of the item in hertz.</summary>
    internal const int Rate = 48000;

    /// <summary>Lower edge of the band the gate measures, in hertz (above the 12 kHz super-wideband cutoff).</summary>
    internal const double HighBandLowHz = 13000;

    /// <summary>Upper edge of the band the gate measures, in hertz (below the fullband roll-off at 20 kHz).</summary>
    internal const double HighBandHighHz = 19000;

    /// <summary>Samples skipped at the start of every band-energy measurement (codec start-up).</summary>
    internal const int WarmupSamples = 4800;

    private const int TotalSamples = 6 * Rate;
    private const int FftSize = 1024;
    private const int FftHop = 512;

    private static readonly Lazy<short[]> MonoLazy = new(Build);

    /// <summary>The item, built once.</summary>
    internal static short[] Mono => MonoLazy.Value;

    /// <summary>Builds the item from scratch; deterministic, so every call returns the same samples.</summary>
    internal static short[] Build()
    {
        double[] voiced = BuildVoiced();
        double[] noise = BuildNoiseBursts();
        var pcm = new short[TotalSamples];
        for (int n = 0; n < TotalSamples; n++)
        {
            double t = (double)n / Rate;
            double tones =
                (1200.0 * (1.0 + (0.5 * Math.Sin(2.0 * Math.PI * 3.0 * t))) * Math.Sin(2.0 * Math.PI * 13000.0 * t))
                + (1200.0 * (1.0 + (0.5 * Math.Sin(2.0 * Math.PI * 5.0 * t))) * Math.Sin(2.0 * Math.PI * 17000.0 * t));
            double sum = voiced[n] + tones + noise[n];
            pcm[n] = (short)Math.Clamp(Math.Round(sum), short.MinValue, short.MaxValue);
        }

        return pcm;
    }

    /// <summary>
    /// Mean power (samples normalised by 32768) in <c>[lowHz, highHz)</c>: a 1024-point Hann-windowed
    /// FFT with hop 512 starting at <paramref name="skipSamples"/>, averaged over the frames. Zero
    /// when no whole frame fits.
    /// </summary>
    internal static double BandEnergy(ReadOnlySpan<short> pcm, double lowHz, double highHz, int skipSamples)
    {
        var window = new double[FftSize];
        double windowPower = 0;
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / FftSize));
            windowPower += window[i] * window[i];
        }

        var re = new double[FftSize];
        var im = new double[FftSize];
        double binHz = (double)Rate / FftSize;
        double total = 0;
        int frames = 0;
        for (int start = skipSamples; start + FftSize <= pcm.Length; start += FftHop)
        {
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = pcm[start + i] / 32768.0 * window[i];
                im[i] = 0;
            }

            Fft(re, im);
            double power = 0;
            for (int k = 1; k < FftSize / 2; k++)
            {
                double f = k * binHz;
                if (f >= lowHz && f < highHz)
                {
                    power += (re[k] * re[k]) + (im[k] * im[k]);
                }
            }

            // One-sided spectrum: 2/(N * sum(w^2)) turns |X|^2 into signal power.
            total += 2.0 * power / (FftSize * windowPower);
            frames++;
        }

        return frames == 0 ? 0 : total / frames;
    }

    /// <summary>The band energy of <see cref="BandEnergy"/> in dB, floored at -200 dB.</summary>
    internal static double BandEnergyDb(ReadOnlySpan<short> pcm, double lowHz, double highHz, int skipSamples) =>
        10.0 * Math.Log10(Math.Max(BandEnergy(pcm, lowHz, highHz, skipSamples), 1e-20));

    private static double[] BuildVoiced()
    {
        var voiced = new double[TotalSamples];
        double phase = 0;
        double peak = 0;
        const int SyllablePeriod = Rate / 4;
        const int SyllableOn = Rate / 5;
        for (int n = 0; n < TotalSamples; n++)
        {
            double t = (double)n / Rate;
            double f0 = 140.0 + (40.0 * Math.Sin(2.0 * Math.PI * 0.7 * t));
            phase += 2.0 * Math.PI * f0 / Rate;
            double sum = 0;
            for (int k = 1; k * f0 < 7000.0; k++)
            {
                sum += Math.Sin(k * phase) / k;
            }

            int inSyllable = n % SyllablePeriod;
            double envelope = inSyllable < SyllableOn
                ? 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * inSyllable / SyllableOn))
                : 0.0;
            voiced[n] = sum * envelope;
            peak = Math.Max(peak, Math.Abs(voiced[n]));
        }

        double scale = peak > 0 ? 9000.0 / peak : 0.0;
        for (int n = 0; n < TotalSamples; n++)
        {
            voiced[n] *= scale;
        }

        return voiced;
    }

    private static double[] BuildNoiseBursts()
    {
        // Fixed-seed xorshift32 white noise in [-1, 1).
        uint state = 0x2545F491;
        var white = new double[TotalSamples];
        for (int n = 0; n < TotalSamples; n++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            white[n] = (((double)state / 4294967296.0) * 2.0) - 1.0;
        }

        // 63-tap Hamming-windowed-sinc low-pass at 12.5 kHz, spectrally inverted into a high-pass.
        const int Taps = 63;
        const int Centre = (Taps - 1) / 2;
        double cutoff = 12500.0 / Rate;
        var low = new double[Taps];
        double lowSum = 0;
        for (int i = 0; i < Taps; i++)
        {
            int m = i - Centre;
            double sinc = m == 0 ? 2.0 * cutoff : Math.Sin(2.0 * Math.PI * cutoff * m) / (Math.PI * m);
            low[i] = sinc * (0.54 - (0.46 * Math.Cos(2.0 * Math.PI * i / (Taps - 1))));
            lowSum += low[i];
        }

        var high = new double[Taps];
        for (int i = 0; i < Taps; i++)
        {
            high[i] = -low[i] / lowSum;
        }

        high[Centre] += 1.0;

        // Burst gate: starts every 250 ms offset by 125 ms, 60 ms long, 15 ms decay constant.
        const int BurstPeriod = Rate / 4;
        const int BurstOffset = Rate / 8;
        const int BurstLength = Rate * 60 / 1000;
        double tau = 0.015 * Rate;
        var bursts = new double[TotalSamples];
        double peak = 0;
        for (int n = 0; n < TotalSamples; n++)
        {
            int since = (n - BurstOffset) % BurstPeriod;
            if (n < BurstOffset || since >= BurstLength)
            {
                continue;
            }

            double filtered = 0;
            for (int i = 0; i < Taps; i++)
            {
                int j = n - i;
                if (j >= 0)
                {
                    filtered += high[i] * white[j];
                }
            }

            bursts[n] = filtered * Math.Exp(-since / tau);
            peak = Math.Max(peak, Math.Abs(bursts[n]));
        }

        double scale = peak > 0 ? 2500.0 / peak : 0.0;
        for (int n = 0; n < TotalSamples; n++)
        {
            bursts[n] *= scale;
        }

        return bursts;
    }

    // In-place iterative radix-2 FFT.
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2.0 * Math.PI / length;
            double wr = Math.Cos(angle);
            double wi = Math.Sin(angle);
            for (int start = 0; start < n; start += length)
            {
                double cr = 1.0;
                double ci = 0.0;
                for (int k = 0; k < length / 2; k++)
                {
                    int a = start + k;
                    int b = a + (length / 2);
                    double tr = (re[b] * cr) - (im[b] * ci);
                    double ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double next = (cr * wr) - (ci * wi);
                    ci = (cr * wi) + (ci * wr);
                    cr = next;
                }
            }
        }
    }
}
