// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Audio.Simd;

/// <summary>
/// Element-wise SIMD kernels for the CELT encoder front end.
/// </summary>
/// <remarks>
/// Every kernel here is a pure element-wise operation (a multiply, or a two-tap filter), so the
/// vector path performs the same single-rounded operations in the same order as the scalar
/// reference and is bit-identical to it.
/// </remarks>
internal static class EncKernels
{
    /// <summary>Pre-emphasis coefficient at 48 kHz (RFC 6716 Appendix A, celt/modes.c: preemph[0]).</summary>
    internal const float PreEmphasisCoefficient = 0.8500061035f;

    /// <summary>Multiplies every element by <paramref name="scale"/> in place.</summary>
    internal static void Scale(Span<float> values, float scale, bool useVector = true)
    {
        if (!useVector || !SimdConfig.UseSimd || !Vector128.IsHardwareAccelerated || values.Length < 8)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] *= scale;
            }

            return;
        }

        ref float reference = ref MemoryMarshal.GetReference(values);
        Vector128<float> scaleVector = Vector128.Create(scale);
        int j = 0;
        for (; j + 4 <= values.Length; j += 4)
        {
            Vector128<float> value = Vector128.LoadUnsafe(ref reference, (nuint)j);
            Vector128.StoreUnsafe(value * scaleVector, ref reference, (nuint)j);
        }

        for (; j < values.Length; j++)
        {
            values[j] *= scale;
        }
    }

    /// <summary>Writes <paramref name="source"/> * <paramref name="gain"/> into <paramref name="destination"/>.</summary>
    internal static void NormaliseInto(ReadOnlySpan<float> source, Span<float> destination, float gain, bool useVector = true)
    {
        if (!useVector || !SimdConfig.UseSimd || !Vector128.IsHardwareAccelerated || source.Length < 8)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[i] * gain;
            }

            return;
        }

        ref float src = ref MemoryMarshal.GetReference(source);
        ref float dst = ref MemoryMarshal.GetReference(destination);
        Vector128<float> gainVector = Vector128.Create(gain);
        int j = 0;
        for (; j + 4 <= source.Length; j += 4)
        {
            Vector128<float> value = Vector128.LoadUnsafe(ref src, (nuint)j);
            Vector128.StoreUnsafe(value * gainVector, ref dst, (nuint)j);
        }

        for (; j < source.Length; j++)
        {
            destination[j] = source[j] * gain;
        }
    }

    /// <summary>Vector pre-emphasis; see <c>CeltEncoderKernels.PreEmphasis</c> for the contract.</summary>
    internal static void PreEmphasis(ReadOnlySpan<short> pcm, Span<float> destination, ref float memory, out bool silent)
    {
        ref short src = ref MemoryMarshal.GetReference(pcm);
        ref float dst = ref MemoryMarshal.GetReference(destination);

        float state = memory;
        float first = Unsafe.Add(ref src, 0);
        float y = first + state;
        Unsafe.Add(ref dst, 0) = y;
        bool allZero = y == 0f;

        Vector128<float> negCoefficient = Vector128.Create(-PreEmphasisCoefficient);
        Vector128<float> zero = Vector128<float>.Zero;
        int i = 1;
        for (; i + 8 <= pcm.Length; i += 8)
        {
            Vector128<short> current = Vector128.LoadUnsafe(in src, (nuint)i);
            Vector128<short> previous = Vector128.LoadUnsafe(in src, (nuint)(i - 1));
            (Vector128<int> currentLow, Vector128<int> currentHigh) = Vector128.Widen(current);
            (Vector128<int> previousLow, Vector128<int> previousHigh) = Vector128.Widen(previous);

            Vector128<float> low =
                (negCoefficient * Vector128.ConvertToSingle(previousLow)) + Vector128.ConvertToSingle(currentLow);
            Vector128<float> high =
                (negCoefficient * Vector128.ConvertToSingle(previousHigh)) + Vector128.ConvertToSingle(currentHigh);

            Vector128.StoreUnsafe(low, ref dst, (nuint)i);
            Vector128.StoreUnsafe(high, ref dst, (nuint)(i + 4));

            if (!Vector128.EqualsAll(low, zero) || !Vector128.EqualsAll(high, zero))
            {
                allZero = false;
            }
        }

        for (; i < pcm.Length; i++)
        {
            float current = Unsafe.Add(ref src, i);
            float previous = Unsafe.Add(ref src, i - 1);
            y = current + (-PreEmphasisCoefficient * previous);
            Unsafe.Add(ref dst, i) = y;
            allZero &= y == 0f;
        }

        memory = -PreEmphasisCoefficient * Unsafe.Add(ref src, pcm.Length - 1);
        silent = allZero;
    }
}