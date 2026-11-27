using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The SILK and CELT encoder kernels of this tier (see <see cref="ISilkEncoderKernels"/>, <see cref="IEncoderKernels"/>).</summary>
internal readonly partial struct AdvSimdArm64Kernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length) => SilkVec<ArmIntOps>.InnerProd(a, b, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length) => SilkVec<ArmIntOps>.InnerProd64(a, b, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CorrLags(
        ReadOnlySpan<short> target, ReadOnlySpan<short> basis, int basisStart, int lagCount, int length, Span<int> output) =>
        SilkVec<ArmIntOps>.CorrLags(target, basis, basisStart, lagCount, length, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order) =>
        SilkVec<ArmIntOps>.LpcAnalysisFilter(output, input, coefficients, length, order);

    /// <summary>Unfused on every vector tier so the pitch search is bit-identical to the single-lag dot it replaced.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void XcorrLags(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int lagCount, int len, Span<float> output) =>
        CeltEncVec<PortableOps>.XcorrLags(x, y, lagCount, len, output);
}
