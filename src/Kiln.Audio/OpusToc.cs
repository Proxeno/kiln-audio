namespace Kiln.Audio;

/// <summary>Coding mode selected by a TOC configuration (RFC 6716 §3.1, Table 2).</summary>
internal enum OpusMode
{
    /// <summary>SILK-only.</summary>
    Silk,

    /// <summary>Hybrid SILK + CELT.</summary>
    Hybrid,

    /// <summary>CELT-only.</summary>
    Celt,
}

/// <summary>Audio bandwidth (RFC 6716 §2, Table 1).</summary>
public enum OpusBandwidth
{
    /// <summary>Narrowband, 4 kHz audio bandwidth (8 kHz effective sample rate).</summary>
    Narrowband,

    /// <summary>Mediumband, 6 kHz (12 kHz).</summary>
    Mediumband,

    /// <summary>Wideband, 8 kHz (16 kHz).</summary>
    Wideband,

    /// <summary>Super-wideband, 12 kHz (24 kHz).</summary>
    SuperWideband,

    /// <summary>Fullband, 20 kHz (48 kHz).</summary>
    Fullband,
}

/// <summary>A parsed RFC 6716 §3.1 table-of-contents byte.</summary>
/// <param name="Config">Configuration number, 0 to 31.</param>
/// <param name="Mode">Coding mode.</param>
/// <param name="Bandwidth">Audio bandwidth.</param>
/// <param name="FrameDuration">Frame duration.</param>
/// <param name="IsStereo">The "s" bit: true for stereo.</param>
/// <param name="FrameCountCode">The "c" code, 0 to 3 (§3.2).</param>
internal readonly record struct OpusToc(
    int Config,
    OpusMode Mode,
    OpusBandwidth Bandwidth,
    OpusFrameDuration FrameDuration,
    bool IsStereo,
    int FrameCountCode)
{
    /// <summary>
    /// Parses a TOC byte: bits 7..3 are the config, bit 2 is the stereo flag, bits 1..0 the
    /// frame-count code (§3.1). Every byte value is valid.
    /// </summary>
    internal static OpusToc Parse(byte toc)
    {
        int config = toc >> 3;
        bool stereo = (toc & 0x04) != 0;
        int code = toc & 0x03;

        OpusMode mode;
        OpusBandwidth bandwidth;
        OpusFrameDuration duration;

        if (config < 12)
        {
            // Table 2: configs 0-11, SILK-only; 4 durations (10/20/40/60 ms) per bandwidth NB, MB, WB.
            mode = OpusMode.Silk;
            bandwidth = (OpusBandwidth)(config / 4);
            duration = (OpusFrameDuration)(OpusFrameDuration.Ms10 + (config % 4));
        }
        else if (config < 16)
        {
            // Table 2: configs 12-15, Hybrid; SWB (12-13) and FB (14-15), 10/20 ms.
            mode = OpusMode.Hybrid;
            bandwidth = config < 14 ? OpusBandwidth.SuperWideband : OpusBandwidth.Fullband;
            duration = (config & 1) == 0 ? OpusFrameDuration.Ms10 : OpusFrameDuration.Ms20;
        }
        else
        {
            // Table 2: configs 16-31, CELT-only; NB, WB, SWB, FB (no mediumband), 2.5/5/10/20 ms.
            mode = OpusMode.Celt;
            int group = (config - 16) / 4;
            bandwidth = group switch
            {
                0 => OpusBandwidth.Narrowband,
                1 => OpusBandwidth.Wideband,
                2 => OpusBandwidth.SuperWideband,
                _ => OpusBandwidth.Fullband,
            };
            duration = (OpusFrameDuration)(config % 4);
        }

        return new OpusToc(config, mode, bandwidth, duration, stereo, code);
    }
}
