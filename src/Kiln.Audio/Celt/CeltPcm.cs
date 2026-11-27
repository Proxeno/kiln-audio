// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Celt;

/// <summary>
/// Converts the synthesizer's normalized floating-point PCM to signed 16-bit PCM.
/// </summary>
/// <remarks>
/// The rounding and saturation match the reference decoder's float-to-int path
/// (RFC 6716 Appendix A, celt/float_cast.h: <c>FLOAT2INT16</c>): the normalized sample
/// is scaled by 32768, clamped to [-32768, 32767], then rounded half up and truncated.
/// </remarks>
internal static class CeltPcm
{
    /// <summary>Converts one normalized sample in [-1, 1] to signed 16-bit PCM.</summary>
    /// <param name="sample">A sample in the reference's normalized float domain.</param>
    /// <returns>The saturated, rounded 16-bit value.</returns>
    internal static short ToInt16(float sample)
    {
        float scaled = sample * CeltTables.SignalScale;
        scaled = Math.Max(-32768f, Math.Min(32767f, scaled));
        return (short)(int)Math.Floor(0.5f + scaled);
    }

    /// <summary>Converts a run of normalized samples to signed 16-bit PCM, bit-identical to the scalar overload.</summary>
    /// <param name="source">Normalized samples.</param>
    /// <param name="destination">Receives <c>source.Length</c> samples.</param>
    internal static void ToInt16(ReadOnlySpan<float> source, Span<short> destination)
    {
        int count = source.Length;
        if (destination.Length < count)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        int i = 0;
        if (Vector128.IsHardwareAccelerated && SimdConfig.UseSimd)
        {
            ref float s = ref MemoryMarshal.GetReference(source);
            ref short d = ref MemoryMarshal.GetReference(destination);
            Vector128<float> scale = Vector128.Create(CeltTables.SignalScale);
            Vector128<float> lo = Vector128.Create(-32768f);
            Vector128<float> hi = Vector128.Create(32767f);
            Vector128<float> half = Vector128.Create(0.5f);
            for (; i + 8 <= count; i += 8)
            {
                Vector128<int> a = Convert4(Vector128.LoadUnsafe(ref s, (nuint)i), scale, lo, hi, half);
                Vector128<int> b = Convert4(Vector128.LoadUnsafe(ref s, (nuint)(i + 4)), scale, lo, hi, half);
                Vector128.Narrow(a, b).StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            destination[i] = ToInt16(source[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Convert4(
        Vector128<float> v, Vector128<float> scale, Vector128<float> lo, Vector128<float> hi, Vector128<float> half)
    {
        Vector128<float> scaled = Vector128.Min(Vector128.Max(v * scale, lo), hi);
        return Vector128.ConvertToInt32(Vector128.Floor(scaled + half));
    }
}
