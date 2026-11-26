// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using System.Runtime.Intrinsics;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Silk;

/// <summary>
/// The per-sample quantizer decision shared by every delayed-decision state
/// (RFC 6716 §5.2.3.8, Appendix A <c>silk_noise_shape_quantizer_del_dec</c>).
/// </summary>
/// <remarks>
/// <para>
/// For each state the reference dithers and clamps the residual, derives the two quantizer
/// levels that straddle it, and scores both with the rate/distortion cost
/// <c>lambda*|q| + (r-q)^2</c> in Q10. All of that is independent per state, so it is the part of
/// the inner state loop that is vectorized; the prediction and noise-shaping recurrences stay
/// scalar because each state's history feeds its own next sample.
/// </para>
/// <para>
/// The integer arithmetic is exact: products use the reference's 16-bit operand truncation,
/// shifts are arithmetic, and the vector path produces bit-identical results to the scalar path.
/// </para>
/// </remarks>
internal static class SilkNsqDelayedDecisionKernel
{
    /// <summary>Number of states in one <see cref="Vector128{T}"/> of 32-bit lanes.</summary>
    internal const int VectorLanes = 4;

    /// <summary>The reference's quantization-level adjustment, Q10 (Appendix A <c>QUANT_LEVEL_ADJUST_Q10</c>).</summary>
    internal const int QuantLevelAdjustQ10 = 80;

    /// <summary>The reference's residual clamp, lower bound (Appendix A <c>-(31 &lt;&lt; 10)</c>).</summary>
    internal const int ResidualMinQ10 = -(31 << 10);

    /// <summary>The reference's residual clamp, upper bound (Appendix A <c>30 &lt;&lt; 10</c>).</summary>
    internal const int ResidualMaxQ10 = 30 << 10;

    /// <summary>Evaluates the quantizer candidates with the portable vector path when enabled.</summary>
    /// <param name="residualQ10">The residual before dither sign flip, one entry per state.</param>
    /// <param name="seeds">The dither seeds, one entry per state.</param>
    /// <param name="count">The number of live states, 1 to 4.</param>
    /// <param name="offsetQ10">The signal-type quantization offset, Q10.</param>
    /// <param name="lambdaQ10">The rate/distortion tradeoff, Q10.</param>
    /// <param name="q1Q10">Receives the first candidate level, Q10.</param>
    /// <param name="q2Q10">Receives the second candidate level, Q10.</param>
    /// <param name="rd1Q10">Receives the first candidate cost, Q10.</param>
    /// <param name="rd2Q10">Receives the second candidate cost, Q10.</param>
    internal static void Evaluate(
        ReadOnlySpan<int> residualQ10,
        ReadOnlySpan<int> seeds,
        int count,
        int offsetQ10,
        int lambdaQ10,
        Span<int> q1Q10,
        Span<int> q2Q10,
        Span<int> rd1Q10,
        Span<int> rd2Q10)
    {
        if (SimdConfig.UseSimd && Vector128.IsHardwareAccelerated)
        {
            EvaluateSimd(residualQ10, seeds, count, offsetQ10, lambdaQ10, q1Q10, q2Q10, rd1Q10, rd2Q10);
        }
        else
        {
            EvaluateScalar(residualQ10, seeds, count, offsetQ10, lambdaQ10, q1Q10, q2Q10, rd1Q10, rd2Q10);
        }
    }

    /// <summary>The scalar reference for <see cref="Evaluate"/>.</summary>
    internal static void EvaluateScalar(
        ReadOnlySpan<int> residualQ10,
        ReadOnlySpan<int> seeds,
        int count,
        int offsetQ10,
        int lambdaQ10,
        Span<int> q1Q10,
        Span<int> q2Q10,
        Span<int> rd1Q10,
        Span<int> rd2Q10)
    {
        int lambda = (short)lambdaQ10;
        for (int k = 0; k < count; k++)
        {
            int rQ10 = residualQ10[k];
            if (seeds[k] < 0)
            {
                rQ10 = -rQ10;
            }

            rQ10 = rQ10 > ResidualMaxQ10 ? ResidualMaxQ10 : (rQ10 < ResidualMinQ10 ? ResidualMinQ10 : rQ10);

            int q1;
            int q2;
            int q1Q0 = (rQ10 - offsetQ10) >> 10;
            if (q1Q0 > 0)
            {
                q1 = ((q1Q0 << 10) - QuantLevelAdjustQ10) + offsetQ10;
                q2 = q1 + 1024;
                rd1Q10[k] = (short)q1 * lambda;
                rd2Q10[k] = (short)q2 * lambda;
            }
            else if (q1Q0 == 0)
            {
                q1 = offsetQ10;
                q2 = q1 + (1024 - QuantLevelAdjustQ10);
                rd1Q10[k] = (short)q1 * lambda;
                rd2Q10[k] = (short)q2 * lambda;
            }
            else if (q1Q0 == -1)
            {
                q2 = offsetQ10;
                q1 = q2 - (1024 - QuantLevelAdjustQ10);
                rd1Q10[k] = (short)-q1 * lambda;
                rd2Q10[k] = (short)q2 * lambda;
            }
            else
            {
                q1 = ((q1Q0 << 10) + QuantLevelAdjustQ10) + offsetQ10;
                q2 = q1 + 1024;
                rd1Q10[k] = (short)-q1 * lambda;
                rd2Q10[k] = (short)-q2 * lambda;
            }

            int rr = rQ10 - q1;
            rd1Q10[k] = (rd1Q10[k] + ((short)rr * (short)rr)) >> 10;
            rr = rQ10 - q2;
            rd2Q10[k] = (rd2Q10[k] + ((short)rr * (short)rr)) >> 10;
            q1Q10[k] = q1;
            q2Q10[k] = q2;
        }
    }

