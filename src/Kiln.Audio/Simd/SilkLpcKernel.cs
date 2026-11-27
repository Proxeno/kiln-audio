using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd;

/// <summary>
/// The SILK short-term LPC synthesis loop (RFC 6716 §4.2.7.9.2) as a fixed-point kernel with a
/// scalar reference path and a <see cref="System.Numerics.Vector{T}"/> path.
/// </summary>
/// <remarks>
/// <para>
/// The per-sample prediction is a dot product of the order-tap LPC coefficients with the most
/// recent output history, where each term is the high 16 bits of a signed 32-by-16 product
/// (<c>Smulwb</c>). The synthesis output feeds back into the next sample's history, so samples
/// are processed in order; only the dot product itself is vectorised.
/// </para>
/// <para>
/// The vector path is bit-exact with the scalar path: each term is an independently rounded
/// integer contribution and integer addition is associative modulo 2^32, so splitting, multiplying,
/// shifting and summing the lanes gives the same wrapped result as the sequential scalar chain.
/// </para>
/// </remarks>
internal static class SilkLpcKernel
{
    /// <summary>The maximum LPC order, which is also the history offset in the state buffer.</summary>
    internal const int MaxLpcOrder = 16;

    /// <summary>Runs one subframe of short-term LPC synthesis.</summary>
    /// <param name="lpcQ14">
    /// The LPC state, Q14: the first <see cref="MaxLpcOrder"/> samples are the history before this
    /// frame and the following samples receive the synthesis output.
    /// </param>
    /// <param name="coefficientsQ12">The interpolated LPC coefficients, Q12, <c>order</c> long.</param>
    /// <param name="residualQ14">The excitation for this subframe, Q14.</param>
    /// <param name="gainQ10">The subframe gain, Q10.</param>
    /// <param name="order">The LPC order (10 or 16).</param>
    /// <param name="subframeLength">The number of samples to produce.</param>
    /// <param name="output">Receives the saturated 16-bit output samples.</param>
    internal static void Synthesize(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int order,
        int subframeLength,
        Span<short> output)
    {
        if (SimdConfig.UseSimd && order >= Vector128<int>.Count)
        {
            SynthesizeVector(lpcQ14, coefficientsQ12, residualQ14, gainQ10, order, subframeLength, output);
        }
        else
        {
            SynthesizeScalar(lpcQ14, coefficientsQ12, residualQ14, gainQ10, order, subframeLength, output);
        }
    }

    /// <summary>The scalar reference path.</summary>
    /// <param name="lpcQ14">The LPC state, Q14.</param>
    /// <param name="coefficientsQ12">The interpolated LPC coefficients, Q12.</param>
    /// <param name="residualQ14">The excitation, Q14.</param>
    /// <param name="gainQ10">The subframe gain, Q10.</param>
    /// <param name="order">The LPC order.</param>
    /// <param name="subframeLength">The number of samples to produce.</param>
    /// <param name="output">Receives the output samples.</param>
    internal static void SynthesizeScalar(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int order,
        int subframeLength,
        Span<short> output)
    {
        for (int i = 0; i < subframeLength; i++)
        {
            int predictionQ10 = order >> 1;
            for (int j = 0; j < order; j++)
            {
                predictionQ10 = unchecked(
                    predictionQ10 + SilkFixedPoint.Smulwb(lpcQ14[MaxLpcOrder + i - 1 - j], coefficientsQ12[j]));
            }

            output[i] = Store(lpcQ14, residualQ14, gainQ10, i, predictionQ10);
        }
    }

    /// <summary>The vector path: the order-tap dot product runs four taps at a time.</summary>
    /// <param name="lpcQ14">The LPC state, Q14.</param>
    /// <param name="coefficientsQ12">The interpolated LPC coefficients, Q12.</param>
    /// <param name="residualQ14">The excitation, Q14.</param>
    /// <param name="gainQ10">The subframe gain, Q10.</param>
    /// <param name="order">The LPC order.</param>
    /// <param name="subframeLength">The number of samples to produce.</param>
    /// <param name="output">Receives the output samples.</param>
    internal static void SynthesizeVector(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int order,
        int subframeLength,
        Span<short> output)
    {
        // PERF-3: the 10- and 16-tap orders get a fully unrolled path that keeps the four
        // reversed-coefficient vectors in registers across the whole subframe.
        if (order == 16)
        {
            SynthesizeOrder16(lpcQ14, coefficientsQ12, residualQ14, gainQ10, subframeLength, output);
            return;
        }

        if (order == 10)
        {
            SynthesizeOrder10(lpcQ14, coefficientsQ12, residualQ14, gainQ10, subframeLength, output);
            return;
        }

        // The scalar loop walks the history backwards while the coefficients walk forwards.
        // Reversing the coefficients once turns the dot product into two ascending walks, so the
        // vector path needs no lane shuffle at all.
        Span<int> reversedCoefficients = stackalloc int[order];
        for (int t = 0; t < order; t++)
        {
            reversedCoefficients[t] = coefficientsQ12[order - 1 - t];
        }

        const int Width = 4;
        int vectorised = order - (order % Width);

        for (int i = 0; i < subframeLength; i++)
        {
            int start = MaxLpcOrder + i - order;
            int predictionQ10 = order >> 1;
            for (int t = 0; t < vectorised; t += Width)
            {
                Vector128<int> state = Load(lpcQ14, start + t);
                Vector128<int> coefficient = Load(reversedCoefficients, t);
                predictionQ10 = unchecked(predictionQ10 + Dot(state, coefficient));
            }

            for (int t = vectorised; t < order; t++)
            {
                predictionQ10 = unchecked(
                    predictionQ10 + SilkFixedPoint.Smulwb(lpcQ14[start + t], reversedCoefficients[t]));
            }

            output[i] = Store(lpcQ14, residualQ14, gainQ10, i, predictionQ10);
        }
    }

