using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
/// At an output rate equal to the input rate the converter is an exact copy followed by the
/// alignment delay. libopus also runs a "copy" resampler in that case, and its output is delayed
/// relative to the SILK synthesis by a fixed number of samples (5/10/13 for mono and 4/9/12 for
/// stereo at 8/12/16 kHz). The alignment delay reproduces that, so equal rates are a pure delay,
/// not a zero-delay copy.
/// </para>
/// <para>
/// The converter carries a short integer alignment delay (see <see cref="AlignmentDelay"/>) so
/// that the total delay of SILK synthesis followed by this resampler equals libopus's total delay
/// from its SILK synthesis to the output at the requested rate, for every rate pair and channel
/// count. That alignment is what makes the end-to-end SILK-only output line up with libopus to the
/// sample; the measured 48 kHz alignment is 19/29/31 output samples for mono and 13/25/28 for
/// stereo at 8/12/16 kHz in. See docs/architecture.md.
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

    private const int BlockCapacity = 1024;
    private const int Lanes = 8;

    private static readonly bool UseFma = Fma.IsSupported || AdvSimd.Arm64.IsSupported;

    private readonly int[] _phaseLength;
    private readonly int[] _phaseOffset;
    private readonly double[] _coefficients;

    // Polyphase float banks: per phase the taps are stored oldest-first (reversed) and padded at
    // the front with zeros to a multiple of Lanes, so one forward dot product over the linear
    // history evaluates the phase.
    private readonly float[] _bank;
    private readonly int[] _bankOffset;
    private readonly int[] _bankLength;
    private readonly int _historyLength;

    // Linear input history: [_historyLength carried samples][up to BlockCapacity new samples].
    private readonly float[] _buffer;

    // Integer delay applied after the filter so the total delay matches libopus. Oldest first.
    private readonly short[] _delayRing;

    private long _consumed;
    private long _produced;

    /// <summary>Creates a resampler for one rate pair and channel count.</summary>
    /// <param name="inRateHz">Input rate: 8000, 12000 or 16000.</param>
    /// <param name="outRateHz">Output rate: 8000, 12000, 16000, 24000 or 48000.</param>
    /// <param name="channels">Channel count of the stream being decoded, 1 or 2.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate or the channel count is not supported.</exception>
    internal SilkResampler(int inRateHz, int outRateHz, int channels = 1)
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

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 or 2.");
        }

        _inRate = inRateHz;
        _outRate = outRateHz;
        int divisor = Gcd(inRateHz, outRateHz);
        _up = outRateHz / divisor;
        _down = inRateHz / divisor;
        _copy = inRateHz == outRateHz;

        int alignment = AlignmentDelay(inRateHz, outRateHz, channels);
        _delayRing = new short[alignment];

        if (_copy)
        {
            _phaseLength = [0];
            _phaseOffset = [0];
            _coefficients = [];
            _bank = [];
            _bankOffset = [0];
            _bankLength = [0];
            _historyLength = 0;
            _buffer = [];
            DelaySamplesOut = alignment;
            return;
        }

        int upsampledRate = inRateHz * _up;
        int lowerRate = Math.Min(inRateHz, outRateHz);
        double[] prototype = DesignPrototype(upsampledRate, lowerRate, _up);
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

        _bankOffset = new int[_up];
        _bankLength = new int[_up];
        int bankTotal = 0;
        int longest = 0;
        for (int phase = 0; phase < _up; phase++)
        {
            int padded = (_phaseLength[phase] + Lanes - 1) / Lanes * Lanes;
            _bankOffset[phase] = bankTotal;
            _bankLength[phase] = padded;
            bankTotal += padded;
            longest = Math.Max(longest, padded);
        }

        _bank = new float[bankTotal];
        for (int phase = 0; phase < _up; phase++)
        {
            int length = _phaseLength[phase];
            int padded = _bankLength[phase];
            for (int tap = 0; tap < length; tap++)
            {
                _bank[_bankOffset[phase] + padded - 1 - tap] = (float)_coefficients[_phaseOffset[phase] + tap];
            }
        }

        // A decimating output can sit up to ceil(down / up) samples behind the newest input, so the
        // carried history covers the longest phase plus that lag.
        _historyLength = longest + ((_down + _up - 1) / _up);
        _buffer = new float[_historyLength + BlockCapacity];

        DelaySamplesOut =
            ComputeDelaySamplesOut(prototype, _up, _down, inRateHz, outRateHz) + alignment;
    }

    /// <summary>Total nominal latency in output samples.</summary>
    /// <remarks>
    /// This is the minimum-phase prototype's low-frequency group delay divided by the downsampling
    /// factor, plus the integer alignment delay that matches libopus's total decode-to-output
    /// delay. The actual delay is frequency dependent; the oracle tests report the
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
        Array.Clear(_buffer);
        Array.Clear(_delayRing);
        _consumed = 0;
        _produced = 0;
    }

    /// <summary>Copies the complete streaming state of <paramref name="other"/> into this resampler.</summary>
    /// <param name="other">A resampler built for the same rate pair.</param>
    internal void CopyStateFrom(SilkResampler other)
    {
        Array.Copy(other._buffer, _buffer, _buffer.Length);
        Array.Copy(other._delayRing, _delayRing, _delayRing.Length);
        _consumed = other._consumed;
        _produced = other._produced;
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

        int outputOffset = 0;
        int inputOffset = 0;
        while (inputOffset < input.Length)
        {
            int chunk = Math.Min(BlockCapacity, input.Length - inputOffset);
            int produced = _copy ? chunk : FilterBlock(input.Slice(inputOffset, chunk), output.Slice(outputOffset));
            if (_copy)
            {
                input.Slice(inputOffset, chunk).CopyTo(output.Slice(outputOffset));
                _consumed += chunk;
                _produced += chunk;
            }

            ApplyDelay(output.Slice(outputOffset, produced));
            outputOffset += produced;
            inputOffset += chunk;
        }
    }

    /// <summary>Filters one block of at most <see cref="BlockCapacity"/> input samples.</summary>
    /// <returns>The number of output samples written.</returns>
    private int FilterBlock(ReadOnlySpan<short> input, Span<short> output)
    {
        int history = _historyLength;
        Span<float> buffer = _buffer;
        for (int i = 0; i < input.Length; i++)
        {
            buffer[history + i] = input[i];
        }

        long consumedBefore = _consumed;
        long target = ((consumedBefore + input.Length) * _up) / _down;
        int count = checked((int)(target - _produced));

        // Position of the first output: index = produced * down, phase = index % up and newest
        // input sample = index / up, then advanced incrementally.
        long index = _produced * _down;
        long newestAbsolute = index / _up;
        int phase = (int)(index - (newestAbsolute * _up));
        int stepSamples = _down / _up;
        int stepPhase = _down % _up;
        int newest = history + (int)(newestAbsolute - consumedBefore);

        ref float bufferRef = ref MemoryMarshal.GetArrayDataReference(_buffer);
        ref float bankRef = ref MemoryMarshal.GetArrayDataReference(_bank);
        for (int n = 0; n < count; n++)
        {
            int length = _bankLength[phase];
            float sum = Dot(
                ref Unsafe.Add(ref bufferRef, newest - length + 1),
                ref Unsafe.Add(ref bankRef, _bankOffset[phase]),
                length);
            output[n] = ToInt16(sum);
            newest += stepSamples;
            phase += stepPhase;
            if (phase >= _up)
            {
                phase -= _up;
                newest++;
            }
        }

        // Carry the newest samples as the next block's history.
        buffer.Slice(input.Length, history).CopyTo(buffer);
        _consumed += input.Length;
        _produced += count;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ToInt16(float sum)
    {
        float rounded = MathF.Round(sum);
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

    /// <summary>Dot product of <paramref name="length"/> (a multiple of 8) floats.</summary>
    private static float Dot(ref float x, ref float c, int length)
    {
        Vector128<float> acc0 = Vector128<float>.Zero;
        Vector128<float> acc1 = Vector128<float>.Zero;
        if (UseFma)
        {
            for (int i = 0; i < length; i += Lanes)
            {
                acc0 = Vector128.FusedMultiplyAdd(
                    Vector128.LoadUnsafe(ref x, (nuint)i), Vector128.LoadUnsafe(ref c, (nuint)i), acc0);
                acc1 = Vector128.FusedMultiplyAdd(
                    Vector128.LoadUnsafe(ref x, (nuint)(i + 4)), Vector128.LoadUnsafe(ref c, (nuint)(i + 4)), acc1);
            }
        }
        else
        {
            for (int i = 0; i < length; i += Lanes)
            {
                acc0 += Vector128.LoadUnsafe(ref x, (nuint)i) * Vector128.LoadUnsafe(ref c, (nuint)i);
                acc1 += Vector128.LoadUnsafe(ref x, (nuint)(i + 4)) * Vector128.LoadUnsafe(ref c, (nuint)(i + 4));
            }
        }

        return Vector128.Sum(acc0 + acc1);
    }

    /// <summary>Delays a block of output by the alignment delay, carrying the tail between blocks.</summary>
    private void ApplyDelay(Span<short> block)
    {
        int delay = _delayRing.Length;
        if (delay == 0 || block.Length == 0)
        {
            return;
        }

        Span<short> tail = stackalloc short[delay];
        if (block.Length >= delay)
        {
            block[^delay..].CopyTo(tail);
            block[..^delay].CopyTo(block[delay..]);
            _delayRing.CopyTo(block);
            tail.CopyTo(_delayRing);
        }
        else
        {
            int n = block.Length;
            _delayRing.AsSpan(0, n).CopyTo(tail);
            _delayRing.AsSpan(n).CopyTo(_delayRing);
            block.CopyTo(_delayRing.AsSpan(delay - n));
            tail[..n].CopyTo(block);
        }
    }

    /// <summary>
    /// Integer delay, in output samples, added after the filter so that the total delay of SILK
    /// synthesis plus this resampler equals libopus's total delay from its SILK synthesis to the
    /// output at <paramref name="outRateHz"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 6716 §4.2.9 makes the SILK resampler non-normative, but our goal is sample alignment
    /// with libopus 1.5.2, so the alignment is calibrated to it. Two effects are folded in:
    /// libopus's internal copy-path buffering, which delays the signal it presents at the internal
    /// rate by 4-13 samples depending on rate and channel count (one more sample for mono, because
    /// the mono path buffers the mid history), and the difference between libopus's resampler and
    /// our own filter's effective delay. The values were obtained by cross-correlating our output
    /// against <c>opus_demo -d &lt;rate&gt;</c> for SILK-only streams and choosing the integer that
    /// puts the peak at lag 0; see <c>ResampleConformanceTests</c>.
    /// </para>
    /// <para>
    /// The table is a calibration of this filter, not a libopus table: if the prototype design
    /// changes, these values must be re-measured.
    /// </para>
    /// </remarks>
    private static int AlignmentDelay(int inRateHz, int outRateHz, int channels) => channels == 2
        ? (inRateHz, outRateHz) switch
        {
            (8000, 8000) => 4,
            (8000, 12000) => 4,
            (8000, 16000) => 4,
            (8000, 24000) => 7,
            (8000, 48000) => 13,
            (12000, 8000) => 4,
            (12000, 12000) => 9,
            (12000, 16000) => 8,
            (12000, 24000) => 14,
            (12000, 48000) => 25,
            (16000, 8000) => 4,
            (16000, 12000) => 7,
            (16000, 16000) => 12,
            (16000, 24000) => 14,
            (16000, 48000) => 28,
            _ => throw new ArgumentOutOfRangeException(nameof(outRateHz), outRateHz, "Unsupported rate pair."),
        }
        : (inRateHz, outRateHz) switch
        {
            (8000, 8000) => 5,
            (8000, 12000) => 5,
            (8000, 16000) => 6,
            (8000, 24000) => 10,
            (8000, 48000) => 19,
            (12000, 8000) => 5,
            (12000, 12000) => 10,
            (12000, 16000) => 10,
            (12000, 24000) => 16,
            (12000, 48000) => 29,
            (16000, 8000) => 5,
            (16000, 12000) => 8,
            (16000, 16000) => 13,
            (16000, 24000) => 16,
            (16000, 48000) => 31,
            _ => throw new ArgumentOutOfRangeException(nameof(outRateHz), outRateHz, "Unsupported rate pair."),
        };

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
