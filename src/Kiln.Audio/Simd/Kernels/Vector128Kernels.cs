using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The portable 128-bit tier: <see cref="System.Runtime.Intrinsics.Vector128{T}"/> shuffles and separately rounded multiply-add. Used on SSE2-only x64, WASM and any other 128-bit host, and bit-identical to the scalar tier except where a kernel reassociates a sum.</summary>
internal readonly partial struct Vector128Kernels : ICeltKernels, ISilkEncoderKernels, IEncoderKernels
{
    private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    /// <summary>Gets a value indicating whether the host can run this tier.</summary>
    public static bool IsSupported => AudioKernels.IsSupported(AudioKernelTier.Vector128);

    public static AudioKernelTier Tier => AudioKernelTier.Vector128;

    [MethodImpl(Inline)]
    public static void ScaleCopy(Span<float> destination, ReadOnlySpan<float> source, float gain) =>
        DecKernels.ScaleCopy(destination, source, gain, useVector: true);

    [MethodImpl(Inline)]
    public static void ScaleInPlace(Span<float> values, float gain) => DecKernels.ScaleInPlace(values, gain, useVector: true);

    [MethodImpl(Inline)]
    public static void ScaleIntToFloat(Span<float> destination, ReadOnlySpan<int> source, float gain) =>
        DecKernels.ScaleIntToFloat(destination, source, gain, useVector: true);

    [MethodImpl(Inline)]
    public static void StereoMerge(Span<float> x, Span<float> y, float mid, float lgain, float rgain, int count) =>
        DecKernels.StereoMerge(x, y, mid, lgain, rgain, count, useVector: true);

    /// <summary>The unfused dot product used by the encoder's prefilter and input resampler (see <c>CeltVec.Dot</c>).</summary>
    [MethodImpl(Inline)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => CeltVec<PortableOps>.Dot(a, b);

    [MethodImpl(Inline)]
    public static float SumSquares(ReadOnlySpan<float> x) => CeltVec<PortableOps>.SumSquares(x);

    [MethodImpl(Inline)]
    public static void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy) =>
        CeltVec<PortableOps>.StereoDots(x, y, out xy, out yy);

    [MethodImpl(Inline)]
    public static void Rotate(Span<float> x, int length, int stride, float c, float s) =>
        CeltVec<PortableOps>.Rotate(x, length, stride, c, s);

    [MethodImpl(Inline)]
    public static void Haar(Span<float> x, int n0, int stride) => CeltVec<PortableOps>.Haar(x, n0, stride);

    [MethodImpl(Inline)]
    public static void AddInPlace(Span<float> destination, ReadOnlySpan<float> source) =>
        CeltVec<PortableOps>.AddInPlace(destination, source);

    [MethodImpl(Inline)]
    public static void Average(Span<float> x, ReadOnlySpan<float> y) => CeltVec<PortableOps>.Average(x, y);

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
        CeltVec<PortableOps>.FftLeaf(
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
        CeltVec<PortableOps>.FftCombine(radix, real, imaginary, offsets, half, twiddleReal, twiddleImaginary, inverse);

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
        CeltVec<PortableOps>.ImdctPre(coefficients, stride, real, imaginary, rotCos, rotSin, count, half);

    [MethodImpl(Inline)]
    public static void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half) =>
        CeltVec<PortableOps>.ImdctPostDeshuffle(real, imaginary, rotCos, rotSin, deshuffled, half);

    [MethodImpl(Inline)]
    public static void ImdctMirror(
        ReadOnlySpan<float> deshuffled,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap) =>
        CeltVec<PortableOps>.ImdctMirror(deshuffled, window, windowReversed, output, count, overlap);

    [MethodImpl(Inline)]
    public static void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap) =>
        CeltVec<PortableOps>.OverlapAddSave(raw, state, count, overlap);

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
        CeltPostFilterKernel.CombFilter<PortableOps>(
            history, frameStart, count, period0, period1, gain0, gain1, tapSet0, tapSet1, windowSquare, overlap, useVector: true);

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
        CeltVec<PortableOps>.Deemphasis(source0, source1, destination, samples, channels, duplicate, ref memory0, ref memory1, scale);
}
