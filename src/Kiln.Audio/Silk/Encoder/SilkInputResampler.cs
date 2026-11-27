// Original work: a rational polyphase FIR input resampler for the SILK encoder.
// The SILK input resampler is non-normative (RFC 6716 §5.2.1 leaves it to the implementation).

using System;
using System.Numerics;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Converts the API-rate input of one SILK encoder to the internal rate (8, 12 or 16 kHz).
/// </summary>
/// <remarks>
/// <para>
/// This is original, non-normative work: a rational <c>L/M</c> polyphase FIR with a
/// Kaiser-windowed sinc prototype designed in code (about 85 dB stopband, passband flat to 0.9 of
/// the output Nyquist, stopband from 1.1 of it, so nothing aliases into the passband). The
/// prototype is centred, and the stream is delayed by <see cref="DelayInputSamples"/> input
/// samples (25 output samples when downsampling) so the output is causal.
/// </para>
/// <para>
/// The filter is designed once in the constructor; <see cref="Process"/> only runs the
/// multiply-accumulate loop and allocates nothing. Equal input and output rates bypass the
/// filter entirely (a pure copy with zero delay).
/// </para>
/// </remarks>
internal sealed class SilkInputResampler
{
    // Kaiser beta for about 85 dB of stopband attenuation.
    private const double KaiserBeta = 8.4;

    // Delay in output samples when downsampling; it sets the transition width (0.2 of the output
    // Nyquist frequency at the beta above).
    private const int DelayOutputSamplesDown = 25;

    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _interpolation;
    private readonly int _decimation;
    private readonly int _halfTaps;
    private readonly float[] _coefficients;
    private readonly bool _bypass;

    // History (2 * halfTaps samples) followed by the current block, so every output is one
    // contiguous dot product.
    private float[] _work;
    private long _inputCount;
    private long _timeNumerator;

    /// <summary>Creates a resampler from <paramref name="inputRate"/> to <paramref name="outputRate"/>.</summary>
    internal SilkInputResampler(int inputRate, int outputRate)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _work = [];
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
        _halfTaps = outputRate < inputRate
            ? (int)Math.Ceiling((double)DelayOutputSamplesDown * inputRate / outputRate)
            : 16;
        int taps = 2 * _halfTaps;

        // Cutoff at the output Nyquist frequency (relative to the input rate), centre of the
        // transition band.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate);
        _coefficients = new float[_interpolation * taps];
        double windowNorm = BesselI0(KaiserBeta);
        var phaseValues = new double[taps];
        for (int phase = 0; phase < _interpolation; phase++)
        {
            double fraction = (double)phase / _interpolation;
            double sum = 0;
            for (int tap = 0; tap < taps; tap++)
            {
                double d = tap - _halfTaps + 1 - fraction;
                double z = d / _halfTaps;
                double window = Math.Abs(z) >= 1 ? 0 : BesselI0(KaiserBeta * Math.Sqrt(1 - (z * z))) / windowNorm;
                double value = 2 * cutoff * Sinc(2 * cutoff * d) * window;
                phaseValues[tap] = value;
                sum += value;
            }

            // Unity DC gain in every phase avoids a static amplitude ripple.
            for (int tap = 0; tap < taps; tap++)
            {
                _coefficients[(phase * taps) + tap] = (float)(phaseValues[tap] / sum);
            }
        }
    }

    /// <summary>Gets the resampler delay in input samples.</summary>
    internal int DelayInputSamples => _bypass ? 0 : _halfTaps;

    /// <summary>Gets the resampler delay in output samples, rounded down.</summary>
    internal int DelayOutputSamples => _bypass
        ? 0
        : (int)(((long)_halfTaps * _outputRate) / _inputRate);

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

        int taps = 2 * _halfTaps;
        int history = taps;
        int needed = history + input.Length + 1;
        if (_work.Length < needed)
        {
            float[] grown = new float[needed];
            Array.Copy(_work, grown, Math.Min(_work.Length, history));
            _work = grown;
        }

        float[] work = _work;
        for (int i = 0; i < input.Length; i++)
        {
            work[history + i] = input[i];
        }

        work[history + input.Length] = input.Length > 0 ? input[^1] : 0;

        long blockStart = _inputCount;
        for (int i = 0; i < outputCount; i++)
        {
            long uNumerator = _timeNumerator - ((long)_halfTaps * _interpolation);
            long baseIndex = FloorDivide(uNumerator, _interpolation);
            int phase = (int)(uNumerator - (baseIndex * _interpolation));
            long first = baseIndex - _halfTaps + 1;
            int start = (int)(first - blockStart) + history;

            float accumulator = Dot(
                work.AsSpan(start, taps), _coefficients.AsSpan(phase * taps, taps));
            output[i] = (short)Math.Clamp((int)MathF.Round(accumulator), short.MinValue, short.MaxValue);
            _timeNumerator += _decimation;
        }

        // Slide the newest 2 * halfTaps samples down as the history of the next block.
        Array.Copy(work, input.Length, work, 0, history);

        _inputCount = blockStart + input.Length;
    }

    /// <summary>Clears the filter history and the output phase.</summary>
    internal void Reset()
    {
        Array.Clear(_work);
        _inputCount = 0;
        _timeNumerator = 0;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length;
        int i = 0;
        float total = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            int step = Vector<float>.Count;
            for (; i <= n - step; i += step)
            {
                acc += new Vector<float>(a.Slice(i, step)) * new Vector<float>(b.Slice(i, step));
            }

            total = Vector.Sum(acc);
        }

        for (; i < n; i++)
        {
            total += a[i] * b[i];
        }

        return total;
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
