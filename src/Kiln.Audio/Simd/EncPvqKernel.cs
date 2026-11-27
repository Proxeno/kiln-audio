// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd;

/// <summary>
/// The CELT encoder's PVQ greedy search (RFC 6716 §4.3.4.2; celt/vq.c: <c>op_pvq_search_c</c>),
/// with a <see cref="Vector128{T}"/> inner loop.
/// </summary>
/// <remarks>
/// <para>
/// The scalar reference lives in <c>CeltEncoderKernels.PvqSearchScalar</c>. The vector path
/// computes the four-lane candidate sums with <see cref="Vector128{T}"/> and then walks the lanes
/// in the same index order as the scalar loop, so the selected pulse and the returned norm are
/// bit-identical to the reference.
/// </para>
/// <para>
/// Every floating-point operation is the same single-rounded operation the scalar loop performs
/// (an add and one multiply), so the four-lane path cannot change the pulse vector.
/// </para>
/// </remarks>
internal static class EncPvqKernel
{
    private const float VeryLarge = 1e15f;

    /// <summary>Vector greedy PVQ search; see <c>CeltEncoderKernels.PvqSearch</c> for the contract.</summary>
    internal static float Search(Span<float> x, Span<int> iy, Span<float> signs, Span<float> y, int n, int k)
    {
        ref float xr = ref MemoryMarshal.GetReference(x);
        ref int iyr = ref MemoryMarshal.GetReference(iy);
        ref float signsr = ref MemoryMarshal.GetReference(signs);
        ref float yr = ref MemoryMarshal.GetReference(y);

        for (int j = 0; j < n; j++)
        {
            float value = Unsafe.Add(ref xr, j);
            if (value > 0f)
            {
                Unsafe.Add(ref signsr, j) = 1f;
            }
            else
            {
                Unsafe.Add(ref signsr, j) = -1f;
                Unsafe.Add(ref xr, j) = -value;
            }

            Unsafe.Add(ref iyr, j) = 0;
            Unsafe.Add(ref yr, j) = 0f;
        }

        float xy = 0f;
        float yy = 0f;
        int pulsesLeft = k;

        if (k > (n >> 1))
        {
            float sum = 0f;
            for (int j = 0; j < n; j++)
            {
                sum += Unsafe.Add(ref xr, j);
            }

            if (!(sum > 1e-15f && sum < 64f))
            {
                Unsafe.Add(ref xr, 0) = 1f;
                for (int j = 1; j < n; j++)
                {
                    Unsafe.Add(ref xr, j) = 0f;
                }

                sum = 1f;
            }

            float reciprocal = (k - 1) * (1f / sum);
            for (int j = 0; j < n; j++)
            {
                int pulse = (int)MathF.Floor(reciprocal * Unsafe.Add(ref xr, j));
                Unsafe.Add(ref iyr, j) = pulse;
                Unsafe.Add(ref yr, j) = pulse;
                float yValue = pulse;
                yy += yValue * yValue;
                xy += Unsafe.Add(ref xr, j) * yValue;
                Unsafe.Add(ref yr, j) = yValue * 2f;
                pulsesLeft -= pulse;
            }
        }

        if (pulsesLeft > n + 3)
        {
            float tmp = pulsesLeft;
            yy += tmp * tmp;
            yy += tmp * Unsafe.Add(ref yr, 0);
            Unsafe.Add(ref iyr, 0) += pulsesLeft;
            pulsesLeft = 0;
        }

        if (Vector128.IsHardwareAccelerated && n >= 4)
        {
            SearchVector(ref xr, ref iyr, ref yr, n, pulsesLeft, ref xy, ref yy);
        }
        else
        {
            SearchScalar(ref xr, ref iyr, ref yr, n, pulsesLeft, ref xy, ref yy);
        }

        for (int j = 0; j < n; j++)
        {
            float xValue = Unsafe.Add(ref xr, j);
            float sign = Unsafe.Add(ref signsr, j);
            Unsafe.Add(ref xr, j) = xValue * sign;
            if (sign < 0f)
            {
                Unsafe.Add(ref iyr, j) = -Unsafe.Add(ref iyr, j);
            }
        }

        return yy;
    }

    private static void SearchVector(
        ref float xr, ref int iyr, ref float yr, int n, int pulsesLeft, ref float xy, ref float yy)
    {
        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            float bestNum = -VeryLarge;
            float bestDen = 0f;
            yy += 1f;

            Vector128<float> xyv = Vector128.Create(xy);
            Vector128<float> yyv = Vector128.Create(yy);

            // ENC-CELT-1: a group of lanes can only move the running best if at least one lane beats the
            // current best, so every group is first tested with one vector compare against the *current*
            // best (the same two multiplies as the scalar test). Groups with no winner are skipped; a group
            // with a winner is walked lane by lane in index order, so the result is exactly the scalar
            // search's (ties keep the earliest index, the running cross-multiply sees the same values).
            Vector128<float> bestNumV = Vector128.Create(bestNum);
            Vector128<float> bestDenV = Vector128.Create(bestDen);
            int j = 0;
            for (; j + 4 <= n; j += 4)
            {
                Vector128<float> vx = Vector128.LoadUnsafe(ref xr, (nuint)j);
                Vector128<float> vy = Vector128.LoadUnsafe(ref yr, (nuint)j);
                Vector128<float> rxy = xyv + vx;
                rxy *= rxy;
                Vector128<float> ryy = yyv + vy;

                Vector128<float> win = Vector128.GreaterThan(bestDenV * rxy, ryy * bestNumV);
                if (win == Vector128<float>.Zero)
                {
                    continue;
                }

                for (int lane = 0; lane < 4; lane++)
                {
                    float num = rxy.GetElement(lane);
                    float den = ryy.GetElement(lane);
                    if (bestDen * num > den * bestNum)
                    {
                        bestDen = den;
                        bestNum = num;
                        bestId = j + lane;
                    }
                }

                bestNumV = Vector128.Create(bestNum);
                bestDenV = Vector128.Create(bestDen);
            }

            for (; j < n; j++)
            {
                float num = xy + Unsafe.Add(ref xr, j);
                num *= num;
                float den = yy + Unsafe.Add(ref yr, j);
                if (bestDen * num > den * bestNum)
                {
                    bestDen = den;
                    bestNum = num;
                    bestId = j;
                }
            }

            xy += Unsafe.Add(ref xr, bestId);
            yy += Unsafe.Add(ref yr, bestId);
            Unsafe.Add(ref yr, bestId) += 2f;
            Unsafe.Add(ref iyr, bestId)++;
        }
    }

    private static void SearchScalar(
        ref float xr, ref int iyr, ref float yr, int n, int pulsesLeft, ref float xy, ref float yy)
    {
        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            float bestNum = -VeryLarge;
            float bestDen = 0f;
            yy += 1f;
            for (int j = 0; j < n; j++)
            {
                float num = xy + Unsafe.Add(ref xr, j);
                num *= num;
                float den = yy + Unsafe.Add(ref yr, j);
                if (bestDen * num > den * bestNum)
                {
                    bestDen = den;
                    bestNum = num;
                    bestId = j;
                }
            }

            xy += Unsafe.Add(ref xr, bestId);
            yy += Unsafe.Add(ref yr, bestId);
            Unsafe.Add(ref yr, bestId) += 2f;
            Unsafe.Add(ref iyr, bestId)++;
        }
    }
}