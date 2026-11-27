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
}
