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
    /// Combines the two forward DCT-IV results: <c>coefficients[k*stride] = cos(beta_k)*first[k] - sin(beta_k)*secondReversed[k]</c>.
    /// </summary>
    /// <param name="coefficients">Receives the coefficients.</param>
    /// <param name="first">The DCT-IV of the difference sequence.</param>
    /// <param name="secondReversed">The DCT-IV of the sign-flipped sum, in reverse order.</param>
    /// <param name="betaCos">The cosine table.</param>
    /// <param name="betaSin">The sine table.</param>
    /// <param name="count">The number of coefficients.</param>
    /// <param name="stride">The coefficient stride.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void CombineForward(
        Span<float> coefficients,
        ReadOnlySpan<float> first,
        ReadOnlySpan<float> secondReversed,
        ReadOnlySpan<float> betaCos,
        ReadOnlySpan<float> betaSin,
        int count,
        int stride,
        bool useVector)
    {
        if (useVector && stride == 1)
        {
            int k = 0;
            for (; k + 4 <= count; k += 4)
            {
                Vector128<float> value =
                    (Load(betaCos, k) * Load(first, k))
                    - (Load(betaSin, k) * Load(secondReversed, k));
                Store(coefficients, k, value);
            }

            for (; k < count; k++)
            {
                coefficients[k] = (betaCos[k] * first[k]) - (betaSin[k] * secondReversed[k]);
            }

            return;
        }

        for (int k = 0; k < count; k++)
        {
            coefficients[k * stride] = (betaCos[k] * first[k]) - (betaSin[k] * secondReversed[k]);
        }
    }

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
