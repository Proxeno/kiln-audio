using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The scalar reference bodies of the SILK encoder kernels: the exact loops the encoder used before the kernel sets,
/// kept as the bit-exactness oracle and as the <see cref="ScalarKernels"/> tier.
/// </summary>
internal static class SilkScalar
{
    internal static int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum = unchecked(sum + (a[i] * b[i]));
        }

        return sum;
    }

    internal static long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        long sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += (long)a[i] * b[i];
        }

        return sum;
    }

    internal static void CorrLags(
        ReadOnlySpan<short> target, ReadOnlySpan<short> basis, int basisStart, int lagCount, int length, Span<int> output)
    {
        for (int i = 0; i < lagCount; i++)
        {
            output[i] = InnerProd(target, basis[(basisStart - i)..], length);
        }
    }

    internal static void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int prediction = 0;
            for (int j = 0; j < order; j++)
            {
                prediction = unchecked(prediction + (input[ix - 1 - j] * coefficients[j]));
            }

            int residualQ12 = unchecked((input[ix] << 12) - prediction);
            int rounded = ((residualQ12 >> 11) + 1) >> 1;
            output[ix] = (short)(rounded > short.MaxValue ? short.MaxValue : (rounded < short.MinValue ? short.MinValue : rounded));
        }

        output[..order].Clear();
    }

    internal static void BurgRows(
        Span<int> firstRow, Span<int> lastRow, ReadOnlySpan<int> afQa, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed,
        int n, int x1, int x2, ref int tmp1, ref int tmp2)
    {
        int length = x.Length;
        for (int k = 0; k < n; k++)
        {
            firstRow[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(firstRow[k], x1, x[n - k - 1]);
            lastRow[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(lastRow[k], x2, x[length - n + k]);
            tmp1 = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(tmp1, afQa[k], x[n - k - 1]);
            tmp2 = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(tmp2, afQa[k], x[length - n + k]);
        }
    }

    internal static void BurgCorr(
        Span<int> caf, Span<int> cab, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed, int n, int tmp1, int tmp2)
    {
        int length = x.Length;
        for (int k = 0; k <= n; k++)
        {
            caf[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(caf[k], tmp1, x[n - k]);
            cab[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(cab[k], tmp2, x[length - n + k - 1]);
        }
    }
}

/// <summary>
/// The SILK encoder's 16-bit integer kernels, one body for every vector tier (the tier supplies the multiply-accumulate
/// through <typeparamref name="T"/>). All arithmetic is exact integer arithmetic with the reference's wrap-around, so
/// every tier is bit-identical to <see cref="SilkScalar"/>.
/// </summary>
internal static class SilkVec<T>
    where T : struct, IIntOps
{
    internal static int InnerProd(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        if ((uint)length > (uint)a.Length || (uint)length > (uint)b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ref short ar = ref MemoryMarshal.GetReference(a);
        ref short br = ref MemoryMarshal.GetReference(b);
        int i = 0;
        Vector128<int> acc0 = Vector128<int>.Zero;
        Vector128<int> acc1 = Vector128<int>.Zero;
        for (; i + 16 <= length; i += 16)
        {
            acc0 = T.MulAcc(acc0, Vector128.LoadUnsafe(ref ar, (nuint)i), Vector128.LoadUnsafe(ref br, (nuint)i));
            acc1 = T.MulAcc(acc1, Vector128.LoadUnsafe(ref ar, (nuint)(i + 8)), Vector128.LoadUnsafe(ref br, (nuint)(i + 8)));
        }

        if (i + 8 <= length)
        {
            acc0 = T.MulAcc(acc0, Vector128.LoadUnsafe(ref ar, (nuint)i), Vector128.LoadUnsafe(ref br, (nuint)i));
            i += 8;
        }

        int sum = Vector128.Sum(acc0 + acc1);
        for (; i < length; i++)
        {
            sum = unchecked(sum + (Unsafe.Add(ref ar, i) * Unsafe.Add(ref br, i)));
        }

        return sum;
    }

    internal static long InnerProd64(ReadOnlySpan<short> a, ReadOnlySpan<short> b, int length)
    {
        if ((uint)length > (uint)a.Length || (uint)length > (uint)b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ref short ar = ref MemoryMarshal.GetReference(a);
        ref short br = ref MemoryMarshal.GetReference(b);
        int i = 0;
        Vector128<long> acc0 = Vector128<long>.Zero;
        Vector128<long> acc1 = Vector128<long>.Zero;
        for (; i + 16 <= length; i += 16)
        {
            acc0 = T.MulAcc64(acc0, Vector128.LoadUnsafe(ref ar, (nuint)i), Vector128.LoadUnsafe(ref br, (nuint)i));
            acc1 = T.MulAcc64(acc1, Vector128.LoadUnsafe(ref ar, (nuint)(i + 8)), Vector128.LoadUnsafe(ref br, (nuint)(i + 8)));
        }

        if (i + 8 <= length)
        {
            acc0 = T.MulAcc64(acc0, Vector128.LoadUnsafe(ref ar, (nuint)i), Vector128.LoadUnsafe(ref br, (nuint)i));
            i += 8;
        }

        long sum = Vector128.Sum(acc0 + acc1);
        for (; i < length; i++)
        {
            sum += (long)Unsafe.Add(ref ar, i) * Unsafe.Add(ref br, i);
        }

        return sum;
    }

    /// <summary>
    /// <c>output[i] = sum over n &lt; length of target[n] * basis[basisStart - i + n]</c> for <c>i &lt; lagCount</c>, four lags
    /// per pass so each target vector is loaded once for four accumulators.
    /// </summary>
    internal static void CorrLags(
        ReadOnlySpan<short> target, ReadOnlySpan<short> basis, int basisStart, int lagCount, int length, Span<int> output)
    {
        if (lagCount <= 0)
        {
            return;
        }

        if ((uint)length > (uint)target.Length
            || basisStart - (lagCount - 1) < 0
            || (long)basisStart + length > basis.Length
            || output.Length < lagCount)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ref short tr = ref MemoryMarshal.GetReference(target);
        ref short br = ref MemoryMarshal.GetReference(basis);
        int i = 0;
        for (; i + 4 <= lagCount; i += 4)
        {
            ref short b0 = ref Unsafe.Add(ref br, basisStart - i);
            Vector128<int> a0 = Vector128<int>.Zero;
            Vector128<int> a1 = Vector128<int>.Zero;
            Vector128<int> a2 = Vector128<int>.Zero;
            Vector128<int> a3 = Vector128<int>.Zero;
            int n = 0;
            for (; n + 8 <= length; n += 8)
            {
                Vector128<short> t = Vector128.LoadUnsafe(ref tr, (nuint)n);
                a0 = T.MulAcc(a0, t, Vector128.LoadUnsafe(ref b0, (nuint)n));
                a1 = T.MulAcc(a1, t, Vector128.LoadUnsafe(ref Unsafe.Subtract(ref b0, 1), (nuint)n));
                a2 = T.MulAcc(a2, t, Vector128.LoadUnsafe(ref Unsafe.Subtract(ref b0, 2), (nuint)n));
                a3 = T.MulAcc(a3, t, Vector128.LoadUnsafe(ref Unsafe.Subtract(ref b0, 3), (nuint)n));
            }

            int s0 = Vector128.Sum(a0);
            int s1 = Vector128.Sum(a1);
            int s2 = Vector128.Sum(a2);
            int s3 = Vector128.Sum(a3);
            for (; n < length; n++)
            {
                int tv = Unsafe.Add(ref tr, n);
                s0 = unchecked(s0 + (tv * Unsafe.Add(ref b0, n)));
                s1 = unchecked(s1 + (tv * Unsafe.Add(ref b0, n - 1)));
                s2 = unchecked(s2 + (tv * Unsafe.Add(ref b0, n - 2)));
                s3 = unchecked(s3 + (tv * Unsafe.Add(ref b0, n - 3)));
            }

            output[i] = s0;
            output[i + 1] = s1;
            output[i + 2] = s2;
            output[i + 3] = s3;
        }

        for (; i < lagCount; i++)
        {
            output[i] = InnerProd(target, basis[(basisStart - i)..], length);
        }
    }

    /// <summary>
    /// The LPC analysis (whitening) filter in transposed form: eight output samples per pass, one widening
    /// multiply-accumulate pair per tap. Q12 coefficients; the first <paramref name="order"/> outputs are zero.
    /// </summary>
    internal static void LpcAnalysisFilter(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficients, int length, int order)
    {
        if (order > 32 || order <= 0 || length > input.Length || length > output.Length || coefficients.Length < order)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        Span<Vector128<short>> taps = stackalloc Vector128<short>[32];
        for (int j = 0; j < order; j++)
        {
            taps[j] = Vector128.Create(coefficients[j]);
        }

        ref short inr = ref MemoryMarshal.GetReference(input);
        ref short outr = ref MemoryMarshal.GetReference(output);
        Vector128<int> min = Vector128.Create((int)short.MinValue);
        Vector128<int> max = Vector128.Create((int)short.MaxValue);
        int ix = order;
        for (; ix + 8 <= length; ix += 8)
        {
            Vector128<int> lo = Vector128<int>.Zero;
            Vector128<int> hi = Vector128<int>.Zero;
            for (int j = 0; j < order; j++)
            {
                T.MulAccLanes(ref lo, ref hi, Vector128.LoadUnsafe(ref inr, (nuint)(ix - 1 - j)), taps[j]);
            }

            (Vector128<int> xlo, Vector128<int> xhi) = Vector128.Widen(Vector128.LoadUnsafe(ref inr, (nuint)ix));
            Vector128<int> rlo = Vector128.ShiftLeft(xlo, 12) - lo;
            Vector128<int> rhi = Vector128.ShiftLeft(xhi, 12) - hi;
            rlo = Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(rlo, 11) + Vector128<int>.One, 1);
            rhi = Vector128.ShiftRightArithmetic(Vector128.ShiftRightArithmetic(rhi, 11) + Vector128<int>.One, 1);
            rlo = Vector128.Min(Vector128.Max(rlo, min), max);
            rhi = Vector128.Min(Vector128.Max(rhi, min), max);
            Vector128.Narrow(rlo, rhi).StoreUnsafe(ref outr, (nuint)ix);
        }

        for (; ix < length; ix++)
        {
            int prediction = 0;
            for (int j = 0; j < order; j++)
            {
                prediction = unchecked(prediction + (Unsafe.Add(ref inr, ix - 1 - j) * coefficients[j]));
            }

            int residualQ12 = unchecked((Unsafe.Add(ref inr, ix) << 12) - prediction);
            int rounded = ((residualQ12 >> 11) + 1) >> 1;
            Unsafe.Add(ref outr, ix) = (short)(rounded > short.MaxValue ? short.MaxValue : (rounded < short.MinValue ? short.MinValue : rounded));
        }

        output[..order].Clear();
    }

    private static Vector128<int> Load4(ReadOnlySpan<short> source, int index) =>
        Vector128.WidenLower(Vector64.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)index).ToVector128Unsafe());

    private static Vector128<int> LoadInts(Span<int> source, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)index);

    private static Vector128<int> LoadInts(ReadOnlySpan<int> source, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)index);

    /// <summary>Burg's first-half update; see <see cref="ISilkEncoderKernels.BurgRows"/>. Four taps per pass.</summary>
    internal static void BurgRows(
        Span<int> firstRow, Span<int> lastRow, ReadOnlySpan<int> afQa, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed,
        int n, int x1, int x2, ref int tmp1, ref int tmp2)
    {
        int length = x.Length;
        if (n > firstRow.Length || n > lastRow.Length || n > afQa.Length || length < n || xReversed.Length < length)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        int k = 0;
        if (n >= 4)
        {
            Vector128<int> b1 = Vector128.Create(x1);
            Vector128<int> b2 = Vector128.Create(x2);
            Vector128<int> t1 = Vector128<int>.Zero;
            Vector128<int> t2 = Vector128<int>.Zero;
            for (; k + 4 <= n; k += 4)
            {
                // x[n - k - 1 - j] is xReversed[length - n + k + j]: both operands run upward.
                Vector128<int> cr = T.PrepCoefV(Load4(xReversed, length - n + k));
                Vector128<int> cf = T.PrepCoefV(Load4(x, length - n + k));
                Vector128<int> af = LoadInts(afQa, k);
                T.Smlawb(LoadInts(firstRow, k), b1, cr).StoreUnsafe(ref MemoryMarshal.GetReference(firstRow), (nuint)k);
                T.Smlawb(LoadInts(lastRow, k), b2, cf).StoreUnsafe(ref MemoryMarshal.GetReference(lastRow), (nuint)k);
                t1 = T.Smlawb(t1, af, cr);
                t2 = T.Smlawb(t2, af, cf);
            }

            tmp1 = unchecked(tmp1 + Vector128.Sum(t1));
            tmp2 = unchecked(tmp2 + Vector128.Sum(t2));
        }

        for (; k < n; k++)
        {
            firstRow[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(firstRow[k], x1, x[n - k - 1]);
            lastRow[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(lastRow[k], x2, x[length - n + k]);
            tmp1 = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(tmp1, afQa[k], x[n - k - 1]);
            tmp2 = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(tmp2, afQa[k], x[length - n + k]);
        }
    }

    /// <summary>Burg's second-half update; see <see cref="ISilkEncoderKernels.BurgCorr"/>. Four taps per pass.</summary>
    internal static void BurgCorr(
        Span<int> caf, Span<int> cab, ReadOnlySpan<short> x, ReadOnlySpan<short> xReversed, int n, int tmp1, int tmp2)
    {
        int length = x.Length;
        if (n + 1 > caf.Length || n + 1 > cab.Length || length < n + 1 || xReversed.Length < length)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        int k = 0;
        Vector128<int> b1 = Vector128.Create(tmp1);
        Vector128<int> b2 = Vector128.Create(tmp2);
        for (; k + 4 <= n + 1; k += 4)
        {
            // x[n - k - j] is xReversed[length - 1 - n + k + j].
            Vector128<int> cr = T.PrepCoefV(Load4(xReversed, length - 1 - n + k));
            Vector128<int> cf = T.PrepCoefV(Load4(x, length - n + k - 1));
            T.Smlawb(LoadInts(caf, k), b1, cr).StoreUnsafe(ref MemoryMarshal.GetReference(caf), (nuint)k);
            T.Smlawb(LoadInts(cab, k), b2, cf).StoreUnsafe(ref MemoryMarshal.GetReference(cab), (nuint)k);
        }

        for (; k <= n; k++)
        {
            caf[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(caf[k], tmp1, x[n - k]);
            cab[k] = Kiln.Audio.Silk.SilkFixedPoint.Smlawb(cab[k], tmp2, x[length - n + k - 1]);
        }
    }
}
