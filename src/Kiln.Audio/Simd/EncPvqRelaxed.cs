using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.Audio.Simd;

/// <summary>
/// Relaxed-mode PVQ search (<see cref="OpusPerformanceMode.Relaxed"/>): a projection start followed by a
/// largest-remainder apportionment of the leftover pulses, instead of the pulse-at-a-time greedy search.
/// </summary>
/// <remarks>
/// Every pulse vector it returns has exactly <c>k</c> pulses, so the bitstream is standard. It is a
/// plain O(n) scalar routine (a handful of branch-light passes over a small band), so every kernel
/// tier shares it and it is tier-independent by construction. It may pick different pulse positions
/// than the greedy search where the greedy search's one-at-a-time cost adjustment matters.
/// </remarks>
internal static class EncPvqRelaxed
{
    private const int Buckets = 256;

    /// <summary>Searches for the pulse vector; same contract as <c>CeltEncoderKernels.PvqSearch</c>.</summary>
    internal static float Search(Span<float> x, Span<int> iy, Span<float> signs, Span<float> y, int n, int k)
    {
        ref float xr = ref MemoryMarshal.GetReference(x);
        ref int iyr = ref MemoryMarshal.GetReference(iy);
        ref float signsr = ref MemoryMarshal.GetReference(signs);
        ref float yr = ref MemoryMarshal.GetReference(y);

        float sum = 0f;
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
                value = -value;
                Unsafe.Add(ref xr, j) = value;
            }

            sum += value;
        }

        if (!(sum > 1e-15f && sum < 64f))
        {
            // Degenerate target: all pulses on the first component, as the greedy search does.
            for (int j = 0; j < n; j++)
            {
                Unsafe.Add(ref yr, j) = j == 0 ? 1f : 0f;
            }

            sum = 1f;
            float saved0 = Unsafe.Add(ref xr, 0);
            Unsafe.Add(ref xr, 0) = 1f;
            float result = Apportion(ref xr, ref iyr, ref yr, n, k, sum);
            Unsafe.Add(ref xr, 0) = saved0;
            return Finish(ref xr, ref iyr, ref signsr, n, result);
        }

        return Finish(ref xr, ref iyr, ref signsr, n, Apportion(ref xr, ref iyr, ref yr, n, k, sum));
    }

    private static float Apportion(ref float xr, ref int iyr, ref float fr, int n, int k, float sum)
    {
        // Slightly under-shoot so the floors never exceed k even with rounding in the multiply.
        float scale = (k * (1f - 4e-6f)) / sum;
        Span<int> histogram = stackalloc int[Buckets + 1];
        int placed = 0;
        for (int j = 0; j < n; j++)
        {
            float scaled = scale * Unsafe.Add(ref xr, j);
            int pulse = (int)scaled;
            Unsafe.Add(ref iyr, j) = pulse;
            float fraction = scaled - pulse;
            Unsafe.Add(ref fr, j) = fraction;
            histogram[(int)(fraction * Buckets)]++;
            placed += pulse;
        }

        int leftover = k - placed;
        if (leftover > 0)
        {
            // Find the bucket that holds the leftover-th largest fraction.
            int taken = 0;
            int threshold = Buckets;
            while (taken < leftover)
            {
                threshold--;
                taken += histogram[threshold];
            }

            int ties = leftover - (taken - histogram[threshold]);
            for (int j = 0; j < n; j++)
            {
                int bucket = (int)(Unsafe.Add(ref fr, j) * Buckets);
                if (bucket > threshold)
                {
                    Unsafe.Add(ref iyr, j)++;
                }
                else if (bucket == threshold && ties > 0)
                {
                    Unsafe.Add(ref iyr, j)++;
                    ties--;
                }
            }
        }

        float yy = 0f;
        for (int j = 0; j < n; j++)
        {
            float p = Unsafe.Add(ref iyr, j);
            yy += p * p;
        }

        return yy;
    }

    private static float Finish(ref float xr, ref int iyr, ref float signsr, int n, float yy)
    {
        for (int j = 0; j < n; j++)
        {
            float sign = Unsafe.Add(ref signsr, j);
            Unsafe.Add(ref xr, j) *= sign;
            if (sign < 0f)
            {
                Unsafe.Add(ref iyr, j) = -Unsafe.Add(ref iyr, j);
            }
        }

        return yy;
    }
}
