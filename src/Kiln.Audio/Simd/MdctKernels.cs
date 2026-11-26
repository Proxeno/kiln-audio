using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd;

/// <summary>
/// The window-multiply passes at the end of the inverse MDCT, with a scalar reference path and a
/// portable <see cref="Vector128{T}"/> path.
/// </summary>
/// <remarks>
/// The IMDCT output has two regions. The first <c>count</c> samples combine the direct DCT-IV
/// result with the reversed one; the last <c>overlap</c> samples combine the reversed direct
/// result with the direct one and negate. Both regions are elementwise once the reversed copies
/// have been produced by the unpack stage, which is why only this pass is vectorised.
/// </remarks>
internal static class MdctKernels
{
    /// <summary>
    /// Writes the first region: <c>output[n] = window[n] * scale * (direct[n] - reversed[n])</c>.
    /// </summary>
    /// <param name="output">Destination, at least <paramref name="count"/> samples.</param>
    /// <param name="direct">The direct DCT-IV result.</param>
    /// <param name="reversed">The second DCT-IV result in reverse order.</param>
    /// <param name="window">The window.</param>
    /// <param name="scale">The <c>2/N</c> inverse factor.</param>
    /// <param name="count">The number of samples to write.</param>
    public static void WindowFirst(
        Span<float> output,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> window,
        float scale,
        int count) =>
        WindowFirst(output, direct, reversed, window, scale, count, SimdConfig.UseSimd);

    /// <summary>Writes the first region, forcing the vector or scalar path.</summary>
    public static void WindowFirst(
        Span<float> output,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> window,
        float scale,
        int count,
        bool useVector)
    {
        if (useVector)
        {
            var scaleVector = Vector128.Create(scale);
            int n = 0;
            for (; n + 4 <= count; n += 4)
            {
                Vector128<float> weightedWindow = Load(window, n) * scaleVector;
                Store(output, n, weightedWindow * (Load(direct, n) - Load(reversed, n)));
            }

            WindowFirstScalar(output, direct, reversed, window, scale, count, n);
            return;
        }

        WindowFirstScalar(output, direct, reversed, window, scale, count, 0);
    }

    private static void WindowFirstScalar(
        Span<float> output,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> window,
        float scale,
        int count,
        int from)
    {
        for (int n = from; n < count; n++)
        {
            output[n] = window[n] * scale * (direct[n] - reversed[n]);
        }
    }

    /// <summary>
    /// Writes the second region: <c>output[m] = window[m] * scale * -(reversed[m] + direct[m])</c>.
    /// </summary>
    /// <param name="output">Destination, at least <paramref name="count"/> samples.</param>
    /// <param name="reversed">The first DCT-IV result in reverse order.</param>
    /// <param name="direct">The second DCT-IV result.</param>
    /// <param name="window">The window slice covering this region.</param>
    /// <param name="scale">The <c>2/N</c> inverse factor.</param>
    /// <param name="count">The number of samples to write.</param>
    public static void WindowSecond(
        Span<float> output,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> window,
        float scale,
        int count) =>
        WindowSecond(output, reversed, direct, window, scale, count, SimdConfig.UseSimd);

    /// <summary>Writes the second region, forcing the vector or scalar path.</summary>
    public static void WindowSecond(
        Span<float> output,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> window,
        float scale,
        int count,
        bool useVector)
    {
        if (useVector)
        {
            var scaleVector = Vector128.Create(-scale);
            int n = 0;
            for (; n + 4 <= count; n += 4)
            {
                Vector128<float> weightedWindow = Load(window, n) * scaleVector;
                Store(output, n, weightedWindow * (Load(reversed, n) + Load(direct, n)));
            }

            WindowSecondScalar(output, reversed, direct, window, scale, count, n);
            return;
        }

        WindowSecondScalar(output, reversed, direct, window, scale, count, 0);
    }

    private static void WindowSecondScalar(
        Span<float> output,
        ReadOnlySpan<float> reversed,
        ReadOnlySpan<float> direct,
        ReadOnlySpan<float> window,
        float scale,
        int count,
        int from)
    {
        for (int n = from; n < count; n++)
        {
            output[n] = window[n] * -scale * (reversed[n] + direct[n]);
        }
    }

    private static Vector128<float> Load(ReadOnlySpan<float> values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);

    private static void Store(Span<float> values, int index, Vector128<float> value) =>
        value.StoreUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);
}
