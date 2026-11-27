using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>
/// The 128-bit primitives the shared vector kernels are written against: fused or unfused
/// multiply-add, and the lane shuffles (reverse, zip, unzip, shift). Each vector tier supplies
/// the instruction that is native to it, so one kernel body serves all three vector tiers
/// without any runtime dispatch (the struct type argument is resolved at JIT time).
/// </summary>
internal interface IVecOps : IScalarFma
{
    /// <summary>Computes <c>acc + (a * b)</c>.</summary>
    static abstract Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b);

    /// <summary>Computes <c>acc - (a * b)</c>.</summary>
    static abstract Vector128<float> MulSub(Vector128<float> acc, Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[v3, v2, v1, v0]</c>.</summary>
    static abstract Vector128<float> Reverse(Vector128<float> v);

    /// <summary>Returns <c>[a0, a2, b0, b2]</c>.</summary>
    static abstract Vector128<float> UnzipEven(Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[a1, a3, b1, b3]</c>.</summary>
    static abstract Vector128<float> UnzipOdd(Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[a0, b0, a1, b1]</c>.</summary>
    static abstract Vector128<float> ZipLow(Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[a2, b2, a3, b3]</c>.</summary>
    static abstract Vector128<float> ZipHigh(Vector128<float> a, Vector128<float> b);

    /// <summary>Returns <c>[0, v0, v1, v2]</c>.</summary>
    static abstract Vector128<float> ShiftUp1(Vector128<float> v);

    /// <summary>Returns <c>[0, 0, v0, v1]</c>.</summary>
    static abstract Vector128<float> ShiftUp2(Vector128<float> v);

    /// <summary>Returns <c>[v3, v3, v3, v3]</c>.</summary>
    static abstract Vector128<float> Broadcast3(Vector128<float> v);
}

/// <summary>Portable <see cref="Vector128{T}"/> operations with separately rounded multiply-add.</summary>
internal readonly struct PortableOps : IVecOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) => acc + (a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulSub(Vector128<float> acc, Vector128<float> a, Vector128<float> b) => acc - (a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MulAdd(float acc, float a, float b) => acc + (a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Reverse(Vector128<float> v) => Vector128.Shuffle(v, Vector128.Create(3, 2, 1, 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipEven(Vector128<float> a, Vector128<float> b) =>
        Vector128.Create(
            Vector128.Shuffle(a, Vector128.Create(0, 2, 0, 2)).GetLower(),
            Vector128.Shuffle(b, Vector128.Create(0, 2, 0, 2)).GetLower());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipOdd(Vector128<float> a, Vector128<float> b) =>
        Vector128.Create(
            Vector128.Shuffle(a, Vector128.Create(1, 3, 1, 3)).GetLower(),
            Vector128.Shuffle(b, Vector128.Create(1, 3, 1, 3)).GetLower());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(
            Vector128.Create(0, -1, 0, -1).AsSingle(),
            Vector128.Shuffle(b, Vector128.Create(0, 0, 1, 1)),
            Vector128.Shuffle(a, Vector128.Create(0, 0, 1, 1)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(
            Vector128.Create(0, -1, 0, -1).AsSingle(),
            Vector128.Shuffle(b, Vector128.Create(2, 2, 3, 3)),
            Vector128.Shuffle(a, Vector128.Create(2, 2, 3, 3)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp1(Vector128<float> v) =>
        Vector128.Shuffle(v, Vector128.Create(0, 0, 1, 2)) & Vector128.Create(0, -1, -1, -1).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp2(Vector128<float> v) =>
        Vector128.Shuffle(v, Vector128.Create(0, 0, 0, 1)) & Vector128.Create(0, 0, -1, -1).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Broadcast3(Vector128<float> v) => Vector128.Shuffle(v, Vector128.Create(3, 3, 3, 3));
}

/// <summary>arm64: <c>AdvSimd</c> and <c>AdvSimd.Arm64</c> instructions directly, fused multiply-add.</summary>
internal readonly struct ArmOps : IVecOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        AdvSimd.FusedMultiplyAdd(acc, a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulSub(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        AdvSimd.FusedMultiplySubtract(acc, a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MulAdd(float acc, float a, float b) => MathF.FusedMultiplyAdd(a, b, acc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Reverse(Vector128<float> v)
    {
        Vector128<float> r = AdvSimd.ReverseElement32(v.AsUInt64()).AsSingle();
        return AdvSimd.ExtractVector128(r, r, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipEven(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.UnzipEven(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipOdd(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.UnzipOdd(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh(Vector128<float> a, Vector128<float> b) => AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp1(Vector128<float> v) => AdvSimd.ExtractVector128(Vector128<float>.Zero, v, 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp2(Vector128<float> v) => AdvSimd.ExtractVector128(Vector128<float>.Zero, v, 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Broadcast3(Vector128<float> v) => AdvSimd.DuplicateSelectedScalarToVector128(v, 3);
}

/// <summary>x64 with FMA3: 128-bit VEX encodings of the SSE shuffles and fused multiply-add.</summary>
internal readonly struct X86Ops : IVecOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulAdd(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        Fma.MultiplyAdd(a, b, acc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MulSub(Vector128<float> acc, Vector128<float> a, Vector128<float> b) =>
        Fma.MultiplyAddNegated(a, b, acc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MulAdd(float acc, float a, float b) => MathF.FusedMultiplyAdd(a, b, acc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Reverse(Vector128<float> v) => Sse.Shuffle(v, v, 0x1B);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipEven(Vector128<float> a, Vector128<float> b) => Sse.Shuffle(a, b, 0x88);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnzipOdd(Vector128<float> a, Vector128<float> b) => Sse.Shuffle(a, b, 0xDD);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipLow(Vector128<float> a, Vector128<float> b) => Sse.UnpackLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ZipHigh(Vector128<float> a, Vector128<float> b) => Sse.UnpackHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp1(Vector128<float> v) =>
        Sse2.ShiftLeftLogical128BitLane(v.AsInt32(), 4).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ShiftUp2(Vector128<float> v) =>
        Sse2.ShiftLeftLogical128BitLane(v.AsInt32(), 8).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Broadcast3(Vector128<float> v) => Sse.Shuffle(v, v, 0xFF);
}
