using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The scalar tier: the reference path and the target of <c>KILN_AUDIO_DISABLE_SIMD</c>.</summary>
internal readonly struct ScalarKernels : ICeltKernels
{
    private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    /// <summary>Gets a value indicating whether the host can run this tier (always).</summary>
    public static bool IsSupported => true;

    public static AudioKernelTier Tier => AudioKernelTier.Scalar;

    [MethodImpl(Inline)]
    public static void ScaleCopy(Span<float> destination, ReadOnlySpan<float> source, float gain) =>
        DecKernels.ScaleCopy(destination, source, gain, useVector: false);

    [MethodImpl(Inline)]
    public static void ScaleInPlace(Span<float> values, float gain) => DecKernels.ScaleInPlace(values, gain, useVector: false);

    [MethodImpl(Inline)]
    public static void ScaleIntToFloat(Span<float> destination, ReadOnlySpan<int> source, float gain) =>
        DecKernels.ScaleIntToFloat(destination, source, gain, useVector: false);

    [MethodImpl(Inline)]
    public static void StereoMerge(Span<float> x, Span<float> y, float mid, float lgain, float rgain, int count) =>
        DecKernels.StereoMerge(x, y, mid, lgain, rgain, count, useVector: false);

    [MethodImpl(Inline)]
    public static float SumSquares(ReadOnlySpan<float> x) => CeltScalar.SumSquares(x);

    [MethodImpl(Inline)]
    public static void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy) =>
        CeltScalar.StereoDots(x, y, out xy, out yy);

    [MethodImpl(Inline)]
    public static void Rotate(Span<float> x, int length, int stride, float c, float s) =>
        CeltScalar.Rotate(x, length, stride, c, s);

    [MethodImpl(Inline)]
    public static void Haar(Span<float> x, int n0, int stride) => CeltScalar.Haar(x, n0, stride);

    [MethodImpl(Inline)]
    public static void AddInPlace(Span<float> destination, ReadOnlySpan<float> source) =>
        CeltScalar.AddInPlace(destination, source);

    [MethodImpl(Inline)]
    public static void Average(Span<float> x, ReadOnlySpan<float> y) => CeltScalar.Average(x, y);

    [MethodImpl(Inline)]
    public static void FftLeaf(
        int radix,
        ReadOnlySpan<float> sourceReal,
        ReadOnlySpan<float> sourceImaginary,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse) =>
        CeltScalar.FftLeaf(
            radix, sourceReal, sourceImaginary, real, imaginary, offsets, twiddleReal, twiddleImaginary, inverse);

    [MethodImpl(Inline)]
    public static void FftCombine(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse) =>
        FftKernels.CombineMany(radix, real, imaginary, offsets, half, twiddleReal, twiddleImaginary, inverse, useVector: false);

    [MethodImpl(Inline)]
    public static void ImdctPre(
        ReadOnlySpan<float> coefficients,
        int stride,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        int half) =>
        MdctKernels.RotateInversePre(coefficients, stride, real, imaginary, rotCos, rotSin, count, half, useVector: false);

    [MethodImpl(Inline)]
    public static void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half) =>
        CeltScalar.ImdctPostDeshuffle(real, imaginary, rotCos, rotSin, deshuffled, half);

    [MethodImpl(Inline)]
    public static void ImdctMirror(
        ReadOnlySpan<float> deshuffled,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap) =>
        CeltScalar.ImdctMirror(deshuffled, window, windowReversed, output, count, overlap);

    [MethodImpl(Inline)]
    public static void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap) =>
        CeltScalar.OverlapAddSave(raw, state, count, overlap);

    [MethodImpl(Inline)]
    public static void CombFilter(
        float[] history,
        int frameStart,
        int count,
        int period0,
        int period1,
        float gain0,
        float gain1,
        int tapSet0,
        int tapSet1,
        ReadOnlySpan<float> windowSquare,
        int overlap) =>
        CeltPostFilterKernel.CombFilter(
            history, frameStart, count, period0, period1, gain0, gain1, tapSet0, tapSet1, windowSquare, overlap, useVector: false);

    [MethodImpl(Inline)]
    public static void Deemphasis(
        ReadOnlySpan<float> source0,
        ReadOnlySpan<float> source1,
        Span<float> destination,
        int samples,
        int channels,
        bool duplicate,
        ref float memory0,
        ref float memory1,
        float scale) =>
        CeltScalar.Deemphasis(source0, source1, destination, samples, channels, duplicate, ref memory0, ref memory1, scale);
}
