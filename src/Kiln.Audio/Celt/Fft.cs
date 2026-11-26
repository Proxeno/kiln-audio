using System.Collections.Concurrent;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Celt;

/// <summary>A single-precision complex number used by the scalar CELT kernels.</summary>
/// <remarks>
/// The MDCT and window tables are single precision (RFC 6716 §4.3.7 is a floating-point
/// definition); this struct keeps the FFT in the same arithmetic so the scalar reference can be
/// used as the oracle for later SIMD work.
/// </remarks>
internal readonly record struct Complex32(float Real, float Imag)
{
    /// <summary>Adds two complex values.</summary>
    public static Complex32 operator +(Complex32 left, Complex32 right) =>
        new(left.Real + right.Real, left.Imag + right.Imag);

    /// <summary>Multiplies two complex values.</summary>
    public static Complex32 operator *(Complex32 left, Complex32 right) =>
        new(
            (left.Real * right.Real) - (left.Imag * right.Imag),
            (left.Real * right.Imag) + (left.Imag * right.Real));

    /// <summary>Multiplies <paramref name="value"/> by the complex conjugate of <paramref name="factor"/>.</summary>
    public static Complex32 MultiplyConjugate(Complex32 value, Complex32 factor) =>
        new(
            (value.Real * factor.Real) + (value.Imag * factor.Imag),
            (value.Imag * factor.Real) - (value.Real * factor.Imag));
}

/// <summary>
/// A complex fast Fourier transform with radices 2, 3, 4 and 5, precomputed per size.
/// </summary>
/// <remarks>
/// <para>
/// This is an original mixed-radix decimation-in-time Cooley-Tukey implementation. The size is
/// factored as <c>N = r0 * r1 * ...</c> with each radix in {2, 3, 4, 5}; for radix <c>r</c> and
/// sub-transform length <c>m = N/r</c> the transform is
/// <c>X[p + m*q] = sum_s W_N^(s*p) W_r^(s*q) Z_s[p]</c>, where <c>Z_s</c> is the <c>m</c>-point
/// transform of the stride-<c>r</c> subsequence <c>x[s], x[s+r], ...</c>. The combination is done
/// in place for each <c>p</c>.
/// </para>
/// <para>
/// The combine stage is delegated to <see cref="FftKernels"/>, which has a scalar reference path
/// and a <c>Vector128</c> path (radix 2, 3, 4 and 5) over separate real/imaginary arrays. The
/// kernel is the structural change from the PERF-0 reference: the generic <c>r * r</c> small-DFT
/// multiply loop is gone, replaced by the fixed radix-4/2/3/5 butterflies, and the transform is
/// held in a structure-of-arrays layout so the vector path needs no lane shuffles.
/// </para>
/// <para>
/// The transform is out of place and stateless: all twiddle tables are immutable and the working
/// buffers are <c>stackalloc</c>-ed per call, so instances returned by <see cref="Get"/> may be
/// shared across threads. Execute allocates nothing. The input and output spans must not overlap.
/// </para>
/// </remarks>
internal sealed class Fft
{
    private static readonly ConcurrentDictionary<int, Fft> Cache = new();

    private readonly int _size;
    private readonly int[] _radices;
    private readonly int[] _halves;
    private readonly int[] _nodeCounts;
    private readonly int[] _leafDestination;
    private readonly int[][] _nodeOffsets;
    private readonly float[][] _twiddleReal;
    private readonly float[][] _twiddleImaginary;
    private readonly float[][] _twiddleRealConjugate;
    private readonly float[][] _twiddleImaginaryConjugate;

