using System.Collections.Concurrent;

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
/// The transform is out of place and stateless: all twiddle tables are immutable, so instances
/// returned by <see cref="Get"/> may be shared across threads. Execute allocates nothing. The
/// input and output spans must not overlap.
/// </para>
/// </remarks>
internal sealed class Fft
{
    private static readonly ConcurrentDictionary<int, Fft> Cache = new();

    private readonly int _size;
    private readonly int[] _radices;
    private readonly Complex32[][] _twiddles;
    private readonly Complex32[][] _smallDfts;

    private Fft(int size)
    {
        _size = size;
        _radices = Factor(size);
        int levels = _radices.Length;
        _twiddles = new Complex32[levels][];
        _smallDfts = new Complex32[levels][];

        int n = size;
        for (int level = 0; level < levels; level++)
        {
            int radix = _radices[level];
            int m = n / radix;

            // Twiddle W_n^(s*p) for the r-way combine, stored as [p*r + s].
            var twiddles = new Complex32[radix * m];
            for (int p = 0; p < m; p++)
            {
                for (int s = 0; s < radix; s++)
                {
                    twiddles[(p * radix) + s] = UnitRoot(s * p, n);
                }
            }

            // The radix-point DFT kernel W_r^(s*q), stored as [q*r + s].
            var smallDft = new Complex32[radix * radix];
            for (int q = 0; q < radix; q++)
            {
                for (int s = 0; s < radix; s++)
                {
                    smallDft[(q * radix) + s] = UnitRoot(s * q, radix);
                }
            }

            _twiddles[level] = twiddles;
            _smallDfts[level] = smallDft;
            n = m;
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

        Transform(input, 0, 1, output, 0, _size, 0, inverse: false);
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

        Transform(input, 0, 1, output, 0, _size, 0, inverse: true);
        float scale = 1f / _size;
        for (int i = 0; i < _size; i++)
        {
            Complex32 value = output[i];
            output[i] = new Complex32(value.Real * scale, value.Imag * scale);
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

    private void Transform(
        ReadOnlySpan<Complex32> source,
        int sourceOffset,
        int sourceStep,
        Span<Complex32> destination,
        int destinationOffset,
        int length,
        int level,
        bool inverse)
    {
        if (length == 1)
        {
            destination[destinationOffset] = source[sourceOffset];
            return;
        }

        int radix = _radices[level];
        int half = length / radix;

        // Transform each stride-radix subsequence into its own contiguous region.
        for (int s = 0; s < radix; s++)
        {
            Transform(
                source,
                sourceOffset + (s * sourceStep),
                sourceStep * radix,
                destination,
                destinationOffset + (s * half),
                half,
                level + 1,
                inverse);
        }

        Complex32[] twiddles = _twiddles[level];
        Complex32[] smallDft = _smallDfts[level];
        Span<Complex32> values = stackalloc Complex32[radix];
        Span<Complex32> combined = stackalloc Complex32[radix];

        for (int p = 0; p < half; p++)
        {
            for (int s = 0; s < radix; s++)
            {
                Complex32 value = destination[destinationOffset + (s * half) + p];
                Complex32 twiddle = twiddles[(p * radix) + s];
                values[s] = inverse ? Complex32.MultiplyConjugate(value, twiddle) : value * twiddle;
            }

            for (int q = 0; q < radix; q++)
            {
                Complex32 accumulator = default;
                for (int s = 0; s < radix; s++)
                {
                    Complex32 kernel = smallDft[(q * radix) + s];
                    accumulator += inverse
                        ? Complex32.MultiplyConjugate(values[s], kernel)
                        : values[s] * kernel;
                }

                combined[q] = accumulator;
            }

            for (int q = 0; q < radix; q++)
            {
                destination[destinationOffset + p + (half * q)] = combined[q];
            }
        }
    }
}
