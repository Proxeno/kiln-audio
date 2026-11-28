using System.Runtime.CompilerServices;
using Kiln.Audio.Decoder;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// A decoder profile layered over a kernel tier. Every member forwards to <typeparamref name="TK"/>
/// except the ones a Relaxed profile replaces with a faster, not bit-exact version. The Precise
/// profile never goes through this wrapper: it uses the tier struct directly, so its output is
/// unchanged.
/// </summary>
/// <typeparam name="TK">The kernel tier.</typeparam>
/// <typeparam name="TP">The decoder profile.</typeparam>
internal readonly struct ProfiledKernels<TK, TP> : ICeltKernels
    where TK : struct, ICeltKernels
    where TP : struct, IDecoderProfile
{
    private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    public static AudioKernelTier Tier => TK.Tier;

    public static bool Relaxed => TP.Relaxed;

    [MethodImpl(Inline)]
    public static float FastRsqrt(float x) => TK.FastRsqrt(x);

    [MethodImpl(Inline)]
    public static void ScaleCopy(Span<float> destination, ReadOnlySpan<float> source, float gain) =>
        TK.ScaleCopy(destination, source, gain);

    [MethodImpl(Inline)]
    public static void ScaleInPlace(Span<float> values, float gain) => TK.ScaleInPlace(values, gain);

    [MethodImpl(Inline)]
    public static void ScaleIntToFloat(Span<float> destination, ReadOnlySpan<int> source, float gain) =>
        TK.ScaleIntToFloat(destination, source, gain);

    [MethodImpl(Inline)]
    public static void StereoMerge(Span<float> x, Span<float> y, float mid, float lgain, float rgain, int count) =>
        TK.StereoMerge(x, y, mid, lgain, rgain, count);

    [MethodImpl(Inline)]
    public static float SumSquares(ReadOnlySpan<float> x) => TK.SumSquares(x);

    [MethodImpl(Inline)]
    public static void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy) =>
        TK.StereoDots(x, y, out xy, out yy);

    [MethodImpl(Inline)]
    public static void Rotate(Span<float> x, int length, int stride, float c, float s) =>
        TK.Rotate(x, length, stride, c, s);

    [MethodImpl(Inline)]
    public static void Haar(Span<float> x, int n0, int stride) => TK.Haar(x, n0, stride);

    [MethodImpl(Inline)]
    public static void AddInPlace(Span<float> destination, ReadOnlySpan<float> source) =>
        TK.AddInPlace(destination, source);

    [MethodImpl(Inline)]
    public static void Average(Span<float> x, ReadOnlySpan<float> y) => TK.Average(x, y);

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
        TK.FftLeaf(radix, sourceReal, sourceImaginary, real, imaginary, offsets, twiddleReal, twiddleImaginary, inverse);

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
        TK.FftCombine(radix, real, imaginary, offsets, half, twiddleReal, twiddleImaginary, inverse);

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
        TK.ImdctPre(coefficients, stride, real, imaginary, rotCos, rotSin, count, half);

    [MethodImpl(Inline)]
    public static void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half) =>
        TK.ImdctPostDeshuffle(real, imaginary, rotCos, rotSin, deshuffled, half);

    [MethodImpl(Inline)]
    public static void ImdctMirror(
        ReadOnlySpan<float> deshuffled,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap) =>
        TK.ImdctMirror(deshuffled, window, windowReversed, output, count, overlap);

    [MethodImpl(Inline)]
    public static void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap) =>
        TK.OverlapAddSave(raw, state, count, overlap);

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
        TK.CombFilter(history, frameStart, count, period0, period1, gain0, gain1, tapSet0, tapSet1, windowSquare, overlap);

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
        TK.Deemphasis(source0, source1, destination, samples, channels, duplicate, ref memory0, ref memory1, scale);
}