    private Fft(int size)
    {
        _size = size;
        _radices = Factor(size);
        int levels = _radices.Length;
        _halves = new int[levels];
        _nodeCounts = new int[levels];
        _twiddleReal = new float[levels][];
        _twiddleImaginary = new float[levels][];
        _twiddleRealConjugate = new float[levels][];
        _twiddleImaginaryConjugate = new float[levels][];

        int n = size;
        int nodes = 1;
        for (int level = 0; level < levels; level++)
        {
            int radix = _radices[level];
            int half = n / radix;
            _halves[level] = half;
            _nodeCounts[level] = nodes;
            nodes *= radix;

            // Twiddle W_n^(s*p), stored per source index as [s*half + p] so consecutive p values
            // are contiguous. The conjugate tables serve the inverse transform directly.
            var real = new float[radix * half];
            var imaginary = new float[radix * half];
            var realConjugate = new float[radix * half];
            var imaginaryConjugate = new float[radix * half];
            for (int s = 0; s < radix; s++)
            {
                for (int p = 0; p < half; p++)
                {
                    Complex32 root = UnitRoot(s * p, n);
                    int index = (s * half) + p;
                    real[index] = root.Real;
                    imaginary[index] = root.Imag;
                    realConjugate[index] = root.Real;
                    imaginaryConjugate[index] = -root.Imag;
                }
            }

            _twiddleReal[level] = real;
            _twiddleImaginary[level] = imaginary;
            _twiddleRealConjugate[level] = realConjugate;
            _twiddleImaginaryConjugate[level] = imaginaryConjugate;
            n = half;
        }

        // Precompute the leaf permutation and the node offsets so Execute has no mixed-radix
        // division in its inner loops.
        _leafDestination = new int[size];
        for (int leaf = 0; leaf < size; leaf++)
        {
            int remaining = leaf;
            int destination = 0;
            for (int level = 0; level < levels; level++)
            {
                destination += (remaining % _radices[level]) * _halves[level];
                remaining /= _radices[level];
            }

            _leafDestination[leaf] = destination;
        }

        _nodeOffsets = new int[levels][];
        for (int level = 0; level < levels; level++)
        {
            int nodeCount = _nodeCounts[level];
            var offsets = new int[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                int remaining = node;
                int offset = 0;
                for (int m = 0; m < level; m++)
                {
                    offset += (remaining % _radices[m]) * _halves[m];
                    remaining /= _radices[m];
                }

                offsets[node] = offset;
            }

            _nodeOffsets[level] = offsets;
        }
    }

    /// <summary>Gets the transform length.</summary>
    public int Size => _size;

    /// <summary>Gets a shared, immutable transform of the requested size.</summary>
    /// <param name="size">The transform length; its prime factors must be 2, 3 or 5.</param>
    /// <returns>The cached transform.</returns>
    public static Fft Get(int size) =>
        Cache.GetOrAdd(size, static value => new Fft(value));

    /// <summary>Computes the forward transform <c>X[k] = sum_n x[n] e^(-i*2*pi*n*k/N)</c>.</summary>
    /// <param name="input">The input, at least <see cref="Size"/> samples.</param>
    /// <param name="output">The output, at least <see cref="Size"/> samples; must not overlap the input.</param>
    public void Forward(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _size)
        {
            throw new ArgumentException("The input is shorter than the transform size.", nameof(input));
        }

        if (output.Length < _size)
        {
            throw new ArgumentException("The output is shorter than the transform size.", nameof(output));
        }

