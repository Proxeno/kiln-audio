using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// Streaming sample-rate converter for the SILK layer: 8, 12 or 16 kHz in, 8 to 48 kHz out.
/// </summary>
/// <remarks>
/// <para>
/// The converter is a rational polyphase FIR. The prototype is a Kaiser-windowed sinc low-pass
/// designed here (no table is copied from another implementation): the passband is flat to
/// 0.9 times the lower Nyquist frequency and the stopband starts at that Nyquist frequency.
/// The prototype is converted to minimum phase with the real-cepstrum method so the converter's
/// latency stays small; only its magnitude is constrained, not its phase.
/// </para>
/// <para>
/// At an output rate equal to the input rate the converter is an exact copy. The libopus decoder
/// also runs a "copy" resampler in that case; the delay it adds is relative to an internal SILK
/// signal that neither the public API nor <c>opus_demo</c> exposes, so it cannot be observed by
/// cross-correlation. The observable relative delay of a copy is zero, and that is what this class
/// does.
/// </para>
/// <para>
/// Measured against libopus 1.5.2 (48 kHz output, SILK-only streams): libopus's relative lag is
/// +3 output samples for 8 kHz in and -3 / -2 for 12 / 16 kHz in. The corresponding nominal
/// delays of this converter are 16 / 10 / 7 output samples. libopus's internal-rate decode carries
/// its copy-path delay (4/9/12 input samples) relative to an internal signal that is not exposed,
/// so a causal converter fed that PCM cannot reproduce the small negative relative lag; the
/// residual is 9-13 output samples (0.19-0.27 ms). See docs/architecture.md.
/// </para>
/// <para>
/// <see cref="Process"/> carries all state between calls, allocates nothing, and produces exactly
/// <c>floor((consumed * L) / M) - produced</c> samples for the call, where <c>L/M</c> is the
/// reduced output/input ratio. The caller must therefore size each output span from the total
/// input consumed so far; for non-integer ratios a call can produce one sample more or less than
/// <c>input.Length * L / M</c>.
/// </para>
/// </remarks>
internal sealed class SilkResampler
{
    private const double StopbandAttenuationDb = 70.0;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly int _up;      // L
    private readonly int _down;    // M
    private readonly bool _copy;

    private readonly int[] _phaseLength;
    private readonly int[] _phaseOffset;
    private readonly double[] _coefficients;
    private readonly int _historyLength;
    private readonly double[] _history;

    private long _consumed;
    private long _produced;
    private int _ringPosition;

    /// <summary>Creates a resampler for one rate pair.</summary>
    /// <param name="inRateHz">Input rate: 8000, 12000 or 16000.</param>
    /// <param name="outRateHz">Output rate: 8000, 12000, 16000, 24000 or 48000.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate is not supported.</exception>
    internal SilkResampler(int inRateHz, int outRateHz)
    {
        if (inRateHz is not (8000 or 12000 or 16000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inRateHz), inRateHz, "Input rate must be 8000, 12000 or 16000 Hz.");
        }

