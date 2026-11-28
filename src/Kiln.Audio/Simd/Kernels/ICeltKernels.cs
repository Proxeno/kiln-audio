namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The CELT decode/synthesis kernels. Implemented by one <see langword="struct"/> per tier
/// (<see cref="ScalarKernels"/>, <see cref="Vector128Kernels"/>, <see cref="AdvSimdArm64Kernels"/>,
/// <see cref="Avx2FmaKernels"/>); the decoder and synthesizer are generic over the tier so each
/// call is a direct, inlinable call chosen at JIT time, with no dispatch inside a frame.
/// </summary>
/// <remarks>
/// Every member is a loop over a whole span, never a per-sample call. Floating-point results may
/// differ between tiers by reassociation and by fused multiply-add rounding, within the
/// conformance tolerance; integer results never differ.
/// </remarks>
internal interface ICeltKernels
{
    /// <summary>Gets the tier this kernel set implements.</summary>
    static abstract AudioKernelTier Tier { get; }

    /// <summary>
    /// Gets a value indicating whether this kernel set is a Relaxed-profile wrapper, so callers can
    /// take a non-bit-exact shortcut. A constant per instantiation: the JIT folds the branch.
    /// </summary>
    static virtual bool Relaxed => false;

    /// <summary>
    /// Returns <c>1 / sqrt(x)</c> for positive <paramref name="x"/>, accurate to about 2 ulp: the
    /// hardware reciprocal-square-root estimate refined by one Newton-Raphson step. Used only by
    /// Relaxed profiles; the default is the exact expression.
    /// </summary>
    static virtual float FastRsqrt(float x) => 1f / MathF.Sqrt(x);

    // ---- PVQ and band leaf kernels ----

    /// <summary><c>destination[i] = source[i] * gain</c>.</summary>
    static abstract void ScaleCopy(Span<float> destination, ReadOnlySpan<float> source, float gain);

    /// <summary><c>values[i] *= gain</c>.</summary>
    static abstract void ScaleInPlace(Span<float> values, float gain);

    /// <summary><c>destination[i] = source[i] * gain</c> for integer pulses.</summary>
    static abstract void ScaleIntToFloat(Span<float> destination, ReadOnlySpan<int> source, float gain);

    /// <summary>The mid/side to left/right merge of a stereo band.</summary>
    static abstract void StereoMerge(Span<float> x, Span<float> y, float mid, float lgain, float rgain, int count);

    /// <summary>Returns the sum of squares.</summary>
    static abstract float SumSquares(ReadOnlySpan<float> x);

    /// <summary>Returns the two dot products <c>x.y</c> and <c>y.y</c> over the length of <paramref name="x"/>.</summary>
    static abstract void StereoDots(ReadOnlySpan<float> x, ReadOnlySpan<float> y, out float xy, out float yy);

    /// <summary>One pass of the spreading rotation (<c>exp_rotation1</c>).</summary>
    static abstract void Rotate(Span<float> x, int length, int stride, float c, float s);

    /// <summary>The Haar butterfly of the time/frequency reshaping.</summary>
    static abstract void Haar(Span<float> x, int n0, int stride);

    /// <summary><c>destination[i] += source[i]</c>.</summary>
    static abstract void AddInPlace(Span<float> destination, ReadOnlySpan<float> source);

    /// <summary><c>x[i] = 0.5 * (x[i] + y[i])</c>, the stereo-to-mono downmix.</summary>
    static abstract void Average(Span<float> x, ReadOnlySpan<float> y);

    // ---- FFT ----

    /// <summary>
    /// The leaf level of the FFT with the decimation permutation fused in: leaf node <c>n</c>
    /// reads <c>source[n + s * nodes]</c> and writes <c>offsets[n] + s</c>.
    /// </summary>
    static abstract void FftLeaf(
        int radix,
        ReadOnlySpan<float> sourceReal,
        ReadOnlySpan<float> sourceImaginary,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse);

    /// <summary>One combine level of the FFT over every node of the level.</summary>
    static abstract void FftCombine(
        int radix,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<int> offsets,
        int half,
        ReadOnlySpan<float> twiddleReal,
        ReadOnlySpan<float> twiddleImaginary,
        bool inverse);

    // ---- IMDCT ----

    /// <summary>The inverse MDCT pre-rotation.</summary>
    static abstract void ImdctPre(
        ReadOnlySpan<float> coefficients,
        int stride,
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        int half);

    /// <summary>The inverse MDCT post-rotation fused with the de-shuffle (one pass).</summary>
    static abstract void ImdctPostDeshuffle(
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        Span<float> deshuffled,
        int half);

    /// <summary>The unscaled TDAC mirror: a block copy for the middle and windowed ramps at both ends.</summary>
    static abstract void ImdctMirror(
        ReadOnlySpan<float> deshuffled,
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> windowReversed,
        Span<float> output,
        int count,
        int overlap);

    /// <summary><c>raw[j] += state[j]; state[j] = raw[count + j]</c> for <c>j &lt; overlap</c>.</summary>
    static abstract void OverlapAddSave(Span<float> raw, Span<float> state, int count, int overlap);

    // ---- synthesis ----

    /// <summary>The comb post-filter with a squared-window parameter cross-fade, in place.</summary>
    static abstract void CombFilter(
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
        int overlap);

    /// <summary>
    /// De-emphasis of one or two channels at once, writing interleaved output scaled by <paramref name="scale"/>.
    /// </summary>
    /// <param name="source0">First channel input.</param>
    /// <param name="source1">Second channel input; ignored when <paramref name="channels"/> is 1.</param>
    /// <param name="destination">Interleaved output.</param>
    /// <param name="samples">Samples per channel.</param>
    /// <param name="channels">1 or 2 output channels.</param>
    /// <param name="duplicate">When true and <paramref name="channels"/> is 2, channel 1 is a copy of channel 0 and <paramref name="source1"/> is ignored.</param>
    /// <param name="memory0">Channel 0 filter memory, updated.</param>
    /// <param name="memory1">Channel 1 filter memory, updated (unchanged when duplicating).</param>
    /// <param name="scale">Output scale.</param>
    static abstract void Deemphasis(
        ReadOnlySpan<float> source0,
        ReadOnlySpan<float> source1,
        Span<float> destination,
        int samples,
        int channels,
        bool duplicate,
        ref float memory0,
        ref float memory1,
        float scale);
}
