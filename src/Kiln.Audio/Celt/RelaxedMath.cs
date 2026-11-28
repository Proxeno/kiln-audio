using System.Runtime.CompilerServices;

namespace Kiln.Audio.Celt;

/// <summary>
/// Single-precision approximations the Relaxed decoder profile uses in place of the double-precision
/// library calls. Each is accurate to a few units in the last place of a float, far inside the
/// RFC 8251 comparison tolerance, and none allocates.
/// </summary>
internal static class RelaxedMath
{
    /// <summary>
    /// Returns <c>2^x</c> for <c>x</c> up to 32, with a relative error below 3e-7: the exponent is
    /// split off as an integer, and a degree-5 minimax polynomial covers the fraction in [-0.5, 0.5].
    /// </summary>
    /// <param name="x">The base-2 logarithm of the result.</param>
    /// <returns>The approximate power of two.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Exp2(float x)
    {
        x = MathF.Max(x, -120f);
        float rounded = MathF.Round(x);
        float f = x - rounded;
        float p = 0.0013395285f;
        p = MathF.FusedMultiplyAdd(p, f, 0.009670768f);
        p = MathF.FusedMultiplyAdd(p, f, 0.05550341f);
        p = MathF.FusedMultiplyAdd(p, f, 0.24022212f);
        p = MathF.FusedMultiplyAdd(p, f, 0.6931472f);
        p = MathF.FusedMultiplyAdd(p, f, 1.0000001f);
        int exponent = (int)rounded;
        return p * BitConverter.Int32BitsToSingle((exponent + 127) << 23);
    }
}
