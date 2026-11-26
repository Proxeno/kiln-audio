// Original work: a rational polyphase FIR input resampler for the SILK encoder.
// The SILK input resampler is non-normative (RFC 6716 §5.2.1 leaves it to the implementation).

using System;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Converts the API-rate input of one SILK encoder to the internal rate (8, 12 or 16 kHz).
/// </summary>
/// <remarks>
/// <para>
/// This is original, non-normative work: a rational <c>L/M</c> polyphase FIR with a
/// Kaiser-windowed sinc prototype designed in code. It is anti-aliased for downsampling and
/// interpolates for upsampling. The prototype is centred, and the stream is delayed by
/// <see cref="DelayInputSamples"/> input samples so the output is causal.
/// </para>
/// <para>
/// The filter is designed once in the constructor; <see cref="Process"/> only runs the
/// multiply-accumulate loop and allocates nothing. Equal input and output rates bypass the
/// filter entirely (a pure copy with zero delay).
/// </para>
/// </remarks>
internal sealed class SilkInputResampler
{
    // Half the prototype support, in input samples. 32 taps per phase is ample for a 16 kHz
    // speech band and keeps the per-sample cost well below the analysis stages.
    private const int HalfTaps = 16;

    // Kaiser beta: about 80 dB of stopband attenuation at this length.
    private const double KaiserBeta = 8.0;

    // Fraction of the lower Nyquist frequency the passband is allowed to use.
    private const double CutoffFraction = 0.92;

    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _interpolation;
    private readonly int _decimation;
    private readonly double[] _coefficients;
    private readonly bool _bypass;

    // The kernel spans 2 * HalfTaps taps behind the newest sample, so that much history is kept.
    private const int HistoryLength = 2 * HalfTaps;
    private readonly double[] _history = new double[HistoryLength];
    private long _inputCount;
    private long _timeNumerator;

    /// <summary>Creates a resampler from <paramref name="inputRate"/> to <paramref name="outputRate"/>.</summary>
    internal SilkInputResampler(int inputRate, int outputRate)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        if (inputRate == outputRate)
        {
            _bypass = true;
            _interpolation = 1;
            _decimation = 1;
            _coefficients = [];
            return;
        }

        int divisor = GreatestCommonDivisor(inputRate, outputRate);
        _interpolation = outputRate / divisor;
        _decimation = inputRate / divisor;

        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * CutoffFraction;
        _coefficients = new double[_interpolation * 2 * HalfTaps];
        double windowNorm = BesselI0(KaiserBeta);
        for (int phase = 0; phase < _interpolation; phase++)
        {
            double fraction = (double)phase / _interpolation;
            double sum = 0;
            int offset = phase * 2 * HalfTaps;
            for (int tap = 0; tap < 2 * HalfTaps; tap++)
            {
                double position = fraction + tap - HalfTaps;
                double sinc = 2 * cutoff * Sinc(2 * cutoff * position);

                double z = (tap + fraction - HalfTaps + 0.5) / HalfTaps;
                double window = Math.Abs(z) >= 1
                    ? 0
                    : BesselI0(KaiserBeta * Math.Sqrt(1 - (z * z))) / windowNorm;

                double value = sinc * window;
                _coefficients[offset + tap] = value;
                sum += value;
            }

            // Normalize every phase to unity DC gain. The window slightly changes the phase sum,
            // so per-phase normalization avoids a static amplitude ripple.
            if (sum != 0)
            {
                for (int tap = 0; tap < 2 * HalfTaps; tap++)
                {
                    _coefficients[offset + tap] /= sum;
                }
            }
        }
    }

    /// <summary>Gets the resampler delay in input samples.</summary>
    internal int DelayInputSamples => _bypass ? 0 : HalfTaps;

    /// <summary>Gets the resampler delay in output samples, rounded down.</summary>
    internal int DelayOutputSamples => _bypass
        ? 0
        : (int)(((long)HalfTaps * _outputRate) / _inputRate);

    /// <summary>Returns the number of output samples <see cref="Process"/> writes for a block.</summary>
    internal int OutputLength(int inputLength) =>
        (int)(((long)inputLength * _outputRate) / _inputRate);

    /// <summary>Resamples one block of 16-bit samples.</summary>
    /// <param name="input">Input samples at the API rate.</param>
    /// <param name="output">Destination for the internal-rate samples.</param>
    internal void Process(ReadOnlySpan<short> input, Span<short> output)
    {
        int outputCount = OutputLength(input.Length);
        if (outputCount > output.Length)
        {
            throw new ArgumentException("The output buffer is too small for the resampled block.", nameof(output));
        }

        if (_bypass)
        {
            for (int i = 0; i < outputCount; i++)
            {
                output[i] = input[i];
            }

            _inputCount += input.Length;
            return;
        }

        long blockStart = _inputCount;
        long blockEnd = blockStart + input.Length;
        long historyStart = blockStart - HistoryLength;

        for (int i = 0; i < outputCount; i++)
        {
            long uNumerator = _timeNumerator - ((long)HalfTaps * _interpolation);
            long baseIndex = FloorDivide(uNumerator, _interpolation);
            int phase = (int)(uNumerator - (baseIndex * _interpolation));
            int coefficientOffset = phase * 2 * HalfTaps;

            double accumulator = 0;
            for (int tap = 0; tap < 2 * HalfTaps; tap++)
            {
                long index = baseIndex + HalfTaps - tap;
                double sample;
                if (index >= blockStart)
                {
                    if (index < blockEnd)
                    {
                        sample = input[(int)(index - blockStart)];
                    }
                    else
                    {
                        sample = input[input.Length - 1];
                    }
                }
                else if (index >= historyStart)
                {
                    sample = _history[index - historyStart];
                }
                else
                {
                    sample = 0;
                }

                accumulator += sample * _coefficients[coefficientOffset + tap];
            }

            output[i] = (short)Math.Clamp((long)Math.Round(accumulator), short.MinValue, short.MaxValue);
            _timeNumerator += _decimation;
        }

        // Keep the newest input samples for the next block, sliding older history down.
        int keep = Math.Min(HistoryLength, input.Length);
        _history.AsSpan(keep).CopyTo(_history);
        for (int i = 0; i < keep; i++)
        {
            _history[HistoryLength - keep + i] = input[input.Length - keep + i];
        }

        _inputCount = blockEnd;
        _ = historyStart;
    }

    /// <summary>Clears the filter history and the output phase.</summary>
    internal void Reset()
    {
        _history.AsSpan().Clear();
        _inputCount = 0;
        _timeNumerator = 0;
    }

    private static long FloorDivide(long numerator, int denominator)
    {
        long quotient = numerator / denominator;
        if (numerator % denominator != 0 && numerator < 0)
        {
            quotient--;
        }

        return quotient;
    }

    private static double Sinc(double value) =>
        Math.Abs(value) < 1e-9 ? 1.0 : Math.Sin(Math.PI * value) / (Math.PI * value);

    private static double BesselI0(double value)
    {
        double sum = 1;
        double term = 1;
        double half = value / 2;
        for (int k = 1; k < 32; k++)
        {
            term *= (half / k) * (half / k);
            sum += term;
            if (term < sum * 1e-12)
            {
                break;
            }
        }

        return sum;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
