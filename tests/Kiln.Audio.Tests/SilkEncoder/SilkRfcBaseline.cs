namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Frozen G2 baseline: aggregate segmental SNR of the RFC 6716 Appendix A reference encoder.
/// </summary>
/// <remarks>
/// <para>
/// How these numbers were produced (2026-11-26): the RFC 6716 Appendix A reference source, built
/// with FIXED_POINT=1 and its own <c>opus_demo</c>, encoded the exact G2 corpus (RFC 8251 vectors
/// 2, 3, 4 and 6, mixed to mono, 48 kHz) as <c>-e voip 48000 1 BITRATE -bandwidth BW -framesize MS
/// -complexity 0</c>. The packets were decoded with the libopus 1.5.2 oracle decoder (the decoder is
/// normative, so any conformant decoder gives the same PCM) and scored with the same
/// <see cref="SilkQualityHarness.Score"/> and the same independent <see cref="IdealResampler"/>
/// reference as our encoder. The figure is the segment-weighted mean segSNR in dB over the whole
/// corpus. Regenerate with <see cref="SilkRfcBaselineGenerator"/> (set KILN_SILK_RFC_OPUS_DEMO).
/// </para>
/// <para>
/// The G2 gate asserts ours &gt;= baseline - <see cref="ToleranceDb"/> dB. The 0.05 dB allowance
/// covers measurement noise (delay alignment, rounding); our encoder is deterministic, so a given
/// build always produces the same figure.
/// </para>
/// </remarks>
internal static class SilkRfcBaseline
{
    /// <summary>Allowed shortfall in dB, a measurement-noise allowance.</summary>
    internal const double ToleranceDb = 0.05;

    private static readonly Dictionary<(OpusBandwidth, int, int), double> Table = new()
    {
        [(OpusBandwidth.Narrowband, 20, 8000)] = 3.04,
        [(OpusBandwidth.Narrowband, 20, 12000)] = 4.98,
        [(OpusBandwidth.Narrowband, 60, 8000)] = 3.09,
        [(OpusBandwidth.Narrowband, 60, 12000)] = 5.03,
        [(OpusBandwidth.Mediumband, 20, 8000)] = 2.42,
        [(OpusBandwidth.Mediumband, 20, 12000)] = 4.66,
        [(OpusBandwidth.Mediumband, 20, 20000)] = 5.95,
        [(OpusBandwidth.Mediumband, 60, 8000)] = 2.52,
        [(OpusBandwidth.Mediumband, 60, 12000)] = 4.72,
        [(OpusBandwidth.Mediumband, 60, 20000)] = 5.98,
        [(OpusBandwidth.Wideband, 20, 8000)] = 1.71,
        [(OpusBandwidth.Wideband, 20, 12000)] = 3.58,
        [(OpusBandwidth.Wideband, 20, 20000)] = 5.78,
        [(OpusBandwidth.Wideband, 60, 8000)] = 1.72,
        [(OpusBandwidth.Wideband, 60, 12000)] = 3.48,
        [(OpusBandwidth.Wideband, 60, 20000)] = 5.75,
    };

    /// <summary>Looks up the frozen RFC figure for a configuration at complexity 0.</summary>
    internal static bool TryGet(OpusBandwidth bandwidth, int milliseconds, int bitrate, out double db) =>
        Table.TryGetValue((bandwidth, milliseconds, bitrate), out db);
}
