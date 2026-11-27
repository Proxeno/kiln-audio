using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The SILK decode kernel bodies shared by the three vector tiers, parameterised by an
/// <see cref="ISilkOps"/> struct so each tier compiles its own instruction selection with no
/// runtime dispatch. The integer kernels are bit-exact with the scalar tier.
/// </summary>
internal static class SilkDecVec<T>
    where T : struct, ISilkOps
{
    private const int MaxLpcOrder = 16;
    private const int LtpTaps = 5;

    private static ArgumentException TooShort() => new("A SILK kernel buffer is too short for the requested count.");

    // ---- LPC synthesis ----

    /// <summary>One subframe of short-term LPC synthesis; orders 10 and 16 are vectorised.</summary>
    public static void LpcSynthesize(
        Span<int> lpcQ14,
        ReadOnlySpan<short> coefficientsQ12,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int order,
        int subframeLength,
        Span<short> output)
    {
        if (lpcQ14.Length < MaxLpcOrder + subframeLength
            || residualQ14.Length < subframeLength
            || output.Length < subframeLength
            || coefficientsQ12.Length < order)
        {
            throw TooShort();
        }

        if (order == 16)
        {
            LpcSynthesize16(lpcQ14, coefficientsQ12, residualQ14, gainQ10, subframeLength, output);
        }
        else if (order == 10)
        {
            LpcSynthesize10(lpcQ14, coefficientsQ12, residualQ14, gainQ10, subframeLength, output);
        }
        else
        {
            SilkLpcKernel.SynthesizeScalar(lpcQ14, coefficientsQ12, residualQ14, gainQ10, order, subframeLength, output);
        }
    }

    private static void LpcSynthesize16(
        Span<int> lpcQ14,
        ReadOnlySpan<short> a,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int subframeLength,
        Span<short> output)
    {
        Vector128<int> c0 = T.FromSext(Vector128.Create((int)a[15], a[14], a[13], a[12]));
        Vector128<int> c1 = T.FromSext(Vector128.Create((int)a[11], a[10], a[9], a[8]));
        Vector128<int> c2 = T.FromSext(Vector128.Create((int)a[7], a[6], a[5], a[4]));
        int a3 = a[3];
        int a2 = a[2];
        int a1 = a[1];
        int a0 = a[0];
        ref int lpc = ref MemoryMarshal.GetReference(lpcQ14);
        ref int residual = ref MemoryMarshal.GetReference(residualQ14);
        ref short destination = ref MemoryMarshal.GetReference(output);

        for (int i = 0; i < subframeLength; i++)
        {
            // Only the a[0] term depends on the newest output; the rest read samples at least two
            // old, so the dot product overlaps the dependency chain. Integer addition is
            // associative modulo 2^32, so the result is bit-exact.
            Vector128<int> older = T.Smulwb(Vector128.LoadUnsafe(ref lpc, (nuint)i), c0);
            older += T.Smulwb(Vector128.LoadUnsafe(ref lpc, (nuint)(i + 4)), c1);
            older += T.Smulwb(Vector128.LoadUnsafe(ref lpc, (nuint)(i + 8)), c2);
            int prediction = unchecked(8 + Vector128.Sum(older));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, i + 12), a3));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, i + 13), a2));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, i + 14), a1));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, i + 15), a0));
            int value = unchecked(Unsafe.Add(ref residual, i) + (prediction << 4));
            Unsafe.Add(ref lpc, MaxLpcOrder + i) = value;
            int scaled = SilkFixedPoint.Smulww(value, gainQ10);
            Unsafe.Add(ref destination, i) = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(scaled, 8));
        }
    }

    private static void LpcSynthesize10(
        Span<int> lpcQ14,
        ReadOnlySpan<short> a,
        ReadOnlySpan<int> residualQ14,
        int gainQ10,
        int subframeLength,
        Span<short> output)
    {
        Vector128<int> c0 = T.FromSext(Vector128.Create((int)a[9], a[8], a[7], a[6]));
        Vector128<int> c1 = T.FromSext(Vector128.Create((int)a[5], a[4], a[3], a[2]));
        int a1 = a[1];
        int a0 = a[0];
        ref int lpc = ref MemoryMarshal.GetReference(lpcQ14);
        ref int residual = ref MemoryMarshal.GetReference(residualQ14);
        ref short destination = ref MemoryMarshal.GetReference(output);

        for (int i = 0; i < subframeLength; i++)
        {
            // History for sample i starts at lpc[16 + i - 10].
            int start = MaxLpcOrder + i - 10;
            Vector128<int> terms = T.Smulwb(Vector128.LoadUnsafe(ref lpc, (nuint)start), c0);
            terms += T.Smulwb(Vector128.LoadUnsafe(ref lpc, (nuint)(start + 4)), c1);
            int prediction = unchecked(5 + Vector128.Sum(terms));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, start + 8), a1));
            prediction = unchecked(prediction + SilkFixedPoint.Smulwb(Unsafe.Add(ref lpc, start + 9), a0));
            int value = unchecked(Unsafe.Add(ref residual, i) + (prediction << 4));
            Unsafe.Add(ref lpc, MaxLpcOrder + i) = value;
            int scaled = SilkFixedPoint.Smulww(value, gainQ10);
            Unsafe.Add(ref destination, i) = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(scaled, 8));
        }
    }

    // ---- LTP synthesis ----

    /// <summary>One subframe of LTP synthesis; four lanes at a time when the lag allows it.</summary>
    public static void LtpSynthesize(
        Span<int> sLtpQ15,
        int predictionLag,
        int writeIndex,
        ReadOnlySpan<short> coefficientsQ14,
        ReadOnlySpan<int> excitationQ14,
        Span<int> residualQ14,
        int count)
    {
        if (count > sLtpQ15.Length - writeIndex
            || count > excitationQ14.Length
            || count > residualQ14.Length
            || coefficientsQ14.Length < LtpTaps
            || predictionLag < LtpTaps - 1
            || predictionLag + count > sLtpQ15.Length)
        {
            throw TooShort();
        }

        int i = 0;

        // Four lanes are independent only when every write of a block lands past every read of
        // the block: lag = writeIndex - predictionLag + 2 >= 10.
        if (count >= 4 && predictionLag >= 4 && (writeIndex - predictionLag) >= 8)
        {
            Vector128<int> k0 = T.FromSext(Vector128.Create((int)coefficientsQ14[0]));
            Vector128<int> k1 = T.FromSext(Vector128.Create((int)coefficientsQ14[1]));
            Vector128<int> k2 = T.FromSext(Vector128.Create((int)coefficientsQ14[2]));
            Vector128<int> k3 = T.FromSext(Vector128.Create((int)coefficientsQ14[3]));
            Vector128<int> k4 = T.FromSext(Vector128.Create((int)coefficientsQ14[4]));
            ref int buffer = ref MemoryMarshal.GetReference(sLtpQ15);
            ref int excitation = ref MemoryMarshal.GetReference(excitationQ14);
            ref int residual = ref MemoryMarshal.GetReference(residualQ14);
            for (; i + 4 <= count; i += 4)
            {
                int read = predictionLag + i;
                Vector128<int> prediction = Vector128.Create(2);
                prediction += T.Smulwb(Vector128.LoadUnsafe(ref buffer, (nuint)read), k0);
                prediction += T.Smulwb(Vector128.LoadUnsafe(ref buffer, (nuint)(read - 1)), k1);
                prediction += T.Smulwb(Vector128.LoadUnsafe(ref buffer, (nuint)(read - 2)), k2);
                prediction += T.Smulwb(Vector128.LoadUnsafe(ref buffer, (nuint)(read - 3)), k3);
                prediction += T.Smulwb(Vector128.LoadUnsafe(ref buffer, (nuint)(read - 4)), k4);
                Vector128<int> value = Vector128.LoadUnsafe(ref excitation, (nuint)i) + Vector128.ShiftLeft(prediction, 1);
                value.StoreUnsafe(ref residual, (nuint)i);
                Vector128.ShiftLeft(value, 1).StoreUnsafe(ref buffer, (nuint)(writeIndex + i));
            }
        }

        if (i < count)
        {
            DecSilkLtp.SynthesizeScalar(
                sLtpQ15, predictionLag + i, writeIndex + i, coefficientsQ14, excitationQ14[i..], residualQ14[i..], count - i);
        }
    }

    // ---- LTP rewhitening and scaling ----

    /// <summary>The LPC analysis filter, eight outputs per step across the whole tap loop.</summary>
    public static void LpcAnalysis(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficientsQ12, int length, int order)
    {
        if (output.Length < order || order <= 0 || coefficientsQ12.Length < order)
        {
            throw TooShort();
        }

        if (length <= order)
        {
            output[..order].Clear();
            return;
        }

        if (input.Length < length || output.Length < length)
        {
            throw TooShort();
        }

        ref short source = ref MemoryMarshal.GetReference(input);
        ref short destination = ref MemoryMarshal.GetReference(output);
        int ix = order;
        for (; ix + 8 <= length; ix += 8)
        {
            Vector128<int> lowA = Vector128<int>.Zero;
            Vector128<int> highA = Vector128<int>.Zero;
            Vector128<int> lowB = Vector128<int>.Zero;
            Vector128<int> highB = Vector128<int>.Zero;
            int j = 0;
            for (; j + 2 <= order; j += 2)
            {
                Vector128<short> x0 = Vector128.LoadUnsafe(ref source, (nuint)(ix - 1 - j));
                Vector128<short> x1 = Vector128.LoadUnsafe(ref source, (nuint)(ix - 2 - j));
                Vector128<short> c0 = Vector128.Create(coefficientsQ12[j]);
                Vector128<short> c1 = Vector128.Create(coefficientsQ12[j + 1]);
                lowA = T.MulAddWideLow(lowA, x0, c0);
                highA = T.MulAddWideHigh(highA, x0, c0);
                lowB = T.MulAddWideLow(lowB, x1, c1);
                highB = T.MulAddWideHigh(highB, x1, c1);
            }

            if (j < order)
            {
                Vector128<short> x0 = Vector128.LoadUnsafe(ref source, (nuint)(ix - 1 - j));
                Vector128<short> c0 = Vector128.Create(coefficientsQ12[j]);
                lowA = T.MulAddWideLow(lowA, x0, c0);
                highA = T.MulAddWideHigh(highA, x0, c0);
            }

            Vector128<short> current = Vector128.LoadUnsafe(ref source, (nuint)ix);
            Vector128<int> residualLow = Vector128.ShiftLeft(Vector128.WidenLower(current), 12) - (lowA + lowB);
            Vector128<int> residualHigh = Vector128.ShiftLeft(Vector128.WidenUpper(current), 12) - (highA + highB);

            // RshiftRound(x, 12) = ((x >> 11) + 1) >> 1.
            Vector128<int> one = Vector128.Create(1);
            residualLow = Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(residualLow, 11) + one, 1);
            residualHigh = Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(residualHigh, 11) + one, 1);
            T.NarrowSat(residualLow, residualHigh).StoreUnsafe(ref destination, (nuint)ix);
        }

        SilkDecScalar.LpcAnalysisRange(output, input, coefficientsQ12, ix, length, order);
        output[..order].Clear();
    }

    /// <summary><c>destination[i] = Smulwb(gain, source[i])</c>.</summary>
    public static void ScaleToQ15(Span<int> destination, ReadOnlySpan<short> source, int gain)
    {
        int count = source.Length;
        if (destination.Length < count)
        {
            throw TooShort();
        }

        ref short s = ref MemoryMarshal.GetReference(source);
        ref int d = ref MemoryMarshal.GetReference(destination);
        Vector128<int> g = Vector128.Create(gain);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> v = Vector128.LoadUnsafe(ref s, (nuint)i);
            T.Smulwb(g, T.FromSext(Vector128.WidenLower(v))).StoreUnsafe(ref d, (nuint)i);
            T.Smulwb(g, T.FromSext(Vector128.WidenUpper(v))).StoreUnsafe(ref d, (nuint)(i + 4));
        }

        for (; i < count; i++)
        {
            destination[i] = SilkFixedPoint.Smulwb(gain, source[i]);
        }
    }

    /// <summary><c>values[i] = Smulww(gain, values[i])</c>.</summary>
    public static void ScaleWideInPlace(Span<int> values, int gain)
    {
        ref int d = ref MemoryMarshal.GetReference(values);
        Vector128<int> g = Vector128.Create(gain);
        int count = values.Length;
        int i = 0;
        for (; i + 4 <= count; i += 4)
        {
            T.Smulww(g, Vector128.LoadUnsafe(ref d, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < count; i++)
        {
            values[i] = SilkFixedPoint.Smulww(gain, values[i]);
        }
    }

    // ---- Stereo ----

    /// <summary>Mid/side to left/right, eight samples per step, writing interleaved output.</summary>
    public static void StereoUnmix(
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
        Span<short> output)
    {
        if (mid.Length < frameLength + 2 || side.Length < frameLength + 2 || output.Length < 2 * frameLength)
        {
            throw TooShort();
        }

        ref short m = ref MemoryMarshal.GetReference(mid);
        ref short s = ref MemoryMarshal.GetReference(side);
        ref short o = ref MemoryMarshal.GetReference(output);
        int n = 0;

        // Ramp phase: pred(n) = prev + (n + 1) * delta.
        Vector128<int> lanes = Vector128.Create(1, 2, 3, 4);
        Vector128<int> p0Low = Vector128.Create(pred0PrevQ13) + (Vector128.Create(delta0Q13) * lanes);
        Vector128<int> p1Low = Vector128.Create(pred1PrevQ13) + (Vector128.Create(delta1Q13) * lanes);
        Vector128<int> step0 = Vector128.Create(4 * delta0Q13);
        Vector128<int> step1 = Vector128.Create(4 * delta1Q13);
        int rampEnd = Math.Min(interpLength, frameLength);
        for (; n + 8 <= rampEnd; n += 8)
        {
            Vector128<int> p0High = p0Low + step0;
            Vector128<int> p1High = p1Low + step1;
            UnmixBlock(ref m, ref s, ref o, n, T.FromRaw(p0Low), T.FromRaw(p0High), T.FromRaw(p1Low), T.FromRaw(p1High));
            p0Low = p0High + step0;
            p1Low = p1High + step1;
        }

        // Constant phase.
        Vector128<int> c0 = T.FromRaw(Vector128.Create(pred0Q13));
        Vector128<int> c1 = T.FromRaw(Vector128.Create(pred1Q13));
        int constantStart = Math.Max(n, rampEnd);
        if (n < rampEnd)
        {
            // A ramp that does not end on a block boundary: finish it with the scalar path.
            SilkDecScalar.StereoUnmix(mid, side, n, rampEnd, interpLength, pred0PrevQ13, pred1PrevQ13, delta0Q13, delta1Q13, pred0Q13, pred1Q13, output);
            n = rampEnd;
        }

        for (n = constantStart; n + 8 <= frameLength; n += 8)
        {
            UnmixBlock(ref m, ref s, ref o, n, c0, c0, c1, c1);
        }

        if (n < frameLength)
        {
            SilkDecScalar.StereoUnmix(mid, side, n, frameLength, interpLength, pred0PrevQ13, pred1PrevQ13, delta0Q13, delta1Q13, pred0Q13, pred1Q13, output);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnmixBlock(
        ref short m, ref short s, ref short o, int n,
        Vector128<int> p0Low, Vector128<int> p0High, Vector128<int> p1Low, Vector128<int> p1High)
    {
        Vector128<short> m0 = Vector128.LoadUnsafe(ref m, (nuint)n);
        Vector128<short> m1 = Vector128.LoadUnsafe(ref m, (nuint)(n + 1));
        Vector128<short> m2 = Vector128.LoadUnsafe(ref m, (nuint)(n + 2));
        Vector128<short> sd = Vector128.LoadUnsafe(ref s, (nuint)(n + 1));

        Vector128<int> low = SideHalf(
            Vector128.WidenLower(m0), Vector128.WidenLower(m1), Vector128.WidenLower(m2), Vector128.WidenLower(sd), p0Low, p1Low);
        Vector128<int> high = SideHalf(
            Vector128.WidenUpper(m0), Vector128.WidenUpper(m1), Vector128.WidenUpper(m2), Vector128.WidenUpper(sd), p0High, p1High);
        Vector128<short> predicted = T.NarrowSat(low, high);
        Vector128<short> left = T.AddSat16(m1, predicted);
        Vector128<short> right = T.SubSat16(m1, predicted);
        T.ZipLow16(left, right).StoreUnsafe(ref o, (nuint)(2 * n));
        T.ZipHigh16(left, right).StoreUnsafe(ref o, (nuint)((2 * n) + 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SideHalf(
        Vector128<int> m0, Vector128<int> m1, Vector128<int> m2, Vector128<int> sd, Vector128<int> p0, Vector128<int> p1)
    {
        Vector128<int> sum = Vector128.ShiftLeft(m0 + m2 + Vector128.ShiftLeft(m1, 1), 9);
        sum = Vector128.ShiftLeft(sd, 8) + T.Smulwb(sum, p0);
        sum += T.Smulwb(Vector128.ShiftLeft(m1, 11), p1);

        // RshiftRound(sum, 8) = ((sum >> 7) + 1) >> 1; the saturation happens in the narrowing.
        return Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(sum, 7) + Vector128.Create(1), 1);
    }

    // ---- Output plumbing ----

    /// <summary>Splits interleaved stereo into two planes.</summary>
    public static void Deinterleave(ReadOnlySpan<short> interleaved, Span<short> first, Span<short> second, int count)
    {
        if (interleaved.Length < 2 * count || first.Length < count || second.Length < count)
        {
            throw TooShort();
        }

        ref short src = ref MemoryMarshal.GetReference(interleaved);
        ref short a = ref MemoryMarshal.GetReference(first);
        ref short b = ref MemoryMarshal.GetReference(second);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> v0 = Vector128.LoadUnsafe(ref src, (nuint)(2 * i));
            Vector128<short> v1 = Vector128.LoadUnsafe(ref src, (nuint)((2 * i) + 8));
            T.UnzipEven16(v0, v1).StoreUnsafe(ref a, (nuint)i);
            T.UnzipOdd16(v0, v1).StoreUnsafe(ref b, (nuint)i);
        }

        for (; i < count; i++)
        {
            first[i] = interleaved[2 * i];
            second[i] = interleaved[(2 * i) + 1];
        }
    }

    /// <summary><c>destination[i] = (a[i] + b[i]) &gt;&gt; 1</c>.</summary>
    public static void AverageShorts(ReadOnlySpan<short> a, ReadOnlySpan<short> b, Span<short> destination, int count)
    {
        if (a.Length < count || b.Length < count || destination.Length < count)
        {
            throw TooShort();
        }

        ref short pa = ref MemoryMarshal.GetReference(a);
        ref short pb = ref MemoryMarshal.GetReference(b);
        ref short pd = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            T.HalvingAdd16(Vector128.LoadUnsafe(ref pa, (nuint)i), Vector128.LoadUnsafe(ref pb, (nuint)i))
                .StoreUnsafe(ref pd, (nuint)i);
        }

        for (; i < count; i++)
        {
            destination[i] = (short)((a[i] + b[i]) >> 1);
        }
    }

    /// <summary>Converts 16-bit samples to scaled floats.</summary>
    public static void ShortsToFloat(ReadOnlySpan<short> source, Span<float> destination, int count, float scale)
    {
        if (source.Length < count || destination.Length < count)
        {
            throw TooShort();
        }

        ref short s = ref MemoryMarshal.GetReference(source);
        ref float d = ref MemoryMarshal.GetReference(destination);
        Vector128<float> k = Vector128.Create(scale);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> v = Vector128.LoadUnsafe(ref s, (nuint)i);
            (Vector128.ConvertToSingle(Vector128.WidenLower(v)) * k).StoreUnsafe(ref d, (nuint)i);
            (Vector128.ConvertToSingle(Vector128.WidenUpper(v)) * k).StoreUnsafe(ref d, (nuint)(i + 4));
        }

        for (; i < count; i++)
        {
            destination[i] = source[i] * scale;
        }
    }

    /// <summary>Converts two 16-bit planes to scaled, interleaved floats.</summary>
    public static void InterleaveToFloat(
        ReadOnlySpan<short> left, ReadOnlySpan<short> right, Span<float> destination, int count, float scale)
    {
        if (left.Length < count || right.Length < count || destination.Length < 2 * count)
        {
            throw TooShort();
        }

        ref short l = ref MemoryMarshal.GetReference(left);
        ref short r = ref MemoryMarshal.GetReference(right);
        ref float d = ref MemoryMarshal.GetReference(destination);
        Vector128<float> k = Vector128.Create(scale);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            Vector128<short> lv = Vector128.LoadUnsafe(ref l, (nuint)i);
            Vector128<short> rv = Vector128.LoadUnsafe(ref r, (nuint)i);
            Vector128<float> l0 = Vector128.ConvertToSingle(Vector128.WidenLower(lv)) * k;
            Vector128<float> l1 = Vector128.ConvertToSingle(Vector128.WidenUpper(lv)) * k;
            Vector128<float> r0 = Vector128.ConvertToSingle(Vector128.WidenLower(rv)) * k;
            Vector128<float> r1 = Vector128.ConvertToSingle(Vector128.WidenUpper(rv)) * k;
            T.ZipLow32(l0, r0).StoreUnsafe(ref d, (nuint)(2 * i));
            T.ZipHigh32(l0, r0).StoreUnsafe(ref d, (nuint)((2 * i) + 4));
            T.ZipLow32(l1, r1).StoreUnsafe(ref d, (nuint)((2 * i) + 8));
            T.ZipHigh32(l1, r1).StoreUnsafe(ref d, (nuint)((2 * i) + 12));
        }

        for (; i < count; i++)
        {
            destination[2 * i] = left[i] * scale;
            destination[(2 * i) + 1] = right[i] * scale;
        }
    }

    // ---- Resampler ----

    /// <summary>Evaluates polyphase outputs four at a time with a transposed reduction.</summary>
    public static void Resample(
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
        Span<short> output)
    {
        SilkDecScalar.ValidateResample(history, bank, bankOffset, bankLength, newest, phase, stepSamples, stepPhase, up, count, output);
        ref float h = ref MemoryMarshal.GetReference(history);
        ref float b = ref MemoryMarshal.GetReference(bank);
        ref short o = ref MemoryMarshal.GetReference(output);
        int n = 0;
        for (; n + 4 <= count; n += 4)
        {
            int newest0 = newest;
            int phase0 = phase;
            Advance(ref newest, ref phase, stepSamples, stepPhase, up);
            int newest1 = newest;
            int phase1 = phase;
            Advance(ref newest, ref phase, stepSamples, stepPhase, up);
            int newest2 = newest;
            int phase2 = phase;
            Advance(ref newest, ref phase, stepSamples, stepPhase, up);
            int newest3 = newest;
            int phase3 = phase;
            Advance(ref newest, ref phase, stepSamples, stepPhase, up);

            int length = bankLength[phase0];
            Vector128<float> sums;
            if (length == bankLength[phase1] && length == bankLength[phase2] && length == bankLength[phase3])
            {
                ref float x0 = ref Unsafe.Add(ref h, newest0 - length + 1);
                ref float x1 = ref Unsafe.Add(ref h, newest1 - length + 1);
                ref float x2 = ref Unsafe.Add(ref h, newest2 - length + 1);
                ref float x3 = ref Unsafe.Add(ref h, newest3 - length + 1);
                ref float c0 = ref Unsafe.Add(ref b, bankOffset[phase0]);
                ref float c1 = ref Unsafe.Add(ref b, bankOffset[phase1]);
                ref float c2 = ref Unsafe.Add(ref b, bankOffset[phase2]);
                ref float c3 = ref Unsafe.Add(ref b, bankOffset[phase3]);
                Vector128<float> a0 = Vector128<float>.Zero;
                Vector128<float> a1 = Vector128<float>.Zero;
                Vector128<float> a2 = Vector128<float>.Zero;
                Vector128<float> a3 = Vector128<float>.Zero;
                for (int t = 0; t < length; t += 4)
                {
                    nuint k = (nuint)t;
                    a0 = T.MulAdd(a0, Vector128.LoadUnsafe(ref x0, k), Vector128.LoadUnsafe(ref c0, k));
                    a1 = T.MulAdd(a1, Vector128.LoadUnsafe(ref x1, k), Vector128.LoadUnsafe(ref c1, k));
                    a2 = T.MulAdd(a2, Vector128.LoadUnsafe(ref x2, k), Vector128.LoadUnsafe(ref c2, k));
                    a3 = T.MulAdd(a3, Vector128.LoadUnsafe(ref x3, k), Vector128.LoadUnsafe(ref c3, k));
                }

                sums = T.PairwiseAdd32(T.PairwiseAdd32(a0, a1), T.PairwiseAdd32(a2, a3));
            }
            else
            {
                sums = Vector128.Create(
                    Dot(ref h, ref b, bankLength, bankOffset, newest0, phase0),
                    Dot(ref h, ref b, bankLength, bankOffset, newest1, phase1),
                    Dot(ref h, ref b, bankLength, bankOffset, newest2, phase2),
                    Dot(ref h, ref b, bankLength, bankOffset, newest3, phase3));
            }

            Vector128<int> rounded = T.RoundToInt(sums);
            T.NarrowSat(rounded, rounded).GetLower().StoreUnsafe(ref o, (nuint)n);
        }

        for (; n < count; n++)
        {
            float sum = Dot(ref h, ref b, bankLength, bankOffset, newest, phase);
            float rounded = MathF.Round(sum);
            Unsafe.Add(ref o, n) = rounded > short.MaxValue ? short.MaxValue : rounded < short.MinValue ? short.MinValue : (short)rounded;
            Advance(ref newest, ref phase, stepSamples, stepPhase, up);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Advance(ref int newest, ref int phase, int stepSamples, int stepPhase, int up)
    {
        newest += stepSamples;
        phase += stepPhase;
        if (phase >= up)
        {
            phase -= up;
            newest++;
        }
    }

    private static float Dot(ref float h, ref float b, ReadOnlySpan<int> bankLength, ReadOnlySpan<int> bankOffset, int newest, int phase)
    {
        int length = bankLength[phase];
        ref float x = ref Unsafe.Add(ref h, newest - length + 1);
        ref float c = ref Unsafe.Add(ref b, bankOffset[phase]);
        Vector128<float> a0 = Vector128<float>.Zero;
        Vector128<float> a1 = Vector128<float>.Zero;
        int t = 0;
        for (; t + 8 <= length; t += 8)
        {
            a0 = T.MulAdd(a0, Vector128.LoadUnsafe(ref x, (nuint)t), Vector128.LoadUnsafe(ref c, (nuint)t));
            a1 = T.MulAdd(a1, Vector128.LoadUnsafe(ref x, (nuint)(t + 4)), Vector128.LoadUnsafe(ref c, (nuint)(t + 4)));
        }

        for (; t + 4 <= length; t += 4)
        {
            a0 = T.MulAdd(a0, Vector128.LoadUnsafe(ref x, (nuint)t), Vector128.LoadUnsafe(ref c, (nuint)t));
        }

        return Vector128.Sum(a0 + a1);
    }

    // ---- Integer-ratio upsampler over time lanes ----

    /// <summary>Upsamples by 3, 4 or 6 with four input samples per vector, sharing every coefficient load across phases.</summary>
    public static int ResampleUp(
        ReadOnlySpan<float> history, ReadOnlySpan<float> bankT, int up, int length, int newest, int inputs, Span<short> output)
    {
        SilkDecScalar.ValidateResampleUp(history, bankT, up, length, newest, inputs, output);
        ref float h = ref MemoryMarshal.GetReference(history);
        ref float c = ref MemoryMarshal.GetReference(bankT);
        ref short o = ref MemoryMarshal.GetReference(output);
        int done = inputs & ~3;
        int j = 0;
        if (up == 6)
        {
            for (; j + 8 <= done; j += 8)
            {
                Up6Pair(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 6));
            }

            for (; j < done; j += 4)
            {
                Up6Single(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 6));
            }
        }
        else if (up == 4)
        {
            for (; j + 8 <= done; j += 8)
            {
                Up4Pair(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 4));
            }

            for (; j < done; j += 4)
            {
                Up4Single(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 4));
            }
        }
        else
        {
            for (; j + 8 <= done; j += 8)
            {
                Up3Pair(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 3));
            }

            for (; j < done; j += 4)
            {
                Up3Single(ref Unsafe.Add(ref h, newest + j - length + 1), ref c, length, ref Unsafe.Add(ref o, j * 3));
            }
        }

        if (done < inputs)
        {
            // Finish the last one to three input samples with the same lane arithmetic, so the result
            // does not depend on how the stream was chunked; the extra lanes are computed and dropped.
            Span<short> tail = stackalloc short[24];
            ref short t = ref MemoryMarshal.GetReference(tail);
            ref float x = ref Unsafe.Add(ref h, newest + done - length + 1);
            if (up == 6)
            {
                Up6Single(ref x, ref c, length, ref t);
            }
            else if (up == 4)
            {
                Up4Single(ref x, ref c, length, ref t);
            }
            else
            {
                Up3Single(ref x, ref c, length, ref t);
            }

            tail[..((inputs - done) * up)].CopyTo(output[(done * up)..]);
        }

        return inputs;
    }

    private static void Up6Pair(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default;
        Vector128<float> b0 = default, b1 = default, b2 = default, b3 = default, b4 = default, b5 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> xb = Vector128.LoadUnsafe(ref x, (nuint)(k + 4));
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(8 * k));
            Vector128<float> c1 = Vector128.LoadUnsafe(ref c, (nuint)((8 * k) + 4));
            a0 = T.MulAddLane0(a0, xa, c0);
            b0 = T.MulAddLane0(b0, xb, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            b1 = T.MulAddLane1(b1, xb, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
            b2 = T.MulAddLane2(b2, xb, c0);
            a3 = T.MulAddLane3(a3, xa, c0);
            b3 = T.MulAddLane3(b3, xb, c0);
            a4 = T.MulAddLane0(a4, xa, c1);
            b4 = T.MulAddLane0(b4, xb, c1);
            a5 = T.MulAddLane1(a5, xa, c1);
            b5 = T.MulAddLane1(b5, xb, c1);
        }

        Store6(a0, a1, a2, a3, a4, a5, ref o);
        Store6(b0, b1, b2, b3, b4, b5, ref Unsafe.Add(ref o, 24));
    }

    private static void Up6Single(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(8 * k));
            Vector128<float> c1 = Vector128.LoadUnsafe(ref c, (nuint)((8 * k) + 4));
            a0 = T.MulAddLane0(a0, xa, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
            a3 = T.MulAddLane3(a3, xa, c0);
            a4 = T.MulAddLane0(a4, xa, c1);
            a5 = T.MulAddLane1(a5, xa, c1);
        }

        Store6(a0, a1, a2, a3, a4, a5, ref o);
    }

    private static void Up4Pair(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default;
        Vector128<float> b0 = default, b1 = default, b2 = default, b3 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> xb = Vector128.LoadUnsafe(ref x, (nuint)(k + 4));
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(4 * k));
            a0 = T.MulAddLane0(a0, xa, c0);
            b0 = T.MulAddLane0(b0, xb, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            b1 = T.MulAddLane1(b1, xb, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
            b2 = T.MulAddLane2(b2, xb, c0);
            a3 = T.MulAddLane3(a3, xa, c0);
            b3 = T.MulAddLane3(b3, xb, c0);
        }

        Store4(a0, a1, a2, a3, ref o);
        Store4(b0, b1, b2, b3, ref Unsafe.Add(ref o, 16));
    }

    private static void Up4Single(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(4 * k));
            a0 = T.MulAddLane0(a0, xa, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
            a3 = T.MulAddLane3(a3, xa, c0);
        }

        Store4(a0, a1, a2, a3, ref o);
    }

    private static void Up3Pair(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default;
        Vector128<float> b0 = default, b1 = default, b2 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> xb = Vector128.LoadUnsafe(ref x, (nuint)(k + 4));
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(4 * k));
            a0 = T.MulAddLane0(a0, xa, c0);
            b0 = T.MulAddLane0(b0, xb, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            b1 = T.MulAddLane1(b1, xb, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
            b2 = T.MulAddLane2(b2, xb, c0);
        }

        Store3(a0, a1, a2, ref o);
        Store3(b0, b1, b2, ref Unsafe.Add(ref o, 12));
    }

    private static void Up3Single(ref float x, ref float c, int length, ref short o)
    {
        Vector128<float> a0 = default, a1 = default, a2 = default;
        for (int k = 0; k < length; k++)
        {
            Vector128<float> xa = Vector128.LoadUnsafe(ref x, (nuint)k);
            Vector128<float> c0 = Vector128.LoadUnsafe(ref c, (nuint)(4 * k));
            a0 = T.MulAddLane0(a0, xa, c0);
            a1 = T.MulAddLane1(a1, xa, c0);
            a2 = T.MulAddLane2(a2, xa, c0);
        }

        Store3(a0, a1, a2, ref o);
    }

    /// <summary>Four consecutive input samples times six phases: transposes to time-major, rounds, saturates, stores 24 shorts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store6(
        Vector128<float> p0, Vector128<float> p1, Vector128<float> p2, Vector128<float> p3, Vector128<float> p4, Vector128<float> p5, ref short o)
    {
        Vector128<float> z01l = T.ZipLow32(p0, p1);
        Vector128<float> z01h = T.ZipHigh32(p0, p1);
        Vector128<float> z23l = T.ZipLow32(p2, p3);
        Vector128<float> z23h = T.ZipHigh32(p2, p3);
        Vector128<float> z45l = T.ZipLow32(p4, p5);
        Vector128<float> z45h = T.ZipHigh32(p4, p5);

        // time j: [p0 p1 p2 p3] then [p4 p5].
        Vector128<int> t0 = T.RoundToInt(Vector128.Create(z01l.GetLower(), z23l.GetLower()));
        Vector128<int> t1 = T.RoundToInt(Vector128.Create(z01l.GetUpper(), z23l.GetUpper()));
        Vector128<int> t2 = T.RoundToInt(Vector128.Create(z01h.GetLower(), z23h.GetLower()));
        Vector128<int> t3 = T.RoundToInt(Vector128.Create(z01h.GetUpper(), z23h.GetUpper()));
        Vector128<int> q0 = T.RoundToInt(Vector128.Create(z45l.GetLower(), z45l.GetLower()));
        Vector128<int> q1 = T.RoundToInt(Vector128.Create(z45l.GetUpper(), z45l.GetUpper()));
        Vector128<int> q2 = T.RoundToInt(Vector128.Create(z45h.GetLower(), z45h.GetLower()));
        Vector128<int> q3 = T.RoundToInt(Vector128.Create(z45h.GetUpper(), z45h.GetUpper()));
        Put6(T.NarrowSat(t0, q0), ref o);
        Put6(T.NarrowSat(t1, q1), ref Unsafe.Add(ref o, 6));
        Put6(T.NarrowSat(t2, q2), ref Unsafe.Add(ref o, 12));
        Put6(T.NarrowSat(t3, q3), ref Unsafe.Add(ref o, 18));
    }

    /// <summary>Stores shorts 0..5 of a vector (a 64-bit and a 32-bit store).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put6(Vector128<short> v, ref short o)
    {
        v.GetLower().StoreUnsafe(ref o);
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref o, 4)), v.AsInt32().GetElement(2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(Vector128<float> p0, Vector128<float> p1, Vector128<float> p2, Vector128<float> p3, ref short o)
    {
        Vector128<float> z01l = T.ZipLow32(p0, p1);
        Vector128<float> z01h = T.ZipHigh32(p0, p1);
        Vector128<float> z23l = T.ZipLow32(p2, p3);
        Vector128<float> z23h = T.ZipHigh32(p2, p3);
        Vector128<int> t0 = T.RoundToInt(Vector128.Create(z01l.GetLower(), z23l.GetLower()));
        Vector128<int> t1 = T.RoundToInt(Vector128.Create(z01l.GetUpper(), z23l.GetUpper()));
        Vector128<int> t2 = T.RoundToInt(Vector128.Create(z01h.GetLower(), z23h.GetLower()));
        Vector128<int> t3 = T.RoundToInt(Vector128.Create(z01h.GetUpper(), z23h.GetUpper()));
        T.NarrowSat(t0, t1).StoreUnsafe(ref o);
        T.NarrowSat(t2, t3).StoreUnsafe(ref Unsafe.Add(ref o, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store3(Vector128<float> p0, Vector128<float> p1, Vector128<float> p2, ref short o)
    {
        Vector128<float> z01l = T.ZipLow32(p0, p1);
        Vector128<float> z01h = T.ZipHigh32(p0, p1);
        Vector128<float> z2l = T.ZipLow32(p2, p2);
        Vector128<float> z2h = T.ZipHigh32(p2, p2);
        Vector128<int> t0 = T.RoundToInt(Vector128.Create(z01l.GetLower(), z2l.GetLower()));
        Vector128<int> t1 = T.RoundToInt(Vector128.Create(z01l.GetUpper(), z2l.GetUpper()));
        Vector128<int> t2 = T.RoundToInt(Vector128.Create(z01h.GetLower(), z2h.GetLower()));
        Vector128<int> t3 = T.RoundToInt(Vector128.Create(z01h.GetUpper(), z2h.GetUpper()));
        Put3(T.NarrowSat(t0, t0), ref o);
        Put3(T.NarrowSat(t1, t1), ref Unsafe.Add(ref o, 3));
        Put3(T.NarrowSat(t2, t2), ref Unsafe.Add(ref o, 6));
        Put3(T.NarrowSat(t3, t3), ref Unsafe.Add(ref o, 9));
    }

    /// <summary>Stores shorts 0..2 of a vector (a 32-bit and a 16-bit store).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put3(Vector128<short> v, ref short o)
    {
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref o), v.AsInt32().GetElement(0));
        Unsafe.Add(ref o, 2) = v.GetElement(2);
    }
}
