// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Fixed-point primitives and small signal-processing helpers used only by the SILK encoder.
/// </summary>
/// <remarks>
/// These mirror the Appendix A macros in <c>silk/macros.h</c>, <c>silk/SigProc_FIX.h</c> and the
/// small standalone translation units. The decoder-side helpers in <see cref="SilkFixedPoint"/>
/// stay untouched; this file only adds what the encoder's analysis needs.
/// </remarks>
internal static class SilkEncoderMath
{
    // ---------------------------------------------------------------------------------------
    // Limits shared by every stage of the encoder.
    // ---------------------------------------------------------------------------------------

    /// <summary>Maximum internal sample rate, in kHz (wideband).</summary>
    internal const int MaxFsKhz = 16;

    /// <summary>Maximum API sample rate, in kHz.</summary>
    internal const int MaxApiFsKhz = 48;

    /// <summary>5 ms subframe length in milliseconds.</summary>
    internal const int SubFrameLengthMs = 5;

    /// <summary>Maximum subframe length in samples.</summary>
    internal const int MaxSubFrameLength = SubFrameLengthMs * MaxFsKhz;

    /// <summary>Maximum SILK frame length in samples (20 ms).</summary>
    internal const int MaxFrameLength = 4 * MaxSubFrameLength;

    /// <summary>Maximum number of 16-sample shell blocks in a frame.</summary>
    internal const int MaxShellBlocks = MaxFrameLength / 16;

    /// <summary>Look-ahead for the noise shape analysis, in milliseconds.</summary>
    internal const int LaShapeMs = 5;

    /// <summary>Maximum look-ahead for the noise shape analysis, in samples.</summary>
    internal const int LaShapeMax = LaShapeMs * MaxFsKhz;

    /// <summary>Look-ahead for the pitch analysis, in milliseconds.</summary>
    internal const int LaPitchMs = 2;

    /// <summary>Maximum look-ahead for the pitch analysis, in samples.</summary>
    internal const int LaPitchMax = LaPitchMs * MaxFsKhz;

    /// <summary>Long-term prediction filter order.</summary>
    internal const int LtpOrder = 5;

    /// <summary>Maximum LPC order, which is also the maximum predictor order.</summary>
    internal const int MaxLpcOrder = 16;

    /// <summary>Minimum LPC order (NB and MB).</summary>
    internal const int MinLpcOrder = 10;

    /// <summary>Maximum LPC order used as the noise shaping filter order.</summary>
    internal const int MaxShapeLpcOrder = 16;

    /// <summary>Number of harmonic shaping FIR taps.</summary>
    internal const int HarmShapeFirTaps = 3;

    /// <summary>Length of the LTP history buffer, a power of two.</summary>
    internal const int LtbBufLength = 512;

    /// <summary>Mask for the LTP history buffer.</summary>
    internal const int LtbMask = LtbBufLength - 1;

    /// <summary>Number of gain quantization levels.</summary>
    internal const int GainLevels = 64;

    /// <summary>Number of NLSF stage-1 codebook vectors.</summary>
    internal const int NlsfVectors = 32;

    /// <summary>Maximum number of NLSF survivors.</summary>
    internal const int NlsfMaxSurvivors = 32;

    /// <summary>Largest NLSF quantization residual magnitude.</summary>
    internal const int NlsfQuantMaxAmplitude = 4;

    /// <summary>Largest NLSF quantization residual magnitude including the extension.</summary>
    internal const int NlsfQuantMaxAmplitudeExt = 10;

    /// <summary>Fixed-point representative of 0.1, the NLSF level adjustment.</summary>
    internal const int NlsfQuantLevelAdjustQ10 = 102;

    /// <summary>Q domain of the Laroia weights.</summary>
    internal const int NlsfWeightQ = 2;

    /// <summary>Number of VAD bands.</summary>
    internal const int VadBandCount = 4;

    /// <summary>16-bit maximum.</summary>
    internal const int Int16Max = 32767;

    /// <summary>16-bit minimum.</summary>
    internal const int Int16Min = -32768;

    /// <summary>32-bit maximum.</summary>
    internal const int Int32Max = int.MaxValue;

    /// <summary>32-bit minimum.</summary>
    internal const int Int32Min = int.MinValue;

    // ---------------------------------------------------------------------------------------
    // Multiply-accumulate family (silk/macros.h). The 64-bit forms are mathematically identical
    // to the 32-bit macro definitions; see the note above Smulwb in SilkFixedPoint.
    // ---------------------------------------------------------------------------------------

