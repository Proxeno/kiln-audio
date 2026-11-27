using System.Runtime.CompilerServices;

namespace Kiln.Audio.Simd.Kernels;

internal readonly partial struct ScalarKernels : ISilkKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LpcSynthesize(Span<int> lpcQ14, ReadOnlySpan<short> coefficientsQ12, ReadOnlySpan<int> residualQ14, int gainQ10, int order, int subframeLength, Span<short> output) =>
        SilkLpcKernel.SynthesizeScalar(lpcQ14, coefficientsQ12, residualQ14, gainQ10, order, subframeLength, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LtpSynthesize(Span<int> sLtpQ15, int predictionLag, int writeIndex, ReadOnlySpan<short> coefficientsQ14, ReadOnlySpan<int> excitationQ14, Span<int> residualQ14, int count) =>
        DecSilkLtp.Synthesize(sLtpQ15, predictionLag, writeIndex, coefficientsQ14, excitationQ14, residualQ14, count, useVector: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LpcAnalysis(Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficientsQ12, int length, int order) =>
        SilkScalar.LpcAnalysis(output, input, coefficientsQ12, length, order);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ScaleToQ15(Span<int> destination, ReadOnlySpan<short> source, int gain) =>
        SilkScalar.ScaleToQ15(destination, source, gain);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ScaleWideInPlace(Span<int> values, int gain) =>
        SilkScalar.ScaleWideInPlace(values, gain);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StereoUnmix(ReadOnlySpan<short> mid, ReadOnlySpan<short> side, int frameLength, int interpLength, int pred0PrevQ13, int pred1PrevQ13, int delta0Q13, int delta1Q13, int pred0Q13, int pred1Q13, Span<short> output) =>
        SilkScalar.StereoUnmix(mid, side, 0, frameLength, interpLength, pred0PrevQ13, pred1PrevQ13, delta0Q13, delta1Q13, pred0Q13, pred1Q13, output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Deinterleave(ReadOnlySpan<short> interleaved, Span<short> first, Span<short> second, int count) =>
        SilkScalar.Deinterleave(interleaved, first, second, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AverageShorts(ReadOnlySpan<short> a, ReadOnlySpan<short> b, Span<short> destination, int count) =>
        SilkScalar.AverageShorts(a, b, destination, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShortsToFloat(ReadOnlySpan<short> source, Span<float> destination, int count, float scale) =>
        SilkScalar.ShortsToFloat(source, destination, count, scale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InterleaveToFloat(ReadOnlySpan<short> left, ReadOnlySpan<short> right, Span<float> destination, int count, float scale) =>
        SilkScalar.InterleaveToFloat(left, right, destination, count, scale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Resample(ReadOnlySpan<float> history, ReadOnlySpan<float> bank, ReadOnlySpan<int> bankOffset, ReadOnlySpan<int> bankLength, int newest, int phase, int stepSamples, int stepPhase, int up, int count, Span<short> output) =>
        SilkScalar.Resample(history, bank, bankOffset, bankLength, newest, phase, stepSamples, stepPhase, up, count, output);
}
