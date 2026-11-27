using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The ENC-CELT-1 analysis kernels of this tier (see <see cref="IEncoderKernels"/>).</summary>
internal readonly partial struct Avx2FmaKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Dot4(
        int n,
        ReadOnlySpan<float> a0, ReadOnlySpan<float> b0,
        ReadOnlySpan<float> a1, ReadOnlySpan<float> b1,
        ReadOnlySpan<float> a2, ReadOnlySpan<float> b2,
        ReadOnlySpan<float> a3, ReadOnlySpan<float> b3,
        out float s0, out float s1, out float s2, out float s3) =>
        CeltEncVec<X86Ops>.Dot4(n, a0, b0, a1, b1, a2, b2, a3, b3, out s0, out s1, out s2, out s3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SpreadCounts(ReadOnlySpan<float> x, int bandSamples, out int t0, out int t1, out int t2) =>
        CeltEncVec<X86Ops>.SpreadCounts(x, bandSamples, out t0, out t1, out t2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Autocorr5(ReadOnlySpan<float> x, Span<float> ac) => CeltEncVec<X86Ops>.Autocorr5(x, ac);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fir4(Span<float> x, float t0, float t1, float t2, float t3) => CeltEncVec<X86Ops>.Fir4(x, t0, t1, t2, t3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fir1(Span<float> x, float t0) => CeltEncVec<X86Ops>.Fir1(x, t0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecimateLowpass(ReadOnlySpan<float> source, Span<float> low, int count, bool accumulate) =>
        CeltEncVec<X86Ops>.DecimateLowpass(source, low, count, accumulate);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlockMaxAbs(ReadOnlySpan<float> x, int block, Span<float> bins, int binCount) =>
        CeltEncVec<X86Ops>.BlockMaxAbs(x, block, bins, binCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CombFilter(
        Span<float> y,
        ReadOnlySpan<float> x,
        int xStart,
        int t0,
        int t1,
        int n,
        float g0,
        float g1,
        ReadOnlySpan<float> gains0,
        ReadOnlySpan<float> gains1,
        ReadOnlySpan<float> window,
        int overlap) =>
        CeltEncVec<X86Ops>.CombFilter(y, x, xStart, t0, t1, n, g0, g1, gains0, gains1, window, overlap);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MdctFoldMiddle(
        ReadOnlySpan<float> input, int source1, int source2, Span<float> real, Span<float> imaginary, int start, int end) =>
        CeltEncVec<X86Ops>.MdctFoldMiddle(input, source1, source2, real, imaginary, start, end);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MdctPostContiguous(
        Span<float> coefficients,
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count) =>
        CeltEncVec<X86Ops>.MdctPostContiguous(coefficients, real, imaginary, rotCos, rotSin, count);
}
