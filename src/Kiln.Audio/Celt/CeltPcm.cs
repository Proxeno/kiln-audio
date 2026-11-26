// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

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
}
