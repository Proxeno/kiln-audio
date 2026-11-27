using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The SILK decode kernels (SIMD-2). Implemented by the same per-tier structs as
/// <see cref="ICeltKernels"/>; the SILK synthesizer and output stage are generic over the tier, so
/// each call is a direct, inlinable call chosen at JIT time with no dispatch inside a frame.
/// </summary>
/// <remarks>
/// Every member is a loop over a whole span, never a per-sample call. All integer kernels are
/// bit-exact across tiers (the SILK decoder is bit-exact with libopus); the float kernels
/// (<see cref="InterleaveToFloat"/>, <see cref="ShortsToFloat"/> and <see cref="Resample"/>) agree
/// to within fused multiply-add rounding.
/// </remarks>
internal interface ISilkKernels
{
    /// <summary>One subframe of short-term LPC synthesis (RFC 6716 4.2.7.9.2).</summary>
    static abstract void LpcSynthesize(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int order,
        int subframeLength,
        Span<short> output);

    /// <summary>One subframe of long-term prediction synthesis (RFC 6716 4.2.7.9.1).</summary>
    static abstract void LtpSynthesize(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count);

    /// <summary>
    /// The LPC analysis filter used for LTP rewhitening: for <c>ix</c> in [order, length),
    /// <c>output[ix]</c> is the saturated, rounded Q12 residual of <c>input[ix]</c> predicted from
    /// <c>input[ix-1-j]</c>; the first <c>order</c> outputs are cleared.
    /// </summary>
    static abstract void LpcAnalysis(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficientsQ12, int length, int order);

    /// <summary><c>destination[i] = Smulwb(gain, source[i])</c> (sLTP scaling).</summary>
    static abstract void ScaleToQ15(Span<int> destination, ReadOnlySpan<short> source, int gain);

    /// <summary><c>values[i] = Smulww(gain, values[i])</c> (gain adjustment).</summary>
    static abstract void ScaleWideInPlace(Span<int> values, int gain);

    /// <summary>
    /// The mid/side to left/right conversion of <c>silk_stereo_MS_to_LR</c>, writing interleaved
    /// samples. <paramref name="mid"/> and <paramref name="side"/> hold <c>frameLength + 2</c>
    /// samples (two history samples first); the weights ramp over <paramref name="interpLength"/>
    /// samples from the previous values by the given deltas, then stay at the final values.
    /// </summary>
    static abstract void StereoUnmix(
        ReadOnlySpan<short> mid,
        ReadOnlySpan<short> side,
        int frameLength,
        int interpLength,
        int pred0PrevQ13,
        int pred1PrevQ13,
        int delta0Q13,
        int delta1Q13,
        int pred0Q13,
        int pred1Q13,
        Span<short> output);

    /// <summary>Splits interleaved stereo samples into two planes.</summary>
    static abstract void Deinterleave(ReadOnlySpan<short> interleaved, Span<short> first, Span<short> second, int count);

    /// <summary><c>destination[i] = (a[i] + b[i]) &gt;&gt; 1</c>.</summary>
    static abstract void AverageShorts(ReadOnlySpan<short> a, ReadOnlySpan<short> b, Span<short> destination, int count);

    /// <summary>Converts 16-bit samples to float and scales them.</summary>
    static abstract void ShortsToFloat(ReadOnlySpan<short> source, Span<float> destination, int count, float scale);

    /// <summary>Converts two 16-bit planes to scaled, interleaved floats.</summary>
    static abstract void InterleaveToFloat(
        ReadOnlySpan<short> left, ReadOnlySpan<short> right, Span<float> destination, int count, float scale);

