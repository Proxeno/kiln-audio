using Kiln.Audio.Celt;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Tests;

/// <summary>
/// Parity between the vector kernels and their scalar reference paths.
/// </summary>
/// <remarks>
/// The float kernels must agree to 1e-6 of the signal RMS, which is the PERF-1 acceptance rule.
/// The comparisons run both paths in-process rather than relying on
/// <c>KILN_AUDIO_DISABLE_SIMD</c>, so they exercise the scalar fallback in CI regardless of how
/// the test process was started; <see cref="Disable_switch_parses_the_expected_values"/> covers the
/// environment switch itself.
/// </remarks>
public class SimdParityTests
{
    private const double Tolerance = 1e-6;

    /// <summary>The FFT sizes the MDCT uses.</summary>
    public static TheoryData<int> FftSizes => new() { 60, 120, 240, 480 };

    /// <summary>The MDCT coefficient counts.</summary>
    public static TheoryData<int> MdctSizes => new() { 120, 240, 480, 960 };

    /// <summary>The supported radices.</summary>
    public static TheoryData<int> Radices => new() { 2, 3, 4, 5 };

    [Theory]
    [MemberData(nameof(FftSizes))]
    public void Fft_forward_matches_the_scalar_path(int size)
    {
        var fft = Fft.Get(size);
        var input = new Complex32[size];
        var vectorOutput = new Complex32[size];
        var scalarOutput = new Complex32[size];

        for (int seed = 0; seed < 3; seed++)
        {
            FillComplex(input, seed);
            fft.Forward(input, vectorOutput);
            fft.ForwardScalar(input, scalarOutput);

            AssertWithinRms(
                AsDoubles(scalarOutput),
                AsDoubles(vectorOutput),
                $"size={size} seed={seed}");
        }
    }

    [Theory]
    [MemberData(nameof(FftSizes))]
    public void Fft_inverse_matches_the_scalar_path(int size)
    {
        var fft = Fft.Get(size);
        var input = new Complex32[size];
        var vectorOutput = new Complex32[size];
        var scalarOutput = new Complex32[size];

        for (int seed = 0; seed < 3; seed++)
        {
            FillComplex(input, seed + 71);
            fft.Inverse(input, vectorOutput);
            fft.InverseScalar(input, scalarOutput);

            AssertWithinRms(
                AsDoubles(scalarOutput),
                AsDoubles(vectorOutput),
                $"size={size} seed={seed + 71}");
        }
    }

