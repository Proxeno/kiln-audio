namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Values for the full-band gate, a separate gate added for 1.0 on the generated
/// <see cref="FullbandCorpus"/> item. <see cref="HybridRfcBaseline"/> (RFC 8251 vectors 2, 3, 4
/// and 6) is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The corpus item is 6 s of generated 48 kHz mono PCM with content up to 19 kHz. The RFC figures are
/// the segSNR of the RFC 6716 Appendix A reference encoder on it, run through
/// <see cref="HybridQualityHarness"/> with the same command line as the older baseline:
/// <c>-e voip 48000 1 BITRATE -bandwidth {SWB|FB} -framesize 20 -complexity 0</c>, decoded with the
/// libopus 1.5.2 decoder and scored against <see cref="HybridFullbandHarness.Reference"/>. They were
/// measured on 2026-11-27. Regenerate with <see cref="HybridFullbandBaselineGenerator"/> (set
/// <c>KILN_HYBRID_RFC_OPUS_DEMO</c> to the RFC build's <c>opus_demo</c>).
/// </para>
/// <para>
/// What the first measurement showed, and why the gate has the shape it has. In the automatic
/// path <see cref="OpusEncoderOptions.Bandwidth"/> is an upper bound, not a forced value: the
/// mode selector applies the reference's bitrate thresholds, and at complexity 0 and constant
/// bitrate it chooses wideband at 24 kbit/s and super-wideband at 32 kbit/s for this item. So at
/// those bitrates a fullband cap and a super-wideband cap produce the same packets, and neither
/// carries 13 to 19 kHz (retention about -77 dB), while the reference encoders are forced to the
/// named bandwidth by <c>-bandwidth</c>. That is also why the super-wideband and fullband rows of
/// the older gate score identically for our encoder. Fullband is selected from about 35 kbit/s.
/// </para>
/// <para>
/// The gate therefore has two parts. The band-retention test runs at 48 and 64 kbit/s, where the
/// selector does choose fullband, and checks that the band above 12 kHz is kept under a fullband
/// cap and removed under a super-wideband cap. The segSNR test gates the two fullband rows at 24
/// and 32 kbit/s against the RFC encoder; the two super-wideband rows are measured and reported
/// only, because there the comparison is between different bandwidths (ours 1.47 dB against the
/// RFC encoder's 1.52 dB at 24 kbit/s, 3.51 dB against 1.50 dB at 32 kbit/s).
/// </para>
/// </remarks>
internal static class HybridFullbandBaseline
{
    /// <summary>Allowed segSNR shortfall in dB, a measurement-noise allowance.</summary>
    internal const double ToleranceDb = 0.05;

    /// <summary>
    /// Floor for the 13 to 19 kHz energy retention of our encoder under a fullband cap, in dB. This
    /// is a design bound, not a frozen measurement: a fullband stream must keep the band within 6 dB.
    /// </summary>
    internal const double MinFullbandRetentionDb = -6.0;

    /// <summary>Fullband must retain at least this many dB more 13 to 19 kHz energy than super-wideband.</summary>
    internal const double MinFullbandOverSuperWidebandDb = 20.0;

    /// <summary>Corpus sanity: the 13 to 19 kHz energy relative to the 0 to 20 kHz energy, in dB.</summary>
    internal const double MinHighBandShareDb = -30.0;

    private static readonly Dictionary<(OpusBandwidth, int), double> Table = new()
    {
        [(OpusBandwidth.SuperWideband, 24000)] = 1.52,
        [(OpusBandwidth.SuperWideband, 32000)] = 1.50,
        [(OpusBandwidth.Fullband, 24000)] = -0.12,
        [(OpusBandwidth.Fullband, 32000)] = 1.07,
    };

    /// <summary>
    /// Whether a configuration's RFC figure is a hard gate. Only the fullband rows are; see the
    /// class remarks.
    /// </summary>
    internal static bool IsGated(OpusBandwidth bandwidth) => bandwidth == OpusBandwidth.Fullband;

    /// <summary>Looks up the RFC encoder's segSNR for a configuration at complexity 0.</summary>
    internal static bool TryGet(OpusBandwidth bandwidth, int bitrate, out double db) =>
        Table.TryGetValue((bandwidth, bitrate), out db);
}