        if (outRateHz is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outRateHz), outRateHz, "Output rate must be 8000, 12000, 16000, 24000 or 48000 Hz.");
        }

        _inRate = inRateHz;
        _outRate = outRateHz;
        int divisor = Gcd(inRateHz, outRateHz);
        _up = outRateHz / divisor;
        _down = inRateHz / divisor;
        _copy = inRateHz == outRateHz;

        if (_copy)
        {
            _phaseLength = [0];
            _phaseOffset = [0];
            _coefficients = [];
            _historyLength = 1;
            _history = new double[1];
            DelaySamplesOut = 0;
            return;
        }

        int upsampledRate = inRateHz * _up;
        int lowerRate = Math.Min(inRateHz, outRateHz);
        double[] prototype = DesignPrototype(upsampledRate, lowerRate, _up);
        _historyLength = (prototype.Length + _up - 1) / _up;

        _phaseLength = new int[_up];
        _phaseOffset = new int[_up];
        int total = 0;
        for (int phase = 0; phase < _up; phase++)
        {
            _phaseLength[phase] = ((prototype.Length - 1 - phase) / _up) + 1;
            _phaseOffset[phase] = total;
            total += _phaseLength[phase];
        }

        _coefficients = new double[total];
        for (int phase = 0; phase < _up; phase++)
        {
            int offset = _phaseOffset[phase];
            int length = _phaseLength[phase];
            for (int tap = 0; tap < length; tap++)
            {
                _coefficients[offset + tap] = prototype[phase + (tap * _up)];
            }
        }

        DelaySamplesOut = ComputeDelaySamplesOut(prototype, _up, _down, inRateHz, outRateHz);
        _history = new double[_historyLength];
    }

    /// <summary>Nominal latency in output samples, rounded to the nearest sample.</summary>
    /// <remarks>
    /// This is the minimum-phase prototype's low-frequency group delay divided by the downsampling
    /// factor. The actual delay is frequency dependent; the oracle tests report the
    /// cross-correlation delay measured for each rate pair.
    /// </remarks>
    internal int DelaySamplesOut { get; }

    /// <summary>Input sample rate in hertz.</summary>
    internal int InputRateHz => _inRate;

    /// <summary>Output sample rate in hertz.</summary>
    internal int OutputRateHz => _outRate;

    /// <summary>Number of output samples the next call with <paramref name="inputLength"/> samples will emit.</summary>
    /// <param name="inputLength">Length of the next input block.</param>
    internal int OutputCount(int inputLength)
    {
        if (inputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength), inputLength, "Length must be non-negative.");
        }

        if (_copy)
        {
            return inputLength;
        }

        long target = ((_consumed + inputLength) * _up) / _down;
        return checked((int)(target - _produced));
    }

    /// <summary>Clears all filter and timing state, restoring the newly constructed state.</summary>
    internal void Reset()
    {
        Array.Clear(_history);
        _consumed = 0;
        _produced = 0;
        _ringPosition = 0;
    }

    /// <summary>Converts one block of input samples, carrying filter state between calls.</summary>
    /// <param name="input">Input samples at <see cref="InputRateHz"/>.</param>
    /// <param name="output">Destination; must hold at least <see cref="OutputCount"/> samples.</param>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    internal void Process(ReadOnlySpan<short> input, Span<short> output)
    {
        int required = OutputCount(input.Length);
        if (output.Length < required)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The output span needs {required} samples but has {output.Length}."),
                nameof(output));
        }

        if (_copy)
        {
            input.CopyTo(output);
            _consumed += input.Length;
            _produced += input.Length;
            return;
        }

        int written = 0;
        foreach (short sample in input)
        {
            _history[_ringPosition] = sample;
            _ringPosition++;
            if (_ringPosition == _historyLength)
            {
                _ringPosition = 0;
            }

            _consumed++;
            long available = (_consumed * _up) / _down;
            while (_produced < available)
            {
                output[written++] = NextSample();
                _produced++;
            }
        }
    }

    /// <summary>Evaluates the prototype filter's magnitude at a frequency, relative to DC.</summary>
    /// <param name="hertz">Frequency in hertz at the upsampled design rate.</param>
    /// <returns>Magnitude relative to the DC gain.</returns>
    internal double GainAt(double hertz)
    {
        if (_copy)
        {
            return 1.0;
        }

        int upsampledRate = _inRate * _up;
        double omega = 2 * Math.PI * hertz / upsampledRate;
        double re = 0;
        double im = 0;
        double dc = 0;
        for (int phase = 0; phase < _up; phase++)
        {
            int offset = _phaseOffset[phase];
            int length = _phaseLength[phase];
            for (int tap = 0; tap < length; tap++)
            {
                double c = _coefficients[offset + tap];
                int index = phase + (tap * _up);
                re += c * Math.Cos(omega * index);
                im -= c * Math.Sin(omega * index);
                dc += c;
            }
        }

        return Math.Sqrt((re * re) + (im * im)) / dc;
    }

    private short NextSample()
    {
        long index = _produced * _down;
        int phase = (int)(index % _up);
        int newest = (int)(index / _up);
        int offset = _phaseOffset[phase];
        int length = _phaseLength[phase];
        int oldestAvailable = (int)(_consumed - _historyLength);
        double sum = 0;
        for (int tap = 0; tap < length; tap++)
        {
            int source = newest - tap;
            if (source < 0)
            {
                break;
            }

            if (source < oldestAvailable)
            {
                break;
            }

            sum += _coefficients[offset + tap] * _history[source % _historyLength];
        }

        double rounded = Math.Round(sum);
        if (rounded > short.MaxValue)
        {
            return short.MaxValue;
        }

        if (rounded < short.MinValue)
        {
            return short.MinValue;
        }

        return (short)rounded;
    }

    private static double[] DesignPrototype(int upsampledRate, int lowerRate, int up)
    {
        double cutoff = lowerRate / (2.0 * upsampledRate);
        double transition = 0.1 * cutoff;
        double beta = 0.1102 * (StopbandAttenuationDb - 8.7);
        int length = (int)Math.Ceiling((StopbandAttenuationDb - 8) / (2.285 * 2 * Math.PI * transition)) + 1;
        length = Math.Max(length, 4 * up);
        if ((length & 1) == 0)
        {
            length++;
        }

        int center = length / 2;
        double half = length / 2.0;
        double bessel = BesselI0(beta);
        var linear = new double[length];
        double dc = 0;
        for (int k = 0; k < length; k++)
        {
            double u = k - center;
            double angle = 2 * Math.PI * cutoff * u;
            double sinc = u == 0 ? 1 : Math.Sin(angle) / angle;
            double ratio = u / half;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (ratio * ratio)))) / bessel;
            linear[k] = 2 * cutoff * sinc * window;
            dc += linear[k];
        }

        double[] minimum = ToMinimumPhase(linear);
        double gain = 0;
        foreach (double coefficient in minimum)
        {
            gain += coefficient;
        }

        // Zero stuffing divides the DC gain by L; scale so a constant input maps to itself.
        double scale = up / gain;
        for (int k = 0; k < minimum.Length; k++)
        {
            minimum[k] *= scale;
        }

        return minimum;
    }

    /// <summary>Converts a linear-phase prototype to its minimum-phase equivalent.</summary>
    private static double[] ToMinimumPhase(double[] linear)
    {
        int size = 1;
        while (size < linear.Length * 4)
        {
            size <<= 1;
        }

        var re = new double[size];
        var im = new double[size];
        for (int i = 0; i < linear.Length; i++)
        {
            re[i] = linear[i];
        }

        Fft(re, im);
        var logMagnitude = new double[size];
        for (int i = 0; i < size; i++)
        {
            logMagnitude[i] = 0.5 * Math.Log(Math.Max((re[i] * re[i]) + (im[i] * im[i]), 1e-30));
        }

        // Real cepstrum, then fold it to the causal (minimum-phase) form.
        var cepstrumRe = (double[])logMagnitude.Clone();
        var cepstrumIm = new double[size];
        Fft(cepstrumRe, cepstrumIm);
        for (int i = 0; i < size; i++)
        {
            cepstrumRe[i] /= size;
            cepstrumIm[i] /= size;
        }

        for (int i = 1; i < size / 2; i++)
        {
            cepstrumRe[i] *= 2;
            cepstrumIm[i] *= 2;
        }

        for (int i = (size / 2) + 1; i < size; i++)
        {
            cepstrumRe[i] = 0;
            cepstrumIm[i] = 0;
        }

        Fft(cepstrumRe, cepstrumIm);
        for (int i = 0; i < size; i++)
        {
            double magnitude = Math.Exp(cepstrumRe[i]);
            cepstrumRe[i] = magnitude * Math.Cos(cepstrumIm[i]);
            cepstrumIm[i] = -magnitude * Math.Sin(cepstrumIm[i]);
        }

        Fft(cepstrumRe, cepstrumIm);
        var result = new double[linear.Length];
        for (int i = 0; i < linear.Length; i++)
        {
            result[i] = cepstrumRe[i] / size;
        }

        return result;
    }

    /// <summary>
    /// Estimates the effective latency by cross-correlating the impulse response with the ideal
    /// band-limited interpolation of an impulse.
    /// </summary>
    private static int ComputeDelaySamplesOut(
        double[] prototype, int up, int down, int inRate, int outRate)
    {
        int length = ((prototype.Length - 1) / down) + 1;
        var impulse = new double[length];
        for (int m = 0; m < length; m++)
        {
            impulse[m] = prototype[m * down];
        }

        double scale = Math.Min(1.0, (double)outRate / inRate);
        int search = Math.Max(16, length);
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = -4; lag <= search; lag++)
        {
            double correlation = 0;
            for (int m = 0; m < length; m++)
            {
                int index = m - lag;
                double t = index * (double)down / up;
                double arg = Math.PI * scale * t;
                double ideal = arg == 0 ? scale : scale * Math.Sin(arg) / arg;
                correlation += impulse[m] * ideal;
            }

            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        return Math.Max(0, bestLag);
    }

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
            double angle = -2 * Math.PI / length;
            double stepRe = Math.Cos(angle);
            double stepIm = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double currentRe = 1;
                double currentIm = 0;
                for (int j = 0; j < length / 2; j++)
                {
                    int even = i + j;
                    int odd = even + (length / 2);
                    double oddRe = (re[odd] * currentRe) - (im[odd] * currentIm);
                    double oddIm = (re[odd] * currentIm) + (im[odd] * currentRe);
                    double evenRe = re[even];
                    double evenIm = im[even];
                    re[even] = evenRe + oddRe;
                    im[even] = evenIm + oddIm;
                    re[odd] = evenRe - oddRe;
                    im[odd] = evenIm - oddIm;
                    double nextRe = (currentRe * stepRe) - (currentIm * stepIm);
                    currentIm = (currentRe * stepIm) + (currentIm * stepRe);
                    currentRe = nextRe;
                }
            }
        }
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double scaled = x * x / 4;
        for (int i = 1; i < 64; i++)
        {
            term *= scaled / (i * i);
            sum += term;
            if (term < 1e-15 * sum)
            {
                break;
            }
        }

        return sum;
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
