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

    /// <summary>
    /// Applies the forward pre-rotation <c>z = (re + i*im) * (-cos(theta) + i*sin(theta))</c>.
    /// </summary>
    /// <param name="real">The fold real parts, updated in place.</param>
    /// <param name="imaginary">The fold imaginary parts, updated in place.</param>
    /// <param name="rotCos">The combined cosine twiddles.</param>
    /// <param name="rotSin">The combined sine twiddles.</param>
    /// <param name="count">The number of complex values.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void RotateForwardPre(
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        bool useVector)
    {
        if (useVector && Vector128.IsHardwareAccelerated)
        {
            int i = 0;
            for (; i + 4 <= count; i += 4)
            {
                Vector128<float> re = Load(real, i);
                Vector128<float> im = Load(imaginary, i);
                Vector128<float> cos = Load(rotCos, i);
                Vector128<float> sin = Load(rotSin, i);
                Store(real, i, -(re * cos) - (im * sin));
                Store(imaginary, i, (re * sin) - (im * cos));
            }

            for (; i < count; i++)
            {
                float re = real[i];
                float im = imaginary[i];
                float cos = rotCos[i];
                float sin = rotSin[i];
                real[i] = (-re * cos) - (im * sin);
                imaginary[i] = (re * sin) - (im * cos);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            float re = real[i];
            float im = imaginary[i];
            float cos = rotCos[i];
            float sin = rotSin[i];
            real[i] = (-re * cos) - (im * sin);
            imaginary[i] = (re * sin) - (im * cos);
        }
    }

    /// <summary>
    /// Post-rotates the forward spectrum and writes coefficient <c>2p</c> from the real part and
    /// coefficient <c>count-1-2p</c> from the imaginary part, at the requested stride.
    /// </summary>
    /// <param name="coefficients">Receives the coefficients.</param>
    /// <param name="real">The FFT output real parts.</param>
    /// <param name="imaginary">The FFT output imaginary parts.</param>
    /// <param name="rotCos">The combined cosine twiddles.</param>
    /// <param name="rotSin">The combined sine twiddles.</param>
    /// <param name="count">The number of coefficients.</param>
    /// <param name="stride">The distance between consecutive coefficients.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void WriteForwardPost(
        Span<float> coefficients,
        ReadOnlySpan<float> real,
        ReadOnlySpan<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        int stride,
        bool useVector)
    {
        int half = count / 2;
        int last = count - 1;
        if (useVector && Vector128.IsHardwareAccelerated && stride == 1)
        {
            int i = 0;
            for (; i + 4 <= half; i += 4)
            {
                Vector128<float> re = Load(real, i);
                Vector128<float> im = Load(imaginary, i);
                Vector128<float> cos = Load(rotCos, i);
                Vector128<float> sin = Load(rotSin, i);
                Vector128<float> even = (re * cos) + (im * sin);
                Vector128<float> odd = (re * sin) - (im * cos);
                for (int lane = 0; lane < 4; lane++)
                {
                    coefficients[2 * (i + lane)] = even.GetElement(lane);
                    coefficients[last - (2 * (i + lane))] = odd.GetElement(lane);
                }
            }

            for (; i < half; i++)
            {
                float re = real[i];
                float im = imaginary[i];
                float cos = rotCos[i];
                float sin = rotSin[i];
                coefficients[2 * i] = (re * cos) + (im * sin);
                coefficients[last - (2 * i)] = (re * sin) - (im * cos);
            }

            return;
        }

        for (int i = 0; i < half; i++)
        {
            float re = real[i];
            float im = imaginary[i];
            float cos = rotCos[i];
            float sin = rotSin[i];
            coefficients[(2 * i) * stride] = (re * cos) + (im * sin);
            coefficients[(last - (2 * i)) * stride] = (re * sin) - (im * cos);
        }
    }

    /// <summary>Applies the inverse post-rotation <c>z = z * exp(+i*theta)</c> in place.</summary>
    /// <param name="real">The FFT output real parts, updated in place.</param>
    /// <param name="imaginary">The FFT output imaginary parts, updated in place.</param>
    /// <param name="rotCos">The combined cosine twiddles.</param>
    /// <param name="rotSin">The combined sine twiddles.</param>
    /// <param name="count">The number of complex values.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void RotateInversePost(
        Span<float> real,
        Span<float> imaginary,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        int count,
        bool useVector)
    {
        if (useVector && Vector128.IsHardwareAccelerated)
        {
            int i = 0;
            for (; i + 4 <= count; i += 4)
            {
                Vector128<float> re = Load(real, i);
                Vector128<float> im = Load(imaginary, i);
                Vector128<float> cos = Load(rotCos, i);
                Vector128<float> sin = Load(rotSin, i);
                Store(real, i, (re * cos) - (im * sin));
                Store(imaginary, i, (re * sin) + (im * cos));
            }

            for (; i < count; i++)
            {
                float re = real[i];
                float im = imaginary[i];
                float cos = rotCos[i];
                float sin = rotSin[i];
                real[i] = (re * cos) - (im * sin);
                imaginary[i] = (re * sin) + (im * cos);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            float re = real[i];
            float im = imaginary[i];
            float cos = rotCos[i];
            float sin = rotSin[i];
            real[i] = (re * cos) - (im * sin);
            imaginary[i] = (re * sin) + (im * cos);
        }
    }

    /// <summary>
    /// Mirrors the de-shuffled inverse spectrum into the <c>count + overlap</c> windowed output,
    /// applying the window and the <c>2/count</c> normalisation.
    /// </summary>
    /// <param name="output">Receives <c>count + overlap</c> samples.</param>
    /// <param name="deshuffle">The de-shuffled direct/reversed spectrum.</param>
    /// <param name="window">The rising overlap window.</param>
    /// <param name="scale">The <c>2/count</c> inverse factor.</param>
    /// <param name="count">The coefficient count.</param>
    /// <param name="overlap">The overlap length.</param>
    /// <param name="useVector">Whether the vector path may be used (currently the scalar path).</param>
    public static void MirrorInverse(
        Span<float> output,
        ReadOnlySpan<float> deshuffle,
        ReadOnlySpan<float> window,
        float scale,
        int count,
        int overlap,
        bool useVector)
    {
        _ = useVector;
        int half = count / 2;
        int halfOverlap = overlap / 2;
        int total = half - halfOverlap;

        // Lower mirror: the middle block is copied straight, the overlap/2 pairs apply the window.
        int source = half - 1;
        int destination = (count + overlap) / 2 - 1;
        int lower = 0;
        for (int i = 0; i < total; i++)
        {
            output[destination] = deshuffle[source] * scale;
            destination--;
            source--;
        }

        for (int i = total; i < half; i++)
        {
            float value = deshuffle[source];
            source--;
            output[lower] = -window[lower] * value * scale;
            lower++;
            output[destination] = window[overlap - lower] * value * scale;
            destination--;
        }

        // Upper mirror.
        source = half;
        destination = (count + overlap) / 2;
        int tail = count - 1 + overlap;
        lower = 0;
        for (int i = 0; i < total; i++)
        {
            output[destination] = deshuffle[source] * scale;
            destination++;
            source++;
        }

        for (int i = total; i < half; i++)
        {
            float value = deshuffle[source];
            source++;
            output[tail] = window[lower] * value * scale;
            tail--;
            output[destination] = window[overlap - 1 - lower] * value * scale;
            destination++;
            lower++;
        }
    }

    private static Vector128<float> Load(ReadOnlySpan<float> values, int index) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);

    private static void Store(Span<float> values, int index, Vector128<float> value) =>
        value.StoreUnsafe(ref MemoryMarshal.GetReference(values), (nuint)index);
}
