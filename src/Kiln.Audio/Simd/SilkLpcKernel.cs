using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd;

/// <summary>
/// The SILK short-term LPC synthesis loop (RFC 6716 §4.2.7.9.2) as a fixed-point kernel with a
/// scalar reference path and a <see cref="Vector{T}"/> path.
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
/// integer contribution and integer addition is associative modulo 2^32, so widening, multiplying,
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

                // Smulwb(a, c) = ((long)a * (short)c) >> 16. Split a into a = high*65536 + low with
                // high = a >> 16 (arithmetic) and low the zero-extended low half, so
                // Smulwb = high*c + ((low*c) >> 16). Both products fit in 32 bits because c is a
                // short and low is at most 65535.
                Vector128<int> high = Vector128.ShiftRightArithmetic(state, 16);
                Vector128<int> low = Vector128.ShiftRightLogical(Vector128.ShiftLeft(state, 16), 16);
                Vector128<int> term =
                    (high * coefficient)
                    + Vector128.ShiftRightArithmetic(low * coefficient, 16);

                int sum = unchecked(
                    term.GetElement(0)
                    + term.GetElement(1)
                    + term.GetElement(2)
                    + term.GetElement(3));
                predictionQ10 = unchecked(predictionQ10 + sum);
            }

            for (int t = vectorised; t < order; t++)
            {
                predictionQ10 = unchecked(
                    predictionQ10 + SilkFixedPoint.Smulwb(lpcQ14[start + t], reversedCoefficients[t]));
            }

            output[i] = Store(lpcQ14, residualQ14, gainQ10, i, predictionQ10);
        }
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