    /// <summary>Returns <c>(a * (b &gt;&gt; 16)) &gt;&gt; 16</c>.</summary>
    internal static int Smulwt(int a, int b) => (int)(((long)a * (b >> 16)) >> 16);

    /// <summary>Returns <c>a + Smulwt(b, c)</c>.</summary>
    internal static int Smlawt(int a, int b, int c) => unchecked(a + Smulwt(b, c));

    /// <summary>Returns <c>a + (int16)b * (int16)c</c>.</summary>
    internal static int Smlabb(int a, int b, int c) => unchecked(a + ((short)b * (short)c));

    /// <summary>Returns <c>a + (int16)b * (c &gt;&gt; 16)</c>.</summary>
    internal static int Smlabt(int a, int b, int c) => unchecked(a + ((short)b * (c >> 16)));

    /// <summary>Returns <c>a + b * c</c> with 32-bit wrap-around.</summary>
    internal static int Mla(int a, int b, int c) => unchecked(a + (b * c));

    /// <summary>Returns <c>a + (b &lt;&lt; shift)</c> with 32-bit wrap-around.</summary>
    internal static int AddLshift(int a, int b, int shift) => unchecked(a + (b << shift));

    /// <summary>Returns <c>a - (b &lt;&lt; shift)</c> with 32-bit wrap-around.</summary>
    internal static int SubLshift(int a, int b, int shift) => unchecked(a - (b << shift));

    /// <summary>Returns <c>a + (b &gt;&gt; shift)</c> with 32-bit wrap-around.</summary>
    internal static int AddRshift(int a, int b, int shift) => unchecked(a + (b >> shift));

    /// <summary>Returns <c>a - (b &gt;&gt; shift)</c> with 32-bit wrap-around.</summary>
    internal static int SubRshift(int a, int b, int shift) => unchecked(a - (b >> shift));

    /// <summary>Returns <c>a &lt;&lt; shift</c> with 32-bit wrap-around.</summary>
    internal static int Shl(int a, int shift) => unchecked(a << shift);

    /// <summary>Returns <c>a &gt;&gt; shift</c> (arithmetic).</summary>
    internal static int Sar(int a, int shift) => a >> shift;

    /// <summary>Returns <c>a</c> rounded right by <paramref name="shift"/> bits.</summary>
    internal static int RshiftRound(int a, int shift) =>
        shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    /// <summary>Returns <c>a / b</c> truncated toward zero.</summary>
    internal static int Div32(int a, int b) => a / b;

    /// <summary>Returns <c>a / (int16)b</c>, matching <c>silk_DIV32_16</c>.</summary>
    internal static int Div32_16(int a, int b) => a / (short)b;

    /// <summary>Clamps <paramref name="a"/> to [<paramref name="low"/>, <paramref name="high"/>].</summary>
    internal static int Limit(int a, int low, int high) => a > high ? high : (a < low ? low : a);

    /// <summary>Saturates to a 16-bit signed value.</summary>
    internal static int Sat16(int a) => a > Int16Max ? Int16Max : (a < Int16Min ? Int16Min : a);

    /// <summary>Returns the absolute value, matching the reference <c>silk_abs</c>.</summary>
    internal static int Abs(int a) => a > 0 ? a : -a;

    /// <summary>Returns the number of leading zero bits of a 32-bit value (32 for zero).</summary>
    internal static int Clz32(int a) => System.Numerics.BitOperations.LeadingZeroCount((uint)a);

    /// <summary>Returns the number of leading zero bits of a 64-bit value (64 for zero).</summary>
    internal static int Clz64(long a) => System.Numerics.BitOperations.LeadingZeroCount((ulong)a);

    /// <summary>Returns the leading-zero count and the seven fractional bits below the leading one.</summary>
    internal static void ClzFrac(int input, out int leadingZeros, out int fractionQ7)
    {
        leadingZeros = Clz32(input);
        fractionQ7 = SilkFixedPoint.Ror32(input, 24 - leadingZeros) & 0x7F;
    }

    // ---------------------------------------------------------------------------------------
    // Log-domain conversions.
    // ---------------------------------------------------------------------------------------

    /// <summary>Approximates <c>128 * log2(x)</c> (RFC 6716 Appendix A, <c>silk_lin2log</c>).</summary>
    internal static int Lin2Log(int linear)
    {
        ClzFrac(linear, out int leadingZeros, out int fractionQ7);
        return Shl(31 - leadingZeros, 7) + Smlawb(fractionQ7, Mul(fractionQ7, 128 - fractionQ7), 179);
    }

