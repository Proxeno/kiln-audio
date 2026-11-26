using System.Numerics;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests;

/// <summary>The mixed-radix FFT used by the MDCT, checked against a naive DFT.</summary>
public class MdctFftTests
{
    /// <summary>The complex FFT sizes the CELT MDCT uses: half of each coefficient count.</summary>
    public static TheoryData<int> FftSizes => new() { 60, 120, 240, 480 };

    [Theory]
    [MemberData(nameof(FftSizes))]
    public void Forward_matches_a_naive_dft(int size)
    {
        var fft = Fft.Get(size);
        var input = new Complex32[size];
        var actual = new Complex32[size];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(input, seed);

            Complex[] expected = NaiveDft(input, inverse: false);
            fft.Forward(input, actual);

            double error = MaxAbsError(actual, expected);
            double scale = MaxMagnitude(expected);
            Assert.True(
                error <= 1e-4 * scale,
                $"size={size} seed={seed}: error {error:E3} exceeds 1e-4 of {scale:E3}.");
        }
    }

    [Theory]
    [MemberData(nameof(FftSizes))]
    public void Inverse_matches_a_naive_inverse_dft(int size)
    {
        var fft = Fft.Get(size);
        var input = new Complex32[size];
        var actual = new Complex32[size];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(input, seed + 500);

            Complex[] expected = NaiveDft(input, inverse: true);
            fft.Inverse(input, actual);

            double error = MaxAbsError(actual, expected);
            double scale = MaxMagnitude(expected);
            Assert.True(
                error <= 1e-4 * scale,
                $"size={size} seed={seed}: error {error:E3} exceeds 1e-4 of {scale:E3}.");
        }
    }

    private static Complex[] NaiveDft(ReadOnlySpan<Complex32> input, bool inverse)
    {
        int size = input.Length;
        double sign = inverse ? 1.0 : -1.0;
        double scale = inverse ? 1.0 / size : 1.0;
        var result = new Complex[size];

        for (int k = 0; k < size; k++)
        {
            Complex sum = Complex.Zero;
            for (int n = 0; n < size; n++)
            {
                double angle = sign * 2.0 * Math.PI * n * k / size;
                sum += new Complex(input[n].Real, input[n].Imag) * Complex.FromPolarCoordinates(1.0, angle);
            }

            result[k] = sum * scale;
        }

        return result;
    }

    private static void FillRandom(Span<Complex32> destination, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < destination.Length; i++)
        {
            float real = (float)((2.0 * random.NextDouble()) - 1.0);
            float imag = (float)((2.0 * random.NextDouble()) - 1.0);
            destination[i] = new Complex32(real, imag);
        }
    }

    private static double MaxAbsError(ReadOnlySpan<Complex32> actual, ReadOnlySpan<Complex> expected)
    {
        double error = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            var value = new Complex(actual[i].Real, actual[i].Imag);
            error = Math.Max(error, Complex.Abs(value - expected[i]));
        }

        return error;
    }

    private static double MaxMagnitude(ReadOnlySpan<Complex> values)
    {
        double maximum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = Math.Max(maximum, Complex.Abs(values[i]));
        }

        return maximum;
    }
}
