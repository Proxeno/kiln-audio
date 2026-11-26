using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Simd;

/// <summary>
/// The CELT comb post-filter (RFC 6716 §4.3.7.1) with a scalar reference path and a
/// <see cref="Vector128{T}"/> path.
/// </summary>
/// <remarks>
/// <para>
/// The filter is recursive along the sample axis: it reads <c>history[position - period - k]</c>
/// and writes <c>history[position]</c> in place. It is therefore not safe to process consecutive
/// samples in one vector in general. But the read distance is at least the comb period, so a block
/// of <c>V</c> consecutive samples is independent of itself whenever <c>V &lt;= period - 2</c>:
/// every read in the block lands before the block's first sample and is already final. The vector
/// path uses that property to run four samples per iteration while the period allows it, and falls
/// back to the scalar loop otherwise. Each lane performs exactly the scalar operations in the same
/// order, so the two paths are bit-identical.
/// </para>
/// </remarks>
internal static class CeltPostFilterKernel
{
    private const int VectorWidth = 4;

    /// <summary>Applies one in-place comb-filter pass.</summary>
    /// <param name="history">The per-channel history buffer, including overlap before the frame.</param>
    /// <param name="frameStart">The index of the frame's first sample in <paramref name="history"/>.</param>
    /// <param name="count">The number of samples to filter.</param>
    /// <param name="period0">The first comb period.</param>
    /// <param name="period1">The second comb period.</param>
    /// <param name="gain0">The first period gain.</param>
    /// <param name="gain1">The second period gain.</param>
    /// <param name="tapSet0">The first tap set index.</param>
    /// <param name="tapSet1">The second tap set index.</param>
    /// <param name="windowSquare">The squared overlap window.</param>
    /// <param name="overlap">The overlap length.</param>
    internal static void CombFilter(
        float[] history,
        int frameStart,
        int count,
        int period0,
        int period1,
        float gain0,
        float gain1,
        int tapSet0,
        int tapSet1,
        ReadOnlySpan<float> windowSquare,
        int overlap) =>
        CombFilter(
            history, frameStart, count, period0, period1, gain0, gain1, tapSet0, tapSet1,
            windowSquare, overlap, SimdConfig.UseSimd);

