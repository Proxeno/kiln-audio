using System.Runtime.CompilerServices;
using Kiln.Audio.Silk;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ScaleQ16(Span<int> data, int gainQ16)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = SilkFixedPoint.Smulww(gainQ16, data[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NsqSubframe(SilkNsqDelayedDecision quantizer, in SilkNsqSubframeArgs args) =>
        SilkNsqVec<PortableIntOps>.Subframe(quantizer, args);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BurgRows(
        Span<int> firstRow, Span<int> lastRow, ReadOnlySpan<int> afQa, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed,
        int n, int x1, int x2, ref int tmp1, ref int tmp2) =>
        SilkScalar.BurgRows(firstRow, lastRow, afQa, x, xReversed, n, x1, x2, ref tmp1, ref tmp2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BurgCorr(
        Span<int> caf, Span<int> cab, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed, int n, int tmp1, int tmp2) =>
        SilkScalar.BurgCorr(caf, cab, x, xReversed, n, tmp1, tmp2);
}