        Execute(input, output, inverse: false, SimdConfig.UseSimd);
    }

    /// <summary>Forward transform on the scalar path, used by the parity tests.</summary>
    /// <param name="input">The input, at least <see cref="Size"/> samples.</param>
    /// <param name="output">The output, at least <see cref="Size"/> samples.</param>
    internal void ForwardScalar(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _size)
        {
            throw new ArgumentException("The input is shorter than the transform size.", nameof(input));
        }

        if (output.Length < _size)
        {
            throw new ArgumentException("The output is shorter than the transform size.", nameof(output));
        }

        Execute(input, output, inverse: false, useVector: false);
    }

    /// <summary>Computes the inverse transform <c>x[n] = (1/N) sum_k X[k] e^(+i*2*pi*n*k/N)</c>.</summary>
    /// <param name="input">The input, at least <see cref="Size"/> samples.</param>
    /// <param name="output">The output, at least <see cref="Size"/> samples; must not overlap the input.</param>
    public void Inverse(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _size)
        {
            throw new ArgumentException("The input is shorter than the transform size.", nameof(input));
        }

        if (output.Length < _size)
        {
            throw new ArgumentException("The output is shorter than the transform size.", nameof(output));
        }

        Execute(input, output, inverse: true, SimdConfig.UseSimd);
        Scale(output);
    }

    /// <summary>Inverse transform on the scalar path, used by the parity tests.</summary>
    /// <param name="input">The input, at least <see cref="Size"/> samples.</param>
    /// <param name="output">The output, at least <see cref="Size"/> samples.</param>
    internal void InverseScalar(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (input.Length < _size)
        {
            throw new ArgumentException("The input is shorter than the transform size.", nameof(input));
        }

        if (output.Length < _size)
        {
            throw new ArgumentException("The output is shorter than the transform size.", nameof(output));
        }

        Execute(input, output, inverse: true, useVector: false);
        Scale(output);
    }

    private void Scale(Span<Complex32> output)
    {
        float scale = 1f / _size;
        for (int i = 0; i < _size; i++)
        {
            Complex32 value = output[i];
            output[i] = new Complex32(value.Real * scale, value.Imag * scale);
        }
    }

    private void Execute(ReadOnlySpan<Complex32> input, Span<Complex32> output, bool inverse, bool useVector)
    {
        // Stack working buffers keep Execute allocation-free. The sizes the CELT MDCT asks for
        // are at most 8192; above that fall back to a heap buffer (never hit in practice).
        Span<float> sourceReal = _size <= 8192 ? stackalloc float[_size] : new float[_size];
        Span<float> sourceImaginary = _size <= 8192 ? stackalloc float[_size] : new float[_size];
        Span<float> outputReal = _size <= 8192 ? stackalloc float[_size] : new float[_size];
        Span<float> outputImaginary = _size <= 8192 ? stackalloc float[_size] : new float[_size];

        for (int i = 0; i < _size; i++)
        {
            Complex32 value = input[i];
            sourceReal[i] = value.Real;
            sourceImaginary[i] = value.Imag;
        }

        // Place the input in the decimation-in-time leaf order, then run the combine stages
        // bottom-up. This is the recursion of a mixed-radix Cooley-Tukey transform written as
        // loops: leaf `l` has digits (s0, s1, ...) with l = s0 + s1*r0 + ..., reads source
        // sample l, and writes destination index sum(s_m * half_m).
        int levels = _radices.Length;
        int[] leafDestination = _leafDestination;
        for (int leaf = 0; leaf < _size; leaf++)
        {
            int destination = leafDestination[leaf];
            outputReal[destination] = sourceReal[leaf];
            outputImaginary[destination] = sourceImaginary[leaf];
        }

        for (int level = levels - 1; level >= 0; level--)
        {
            ReadOnlySpan<float> twiddleReal = inverse ? _twiddleRealConjugate[level] : _twiddleReal[level];
            ReadOnlySpan<float> twiddleImaginary = inverse
                ? _twiddleImaginaryConjugate[level]
                : _twiddleImaginary[level];

            FftKernels.CombineMany(
                _radices[level],
                outputReal,
                outputImaginary,
                _nodeOffsets[level],
                _halves[level],
                twiddleReal,
                twiddleImaginary,
                inverse,
                useVector);
        }

        for (int i = 0; i < _size; i++)
        {
            output[i] = new Complex32(outputReal[i], outputImaginary[i]);
        }
    }

    private static int[] Factor(int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The transform size must be positive.");
        }

        var factors = new List<int>();
        int remaining = size;
        while (remaining % 4 == 0)
        {
            factors.Add(4);
            remaining /= 4;
        }

        while (remaining % 2 == 0)
        {
            factors.Add(2);
            remaining /= 2;
        }

        while (remaining % 3 == 0)
        {
            factors.Add(3);
            remaining /= 3;
        }

        while (remaining % 5 == 0)
        {
            factors.Add(5);
            remaining /= 5;
        }

        if (remaining != 1)
        {
            throw new NotSupportedException(
                $"FFT size {size} has a prime factor other than 2, 3 or 5.");
        }

        return [.. factors];
    }

    private static Complex32 UnitRoot(int power, int modulus)
    {
        // e^(-i*2*pi*power/modulus), computed in double and rounded to float once.
        double angle = -2.0 * Math.PI * (power % modulus) / modulus;
        return new Complex32((float)Math.Cos(angle), (float)Math.Sin(angle));
    }

}