    [Theory]
    [MemberData(nameof(MdctSizes))]
    public void Mdct_forward_matches_the_scalar_path(int coefficientCount)
    {
        var mdct = new Mdct(coefficientCount);
        var input = new float[coefficientCount + mdct.Overlap];
        var vectorCoefficients = new float[coefficientCount];
        var scalarCoefficients = new float[coefficientCount];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(input, seed);
            mdct.Forward(input, vectorCoefficients);
            mdct.ForwardScalar(input, scalarCoefficients);

            AssertWithinRms(
                Array.ConvertAll(scalarCoefficients, static value => (double)value),
                Array.ConvertAll(vectorCoefficients, static value => (double)value),
                $"coefficients={coefficientCount} seed={seed}");
        }
    }

    [Theory]
    [MemberData(nameof(MdctSizes))]
    public void Mdct_inverse_matches_the_scalar_path(int coefficientCount)
    {
        var mdct = new Mdct(coefficientCount);
        var coefficients = new float[coefficientCount];
        int length = coefficientCount + mdct.Overlap;
        var vectorOutput = new float[length];
        var scalarOutput = new float[length];

        for (int seed = 0; seed < 3; seed++)
        {
            FillRandom(coefficients, seed + 211);
            mdct.Inverse(coefficients, vectorOutput);
            mdct.InverseScalar(coefficients, scalarOutput);

            AssertWithinRms(
                Array.ConvertAll(scalarOutput, static value => (double)value),
                Array.ConvertAll(vectorOutput, static value => (double)value),
                $"coefficients={coefficientCount} seed={seed + 211}");
        }
    }

    [Fact]
    public void Mdct_inverse_stride_layout_matches_the_scalar_path()
    {
        const int ShortCount = 120;
        const int Blocks = 8;
        var mdct = new Mdct(ShortCount);
        var interleaved = new float[Blocks * ShortCount];
        var vectorOutput = new float[Blocks * (ShortCount + mdct.Overlap)];
        var scalarOutput = new float[Blocks * (ShortCount + mdct.Overlap)];

        FillRandom(interleaved, 17);
        for (int block = 0; block < Blocks; block++)
        {
            mdct.Inverse(
                interleaved.AsSpan(block),
                vectorOutput.AsSpan(block * (ShortCount + mdct.Overlap)),
                stride: Blocks);
            mdct.InverseScalar(
                interleaved.AsSpan(block),
                scalarOutput.AsSpan(block * (ShortCount + mdct.Overlap)),
                stride: Blocks);
        }

        AssertWithinRms(
            Array.ConvertAll(scalarOutput, static value => (double)value),
            Array.ConvertAll(vectorOutput, static value => (double)value),
            "8x120 interleaved");
    }

    [Theory]
    [MemberData(nameof(Radices))]
    public void Combine_matches_between_the_scalar_and_vector_paths(int radix)
    {
        const int Half = 20;
        const int Offset = 7;
        var real = new float[Offset + (radix * Half)];
        var imaginary = new float[Offset + (radix * Half)];
        var twiddleReal = new float[radix * Half];
        var twiddleImaginary = new float[radix * Half];

        FillRandom(real, 100 + radix);
        FillRandom(imaginary, 200 + radix);
        FillRandom(twiddleReal, 300 + radix);
        FillRandom(twiddleImaginary, 400 + radix);

        var scalarReal = (float[])real.Clone();
        var scalarImaginary = (float[])imaginary.Clone();
        var vectorReal = (float[])real.Clone();
        var vectorImaginary = (float[])imaginary.Clone();

        FftKernels.Combine(
            radix, scalarReal, scalarImaginary, Offset, Half, twiddleReal, twiddleImaginary,
            inverse: false, useVector: false);
        FftKernels.Combine(
            radix, vectorReal, vectorImaginary, Offset, Half, twiddleReal, twiddleImaginary,
            inverse: false, useVector: true);

        AssertWithinRms(
            ToDoubles(scalarReal, scalarImaginary),
            ToDoubles(vectorReal, vectorImaginary),
            $"radix={radix}");

        // The inverse transform uses conjugated twiddles plus the opposite kernel sign.
        scalarReal = (float[])real.Clone();
        scalarImaginary = (float[])imaginary.Clone();
        vectorReal = (float[])real.Clone();
        vectorImaginary = (float[])imaginary.Clone();
        FftKernels.Combine(
            radix, scalarReal, scalarImaginary, Offset, Half, twiddleReal, twiddleImaginary,
            inverse: true, useVector: false);
        FftKernels.Combine(
            radix, vectorReal, vectorImaginary, Offset, Half, twiddleReal, twiddleImaginary,
            inverse: true, useVector: true);

        AssertWithinRms(
            ToDoubles(scalarReal, scalarImaginary),
            ToDoubles(vectorReal, vectorImaginary),
            $"radix={radix} inverse");
    }

    [Fact]
    public void Mdct_window_kernels_match_between_the_scalar_and_vector_paths()
    {
        const int Count = 96;
        var direct = new float[Count];
        var reversed = new float[Count];
        var window = new float[Count];
        FillRandom(direct, 11);
        FillRandom(reversed, 12);
        FillRandom(window, 13);

        var vectorOutput = new float[Count];
        var scalarOutput = new float[Count];

        MdctKernels.WindowFirst(vectorOutput, direct, reversed, window, 0.5f, Count, useVector: true);
        MdctKernels.WindowFirst(scalarOutput, direct, reversed, window, 0.5f, Count, useVector: false);
        AssertWithinRms(
            Array.ConvertAll(scalarOutput, static value => (double)value),
            Array.ConvertAll(vectorOutput, static value => (double)value),
            "WindowFirst");

        MdctKernels.WindowSecond(vectorOutput, reversed, direct, window, 0.5f, Count, useVector: true);
        MdctKernels.WindowSecond(scalarOutput, reversed, direct, window, 0.5f, Count, useVector: false);
        AssertWithinRms(
            Array.ConvertAll(scalarOutput, static value => (double)value),
            Array.ConvertAll(vectorOutput, static value => (double)value),
            "WindowSecond");
    }

    [Theory]
    [InlineData(256, 320, 840, 0.5f, 0.5f)]
    [InlineData(15, 17, 120, 0.7f, 0.3f)]
    [InlineData(19, 19, 100, 0.25f, 0.75f)]
    [InlineData(4, 7, 200, 0.5f, 0.5f)]
    [InlineData(120, 120, 840, 0f, 0.9f)]
    public void Celt_post_filter_is_bit_exact_between_the_scalar_and_vector_paths(
        int period0, int period1, int count, float gain0, float gain1)
    {
        const int HistorySize = 2048;
        const int FrameStart = 1088;
        const int Overlap = 120;
        var history = new float[HistorySize];
        FillRandom(history, period0 + (period1 * 31));
        var windowSquare = new float[Overlap];
        FillRandom(windowSquare, 5);
        for (int i = 0; i < Overlap; i++)
        {
            windowSquare[i] = windowSquare[i] * windowSquare[i];
        }

        var vectorHistory = (float[])history.Clone();
        var scalarHistory = (float[])history.Clone();

        CeltPostFilterKernel.CombFilter(
            vectorHistory, FrameStart, count, period0, period1, gain0, gain1, 0, 1,
            windowSquare, Overlap, useVector: true);
        CeltPostFilterKernel.CombFilter(
            scalarHistory, FrameStart, count, period0, period1, gain0, gain1, 0, 1,
            windowSquare, Overlap, useVector: false);

        Assert.Equal(scalarHistory, vectorHistory);
    }

    [Fact]
    public void Celt_post_filter_skips_an_unused_out_of_range_period()
    {
        // The decoder passes the newly decoded pitch through even when its gain is zero, so an
        // unused period can be larger than the history. Both paths must skip the read rather
        // than throw.
        const int HistorySize = 512;
        const int FrameStart = 400;
        const int Overlap = 120;
        var history = new float[HistorySize];
        FillRandom(history, 77);
        var windowSquare = new float[Overlap];
        FillRandom(windowSquare, 78);
        var vectorHistory = (float[])history.Clone();
        var scalarHistory = (float[])history.Clone();

        CeltPostFilterKernel.CombFilter(
            vectorHistory, FrameStart, 100, 30, 4000, 0.5f, 0f, 0, 1,
            windowSquare, Overlap, useVector: true);
        CeltPostFilterKernel.CombFilter(
            scalarHistory, FrameStart, 100, 30, 4000, 0.5f, 0f, 0, 1,
            windowSquare, Overlap, useVector: false);

        Assert.Equal(scalarHistory, vectorHistory);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(16)]
    public void Silk_lpc_kernel_is_bit_exact_between_the_scalar_and_vector_paths(int order)
    {
        const int SubframeLength = 80;
        const int MaxOrder = 16;
        var initialLpc = new int[MaxOrder + SubframeLength];
        var coefficients = new short[order];
        var residual = new int[SubframeLength];
        var vectorOutput = new short[SubframeLength];
        var scalarOutput = new short[SubframeLength];
        var vectorState = new int[initialLpc.Length];
        var scalarState = new int[initialLpc.Length];

        for (int seed = 0; seed < 5; seed++)
        {
            var random = new Random(seed + 900);
            for (int i = 0; i < initialLpc.Length; i++)
            {
                initialLpc[i] = seed switch
                {
                    0 => random.Next(-1 << 20, 1 << 20),
                    1 => (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1),
                    2 => i % 3 == 0 ? int.MaxValue : int.MinValue,
                    _ => (int)(random.NextInt64(int.MinValue, (long)int.MaxValue + 1)),
                };
            }

            for (int j = 0; j < order; j++)
            {
                coefficients[j] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            }

            for (int i = 0; i < SubframeLength; i++)
            {
                residual[i] = (int)(random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
            }

            initialLpc.CopyTo(vectorState, 0);
            initialLpc.CopyTo(scalarState, 0);
            int gainQ10 = random.Next(1, 1 << 24);

            SilkLpcKernel.SynthesizeVector(
                vectorState, coefficients, residual, gainQ10, order, SubframeLength, vectorOutput);
            SilkLpcKernel.SynthesizeScalar(
                scalarState, coefficients, residual, gainQ10, order, SubframeLength, scalarOutput);

            Assert.Equal(scalarState, vectorState);
            Assert.Equal(scalarOutput, vectorOutput);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void Disable_switch_parses_the_expected_values(string? value, bool disabled)
    {
        Assert.Equal(disabled, SimdConfig.IsDisabledValue(value));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(96)]
    [InlineData(176)]
    public void Dec_scale_copy_is_bit_exact_between_the_scalar_and_vector_paths(int count)
    {
        var source = new float[count];
        FillRandom(source, count + 400);
        var vectorOutput = new float[count];
        var scalarOutput = new float[count];
        const float Gain = 0.375f;

        DecKernels.ScaleCopy(vectorOutput, source, Gain, useVector: true);
        DecKernels.ScaleCopy(scalarOutput, source, Gain, useVector: false);

        Assert.Equal(scalarOutput, vectorOutput);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(120)]
    public void Dec_scale_in_place_is_bit_exact_between_the_scalar_and_vector_paths(int count)
    {
        var source = new float[count];
        FillRandom(source, count + 500);
        var vectorValues = (float[])source.Clone();
        var scalarValues = (float[])source.Clone();
        const float Gain = -1.25f;

        DecKernels.ScaleInPlace(vectorValues, Gain, useVector: true);
        DecKernels.ScaleInPlace(scalarValues, Gain, useVector: false);

        Assert.Equal(scalarValues, vectorValues);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(13)]
    [InlineData(96)]
    [InlineData(176)]
    public void Dec_scale_int_to_float_is_bit_exact_between_the_scalar_and_vector_paths(int count)
    {
        var source = new int[count];
        var random = new Random(count + 600);
        for (int i = 0; i < count; i++)
        {
            source[i] = random.Next(-8, 9);
        }

        var vectorOutput = new float[count];
        var scalarOutput = new float[count];
        const float Gain = 0.0137f;

        DecKernels.ScaleIntToFloat(vectorOutput, source, Gain, useVector: true);
        DecKernels.ScaleIntToFloat(scalarOutput, source, Gain, useVector: false);

        Assert.Equal(scalarOutput, vectorOutput);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(88)]
    public void Dec_stereo_merge_is_bit_exact_between_the_scalar_and_vector_paths(int count)
    {
        var xSource = new float[count];
        var ySource = new float[count];
        FillRandom(xSource, count + 700);
        FillRandom(ySource, count + 800);
        var vectorX = (float[])xSource.Clone();
        var vectorY = (float[])ySource.Clone();
        var scalarX = (float[])xSource.Clone();
        var scalarY = (float[])ySource.Clone();

        DecKernels.StereoMerge(vectorX, vectorY, 0.7f, 1.1f, 0.9f, count, useVector: true);
        DecKernels.StereoMerge(scalarX, scalarY, 0.7f, 1.1f, 0.9f, count, useVector: false);

        Assert.Equal(scalarX, vectorX);
        Assert.Equal(scalarY, vectorY);
    }

    [Theory]
    [InlineData(80, 32)]
    [InlineData(80, 10)]
    [InlineData(80, 12)]
    [InlineData(40, 160)]
    [InlineData(4, 16)]
    [InlineData(320, 20)]
    public void Silk_ltp_synthesis_is_bit_exact_between_the_scalar_and_vector_paths(int count, int lag)
    {
        const int Taps = 5;
        const int BufferLength = 1024;
        const int PredictionLag = 400;
        int writeIndex = PredictionLag + lag - 2;
        var random = new Random((count * 131) + lag);
        var initial = new int[BufferLength];
        for (int i = 0; i < initial.Length; i++)
        {
            initial[i] = (int)(random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
        }

        var coefficients = new short[Taps];
        for (int t = 0; t < Taps; t++)
        {
            coefficients[t] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        var excitation = new int[count];
        for (int i = 0; i < count; i++)
        {
            excitation[i] = (int)(random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
        }

        var vectorBuffer = (int[])initial.Clone();
        var scalarBuffer = (int[])initial.Clone();
        var vectorResidual = new int[count];
        var scalarResidual = new int[count];

        DecSilkLtp.Synthesize(
            vectorBuffer, PredictionLag, writeIndex, coefficients, excitation, vectorResidual, count, useVector: true);
        DecSilkLtp.Synthesize(
            scalarBuffer, PredictionLag, writeIndex, coefficients, excitation, scalarResidual, count, useVector: false);

        Assert.Equal(scalarResidual, vectorResidual);
        Assert.Equal(scalarBuffer, vectorBuffer);
    }

    private static void AssertWithinRms(double[] expected, double[] actual, string context)
    {
        Assert.Equal(expected.Length, actual.Length);
        double sum = 0.0;
        double maximum = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            sum += expected[i] * expected[i];
            maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i]));
        }

        double rms = Math.Sqrt(sum / expected.Length);
        Assert.True(
            maximum <= Tolerance * rms,
            FormattableString.Invariant(
                $"{context}: max error {maximum:E3} exceeds {Tolerance:E1} of RMS {rms:E3}."));
    }

    private static double[] ToDoubles(float[] real, float[] imaginary)
    {
        var result = new double[real.Length * 2];
        for (int i = 0; i < real.Length; i++)
        {
            result[2 * i] = real[i];
            result[(2 * i) + 1] = imaginary[i];
        }

        return result;
    }

    private static double[] AsDoubles(Complex32[] values) => Flatten(
        Array.ConvertAll(values, static value => (double)value.Real),
        Array.ConvertAll(values, static value => (double)value.Imag));

    private static double[] Flatten(double[] real, double[] imaginary)
    {
        var result = new double[real.Length * 2];
        for (int i = 0; i < real.Length; i++)
        {
            result[2 * i] = real[i];
            result[(2 * i) + 1] = imaginary[i];
        }

        return result;
    }

    private static void FillComplex(Span<Complex32> destination, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = new Complex32(
                (float)((2.0 * random.NextDouble()) - 1.0),
                (float)((2.0 * random.NextDouble()) - 1.0));
        }
    }

    private static void FillRandom(Span<float> destination, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (float)((2.0 * random.NextDouble()) - 1.0);
        }
    }
}