    /// <summary>Approximates <c>2^(x / 128)</c> (RFC 6716 Appendix A, <c>silk_log2lin</c>).</summary>
    internal static int Log2Lin(int logQ7)
    {
        if (logQ7 < 0)
        {
            return 0;
        }

        int output = Shl(1, logQ7 >> 7);
        int fractionQ7 = logQ7 & 0x7F;
        if (logQ7 < 2048)
        {
            return AddRshift(output, Mul(output, Smlawb(fractionQ7, Mul(Smulbb(fractionQ7, 128 - fractionQ7), 1), -174)), 7);
        }

        return Mla(output, output >> 7, Smlawb(fractionQ7, Smulbb(fractionQ7, 128 - fractionQ7), -174));
    }

    /// <summary>Returns <c>a * b</c> with 32-bit wrap-around.</summary>
    internal static int Mul(int a, int b) => unchecked(a * b);

    /// <summary>Returns <c>a + b</c> with 32-bit wrap-around.</summary>
    internal static int Add(int a, int b) => unchecked(a + b);

    /// <summary>Returns <c>a - b</c> with 32-bit wrap-around.</summary>
    internal static int Sub(int a, int b) => unchecked(a - b);

    private static int Smlawb(int a, int b, int c) => SilkFixedPoint.Smlawb(a, b, c);

    /// <summary>Returns <c>(int16)a * (int16)b</c>.</summary>
    internal static int Smulbb(int a, int b) => unchecked((short)a * (short)b);

    // ---------------------------------------------------------------------------------------
    // Small vector operations.
    // ---------------------------------------------------------------------------------------

    /// <summary>Returns the approximate sigmoid at <paramref name="inputQ5"/> (Q15).</summary>
    internal static int SigmQ15(int inputQ5)
    {
        if (inputQ5 < 0)
        {
            inputQ5 = -inputQ5;
            if (inputQ5 >= 6 * 32)
            {
                return 0;
            }

            int index = inputQ5 >> 5;
            return SigmLutNegQ15[index] - Smulbb(SigmLutSlopeQ10[index], inputQ5 & 0x1F);
        }

        if (inputQ5 >= 6 * 32)
        {
            return 32767;
        }

        int positiveIndex = inputQ5 >> 5;
        return SigmLutPosQ15[positiveIndex] + Smulbb(SigmLutSlopeQ10[positiveIndex], inputQ5 & 0x1F);
    }

