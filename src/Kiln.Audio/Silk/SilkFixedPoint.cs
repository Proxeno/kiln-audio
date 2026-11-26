// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using System.Numerics;

namespace Kiln.Audio.Silk;

/// <summary>
/// The fixed-point primitives of the SILK reference decoder (RFC 6716 §4.2.7).
/// </summary>
/// <remarks>
/// These mirror the macros in the Appendix A source (<c>silk/macros.h</c> and
/// <c>silk/SigProc_FIX.h</c>) one for one, including their deliberate wrap-around. The
/// reconstruction stages below are only bit-exact with libopus if every intermediate keeps the
/// same width and rounding, so these helpers are written with explicit <c>long</c> intermediates
/// and <c>unchecked</c> arithmetic rather than being folded into a more idiomatic form.
/// </remarks>
internal static class SilkFixedPoint
{
    /// <summary>Largest <see cref="ushort"/>-sized signed value.</summary>
    internal const int Int16Max = 0x7FFF;

    /// <summary>Smallest 16-bit signed value.</summary>
    internal const int Int16Min = -0x8000;

    /// <summary>Largest 32-bit signed value.</summary>
    internal const int Int32Max = 0x7FFFFFFF;

    /// <summary>Smallest 32-bit signed value.</summary>
    internal const int Int32Min = unchecked((int)0x80000000);

    /// <summary>Returns <c>a * b</c> with 32-bit wrap-around.</summary>
    internal static int Mul(int a, int b) => unchecked(a * b);

    /// <summary>Returns <c>a + b</c> with 32-bit wrap-around.</summary>
    internal static int Add(int a, int b) => unchecked(a + b);

    /// <summary>Returns <c>a - b</c> with 32-bit wrap-around.</summary>
    internal static int Sub(int a, int b) => unchecked(a - b);

    /// <summary>Returns <c>(int16)a * (int16)b</c>.</summary>
    internal static int Smulbb(int a, int b) => unchecked((short)a * (short)b);

    /// <summary>Returns <c>(a * (int16)b) &gt;&gt; 16</c>.</summary>
    internal static int Smulwb(int a, int b) => (int)(((long)a * (short)b) >> 16);

    /// <summary>Returns <c>a + ((b * (int16)c) &gt;&gt; 16)</c>.</summary>
    internal static int Smlawb(int a, int b, int c) => unchecked(a + Smulwb(b, c));

    /// <summary>Returns <c>(a * b) &gt;&gt; 16</c> for the full 32-bit operands.</summary>
    internal static int Smulww(int a, int b) => (int)(((long)a * b) >> 16);

    /// <summary>Returns <c>a + ((b * c) &gt;&gt; 16)</c>.</summary>
    internal static int Smlaww(int a, int b, int c) => unchecked(a + Smulww(b, c));

    /// <summary>Returns the high 32 bits of the signed 64-bit product.</summary>
    internal static int Smmul(int a, int b) => (int)(((long)a * b) >> 32);

    /// <summary>Returns the signed 64-bit product.</summary>
    internal static long Smull(int a, int b) => (long)a * b;

    /// <summary>Returns <c>a</c> rounded right by <paramref name="shift"/> bits.</summary>
    internal static int RshiftRound(int a, int shift) =>
        shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    /// <summary>Returns <c>a</c> rounded right by <paramref name="shift"/> bits (64-bit).</summary>
    internal static long RshiftRound64(long a, int shift) =>
        shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    /// <summary>Saturates to a 16-bit signed value.</summary>
    internal static int Sat16(int a) => a > Int16Max ? Int16Max : a < Int16Min ? Int16Min : a;

    /// <summary>Returns <c>(int16)(a + b)</c> with saturation (RFC 8251 §7).</summary>
    internal static int AddSat16(int a, int b) => Sat16(unchecked(a + b));

    /// <summary>Returns <c>a - b</c> saturated to 32 bits (RFC 8251 §6).</summary>
    internal static int SubSat32(int a, int b)
    {
        long result = (long)a - b;
        return result > Int32Max ? Int32Max : result < Int32Min ? Int32Min : (int)result;
    }

    /// <summary>Returns <c>a &lt;&lt; shift</c> with 32-bit wrap-around.</summary>
    internal static int Lshift(int a, int shift) => unchecked((int)((uint)a << shift));

    /// <summary>Returns <c>a &gt;&gt; shift</c> (arithmetic).</summary>
    internal static int Rshift(int a, int shift) => a >> shift;

