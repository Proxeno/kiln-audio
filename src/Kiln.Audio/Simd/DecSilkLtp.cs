using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd;

/// <summary>
/// The SILK long-term prediction (LTP) synthesis loop of RFC 6716 §4.2.7.9.1, with a scalar
/// reference path and a portable <see cref="Vector128{T}"/> path.
/// </summary>
/// <remarks>
/// <para>
/// Each output sample is a five-tap <c>Smlawb</c> recurrence over the LTP history
/// <c>sLTP_Q15</c>. The loop is associative across samples only when the pitch lag is long
/// enough that a sample's write does not fall inside the read window of a later sample in the
/// same block: with four lanes and five taps that requires <c>lag &gt;= 10</c>. The vector path
/// loads the eight-sample window once per block and rebuilds the five shifted tap vectors with
/// lane shuffles, then applies the taps in the reference order.
/// </para>
/// <para>
/// <c>Smulwb(a, c) = ((int64)a * (int16)c) &gt;&gt; 16</c> is evaluated in 32-bit lanes as
/// <c>(a &gt;&gt; 16)*c + (((a &amp; 0xFFFF)*c) &gt;&gt; 16)</c>, which is exact for the same reason
/// as in <see cref="SilkLpcKernel"/>, so the vector path is bit-exact with the scalar path.
/// </para>
/// </remarks>
internal static class DecSilkLtp
{
    /// <summary>The LTP filter order (five taps).</summary>
    internal const int Taps = 5;

    /// <summary>Runs the LTP synthesis loop, choosing the vector path when it is valid.</summary>
    /// <param name="sLtpQ15">The LTP history and prediction buffer, Q15.</param>
    /// <param name="predictionLag">The read index of tap 0 for the first sample.</param>
    /// <param name="writeIndex">The write index of the first sample.</param>
    /// <param name="coefficientsQ14">The five LTP taps, Q14.</param>
    /// <param name="excitationQ14">The excitation for this subframe, Q14.</param>
    /// <param name="residualQ14">Receives the LTP residual, Q14.</param>
    /// <param name="count">The number of samples.</param>
    public static void Synthesize(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count) =>
        Synthesize(
            sLtpQ15,
            predictionLag,
            writeIndex,
            coefficientsQ14,
            excitationQ14,
            residualQ14,
            count,
            SimdConfig.UseSimd);