    /// <summary>
    /// Evaluates <paramref name="count"/> polyphase outputs: output <c>n</c> is the dot product of
    /// <c>bank[bankOffset[phase] ..]</c> (<c>bankLength[phase]</c> floats) with the history ending at
    /// <c>newest</c>, rounded to nearest even and saturated to 16 bits.
    /// </summary>
    static abstract void Resample(
        ReadOnlySpan<float> history,
        ReadOnlySpan<float> bank,
        ReadOnlySpan<int> bankOffset,
        ReadOnlySpan<int> bankLength,
        int newest,
        int phase,
        int stepSamples,
        int stepPhase,
        int up,
        int count,
        Span<short> output);
}

/// <summary>The per-ISA primitives the shared SILK vector kernels are written against.</summary>
internal interface ISilkOps
{
    /// <summary>Prepares a lane vector of already sign-extended 16-bit values as a <see cref="Smulwb"/> operand.</summary>
    static abstract Vector128<int> FromSext(Vector128<int> sext);

    /// <summary>Prepares a lane vector whose low 16 bits are the 16-bit value as a <see cref="Smulwb"/> operand.</summary>
    static abstract Vector128<int> FromRaw(Vector128<int> raw);

    /// <summary>Per lane <c>(a * (short)c) &gt;&gt; 16</c> with <c>c</c> prepared by <see cref="FromSext"/> or <see cref="FromRaw"/>.</summary>
    static abstract Vector128<int> Smulwb(Vector128<int> a, Vector128<int> prepared);

    /// <summary>Per lane <c>((long)a * b) &gt;&gt; 16</c>, truncated to 32 bits.</summary>
    static abstract Vector128<int> Smulww(Vector128<int> a, Vector128<int> b);

    /// <summary>Adds the widened products of the low four lanes to <paramref name="acc"/>.</summary>
    static abstract Vector128<int> MulAddWideLow(Vector128<int> acc, Vector128<short> a, Vector128<short> b);

    /// <summary>Adds the widened products of the high four lanes to <paramref name="acc"/>.</summary>
    static abstract Vector128<int> MulAddWideHigh(Vector128<int> acc, Vector128<short> a, Vector128<short> b);

    /// <summary>Narrows two int vectors to one short vector with saturation.</summary>
    static abstract Vector128<short> NarrowSat(Vector128<int> low, Vector128<int> high);

    /// <summary>Saturating 16-bit add.</summary>
    static abstract Vector128<short> AddSat16(Vector128<short> a, Vector128<short> b);

    /// <summary>Saturating 16-bit subtract.</summary>
    static abstract Vector128<short> SubSat16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns the even-indexed lanes of <paramref name="a"/> then of <paramref name="b"/>.</summary>
    static abstract Vector128<short> UnzipEven16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns the odd-indexed lanes of <paramref name="a"/> then of <paramref name="b"/>.</summary>
    static abstract Vector128<short> UnzipOdd16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns <c>[a0, b0, a1, b1, a2, b2, a3, b3]</c>.</summary>
    static abstract Vector128<short> ZipLow16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns <c>[a4, b4, a5, b5, a6, b6, a7, b7]</c>.</summary>
    static abstract Vector128<short> ZipHigh16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns <c>(a + b) &gt;&gt; 1</c> per lane without overflow.</summary>
    static abstract Vector128<short> HalvingAdd16(Vector128<short> a, Vector128<short> b);

    /// <summary>Returns <c>[a0, b0, a1, b1]</c>.</summary>
    static abstract Vector128<float> ZipLow32(Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[a2, b2, a3, b3]</c>.</summary>
    static abstract Vector128<float> ZipHigh32(Vector128<float> a, Vector128<float> b);

    /// <summary>Computes <c>acc + (a * b)</c>, fused where the tier has it.</summary>
    static abstract Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[a0 + a1, a2 + a3, b0 + b1, b2 + b3]</c>.</summary>
    static abstract Vector128<float> PairwiseAdd32(Vector128<float> a, Vector128<float> b);

    /// <summary>Rounds to nearest even and converts to 32-bit integers.</summary>
    static abstract Vector128<int> RoundToInt(Vector128<float> value);
}
