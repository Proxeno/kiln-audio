namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The CELT encoder analysis kernels. Implemented by the same tier structs as <see cref="ICeltKernels"/>; the CELT
/// encoder core is generic over the tier, selected once in its constructor, with one <c>switch</c> per frame.
/// </summary>
/// <remarks>
/// Float kernels keep the accumulation structure of the loops they replaced, so encoder output does not change; every
/// member is a loop over a whole span.
/// </remarks>
internal interface IEncoderKernels
{
    /// <summary>
    /// <c>output[i] = dot(x[..len], y[i..])</c> for <c>i &lt; lagCount</c>: the pitch-search cross-correlation, four lags
    /// per pass with the 4-lane accumulation of the single-lag dot product.
    /// </summary>
    static abstract void XcorrLags(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int lagCount, int len, Span<float> output);
}
