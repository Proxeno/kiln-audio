using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>Portable <see cref="Vector128{T}"/> SILK primitives (SSE2-only x64, WASM, any 128-bit host).</summary>
internal readonly struct PortableSilkOps : ISilkOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromSext(Vector128<int> sext) => sext;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromRaw(Vector128<int> raw) =>
        Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(raw, 16), 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smulwb(Vector128<int> a, Vector128<int> prepared)
    {
        // a * c >> 16 with a = high * 65536 + low: high * c + ((low * c) >> 16); both products fit in 32 bits.
        Vector128<int> high = Vector128.ShiftRightArithmetic(a, 16);
        Vector128<int> low = Vector128.ShiftRightLogical(Vector128.ShiftLeft(a, 16), 16);
        return (high * prepared) + Vector128.ShiftRightArithmetic(low * prepared, 16);
    }

    public static Vector128<int> Smulww(Vector128<int> a, Vector128<int> b) =>
        Vector128.Create(
            SilkFixedPoint.Smulww(a.GetElement(0), b.GetElement(0)),
            SilkFixedPoint.Smulww(a.GetElement(1), b.GetElement(1)),
            SilkFixedPoint.Smulww(a.GetElement(2), b.GetElement(2)),
            SilkFixedPoint.Smulww(a.GetElement(3), b.GetElement(3)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideLow(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        acc + (Vector128.WidenLower(a) * Vector128.WidenLower(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideHigh(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        acc + (Vector128.WidenUpper(a) * Vector128.WidenUpper(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> NarrowSat(Vector128<int> low, Vector128<int> high)
    {
        Vector128<int> max = Vector128.Create(32767);
        Vector128<int> min = Vector128.Create(-32768);
        return Vector128.Narrow(
            Vector128.Max(Vector128.Min(low, max), min),
            Vector128.Max(Vector128.Min(high, max), min));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> AddSat16(Vector128<short> a, Vector128<short> b) =>
        NarrowSat(
            Vector128.WidenLower(a) + Vector128.WidenLower(b),
            Vector128.WidenUpper(a) + Vector128.WidenUpper(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> SubSat16(Vector128<short> a, Vector128<short> b) =>
        NarrowSat(
            Vector128.WidenLower(a) - Vector128.WidenLower(b),
            Vector128.WidenUpper(a) - Vector128.WidenUpper(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipEven16(Vector128<short> a, Vector128<short> b) =>
        Vector128.Narrow(
            Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(a.AsInt32(), 16), 16),
            Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(b.AsInt32(), 16), 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipOdd16(Vector128<short> a, Vector128<short> b) =>
        Vector128.Narrow(
            Vector128.ShiftRightArithmetic(a.AsInt32(), 16),
            Vector128.ShiftRightArithmetic(b.AsInt32(), 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipLow16(Vector128<short> a, Vector128<short> b) =>
        Vector128.ConditionalSelect(
            Vector128.Create((short)0, -1, 0, -1, 0, -1, 0, -1),
            Vector128.Shuffle(b, Vector128.Create((short)0, 0, 1, 1, 2, 2, 3, 3)),
            Vector128.Shuffle(a, Vector128.Create((short)0, 0, 1, 1, 2, 2, 3, 3)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipHigh16(Vector128<short> a, Vector128<short> b) =>
        Vector128.ConditionalSelect(
            Vector128.Create((short)0, -1, 0, -1, 0, -1, 0, -1),
            Vector128.Shuffle(b, Vector128.Create((short)4, 4, 5, 5, 6, 6, 7, 7)),
            Vector128.Shuffle(a, Vector128.Create((short)4, 4, 5, 5, 6, 6, 7, 7)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> HalvingAdd16(Vector128<short> a, Vector128<short> b) =>
        Vector128.Narrow(
            Vector128.ShiftRightArithmetic(Vector128.WidenLower(a) + Vector128.WidenLower(b), 1),
            Vector128.ShiftRightArithmetic(Vector128.WidenUpper(a) + Vector128.WidenUpper(b), 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow32(Vector128<float> a, Vector128<float> b) => PortableOps.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh32(Vector128<float> a, Vector128<float> b) => PortableOps.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) => acc + (a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> PairwiseAdd32(Vector128<float> a, Vector128<float> b) =>
        PortableOps.UnzipEven(a, b) + PortableOps.UnzipOdd(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> RoundToInt(Vector128<float> value) =>
        Vector128.ConvertToInt32(Vector128.Round(value, MidpointRounding.ToEven));
}

/// <summary>arm64: <c>AdvSimd</c> and <c>AdvSimd.Arm64</c> instructions directly.</summary>
internal readonly struct ArmSilkOps : ISilkOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromSext(Vector128<int> sext) => Vector128.ShiftLeft(sext, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromRaw(Vector128<int> raw) =>
        Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(raw, 16), 1);

    /// <summary>
    /// <c>SQDMULH</c> returns <c>(2 * a * b) &gt;&gt; 32</c>; with <c>b = c &lt;&lt; 15</c> that is exactly
    /// <c>(a * c) &gt;&gt; 16</c> (floor), and it can only saturate for <c>a = b = INT_MIN</c>, which a
    /// 16-bit <c>c</c> shifted by 15 cannot reach.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smulwb(Vector128<int> a, Vector128<int> prepared) =>
        AdvSimd.MultiplyDoublingSaturateHigh(a, prepared);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smulww(Vector128<int> a, Vector128<int> b)
    {
        Vector128<long> low = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
        Vector128<long> high = AdvSimd.MultiplyWideningUpper(a, b);
        return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(low, 16), high, 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideLow(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        AdvSimd.MultiplyWideningLowerAndAdd(acc, a.GetLower(), b.GetLower());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideHigh(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        AdvSimd.MultiplyWideningUpperAndAdd(acc, a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> NarrowSat(Vector128<int> low, Vector128<int> high) =>
        AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(low), high);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> AddSat16(Vector128<short> a, Vector128<short> b) => AdvSimd.AddSaturate(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> SubSat16(Vector128<short> a, Vector128<short> b) => AdvSimd.SubtractSaturate(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipEven16(Vector128<short> a, Vector128<short> b) => AdvSimd.Arm64.UnzipEven(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipOdd16(Vector128<short> a, Vector128<short> b) => AdvSimd.Arm64.UnzipOdd(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipLow16(Vector128<short> a, Vector128<short> b) => AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipHigh16(Vector128<short> a, Vector128<short> b) => AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> HalvingAdd16(Vector128<short> a, Vector128<short> b) => AdvSimd.FusedAddHalving(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow32(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh32(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        AdvSimd.FusedMultiplyAdd(acc, a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> PairwiseAdd32(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.AddPairwise(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> RoundToInt(Vector128<float> value) => AdvSimd.ConvertToInt32RoundToEven(value);
}

/// <summary>x64 with SSE4.1 and FMA3 (the AVX2+FMA tier): 128-bit VEX encodings.</summary>
internal readonly struct X86SilkOps : ISilkOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromSext(Vector128<int> sext) => sext;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> FromRaw(Vector128<int> raw) =>
        Sse2.ShiftRightArithmetic(Sse2.ShiftLeftLogical(raw, 16), 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smulwb(Vector128<int> a, Vector128<int> prepared)
    {
        Vector128<int> high = Sse2.ShiftRightArithmetic(a, 16);
        Vector128<int> low = Sse2.ShiftRightLogical(Sse2.ShiftLeftLogical(a, 16), 16);
        return Sse2.Add(Sse41.MultiplyLow(high, prepared), Sse2.ShiftRightArithmetic(Sse41.MultiplyLow(low, prepared), 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smulww(Vector128<int> a, Vector128<int> b)
    {
        // pmuldq multiplies lanes 0 and 2; bits 16..47 of each 64-bit product are the result.
        Vector128<long> even = Sse41.Multiply(a, b);
        Vector128<long> odd = Sse41.Multiply(Sse2.Shuffle(a, 0xF5), Sse2.Shuffle(b, 0xF5));
        Vector128<int> e = Sse2.ShiftRightLogical(even.AsUInt64(), 16).AsInt32();
        Vector128<int> o = Sse2.ShiftLeftLogical(Sse2.ShiftRightLogical(odd.AsUInt64(), 16), 32).AsInt32();
        return Sse41.Blend(e.AsInt16(), o.AsInt16(), 0xCC).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideLow(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        Sse2.Add(acc, Sse41.MultiplyLow(Sse41.ConvertToVector128Int32(a), Sse41.ConvertToVector128Int32(b)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAddWideHigh(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        Sse2.Add(
            acc,
            Sse41.MultiplyLow(
                Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(a, 8)),
                Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(b, 8))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> NarrowSat(Vector128<int> low, Vector128<int> high) => Sse2.PackSignedSaturate(low, high);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> AddSat16(Vector128<short> a, Vector128<short> b) => Sse2.AddSaturate(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> SubSat16(Vector128<short> a, Vector128<short> b) => Sse2.SubtractSaturate(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipEven16(Vector128<short> a, Vector128<short> b) =>
        Sse2.PackSignedSaturate(
            Sse2.ShiftRightArithmetic(Sse2.ShiftLeftLogical(a.AsInt32(), 16), 16),
            Sse2.ShiftRightArithmetic(Sse2.ShiftLeftLogical(b.AsInt32(), 16), 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> UnzipOdd16(Vector128<short> a, Vector128<short> b) =>
        Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(a.AsInt32(), 16), Sse2.ShiftRightArithmetic(b.AsInt32(), 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipLow16(Vector128<short> a, Vector128<short> b) => Sse2.UnpackLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ZipHigh16(Vector128<short> a, Vector128<short> b) => Sse2.UnpackHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> HalvingAdd16(Vector128<short> a, Vector128<short> b) =>
        Sse2.PackSignedSaturate(
            Sse2.ShiftRightArithmetic(Sse2.Add(Sse41.ConvertToVector128Int32(a), Sse41.ConvertToVector128Int32(b)), 1),
            Sse2.ShiftRightArithmetic(
                Sse2.Add(
                    Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(a, 8)),
                    Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(b, 8))),
                1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow32(Vector128<float> a, Vector128<float> b) => Sse.UnpackLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh32(Vector128<float> a, Vector128<float> b) => Sse.UnpackHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        Fma.MultiplyAdd(a, b, acc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> PairwiseAdd32(Vector128<float> a, Vector128<float> b) => Sse3.HorizontalAdd(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> RoundToInt(Vector128<float> value) => Sse2.ConvertToVector128Int32(value);
}
