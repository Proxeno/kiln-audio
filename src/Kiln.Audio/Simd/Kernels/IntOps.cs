using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The 16-bit integer multiply-accumulate primitives the SILK encoder kernels are written against.
/// Each vector tier supplies its native form; every result is exact integer arithmetic (the 32-bit
/// forms wrap modulo 2^32 exactly like the scalar reference), so all tiers are bit-identical.
/// </summary>
internal interface IIntOps
{
    /// <summary>Adds the 8 products <c>a[i]*b[i]</c> into the 4 lanes of <paramref name="acc"/> (pairs fold into one lane), wrapping.</summary>
    static abstract Vector128<int> MulAcc(Vector128<int> acc, Vector128<short> a, Vector128<short> b);

    /// <summary>Adds the 8 exact products <c>a[i]*b[i]</c> into the 2 lanes of <paramref name="acc"/>.</summary>
    static abstract Vector128<long> MulAcc64(Vector128<long> acc, Vector128<short> a, Vector128<short> b);

    /// <summary>Adds the lane-wise products of the low halves into <paramref name="lo"/> and of the high halves into <paramref name="hi"/>.</summary>
    static abstract void MulAccLanes(ref Vector128<int> lo, ref Vector128<int> hi, Vector128<short> a, Vector128<short> b);

    /// <summary>
    /// Prepares a 16-bit multiplier (given sign-extended in an <see cref="int"/>) for <see cref="Smlawb"/>; hoisted out of loops.
    /// </summary>
    static abstract Vector128<int> PrepCoef(int coefficient);

    /// <summary>The per-lane form of <see cref="PrepCoef"/>: each lane holds a sign-extended 16-bit multiplier.</summary>
    static abstract Vector128<int> PrepCoefV(Vector128<int> coefficients);

    /// <summary>
    /// The reference's <c>silk_SMLAWB</c> in every lane: <c>acc + ((b * (int16)c) &gt;&gt; 16)</c> with the exact 48-bit product,
    /// wrapping into 32 bits. <paramref name="prepared"/> comes from <see cref="PrepCoef"/>.
    /// </summary>
    static abstract Vector128<int> Smlawb(Vector128<int> acc, Vector128<int> b, Vector128<int> prepared);
}

/// <summary>Portable <see cref="Vector128{T}"/> integer operations (SSE2-only x64, WASM, any 128-bit host).</summary>
internal readonly struct PortableIntOps : IIntOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAcc(Vector128<int> acc, Vector128<short> a, Vector128<short> b)
    {
        (Vector128<int> alo, Vector128<int> ahi) = Vector128.Widen(a);
        (Vector128<int> blo, Vector128<int> bhi) = Vector128.Widen(b);
        return acc + (alo * blo) + (ahi * bhi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> MulAcc64(Vector128<long> acc, Vector128<short> a, Vector128<short> b)
    {
        (Vector128<int> alo, Vector128<int> ahi) = Vector128.Widen(a);
        (Vector128<int> blo, Vector128<int> bhi) = Vector128.Widen(b);
        Vector128<int> plo = alo * blo;
        Vector128<int> phi = ahi * bhi;
        return acc + Vector128.WidenLower(plo) + Vector128.WidenUpper(plo) + Vector128.WidenLower(phi) + Vector128.WidenUpper(phi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MulAccLanes(ref Vector128<int> lo, ref Vector128<int> hi, Vector128<short> a, Vector128<short> b)
    {
        (Vector128<int> alo, Vector128<int> ahi) = Vector128.Widen(a);
        (Vector128<int> blo, Vector128<int> bhi) = Vector128.Widen(b);
        lo += alo * blo;
        hi += ahi * bhi;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoef(int coefficient) => Vector128.Create(coefficient);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoefV(Vector128<int> coefficients) => coefficients;

    /// <summary>High half exact: <c>(b &gt;&gt; 16) * c + (((b &amp; 0xFFFF) * c) &gt;&gt; 16)</c> equals <c>(b * c) &gt;&gt; 16</c> for a 16-bit <c>c</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smlawb(Vector128<int> acc, Vector128<int> b, Vector128<int> prepared) =>
        acc + (Vector128.ShiftRightArithmetic(b, 16) * prepared)
            + Vector128.ShiftRightArithmetic((b & Vector128.Create(0xFFFF)) * prepared, 16);
}

/// <summary>arm64: <c>AdvSimd</c> widening multiply-accumulate directly.</summary>
internal readonly struct ArmIntOps : IIntOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAcc(Vector128<int> acc, Vector128<short> a, Vector128<short> b)
    {
        acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, a.GetLower(), b.GetLower());
        return AdvSimd.MultiplyWideningUpperAndAdd(acc, a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> MulAcc64(Vector128<long> acc, Vector128<short> a, Vector128<short> b)
    {
        acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower()));
        return AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningUpper(a, b));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MulAccLanes(ref Vector128<int> lo, ref Vector128<int> hi, Vector128<short> a, Vector128<short> b)
    {
        lo = AdvSimd.MultiplyWideningLowerAndAdd(lo, a.GetLower(), b.GetLower());
        hi = AdvSimd.MultiplyWideningUpperAndAdd(hi, a, b);
    }

    /// <summary><c>sqdmulh</c> with the multiplier pre-shifted left by 15 is exactly <c>(b * c) &gt;&gt; 16</c> (no saturation: |c &lt;&lt; 15| &lt; 2^30).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoef(int coefficient) => Vector128.Create(coefficient << 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoefV(Vector128<int> coefficients) => Vector128.ShiftLeft(coefficients, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smlawb(Vector128<int> acc, Vector128<int> b, Vector128<int> prepared) =>
        acc + AdvSimd.MultiplyDoublingSaturateHigh(b, prepared);
}

/// <summary>x64 (AVX2+FMA tier, 128-bit VEX forms): <c>pmaddwd</c> for the 32-bit dot, portable widening for the exact 64-bit sum.</summary>
internal readonly struct X86IntOps : IIntOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MulAcc(Vector128<int> acc, Vector128<short> a, Vector128<short> b) =>
        acc + Sse2.MultiplyAddAdjacent(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> MulAcc64(Vector128<long> acc, Vector128<short> a, Vector128<short> b) =>
        PortableIntOps.MulAcc64(acc, a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MulAccLanes(ref Vector128<int> lo, ref Vector128<int> hi, Vector128<short> a, Vector128<short> b)
    {
        // pmullw/pmulhw give the exact 32-bit products of the low and high halves via an unpack.
        Vector128<short> low = Sse2.MultiplyLow(a, b);
        Vector128<short> high = Sse2.MultiplyHigh(a, b);
        lo += Sse2.UnpackLow(low, high).AsInt32();
        hi += Sse2.UnpackHigh(low, high).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoef(int coefficient) => PortableIntOps.PrepCoef(coefficient);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> PrepCoefV(Vector128<int> coefficients) => coefficients;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Smlawb(Vector128<int> acc, Vector128<int> b, Vector128<int> prepared) =>
        PortableIntOps.Smlawb(acc, b, prepared);
}
