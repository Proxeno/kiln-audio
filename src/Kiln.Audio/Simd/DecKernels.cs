using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd;

/// <summary>
/// Elementwise kernels shared by the CELT frequency-domain decoder, each with a scalar reference
/// path and a portable <see cref="Vector128{T}"/> path.
/// </summary>
/// <remarks>
/// <para>
/// Every kernel here is elementwise: each output element is computed from the same inputs with
/// the same operation as the scalar code, so the vector and scalar paths produce bit-identical
/// results and the parity tests compare with exact array equality rather than an RMS tolerance.
/// </para>
/// <para>
/// <c>useVector</c> forces a path for the parity tests; the default overloads honour
/// <see cref="SimdConfig.UseSimd"/>.
/// </para>
/// </remarks>
internal static class DecKernels
{
    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> with a gain.</summary>
    /// <param name="destination">Destination, at least as long as the source.</param>
    /// <param name="source">The input samples.</param>
    /// <param name="gain">The per-sample multiplier.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void ScaleCopy(
        Span<float> destination, ReadOnlySpan<float> source, float gain, bool useVector)
    {
        int count = source.Length;
        if (count > destination.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        if (useVector && count >= Vector128<float>.Count)
        {
            var gainVector = Vector128.Create(gain);
            ref float s = ref MemoryMarshal.GetReference(source);
            ref float d = ref MemoryMarshal.GetReference(destination);
            int i = 0;
            for (; i + Vector128<float>.Count <= count; i += Vector128<float>.Count)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref s, (nuint)i) * gainVector;
                value.StoreUnsafe(ref d, (nuint)i);
            }

            for (; i < count; i++)
            {
                destination[i] = source[i] * gain;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = source[i] * gain;
        }
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> with a gain.</summary>
    /// <param name="destination">Destination, at least as long as the source.</param>
    /// <param name="source">The input samples.</param>
    /// <param name="gain">The per-sample multiplier.</param>
    public static void ScaleCopy(Span<float> destination, ReadOnlySpan<float> source, float gain) =>
        ScaleCopy(destination, source, gain, SimdConfig.UseSimd);

    /// <summary>Scales a span in place.</summary>
    /// <param name="values">The samples to scale.</param>
    /// <param name="gain">The multiplier.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void ScaleInPlace(Span<float> values, float gain, bool useVector)
    {
        if (useVector && values.Length >= Vector128<float>.Count)
        {
            var gainVector = Vector128.Create(gain);
            ref float r = ref MemoryMarshal.GetReference(values);
            int i = 0;
            for (; i + Vector128<float>.Count <= values.Length; i += Vector128<float>.Count)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref r, (nuint)i) * gainVector;
                value.StoreUnsafe(ref r, (nuint)i);
            }

            for (; i < values.Length; i++)
            {
                values[i] *= gain;
            }

            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] *= gain;
        }
    }

    /// <summary>Scales a span in place.</summary>
    /// <param name="values">The samples to scale.</param>
    /// <param name="gain">The multiplier.</param>
    public static void ScaleInPlace(Span<float> values, float gain) =>
        ScaleInPlace(values, gain, SimdConfig.UseSimd);

    /// <summary>Converts a pulse vector to float and scales it, as the PVQ decoder does.</summary>
    /// <param name="destination">Destination, at least as long as the source.</param>
    /// <param name="source">The integer pulse vector.</param>
    /// <param name="gain">The per-sample multiplier.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void ScaleIntToFloat(
        Span<float> destination, ReadOnlySpan<int> source, float gain, bool useVector)
    {
        int count = source.Length;
        if (count > destination.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        if (useVector && count >= Vector128<int>.Count)
        {
            var gainVector = Vector128.Create(gain);
            ref int s = ref MemoryMarshal.GetReference(source);
            ref float d = ref MemoryMarshal.GetReference(destination);
            int i = 0;
            for (; i + Vector128<int>.Count <= count; i += Vector128<int>.Count)
            {
                Vector128<int> pulses = Vector128.LoadUnsafe(ref s, (nuint)i);
                Vector128<float> value = Vector128.ConvertToSingle(pulses) * gainVector;
                value.StoreUnsafe(ref d, (nuint)i);
            }

            for (; i < count; i++)
            {
                destination[i] = source[i] * gain;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = source[i] * gain;
        }
    }

    /// <summary>Converts a pulse vector to float and scales it, as the PVQ decoder does.</summary>
    /// <param name="destination">Destination, at least as long as the source.</param>
    /// <param name="source">The integer pulse vector.</param>
    /// <param name="gain">The per-sample multiplier.</param>
    public static void ScaleIntToFloat(Span<float> destination, ReadOnlySpan<int> source, float gain) =>
        ScaleIntToFloat(destination, source, gain, SimdConfig.UseSimd);

    /// <summary>
    /// Reconstructs the left and right channel shapes from a mid/side band
    /// (RFC 6716 §4.3.4.2, <c>stereo_merge</c>): <c>x = lgain*(mid*x - y)</c>,
    /// <c>y = rgain*(mid*x + y)</c>.
    /// </summary>
    /// <param name="x">The mid channel; replaced by the left channel.</param>
    /// <param name="y">The side channel; replaced by the right channel.</param>
    /// <param name="mid">The mid gain.</param>
    /// <param name="lgain">The left-channel normalization gain.</param>
    /// <param name="rgain">The right-channel normalization gain.</param>
    /// <param name="count">The number of samples.</param>
    /// <param name="useVector">Whether the vector path may be used.</param>
    public static void StereoMerge(
        Span<float> x,
        Span<float> y,
        float mid,
        float lgain,
        float rgain,
        int count,
        bool useVector)
    {
        if (useVector && count >= Vector128<float>.Count)
        {
            Vector128<float> midVector = Vector128.Create(mid);
            Vector128<float> leftVector = Vector128.Create(lgain);
            Vector128<float> rightVector = Vector128.Create(rgain);
            ref float xr = ref MemoryMarshal.GetReference(x);
            ref float yr = ref MemoryMarshal.GetReference(y);
            int j = 0;
            for (; j + Vector128<float>.Count <= count; j += Vector128<float>.Count)
            {
                Vector128<float> xv = Vector128.LoadUnsafe(ref xr, (nuint)j);
                Vector128<float> yv = Vector128.LoadUnsafe(ref yr, (nuint)j);
                Vector128<float> l = midVector * xv;
                (leftVector * (l - yv)).StoreUnsafe(ref xr, (nuint)j);
                (rightVector * (l + yv)).StoreUnsafe(ref yr, (nuint)j);
            }

            for (; j < count; j++)
            {
                float l = mid * x[j];
                float r = y[j];
                x[j] = lgain * (l - r);
                y[j] = rgain * (l + r);
            }

            return;
        }

        for (int j = 0; j < count; j++)
        {
            float l = mid * x[j];
            float r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    /// <summary>Reconstructs the left and right channel shapes from a mid/side band.</summary>
    /// <param name="x">The mid channel; replaced by the left channel.</param>
    /// <param name="y">The side channel; replaced by the right channel.</param>
    /// <param name="mid">The mid gain.</param>
    /// <param name="lgain">The left-channel normalization gain.</param>
    /// <param name="rgain">The right-channel normalization gain.</param>
    /// <param name="count">The number of samples.</param>
    public static void StereoMerge(
        Span<float> x, Span<float> y, float mid, float lgain, float rgain, int count) =>
        StereoMerge(x, y, mid, lgain, rgain, count, SimdConfig.UseSimd);
}
