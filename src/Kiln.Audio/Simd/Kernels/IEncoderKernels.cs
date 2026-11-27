namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The CELT encoder analysis kernels. Implemented by the same tier structs as <see cref="ICeltKernels"/>; the CELT
/// encoder core is generic over the tier, selected once in its constructor, with one <c>switch</c> per frame.
/// </summary>
/// <remarks>
/// Float kernels keep the accumulation structure of the loops they replaced, so encoder output does not change; every
/// member is a loop over a whole span.
/// </remarks>
internal interface IEncoderKernels : ICeltKernels
{
    /// <summary>
    /// <c>output[i] = dot(x[..len], y[i..])</c> for <c>i &lt; lagCount</c>: the pitch-search cross-correlation, four lags
    /// per pass with the 4-lane accumulation of the single-lag dot product.
    /// </summary>
    static abstract void XcorrLags(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int lagCount, int len, Span<float> output);

    /// <summary>Four dot products in one pass, each accumulated like the single-lag dot (see <c>CeltEncVec&lt;T&gt;.Dot4</c>).</summary>
    static abstract void Dot4(
        int n,
        ReadOnlySpan<float> a0, ReadOnlySpan<float> b0,
        ReadOnlySpan<float> a1, ReadOnlySpan<float> b1,
        ReadOnlySpan<float> a2, ReadOnlySpan<float> b2,
        ReadOnlySpan<float> a3, ReadOnlySpan<float> b3,
        out float s0, out float s1, out float s2, out float s3);

    /// <summary>Counts the bins whose <c>x*x*bandSamples</c> is below 0.25, 0.0625 and 0.015625 (spreading decision).</summary>
    static abstract void SpreadCounts(ReadOnlySpan<float> x, int bandSamples, out int t0, out int t1, out int t2);

    /// <summary>The autocorrelation at lags 0 to 4 (no regularisation), each lag accumulated in index order.</summary>
    static abstract void Autocorr5(ReadOnlySpan<float> x, Span<float> ac);

    /// <summary>In-place 4-tap FIR with zero history.</summary>
    static abstract void Fir4(Span<float> x, float t0, float t1, float t2, float t3);

    /// <summary>In-place 1-tap FIR with zero history.</summary>
    static abstract void Fir1(Span<float> x, float t0);

    /// <summary>The pitch search's low-pass and decimate-by-two of one channel, written or accumulated into <paramref name="low"/>.</summary>
    static abstract void DecimateLowpass(ReadOnlySpan<float> source, Span<float> low, int count, bool accumulate);

    /// <summary>The maximum absolute value of each block of <paramref name="block"/> samples.</summary>
    static abstract void BlockMaxAbs(ReadOnlySpan<float> x, int block, Span<float> bins, int binCount);

    /// <summary>The encoder's comb pre-filter; see <c>CeltEncVec&lt;T&gt;.CombFilter</c>.</summary>
    static abstract void CombFilter(
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
        int overlap);

    /// <summary>The forward MDCT's un-windowed middle fold (two strided gathers), for <c>i = start..end-1</c>.</summary>
    static abstract void MdctFoldMiddle(
        ReadOnlySpan<float> input, int source1, int source2, Span<float> real, Span<float> imaginary, int start, int end);

    /// <summary>The forward MDCT post-rotation of a contiguous block; <paramref name="count"/>/2 must be a multiple of 8.</summary>
    static abstract void MdctPostContiguous(
        Span<float> coefficients,
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count);
}