    private static void SynthesizeOrder16(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int subframeLength,
        Span<short> output)
    {
        Vector128<int> c0 = Vector128.Create(
            (int)coefficientsQ12[15], (int)coefficientsQ12[14],
            (int)coefficientsQ12[13], (int)coefficientsQ12[12]);
        Vector128<int> c1 = Vector128.Create(
            (int)coefficientsQ12[11], (int)coefficientsQ12[10],
            (int)coefficientsQ12[9], (int)coefficientsQ12[8]);
        Vector128<int> c2 = Vector128.Create(
            (int)coefficientsQ12[7], (int)coefficientsQ12[6],
            (int)coefficientsQ12[5], (int)coefficientsQ12[4]);
        Vector128<int> c3 = Vector128.Create(
            (int)coefficientsQ12[3], (int)coefficientsQ12[2],
            (int)coefficientsQ12[1], (int)coefficientsQ12[0]);

        for (int i = 0; i < subframeLength; i++)
        {
            int start = MaxLpcOrder + i - 16;
            int predictionQ10 = 8;
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start), c0));
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start + 4), c1));
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start + 8), c2));
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start + 12), c3));
            output[i] = Store(lpcQ14, residualQ14, gainQ10, i, predictionQ10);
        }
    }

    private static void SynthesizeOrder10(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int subframeLength,
        Span<short> output)
    {
        Vector128<int> c0 = Vector128.Create(
            (int)coefficientsQ12[9], (int)coefficientsQ12[8],
            (int)coefficientsQ12[7], (int)coefficientsQ12[6]);
        Vector128<int> c1 = Vector128.Create(
            (int)coefficientsQ12[5], (int)coefficientsQ12[4],
            (int)coefficientsQ12[3], (int)coefficientsQ12[2]);

        for (int i = 0; i < subframeLength; i++)
        {
            int start = MaxLpcOrder + i - 10;
            int predictionQ10 = 5;
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start), c0));
            predictionQ10 = unchecked(predictionQ10 + Dot(Load(lpcQ14, start + 4), c1));
            predictionQ10 = unchecked(
                predictionQ10 + SilkFixedPoint.Smulwb(lpcQ14[start + 8], coefficientsQ12[1]));
            predictionQ10 = unchecked(
                predictionQ10 + SilkFixedPoint.Smulwb(lpcQ14[start + 9], coefficientsQ12[0]));
            output[i] = Store(lpcQ14, residualQ14, gainQ10, i, predictionQ10);
        }
    }

    /// <summary>
    /// Sums one four-tap group of <c>Smulwb</c> contributions. <c>Smulwb(a, c) = ((long)a * (short)c) >> 16</c>:
    /// splitting <c>a</c> into <c>high*65536 + low</c> gives <c>high*c + ((low*c) >> 16)</c>, and both
    /// products fit in 32 bits because <c>c</c> is a short and <c>low</c> is at most 65535.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Dot(Vector128<int> state, Vector128<int> coefficient)
    {
        Vector128<int> high = Vector128.ShiftRightArithmetic(state, 16);
        Vector128<int> low = Vector128.ShiftRightLogical(Vector128.ShiftLeft(state, 16), 16);
        Vector128<int> term =
            (high * coefficient)
            + Vector128.ShiftRightArithmetic(low * coefficient, 16);
        return unchecked(
            term.GetElement(0)
            + term.GetElement(1)
            + term.GetElement(2)
            + term.GetElement(3));
    }

    private static Vector128<int> Load(ReadOnlySpan<int> values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);

    private static short Store(
        Span<int> lpcQ14,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int i,
        int predictionQ10)
    {
        int value = unchecked(residualQ14[i] + (predictionQ10 << 4));
        lpcQ14[MaxLpcOrder + i] = value;
        int scaled = SilkFixedPoint.Smulww(value, gainQ10);
        return (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(scaled, 8));
    }
}
