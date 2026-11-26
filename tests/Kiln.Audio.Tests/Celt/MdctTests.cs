using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests;

/// <summary>Correctness of the scalar CELT MDCT/IMDCT against a naive double-precision reference.</summary>
public class MdctTests
{
    private const int Overlap = 120;

    /// <summary>The CELT frame sizes at 48 kHz, in MDCT coefficients.</summary>
    public static TheoryData<int> Sizes => new() { 120, 240, 480, 960 };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Forward_matches_naive_for_random_signals(int coefficientCount)
    {
        var mdct = new Mdct(coefficientCount, Overlap);
        float[] window = BuildWindow(coefficientCount, Overlap);
        var input = new float[coefficientCount + Overlap];
        var coefficients = new float[coefficientCount];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(input, seed);

            double[] expected = NaiveForward(input, window, coefficientCount, Overlap);
            mdct.Forward(input, coefficients);

            double error = MaxAbsError(coefficients, expected);
            double rms = Rms(expected);
            Assert.True(
                error <= 1e-5 * rms,
                $"coefficients={coefficientCount} seed={seed}: error {error:E3} exceeds 1e-5 of RMS {rms:E3}.");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Forward_matches_naive_for_impulses(int coefficientCount)
    {
        var mdct = new Mdct(coefficientCount, Overlap);
        float[] window = BuildWindow(coefficientCount, Overlap);
        int length = coefficientCount + Overlap;
        var input = new float[length];
        var coefficients = new float[coefficientCount];

        foreach (int position in new[] { 0, length / 2, length - 1 })
        {
            Array.Clear(input);
            input[position] = 1f;

            double[] expected = NaiveForward(input, window, coefficientCount, Overlap);
            mdct.Forward(input, coefficients);

            double error = MaxAbsError(coefficients, expected);
            double rms = Rms(expected);
            Assert.True(
                error <= 1e-5 * rms,
                $"coefficients={coefficientCount} impulse={position}: error {error:E3} exceeds 1e-5 of RMS {rms:E3}.");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Inverse_matches_naive_for_random_coefficients(int coefficientCount)
    {
        var mdct = new Mdct(coefficientCount, Overlap);
        float[] window = BuildWindow(coefficientCount, Overlap);
        var coefficients = new float[coefficientCount];
        var output = new float[coefficientCount + Overlap];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(coefficients, seed + 100);

            double[] expected = NaiveInverse(coefficients, window, coefficientCount, Overlap);
            mdct.Inverse(coefficients, output);

            double error = MaxAbsError(output, expected);
            double rms = Rms(expected);
            Assert.True(
                error <= 1e-5 * rms,
                $"coefficients={coefficientCount} seed={seed}: error {error:E3} exceeds 1e-5 of RMS {rms:E3}.");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Forward_then_inverse_plus_overlap_add_reconstructs_the_input(int coefficientCount)
    {
        const int Frames = 50;
        int length = coefficientCount + Overlap;
        int total = (Frames * coefficientCount) + length;

        var mdct = new Mdct(coefficientCount, Overlap);
        var input = new float[total];
        FillRandom(input, 7);

        var coefficients = new float[coefficientCount];
        var block = new float[length];
        var output = new float[Frames * coefficientCount];
        var saved = new float[Overlap];

        Array.Clear(saved);
        for (int frame = 0; frame < Frames; frame++)
        {
            ReadOnlySpan<float> samples = input.AsSpan(frame * coefficientCount, length);
            mdct.Forward(samples, coefficients);
            mdct.Inverse(coefficients, block);

            for (int n = 0; n < coefficientCount; n++)
            {
                float value = block[n];
                if (n < Overlap)
                {
                    value += saved[n];
                }

                output[(frame * coefficientCount) + n] = value;
            }

            for (int n = 0; n < Overlap; n++)
            {
                saved[n] = block[coefficientCount + n];
            }
        }

        // The first overlap cannot reconstruct: the first frame has no predecessor. Every later
        // sample must match.
        double error = 0.0;
        for (int t = Overlap; t < output.Length; t++)
        {
            error = Math.Max(error, Math.Abs(output[t] - input[t]));
        }

        Assert.True(error <= 1e-5, $"coefficients={coefficientCount}: reconstruction error {error:E3}.");
    }

    [Fact]
    public void Stride_produces_the_interleaved_short_block_layout()
    {
        const int ShortCount = 120;
        const int Blocks = 8;
        const int Length = ShortCount + Overlap;

        var mdct = new Mdct(ShortCount, Overlap);
        var input = new float[Blocks * Length];
        FillRandom(input, 11);

        var interleaved = new float[Blocks * ShortCount];
        var contiguous = new float[ShortCount];

        for (int block = 0; block < Blocks; block++)
        {
            ReadOnlySpan<float> samples = input.AsSpan(block * Length, Length);
            mdct.Forward(samples, interleaved.AsSpan(block), stride: Blocks);
            mdct.Forward(samples, contiguous);

            for (int k = 0; k < ShortCount; k++)
            {
                // RFC 6716 §4.3.7 / §4.3.4: coefficient k of short block b lives at X[k * B + b].
                Assert.Equal(contiguous[k], interleaved[(k * Blocks) + block]);
            }
        }

        // The inverse reads the same interleaved layout.
        var blockCoefficients = new float[ShortCount];
        var deinterleaved = new float[Length];
        var asContiguous = new float[Length];
        for (int block = 0; block < Blocks; block++)
        {
            for (int k = 0; k < ShortCount; k++)
            {
                blockCoefficients[k] = interleaved[(k * Blocks) + block];
            }

            mdct.Inverse(interleaved.AsSpan(block), deinterleaved, stride: Blocks);
            mdct.Inverse(blockCoefficients, asContiguous);

            for (int n = 0; n < Length; n++)
            {
                Assert.Equal(asContiguous[n], deinterleaved[n]);
            }
        }
    }

    private static float[] BuildWindow(int coefficientCount, int overlap)
    {
        int length = coefficientCount + overlap;
        var window = new float[length];
        float[] rising = CeltWindow.Create(overlap);
        Array.Copy(rising, window, overlap);

        for (int n = overlap; n < coefficientCount; n++)
        {
            window[n] = 1f;
        }

        for (int n = coefficientCount; n < length; n++)
        {
            window[n] = rising[length - 1 - n];
        }

        return window;
    }

    private static double[] NaiveForward(
        ReadOnlySpan<float> input, float[] window, int coefficientCount, int overlap)
    {
        int length = coefficientCount + overlap;
        double phaseOffset = coefficientCount - (overlap / 2.0);
        var result = new double[coefficientCount];

        for (int k = 0; k < coefficientCount; k++)
        {
            double sum = 0.0;
            for (int n = 0; n < length; n++)
            {
                double angle = Math.PI / coefficientCount * (n + 0.5 + phaseOffset) * (k + 0.5);
                sum += window[n] * input[n] * Math.Cos(angle);
            }

            result[k] = sum;
        }

        return result;
    }

    private static double[] NaiveInverse(
        ReadOnlySpan<float> coefficients, float[] window, int coefficientCount, int overlap)
    {
        int length = coefficientCount + overlap;
        double phaseOffset = coefficientCount - (overlap / 2.0);
        double scale = 2.0 / coefficientCount;
        var result = new double[length];

        for (int n = 0; n < length; n++)
        {
            double sum = 0.0;
            for (int k = 0; k < coefficientCount; k++)
            {
                double angle = Math.PI / coefficientCount * (n + 0.5 + phaseOffset) * (k + 0.5);
                sum += coefficients[k] * Math.Cos(angle);
            }

            result[n] = window[n] * scale * sum;
        }

        return result;
    }

    private static void FillRandom(Span<float> destination, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (float)((2.0 * random.NextDouble()) - 1.0);
        }
    }

    private static double MaxAbsError(ReadOnlySpan<float> actual, ReadOnlySpan<double> expected)
    {
        double error = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            error = Math.Max(error, Math.Abs(actual[i] - expected[i]));
        }

        return error;
    }

    private static double Rms(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i] * values[i];
        }

        return Math.Sqrt(sum / values.Length);
    }
}