    /// <summary>Runs the LTP synthesis loop, forcing the vector or scalar path.</summary>
    /// <param name="sLtpQ15">The LTP history and prediction buffer, Q15.</param>
    /// <param name="predictionLag">The read index of tap 0 for the first sample.</param>
    /// <param name="writeIndex">The write index of the first sample.</param>
    /// <param name="coefficientsQ14">The five LTP taps, Q14.</param>
    /// <param name="excitationQ14">The excitation for this subframe, Q14.</param>
    /// <param name="residualQ14">Receives the LTP residual, Q14.</param>
    /// <param name="count">The number of samples.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void Synthesize(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count,
        bool useVector)
    {
        if (count > sLtpQ15.Length - writeIndex
            || count > excitationQ14.Length
            || count > residualQ14.Length
            || coefficientsQ14.Length < Taps)
        {
            throw new ArgumentException("The LTP buffers are too short for the requested count.");
        }

        // Four lanes are independent only when the lag keeps every write of the block past every read
        // of the block, which needs lag = writeIndex - predictionLag + 2 >= 10 here.
        int i = 0;
        if (useVector
            && count >= Vector128<int>.Count
            && predictionLag >= Vector128<int>.Count
            && (writeIndex - predictionLag) >= 8)
        {
            SynthesizeVector(
                sLtpQ15, predictionLag, writeIndex, coefficientsQ14, excitationQ14, residualQ14, count, ref i);
        }

        SynthesizeScalar(
            sLtpQ15, predictionLag + i, writeIndex + i, coefficientsQ14, excitationQ14[i..], residualQ14[i..], count - i);
    }

    /// <summary>The scalar reference path.</summary>
    /// <param name="sLtpQ15">The LTP history and prediction buffer, Q15.</param>
    /// <param name="predictionLag">The read index of tap 0 for the first sample.</param>
    /// <param name="writeIndex">The write index of the first sample.</param>
    /// <param name="coefficientsQ14">The five LTP taps, Q14.</param>
    /// <param name="excitationQ14">The excitation for this subframe, Q14.</param>
    /// <param name="residualQ14">Receives the LTP residual, Q14.</param>
    /// <param name="count">The number of samples.</param>
    public static void SynthesizeScalar(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            int predictionLagIndex = predictionLag + i;
            int predictionQ13 = 2;
            for (int tap = 0; tap < Taps; tap++)
            {
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, sLtpQ15[predictionLagIndex - tap], coefficientsQ14[tap]);
            }

            int residual = unchecked(excitationQ14[i] + (predictionQ13 << 1));
            residualQ14[i] = residual;
            sLtpQ15[writeIndex + i] = unchecked(residual << 1);
        }
    }

    private static void SynthesizeVector(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count,
        ref int processed)
    {
        Vector128<int> coefficient0 = Vector128.Create((int)(short)coefficientsQ14[0]);
        Vector128<int> coefficient1 = Vector128.Create((int)(short)coefficientsQ14[1]);
        Vector128<int> coefficient2 = Vector128.Create((int)(short)coefficientsQ14[2]);
        Vector128<int> coefficient3 = Vector128.Create((int)(short)coefficientsQ14[3]);
        Vector128<int> coefficient4 = Vector128.Create((int)(short)coefficientsQ14[4]);

        ref int buffer = ref MemoryMarshal.GetReference(sLtpQ15);
        ref int excitation = ref MemoryMarshal.GetReference(excitationQ14);
        ref int residual = ref MemoryMarshal.GetReference(residualQ14);
        int i = 0;
        for (; i + Vector128<int>.Count <= count; i += Vector128<int>.Count)
        {
            int read = predictionLag + i;

            // Tap t for lane j reads history[read + j - t], so the five shifted four-sample
            // windows are five unaligned loads of the same buffer (no cross-lane shuffle is
            // available for a two-source vector in the portable API).
            Vector128<int> tap0 = Vector128.LoadUnsafe(ref buffer, (nuint)read);
            Vector128<int> tap1 = Vector128.LoadUnsafe(ref buffer, (nuint)(read - 1));
            Vector128<int> tap2 = Vector128.LoadUnsafe(ref buffer, (nuint)(read - 2));
            Vector128<int> tap3 = Vector128.LoadUnsafe(ref buffer, (nuint)(read - 3));
            Vector128<int> tap4 = Vector128.LoadUnsafe(ref buffer, (nuint)(read - 4));

            Vector128<int> prediction = Vector128.Create(2);
            prediction += SmulwbVector(tap0, coefficient0);
            prediction += SmulwbVector(tap1, coefficient1);
            prediction += SmulwbVector(tap2, coefficient2);
            prediction += SmulwbVector(tap3, coefficient3);
            prediction += SmulwbVector(tap4, coefficient4);

            Vector128<int> value =
                Vector128.LoadUnsafe(ref excitation, (nuint)i) + Vector128.ShiftLeft(prediction, 1);
            value.StoreUnsafe(ref residual, (nuint)i);
            Vector128.ShiftLeft(value, 1).StoreUnsafe(ref buffer, (nuint)(writeIndex + i));
        }

        processed = i;
    }

    /// <summary>
    /// <c>Smulwb(a, c)</c> for four 32-bit lanes: the high half times the tap plus the shifted
    /// low-half product, which is exact and avoids 64-bit widening.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SmulwbVector(Vector128<int> value, Vector128<int> coefficient)
    {
        Vector128<int> high = Vector128.ShiftRightArithmetic(value, 16);
        Vector128<int> low = Vector128.ShiftRightLogical(Vector128.ShiftLeft(value, 16), 16);
        return (high * coefficient) + Vector128.ShiftRightArithmetic(low * coefficient, 16);
    }
}
