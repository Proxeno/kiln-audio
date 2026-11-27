using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The SILK encoder kernels. Implemented by the same tier structs as <see cref="ICeltKernels"/>; the SILK encoder core is
/// generic over the tier, selected once in its constructor, with one <c>switch</c> per frame.
/// </summary>
/// <remarks>
/// Every member is exact integer arithmetic with the reference's wrap-around, so a kernel that feeds an integer decision
/// returns the same bits on every tier. Every member is a loop over a whole span; none is a per-sample call.
/// </remarks>
internal interface ISilkEncoderKernels
{
    /// <summary>The 32-bit inner product of two 16-bit vectors, with wrap-around.</summary>
    static abstract int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length);

    /// <summary>The exact 64-bit inner product of two 16-bit vectors.</summary>
    static abstract long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length);

    /// <summary>
    /// <c>output[i] = InnerProd(target, basis[(basisStart - i)..], length)</c> for <c>i &lt; lagCount</c>: a run of
    /// consecutive correlation lags with the target loaded once per four lags.
    /// </summary>
    static abstract void CorrLags(
        ReadOnlySpan<short> target, ReadOnlySpan<short> basis, int basisStart, int lagCount, int length, Span<int> output);

    /// <summary>The LPC analysis (whitening) filter over Q12 coefficients; the first <paramref name="order"/> outputs are zero.</summary>
    static abstract void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order);

    /// <summary><c>data[i] = Smulww(gainQ16, data[i])</c> over the span (the delayed-decision quantizer's gain re-scaling), exact.</summary>
    static abstract void ScaleQ16(Span<int> data, int gainQ16);

    /// <summary>
    /// Quantizes one subframe over all delayed-decision states, one SIMD lane per state (the loop over the samples is inside
    /// the kernel). Bit-identical to the scalar reference on every tier.
    /// </summary>
    static abstract void NsqSubframe(SilkNsqDelayedDecision quantizer, in SilkNsqSubframeArgs args);

    /// <summary>
    /// One step of Burg's recursion over one subframe, first half (<c>silk_burg_modified</c>, the <c>k &lt; n</c> loop):
    /// <c>firstRow[k]</c> and <c>lastRow[k]</c> gain a <c>Smlawb</c> term and the two running correlations
    /// <paramref name="tmp1"/>/<paramref name="tmp2"/> accumulate <c>Smlawb(afQa[k], x[..])</c>.
    /// <paramref name="xReversed"/> is <paramref name="x"/> reversed. Exact.
    /// </summary>
    static abstract void BurgRows(
        Span<int> firstRow, Span<int> lastRow, ReadOnlySpan<int> afQa, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed,
        int n, int x1, int x2, ref int tmp1, ref int tmp2);

    /// <summary>
    /// Burg's recursion, second half (the <c>k &lt;= n</c> loop): <c>caf[k]</c> and <c>cab[k]</c> gain
    /// <c>Smlawb(.., tmp, x[..])</c> terms. Exact.
    /// </summary>
    static abstract void BurgCorr(
        Span<int> caf, Span<int> cab, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed, int n, int tmp1, int tmp2);
}
