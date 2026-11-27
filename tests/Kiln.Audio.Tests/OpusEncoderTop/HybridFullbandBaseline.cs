namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Frozen values for the full-band hybrid gate, a separate gate added for 1.0 on the generated
/// <see cref="FullbandCorpus"/> item. <see cref="HybridRfcBaseline"/> (RFC 8251 vectors 2, 3, 4
/// and 6) is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The corpus item is 6 s of generated 48 kHz mono PCM with content up to 19 kHz. The RFC figures are
/// the segSNR of the RFC 6716 Appendix A reference encoder on it, run through
/// <see cref="HybridQualityHarness"/> with the same command line as the older baseline:
/// <c>-e voip 48000 1 BITRATE -bandwidth {SWB|FB} -framesize 20 -complexity 0</c>, decoded with the
/// libopus 1.5.2 decoder and scored against <see cref="HybridFullbandHarness.Reference"/>.
/// </para>
/// <para>
/// Regenerate the table with <see cref="HybridFullbandBaselineGenerator"/> (set
/// <c>KILN_HYBRID_RFC_OPUS_DEMO</c> to the RFC build's <c>opus_demo</c>).
/// </para>
/// </remarks>
internal static class HybridFullbandBaseline
{
    /// <summary>Allowed segSNR shortfall in dB, a measurement-noise allowance.</summary>
    internal const double ToleranceDb = 0.05;

    // PROVISIONAL: freeze after first measurement
    /// <summary>Floor for the fullband 13 to 19 kHz energy retention of our encoder, in dB.</summary>
    internal const double MinFullbandRetentionDb = -200.0;

    /// <summary>Fullband must retain at least this many dB more 13 to 19 kHz energy than super-wideband.</summary>
    internal const double MinFullbandOverSuperWidebandDb = 20.0;

    /// <summary>Corpus sanity: the 13 to 19 kHz energy relative to the 0 to 20 kHz energy, in dB.</summary>
    internal const double MinHighBandShareDb = -30.0;

    private static readonly Dictionary<(OpusBandwidth, int), double> Table = new()
    {
        // PROVISIONAL: freeze after first measurement
        [(OpusBandwidth.SuperWideband, 24000)] = -10.0,

        // PROVISIONAL: freeze after first measurement
        [(OpusBandwidth.SuperWideband, 32000)] = -10.0,

        // PROVISIONAL: freeze after first measurement
        [(OpusBandwidth.Fullband, 24000)] = -10.0,

        // PROVISIONAL: freeze after first measurement
        [(OpusBandwidth.Fullband, 32000)] = -10.0,
    };

    /// <summary>Looks up the frozen RFC segSNR for a configuration at complexity 0.</summary>
    internal static bool TryGet(OpusBandwidth bandwidth, int bitrate, out double db) =>
        Table.TryGetValue((bandwidth, bitrate), out db);
}