    /// <summary>Sums the squares of a 16-bit vector, returning the energy and its right shift.</summary>
    internal static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> input)
    {
        int nrg = 0;
        int shft = 0;
        int length = input.Length - 1;
        int i;
        for (i = 0; i < length; i += 2)
        {
            nrg = unchecked(nrg + (input[i] * input[i]));
            nrg = unchecked(nrg + (input[i + 1] * input[i + 1]));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft = 2;
                break;
            }
        }

        for (; i < length; i += 2)
        {
            int tmp = input[i] * input[i];
            tmp = unchecked(tmp + (input[i + 1] * input[i + 1]));
            nrg = unchecked(nrg + ((int)((uint)tmp >> shft)));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft += 2;
            }
        }

        if (i == length)
        {
            int tmp = input[i] * input[i];
            nrg = unchecked(nrg + ((int)((uint)tmp >> shft)));
        }

        if ((nrg & unchecked((int)0xC0000000)) != 0)
        {
            nrg = (int)((uint)nrg >> 2);
            shft += 2;
        }

        shift = shft;
        energy = nrg;
    }

    /// <summary>Returns the 32-bit inner product of two 16-bit vectors, with wrap-around.</summary>
    internal static int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum = unchecked(sum + (a[i] * b[i]));
        }

        return sum;
    }

    /// <summary>Returns the 64-bit inner product of two 16-bit vectors.</summary>
    internal static long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        long sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += (long)a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Inserts values into increasing order, keeping the smallest <paramref name="count"/>.</summary>
    internal static void InsertionSortIncreasing(Span<int> values, Span<int> indices, int length, int count)
    {
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        for (int i = 1; i < count; i++)
        {
            int value = values[i];
            int j;
            for (j = i - 1; j >= 0 && value < values[j]; j--)
            {
                values[j + 1] = values[j];
                indices[j + 1] = indices[j];
            }

            values[j + 1] = value;
            indices[j + 1] = i;
        }

        for (int i = count; i < length; i++)
        {
            int value = values[i];
            if (value < values[count - 1])
            {
                int j;
                for (j = count - 2; j >= 0 && value < values[j]; j--)
                {
                    values[j + 1] = values[j];
                    indices[j + 1] = indices[j];
                }

                values[j + 1] = value;
                indices[j + 1] = i;
            }
        }
    }

    /// <summary>Sorts a 16-bit vector in increasing order (insertion sort).</summary>
    internal static void SortIncreasing(Span<short> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            short value = values[i];
            int j = i - 1;
            while (j >= 0 && value < values[j])
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = value;
        }
    }

    // ---------------------------------------------------------------------------------------
    // LPC helpers.
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs the LPC analysis filter over <paramref name="input"/> (Q12 coefficients).</summary>
    internal static void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int inputIndex = ix - 1;
            int predictionQ12 = SilkFixedPoint.Smulbb(input[inputIndex], coefficients[0]);
            for (int j = 1; j < order; j++)
            {
                predictionQ12 = unchecked(predictionQ12 + (input[inputIndex - j] * coefficients[j]));
            }

            int residualQ12 = unchecked((input[inputIndex + 1] << 12) - predictionQ12);
            output[ix] = (short)Sat16(RshiftRound(residualQ12, 12));
        }

        output[..order].Clear();
    }

    /// <summary>Computes the inverse prediction gain (Q30) of Q12 LPC coefficients.</summary>
    internal static int LpcInversePredGain(ReadOnlySpan<short> coefficientsQ12, int order)
    {
        const int Qa = 24;
        const int aLimit = 16773022; // round(0.99975 * 2^24)
        Span<int> aOld = stackalloc int[MaxLpcOrder];
        Span<int> aNew = stackalloc int[MaxLpcOrder];
        Span<int> aTmp = stackalloc int[MaxLpcOrder];
        int dcResponse = 0;
        for (int k = 0; k < order; k++)
        {
            dcResponse += coefficientsQ12[k];
            aNew[k] = coefficientsQ12[k] << (Qa - 12);
        }

        if (dcResponse >= 4096)
        {
            return 0;
        }

        int inverseGainQ30 = 1 << 30;
        for (int k = order - 1; k > 0; k--)
        {
            if (aNew[k] > aLimit || aNew[k] < -aLimit)
            {
                return 0;
            }

            int rcQ31 = -SilkFixedPoint.Lshift(aNew[k], 31 - Qa);
            int rcMult1Q30 = unchecked((1 << 30) - SilkFixedPoint.Smmul(rcQ31, rcQ31));
            int mult2Q = 32 - Clz32(Abs(rcMult1Q30));
            int rcMult2 = SilkFixedPoint.Inverse32VarQ(rcMult1Q30, mult2Q + 30);
            inverseGainQ30 = SilkFixedPoint.Lshift(SilkFixedPoint.Smmul(inverseGainQ30, rcMult1Q30), 2);

            aNew.CopyTo(aOld);
            for (int n = 0; n < k; n++)
            {
                int tmpQa = unchecked(aOld[n] - (int)SilkFixedPoint.RshiftRound64(
                    (long)aOld[k - n - 1] * rcQ31, 31));
                aNew[n] = (int)SilkFixedPoint.RshiftRound64((long)tmpQa * rcMult2, mult2Q);
            }
        }

        if (aNew[0] > aLimit || aNew[0] < -aLimit)
        {
            return 0;
        }

        int rc0Q31 = -SilkFixedPoint.Lshift(aNew[0], 31 - Qa);
        int rcMult1Q30Final = unchecked((1 << 30) - SilkFixedPoint.Smmul(rc0Q31, rc0Q31));
        return SilkFixedPoint.Lshift(SilkFixedPoint.Smmul(inverseGainQ30, rcMult1Q30Final), 2);
    }

    /// <summary>Bandwidth-expands a Q12/Q16 AR filter (16-bit coefficients).</summary>
    internal static void Bwexpander(Span<short> ar, int order, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < order - 1; i++)
        {
            ar[i] = (short)RshiftRound(unchecked(chirpQ16 * ar[i]), 16);
            chirpQ16 = unchecked(chirpQ16 + RshiftRound(unchecked(chirpQ16 * chirpMinusOneQ16), 16));
        }

        ar[order - 1] = (short)RshiftRound(unchecked(chirpQ16 * ar[order - 1]), 16);
    }

    /// <summary>Bandwidth-expands a 32-bit AR filter.</summary>
    internal static void Bwexpander32(Span<int> ar, int order, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < order - 1; i++)
        {
            ar[i] = SilkFixedPoint.Smulww(chirpQ16, ar[i]);
            chirpQ16 = unchecked(chirpQ16 + RshiftRound(unchecked(chirpQ16 * chirpMinusOneQ16), 16));
        }

        ar[order - 1] = SilkFixedPoint.Smulww(chirpQ16, ar[order - 1]);
    }

    /// <summary>Converts Q15 reflection coefficients to Q24 prediction coefficients.</summary>
    internal static void K2a(Span<int> aQ24, ReadOnlySpan<short> rcQ15, int order)
    {
        Span<int> aTmp = stackalloc int[MaxLpcOrder];
        for (int k = 0; k < order; k++)
        {
            for (int n = 0; n < k; n++)
            {
                aTmp[n] = aQ24[n];
            }

            for (int n = 0; n < k; n++)
            {
                aQ24[n] = Smlawb(aQ24[n], aTmp[k - n - 1] << 1, rcQ15[k]);
            }

            aQ24[k] = -(rcQ15[k] << 9);
        }
    }

    /// <summary>Converts Q16 reflection coefficients to Q24 prediction coefficients.</summary>
    internal static void K2aQ16(Span<int> aQ24, ReadOnlySpan<int> rcQ16, int order)
    {
        Span<int> aTmp = stackalloc int[MaxLpcOrder];
        for (int k = 0; k < order; k++)
        {
            for (int n = 0; n < k; n++)
            {
                aTmp[n] = aQ24[n];
            }

            for (int n = 0; n < k; n++)
            {
                aQ24[n] = Smlaww(aQ24[n], aTmp[k - n - 1], rcQ16[k]);
            }

            aQ24[k] = -(rcQ16[k] << 8);
        }
    }

    /// <summary>Interpolates two Q15 vectors with a Q2 factor (RFC 6716 Appendix A, <c>silk_interpolate</c>).</summary>
    internal static void Interpolate(Span<short> output, ReadOnlySpan<short> first, ReadOnlySpan<short> second, int factorQ2, int length)
    {
        for (int i = 0; i < length; i++)
        {
            output[i] = (short)AddRshift(first[i], Smulbb(second[i] - first[i], factorQ2), 2);
        }
    }

    private static int Smlaww(int a, int b, int c) => SilkFixedPoint.Smlaww(a, b, c);

    // ---------------------------------------------------------------------------------------
    // Small analysis helpers shared by the pitch and prediction stages.
    // ---------------------------------------------------------------------------------------

    /// <summary>Returns the maximum absolute value of a 16-bit vector.</summary>
    internal static int Int16ArrayMaxAbs(ReadOnlySpan<short> values)
    {
        int max = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int absolute = Abs(values[i]);
            if (absolute > max)
            {
                max = absolute;
            }
        }

        return max;
    }

    /// <summary>Returns the number of leading zero bits of a 16-bit value (16 for zero).</summary>
    internal static int Clz16(int value) => System.Numerics.BitOperations.LeadingZeroCount((uint)(ushort)value) - 16;

    /// <summary>Computes the autocorrelation sequence with the reference scaling (silk_autocorr), scalar tier.</summary>
    internal static void Autocorr(out int scale, Span<int> results, ReadOnlySpan<short> input, int inputSize, int correlationCount) =>
        Autocorr<ScalarKernels>(out scale, results, input, inputSize, correlationCount);

    /// <summary>Computes the autocorrelation sequence with the reference scaling (silk_autocorr).</summary>
    internal static void Autocorr<TK>(out int scale, Span<int> results, ReadOnlySpan<short> input, int inputSize, int correlationCount)
        where TK : struct, ISilkEncoderKernels
    {
        int count = Math.Min(inputSize, correlationCount);
        long correlation64 = TK.InnerProd64(input, input, inputSize) + 1;
        int leadingZeros = Clz64(correlation64);
        int rightShifts = 35 - leadingZeros;
        scale = rightShifts;

        if (rightShifts <= 0)
        {
            results[0] = (int)(correlation64 << -rightShifts);
            for (int i = 1; i < count; i++)
            {
                results[i] = TK.InnerProd(input, input[i..], inputSize - i) << -rightShifts;
            }
        }
        else
        {
            results[0] = (int)(correlation64 >> rightShifts);
            for (int i = 1; i < count; i++)
            {
                results[i] = (int)(TK.InnerProd64(input, input[i..], inputSize - i) >> rightShifts);
            }
        }
    }

    /// <summary>Computes reflection coefficients from correlations with the fast Schur recursion.</summary>
    internal static int Schur(Span<short> rcQ15, ReadOnlySpan<int> correlations, int order)
    {
        Span<int> c0 = stackalloc int[MaxLpcOrder + 1];
        Span<int> c1 = stackalloc int[MaxLpcOrder + 1];
        int leadingZeros = Clz32(correlations[0]);
        int shift;
        if (leadingZeros < 2)
        {
            shift = -1;
        }
        else if (leadingZeros > 2)
        {
            shift = leadingZeros - 2;
        }
        else
        {
            shift = 0;
        }

        for (int k = 0; k <= order; k++)
        {
            int value = shift < 0 ? correlations[k] >> 1 : Shl(correlations[k], shift);
            c0[k] = value;
            c1[k] = value;
        }

        for (int k = 0; k < order; k++)
        {
            int reflectionQ15 = -Div32_16(c0[k + 1], Math.Max(c1[0] >> 15, 1));
            reflectionQ15 = Sat16(reflectionQ15);
            rcQ15[k] = (short)reflectionQ15;
            for (int n = 0; n < order - k; n++)
            {
                int tmp1 = c0[n + k + 1];
                int tmp2 = c1[n];
                c0[n + k + 1] = Smlawb(tmp1, tmp2 << 1, reflectionQ15);
                c1[n] = Smlawb(tmp2, tmp1 << 1, reflectionQ15);
            }
        }

        return c1[0];
    }

    /// <summary>Applies the recursive sine window used by the pitch analysis windowing.</summary>
    internal static void ApplySineWindow(Span<short> output, ReadOnlySpan<short> input, int windowType, int length)
    {
        int k = (length >> 2) - 4;
        int fQ16 = SineWindowFrequencyQ16[k];
        int cQ16 = SilkFixedPoint.Smulwb(fQ16, -fQ16);

        int s0Q16;
        int s1Q16;
        if (windowType == 1)
        {
            s0Q16 = 0;
            s1Q16 = fQ16 + (length >> 3);
        }
        else
        {
            s0Q16 = 1 << 16;
            s1Q16 = (1 << 16) + (cQ16 >> 1) + (length >> 4);
        }

        for (k = 0; k < length; k += 4)
        {
            output[k] = (short)SilkFixedPoint.Smulwb((s0Q16 + s1Q16) >> 1, input[k]);
            output[k + 1] = (short)SilkFixedPoint.Smulwb(s1Q16, input[k + 1]);
            s0Q16 = unchecked(SilkFixedPoint.Smulwb(s1Q16, cQ16) + (s1Q16 << 1) - s0Q16 + 1);
            s0Q16 = Math.Min(s0Q16, 1 << 16);

            output[k + 2] = (short)SilkFixedPoint.Smulwb((s0Q16 + s1Q16) >> 1, input[k + 2]);
            output[k + 3] = (short)SilkFixedPoint.Smulwb(s0Q16, input[k + 3]);
            s1Q16 = unchecked(SilkFixedPoint.Smulwb(s0Q16, cQ16) + (s0Q16 << 1) - s1Q16);
            s1Q16 = Math.Min(s1Q16, 1 << 16);
        }
    }

    /// <summary>Returns a saturated 32-bit addition.</summary>
    internal static int AddSat32(int a, int b) =>
        (int)Math.Clamp((long)a + b, Int32Min, Int32Max);

    /// <summary>Returns a saturated 16-bit addition.</summary>
    internal static int AddSat16(int a, int b) => Sat16(unchecked(a + b));

    private static readonly int[] SineWindowFrequencyQ16 =
    [
        12111, 9804, 8235, 7100, 6239, 5565, 5022, 4575, 4202,
        3885, 3612, 3375, 3167, 2984, 2820, 2674, 2542, 2422,
        2313, 2214, 2123, 2038, 1961, 1889, 1822, 1760, 1702,
    ];

    private static readonly int[] SigmLutSlopeQ10 = [237, 153, 73, 30, 12, 7];
    private static readonly int[] SigmLutPosQ15 = [16384, 23955, 28861, 31213, 32178, 32548];
    private static readonly int[] SigmLutNegQ15 = [16384, 8812, 3906, 1554, 589, 219];
}
