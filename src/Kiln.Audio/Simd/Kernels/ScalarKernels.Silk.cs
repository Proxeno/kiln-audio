using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The SILK and CELT encoder kernels of this tier (see <see cref="ISilkEncoderKernels"/>, <see cref="IEncoderKernels"/>).</summary>
internal readonly partial struct ScalarKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length) => SilkScalar.InnerProd(a, b, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length) => SilkScalar.InnerProd64(a, b, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CorrLags(
        ReadOnlySpan<short> target, ReadOnlySpan<short> basis, int basisStart, int lagCount, int length, Span<int> output) =>
        SilkScalar.CorrLags(target, basis, basisStart, lagCount, length, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order) =>
        SilkScalar.LpcAnalysisFilter(output, input, coefficients, length, order);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void XcorrLags(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int lagCount, int len, Span<float> output)
    {
        for (int i = 0; i < lagCount; i++)
        {
            output[i] = Kiln.Audio.Celt.Encoder.CeltPrefilter.DotScalar(x[..len], y.Slice(i, len));
        }
    }
}