    /// <summary>Applies one in-place comb-filter pass, forcing the scalar or vector path.</summary>
    /// <param name="history">The per-channel history buffer.</param>
    /// <param name="frameStart">The index of the frame's first sample.</param>
    /// <param name="count">The number of samples to filter.</param>
    /// <param name="period0">The first comb period.</param>
    /// <param name="period1">The second comb period.</param>
    /// <param name="gain0">The first period gain.</param>
    /// <param name="gain1">The second period gain.</param>
    /// <param name="tapSet0">The first tap set index.</param>
    /// <param name="tapSet1">The second tap set index.</param>
    /// <param name="windowSquare">The squared overlap window.</param>
    /// <param name="overlap">The overlap length.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    internal static void CombFilter(
        float[] history,
        int frameStart,
        int count,
        int period0,
        int period1,
        float gain0,
        float gain1,
        int tapSet0,
        int tapSet1,
        ReadOnlySpan<float> windowSquare,
        int overlap,
        bool useVector)
    {
        ReadOnlySpan<float> taps0 = CeltTables.PostFilterGains.Slice(tapSet0 * 3, 3);
        ReadOnlySpan<float> taps1 = CeltTables.PostFilterGains.Slice(tapSet1 * 3, 3);

        float g00 = gain0 * taps0[0];
        float g01 = gain0 * taps0[1];
        float g02 = gain0 * taps0[2];
        float g10 = gain1 * taps1[0];
        float g11 = gain1 * taps1[1];
        float g12 = gain1 * taps1[2];

        // A period is only meaningful when its gains are non-zero: the decoder passes the newly
        // decoded pitch through unchanged even when it is zero, so a zero gain set must also skip
        // the read (the reference does the same). This is load-bearing for the index bounds.
        bool use0 = g00 != 0f || g01 != 0f || g02 != 0f;
        bool use1 = g10 != 0f || g11 != 0f || g12 != 0f;

        int fadeEnd = Math.Min(overlap, count);
        int index = 0;

        bool vector = useVector
            && period0 >= VectorWidth + 2
            && period1 >= VectorWidth + 2;

        if (vector)
        {
            for (; index + VectorWidth <= fadeEnd; index += VectorWidth)
            {
                FadeBlock(
                    history, frameStart + index, index, period0, period1,
                    g00, g01, g02, g10, g11, g12, use0, use1, windowSquare);
            }
        }

        for (; index < fadeEnd; index++)
        {
            FadeSample(
                history, frameStart + index, index, period0, period1,
                g00, g01, g02, g10, g11, g12, use0, use1, windowSquare);
        }

        if (vector && use1)
        {
            for (; index + VectorWidth <= count; index += VectorWidth)
            {
                SteadyBlock(history, frameStart + index, period1, g10, g11, g12);
            }
        }

        for (; index < count; index++)
        {
            SteadySample(history, frameStart + index, period1, g10, g11, g12, use1);
        }
    }

    private static void FadeBlock(
        float[] history,
        int position,
        int fadeIndex,
        int period0,
        int period1,
        float g00,
        float g01,
        float g02,
        float g10,
        float g11,
        float g12,
        bool use0,
        bool use1,
        ReadOnlySpan<float> windowSquare)
    {
        Vector128<float> value = Load(history, position);

        Vector128<float> fade = Load(windowSquare, fadeIndex);
        Vector128<float> weight = Vector128.Create(1f) - fade;

        if (use0)
        {
            Vector128<float> sum0 =
                (Vector128.Create(g00) * Load(history, position - period0))
                + (Vector128.Create(g01) * (Load(history, position - period0 - 1) + Load(history, position - period0 + 1)))
                + (Vector128.Create(g02) * (Load(history, position - period0 - 2) + Load(history, position - period0 + 2)));
            value += weight * sum0;
        }

        if (use1)
        {
            Vector128<float> sum1 =
                (Vector128.Create(g10) * Load(history, position - period1))
                + (Vector128.Create(g11) * (Load(history, position - period1 - 1) + Load(history, position - period1 + 1)))
                + (Vector128.Create(g12) * (Load(history, position - period1 - 2) + Load(history, position - period1 + 2)));
            value += fade * sum1;
        }

        Store(history, position, value);
    }

    private static void FadeSample(
        float[] history,
        int position,
        int fadeIndex,
        int period0,
        int period1,
        float g00,
        float g01,
        float g02,
        float g10,
        float g11,
        float g12,
        bool use0,
        bool use1,
        ReadOnlySpan<float> windowSquare)
    {
        float fade = windowSquare[fadeIndex];
        float value = history[position];
        if (use0)
        {
            float weight = 1f - fade;
            value += weight * (
                (g00 * history[position - period0])
                + (g01 * (history[position - period0 - 1] + history[position - period0 + 1]))
                + (g02 * (history[position - period0 - 2] + history[position - period0 + 2])));
        }

        if (use1)
        {
            value += fade * (
                (g10 * history[position - period1])
                + (g11 * (history[position - period1 - 1] + history[position - period1 + 1]))
                + (g12 * (history[position - period1 - 2] + history[position - period1 + 2])));
        }

        history[position] = value;
    }

    private static void SteadyBlock(
        float[] history,
        int position,
        int period,
        float g10,
        float g11,
        float g12)
    {
        var g10v = Vector128.Create(g10);
        var g11v = Vector128.Create(g11);
        var g12v = Vector128.Create(g12);
        Vector128<float> value = Load(history, position);
        value +=
            (g10v * Load(history, position - period))
            + (g11v * (Load(history, position - period - 1) + Load(history, position - period + 1)))
            + (g12v * (Load(history, position - period - 2) + Load(history, position - period + 2)));
        Store(history, position, value);
    }

    private static void SteadySample(
        float[] history,
        int position,
        int period,
        float g10,
        float g11,
        float g12,
        bool use1)
    {
        if (!use1)
        {
            return;
        }

        history[position] +=
            (g10 * history[position - period])
            + (g11 * (history[position - period - 1] + history[position - period + 1]))
            + (g12 * (history[position - period - 2] + history[position - period + 2]));
    }

    private static Vector128<float> Load(float[] values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values.AsSpan()), (nuint)index);

    private static Vector128<float> Load(ReadOnlySpan<float> values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);

    private static void Store(float[] values, int index, Vector128<float> value) =>
        value.StoreUnsafe(ref MemoryMarshal.GetReference(values.AsSpan()), (nuint)index);
}