    /// <summary>The portable <see cref="Vector128{T}"/> path; bit-identical to the scalar one.</summary>
    internal static void EvaluateSimd(
        ReadOnlySpan<int> residualQ10,
        ReadOnlySpan<int> seeds,
        int count,
        int offsetQ10,
        int lambdaQ10,
        Span<int> q1Q10,
        Span<int> q2Q10,
        Span<int> rd1Q10,
        Span<int> rd2Q10)
    {
        Span<int> r = stackalloc int[VectorLanes];
        Span<int> s = stackalloc int[VectorLanes];
        residualQ10[..count].CopyTo(r);
        seeds[..count].CopyTo(s);
        for (int k = count; k < VectorLanes; k++)
        {
            r[k] = r[count - 1];
            s[k] = s[count - 1];
        }

        Vector128<int> rv = Vector128.Create(r);
        Vector128<int> seedNeg = Vector128.LessThan(Vector128.Create(s), Vector128<int>.Zero);
        rv = Vector128.ConditionalSelect(seedNeg, -rv, rv);
        rv = Vector128.Min(Vector128.Max(rv, Vector128.Create(ResidualMinQ10)), Vector128.Create(ResidualMaxQ10));

        Vector128<int> offset = Vector128.Create(offsetQ10);
        Vector128<int> lambda = Vector128.Create((int)(short)lambdaQ10);
        Vector128<int> q1Q0 = Vector128.ShiftRightArithmetic(rv - offset, 10);

        Vector128<int> gt0 = Vector128.GreaterThan(q1Q0, Vector128<int>.Zero);
        Vector128<int> eq0 = Vector128.Equals(q1Q0, Vector128<int>.Zero);
        Vector128<int> eqm1 = Vector128.Equals(q1Q0, Vector128.Create(-1));
        Vector128<int> lt = Vector128.LessThan(q1Q0, Vector128.Create(-1));

        Vector128<int> q1a = Vector128.ShiftLeft(q1Q0, 10) - Vector128.Create(QuantLevelAdjustQ10) + offset;
        Vector128<int> q1b = offset;
        Vector128<int> q1c = offset - Vector128.Create(1024 - QuantLevelAdjustQ10);
        Vector128<int> q1d = Vector128.ShiftLeft(q1Q0, 10) + Vector128.Create(QuantLevelAdjustQ10) + offset;
        Vector128<int> q1 = Blend4(gt0, q1a, eq0, q1b, eqm1, q1c, lt, q1d);

        Vector128<int> q2a = q1a + Vector128.Create(1024);
        Vector128<int> q2b = q1b + Vector128.Create(1024 - QuantLevelAdjustQ10);
        Vector128<int> q2c = offset;
        Vector128<int> q2d = q1d + Vector128.Create(1024);
        Vector128<int> q2 = Blend4(gt0, q2a, eq0, q2b, eqm1, q2c, lt, q2d);

        // The reference multiplies the magnitude of the (16-bit truncated) level by lambda.
        Vector128<int> a1 = Narrow16(q1);
        a1 = Vector128.ConditionalSelect(Vector128.LessThan(a1, Vector128<int>.Zero), -a1, a1);
        Vector128<int> rd1 = Vector128.Multiply(a1, lambda);
        Vector128<int> a2 = Narrow16(q2);
        a2 = Vector128.ConditionalSelect(Vector128.LessThan(a2, Vector128<int>.Zero), -a2, a2);
        Vector128<int> rd2 = Vector128.Multiply(a2, lambda);

        Vector128<int> rr1 = Narrow16(rv - q1);
        rd1 = Vector128.ShiftRightArithmetic(rd1 + Vector128.Multiply(rr1, rr1), 10);
        Vector128<int> rr2 = Narrow16(rv - q2);
        rd2 = Vector128.ShiftRightArithmetic(rd2 + Vector128.Multiply(rr2, rr2), 10);

        q1.CopyTo(q1Q10);
        q2.CopyTo(q2Q10);
        rd1.CopyTo(rd1Q10);
        rd2.CopyTo(rd2Q10);
    }

    private static Vector128<int> Narrow16(Vector128<int> value) =>
        Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(value, 16), 16);

    private static Vector128<int> Blend4(
        Vector128<int> m0, Vector128<int> v0,
        Vector128<int> m1, Vector128<int> v1,
        Vector128<int> m2, Vector128<int> v2,
        Vector128<int> m3, Vector128<int> v3) =>
        (m0 & v0) | (m1 & v1) | (m2 & v2) | (m3 & v3);
}