    /// <summary>Returns <c>a &lt;&lt; shift</c> after saturating to the shifted 32-bit range.</summary>
    internal static int LshiftSat32(int a, int shift) =>
        Lshift(Limit(a, Int32Min >> shift, Int32Max >> shift), shift);

    /// <summary>Clamps <paramref name="a"/> to the range spanned by the two limits.</summary>
    internal static int Limit(int a, int limit1, int limit2) =>
        limit1 > limit2
            ? (a > limit1 ? limit1 : (a < limit2 ? limit2 : a))
            : (a > limit2 ? limit2 : (a < limit1 ? limit1 : a));

    /// <summary>Returns the smaller of two values.</summary>
    internal static int MinInt(int a, int b) => a < b ? a : b;

    /// <summary>Returns the larger of two values.</summary>
    internal static int MaxInt(int a, int b) => a > b ? a : b;

    /// <summary>Returns the absolute value, matching the reference <c>silk_abs</c>.</summary>
    internal static int Abs(int a) => a > 0 ? a : -a;

    /// <summary>Returns the sign of <paramref name="a"/>: -1, 0 or 1.</summary>
    internal static int Sign(int a) => a > 0 ? 1 : (a < 0 ? -1 : 0);

    /// <summary>Returns the number of leading zero bits of a 32-bit value (32 for zero).</summary>
    internal static int Clz32(int a) => BitOperations.LeadingZeroCount((uint)a);

    /// <summary>Rotates a 32-bit value right; a negative rotation rotates left.</summary>
    internal static int Ror32(int a, int rot)
    {
        if (rot == 0)
        {
            return a;
        }

        uint x = (uint)a;
        if (rot < 0)
        {
            int m = -rot;
            return (int)((x << m) | (x >> (32 - m)));
        }

        return (int)((x << (32 - rot)) | (x >> rot));
    }

    /// <summary>The SILK pseudo-random generator (RFC 6716 §4.2.7.8.6, <c>silk_RAND</c>).</summary>
    internal static int Rand(int seed) => unchecked(907633515 + (seed * 196314165));

    /// <summary>Approximates <c>sqrt(x)</c>; see <c>silk_SQRT_APPROX</c>.</summary>
    internal static int SqrtApprox(int x)
    {
        if (x <= 0)
        {
            return 0;
        }

        int lz = Clz32(x);
        int fracQ7 = Ror32(x, 24 - lz) & 0x7F;
        int y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= Rshift(lz, 1);
        y = Smlawb(y, y, Smulbb(213, fracQ7));
        return y;
    }

    /// <summary>Approximates <c>(a &lt;&lt; Qres) / b</c>; see <c>silk_DIV32_varQ</c>.</summary>
    internal static int Div32VarQ(int a32, int b32, int qres)
    {
        int aHeadrm = Clz32(Abs(a32)) - 1;
        int a32Nrm = Lshift(a32, aHeadrm);
        int bHeadrm = Clz32(Abs(b32)) - 1;
        int b32Nrm = Lshift(b32, bHeadrm);

        int b32Inv = unchecked((Int32Max >> 2) / (b32Nrm >> 16));
        int result = Smulwb(a32Nrm, b32Inv);
        a32Nrm = unchecked(a32Nrm - Lshift(Smmul(b32Nrm, result), 3));
        result = Smlawb(result, a32Nrm, b32Inv);

        int lshift = 29 + aHeadrm - bHeadrm - qres;
        if (lshift < 0)
        {
            return LshiftSat32(result, -lshift);
        }

        return lshift < 32 ? result >> lshift : 0;
    }

    /// <summary>Approximates <c>(1 &lt;&lt; Qres) / b</c>; see <c>silk_INVERSE32_varQ</c>.</summary>
    internal static int Inverse32VarQ(int b32, int qres)
    {
        int bHeadrm = Clz32(Abs(b32)) - 1;
        int b32Nrm = Lshift(b32, bHeadrm);

        int b32Inv = unchecked((Int32Max >> 2) / (b32Nrm >> 16));
        int result = Lshift(b32Inv, 16);
        int errQ32 = Lshift(unchecked((1 << 29) - Smulwb(b32Nrm, b32Inv)), 3);
        result = Smlaww(result, errQ32, b32Inv);

        int lshift = 61 - bHeadrm - qres;
        if (lshift <= 0)
        {
            return LshiftSat32(result, -lshift);
        }

        return lshift < 32 ? result >> lshift : 0;
    }
}
